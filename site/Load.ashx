<%@ WebHandler Language="C#" Class="LoadHandler" %>

using System;
using System.Web;

/// <summary>
/// JSON endpoint for scripts and load generators. Same parameters as
/// Default.aspx, no page lifecycle and no HTML, so the measured cost is the
/// load you asked for rather than the rendering.
///
/// Status codes: 200 when the load ran, 500 when a phase failed (so a load
/// generator counts it as a failure), 503 when load generation is disabled.
/// </summary>
public class LoadHandler : IHttpHandler
{
    public bool IsReusable
    {
        get { return false; }
    }

    public void ProcessRequest(HttpContext context)
    {
        LoadOptions options = LoadOptions.FromRequest(context.Request);
        JsonObject result = LoadEngine.Run(options);

        string status = "ok";
        foreach (System.Collections.Generic.KeyValuePair<string, object> item in result.Items)
        {
            if (item.Key == "status")
            {
                status = Convert.ToString(item.Value);
                break;
            }
        }

        HttpResponse response = context.Response;
        response.ContentType = "application/json";
        response.Cache.SetCacheability(HttpCacheability.NoCache);
        response.AppendHeader("X-LoadTest-Machine", Environment.MachineName);
        response.AppendHeader("X-LoadTest-Status", status);

        if (status == "disabled")
        {
            response.StatusCode = 503;
        }
        else if (status == "error")
        {
            response.StatusCode = 500;
        }

        // Keep the body readable even when IIS would otherwise swap in its own
        // error page for a non 200 status.
        response.TrySkipIisCustomErrors = true;
        response.Write(result.ToString());
    }
}
