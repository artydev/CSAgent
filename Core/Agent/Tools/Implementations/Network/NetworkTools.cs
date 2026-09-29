using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace CsAgent.Core.Agent;

public static partial class ToolDispatcher
{
    private static async Task<string> HttpRequestAsync(
        string url,
        string method,
        JsonObject? headers,
        string? body,
        int timeoutMs)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url))
                return "Error: http_request - 'url' argument is required.";

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return $"Error: http_request - invalid URL '{url}'. Only http/https URLs are allowed.";

            if (timeoutMs < 1) timeoutMs = 1;
            if (timeoutMs > 120_000) timeoutMs = 120_000;

            using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };

            using var request = new HttpRequestMessage(new HttpMethod(method.ToUpperInvariant()), uri);

            if (headers is not null)
            {
                foreach (var kvp in headers)
                {
                    if (kvp.Value is null) continue;
                    var value = kvp.Value.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(value)) continue;

                    // Content headers must be set on the content, not the request.
                    if (kvp.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!request.Headers.TryAddWithoutValidation(kvp.Key, value))
                        return $"Error: http_request - invalid header '{kvp.Key}'.";
                }
            }

            if (!string.IsNullOrEmpty(body))
            {
                var contentType = "application/json";
                if (headers is not null && headers["Content-Type"] is JsonValue ct)
                    contentType = ct.GetValue<string>();

                request.Content = new StringContent(body, Encoding.UTF8, contentType);
            }

            using var response = await client.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();

            var sb = new StringBuilder();
            sb.AppendLine($"Status: {(int)response.StatusCode} {response.ReasonPhrase}");
            sb.AppendLine($"Headers:");
            foreach (var h in response.Headers)
                sb.AppendLine($"  {h.Key}: {string.Join(", ", h.Value)}");
            sb.AppendLine($"Body:");
            sb.Append(responseBody);

            return sb.ToString().TrimEnd();
        }
        catch (TaskCanceledException)
        {
            return $"Error: http_request - request to '{url}' timed out after {timeoutMs} ms.";
        }
        catch (HttpRequestException ex)
        {
            return $"Error: http_request - {ex.Message}";
        }
        catch (Exception ex) { return $"Error: http_request — {ex.Message}"; }
    }
    private static async Task<string> WebSearchAsync(string query, int maxResults)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(query))
                return "Error: web_search - 'query' argument is required.";

            if (maxResults < 1) maxResults = 1;
            if (maxResults > 10) maxResults = 10;

            // DuckDuckGo Instant Answer API — free, no API key required.
            var url = "https://api.duckduckgo.com/?q=" + Uri.EscapeDataString(query) +
                      "&format=json&no_html=1&skip_disambig=1";

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CsAgent/1.0");

            var json = await client.GetStringAsync(url);
            var root = JsonNode.Parse(json);

            var sb = new StringBuilder();
            var count = 0;

            // Abstract answer (if present) is the most relevant result.
            var abstractText = root?["Abstract"]?.GetValue<string>();
            var abstractUrl = root?["AbstractURL"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(abstractText))
            {
                sb.AppendLine($"[Abstract] {abstractText}");
                if (!string.IsNullOrWhiteSpace(abstractUrl))
                    sb.AppendLine($"  URL: {abstractUrl}");
                sb.AppendLine();
                count++;
            }

            // Related topics.
            if (root?["RelatedTopics"] is JsonArray topics)
            {
                foreach (var topic in topics)
                {
                    if (count >= maxResults) break;

                    if (topic is JsonObject obj)
                    {
                        var text = obj["Text"]?.GetValue<string>();
                        var firstUrl = obj["FirstURL"]?.GetValue<string>();
                        if (string.IsNullOrWhiteSpace(text)) continue;

                        sb.AppendLine($"{count + 1}. {text}");
                        if (!string.IsNullOrWhiteSpace(firstUrl))
                            sb.AppendLine($"   URL: {firstUrl}");
                        sb.AppendLine();
                        count++;
                    }
                    else if (topic is JsonObject nested && nested["Topics"] is JsonArray subTopics)
                    {
                        foreach (var sub in subTopics)
                        {
                            if (count >= maxResults) break;
                            if (sub is not JsonObject subObj) continue;

                            var text = subObj["Text"]?.GetValue<string>();
                            var firstUrl = subObj["FirstURL"]?.GetValue<string>();
                            if (string.IsNullOrWhiteSpace(text)) continue;

                            sb.AppendLine($"{count + 1}. {text}");
                            if (!string.IsNullOrWhiteSpace(firstUrl))
                                sb.AppendLine($"   URL: {firstUrl}");
                            sb.AppendLine();
                            count++;
                        }
                    }
                }
            }

            if (count == 0)
                return $"No results found for '{query}'.";

            return sb.ToString().TrimEnd();
        }
        catch (TaskCanceledException)
        {
            return $"Error: web_search - request timed out for query '{query}'.";
        }
        catch (HttpRequestException ex)
        {
            return $"Error: web_search - {ex.Message}";
        }
        catch (Exception ex) { return $"Error: web_search — {ex.Message}"; }
    }
    private static async Task<string> FetchUrlAsync(string url, int maxChars)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url))
                return "Error: fetch_url - 'url' argument is required.";

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return $"Error: fetch_url - invalid URL '{url}'. Only http/https URLs are allowed.";

            if (maxChars < 1) maxChars = 1;
            if (maxChars > 100_000) maxChars = 100_000;

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) CsAgent/1.0");

            using var response = await client.GetAsync(uri);
            if (!response.IsSuccessStatusCode)
                return $"Error: fetch_url - HTTP {(int)response.StatusCode} {response.ReasonPhrase} for '{url}'.";

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            var html = await response.Content.ReadAsStringAsync();

            // If it's not HTML (e.g. JSON, plain text), return it directly.
            if (!contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                var trimmed = html.Trim();
                if (trimmed.Length > maxChars)
                    trimmed = trimmed[..maxChars] + "\n... (truncated)";
                return $"URL: {url}\nContent-Type: {contentType}\n\n{trimmed}";
            }

            // Extract the <title> for context.
            var title = "";
            var titleMatch = System.Text.RegularExpressions.Regex.Match(
                html, "<title[^>]*>(.*?)</title>", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
            if (titleMatch.Success)
                title = System.Net.WebUtility.HtmlDecode(titleMatch.Groups[1].Value).Trim();

            // Strip scripts/styles and tags, then collapse whitespace.
            var text = System.Text.RegularExpressions.Regex.Replace(
                html, "<script[^>]*>.*?</script>", " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
            text = System.Text.RegularExpressions.Regex.Replace(
                text, "<style[^>]*>.*?</style>", " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
            text = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", " ");
            text = System.Net.WebUtility.HtmlDecode(text);
            text = System.Text.RegularExpressions.Regex.Replace(text, @"[ \t]+", " ");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\n\s*\n+", "\n\n");
            text = text.Trim();

            if (text.Length > maxChars)
                text = text[..maxChars] + "\n... (truncated)";

            var sb = new StringBuilder();
            sb.AppendLine($"URL: {url}");
            if (!string.IsNullOrWhiteSpace(title))
                sb.AppendLine($"Title: {title}");
            sb.AppendLine();
            sb.Append(text);

            return sb.ToString().TrimEnd();
        }
        catch (TaskCanceledException)
        {
            return $"Error: fetch_url - request to '{url}' timed out.";
        }
        catch (HttpRequestException ex)
        {
            return $"Error: fetch_url - {ex.Message}";
        }
        catch (Exception ex) { return $"Error: fetch_url — {ex.Message}"; }
    }

    // ── run_terminal / close_terminal ───────────────────────────────────────

    // Persistent interactive shell sessions keyed by session id.
}
