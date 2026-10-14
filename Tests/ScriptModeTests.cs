using CsAgent.Core.Abstractions;
using CsAgent.Presentation.Tui;
using CsAgent.Shared;

namespace CsAgent.Tests;

static partial class Tests
{
    // ═════════════════════════ --prompt: output that can be piped ═════════════════════════
    static async Task ScriptMode()
    {
        Group("CLI / --prompt script mode");

        static async Task<(string Out, string Err, ScriptObserver Obs, bool? Confirmed)> Run(
            bool canAsk = false, string stdin = "", bool confirm = false, bool fail = false)
        {
            var o = new StringWriter(); var e = new StringWriter();
            var reader = new StringReader(stdin);
            var obs = new ScriptObserver(o, e, canAsk, reader.ReadLine);
            IAgentObserver io = obs;
            bool? confirmed = null;
            await obs.OnWarning("Session summary loaded: 3 note(s)");
            await obs.OnStep(1, 30);
            await obs.OnThought("I will list the folder.");
            await io.OnAssistantModel("deepseek-v4-flash");
            await obs.OnToolCall("list_dir", "{\"path\":\".\"}");
            await obs.OnToolResult("[FILE] a.txt", false);
            await obs.OnStep(2, 30);
            if (fail)
            {
                await obs.OnToolCall("read_file", "{\"path\":\"x\"}");
                await obs.OnToolResult("Error: not found 'x'\nsecond line", true);
            }
            if (confirm)
            {
                await obs.OnToolCall("write_file", "{}");
                confirmed = await obs.OnConfirm("write_file");
            }
            await obs.OnThought("Files: a.txt");
            await io.OnAssistantModel("deepseek-v4-flash");
            await obs.OnDone("Task complete.");
            await io.OnUsage(10, 5, 10, 5);
            await obs.OnWarning("Session summary updated: 1 note(s) saved to x");
            return (o.ToString(), e.ToString(), obs, confirmed);
        }

        await T("--verbose is parsed; default is off", async () =>
        {
            Assert(!ArgumentParser.Parse(new[] { "--prompt", "hi" }).Verbose, "default off");
            Assert(ArgumentParser.Parse(new[] { "--prompt", "hi", "--verbose" }).Verbose, "--verbose");
            Assert(ArgumentParser.Parse(new[] { "--verbose", "mem1" }).MemoryFile == "mem1", "not the memory file");
            await Task.CompletedTask; return "ok";
        });

        await T("stdout holds only the final answer", async () =>
        {
            var (o, e, obs, _) = await Run();
            Assert(o == "Files: a.txt" + Environment.NewLine, $"stdout was: [{o}]");
            Assert(e == "", $"stderr should be empty: [{e}]");
            Assert(!obs.HadError, "no error");
            return "ok";
        });

        await T("a failed tool call goes to stderr, never to stdout", async () =>
        {
            var (o, e, obs, _) = await Run(fail: true);
            Assert(o == "Files: a.txt" + Environment.NewLine, o);
            Assert(e.Contains("warning: read_file failed: Error: not found") && !e.Contains("second line"), e);
            Assert(!obs.HadError, "a failed tool is not a failed run");
            return "ok";
        });

        await T("an error is on stderr and sets HadError (exit code 1)", async () =>
        {
            var o = new StringWriter(); var e = new StringWriter();
            var obs = new ScriptObserver(o, e, false);
            await obs.OnStep(1, 30);
            await obs.OnThought("partial");
            await obs.OnError("Max steps reached");
            Assert(o.ToString() == "", "no stdout: " + o);
            Assert(e.ToString().StartsWith("error: Max steps reached") && obs.HadError, e.ToString());
            return "ok";
        });

        await T("memory failures are reported on stderr, routine notices are not", async () =>
        {
            var o = new StringWriter(); var e = new StringWriter();
            var obs = new ScriptObserver(o, e, false);
            await obs.OnWarning("Session summary not updated (timeout); the previous one is kept.");
            await obs.OnWarning("Session summary unchanged (nothing new).");
            await obs.OnDanger("blocked command");
            Assert(e.ToString().Contains("not updated") && !e.ToString().Contains("unchanged"), e.ToString());
            Assert(e.ToString().Contains("danger: blocked command") && o.ToString() == "", e.ToString());
            return "ok";
        });

        await T("confirmation is declined when input is redirected", async () =>
        {
            var (o, e, _, c) = await Run(canAsk: false, stdin: "y\n", confirm: true);
            Assert(c == false, "must be declined");
            Assert(e.Contains("declined: 'write_file'") && e.Contains("--yes"), e);
            Assert(o == "Files: a.txt" + Environment.NewLine, o);
            return "ok";
        });

        await T("confirmation is asked on stderr when a keyboard is there", async () =>
        {
            var (o, e, _, yes) = await Run(canAsk: true, stdin: "y\n", confirm: true);
            Assert(yes == true && e.Contains("Allow destructive action 'write_file'"), e);
            Assert(o == "Files: a.txt" + Environment.NewLine, o);
            var (_, _, _, no) = await Run(canAsk: true, stdin: "\n", confirm: true);
            Assert(no == false, "empty answer = no");
            return "ok";
        });
    }
}
