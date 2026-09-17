<#
.SYNOPSIS
    Drives concurrent requests against the IIS load test site and summarises
    throughput, latency and which servers answered.

.DESCRIPTION
    A dependency free driver for the site in the "site" folder. It reports
    requests per second, latency percentiles, the status code mix and the
    distinct values of the X-LoadTest-Machine response header, which is what
    tells you whether a load balancer is actually spreading traffic.

.PARAMETER Url
    Full URL including the load parameters, for example
    http://server/loadtest/Load.ashx?cpuMs=200&memMB=64

.PARAMETER Concurrency
    Number of parallel workers. Each keeps its connection alive and loops.

.PARAMETER Seconds
    How long to keep sending. With -Requests it acts as a safety timeout.

.PARAMETER Requests
    Total number of requests to send instead of running for a duration.

.PARAMETER TimeoutSeconds
    Per request timeout. Raise it above the load the URL asks for.

.EXAMPLE
    .\Invoke-LoadTest.ps1 -Url 'http://web01/loadtest/Load.ashx?cpuMs=250' -Concurrency 20 -Seconds 60

.EXAMPLE
    .\Invoke-LoadTest.ps1 -Url 'http://vip/loadtest/Ping.ashx' -Concurrency 8 -Requests 2000
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Url,

    [ValidateRange(1, 1024)]
    [int] $Concurrency = 10,

    [ValidateRange(1, 86400)]
    [int] $Seconds = 30,

    [ValidateRange(0, 10000000)]
    [int] $Requests = 0,

    [ValidateRange(1, 3600)]
    [int] $TimeoutSeconds = 300
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

# Without this the framework caps outbound connections per host at 2 and the
# measured concurrency would be a fiction.
[System.Net.ServicePointManager]::DefaultConnectionLimit = [Math]::Max(64, $Concurrency * 2)
[System.Net.ServicePointManager]::Expect100Continue = $false

if ($Requests -gt 0 -and -not $PSBoundParameters.ContainsKey('Seconds')) {
    $Seconds = 86400
}

$perWorker = 0
if ($Requests -gt 0) {
    $perWorker = [Math]::Ceiling($Requests / $Concurrency)
}

$worker = {
    param($Url, $Deadline, $MaxRequests, $TimeoutMs)

    $results = New-Object System.Collections.ArrayList
    $sent = 0

    while ((Get-Date) -lt $Deadline) {
        if ($MaxRequests -gt 0 -and $sent -ge $MaxRequests) { break }

        $status = 0
        $machine = ''
        $failure = $null
        $watch = [System.Diagnostics.Stopwatch]::StartNew()

        try {
            $request = [System.Net.HttpWebRequest]::Create($Url)
            $request.Method = 'GET'
            $request.Timeout = $TimeoutMs
            $request.ReadWriteTimeout = $TimeoutMs
            $request.KeepAlive = $true
            $request.AllowAutoRedirect = $false

            $response = $request.GetResponse()
            try {
                $status = [int]$response.StatusCode
                $machine = $response.Headers['X-LoadTest-Machine']
                $reader = New-Object System.IO.StreamReader($response.GetResponseStream())
                try { $null = $reader.ReadToEnd() } finally { $reader.Dispose() }
            }
            finally {
                $response.Close()
            }
        }
        catch [System.Net.WebException] {
            $failure = $_.Exception.Message
            $errorResponse = $_.Exception.Response
            if ($null -ne $errorResponse) {
                try {
                    $status = [int]([System.Net.HttpWebResponse]$errorResponse).StatusCode
                    $machine = $errorResponse.Headers['X-LoadTest-Machine']
                }
                finally {
                    $errorResponse.Close()
                }
            }
        }
        catch {
            $failure = $_.Exception.Message
        }

        $watch.Stop()
        $sent++

        [void]$results.Add([PSCustomObject]@{
            Status  = $status
            Ms      = $watch.Elapsed.TotalMilliseconds
            Machine = $machine
            Error   = $failure
        })
    }

    return ,$results.ToArray()
}

Write-Host ''
Write-Host "URL         : $Url"
Write-Host "Concurrency : $Concurrency"
if ($Requests -gt 0) {
    Write-Host "Requests    : $Requests (about $perWorker per worker)"
}
else {
    Write-Host "Duration    : $Seconds s"
}
Write-Host ''

$deadline = (Get-Date).AddSeconds($Seconds)
$pool = [RunspaceFactory]::CreateRunspacePool(1, $Concurrency)
$pool.Open()

$running = @()
$wall = [System.Diagnostics.Stopwatch]::StartNew()

try {
    for ($i = 0; $i -lt $Concurrency; $i++) {
        $shell = [PowerShell]::Create()
        $shell.RunspacePool = $pool
        $null = $shell.AddScript($worker.ToString())
        $null = $shell.AddArgument($Url)
        $null = $shell.AddArgument($deadline)
        $null = $shell.AddArgument($perWorker)
        $null = $shell.AddArgument($TimeoutSeconds * 1000)

        $running += [PSCustomObject]@{ Shell = $shell; Handle = $shell.BeginInvoke() }
    }

    $samples = New-Object System.Collections.ArrayList
    foreach ($item in $running) {
        foreach ($sample in $item.Shell.EndInvoke($item.Handle)) {
            [void]$samples.Add($sample)
        }

        $item.Shell.Dispose()
    }
}
finally {
    $wall.Stop()
    $pool.Close()
    $pool.Dispose()
}

if ($samples.Count -eq 0) {
    Write-Warning 'No requests were sent.'
    return
}

$latencies = @($samples | ForEach-Object { $_.Ms } | Sort-Object)

function Get-Percentile {
    param([double[]] $Sorted, [double] $Percentile)

    if ($Sorted.Length -eq 0) { return 0 }
    $index = [int][Math]::Ceiling(($Percentile / 100.0) * $Sorted.Length) - 1
    if ($index -lt 0) { $index = 0 }
    if ($index -ge $Sorted.Length) { $index = $Sorted.Length - 1 }
    return $Sorted[$index]
}

$failed = @($samples | Where-Object { $_.Status -lt 200 -or $_.Status -ge 300 })
$seconds = [Math]::Max($wall.Elapsed.TotalSeconds, 0.001)

Write-Host 'Summary'
Write-Host '-------'
Write-Host ("requests        : {0}" -f $samples.Count)
Write-Host ("failed          : {0}" -f $failed.Count)
Write-Host ("wall clock      : {0:N2} s" -f $wall.Elapsed.TotalSeconds)
Write-Host ("requests/second : {0:N1}" -f ($samples.Count / $seconds))
Write-Host ''
Write-Host 'Latency (ms)'
Write-Host '------------'
Write-Host ("min  : {0:N1}" -f ($latencies | Measure-Object -Minimum).Minimum)
Write-Host ("avg  : {0:N1}" -f ($latencies | Measure-Object -Average).Average)
Write-Host ("p50  : {0:N1}" -f (Get-Percentile -Sorted $latencies -Percentile 50))
Write-Host ("p95  : {0:N1}" -f (Get-Percentile -Sorted $latencies -Percentile 95))
Write-Host ("p99  : {0:N1}" -f (Get-Percentile -Sorted $latencies -Percentile 99))
Write-Host ("max  : {0:N1}" -f ($latencies | Measure-Object -Maximum).Maximum)
Write-Host ''
Write-Host 'Status codes'
Write-Host '------------'
$samples | Group-Object Status | Sort-Object Name | ForEach-Object {
    Write-Host ("{0,-6} : {1}" -f $_.Name, $_.Count)
}

Write-Host ''
Write-Host 'Answered by'
Write-Host '-----------'
$samples | Group-Object Machine | Sort-Object Name | ForEach-Object {
    $name = if ([string]::IsNullOrEmpty($_.Name)) { '(no header)' } else { $_.Name }
    Write-Host ("{0,-20} : {1}" -f $name, $_.Count)
}

$errors = @($samples | Where-Object { $null -ne $_.Error } | Group-Object Error | Sort-Object Count -Descending)
if ($errors.Count -gt 0) {
    Write-Host ''
    Write-Host 'Errors'
    Write-Host '------'
    $errors | Select-Object -First 5 | ForEach-Object {
        Write-Host ("{0,6} x {1}" -f $_.Count, $_.Name)
    }
}

Write-Host ''
