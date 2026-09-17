using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Web;

/// <summary>
/// A snapshot of the worker process. Every response carries one, which is what
/// makes this page useful for infrastructure checks: you can see which node and
/// which worker process answered, how much memory it is holding and whether the
/// application pool recycled underneath your test run.
/// </summary>
public static class ServerInfo
{
    private const double BytesPerMB = 1024.0 * 1024.0;

    public static JsonObject Snapshot()
    {
        JsonObject json = new JsonObject();

        json.Set("machineName", Environment.MachineName);
        json.Set("utcNow", DateTime.UtcNow);
        json.Set("appPool", Environment.GetEnvironmentVariable("APP_POOL_ID"));
        json.Set("clrVersion", Environment.Version.ToString());
        json.Set("is64BitProcess", Environment.Is64BitProcess);
        json.Set("processorCount", Environment.ProcessorCount);
        json.Set("serverGc", System.Runtime.GCSettings.IsServerGC);

        try
        {
            using (Process process = Process.GetCurrentProcess())
            {
                json.Set("processId", process.Id);
                json.Set("processStartedUtc", process.StartTime.ToUniversalTime());
                json.Set("processUptimeSeconds", (DateTime.Now - process.StartTime).TotalSeconds);
                json.Set("privateMemoryMB", process.PrivateMemorySize64 / BytesPerMB);
                json.Set("threadCount", process.Threads.Count);
            }
        }
        catch (Exception ex)
        {
            // Not fatal: a locked down trust level or restricted identity can
            // deny process introspection, everything else still works.
            json.Set("processInfoError", ex.Message);
        }

        json.Set("workingSetMB", Environment.WorkingSet / BytesPerMB);
        json.Set("gcTotalMemoryMB", GC.GetTotalMemory(false) / BytesPerMB);
        json.Set("gcCollections", new JsonObject()
            .Set("gen0", GC.CollectionCount(0))
            .Set("gen1", GC.CollectionCount(1))
            .Set("gen2", GC.CollectionCount(2)));
        json.Set("retainedMemoryMB", RetainedMemory.CurrentMB);

        int workerThreads;
        int completionPortThreads;
        int maxWorkerThreads;
        int maxCompletionPortThreads;
        ThreadPool.GetAvailableThreads(out workerThreads, out completionPortThreads);
        ThreadPool.GetMaxThreads(out maxWorkerThreads, out maxCompletionPortThreads);
        json.Set("threadPool", new JsonObject()
            .Set("availableWorkerThreads", workerThreads)
            .Set("maxWorkerThreads", maxWorkerThreads)
            .Set("availableIoThreads", completionPortThreads)
            .Set("maxIoThreads", maxCompletionPortThreads));

        json.Set("appDomainStartedUtc", LoadStats.StartedUtc);
        json.Set("appDomainUptimeSeconds", (DateTime.UtcNow - LoadStats.StartedUtc).TotalSeconds);
        json.Set("requestsServed", LoadStats.Requests);
        json.Set("activeRequests", LoadStats.Active);
        json.Set("peakActiveRequests", LoadStats.PeakActive);
        json.Set("tempPath", SafeTempPath());
        json.Set("loadGenerationEnabled", LoadConfig.Enabled);

        HttpContext context = HttpContext.Current;
        if (context != null && context.Request != null)
        {
            try
            {
                json.Set("clientIp", context.Request.UserHostAddress);
            }
            catch (Exception)
            {
                // Ignored: only a convenience field.
            }
        }

        json.Set("limits", new JsonObject()
            .Set("maxCpuMs", LoadConfig.MaxCpuMs)
            .Set("maxCpuThreads", LoadConfig.MaxCpuThreads)
            .Set("maxMemMB", LoadConfig.MaxMemoryMB)
            .Set("maxRetainMB", LoadConfig.MaxRetainedMB)
            .Set("maxFileTotalMB", LoadConfig.MaxFileTotalMB)
            .Set("maxDelayMs", LoadConfig.MaxDelayMs));

        return json;
    }

    /// <summary>Short one line summary used by Ping.ashx.</summary>
    public static string PingLine()
    {
        int processId = 0;
        try
        {
            using (Process process = Process.GetCurrentProcess())
            {
                processId = process.Id;
            }
        }
        catch (Exception)
        {
            // Ignored: the ping line stays useful without the process id.
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "ok machine={0} pid={1} pool={2} utc={3} requests={4} active={5} retainedMB={6}",
            Environment.MachineName,
            processId,
            Environment.GetEnvironmentVariable("APP_POOL_ID") ?? "-",
            DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            LoadStats.Requests,
            LoadStats.Active,
            RetainedMemory.CurrentMB);
    }

    private static string SafeTempPath()
    {
        try
        {
            return LoadConfig.TempPath;
        }
        catch (Exception ex)
        {
            return "unavailable: " + ex.Message;
        }
    }
}
