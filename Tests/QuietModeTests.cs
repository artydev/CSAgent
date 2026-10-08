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
    }
}
