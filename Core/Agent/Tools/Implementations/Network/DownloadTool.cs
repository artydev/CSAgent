using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CsAgent.Core.Agent;

public static partial class ToolDispatcher
{
    // ── download_file ────────────────────────────────────────────────────────
    //
    // Saves an HTTP response straight to a file. Without it, putting web data in a file goes through the
    // model: http_request returns the body into the conversation, then the model has to write it out again,
    // character by character, as the 'content' of write_file. That second step is slow (the model
    // produces its output one token at a time) and costs output tokens. Here the data never enters the conversation.

    private const int DownloadMaxBytes = 20 * 1024 * 1024;

    private static async Task<string> DownloadFileAsync(
        string url, string destination, string? method, JsonObject? headers, string? body,
        string? jsonPath, bool pretty, int timeoutMs)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url))
                return "Error: download_file - 'url' argument is required.";
            if (string.IsNullOrWhiteSpace(destination))
                return "Error: download_file - 'destination' argument is required.";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return $"Error: download_file - invalid URL '{url}'. Only http/https URLs are allowed.";

            var full = Path.GetFullPath(destination);
            if (!IsSafePath(full))
                return $"Error: download_file - Path '{full}' is not allowed for writing. Only files in the current working directory are permitted.";
            if (Directory.Exists(full))
                return $"Error: download_file - '{full}' is a folder; give a file name.";

            if (timeoutMs < 1) timeoutMs = 1;
            if (timeoutMs > 120_000) timeoutMs = 120_000;

            using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) CsAgent/1.0");
            using var request = new HttpRequestMessage(new HttpMethod((method ?? "GET").ToUpperInvariant()), uri);

            if (headers is not null)
            {
                foreach (var kvp in headers)
                {
                    if (kvp.Value is null) continue;
                    var value = kvp.Value.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(value) || kvp.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!request.Headers.TryAddWithoutValidation(kvp.Key, value))
                        return $"Error: download_file - invalid header '{kvp.Key}'.";
                }
            }
            if (!string.IsNullOrEmpty(body))
            {
                var contentType = headers?["Content-Type"] is JsonValue ct ? ct.GetValue<string>() : "application/json";
                request.Content = new StringContent(body, Encoding.UTF8, contentType);
            }

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
                return $"Error: download_file - HTTP {(int)response.StatusCode} {response.ReasonPhrase} for '{url}'. Nothing was written.";

            if (response.Content.Headers.ContentLength is long declared && declared > DownloadMaxBytes)
                return $"Error: download_file - the response is {declared / (1024 * 1024)} MB, the limit is {DownloadMaxBytes / (1024 * 1024)} MB. Nothing was written.";

            // Read it all first (with the limit), write afterwards: a failure leaves no half-written file.
            byte[] data;
            await using (var stream = await response.Content.ReadAsStreamAsync())
            using (var ms = new MemoryStream())
            {
                var buffer = new byte[81920];
                int n;
                while ((n = await stream.ReadAsync(buffer)) > 0)
                {
                    if (ms.Length + n > DownloadMaxBytes)
                        return $"Error: download_file - the response is larger than {DownloadMaxBytes / (1024 * 1024)} MB. Nothing was written.";
                    ms.Write(buffer, 0, n);
                }
                data = ms.ToArray();
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "unknown type";
            bool looksJson = mediaType.Contains("json", StringComparison.OrdinalIgnoreCase);
            JsonNode? json = null;

            if (!string.IsNullOrWhiteSpace(jsonPath) || pretty)
            {
                try { json = JsonNode.Parse(data); }
                catch (JsonException ex) { return $"Error: download_file - the response is not valid JSON ({ex.Message}). Nothing was written."; }
                if (json is null)
                    return "Error: download_file - the response holds no JSON value. Nothing was written.";

                if (!string.IsNullOrWhiteSpace(jsonPath))
                {
                    var part = QueryJson(json, jsonPath!);
                    if (part is null)
                        return $"Error: download_file - json_path '{jsonPath}' not found in the response. {DescribeJson(json)} Nothing was written.";
                    json = part.DeepClone();
                }
                data = new UTF8Encoding(false).GetBytes(json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            else if (looksJson)
            {
                try { json = JsonNode.Parse(data); } catch { json = null; }
            }

            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(full, data);

            var sb = new StringBuilder();
            sb.Append($"OK: saved {data.Length} bytes to '{full}' (HTTP {(int)response.StatusCode}, {mediaType}).");
            if (json is not null) sb.Append(' ').Append(DescribeJson(json));
            sb.Append(" The content was not returned to the conversation; use read_json or read_file only if you need to look at it.");
            return sb.ToString();
        }
        catch (TaskCanceledException)
        {
            return $"Error: download_file - request to '{url}' timed out after {timeoutMs} ms. Nothing was written.";
        }
        catch (HttpRequestException ex)
        {
            return $"Error: download_file - {ex.Message}";
        }
        catch (Exception ex) { return $"Error: download_file — {ex.Message}"; }
    }

    // A short description of a JSON value: enough to check the download, never the data itself.
    internal static string DescribeJson(JsonNode node)
    {
        const int maxKeys = 12;
        static string Kind(JsonNode? n) => n switch
        {
            JsonArray a => $"array of {a.Count}",
            JsonObject => "object",
            null => "null",
            _ => "value"
        };
        switch (node)
        {
            case JsonArray arr:
                var first = arr.Count > 0 ? arr[0] as JsonObject : null;
                return first is null
                    ? $"JSON: array of {arr.Count} item(s)."
                    : $"JSON: array of {arr.Count} object(s); keys of the first: {string.Join(", ", first.Select(k => k.Key).Take(maxKeys))}{(first.Count > maxKeys ? ", ..." : "")}.";
            case JsonObject obj:
                var keys = obj.Take(maxKeys).Select(k => $"{k.Key} ({Kind(k.Value)})");
                return $"JSON: object with keys {string.Join(", ", keys)}{(obj.Count > maxKeys ? ", ..." : "")}.";
            default:
                return "JSON: a single value.";
        }
    }
}
