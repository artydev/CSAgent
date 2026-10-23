using CsAgent.Core.Agent;

namespace CsAgent.Tests;

static partial class Tests
{
    // ═════════════════════════ read_file pagination ═════════════════════════
    // read_file gained two optional arguments, start_line and end_line (1-based). Without them the
    // behavior is unchanged (whole file, 512 KB guard). With them the range is streamed with
    // File.ReadLines, so a large file is never loaded in memory.
    static async Task ReadFilePaginationGroup()
    {
        Group("read_file pagination");
        var native = ToolDispatcher.ToolDefinitions;
        var readDef = native.First(t => t!["function"]!["name"]!.GetValue<string>() == "read_file")!["function"]!;

        async Task<string> ReadIn(string json, Action<string> setup)
        {
            var prev = Directory.GetCurrentDirectory();
            var dir = Tmp();
            Directory.SetCurrentDirectory(dir);
            try
            {
                setup(dir);
                return await ToolDispatcher.DispatchAsync("read_file", json, isWindows: false);
            }
            finally { Directory.SetCurrentDirectory(prev); }
        }

        void TenLines(string dir) => File.WriteAllText(Path.Combine(dir, "f.txt"),
            string.Join("\n", Enumerable.Range(1, 10).Select(i => "line " + i)) + "\n");

        await T("schema: start_line and end_line are optional, path stays required", async () =>
        {
            var props = readDef["parameters"]!["properties"]!;
            Assert(props["start_line"] != null && props["end_line"] != null, "properties missing");
            var required = readDef["parameters"]!["required"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
            Assert(required.SequenceEqual(new[] { "path" }), string.Join(",", required));
            Assert(readDef["description"]!.ToString().Contains("start_line"), "description not updated");
            await Task.CompletedTask; return "ok";
        });

        await T("without line arguments the whole file is returned, exactly as before", async () =>
        {
            var r = await ReadIn("{\"path\":\"f.txt\"}", d => File.WriteAllText(d + "/f.txt", "alpha\nbeta\ngamma\n"));
            Assert(r == "alpha\nbeta\ngamma\n", r);
            return "ok";
        });

        await T("a window returns exactly the requested lines, with a range header", async () =>
        {
            var r = await ReadIn("{\"path\":\"f.txt\",\"start_line\":3,\"end_line\":5}", TenLines);
            Assert(r == $"Lines 3-5:{Environment.NewLine}line 3{Environment.NewLine}line 4{Environment.NewLine}line 5", r);
            return "ok";
        });

        await T("start_line only: from that line to the end of the file", async () =>
        {
            var r = await ReadIn("{\"path\":\"f.txt\",\"start_line\":9}", TenLines);
            Assert(r.StartsWith($"Lines 9-10:") && r.Contains("line 9") && r.EndsWith("line 10"), r);
            Assert(!r.Contains("line 8\n") && !r.Contains("line 8" + Environment.NewLine), "line 8 must not be there");
            return "ok";
        });

        await T("end_line only: from the first line", async () =>
        {
            var r = await ReadIn("{\"path\":\"f.txt\",\"end_line\":2}", TenLines);
            Assert(r.StartsWith($"Lines 1-2:") && r.Contains("line 1") && r.EndsWith("line 2"), r);
            return "ok";
        });

        await T("end_line before start_line is refused", async () =>
        {
            var r = await ReadIn("{\"path\":\"f.txt\",\"start_line\":4,\"end_line\":2}", TenLines);
            Assert(r.Contains("must be greater than or equal to start_line"), r);
            return "ok";
        });

        await T("start_line past the end of the file reports the total number of lines", async () =>
        {
            var r = await ReadIn("{\"path\":\"f.txt\",\"start_line\":9}", d => File.WriteAllText(d + "/f.txt", "a\nb\n"));
            Assert(r.Contains("past the end of the file (2 lines)"), r);
            return "ok";
        });

        await T("an empty file with a window says so plainly", async () =>
        {
            var r = await ReadIn("{\"path\":\"f.txt\",\"start_line\":1}", d => File.WriteAllText(d + "/f.txt", ""));
            Assert(r == "(empty file)", r);
            return "ok";
        });

        await T("a file over 512 KB is refused whole, but read as a window", async () =>
        {
            Action<string> big = d => File.WriteAllText(d + "/big.txt",
                string.Join("\n", Enumerable.Range(1, 30_000).Select(i => "line " + i + " " + new string('x', 20))) + "\n");
            var whole = await ReadIn("{\"path\":\"big.txt\"}", big);
            Assert(whole.StartsWith("Error: file too large") && whole.Contains("start_line and end_line"), whole);
            var win = await ReadIn("{\"path\":\"big.txt\",\"start_line\":1,\"end_line\":2}", big);
            Assert(win.StartsWith("Lines 1-2:"), win);
            Assert(win.Contains("line 1") && win.Contains("line 2") && !win.Contains("line 3"), win);
            return "ok";
        });

        await T("a window wider than 2,000 lines is cut, with a note", async () =>
        {
            Action<string> many = d => File.WriteAllText(d + "/many.txt",
                string.Join("\n", Enumerable.Range(1, 2_100).Select(i => "line " + i)) + "\n");
            var r = await ReadIn("{\"path\":\"many.txt\",\"start_line\":1,\"end_line\":2100}", many);
            Assert(r.Contains("truncated at 2,000 lines"), "no note: " + r.Length);
            Assert(r.Contains("line 2000") && !r.Contains("line 2001"), "cut point");
            return "ok";
        });
    }
}
