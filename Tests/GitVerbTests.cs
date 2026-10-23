using System.Diagnostics;
using CsAgent.Core.Agent;

namespace CsAgent.Tests;

static partial class Tests
{
    // ═════════════════════════════ git verbs ═════════════════════════════════════
    // git_add / git_fetch / git_pull / git_push / git_checkout: thin, safe wrappers around git
    // (fetch --prune, pull --ff-only, plain push, validated checkout), tested against real
    // local repositories with a bare origin.
    static async Task GitVerbGroup()
    {
        Group("git verbs");
        var native = ToolDispatcher.ToolDefinitions;

        // Runs a raw git command; throws when it fails (test setup only).
        static async Task Shell(string cwd, string args)
        {
            using var p = new Process { StartInfo = new ProcessStartInfo("git", args)
            { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
            p.Start();
            var so = await p.StandardOutput.ReadToEndAsync();
            var se = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            if (p.ExitCode != 0) throw new Exception($"git {args} failed ({p.ExitCode}): {so} {se}");
        }
        // Runs a raw git command and returns its output (assertions only).
        static async Task<string> Raw(string cwd, string args)
        {
            using var p = new Process { StartInfo = new ProcessStartInfo("git", args)
            { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
            p.Start();
            var so = await p.StandardOutput.ReadToEndAsync();
            var se = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            return so + se;
        }
        async Task<string> NewRepo(string dir)
        {
            await Shell(dir, "init -q --bare -b main origin.git");
            await Shell(dir, "clone -q origin.git work");
            await Shell(dir + "/work", "config user.email t@t.local");
            await Shell(dir + "/work", "config user.name Test");
            await Shell(dir + "/work", "checkout -qb main");
            File.WriteAllText(dir + "/work/README.txt", "base\n");
            await Shell(dir + "/work", "add -A");
            await Shell(dir + "/work", "commit -qm base");
            await Shell(dir + "/work", "push -qu origin main");
            return dir + "/work";
        }
        async Task<string> SecondClone(string dir, string name)
        {
            await Shell(dir, $"clone -q origin.git {name}");
            await Shell(dir + "/" + name, "config user.email t@t.local");
            await Shell(dir + "/" + name, "config user.name Test");
            return dir + "/" + name;
        }
        async Task<string> Call(string tool, string json, string cwd)
        {
            var prev = Directory.GetCurrentDirectory();
            Directory.SetCurrentDirectory(cwd);
            try { return await ToolDispatcher.DispatchAsync(tool, json, isWindows: false); }
            finally { Directory.SetCurrentDirectory(prev); }
        }

        await T("five verbs are defined, grouped under git, with the expected confirmation rules", async () =>
        {
            foreach (var n in new[] { "git_add", "git_fetch", "git_pull", "git_push", "git_checkout" })
            {
                Assert(native.Any(t => t!["function"]!["name"]!.GetValue<string>() == n), n + " not defined");
                Assert(ToolSelector.GroupFor(n) == "git", n + " not in the git group");
            }
            Assert(ToolDispatcher.IsDestructive("git_pull") && ToolDispatcher.IsDestructive("git_push") && ToolDispatcher.IsDestructive("git_checkout"), "pull/push/checkout must be destructive");
            Assert(!ToolDispatcher.IsDestructive("git_add") && !ToolDispatcher.IsDestructive("git_fetch"), "add/fetch must not be destructive");
            await Task.CompletedTask; return "ok";
        });

        await T("paths outside the working directory are refused", async () =>
        {
            var dir = Tmp();
            var work = await NewRepo(dir);
            var other = Tmp();
            var r = await Call("git_fetch", "{\"path\":\"" + work + "\"}", other);
            Assert(r.Contains("not allowed"), r);
            return "ok";
        });

        await T("git_add stages one file; a spec that looks like an option is refused", async () =>
        {
            var dir = Tmp();
            var work = await NewRepo(dir);
            File.WriteAllText(work + "/a.txt", "a");
            File.WriteAllText(work + "/b.txt", "b");
            var r = await Call("git_add", "{\"path\":\"work\",\"spec\":\"a.txt\"}", dir);
            Assert(r.StartsWith("OK"), r);
            var staged = await Raw(work, "diff --cached --name-only");
            Assert(staged.Contains("a.txt") && !staged.Contains("b.txt"), staged);
            var bad = await Call("git_add", "{\"path\":\"work\",\"spec\":\"--all\"}", dir);
            Assert(bad.Contains("is a path, not a git option"), bad);
            return "ok";
        });

        await T("git_add without a spec stages everything", async () =>
        {
            var dir = Tmp();
            var work = await NewRepo(dir);
            File.WriteAllText(work + "/a.txt", "a");
            File.WriteAllText(work + "/b.txt", "b");
            var r = await Call("git_add", "{\"path\":\"work\"}", dir);
            Assert(r.StartsWith("OK"), r);
            var staged = await Raw(work, "diff --cached --name-only");
            Assert(staged.Contains("a.txt") && staged.Contains("b.txt"), staged);
            return "ok";
        });

        await T("git_fetch brings the new remote branches in", async () =>
        {
            var dir = Tmp();
            var work = await NewRepo(dir);
            var contrib = await SecondClone(dir, "contrib");
            File.WriteAllText(contrib + "/feat.txt", "f");
            await Shell(contrib, "checkout -qb feat");
            await Shell(contrib, "add -A");
            await Shell(contrib, "commit -qm feat");
            await Shell(contrib, "push -q origin feat");
            var r = await Call("git_fetch", "{\"path\":\"work\"}", dir);
            Assert(r.StartsWith("OK"), r);
            var branches = await Raw(work, "branch -r");
            Assert(branches.Contains("feat"), branches);
            return "ok";
        });

        await T("git_pull fast-forwards the current branch", async () =>
        {
            var dir = Tmp();
            var work = await NewRepo(dir);
            var contrib = await SecondClone(dir, "contrib");
            File.WriteAllText(contrib + "/pulled.txt", "p");
            await Shell(contrib, "add -A");
            await Shell(contrib, "commit -qm pull-me");
            await Shell(contrib, "push -q origin main");
            var r = await Call("git_pull", "{\"path\":\"work\"}", dir);
            Assert(r.StartsWith("OK"), r);
            Assert(File.Exists(work + "/pulled.txt"), "file not pulled");
            return "ok";
        });

        await T("git_pull refuses a diverged history instead of merging it", async () =>
        {
            var dir = Tmp();
            var work = await NewRepo(dir);
            var contrib = await SecondClone(dir, "contrib");
            File.WriteAllText(contrib + "/remote.txt", "r");
            await Shell(contrib, "add -A");
            await Shell(contrib, "commit -qm remote");
            await Shell(contrib, "push -q origin main");
            File.WriteAllText(work + "/local.txt", "l");
            await Shell(work, "add -A");
            await Shell(work, "commit -qm local");
            var r = await Call("git_pull", "{\"path\":\"work\"}", dir);
            // git exits 128 on a fatal pull failure, whatever the locale.
            Assert(r.StartsWith("Error (exit 128)"), r);
            Assert(File.Exists(work + "/local.txt"), "local file must survive");
            var head = await Raw(work, "log --format=%s -n 1");
            Assert(head.Contains("local"), "the branch must not have moved: " + head);
            return "ok";
        });

        await T("git_push pushes the current branch to its upstream", async () =>
        {
            var dir = Tmp();
            var work = await NewRepo(dir);
            File.WriteAllText(work + "/pushed.txt", "p");
            await Shell(work, "add -A");
            await Shell(work, "commit -qm push-me");
            var r = await Call("git_push", "{\"path\":\"work\"}", dir);
            Assert(r.StartsWith("OK"), r);
            var originLog = await Raw(dir + "/origin.git", "log main --format=%s -n 1");
            Assert(originLog.Contains("push-me"), originLog);
            return "ok";
        });

        await T("git_checkout switches branches; invalid names are refused", async () =>
        {
            var dir = Tmp();
            var work = await NewRepo(dir);
            await Shell(work, "checkout -qb feature/login");
            var r = await Call("git_checkout", "{\"path\":\"work\",\"branch\":\"main\"}", dir);
            Assert(r.StartsWith("OK"), r);
            var cur = (await Raw(work, "branch --show-current")).Trim();
            Assert(cur == "main", cur);
            foreach (var bad in new[] { "-u", "a..b", "x y", "" })
            {
                var json = bad == "" ? "null" : "\"" + bad + "\"";
                var rb = await Call("git_checkout", "{\"path\":\"work\",\"branch\":" + json + "}", dir);
                Assert(rb.StartsWith("Error"), bad + " -> " + rb);
            }
            return "ok";
        });

        await T("git_checkout can check out a commit by SHA (detached HEAD)", async () =>
        {
            var dir = Tmp();
            var work = await NewRepo(dir);
            var sha = (await Raw(work, "rev-parse --short=10 HEAD")).Trim();
            var r = await Call("git_checkout", "{\"path\":\"work\",\"branch\":\"" + sha + "\"}", dir);
            Assert(r.StartsWith("OK"), r);
            // A detached HEAD reports as 'HEAD' for rev-parse --abbrev-ref, in every locale.
            var head = (await Raw(work, "rev-parse --abbrev-ref HEAD")).Trim();
            Assert(head == "HEAD", head);
            await Shell(work, "checkout -q main");
            return "ok";
        });
    }
}
