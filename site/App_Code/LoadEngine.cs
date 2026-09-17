using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Runs the requested load phases in order: CPU, transient memory, persistent
/// memory, temp file write + read back, delay, forced GC. Every phase is timed
/// and reported, and every phase is isolated so that a failure in one (a full
/// disk, an out of memory) still produces a readable result for the rest.
/// </summary>
public static class LoadEngine
{
    private const double BytesPerMB = 1024.0 * 1024.0;

    /// <summary>
    /// Where the CPU loop dumps its result. Writing to a static keeps the JIT
    /// from optimising the whole loop away. Concurrent writes race, which is
    /// fine - the value is never read.
    /// </summary>
    public static double Sink;

    public static JsonObject Run(LoadOptions options)
    {
        long requestNumber = LoadStats.BeginRequest();
        DateTime startedUtc = DateTime.UtcNow;
        Stopwatch total = Stopwatch.StartNew();

        List<string> errors = new List<string>();
        JsonObject cpu = null;
        JsonObject memory = null;
        JsonObject retained = null;
        JsonObject files = null;
        JsonObject delay = null;
        JsonObject collection = null;
        bool refused = false;

        try
        {
            if (!LoadConfig.Enabled)
            {
                refused = true;
                errors.Add("Load generation is disabled: set LoadTest.Enabled to true in Web.config.");
            }
            else
            {
                if (options.CpuMs > 0)
                {
                    cpu = Guard("cpu", errors, delegate { return RunCpu(options); });
                }

                if (options.MemoryMB > 0)
                {
                    memory = Guard("memory", errors, delegate { return RunMemory(options, errors); });
                }

                if (options.RetainMB.HasValue)
                {
                    retained = Guard("retainedMemory", errors, delegate { return RunRetain(options, errors); });
                }

                if (options.FileMB > 0 && options.FileCount > 0)
                {
                    files = Guard("files", errors, delegate { return RunFiles(options, errors); });
                }

                if (options.DelayMs > 0)
                {
                    delay = Guard("delay", errors, delegate { return RunDelay(options); });
                }

                if (options.ForceGc)
                {
                    collection = Guard("gc", errors, delegate { return RunGc(); });
                }
            }

            total.Stop();

            JsonObject result = new JsonObject();
            result.Set("status", refused ? "disabled" : (errors.Count == 0 ? "ok" : "error"));
            result.Set("requestNumber", requestNumber);
            result.Set("startedUtc", startedUtc);
            result.Set("totalMs", total.Elapsed.TotalMilliseconds);
            result.Set("workDone", options.HasWork && !refused);
            result.Set("options", options.ToJson());
            result.Set("warnings", options.Warnings);
            result.Set("errors", errors);

            if (cpu != null)
            {
                result.Set("cpu", cpu);
            }

            if (memory != null)
            {
                result.Set("memory", memory);
            }

            if (retained != null)
            {
                result.Set("retainedMemory", retained);
            }

            if (files != null)
            {
                result.Set("files", files);
            }

            if (delay != null)
            {
                result.Set("delay", delay);
            }

            if (collection != null)
            {
                result.Set("gc", collection);
            }

            result.Set("server", ServerInfo.Snapshot());
            return result;
        }
        finally
        {
            LoadStats.EndRequest();
        }
    }

    /// <summary>Runs one phase, turning a failure into a reported error.</summary>
    private static JsonObject Guard(string phase, List<string> errors, Func<JsonObject> phaseBody)
    {
        try
        {
            return phaseBody();
        }
        catch (Exception ex)
        {
            errors.Add(phase + ": " + ex.GetType().Name + ": " + ex.Message);
            return null;
        }
    }

    // ---------------------------------------------------------------- CPU ----

    private static JsonObject RunCpu(LoadOptions options)
    {
        int threads = Math.Max(1, options.CpuThreads);
        long[] iterations = new long[threads];

        Stopwatch elapsed = Stopwatch.StartNew();
        if (threads == 1)
        {
            iterations[0] = Burn(options.CpuMs);
        }
        else
        {
            ParallelOptions parallelOptions = new ParallelOptions();
            parallelOptions.MaxDegreeOfParallelism = threads;
            Parallel.For(0, threads, parallelOptions, delegate(int index)
            {
                iterations[index] = Burn(options.CpuMs);
            });
        }

        elapsed.Stop();

        long totalIterations = 0;
        for (int i = 0; i < threads; i++)
        {
            totalIterations += iterations[i];
        }

        double seconds = elapsed.Elapsed.TotalSeconds;

        JsonObject json = new JsonObject();
        json.Set("requestedMsPerThread", options.CpuMs);
        json.Set("threads", threads);
        json.Set("elapsedMs", elapsed.Elapsed.TotalMilliseconds);
        json.Set("iterations", totalIterations);
        // A rough but repeatable CPU score: compare it between nodes or before
        // and after an infrastructure change.
        json.Set("millionIterationsPerSecond", seconds > 0 ? totalIterations / seconds / 1000000.0 : 0.0);
        return json;
    }

    /// <summary>
    /// Busy loop of floating point work for roughly <paramref name="milliseconds"/>.
    /// The value stays bounded (sqrt of a small number), so the loop can run for
    /// any length of time without drifting into infinities.
    /// </summary>
    private static long Burn(int milliseconds)
    {
        const int Block = 4096;

        Stopwatch elapsed = Stopwatch.StartNew();
        long iterations = 0;
        double value = 1.0;

        while (elapsed.ElapsedMilliseconds < milliseconds)
        {
            for (int i = 0; i < Block; i++)
            {
                value = Math.Sqrt((value * 1.0000001) + (i % 97) + 1.0);
            }

            iterations += Block;
        }

        Sink += value;
        return iterations;
    }

    // ------------------------------------------------------------- memory ----

    private static JsonObject RunMemory(LoadOptions options, List<string> errors)
    {
        JsonObject json = new JsonObject();
        json.Set("requestedMB", options.MemoryMB);
        json.Set("touchedEveryPage", options.MemoryTouch);

        List<byte[]> blocks = new List<byte[]>(options.MemoryMB);
        long allocatedBytes = 0;
        Stopwatch allocate = Stopwatch.StartNew();

        try
        {
            for (int i = 0; i < options.MemoryMB; i++)
            {
                byte[] block = new byte[RetainedMemory.BlockBytes];
                if (options.MemoryTouch)
                {
                    RetainedMemory.Touch(block, i);
                }

                blocks.Add(block);
                allocatedBytes += block.Length;
            }
        }
        catch (OutOfMemoryException)
        {
            // Expected outcome of a memory test: report it instead of failing
            // the whole request with a 500 and no numbers.
            errors.Add(string.Format(
                CultureInfo.InvariantCulture,
                "memory: out of memory after {0} MB of the requested {1} MB.",
                (allocatedBytes / 1024) / 1024,
                options.MemoryMB));
        }

        allocate.Stop();

        json.Set("allocatedMB", allocatedBytes / BytesPerMB);
        json.Set("allocateMs", allocate.Elapsed.TotalMilliseconds);
        json.Set("allocateMBPerSecond", Rate(allocatedBytes, allocate.Elapsed));
        json.Set("workingSetMBWhileHeld", Environment.WorkingSet / BytesPerMB);
        json.Set("gcTotalMemoryMBWhileHeld", GC.GetTotalMemory(false) / BytesPerMB);

        if (options.MemoryHoldMs > 0)
        {
            Stopwatch held = Stopwatch.StartNew();
            Thread.Sleep(options.MemoryHoldMs);
            held.Stop();
            json.Set("heldMs", held.Elapsed.TotalMilliseconds);
        }

        blocks.Clear();
        blocks = null;

        json.Set("released", true);
        // Dropping the reference makes the memory collectable, not collected:
        // add gc=true to see the working set come back down within the request.
        json.Set("gcTotalMemoryMBAfterRelease", GC.GetTotalMemory(false) / BytesPerMB);
        return json;
    }

    private static JsonObject RunRetain(LoadOptions options, List<string> errors)
    {
        int target = options.RetainMB.HasValue ? options.RetainMB.Value : 0;
        int before = RetainedMemory.CurrentMB;

        JsonObject json = new JsonObject();
        json.Set("beforeMB", before);
        json.Set("targetMB", target);

        Stopwatch elapsed = Stopwatch.StartNew();
        int after;
        try
        {
            after = RetainedMemory.SetTargetMB(target, options.MemoryTouch);
        }
        catch (OutOfMemoryException)
        {
            after = RetainedMemory.CurrentMB;
            errors.Add(string.Format(
                CultureInfo.InvariantCulture,
                "retainedMemory: out of memory at {0} MB while growing towards {1} MB.",
                after,
                target));
        }

        elapsed.Stop();

        json.Set("currentMB", after);
        json.Set("changeMB", after - before);
        json.Set("elapsedMs", elapsed.Elapsed.TotalMilliseconds);
        json.Set("workingSetMB", Environment.WorkingSet / BytesPerMB);
        // Held until retainMB=0 or the application pool recycles.
        json.Set("note", "Retained memory survives this request. Call again with retainMB=0 to release it.");
        return json;
    }

    // -------------------------------------------------------------- files ----

    private static JsonObject RunFiles(LoadOptions options, List<string> errors)
    {
        const int BufferBytes = RetainedMemory.BlockBytes;

        string directory = LoadConfig.TempPath;

        JsonObject json = new JsonObject();
        json.Set("directory", directory);
        json.Set("fileCount", options.FileCount);
        json.Set("mbPerFile", options.FileMB);
        json.Set("flushToDisk", options.FileFlush);
        json.Set("verifyReadBack", options.FileVerify);

        // Pseudo random payload, so storage level compression or deduplication
        // cannot make the numbers look better than the hardware really is.
        byte[] payload = new byte[BufferBytes];
        new Random(Environment.TickCount).NextBytes(payload);
        byte[] readBuffer = new byte[BufferBytes];

        long expectedBytesPerFile = (long)options.FileMB * BufferBytes;
        long writtenBytes = 0;
        long readBytes = 0;
        double writeMs = 0;
        double readMs = 0;
        int filesWritten = 0;
        int filesVerified = 0;
        int verificationFailures = 0;
        int filesDeleted = 0;

        List<string> paths = new List<string>();

        try
        {
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            for (int file = 0; file < options.FileCount; file++)
            {
                string path = Path.Combine(
                    directory,
                    "iis-loadtest-" + Guid.NewGuid().ToString("N") + ".tmp");
                paths.Add(path);

                Stopwatch write = Stopwatch.StartNew();
                using (FileStream stream = new FileStream(
                    path, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferBytes, FileOptions.None))
                {
                    for (int chunk = 0; chunk < options.FileMB; chunk++)
                    {
                        stream.Write(payload, 0, payload.Length);
                        writtenBytes += payload.Length;
                    }

                    if (options.FileFlush)
                    {
                        // Push the data past the OS cache onto the device.
                        stream.Flush(true);
                    }
                }

                write.Stop();
                writeMs += write.Elapsed.TotalMilliseconds;
                filesWritten++;

                Stopwatch read = Stopwatch.StartNew();
                long fileBytes = 0;
                bool firstChunkMatches = true;
                using (FileStream stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.None, BufferBytes, FileOptions.SequentialScan))
                {
                    int count;
                    while ((count = stream.Read(readBuffer, 0, readBuffer.Length)) > 0)
                    {
                        // Verifying only the first chunk keeps the read timing
                        // honest while still proving the data came back intact.
                        if (options.FileVerify && fileBytes == 0 && !SameBytes(readBuffer, payload, count))
                        {
                            firstChunkMatches = false;
                        }

                        fileBytes += count;
                    }
                }

                read.Stop();
                readMs += read.Elapsed.TotalMilliseconds;
                readBytes += fileBytes;

                if (options.FileVerify)
                {
                    if (firstChunkMatches && fileBytes == expectedBytesPerFile)
                    {
                        filesVerified++;
                    }
                    else
                    {
                        verificationFailures++;
                        errors.Add(string.Format(
                            CultureInfo.InvariantCulture,
                            "files: read back {0} bytes instead of {1}{2}.",
                            fileBytes,
                            expectedBytesPerFile,
                            firstChunkMatches ? string.Empty : " and the content did not match"));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // A full disk or a denied ACL is a result, not a crash: keep the
            // numbers gathered so far and report what went wrong.
            errors.Add("files: " + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            foreach (string path in paths)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        filesDeleted++;
                    }
                }
                catch (Exception ex)
                {
                    errors.Add("files: could not delete " + path + ": " + ex.Message);
                }
            }
        }

        json.Set("filesWritten", filesWritten);
        json.Set("filesDeleted", filesDeleted);
        json.Set("writtenMB", writtenBytes / BytesPerMB);
        json.Set("writeMs", writeMs);
        json.Set("writeMBPerSecond", Rate(writtenBytes, TimeSpan.FromMilliseconds(writeMs)));
        json.Set("readMB", readBytes / BytesPerMB);
        json.Set("readMs", readMs);
        json.Set("readMBPerSecond", Rate(readBytes, TimeSpan.FromMilliseconds(readMs)));
        json.Set("filesVerified", filesVerified);
        json.Set("verificationFailures", verificationFailures);
        return json;
    }

    private static bool SameBytes(byte[] left, byte[] right, int count)
    {
        if (count > left.Length || count > right.Length)
        {
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }

    // ------------------------------------------------------- delay and gc ----

    private static JsonObject RunDelay(LoadOptions options)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        Thread.Sleep(options.DelayMs);
        elapsed.Stop();

        JsonObject json = new JsonObject();
        json.Set("requestedMs", options.DelayMs);
        json.Set("actualMs", elapsed.Elapsed.TotalMilliseconds);
        return json;
    }

    private static JsonObject RunGc()
    {
        long before = GC.GetTotalMemory(false);

        Stopwatch elapsed = Stopwatch.StartNew();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        elapsed.Stop();

        long after = GC.GetTotalMemory(false);

        JsonObject json = new JsonObject();
        json.Set("beforeMB", before / BytesPerMB);
        json.Set("afterMB", after / BytesPerMB);
        json.Set("freedMB", (before - after) / BytesPerMB);
        json.Set("elapsedMs", elapsed.Elapsed.TotalMilliseconds);
        json.Set("workingSetMB", Environment.WorkingSet / BytesPerMB);
        return json;
    }

    private static double Rate(long bytes, TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds <= 0)
        {
            return 0.0;
        }

        return (bytes / BytesPerMB) / elapsed.TotalSeconds;
    }
}
