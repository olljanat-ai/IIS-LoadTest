using System;
using System.Globalization;
using System.IO;
using System.Web.Configuration;

/// <summary>
/// Safety limits and deployment settings, all read from &lt;appSettings&gt; in
/// Web.config so a site can be tuned (or switched off) without a redeploy.
///
/// The limits exist so that a mistyped URL cannot take the server down: every
/// parameter is clamped to its maximum and the clamp is reported back in the
/// response as a warning.
/// </summary>
public static class LoadConfig
{
    /// <summary>Master switch. When false every load request is refused with HTTP 503.</summary>
    public static bool Enabled
    {
        get { return Bool("LoadTest.Enabled", true); }
    }

    /// <summary>Upper bound for cpuMs (per worker thread).</summary>
    public static int MaxCpuMs
    {
        get { return Int("LoadTest.MaxCpuMs", 60000); }
    }

    /// <summary>Upper bound for cpuThreads.</summary>
    public static int MaxCpuThreads
    {
        get { return Int("LoadTest.MaxCpuThreads", 64); }
    }

    /// <summary>Upper bound for memMB (per request, transient).</summary>
    public static int MaxMemoryMB
    {
        get { return Int("LoadTest.MaxMemoryMB", 1024); }
    }

    /// <summary>Upper bound for retainMB (process wide, kept between requests).</summary>
    public static int MaxRetainedMB
    {
        get { return Int("LoadTest.MaxRetainedMB", 2048); }
    }

    /// <summary>Upper bound for fileMB * fileCount within a single request.</summary>
    public static int MaxFileTotalMB
    {
        get { return Int("LoadTest.MaxFileTotalMB", 1024); }
    }

    /// <summary>Upper bound for delayMs.</summary>
    public static int MaxDelayMs
    {
        get { return Int("LoadTest.MaxDelayMs", 60000); }
    }

    /// <summary>
    /// Directory used for the temp file phase. Empty means the process temp
    /// directory. Point it at the volume or UNC share you want to measure.
    /// Deliberately NOT settable through the URL.
    /// </summary>
    public static string TempPath
    {
        get
        {
            string configured = WebConfigurationManager.AppSettings["LoadTest.TempPath"];
            if (string.IsNullOrEmpty(configured))
            {
                return Path.GetTempPath();
            }

            return configured.Trim();
        }
    }

    private static int Int(string key, int fallback)
    {
        string raw = WebConfigurationManager.AppSettings[key];
        int value;
        if (!string.IsNullOrEmpty(raw) &&
            int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) &&
            value >= 0)
        {
            return value;
        }

        return fallback;
    }

    private static bool Bool(string key, bool fallback)
    {
        string raw = WebConfigurationManager.AppSettings[key];
        bool value;
        if (!string.IsNullOrEmpty(raw) && bool.TryParse(raw.Trim(), out value))
        {
            return value;
        }

        return fallback;
    }
}
