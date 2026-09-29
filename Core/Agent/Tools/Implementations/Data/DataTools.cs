using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CsAgent.Core.Agent;

public static partial class ToolDispatcher
{
    private static string ReadJson(string path, string? query)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (!IsSafePath(full))
                return $"Error: read_json - Path '{full}' is not allowed for reading. Only files in the current working directory are permitted.";

            if (!File.Exists(full)) return $"Error: not found '{full}'";

            var len = new FileInfo(full).Length;
            if (len > 512_000) return $"Error: file too large ({len / 1024} KB). Use sh to grep/head.";

            var text = File.ReadAllText(full, Encoding.UTF8);
            JsonNode? node;
            try { node = JsonNode.Parse(text); }
            catch (JsonException ex) { return $"Error: read_json - invalid JSON in '{full}': {ex.Message}"; }

            if (node is null) return $"Error: read_json - '{full}' contains no JSON value.";

            if (!string.IsNullOrWhiteSpace(query))
            {
                var result = QueryJson(node, query);
                if (result is null)
                    return $"Error: read_json - query '{query}' not found in '{full}'.";
                node = result;
            }

            return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex) { return $"Error: read_json — {ex.Message}"; }
    }
    private static string ParseOutput(string output, string format, string? query)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(output))
                return "Error: parse_output - 'output' argument is required.";

            if (output.Length > MaxParseOutputBytes)
                return $"Error: parse_output - output too large ({output.Length / 1024} KB). Max is {MaxParseOutputBytes / 1024} KB.";

            var fmt = (format ?? "auto").Trim().ToLowerInvariant();
            JsonNode? parsed;

            switch (fmt)
            {
                case "json":
                    try { parsed = JsonNode.Parse(output); }
                    catch (JsonException ex) { return $"Error: parse_output - invalid JSON: {ex.Message}"; }
                    break;

                case "keyvalue":
                    parsed = ParseKeyValue(output);
                    break;

                case "csv":
                    parsed = ParseCsv(output);
                    break;

                case "auto":
                default:
                    parsed = ParseAuto(output);
                    break;
            }

            if (parsed is null)
                return "Error: parse_output - could not parse the output into structured data.";

            if (!string.IsNullOrWhiteSpace(query))
            {
                var result = QueryJson(parsed, query);
                if (result is null)
                    return $"Error: parse_output - query '{query}' not found in parsed result.";
                parsed = result;
            }

            return parsed.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex) { return $"Error: parse_output — {ex.Message}"; }
    }
    /// <summary>
    /// Auto-detects the format: tries JSON first, then key=value / key: value lines,
    /// then CSV/TSV rows, and finally falls back to a plain text object.
    /// </summary>
    private static JsonNode? ParseAuto(string output)
    {
        var trimmed = output.Trim();

        // 1) Try JSON.
        try { return JsonNode.Parse(trimmed); }
        catch { /* not JSON */ }

        // 2) Try key=value / key: value lines.
        var kv = ParseKeyValue(trimmed);
        if (kv is JsonObject kvObj && kvObj.Count > 0)
            return kvObj;

        // 3) Try CSV/TSV rows.
        var csv = ParseCsv(trimmed);
        if (csv is JsonArray csvArr && csvArr.Count > 0)
            return csvArr;

        // 4) Fallback: wrap as a plain text object.
        return new JsonObject { ["text"] = trimmed };
    }
    /// <summary>
    /// Parses lines of the form "key=value" or "key: value" into a JSON object.
    /// </summary>
    private static JsonNode ParseKeyValue(string output)
    {
        var obj = new JsonObject();
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            int sep = line.IndexOf('=');
            if (sep < 0) sep = line.IndexOf(':');
            if (sep <= 0) continue;

            var key = line[..sep].Trim().Trim('"', '\'');
            var value = line[(sep + 1)..].Trim().Trim('"', '\'');

            if (key.Length == 0) continue;
            obj[key] = CoerceScalar(value);
        }
        return obj;
    }
    /// <summary>
    /// Parses comma- or tab-separated rows into a JSON array of objects (using the
    /// first row as headers when present) or arrays of scalars.
    /// </summary>
    private static JsonNode ParseCsv(string output)
    {
        var lines = output.Split('\n')
                          .Select(l => l.TrimEnd('\r'))
                          .Where(l => l.Trim().Length > 0)
                          .ToList();

        if (lines.Count == 0) return new JsonArray();

        // Detect delimiter: prefer tab if present, else comma.
        var hasTab = lines.Any(l => l.Contains('\t'));
        var delim = hasTab ? '\t' : ',';

        var rows = lines.Select(l => SplitDelimited(l, delim)).ToList();
        var arr = new JsonArray();

        // Heuristic: if the first row has all non-numeric, non-empty cells, treat as header.
        var first = rows[0];
        bool hasHeader = first.Count > 0 &&
                         first.All(c => c.Length > 0 && !double.TryParse(c, out _));

        int start = hasHeader ? 1 : 0;

        for (int i = start; i < rows.Count; i++)
        {
            var cells = rows[i];
            if (hasHeader)
            {
                var rowObj = new JsonObject();
                for (int c = 0; c < cells.Count; c++)
                {
                    var header = c < first.Count ? first[c].Trim() : $"col{c}";
                    if (header.Length == 0) header = $"col{c}";
                    rowObj[header] = CoerceScalar(cells[c].Trim());
                }
                arr.Add(rowObj);
            }
            else
            {
                var rowArr = new JsonArray();
                foreach (var cell in cells) rowArr.Add(CoerceScalar(cell.Trim()));
                arr.Add(rowArr);
            }
        }

        return arr;
    }
    /// <summary>
    /// Splits a line by the delimiter, respecting simple double-quoted fields.
    /// </summary>
    private static List<string> SplitDelimited(string line, char delim)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;

        foreach (var ch in line)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (ch == delim && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }
        result.Add(current.ToString());
        return result;
    }
    /// <summary>
    /// Converts a string to a JSON scalar (number, bool, or string) when possible.
    /// </summary>
    private static JsonNode CoerceScalar(string value)
    {
        if (long.TryParse(value, out var l)) return JsonValue.Create(l)!;
        if (double.TryParse(value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var d))
            return JsonValue.Create(d)!;
        if (value.Equals("true", StringComparison.OrdinalIgnoreCase)) return JsonValue.Create(true)!;
        if (value.Equals("false", StringComparison.OrdinalIgnoreCase)) return JsonValue.Create(false)!;
        if (value.Equals("null", StringComparison.OrdinalIgnoreCase)) return JsonValue.Create((string?)null)!;
        return JsonValue.Create(value)!;
    }
}
