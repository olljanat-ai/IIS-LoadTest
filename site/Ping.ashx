<%@ WebHandler Language="C#" Class="PingHandler" %>

using System;
using System.Web;

/// <summary>
/// The cheapest possible response: no load, no allocation worth mentioning.
/// Use it as the load balancer health probe, and as the baseline to compare
/// against when Load.ashx is doing real work.
/// </summary>
public class PingHandler : IHttpHandler
{
    public bool IsReusable
    {
        get { return true; }
    }

    public void ProcessRequest(HttpContext context)
    {
        HttpResponse response = context.Response;
        response.ContentType = "text/plain";
        response.Cache.SetCacheability(HttpCacheability.NoCache);
        response.AppendHeader("X-LoadTest-Machine", Environment.MachineName);
        response.Write(ServerInfo.PingLine());
    }
}
