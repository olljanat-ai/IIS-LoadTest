using System;
using System.Collections.Generic;
using System.Threading;

/// <summary>
/// Process wide counters. These live in the ASP.NET AppDomain, so they reset
/// whenever the application pool recycles - which makes them a handy way to
/// spot a recycle while a load test is running.
/// </summary>
public static class LoadStats
{
    private static readonly DateTime StartedUtcValue = DateTime.UtcNow;
    private static long _requests;
    private static int _active;
    private static int _peakActive;

    /// <summary>When this AppDomain started serving requests.</summary>
    public static DateTime StartedUtc
    {
        get { return StartedUtcValue; }
    }

    /// <summary>Load requests handled since the AppDomain started.</summary>
    public static long Requests
    {
        get { return Interlocked.Read(ref _requests); }
    }

    /// <summary>Load requests executing right now.</summary>
    public static int Active
    {
        get { return Thread.VolatileRead(ref _active); }
    }

    /// <summary>Highest number of concurrent load requests seen so far.</summary>
    public static int PeakActive
    {
        get { return Thread.VolatileRead(ref _peakActive); }
    }

    /// <summary>Registers a starting request and returns its sequence number.</summary>
    public static long BeginRequest()
    {
        long sequence = Interlocked.Increment(ref _requests);
        int active = Interlocked.Increment(ref _active);

        while (true)
        {
            int peak = Thread.VolatileRead(ref _peakActive);
            if (active <= peak)
            {
                break;
            }

            if (Interlocked.CompareExchange(ref _peakActive, active, peak) == peak)
            {
                break;
            }
        }

        return sequence;
    }

    public static void EndRequest()
    {
        Interlocked.Decrement(ref _active);
    }
}

/// <summary>
/// The persistent memory pool behind the retainMB parameter: memory that stays
/// allocated after the request finishes, so you can hold a server under memory
/// pressure and watch what the infrastructure does about it.
///
/// One block is exactly 1 MB, so the block count is the size in MB. The pool is
/// released by setting retainMB=0, or by an application pool recycle.
/// </summary>
public static class RetainedMemory
{
    public const int BlockBytes = 1024 * 1024;

    private static readonly object Sync = new object();
    private static readonly List<byte[]> Blocks = new List<byte[]>();

    public static int CurrentMB
    {
        get
        {
            lock (Sync)
            {
                return Blocks.Count;
            }
        }
    }

    /// <summary>
    /// Grows or shrinks the pool towards <paramref name="targetMB"/> and returns
    /// the size actually reached. If the allocation fails the blocks acquired so
    /// far are kept, so the caller can report how far the server got.
    /// </summary>
    public static int SetTargetMB(int targetMB, bool touch)
    {
        lock (Sync)
        {
            while (Blocks.Count > targetMB)
            {
                Blocks.RemoveAt(Blocks.Count - 1);
            }

            while (Blocks.Count < targetMB)
            {
                byte[] block = new byte[BlockBytes];
                if (touch)
                {
                    Touch(block, Blocks.Count);
                }

                Blocks.Add(block);
            }

            return Blocks.Count;
        }
    }

    /// <summary>
    /// Writes one byte per 4 KB page. The CLR hands out zeroed memory already,
    /// but touching every page keeps the pages resident and makes the working
    /// set reflect the allocation.
    /// </summary>
    public static void Touch(byte[] block, int seed)
    {
        for (int offset = 0; offset < block.Length; offset += 4096)
        {
            block[offset] = (byte)(seed + 1);
        }
    }
}
