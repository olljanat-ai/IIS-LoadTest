using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

/// <summary>
/// Minimal dependency-free JSON object writer.
///
/// Values keep their insertion order, which lets the very same object graph be
/// rendered either as JSON (Load.ashx) or as HTML tables (Default.aspx) without
/// describing the results twice.
///
/// Supported value types: null, string, bool, the integral types, double/float/
/// decimal, DateTime (written as UTC ISO-8601), JsonObject and IEnumerable.
/// </summary>
public sealed class JsonObject
{
    private readonly List<KeyValuePair<string, object>> _items = new List<KeyValuePair<string, object>>();

    public IList<KeyValuePair<string, object>> Items
    {
        get { return _items; }
    }

    public JsonObject Set(string name, object value)
    {
        _items.Add(new KeyValuePair<string, object>(name, value));
        return this;
    }

    public override string ToString()
    {
        StringBuilder sb = new StringBuilder(4096);
        WriteValue(sb, this, 0);
        return sb.ToString();
    }

    private static void WriteValue(StringBuilder sb, object value, int depth)
    {
        if (value == null)
        {
            sb.Append("null");
            return;
        }

        JsonObject nested = value as JsonObject;
        if (nested != null)
        {
            WriteObject(sb, nested, depth);
            return;
        }

        string text = value as string;
        if (text != null)
        {
            WriteString(sb, text);
            return;
        }

        if (value is bool)
        {
            sb.Append(((bool)value) ? "true" : "false");
            return;
        }

        if (value is double || value is float || value is decimal)
        {
            double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (double.IsNaN(number) || double.IsInfinity(number))
            {
                sb.Append("null");
                return;
            }

            sb.Append(Math.Round(number, 3).ToString("0.###", CultureInfo.InvariantCulture));
            return;
        }

        if (value is sbyte || value is byte || value is short || value is ushort ||
            value is int || value is uint || value is long || value is ulong)
        {
            sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
            return;
        }

        if (value is DateTime)
        {
            WriteString(sb, ((DateTime)value).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            return;
        }

        IEnumerable items = value as IEnumerable;
        if (items != null)
        {
            WriteArray(sb, items, depth);
            return;
        }

        WriteString(sb, Convert.ToString(value, CultureInfo.InvariantCulture));
    }

    private static void WriteObject(StringBuilder sb, JsonObject value, int depth)
    {
        if (value._items.Count == 0)
        {
            sb.Append("{}");
            return;
        }

        string padding = Padding(depth + 1);
        sb.Append("{\n");
        for (int i = 0; i < value._items.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(",\n");
            }

            sb.Append(padding);
            WriteString(sb, value._items[i].Key);
            sb.Append(": ");
            WriteValue(sb, value._items[i].Value, depth + 1);
        }

        sb.Append("\n").Append(Padding(depth)).Append("}");
    }

    private static void WriteArray(StringBuilder sb, IEnumerable value, int depth)
    {
        string padding = Padding(depth + 1);
        bool any = false;
        StringBuilder body = new StringBuilder();
        foreach (object item in value)
        {
            if (any)
            {
                body.Append(",\n");
            }

            body.Append(padding);
            WriteValue(body, item, depth + 1);
            any = true;
        }

        if (!any)
        {
            sb.Append("[]");
            return;
        }

        sb.Append("[\n").Append(body).Append("\n").Append(Padding(depth)).Append("]");
    }

    private static string Padding(int depth)
    {
        return new string(' ', depth * 2);
    }

    private static void WriteString(StringBuilder sb, string value)
    {
        sb.Append('"');
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < ' ')
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');
    }
}
