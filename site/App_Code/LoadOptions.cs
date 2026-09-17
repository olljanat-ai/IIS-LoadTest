using System;
using System.Collections.Generic;
using System.Globalization;
using System.Web;

/// <summary>
/// The load description parsed out of the URL (query string, or form fields on
/// a POST). Every value is clamped to the limits in Web.config and any clamp or
/// parse problem is recorded in <see cref="Warnings"/> so the response can say
/// exactly which load was executed.
/// </summary>
public sealed class LoadOptions
{
    private readonly List<string> _warnings = new List<string>();

    public int CpuMs { get; set; }
    public int CpuThreads { get; set; }

    public int MemoryMB { get; set; }
    public int MemoryHoldMs { get; set; }
    public bool MemoryTouch { get; set; }

    /// <summary>Target size of the persistent pool. Null means "leave it alone".</summary>
    public int? RetainMB { get; set; }

    public int FileMB { get; set; }
    public int FileCount { get; set; }
    public bool FileFlush { get; set; }
    public bool FileVerify { get; set; }

    public int DelayMs { get; set; }
    public bool ForceGc { get; set; }

    /// <summary>"html" or "json"; the endpoint decides the default.</summary>
    public string Format { get; set; }

    public IList<string> Warnings
    {
        get { return _warnings; }
    }

    /// <summary>True when at least one phase would actually do something.</summary>
    public bool HasWork
    {
        get
        {
            return CpuMs > 0
                || MemoryMB > 0
                || RetainMB.HasValue
                || (FileMB > 0 && FileCount > 0)
                || DelayMs > 0
                || ForceGc;
        }
    }

    public static LoadOptions FromRequest(HttpRequest request)
    {
        LoadOptions options = new LoadOptions();

        options.CpuMs = options.ReadInt(request, "cpuMs", 0, 0, LoadConfig.MaxCpuMs);
        options.CpuThreads = options.ReadInt(request, "cpuThreads", 1, 1, Math.Max(1, LoadConfig.MaxCpuThreads));

        options.MemoryMB = options.ReadInt(request, "memMB", 0, 0, LoadConfig.MaxMemoryMB);
        options.MemoryHoldMs = options.ReadInt(request, "memHoldMs", 0, 0, LoadConfig.MaxDelayMs);
        options.MemoryTouch = options.ReadBool(request, "memTouch", true);

        // retainMB is parsed by hand: an unparseable value has to leave the
        // persistent pool alone, because falling back to 0 would release it.
        string retain = Read(request, "retainMB");
        if (!string.IsNullOrEmpty(retain))
        {
            int retainValue;
            if (int.TryParse(retain.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out retainValue))
            {
                options.RetainMB = options.Clamp("retainMB", retainValue, 0, LoadConfig.MaxRetainedMB);
            }
            else
            {
                options._warnings.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "retainMB: '{0}' is not a whole number, the retained pool was left unchanged.",
                    Quote(retain)));
            }
        }

        options.FileMB = options.ReadInt(request, "fileMB", 0, 0, LoadConfig.MaxFileTotalMB);
        options.FileCount = options.ReadInt(request, "fileCount", 1, 1, Math.Max(1, LoadConfig.MaxFileTotalMB));
        options.FileFlush = options.ReadBool(request, "fileFlush", false);
        options.FileVerify = options.ReadBool(request, "fileVerify", true);

        options.DelayMs = options.ReadInt(request, "delayMs", 0, 0, LoadConfig.MaxDelayMs);
        options.ForceGc = options.ReadBool(request, "gc", false);

        options.Format = (Read(request, "format") ?? string.Empty).Trim().ToLowerInvariant();

        // fileMB is per file, so the total is what has to stay under the cap.
        if (options.FileMB > 0)
        {
            long total = (long)options.FileMB * options.FileCount;
            if (total > LoadConfig.MaxFileTotalMB)
            {
                int allowed = Math.Max(1, LoadConfig.MaxFileTotalMB / options.FileMB);
                options._warnings.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "fileMB * fileCount = {0} MB exceeds the LoadTest.MaxFileTotalMB limit of {1} MB, fileCount reduced from {2} to {3}.",
                    total,
                    LoadConfig.MaxFileTotalMB,
                    options.FileCount,
                    allowed));
                options.FileCount = allowed;
            }
        }

        return options;
    }

    public JsonObject ToJson()
    {
        JsonObject json = new JsonObject();
        json.Set("cpuMs", CpuMs);
        json.Set("cpuThreads", CpuThreads);
        json.Set("memMB", MemoryMB);
        json.Set("memHoldMs", MemoryHoldMs);
        json.Set("memTouch", MemoryTouch);
        json.Set("retainMB", RetainMB.HasValue ? (object)RetainMB.Value : null);
        json.Set("fileMB", FileMB);
        json.Set("fileCount", FileCount);
        json.Set("fileFlush", FileFlush);
        json.Set("fileVerify", FileVerify);
        json.Set("delayMs", DelayMs);
        json.Set("gc", ForceGc);
        return json;
    }

    /// <summary>Rebuilds the query string for this load, for links and copy/paste.</summary>
    public string ToQueryString()
    {
        List<string> parts = new List<string>();
        if (CpuMs > 0)
        {
            parts.Add("cpuMs=" + CpuMs.ToString(CultureInfo.InvariantCulture));
            parts.Add("cpuThreads=" + CpuThreads.ToString(CultureInfo.InvariantCulture));
        }

        if (MemoryMB > 0)
        {
            parts.Add("memMB=" + MemoryMB.ToString(CultureInfo.InvariantCulture));
            if (MemoryHoldMs > 0)
            {
                parts.Add("memHoldMs=" + MemoryHoldMs.ToString(CultureInfo.InvariantCulture));
            }

            if (!MemoryTouch)
            {
                parts.Add("memTouch=false");
            }
        }

        if (RetainMB.HasValue)
        {
            parts.Add("retainMB=" + RetainMB.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (FileMB > 0)
        {
            parts.Add("fileMB=" + FileMB.ToString(CultureInfo.InvariantCulture));
            parts.Add("fileCount=" + FileCount.ToString(CultureInfo.InvariantCulture));
            if (FileFlush)
            {
                parts.Add("fileFlush=true");
            }

            if (!FileVerify)
            {
                parts.Add("fileVerify=false");
            }
        }

        if (DelayMs > 0)
        {
            parts.Add("delayMs=" + DelayMs.ToString(CultureInfo.InvariantCulture));
        }

        if (ForceGc)
        {
            parts.Add("gc=true");
        }

        return string.Join("&", parts.ToArray());
    }

    private static string Read(HttpRequest request, string name)
    {
        // Query string wins; form fields make the HTML form work on a POST too.
        string value = request.QueryString[name];
        if (string.IsNullOrEmpty(value))
        {
            try
            {
                value = request.Form[name];
            }
            catch (Exception)
            {
                value = null;
            }
        }

        return value;
    }

    private int ReadInt(HttpRequest request, string name, int fallback, int min, int max)
    {
        string raw = Read(request, name);
        if (string.IsNullOrEmpty(raw))
        {
            return fallback;
        }

        int value;
        if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            _warnings.Add(string.Format(
                CultureInfo.InvariantCulture,
                "{0}: '{1}' is not a whole number, using {2}.",
                name,
                Quote(raw),
                fallback));
            return fallback;
        }

        return Clamp(name, value, min, max);
    }

    /// <summary>Keeps a value inside its limits and records any adjustment.</summary>
    private int Clamp(string name, int value, int min, int max)
    {
        if (value < min)
        {
            _warnings.Add(string.Format(
                CultureInfo.InvariantCulture,
                "{0}: {1} is below the minimum, raised to {2}.",
                name,
                value,
                min));
            return min;
        }

        if (value > max)
        {
            _warnings.Add(string.Format(
                CultureInfo.InvariantCulture,
                "{0}: {1} exceeds the configured limit, capped at {2}.",
                name,
                value,
                max));
            return max;
        }

        return value;
    }

    private bool ReadBool(HttpRequest request, string name, bool fallback)
    {
        string raw = Read(request, name);
        if (string.IsNullOrEmpty(raw))
        {
            return fallback;
        }

        switch (raw.Trim().ToLowerInvariant())
        {
            case "1":
            case "true":
            case "yes":
            case "on":
                return true;
            case "0":
            case "false":
            case "no":
            case "off":
                return false;
            default:
                _warnings.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}: '{1}' is not a true/false value, using {2}.",
                    name,
                    Quote(raw),
                    fallback ? "true" : "false"));
                return fallback;
        }
    }

    /// <summary>Keeps echoed-back input short. Callers HTML encode on output.</summary>
    private static string Quote(string raw)
    {
        raw = raw.Trim();
        if (raw.Length <= 24)
        {
            return raw;
        }

        return raw.Substring(0, 24) + "...";
    }
}
