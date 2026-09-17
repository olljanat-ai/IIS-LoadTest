# IIS-LoadTest

A minimal ASP.NET (.NET Framework) page you drop into IIS to put a known,
adjustable load on a server: simulated CPU processing, memory reservation and
release, and temp file write + read back. The load is described entirely with
URL parameters, so you can drive it from a browser, `curl`, a load balancer
probe or any load generator.

It is meant for verifying infrastructure - CPU sizing, memory limits and
application pool recycling, disk and SAN throughput, load balancer spread,
request timeouts - not for testing an application.

**No build step.** There is no project file, no `bin` folder and nothing to
compile: the code lives in `App_Code` and is compiled by ASP.NET at runtime.
Copy the `site` folder to the server and point IIS at it.

## Layout

```
site/
  Default.aspx              Browser UI: a form for every parameter plus the results
  Load.ashx                 Same parameters, JSON response, for scripts
  Ping.ashx                 No load at all: health probe and baseline
  Web.config                Safety limits, temp path, IIS settings
  App_Code/
    LoadEngine.cs           The CPU, memory, file, delay and GC phases
    LoadOptions.cs          URL parameter parsing, clamping and warnings
    LoadConfig.cs           Limits read from Web.config appSettings
    LoadStats.cs            Request counters and the persistent memory pool
    ServerInfo.cs           Worker process snapshot included in every response
    HtmlRenderer.cs         Renders the result object as HTML
    Json.cs                 Small JSON writer (no external dependencies)
tools/
  Invoke-LoadTest.ps1       PowerShell driver: concurrency, RPS, latency percentiles
```

## Deploy

On the target server, as administrator:

```powershell
# 1. IIS with ASP.NET 4.x (skip anything already installed)
Install-WindowsFeature Web-Server, Web-Asp-Net45, Web-Net-Ext45, Web-Mgmt-Console

# 2. Copy the site folder, for example to C:\inetpub\loadtest
#    (contents of site\, so that Web.config sits in the root of the app)

# 3. Application pool: .NET CLR v4.0, integrated pipeline
New-WebAppPool -Name LoadTest
Set-ItemProperty IIS:\AppPools\LoadTest -Name managedRuntimeVersion -Value 'v4.0'

# 4. Application under the Default Web Site
New-WebApplication -Site 'Default Web Site' -Name loadtest `
    -PhysicalPath 'C:\inetpub\loadtest' -ApplicationPool LoadTest

# 5. The temp directory has to be writable by the pool identity
$acl = Get-Acl 'C:\Windows\Temp'
$rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
    'IIS AppPool\LoadTest', 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
$acl.AddAccessRule($rule)
Set-Acl 'C:\Windows\Temp' $acl
```

Then open `http://<server>/loadtest/`.

If the server has an older 4.x runtime than 4.8, change both `targetFramework`
values in `Web.config` to match it (for example `4.5.2`).

## Parameters

Every parameter works on `Default.aspx` and on `Load.ashx`, in the query string
or as form fields on a POST. Names are case insensitive.

| Parameter | Default | Effect |
| --- | --- | --- |
| `cpuMs` | `0` | Milliseconds of floating point work to burn, **per thread** |
| `cpuThreads` | `1` | Threads burning CPU in parallel |
| `memMB` | `0` | Megabytes to allocate, released at the end of the request |
| `memHoldMs` | `0` | How long to hold that allocation before releasing it |
| `memTouch` | `true` | Write one byte per 4 KB page so the pages stay resident |
| `retainMB` | unchanged | Target size of the pool kept **between** requests; `0` releases it |
| `fileMB` | `0` | Megabytes written to each temp file, then read back and deleted |
| `fileCount` | `1` | How many temp files to write |
| `fileFlush` | `false` | Flush past the OS cache onto the device before closing |
| `fileVerify` | `true` | Check the size and content of the data read back |
| `delayMs` | `0` | Sleep, to occupy a request thread without using CPU |
| `gc` | `false` | Force a full garbage collection at the end of the request |
| `format` | - | `json` makes `Default.aspx` answer with JSON too |

Booleans accept `true/false`, `1/0`, `yes/no`, `on/off`.

Values above the limits in `Web.config` are **capped, not rejected**, and every
cap or unparseable value is listed under `warnings` in the response.

## Examples

```
# 4 threads burning CPU for 2 seconds each
/loadtest/Default.aspx?cpuMs=2000&cpuThreads=4

# allocate 512 MB, hold it for 5 s, release it and collect
/loadtest/Default.aspx?memMB=512&memHoldMs=5000&gc=true

# hold 1 GB until told otherwise, then release it
/loadtest/Load.ashx?retainMB=1024
/loadtest/Load.ashx?retainMB=0&gc=true

# 8 x 128 MB written to disk and read back, flushed past the OS cache
/loadtest/Load.ashx?fileMB=128&fileCount=8&fileFlush=true

# a slow request that uses no CPU (timeouts, thread pool, keepalives)
/loadtest/Load.ashx?delayMs=30000

# everything at once
/loadtest/Load.ashx?cpuMs=500&cpuThreads=2&memMB=256&fileMB=64&delayMs=250&gc=true
```

From a shell:

```bash
curl -s 'http://web01/loadtest/Load.ashx?cpuMs=250&memMB=64'
```

```powershell
Invoke-RestMethod 'http://web01/loadtest/Load.ashx?cpuMs=250&memMB=64'

# 20 workers for 60 s, with latency percentiles and per server counts
.\tools\Invoke-LoadTest.ps1 -Url 'http://vip/loadtest/Load.ashx?cpuMs=250' -Concurrency 20 -Seconds 60
```

## Response

`Load.ashx` returns JSON; `Default.aspx` renders the same object as tables, so
the labels in the browser are the JSON keys. Timings are milliseconds, sizes are
megabytes.

```json
{
  "status": "ok",
  "requestNumber": 42,
  "totalMs": 2013.44,
  "options": { "cpuMs": 2000, "cpuThreads": 4, "...": null },
  "warnings": [],
  "errors": [],
  "cpu": {
    "requestedMsPerThread": 2000,
    "threads": 4,
    "elapsedMs": 2004.11,
    "iterations": 512000000,
    "millionIterationsPerSecond": 255.47
  },
  "server": {
    "machineName": "WEB01",
    "appPool": "LoadTest",
    "processId": 5120,
    "workingSetMB": 214.6,
    "retainedMemoryMB": 0,
    "requestsServed": 42,
    "peakActiveRequests": 20,
    "...": null
  }
}
```

Status codes from `Load.ashx`: `200` when the load ran, `500` when a phase
failed (so a load generator counts it as a failure), `503` when load generation
is disabled in `Web.config`. Every response carries an `X-LoadTest-Machine`
header, which is the easiest way to confirm a load balancer is spreading
traffic - `tools/Invoke-LoadTest.ps1` summarises it for you.

`Ping.ashx` costs almost nothing and returns one line:

```
ok machine=WEB01 pid=5120 pool=LoadTest utc=2026-09-17T10:15:00.123Z requests=42 active=0 retainedMB=0
```

Use it as the load balancer health probe, and as the baseline to compare against
while `Load.ashx` is doing real work.

## Safety

The endpoint generates load on demand, so treat it as an administrative tool:

* `Web.config` caps every parameter. Tune the caps for the box before you hand
  the URL out; the defaults allow 1 GB per request and 2 GB retained.
* `LoadTest.Enabled=false` turns load generation off without removing the site.
  The page still answers, with the server snapshot and HTTP 503 on `Load.ashx`.
* Restrict access to the hosts that run your tests. `Web.config` contains a
  commented `ipSecurity` block for it; IIS site bindings or a firewall rule work
  just as well.
* `LoadTest.TempPath` is deliberately **not** settable through the URL. Point it
  at the volume you want to measure and grant the pool identity write access.
  Temp files are written with random names and deleted in a `finally` block.

## Notes

* Counters (`requestsServed`, `peakActiveRequests`) and the retained memory pool
  live in the ASP.NET AppDomain, so they reset when the application pool
  recycles. A counter that went backwards mid-test means you found a recycle.
* Releasing memory makes it collectable, not collected. Add `gc=true` to see the
  working set come back down inside the same request.
* Memory is allocated in 1 MB blocks, which land on the large object heap. That
  is intentional: it is the cheapest way to reserve a known amount, and it keeps
  `retainMB` exactly equal to the number of blocks.
* `cpuMs` is per thread, so `cpuMs=1000&cpuThreads=4` asks for about 4 CPU
  seconds in about 1 second of wall clock - if the box has the cores free.
* `millionIterationsPerSecond` is a rough but repeatable CPU score. Compare it
  between nodes, or before and after an infrastructure change.
* `cpuThreads` borrows from the .NET thread pool. Under heavy concurrency the
  pool may hand out fewer threads than requested; `elapsedMs` tells you when
  that happened.
* For a server under sustained load, enable server GC in `aspnet.config` for the
  application pool (`<gcServer enabled="true"/>`). The current setting is
  reported as `serverGc` in every response.
* Long loads need an `executionTimeout` that outlasts them. It is set to 900
  seconds in `Web.config`; IIS request timeouts and any proxy in front of the
  server need to agree.
