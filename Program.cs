using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

class Program
{
    static void Main()
    {
    const string template = @"
{RetailerName:wordwithspace}
{InvoiceDateTime:datetime:dd-MM-yyyy H:mm}
{Address:wordwithspace | prefix('ADDRESS:-')}
{BillNumber:wordwithspace}
Item Rate Qty Total
{Items[].ItemName:word} {Items[].Rate:number} {Items[].Quantity:integer} {Items[].Total:number}
Total Amount {TotalAmount:number}
Total Item {coalesce(TotalItem, RetailerName) | upper():wordwithspace}
";

        const string input = @"
ABC Retailer
15-09-2025 3:45
NY,Pal Road, ZN
Bill Num 20084
Item Rate Qty Total
Item1 34 4 136
Item2 55 2 110
Total Amount 246
Total Item 2
Thank You
";

        try
        {
            var tmpl = TemplateParser.ParseTemplate(template);
            var root = TextToJsonConverter.Convert(input, tmpl);

            var options = new JsonSerializerOptions { WriteIndented = true };
            Console.WriteLine(root.ToJsonString(options));
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error: " + ex);
        }
    }
}

/* ---------------------------
   Template parsing model
   --------------------------- */

record Placeholder(string RawInside, string TargetPath, string? DataType, string? Format, string ExpressionPart);

class TemplateLine
{
    public string RawLine { get; set; } = "";
    public List<Placeholder> Placeholders { get; } = new();
    public bool IsArrayRow { get; set; } = false;
    public string ArrayName { get; set; } = "";
    public Regex? CaptureRegex { get; set; } = null;
}

static class TemplateParser
{
    static readonly Regex placeholderRx = new(@"\{([^}]+)\}", RegexOptions.Compiled);

    public static List<TemplateLine> ParseTemplate(string template)
    {
        var lines = template
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        var result = new List<TemplateLine>();

        foreach (var raw in lines)
        {
            var tl = new TemplateLine { RawLine = raw };
            var matches = placeholderRx.Matches(raw);

            foreach (Match m in matches)
            {
                var inside = m.Groups[1].Value.Trim();

                // Extract trailing :type[:format] at depth 0 (not inside parentheses)
                string exprPart = inside;
                string? dataType = null, format = null;

                int depth = 0, splitPos = -1;
                for (int i = 0; i < inside.Length; i++)
                {
                    char c = inside[i];
                    if (c == '(') depth++;
                    else if (c == ')') depth = Math.Max(0, depth - 1);
                    else if (c == ':' && depth == 0)
                    {
                        splitPos = i;
                        break;
                    }
                }

                if (splitPos >= 0)
                {
                    var tail = inside.Substring(splitPos + 1).Trim();
                    // If the tail contains a pipe, treat everything before the pipe as exprPart, after as pipeline
                    var innerPipeIdx = tail.IndexOf('|');
                    if (innerPipeIdx >= 0)
                    {
                        exprPart = inside.Substring(0, splitPos).Trim() + " |" + tail.Substring(innerPipeIdx + 1).Trim();
                        var typeFormat = tail.Substring(0, innerPipeIdx).Trim();
                        // Correctly extract type and format if both are present
                        var firstColonIdx = typeFormat.IndexOf(':');
                        if (firstColonIdx >= 0)
                        {
                            dataType = typeFormat.Substring(0, firstColonIdx).Trim();
                            format = typeFormat.Substring(firstColonIdx + 1).Trim();
                        }
                        else
                        {
                            dataType = typeFormat.Trim();
                        }
                    }
                    else
                    {
                        exprPart = inside.Substring(0, splitPos).Trim();
                        var firstColonIdx = tail.IndexOf(':');
                        if (firstColonIdx >= 0)
                        {
                            dataType = tail.Substring(0, firstColonIdx).Trim();
                            format = tail.Substring(firstColonIdx + 1).Trim();
                        }
                        else
                        {
                            dataType = tail.Trim();
                        }
                    }
                }

                // determine target path
                string? targetPath;
                // Extract base path before any type, pipe, or function
                var baseExpr = exprPart;
                // Remove anything after first pipe
                var pipeIdx = baseExpr.IndexOf('|');
                if (pipeIdx >= 0) baseExpr = baseExpr.Substring(0, pipeIdx).Trim();
                // Remove anything after first colon (type)
                var colonIdx = baseExpr.IndexOf(':');
                if (colonIdx >= 0) baseExpr = baseExpr.Substring(0, colonIdx).Trim();
                if (IsSimplePath(baseExpr))
                {
                    targetPath = baseExpr;
                }
                else
                {
                    var firstPath = DeriveTargetFromExpression(baseExpr);
                    targetPath = firstPath ?? SanitizeExprToKey(baseExpr);
                }

                tl.Placeholders.Add(new Placeholder(inside, targetPath, dataType, format, exprPart));
            }

            // Array detection: all placeholders refer to same array base (e.g., Items[])
            if (tl.Placeholders.Count > 0)
            {
                var arrBases = tl.Placeholders
                    .Select(p => ExtractArrayBase(p.TargetPath))
                    .Where(b => b != null)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (arrBases.Count == 1 && tl.Placeholders.All(p => p.TargetPath.Contains("[]")))
                {
                    tl.IsArrayRow = true;
                    tl.ArrayName = arrBases[0]!;
                }
                else
                {
                    // build capture regex to extract placeholder groups for non-array lines
                    tl.CaptureRegex = BuildCaptureRegex(raw);
                }
            }

            result.Add(tl);
        }

        return result;
    }

    static bool IsSimplePath(string s) => Regex.IsMatch(s, @"^[A-Za-z0-9_\.\[\]]+$");

    static string? ExtractArrayBase(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var m = Regex.Match(path, @"^(?<base>[A-Za-z0-9_]+)\[\]");
        return m.Success ? m.Groups["base"].Value : null;
    }

    static string? DeriveTargetFromExpression(string expr)
    {
        // If expression is fn(arg1, ...) and arg1 is a simple path, use that
        var m = Regex.Match(expr.Trim(), @"^(?<fn>[A-Za-z_][A-Za-z0-9_]*)\((?<args>.*)\)$", RegexOptions.Singleline);
        if (!m.Success) return null;
        var argsRaw = m.Groups["args"].Value;
        var args = SplitArgs(argsRaw);
        if (args.Count > 0)
        {
            var first = args[0].Trim();
            if (IsSimplePath(first)) return first;
        }
        return null;
    }

    static List<string> SplitArgs(string s)
    {
        var outList = new List<string>();
        if (string.IsNullOrWhiteSpace(s)) return outList;
        int depth = 0;
        bool inQuotes = false;
        int start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"' && (i == 0 || s[i - 1] != '\\')) inQuotes = !inQuotes;
            if (!inQuotes)
            {
                if (c == '(') depth++;
                else if (c == ')') depth = Math.Max(0, depth - 1);
                else if (c == ',' && depth == 0)
                {
                    outList.Add(s.Substring(start, i - start).Trim());
                    start = i + 1;
                }
            }
        }
        var last = s.Substring(start).Trim();
        if (last.Length > 0) outList.Add(last);
        return outList;
    }

    static string SanitizeExprToKey(string expr)
    {
        var sb = new StringBuilder();
        foreach (var c in expr)
            sb.Append(char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_');
        return sb.ToString();
    }

    static Regex BuildCaptureRegex(string templateLine)
    {
        var matches = placeholderRx.Matches(templateLine);
        if (matches.Count == 0) return new Regex("^(.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        var sb = new StringBuilder();
        sb.Append("^\\s*");
        int last = 0;
        for (int i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            var literal = templateLine.Substring(last, m.Index - last);
            if (!string.IsNullOrEmpty(literal))
                sb.Append(Regex.Escape(literal));
            // non-greedy capture for placeholder
            sb.Append("(.+?)");
            last = m.Index + m.Length;
        }
        var trailing = templateLine.Substring(last);
        if (!string.IsNullOrEmpty(trailing)) sb.Append(Regex.Escape(trailing));
        sb.Append("\\s*$");
        return new Regex(sb.ToString(), RegexOptions.Compiled | RegexOptions.IgnoreCase);
    }
}

/* ---------------------------
   Text -> JSON converter
   --------------------------- */
static class TextToJsonConverter
{
    public static JsonObject Convert(string inputText, List<TemplateLine> templateLines)
    {
        var lines = inputText
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        int idx = 0;
        var root = new JsonObject();

        for (int t = 0; t < templateLines.Count && idx < lines.Count; t++)
        {
            var tpl = templateLines[t];

            if (tpl.IsArrayRow)
            {
                var arrName = tpl.ArrayName;
                if (!root.TryGetPropertyValue(arrName, out var arrNode) || arrNode == null)
                {
                    arrNode = new JsonArray();
                    root[arrName] = arrNode;
                }
                var arr = arrNode.AsArray();

                // attempt to skip header line if text matches non-placeholder literal
                var headerLiteral = Regex.Replace(tpl.RawLine, @"\{[^}]+\}", "").Trim();
                if (!string.IsNullOrWhiteSpace(headerLiteral) && idx < lines.Count &&
                    lines[idx].StartsWith(headerLiteral, StringComparison.OrdinalIgnoreCase))
                {
                    idx++;
                }

                // stop condition: next non-array template literal (if any) matches next input line
                string? stopPrefix = GetNextNonArrayLiteral(templateLines, t + 1);

                while (idx < lines.Count)
                {
                    if (stopPrefix != null && lines[idx].StartsWith(stopPrefix, StringComparison.OrdinalIgnoreCase))
                        break;

                    var inputLine = lines[idx];
                    var tokens = Regex.Split(inputLine, @"\s+").Where(p => p.Length > 0).ToArray();

                    var item = new JsonObject();
                    for (int p = 0; p < tpl.Placeholders.Count; p++)
                    {
                        var ph = tpl.Placeholders[p];
                        var token = p < tokens.Length ? tokens[p] : "";
                        var conv = ConvertByType(token, ph.DataType, ph.Format);
                        // after array base
                        var after = ph.TargetPath.Contains("[]")
                            ? ph.TargetPath.Substring(ph.TargetPath.IndexOf("[]") + 2).TrimStart('.')
                            : ph.TargetPath;
                        SetValue(item, after, conv);
                    }
                    arr.Add(item);
                    idx++;
                }
            }
            else
            {
                if (idx >= lines.Count) break;

                var inputLine = lines[idx];

                if (tpl.Placeholders.Count == 0)
                {
                    var lit = tpl.RawLine.Trim();
                    if (!string.IsNullOrEmpty(lit) && inputLine.StartsWith(lit, StringComparison.OrdinalIgnoreCase))
                        idx++;
                    continue;
                }

                bool matched = false;
                // try capture regex first
                if (tpl.CaptureRegex != null)
                {
                    var m = tpl.CaptureRegex.Match(inputLine);
                    if (m.Success)
                    {
                        for (int i = 0; i < tpl.Placeholders.Count; i++)
                        {
                            var ph = tpl.Placeholders[i];
                            var captured = m.Groups[i + 1].Value.Trim();
                            object? value = captured;
                            if (ph.ExpressionPart.Contains("|"))
                            {
                                value = ExpressionEngine.EvaluatePipeline(ph.ExpressionPart, root, captured);
                            }
                            var conv = ConvertByType(value?.ToString() ?? "", ph.DataType, ph.Format);
                            SetValue(root, ph.TargetPath, conv);
                        }
                        matched = true;
                        idx++;
                    }
                }

                if (matched) continue;

                // if single placeholder that is simple path -> consume full line as value
                if (tpl.Placeholders.Count == 1 && IsOnlyPlaceholderLine(tpl.RawLine))
                {
                    var ph = tpl.Placeholders[0];
                    object? value = inputLine;
                    if (ph.ExpressionPart.Contains("|"))
                    {
                        value = ExpressionEngine.EvaluatePipeline(ph.ExpressionPart, root, inputLine);
                    }
                    // Always apply type/format conversion
                    var conv = ConvertByType(value?.ToString() ?? "", ph.DataType, ph.Format);
                    SetValue(root, ph.TargetPath, conv);
                    idx++;
                    continue;
                }

                // fallback: token-based mapping
                var fallbackTokens = Regex.Split(inputLine, @"\s+").Where(p => p.Length > 0).ToArray();
                for (int p = 0; p < tpl.Placeholders.Count; p++)
                {
                    var ph = tpl.Placeholders[p];
                    var tok = p < fallbackTokens.Length ? fallbackTokens[p] : "";
                    object? value = tok;
                    if (ph.ExpressionPart.Contains("|"))
                    {
                        value = ExpressionEngine.EvaluatePipeline(ph.ExpressionPart, root, tok);
                    }
                    var conv = ConvertByType(value?.ToString() ?? "", ph.DataType, ph.Format);
                    SetValue(root, ph.TargetPath, conv);
                }
                idx++;
            }
        }

        // Evaluate expressions (pipelines & functions) for placeholders that had expressions (non-simple path)
        EvaluateExpressionPlaceholders(root, templateLines);

        return root;
    }

    static bool IsOnlyPlaceholderLine(string rawLine)
    {
        // line that is exactly "{...}" ignoring whitespace
        return Regex.IsMatch(rawLine.Trim(), @"^\{\s*[^}]+\s*\}$");
    }

    static void EvaluateExpressionPlaceholders(JsonObject root, List<TemplateLine> templateLines)
    {
        foreach (var tpl in templateLines)
        {
            foreach (var ph in tpl.Placeholders)
            {
                // Only evaluate pipeline if it was not already applied during initial mapping
                if (!ph.ExpressionPart.Contains("|"))
                {
                    if (!IsSimplePath(ph.ExpressionPart))
                    {
                        var eval = ExpressionEngine.EvaluatePipeline(ph.ExpressionPart, root);
                        if (eval != null)
                        {
                            // convert to specified type if provided
                            var final = eval;
                            if (!string.IsNullOrEmpty(ph.DataType))
                                final = ConvertByType(eval?.ToString() ?? "", ph.DataType, ph.Format);
                            SetValue(root, ph.TargetPath, final);
                        }
                    }
                }
            }
        }
    }

    static object? ConvertByType(string raw, string? type, string? format)
    {
        raw ??= "";
        if (string.IsNullOrWhiteSpace(type)) return raw;
        switch (type.ToLowerInvariant())
        {
            case "number":
            case "decimal":
                if (decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) return d;
                var m = Regex.Match(raw, @"-?\d+(\.\d+)?");
                if (m.Success && decimal.TryParse(m.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out d)) return d;
                return raw;
            case "integer":
            case "int":
                if (int.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var i)) return i;
                var mi = Regex.Match(raw, @"-?\d+");
                if (mi.Success && int.TryParse(mi.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out i)) return i;
                return raw;
            case "datetime":
                if (!string.IsNullOrEmpty(format) && DateTime.TryParseExact(raw, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt0))
                    return dt0.ToString("o");
                if (DateTime.TryParse(raw, out var dt1)) return dt1.ToString("o");
                return raw;
            case "date":
                if (!string.IsNullOrEmpty(format) && DateTime.TryParseExact(raw, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d0))
                    return d0.ToString("yyyy-MM-dd");
                if (DateTime.TryParse(raw, out var d1)) return d1.ToString("yyyy-MM-dd");
                return raw;
            case "time":
                if (DateTime.TryParse(raw, out var t0)) return t0.ToString("HH:mm:ss");
                return raw;
            default:
                return raw;
        }
    }

    static void SetValue(JsonObject root, string path, object? value)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var parts = path.Split('.');
        JsonNode? cursor = root;

        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            bool last = (i == parts.Length - 1);

            if (part.EndsWith("[]"))
            {
                var name = part.Substring(0, part.Length - 2);
                if (cursor is JsonObject co)
                {
                    if (!co.TryGetPropertyValue(name, out var existing) || existing == null)
                    {
                        var newArr = new JsonArray();
                        co[name] = newArr;
                        cursor = newArr;
                    }
                    else cursor = existing;
                }
                else if (cursor is JsonArray ca)
                {
                    var lastObj = ca.Count > 0 ? ca[^1] : null;
                    if (ca.Count == 0 || lastObj is not JsonObject)
                    {
                        var newObj = new JsonObject();
                        ca.Add(newObj);
                        lastObj = newObj;
                    }
                    cursor = lastObj;
                    i--; // retry this part now that cursor is object
                }
            }
            else
            {
                if (last)
                {
                    if (cursor is JsonObject obj)
                    {
                        obj[part] = JsonValueFromObject(value);
                        return;
                    }
                    if (cursor is JsonArray arr)
                    {
                        var newObj = new JsonObject { [part] = JsonValueFromObject(value) };
                        arr.Add(newObj);
                        return;
                    }
                }
                else
                {
                    if (cursor is JsonObject obj)
                    {
                        if (!obj.TryGetPropertyValue(part, out var next) || next == null)
                        {
                            var newObj = new JsonObject();
                            obj[part] = newObj;
                            cursor = newObj;
                        }
                        else cursor = next;
                    }
                    else if (cursor is JsonArray arr)
                    {
                        var lastObj = arr.Count > 0 ? arr[^1] : null;
                        if (arr.Count == 0 || lastObj is not JsonObject)
                        {
                            var newObj = new JsonObject();
                            arr.Add(newObj);
                            lastObj = newObj;
                        }
                        cursor = lastObj;
                        i--; // retry this part on the newly created object
                    }
                }
            }
        }
    }

    static JsonNode? JsonValueFromObject(object? v)
    {
        if (v == null) return JsonValue.Create((string?)null);
        if (v is JsonNode jn) return jn;
        if (v is string s) return JsonValue.Create(s);
        if (v is int i) return JsonValue.Create(i);
        if (v is long l) return JsonValue.Create(l);
        if (v is decimal d) return JsonValue.Create(d);
        if (v is double db) return JsonValue.Create(db);
        if (v is bool b) return JsonValue.Create(b);
        if (v is DateTime dt) return JsonValue.Create(dt.ToString("o"));
        return JsonValue.Create(v.ToString());
    }

    static string? GetNextNonArrayLiteral(List<TemplateLine> templates, int start)
    {
        for (int i = start; i < templates.Count; i++)
        {
            if (!templates[i].IsArrayRow)
            {
                var lit = Regex.Replace(templates[i].RawLine, @"\{[^}]+\}", "").Trim();
                if (!string.IsNullOrEmpty(lit)) return lit;
            }
        }
        return null;
    }

    static bool IsSimplePath(string s) => Regex.IsMatch(s, @"^[A-Za-z0-9_\.\[\]]+$");
}

/* ---------------------------
   Expression engine (functions + pipes)
   --------------------------- */
static class ExpressionEngine
{
    public static object? EvaluatePipeline(string pipelineExpr, JsonObject context, object? initialValue = null)
    {
        var parts = SplitPipes(pipelineExpr);
        if (parts.Count == 0) return null;
        object? value;
        int startIdx = 0;
        if (initialValue != null)
        {
            value = initialValue;
            // If the first term is a simple path, skip it
            if (Regex.IsMatch(parts[0].Trim(), @"^[A-Za-z0-9_\.\[\]]+$"))
                startIdx = 1;
        }
        else
        {
            value = EvaluateTerm(parts[0].Trim(), context);
            startIdx = 1;
        }
        for (int i = startIdx; i < parts.Count; i++)
            value = ApplyPipe(parts[i].Trim(), value, context);
        return value;
    }

    static List<string> SplitPipes(string s)
    {
        var list = new List<string>();
        int depth = 0;
        bool inQuotes = false;
        int start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"' && (i == 0 || s[i - 1] != '\\')) inQuotes = !inQuotes;
            if (!inQuotes)
            {
                if (c == '(') depth++;
                else if (c == ')') depth = Math.Max(0, depth - 1);
                else if (c == '|' && depth == 0)
                {
                    list.Add(s.Substring(start, i - start));
                    start = i + 1;
                }
            }
        }
        list.Add(s.Substring(start));
        return list;
    }

    static object? EvaluateTerm(string term, JsonObject ctx)
    {
        term = term.Trim();
        if (term.Length == 0) return null;

        var mfn = Regex.Match(term, @"^(?<fn>[A-Za-z_][A-Za-z0-9_]*)\((?<args>.*)\)$", RegexOptions.Singleline);
        if (mfn.Success)
        {
            var fn = mfn.Groups["fn"].Value;
            var argsRaw = mfn.Groups["args"].Value;
            var argStrs = SplitArgs(argsRaw);
            var args = argStrs.Select(a => EvaluateTerm(a, ctx)).ToList();
            return EvaluateFunction(fn, args, ctx);
        }

        // quoted literal
        if (term.Length >= 2 && term[0] == '"' && term[^1] == '"')
            return Unescape(term[1..^1]);

        // numeric literal
        if (decimal.TryParse(term, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) return d;

        // path like Items[].Total or Address.City
        if (Regex.IsMatch(term, @"^[A-Za-z0-9_\.\[\]]+$"))
            return GetValueAtPath(ctx, term);

        return term;
    }

    static List<string> SplitArgs(string s)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(s)) return list;
        int depth = 0;
        bool inQuotes = false;
        int start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"' && (i == 0 || s[i - 1] != '\\')) inQuotes = !inQuotes;
            if (!inQuotes)
            {
                if (c == '(') depth++;
                else if (c == ')') depth = Math.Max(0, depth - 1);
                else if (c == ',' && depth == 0)
                {
                    list.Add(s.Substring(start, i - start).Trim());
                    start = i + 1;
                }
            }
        }
        var last = s.Substring(start).Trim();
        if (last.Length > 0) list.Add(last);
        return list;
    }

    static object? EvaluateFunction(string fn, List<object?> args, JsonObject ctx)
    {
        switch (fn.ToLowerInvariant())
        {
            case "coalesce":
                foreach (var a in args)
                    if (!IsNullOrEmpty(a))
                        return a;
                return null;
            case "concat":
                return string.Concat(args.Select(a => a?.ToString() ?? ""));
            case "sum":
                if (args.Count >= 1)
                {
                    var first = args[0];
                    if (first is JsonArray ja) return SumArray(ja);
                    if (first is string sp)
                    {
                        var v = GetValueAtPath(ctx, sp);
                        if (v is JsonArray ja2) return SumArray(ja2);
                    }
                    var decs = args.Where(a => a != null).Select(a => ConvertToDecimal(a)).ToList();
                    return decs.Sum();
                }
                return 0m;
            case "count":
                if (args.Count >= 1)
                {
                    var first = args[0];
                    if (first is JsonArray ja) return ja.Count;
                    if (first is string sp) { var v = GetValueAtPath(ctx, sp); if (v is JsonArray j2) return j2.Count; }
                }
                return 0;
            case "valueof":
                if (args.Count >= 1 && args[0] is string p) return GetValueAtPath(ctx, p);
                return null;
            case "join":
                if (args.Count >= 1)
                {
                    var sep = args.Count >= 2 ? args[1]?.ToString() ?? ", " : ", ";
                    if (args[0] is JsonArray jarr && jarr != null) return string.Join(sep, jarr.Select(x => x?.ToString() ?? ""));
                    if (args[0] is string sp)
                    {
                        var v = GetValueAtPath(ctx, sp);
                        if (v is JsonArray j && j != null) return string.Join(sep, j.Select(x => x?.ToString() ?? ""));
                    }
                }
                return "";
            default:
                return null;
        }
    }

    static decimal ConvertToDecimal(object? a)
    {
        if (a == null) return 0m;
        if (a is decimal d) return d;
        if (a is int i) return i;
        if (a is long l) return l;
        if (decimal.TryParse(a.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var x)) return x;
        return 0m;
    }

    static decimal SumArray(JsonArray ja)
    {
        decimal s = 0;
        foreach (var t in ja)
        {
            if (t == null) continue;
            if (t is JsonValue jv)
            {
                if (jv.TryGetValue<decimal>(out var dv)) s += dv;
                else if (decimal.TryParse(jv.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var p)) s += p;
            }
            else
            {
                if (decimal.TryParse(t.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var p)) s += p;
            }
        }
        return s;
    }

    static object? ApplyPipe(string pipeExpr, object? input, JsonObject ctx)
    {
        var m = Regex.Match(pipeExpr.Trim(), @"^(?<fn>[A-Za-z_][A-Za-z0-9_]*)\((?<args>.*)\)$", RegexOptions.Singleline);
        string fn = m.Success ? m.Groups["fn"].Value.ToLowerInvariant() : pipeExpr.Trim().ToLowerInvariant();
        var argStrs = m.Success ? SplitArgs(m.Groups["args"].Value) : new List<string>();

        switch (fn)
        {
            case "upper":
                return input?.ToString()?.ToUpperInvariant();
            case "lower":
                return input?.ToString()?.ToLowerInvariant();
            case "trim":
                return input?.ToString()?.Trim();
            case "replace":
                if (argStrs.Count >= 2)
                {
                    var a0 = EvalArg(argStrs[0], ctx)?.ToString() ?? "";
                    var a1 = EvalArg(argStrs[1], ctx)?.ToString() ?? "";
                    return input?.ToString()?.Replace(a0, a1);
                }
                return input;
            case "concat":
            case "suffix":
                if (argStrs.Count >= 1)
                {
                    var tail = EvalArg(argStrs[0], ctx)?.ToString() ?? "";
                    return (input?.ToString() ?? "") + tail;
                }
                return input;
            case "prefix":
                if (argStrs.Count >= 1)
                {
                    var prefix = argStrs[0].Trim();
                    // Remove surrounding quotes if present
                    if (prefix.Length >= 2 && prefix[0] == '\'' && prefix[^1] == '\'')
                        prefix = prefix.Substring(1, prefix.Length - 2);
                    return prefix + (input?.ToString() ?? "");
                }
                return input;
            case "default":
            case "coalesce":
                if (!IsNullOrEmpty(input)) return input;
                if (argStrs.Count >= 1) return EvaluateTerm(argStrs[0], ctx);
                return input;
            case "tonumber":
            case "toint":
                if (input == null) return null;
                if (decimal.TryParse(input.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var dv)) return dv;
                return input;
            case "dateformat":
            case "format":
                if (argStrs.Count >= 1)
                {
                    var fmt = EvalArg(argStrs[0], ctx)?.ToString();
                    if (DateTime.TryParse(input?.ToString(), out var dt)) return dt.ToString(fmt ?? "o");
                }
                return input;
            default:
                return input;
        }
    }

    static object? EvalArg(string arg, JsonObject ctx)
    {
        arg = arg.Trim();
        if (arg.Length >= 2 && arg[0] == '"' && arg[^1] == '"') return Unescape(arg[1..^1]);
        if (decimal.TryParse(arg, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) return d;
        if (Regex.IsMatch(arg, @"^[A-Za-z0-9_\.\[\]]+$")) return GetValueAtPath(ctx, arg);
        return EvaluateTerm(arg, ctx);
    }

    public static object? GetValueAtPath(JsonObject root, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (path.Contains("[]"))
        {
            var arrName = path.Substring(0, path.IndexOf("[]"));
            var rest = path.Length > arrName.Length + 2 ? path.Substring(path.IndexOf("[]") + 2).TrimStart('.') : null;
            var token = SelectToken(root, arrName);
            if (token is JsonArray ja)
            {
                if (string.IsNullOrEmpty(rest)) return ja;
                var outArr = new JsonArray();
                foreach (var el in ja)
                {
                    if (el is JsonObject jo)
                    {
                        var sub = SelectToken(jo, rest);
                        if (sub != null) outArr.Add(sub);
                    }
                }
                return outArr;
            }
            return null;
        }
        return SelectToken(root, path);
    }

    static JsonNode? SelectToken(JsonNode node, string path)
    {
        if (node == null || string.IsNullOrWhiteSpace(path)) return null;
        var parts = path.Split('.');
        JsonNode? cur = node;
        foreach (var p in parts)
        {
            if (cur is JsonObject jo && jo.TryGetPropertyValue(p, out var next))
                cur = next;
            else return null;
        }
        return cur;
    }

    static bool IsNullOrEmpty(object? o)
    {
        if (o == null) return true;
        if (o is string s) return string.IsNullOrWhiteSpace(s);
        if (o is JsonNode jn)
        {
            if (jn is JsonValue jv && jv.TryGetValue<string>(out var sv)) return string.IsNullOrWhiteSpace(sv);
        }
        return false;
    }

    static object? Unescape(string s) => s.Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\t", "\t");
}
