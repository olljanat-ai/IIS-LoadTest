using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Web;

/// <summary>
/// Renders the result object as HTML cards. It walks the same
/// <see cref="JsonObject"/> the JSON endpoint returns, so the browser view and
/// the machine readable view can never drift apart. Labels are the JSON keys on
/// purpose: what you read in the browser is what you parse in a script.
///
/// Every key and value goes through HTML encoding, because warnings can quote
/// values that came from the query string.
/// </summary>
public static class HtmlRenderer
{
    public static string Render(JsonObject root)
    {
        StringBuilder html = new StringBuilder(8192);

        List<KeyValuePair<string, object>> scalars = new List<KeyValuePair<string, object>>();
        List<KeyValuePair<string, object>> sections = new List<KeyValuePair<string, object>>();

        foreach (KeyValuePair<string, object> item in root.Items)
        {
            if (item.Value is JsonObject)
            {
                sections.Add(item);
            }
            else
            {
                scalars.Add(item);
            }
        }

        if (scalars.Count > 0)
        {
            WriteCard(html, "result", scalars);
        }

        foreach (KeyValuePair<string, object> section in sections)
        {
            WriteCard(html, section.Key, ((JsonObject)section.Value).Items);
        }

        return html.ToString();
    }

    private static void WriteCard(StringBuilder html, string title, IEnumerable<KeyValuePair<string, object>> rows)
    {
        html.Append("<section class=\"card\"><h2>").Append(Encode(title)).Append("</h2>");
        WriteTable(html, rows);
        html.Append("</section>");
    }

    private static void WriteTable(StringBuilder html, IEnumerable<KeyValuePair<string, object>> rows)
    {
        html.Append("<table>");
        foreach (KeyValuePair<string, object> row in rows)
        {
            html.Append("<tr><th>").Append(Encode(row.Key)).Append("</th><td>");

            JsonObject nested = row.Value as JsonObject;
            if (nested != null)
            {
                WriteTable(html, nested.Items);
            }
            else
            {
                html.Append(FormatValue(row.Value));
            }

            html.Append("</td></tr>");
        }

        html.Append("</table>");
    }

    /// <summary>Returns an HTML fragment: encoded, and possibly a list.</summary>
    private static string FormatValue(object value)
    {
        if (value == null)
        {
            return "<span class=\"muted\">null</span>";
        }

        if (value is bool)
        {
            bool flag = (bool)value;
            return "<span class=\"" + (flag ? "yes" : "no") + "\">" + (flag ? "true" : "false") + "</span>";
        }

        string text = value as string;
        if (text != null)
        {
            if (text.Length == 0)
            {
                return "<span class=\"muted\">(empty)</span>";
            }

            return Encode(text);
        }

        if (value is DateTime)
        {
            return Encode(((DateTime)value).ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        }

        if (value is double || value is float || value is decimal)
        {
            double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (double.IsNaN(number) || double.IsInfinity(number))
            {
                return "<span class=\"muted\">n/a</span>";
            }

            return Encode(Math.Round(number, 3).ToString("#,##0.###", CultureInfo.InvariantCulture));
        }

        if (value is sbyte || value is byte || value is short || value is ushort ||
            value is int || value is uint || value is long || value is ulong)
        {
            return Encode(string.Format(CultureInfo.InvariantCulture, "{0:#,##0}", value));
        }

        IEnumerable items = value as IEnumerable;
        if (items != null)
        {
            StringBuilder list = new StringBuilder();
            int count = 0;
            foreach (object item in items)
            {
                if (count == 0)
                {
                    list.Append("<ul>");
                }

                list.Append("<li>").Append(FormatValue(item)).Append("</li>");
                count++;
            }

            if (count == 0)
            {
                return "<span class=\"muted\">none</span>";
            }

            list.Append("</ul>");
            return list.ToString();
        }

        return Encode(Convert.ToString(value, CultureInfo.InvariantCulture));
    }

    private static string Encode(string value)
    {
        return HttpUtility.HtmlEncode(value);
    }
}
