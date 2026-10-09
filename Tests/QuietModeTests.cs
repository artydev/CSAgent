using CsAgent.Core.Abstractions;
using CsAgent.Presentation.Tui;
using CsAgent.Shared;

namespace CsAgent.Tests;

static partial class Tests
{
    // ═════════════════════════ --quiet: only the assistant's messages in the CLI ═════════════════════════
    static async Task QuietMode()
    {
        Group("CLI / --quiet");

        // Runs a scripted session through a ConsoleObserver and returns what was printed.
        static async Task<(string Out, string Err, bool? Confirmed)> Run(bool quiet, string stdin = "y\n", bool confirm = false)
        {
            var oldOut = Console.Out; var oldErr = Console.Error; var oldIn = Console.In;
            var o = new StringWriter(); var e = new StringWriter();
            Console.SetOut(o); Console.SetError(e); Console.SetIn(new StringReader(stdin));
            bool? confirmed = null;
            try
            {
                var obs = new ConsoleObserver(quiet);
                await obs.OnStep(1, 30);
                await obs.OnThought("I will list the folder.");
                await obs.OnToolCall("list_dir", "{\"path\":\"mails/Jacinta\"}");
                await obs.OnToolResult("[FILE] a.msg  (40 KB)", false);
                await obs.OnStep(2, 30);
                await obs.OnToolCall("read_file", "{\"path\":\"missing.txt\"}");
                await obs.OnToolResult("Error: not found 'missing.txt'\nsecond line", true);
                if (confirm)
                {
                    await obs.OnToolCall("write_file", "{\"path\":\"out.txt\",\"content\":\"hello\"}");
                    confirmed = await obs.OnConfirm("write_file");
                }
                await obs.OnThought("Done: one .msg file.");
                await obs.OnDone("Task complete.");
            }
            finally { Console.SetOut(oldOut); Console.SetError(oldErr); Console.SetIn(oldIn); }
            return (o.ToString(), e.ToString(), confirmed);
        }

        await T("--quiet / -q are parsed; default is off; the value of other options is not confused with it", async () =>
        {
            Assert(!ArgumentParser.Parse(Array.Empty<string>()).Quiet, "default must be off");
            Assert(ArgumentParser.Parse(new[] { "--quiet" }).Quiet, "--quiet");
            Assert(ArgumentParser.Parse(new[] { "-q" }).Quiet, "-q");
            Assert(ArgumentParser.Parse(new[] { "-q", "--model", "x", "mem1" }).MemoryFile == "mem1", "-q must not become the memory file");
            await Task.CompletedTask;
            return "ok";
        });

        await T("normal mode is unchanged: steps, tool calls and results are all shown", async () =>
        {
            var (o, _, _) = await Run(quiet: false);
            Assert(o.Contains("Step 1/30") && o.Contains("TOOL CALL: list_dir") && o.Contains("RESULT") && o.Contains("ASSISTANT"), o);
            return "ok";
        });

        await T("quiet mode shows only the assistant's messages and the final success line", async () =>
        {
            var (o, _, _) = await Run(quiet: true);
            Assert(o.Contains("ASSISTANT") && o.Contains("I will list the folder.") && o.Contains("Done: one .msg file."), o);
            Assert(!o.Contains("RESULT") && !o.Contains("TOOL CALL") && !o.Contains("Step 1/30") && !o.Contains("a.msg  (40 KB)"), o);
            Assert(o.Contains("Task complete."), "final line");
            return "ok";
        });

        await T("quiet mode still reports a failed tool call (one line, no raw dump)", async () =>
        {
            var (o, _, _) = await Run(quiet: true);
            Assert(o.Contains("read_file failed: Error: not found 'missing.txt'"), o);
            Assert(!o.Contains("second line"), "only the first line of the error");
            return "ok";
        });

        await T("quiet mode still shows WHAT is being confirmed, and honours the answer", async () =>
        {
            var no = await Run(quiet: true, stdin: "n\n", confirm: true);
            Assert(no.Confirmed == false, "answer n must refuse");
            Assert(no.Out.Contains("TOOL CALL: write_file") && no.Out.Contains("out.txt") && no.Out.Contains("Allow destructive action 'write_file'?"), no.Out);
            var yes = await Run(quiet: true, stdin: "y\n", confirm: true);
            Assert(yes.Confirmed == true, "answer y must allow");
            // The earlier, harmless calls stay hidden even when a confirmation shows its own call.
            Assert(!yes.Out.Contains("list_dir"), yes.Out);
            return "ok";
        });

        // ── the same filter for the web UIs (QuietObserver wraps the SSE observer) ──────────
        // The inner observer records, in order, what it is given.
        static (IAgentObserver Obs, List<string> Seen) Recorder(bool confirmAnswer = true)
        {
            var seen = new List<string>();
            return (new Recording(seen, confirmAnswer), seen);
        }

        await T("web --quiet: steps, tool calls and successful results are held back; messages, warnings, errors and the end pass", async () =>
        {
            var (inner, seen) = Recorder();
            var q = new QuietObserver(inner);
            await q.OnStep(1, 30);
            await q.OnThought("I will list the folder.");
            await q.OnToolCall("list_dir", "{\"path\":\".\"}");
            await q.OnToolResult("[FILE] a.msg", false);
            await q.OnWarning("Session summary loaded");
            await q.OnDanger("blocked command");
            await q.OnError("API 500");
            await q.OnThought("Done.");
            await q.OnDone("Task complete.");
            Assert(string.Join("|", seen) == "thought:I will list the folder.|warning:Session summary loaded|danger:blocked command|error:API 500|thought:Done.|done:Task complete.",
                string.Join("|", seen));
            return "ok";
        });

        await T("web --quiet: a failed tool call is still reported, as one warning line naming the tool", async () =>
        {
            var (inner, seen) = Recorder();
            var q = new QuietObserver(inner);
            await q.OnToolCall("read_file", "{\"path\":\"missing.txt\"}");
            await q.OnToolResult("Error: not found 'missing.txt'\nsecond line", true);
            Assert(seen.Count == 1 && seen[0] == "warning:read_file failed: Error: not found 'missing.txt'", string.Join("|", seen));
            await q.OnToolResult(new string('x', 500), true);
            Assert(seen[1].Length < 260 && seen[1].EndsWith("..."), "a long error must be cut: " + seen[1].Length);
            return "ok";
        });

        await T("web --quiet: a confirmation request is preceded by the call it is about, and the answer is passed back", async () =>
        {
            foreach (var answer in new[] { true, false })
            {
                var (inner, seen) = Recorder(answer);
                var q = new QuietObserver(inner);
                await q.OnToolCall("list_dir", "{\"path\":\".\"}");
                await q.OnToolResult("ok", false);
                await q.OnToolCall("write_file", "{\"path\":\"out.txt\"}");
                var got = await q.OnConfirm("write_file");
                Assert(got == answer, "the user's answer must come back");
                Assert(string.Join("|", seen) == "call:write_file {\"path\":\"out.txt\"}|confirm:write_file", string.Join("|", seen));
            }
            return "ok";
        });

        await T("web --quiet: a confirmation for a tool whose call was not seen shows no other call", async () =>
        {
            var (inner, seen) = Recorder();
            var q = new QuietObserver(inner);
            await q.OnToolCall("list_dir", "{}");
            await q.OnConfirm("write_file");
            Assert(string.Join("|", seen) == "confirm:write_file", string.Join("|", seen));
            return "ok";
        });

        await T("web without --quiet: Wrap returns the observer itself, nothing is filtered (and --api never wraps)", async () =>
        {
            var (inner, seen) = Recorder();
            Assert(ReferenceEquals(QuietObserver.Wrap(inner, quiet: false), inner), "must be the same object");
            Assert(QuietObserver.Wrap(inner, quiet: true) is QuietObserver, "quiet must wrap");
            var plain = QuietObserver.Wrap(inner, quiet: false);
            await plain.OnStep(1, 5); await plain.OnToolCall("sh", "{}"); await plain.OnToolResult("out", false);
            Assert(string.Join("|", seen) == "step:1/5|call:sh {}|result:out", string.Join("|", seen));
            return "ok";
        });
    }

    sealed class Recording(List<string> seen, bool confirmAnswer) : IAgentObserver
    {
        public Task OnStep(int n, int m) { seen.Add($"step:{n}/{m}"); return Task.CompletedTask; }
        public Task OnThought(string t) { seen.Add("thought:" + t); return Task.CompletedTask; }
        public Task OnToolCall(string n, string a) { seen.Add($"call:{n} {a}"); return Task.CompletedTask; }
        public Task OnToolResult(string r, bool e) { seen.Add((e ? "error-result:" : "result:") + r); return Task.CompletedTask; }
        public Task OnDone(string m) { seen.Add("done:" + m); return Task.CompletedTask; }
        public Task OnError(string m) { seen.Add("error:" + m); return Task.CompletedTask; }
        public Task OnWarning(string m) { seen.Add("warning:" + m); return Task.CompletedTask; }
        public Task OnDanger(string m) { seen.Add("danger:" + m); return Task.CompletedTask; }
        public Task<bool> OnConfirm(string t) { seen.Add("confirm:" + t); return Task.FromResult(confirmAnswer); }
    }
}
