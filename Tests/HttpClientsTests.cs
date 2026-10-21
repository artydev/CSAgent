using System.Net;
using CsAgent.Shared;

namespace CsAgent.Tests;

static partial class Tests
{
    // Every HttpClient of the app is made by HttpClients.Create, so they all pass the Windows credentials to a company proxy.
    static async Task HttpClientsGroup()
    {
        Group("HTTP clients: one shared factory");

        await T("the handler gives the default credentials to the proxy, and only to the proxy", () =>
        {
            using var h = HttpClients.NewHandler();
            Assert(ReferenceEquals(h.DefaultProxyCredentials, CredentialCache.DefaultCredentials), "proxy credentials");
            Assert(!h.UseDefaultCredentials, "must not send them to the servers");
            return Task.FromResult<string?>("ok");
        });

        await T("Create applies the timeout and the client still reaches a local server", async () =>
        {
            using var web = new MockWeb();
            web.Answer = _ => (200, "text/plain", System.Text.Encoding.UTF8.GetBytes("hello"), null);
            using var c = HttpClients.Create(TimeSpan.FromSeconds(7));
            Assert(c.Timeout == TimeSpan.FromSeconds(7), "timeout");
            var body = await c.GetStringAsync(web.Url + "/x");
            Assert(body == "hello", "body: " + body);
            return "ok";
        });

        await T("no source file makes an HttpClient by itself any more", () =>
        {
            var root = AppContext.BaseDirectory;
            while (root != null && !File.Exists(Path.Combine(root, "CsAgent.csproj"))) root = Path.GetDirectoryName(root);
            if (root == null) return Task.FromResult<string?>("sources not found: skipped");
            var offenders = new[] { "Core", "Services", "Shared", "Presentation", "Infrastructure" }
                .Select(d => Path.Combine(root, d)).Where(Directory.Exists)
                .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories))
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                         && !f.EndsWith("HttpClients.cs"))
                .Where(f => File.ReadAllLines(f).Any(l => l.Contains("new HttpClient(") && !l.TrimStart().StartsWith("//")))
                .Select(f => Path.GetRelativePath(root, f)).ToList();
            Assert(offenders.Count == 0, "use HttpClients.Create in: " + string.Join(", ", offenders));
            return Task.FromResult<string?>("ok");
        });
    }
}
