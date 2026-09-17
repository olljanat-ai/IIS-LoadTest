<%@ Page Language="C#" EnableViewState="false" %>
<script runat="server">
    private LoadOptions _options;
    private JsonObject _result;
    private bool _renderJson;

    protected void Page_Load(object sender, EventArgs e)
    {
        Response.Cache.SetCacheability(HttpCacheability.NoCache);
        Response.AppendHeader("X-LoadTest-Machine", Environment.MachineName);

        _options = LoadOptions.FromRequest(Request);
        _result = LoadEngine.Run(_options);

        // format=json makes this page answer like Load.ashx, so a browser URL
        // can be handed straight to a script without editing it.
        if (_options.Format == "json")
        {
            _renderJson = true;
            Response.ContentType = "application/json";
        }
    }

    /// <summary>Writing JSON here avoids Response.End() and its thread abort.</summary>
    protected override void Render(HtmlTextWriter writer)
    {
        if (_renderJson)
        {
            writer.Write(_result.ToString());
            return;
        }

        base.Render(writer);
    }

    /// <summary>The "status" value of the result, for the badge in the header.</summary>
    private string Status()
    {
        foreach (System.Collections.Generic.KeyValuePair<string, object> item in _result.Items)
        {
            if (item.Key == "status")
            {
                return Convert.ToString(item.Value);
            }
        }

        return "ok";
    }

    /// <summary>Link to the JSON endpoint for the load currently shown.</summary>
    private string JsonUrl()
    {
        string query = _options.ToQueryString();
        return "Load.ashx" + (query.Length > 0 ? "?" + query : string.Empty);
    }

    private string Number(int value)
    {
        return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private string RetainValue()
    {
        return _options.RetainMB.HasValue ? Number(_options.RetainMB.Value) : string.Empty;
    }

    /// <summary>Renders a true/false dropdown for one of the switch parameters.</summary>
    private string Switch(string name, bool selected)
    {
        return "<select id=\"" + name + "\" name=\"" + name + "\">"
            + "<option value=\"true\"" + (selected ? " selected=\"selected\"" : string.Empty) + ">true</option>"
            + "<option value=\"false\"" + (selected ? string.Empty : " selected=\"selected\"") + ">false</option>"
            + "</select>";
    }
</script>
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>IIS Load Test</title>
<style>
    :root {
        --bg: #f6f7f9;
        --panel: #ffffff;
        --ink: #1b1f24;
        --muted: #6b7480;
        --line: #d9dee4;
        --accent: #0b6bcb;
        --ok: #1a7f4b;
        --warn: #8a5a00;
        --bad: #b3261e;
    }
    @media (prefers-color-scheme: dark) {
        :root {
            --bg: #14171a;
            --panel: #1c2024;
            --ink: #e7eaee;
            --muted: #9aa4b0;
            --line: #2e343a;
            --accent: #63a9f0;
            --ok: #57c48a;
            --warn: #e0b055;
            --bad: #f08a80;
        }
    }
    * { box-sizing: border-box; }
    body {
        margin: 0;
        padding: 24px 16px 48px;
        background: var(--bg);
        color: var(--ink);
        font: 14px/1.5 "Segoe UI", system-ui, -apple-system, Arial, sans-serif;
    }
    .wrap { max-width: 1100px; margin: 0 auto; }
    h1 { font-size: 20px; margin: 0 0 4px; }
    h2 { font-size: 13px; text-transform: uppercase; letter-spacing: .07em; color: var(--muted); margin: 0 0 10px; }
    p.lede { margin: 0 0 20px; color: var(--muted); }
    a { color: var(--accent); }
    .card {
        background: var(--panel);
        border: 1px solid var(--line);
        border-radius: 8px;
        padding: 14px 16px;
        margin: 0 0 14px;
    }
    .grid { display: flex; flex-wrap: wrap; gap: 14px; }
    .grid > .card { flex: 1 1 320px; margin: 0; }
    table { width: 100%; border-collapse: collapse; }
    th, td { text-align: left; vertical-align: top; padding: 4px 8px 4px 0; border-bottom: 1px solid var(--line); }
    tr:last-child > th, tr:last-child > td { border-bottom: 0; }
    th { font-weight: 600; width: 45%; font-family: Consolas, "Courier New", monospace; font-weight: normal; color: var(--muted); }
    td { font-variant-numeric: tabular-nums; }
    td table { margin: 0; }
    td table th { width: 55%; }
    ul { margin: 0; padding-left: 18px; }
    .muted { color: var(--muted); }
    .yes { color: var(--ok); }
    .no { color: var(--muted); }
    form.load { display: flex; flex-wrap: wrap; gap: 12px 18px; align-items: flex-end; }
    form.load div { display: flex; flex-direction: column; gap: 3px; }
    form.load label { font-size: 12px; color: var(--muted); font-family: Consolas, "Courier New", monospace; }
    input[type=number], input[type=text], select {
        width: 118px; padding: 5px 6px; font: inherit;
        color: var(--ink); background: var(--bg);
        border: 1px solid var(--line); border-radius: 5px;
    }
    button {
        padding: 6px 16px; font: inherit; font-weight: 600; cursor: pointer;
        color: #fff; background: var(--accent); border: 0; border-radius: 5px;
    }
    .presets { margin: 12px 0 0; padding: 0; list-style: none; display: flex; flex-wrap: wrap; gap: 6px 14px; font-size: 13px; }
    .status { display: inline-block; padding: 1px 9px; border-radius: 999px; font-size: 12px; font-weight: 600; }
    .status.ok { background: var(--ok); color: #fff; }
    .status.error { background: var(--bad); color: #fff; }
    .status.disabled { background: var(--warn); color: #fff; }
    code { font-family: Consolas, "Courier New", monospace; background: var(--bg); padding: 1px 4px; border-radius: 3px; }
    .ref th { width: auto; font-family: inherit; color: var(--ink); font-weight: 600; }
    .ref td code { white-space: nowrap; }
    footer { color: var(--muted); font-size: 12px; margin-top: 8px; }
</style>
</head>
<body>
<div class="wrap">

    <h1>IIS load test</h1>
    <p class="lede">
        Synthetic CPU, memory and temp file load for verifying infrastructure.
        Adjust the load with URL parameters, or with the form below.
        <span class="status <%= Server.HtmlEncode(Status()) %>"><%= Server.HtmlEncode(Status()) %></span>
    </p>

    <section class="card">
        <h2>load</h2>
        <form class="load" method="get" action="Default.aspx">
            <div><label for="cpuMs">cpuMs</label><input type="number" id="cpuMs" name="cpuMs" min="0" value="<%= Number(_options.CpuMs) %>" /></div>
            <div><label for="cpuThreads">cpuThreads</label><input type="number" id="cpuThreads" name="cpuThreads" min="1" value="<%= Number(_options.CpuThreads) %>" /></div>
            <div><label for="memMB">memMB</label><input type="number" id="memMB" name="memMB" min="0" value="<%= Number(_options.MemoryMB) %>" /></div>
            <div><label for="memHoldMs">memHoldMs</label><input type="number" id="memHoldMs" name="memHoldMs" min="0" value="<%= Number(_options.MemoryHoldMs) %>" /></div>
            <div><label for="memTouch">memTouch</label><%= Switch("memTouch", _options.MemoryTouch) %></div>
            <div><label for="retainMB">retainMB</label><input type="text" id="retainMB" name="retainMB" value="<%= RetainValue() %>" placeholder="unchanged" /></div>
            <div><label for="fileMB">fileMB</label><input type="number" id="fileMB" name="fileMB" min="0" value="<%= Number(_options.FileMB) %>" /></div>
            <div><label for="fileCount">fileCount</label><input type="number" id="fileCount" name="fileCount" min="1" value="<%= Number(_options.FileCount) %>" /></div>
            <div><label for="fileFlush">fileFlush</label><%= Switch("fileFlush", _options.FileFlush) %></div>
            <div><label for="fileVerify">fileVerify</label><%= Switch("fileVerify", _options.FileVerify) %></div>
            <div><label for="delayMs">delayMs</label><input type="number" id="delayMs" name="delayMs" min="0" value="<%= Number(_options.DelayMs) %>" /></div>
            <div><label for="gc">gc</label><%= Switch("gc", _options.ForceGc) %></div>
            <div><button type="submit">Run load</button></div>
        </form>
        <ul class="presets">
            <li><a href="Default.aspx">reset</a></li>
            <li><a href="Default.aspx?cpuMs=2000&amp;cpuThreads=4">cpu: 4 threads x 2 s</a></li>
            <li><a href="Default.aspx?memMB=256&amp;memHoldMs=2000&amp;gc=true">memory: 256 MB held 2 s</a></li>
            <li><a href="Default.aspx?retainMB=512">retain 512 MB</a></li>
            <li><a href="Default.aspx?retainMB=0&amp;gc=true">release retained</a></li>
            <li><a href="Default.aspx?fileMB=64&amp;fileCount=4&amp;fileFlush=true">disk: 4 x 64 MB flushed</a></li>
            <li><a href="Default.aspx?cpuMs=1000&amp;cpuThreads=2&amp;memMB=128&amp;fileMB=32&amp;delayMs=200">mixed</a></li>
            <li><a href="<%= Server.HtmlEncode(JsonUrl()) %>">this load as JSON</a></li>
            <li><a href="Ping.ashx">ping (no load)</a></li>
        </ul>
        <% if (!_options.HasWork) { %>
            <p class="muted" style="margin:12px 0 0">
                No load parameters were supplied, so nothing was executed and only the server snapshot is shown.
            </p>
        <% } %>
    </section>

    <div class="grid">
        <%= HtmlRenderer.Render(_result) %>
    </div>

    <section class="card">
        <h2>parameters</h2>
        <table class="ref">
            <tr><th>Parameter</th><th>Default</th><th>Effect</th></tr>
            <tr><td><code>cpuMs</code></td><td>0</td><td>Milliseconds of floating point work to burn, per thread.</td></tr>
            <tr><td><code>cpuThreads</code></td><td>1</td><td>Threads burning CPU in parallel.</td></tr>
            <tr><td><code>memMB</code></td><td>0</td><td>Megabytes to allocate, then release at the end of the request.</td></tr>
            <tr><td><code>memHoldMs</code></td><td>0</td><td>How long to hold that allocation before releasing it.</td></tr>
            <tr><td><code>memTouch</code></td><td>true</td><td>Write one byte per 4 KB page so the pages stay resident.</td></tr>
            <tr><td><code>retainMB</code></td><td>unchanged</td><td>Target size of the pool kept <em>between</em> requests. <code>0</code> releases it.</td></tr>
            <tr><td><code>fileMB</code></td><td>0</td><td>Megabytes written to each temp file, then read back and deleted.</td></tr>
            <tr><td><code>fileCount</code></td><td>1</td><td>How many temp files to write.</td></tr>
            <tr><td><code>fileFlush</code></td><td>false</td><td>Flush past the OS cache onto the device before closing.</td></tr>
            <tr><td><code>fileVerify</code></td><td>true</td><td>Check size and content of the data read back.</td></tr>
            <tr><td><code>delayMs</code></td><td>0</td><td>Sleep, to occupy a request thread without using CPU.</td></tr>
            <tr><td><code>gc</code></td><td>false</td><td>Force a full garbage collection at the end of the request.</td></tr>
            <tr><td><code>format</code></td><td>-</td><td><code>json</code> returns JSON from this page too.</td></tr>
        </table>
        <footer>
            Values above the limits in <code>Web.config</code> are capped and reported under <code>warnings</code>.
            Endpoints: <code>Default.aspx</code> (this page), <code>Load.ashx</code> (JSON), <code>Ping.ashx</code> (no load).
        </footer>
    </section>

</div>
</body>
</html>
