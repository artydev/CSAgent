using CsAgent.Core.Agent;

namespace CsAgent.Tests;

static partial class Tests
{
    // ═════════════════════════ Command filter: block the dangerous, not the harmless ═════════════════════════
    static async Task CommandFilterGroup()
    {
        Group("Command filter (sh, run_terminal, excel_command)");

        await T("Windows: Excel number formats are not taken for the 'format' command", async () =>
        {
            foreach (var ok in new[]
            {
                "$ws.Range(\"E2:E31\").NumberFormat = \"0.00\"",
                "$ws.Range('E2:E31').NumberFormat = '0.00'",
                "$ws.Columns(\"A\").NumberFormat = \"@\"; $ws.Range(\"B2\").NumberFormat = \"dd/mm/yyyy\"",
                "$ws.Range(\"A1:C1\").Font.Bold = $true",
                "$r.Cells.NumberFormat.ToString()",
            })
                Assert(ToolDispatcher.FindDangerousPattern(ok, true) is null, "should be allowed: " + ok);
            await Task.CompletedTask; return "ok";
        });

        await T("Windows: the dangerous ones are still blocked, wherever they stand in the command", async () =>
        {
            foreach (var (cmd, pattern) in new[]
            {
                ("format c: /q", "format "), ("FORMAT D:", "format "), ("cmd /c format e:", "format "),
                ("echo x & format c:", "format "), ("dir;format c:", "format "),
                ("reg delete HKCU\\Software\\X /f", "reg delete"), ("net user bob /add", "net user"),
                ("shutdown /s /t 0", "shutdown"), ("del /f important.txt", "del /f"),
                ("type C:\\Windows\\System32\\drivers\\etc\\hosts", "\\windows\\system32\\"),
                ("$ws.Range(\"A1\").Value = \"format c:\"", "format "),
            })
                Assert(ToolDispatcher.FindDangerousPattern(cmd, true) == pattern, $"'{cmd}' -> {ToolDispatcher.FindDangerousPattern(cmd, true) ?? "allowed"}");
            await Task.CompletedTask; return "ok";
        });

        await T("Unix: 'git add' is not taken for 'dd', the real ones are blocked", async () =>
        {
            foreach (var ok in new[] { "git add file.txt", "cd src && git add .", "git add -A && git commit -m x", "echo padded value", "ls ./addons" })
                Assert(ToolDispatcher.FindDangerousPattern(ok, false) is null, "should be allowed: " + ok);
            foreach (var (cmd, pattern) in new[]
            {
                ("dd if=/dev/zero of=disk.img", "dd "), ("ls; dd if=a of=b", "dd "), ("sudo ls", "sudo "), ("chmod 777 x", "chmod"),
                ("cat /etc/passwd", "/etc/"), ("mkfs.ext4 /dev/sda1", "mkfs"), ("reboot", "reboot"),
            })
                Assert(ToolDispatcher.FindDangerousPattern(cmd, false) == pattern, $"'{cmd}' -> {ToolDispatcher.FindDangerousPattern(cmd, false) ?? "allowed"}");
            await Task.CompletedTask; return "ok";
        });

        await T("the refusal names the blocked pattern, in all three tools, so the model can rewrite instead of guessing", async () =>
        {
            var excel = await ToolDispatcher.DispatchAsync("excel_command", "{\"command\":\"$ws.Range(\\\"A1\\\").Value = 1; format c:\"}", true);
            Assert(excel.StartsWith("Error: excel_command") && excel.Contains("blocked pattern 'format'") && excel.Contains("Rewrite the command"), excel);
            var sh = await ToolDispatcher.DispatchAsync("sh", "{\"cmd\":\"reg delete HKCU\\\\Software\\\\X /f\"}", true);
            Assert(sh.StartsWith("Error: sh") && sh.Contains("blocked pattern 'reg delete'"), sh);
            var term = await ToolDispatcher.DispatchAsync("run_terminal", "{\"cmd\":\"net user bob /add\"}", true);
            Assert(term.StartsWith("Error: run_terminal") && term.Contains("blocked pattern 'net user'"), term);
            Assert(excel.Contains("not allowed") && sh.Contains("not allowed"), "the existing wording must stay");
            return "ok";
        });
    }
}
