using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using CsAgent.Core.Agent;
using CsAgent.Core.Memory;
using CsAgent.Services;
using CsAgent.Shared;

namespace CsAgent.Tests;

/// <summary>A tiny HTTP server for GET requests: answers every path with what <see cref="Answer"/> returns.</summary>
sealed class MockWeb : IDisposable
{
    readonly TcpListener _l = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource _cts = new();
    public string Url { get; }
    public int Hits;
    public Func<string, (int Status, string ContentType, byte[] Body, long? DeclaredLength)> Answer { get; set; } =
        _ => (200, "text/plain", Array.Empty<byte>(), null);

    public MockWeb()
    {
        _l.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)_l.LocalEndpoint).Port}";
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await _l.AcceptTcpClientAsync(_cts.Token); } catch { return; }
                _ = Task.Run(() => Serve(c));
            }
        });
    }

    async Task Serve(TcpClient c)
    {
        using (c)
        {
            try
            {
                var s = c.GetStream();
                var (header, _) = await MockLlm.ReadRequestAsync(s);
                Interlocked.Increment(ref Hits);
                var path = header.Split(' ')[1];
                var (status, type, body, declared) = Answer(path);
                var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: {type}\r\nContent-Length: {declared ?? body.Length}\r\nConnection: close\r\n\r\n");
                await s.WriteAsync(head);
                await s.WriteAsync(body);
                await s.FlushAsync();
            }
            catch { }
        }
    }

    public void Dispose() { _cts.Cancel(); try { _l.Stop(); } catch { } }
}

static partial class Tests
{
    // ═════════════════════════ download_file: web data straight to a file ═════════════════════════
    static async Task DownloadGroup()
    {
        Group("download_file: the response goes to the file, not through the model");

        // A response shaped like https://dummyjson.com/products?limit=30 (about 40 KB).
        static byte[] Products(int n)
        {
            var items = new JsonArray();
            for (int i = 1; i <= n; i++)
                items.Add(new JsonObject { ["id"] = i, ["title"] = "Product " + i, ["description"] = new string('d', 1200), ["price"] = 9.99 + i, ["tags"] = new JsonArray("a", "b") });
            return Encoding.UTF8.GetBytes(new JsonObject { ["products"] = items, ["total"] = 194, ["skip"] = 0, ["limit"] = n }.ToJsonString());
        }

        async Task<string> InWork(Func<string, Task<string>> body)
        {
            var prev = Directory.GetCurrentDirectory();
            var dir = Tmp();
            Directory.SetCurrentDirectory(dir);
            try { return await body(dir); }
            finally { Directory.SetCurrentDirectory(prev); }
        }
        static Task<string> Call(string args) => ToolDispatcher.DispatchAsync("download_file", args, false);

        await T("the bytes are saved as received, and only a short summary comes back", async () =>
        {
            using var web = new MockWeb();
            var payload = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
            web.Answer = _ => (200, "application/octet-stream", payload, null);
            return await InWork(async dir =>
            {
                var r = await Call($"{{\"url\":\"{web.Url}/x.bin\",\"destination\":\"out/x.bin\"}}");
                Assert(r.StartsWith("OK: saved 256 bytes"), r);
                Assert(File.ReadAllBytes(Path.Combine(dir, "out", "x.bin")).SequenceEqual(payload), "bytes differ");
                Assert(r.Length < 500 && r.Contains("not returned to the conversation"), r);
                return r;
            });
        });

        await T("json_path keeps only the useful part, indented; the result describes its shape, not its data", async () =>
        {
            using var web = new MockWeb();
            web.Answer = _ => (200, "application/json", Products(30), null);
            return await InWork(async dir =>
            {
                var r = await Call($"{{\"url\":\"{web.Url}/products?limit=30\",\"destination\":\"products.json\",\"json_path\":\"products\"}}");
                Assert(r.StartsWith("OK: saved"), r);
                var saved = File.ReadAllText(Path.Combine(dir, "products.json"));
                var arr = JsonNode.Parse(saved)!.AsArray();
                Assert(arr.Count == 30 && arr[29]!["title"]!.GetValue<string>() == "Product 30", "content");
                Assert(saved.Contains("\n  "), "should be indented");
                Assert(!saved.Contains("\"total\""), "the wrapper must be left out");
                Assert(r.Contains("array of 30 object(s)") && r.Contains("id, title, description"), r);
                Assert(!r.Contains("Product 1") && r.Length < 600, "no data in the result: " + r);
                return $"{saved.Length} chars saved, {r.Length} returned";
            });
        });

        await T("without json_path the JSON is saved untouched and described", async () =>
        {
            using var web = new MockWeb();
            var body = Products(3);
            web.Answer = _ => (200, "application/json; charset=utf-8", body, null);
            return await InWork(async dir =>
            {
                var r = await Call($"{{\"url\":\"{web.Url}/p\",\"destination\":\"p.json\"}}");
                Assert(File.ReadAllBytes(Path.Combine(dir, "p.json")).SequenceEqual(body), "must be byte-identical");
                Assert(r.Contains("object with keys products (array of 3), total (value)"), r);
                var pretty = await Call($"{{\"url\":\"{web.Url}/p\",\"destination\":\"p2.json\",\"pretty\":true}}");
                Assert(File.ReadAllText(Path.Combine(dir, "p2.json")).Contains("\n  \"products\""), "pretty");
                return "ok";
            });
        });

        await T("errors write nothing and leave an existing file alone: wrong json_path, not JSON, HTTP error, too large", async () =>
        {
            using var web = new MockWeb();
            web.Answer = p => p switch
            {
                "/json" => (200, "application/json", Products(2), null),
                "/text" => (200, "text/html", Encoding.UTF8.GetBytes("<html>no</html>"), null),
                "/big" => (200, "application/octet-stream", Array.Empty<byte>(), 30L * 1024 * 1024),
                _ => (404, "text/plain", Encoding.UTF8.GetBytes("nope"), null)
            };
            return await InWork(async dir =>
            {
                File.WriteAllText("keep.json", "ORIGINAL");
                var a = await Call($"{{\"url\":\"{web.Url}/json\",\"destination\":\"keep.json\",\"json_path\":\"items\"}}");
                Assert(a.StartsWith("Error: download_file") && a.Contains("'items' not found") && a.Contains("products (array of 2)") && a.Contains("Nothing was written"), a);
                var b = await Call($"{{\"url\":\"{web.Url}/text\",\"destination\":\"keep.json\",\"json_path\":\"products\"}}");
                Assert(b.Contains("not valid JSON") && b.Contains("Nothing was written"), b);
                var c = await Call($"{{\"url\":\"{web.Url}/missing\",\"destination\":\"keep.json\"}}");
                Assert(c.Contains("HTTP 404") && c.Contains("Nothing was written"), c);
                var d = await Call($"{{\"url\":\"{web.Url}/big\",\"destination\":\"keep.json\"}}");
                Assert(d.Contains("limit is 20 MB") && d.Contains("Nothing was written"), d);
                Assert(File.ReadAllText("keep.json") == "ORIGINAL", "the existing file must not change");
                return "ok";
            });
        });

        await T("bad requests are refused before any network call: URL, path outside the folder, folder as destination", async () =>
        {
            using var web = new MockWeb();
            return await InWork(async dir =>
            {
                Assert((await Call("{\"url\":\"ftp://x/y\",\"destination\":\"a.json\"}")).Contains("Only http/https"), "scheme");
                Assert((await Call("{\"url\":\"not a url\",\"destination\":\"a.json\"}")).Contains("invalid URL"), "url");
                var outside = Path.Combine(Path.GetTempPath(), "csagent_outside_" + Guid.NewGuid().ToString("N") + ".json");
                var r = await Call($"{{\"url\":\"{web.Url}/x\",\"destination\":{JsonValue.Create(outside)!.ToJsonString()}}}");
                Assert(r.Contains("not allowed for writing") && !File.Exists(outside), r);
                Directory.CreateDirectory("folder");
                Assert((await Call($"{{\"url\":\"{web.Url}/x\",\"destination\":\"folder\"}}")).Contains("is a folder"), "folder");
                Assert(web.Hits == 0, "the server was contacted " + web.Hits + " time(s)");
                return "ok";
            });
        });

        await T("it is a web tool that asks for confirmation, and the prompt and http_request steer toward it", async () =>
        {
            Assert(ToolSelector.GroupFor("download_file") == "web", "group");
            Assert(ToolDispatcher.IsDestructive("download_file"), "confirmation");
            var http = ToolDispatcher.ToolDefinitions.First(t => t!["function"]!["name"]!.GetValue<string>() == "http_request")!["function"]!["description"]!.GetValue<string>();
            Assert(http.Contains("download_file"), "http_request should point to it");
            var prompt = CodingAgent.SystemMessage(false)["content"]!.GetValue<string>();
            Assert(prompt.Contains("must not go through you") && prompt.Contains("download_file"), "prompt rule");
            await Task.CompletedTask; return "ok";
        });

        await T("agent: one download_file call fills products.json; the model gets a short answer, never the 40 KB", async () =>
        {
            using var web = new MockWeb();
            web.Answer = _ => (200, "application/json", Products(30), null);
            return await InWork(async dir =>
            {
                int n = 0;
                using var mock = new MockLlm
                {
                    Handler = req => MockLlm.IsDistill(req) ? (200, MockLlm.Text("{}"), 0)
                        : ++n == 1 ? (200, MockLlm.ToolCall("download_file", $"{{\"url\":\"{web.Url}/products?limit=30\",\"destination\":\"products.json\",\"json_path\":\"products\"}}"), 0)
                        : (200, MockLlm.Text("done"), 0)
                };
                var msgs = new JsonArray { CodingAgent.SystemMessage(false), JsonHelpers.Message("user", "fetch 30 products from the dummyjson site and write them to products.json") };
                var obs = new SilentObserver();
                using var agent = new CodingAgent("k", mock.BaseUrl, "m", new AgentOptions(Confirm: true), obs, null,
                    new HybridMemoryManager(new SemanticMemory(), new ExactMemory()));
                await agent.RunAsync(msgs, "agent_memory.json");
                var reqs = mock.Requests.Where(r => !MockLlm.IsDistill(r)).ToList();
                Assert(reqs.Count == 2, "requests=" + reqs.Count);
                Assert(reqs[0]["tools"]!.AsArray().Any(t => t!["function"]!["name"]!.GetValue<string>() == "download_file"), "web group should be on for this request");
                var tool = reqs[1]["messages"]!.AsArray().Last(m => m!["role"]!.GetValue<string>() == "tool")!["content"]!.GetValue<string>();
                Assert(tool.StartsWith("OK: saved") && tool.Length < 600, tool);
                Assert(obs.ConfirmCount == 1, "confirmations=" + obs.ConfirmCount);
                Assert(JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "products.json")))!.AsArray().Count == 30, "file");
                return $"{tool.Length} characters back to the model";
            });
        });
    }
}
