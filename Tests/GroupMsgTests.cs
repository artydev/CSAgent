using CsAgent.Core.Agent;

namespace CsAgent.Tests;

static partial class Tests
{
    // ═════════════════════════ group_msg_by_sender ═════════════════════════
    static async Task GroupMsgBySender()
    {
        Group("Outlook .msg / group_msg_by_sender tool");

        static byte[] Msg(string sender, string email)
        {
            var f = new MsgTestFile();
            f.Root.Str(0x0037, "Subject").Str(0x0C1A, sender).Str(0x0C1E, "SMTP").Str(0x0C1F, email).Str(0x1000, "Body");
            return f.Build();
        }

        Func<Func<string, Task<string?>>, Func<Task<string?>>> InWork = body => async () =>
        {
            var prev = Directory.GetCurrentDirectory();
            var dir = Tmp();
            Directory.SetCurrentDirectory(dir);
            try { return await body(dir); }
            finally { Directory.SetCurrentDirectory(prev); }
        };

        static string[] Names(string dir) => Directory.GetFiles(dir).Select(Path.GetFileName).OfType<string>().OrderBy(x => x, StringComparer.Ordinal).ToArray();

        await T("SafeAsciiName: accents removed, spaces and special characters become '_', nothing invalid is left", async () =>
        {
            Assert(ToolDispatcher.SafeAsciiName("Jérôme Dupont", "x", 100) == "Jerome_Dupont", "accents");
            Assert(ToolDispatcher.SafeAsciiName("Été 2026 — bilan (final)!", "x", 100) == "Ete_2026_bilan_final", ToolDispatcher.SafeAsciiName("Été 2026 — bilan (final)!", "x", 100));
            Assert(ToolDispatcher.SafeAsciiName("Straße Œuvre Łódź", "x", 100) == "Strasse_OEuvre_Lodz", ToolDispatcher.SafeAsciiName("Straße Œuvre Łódź", "x", 100));
            Assert(ToolDispatcher.SafeAsciiName("a/b\\c:d*e?f\"g<h>i|j", "x", 100) == "a_b_c_d_e_f_g_h_i_j", "invalid characters");
            Assert(ToolDispatcher.SafeAsciiName("Jero\u0302me", "x", 100) == "Jerome", "decomposed accent");
            Assert(ToolDispatcher.SafeAsciiName("   ", "UnknownSender", 100) == "UnknownSender", "blank");
            Assert(ToolDispatcher.SafeAsciiName("李雷", "UnknownSender", 100) == "UnknownSender", "non-Latin only");
            Assert(ToolDispatcher.SafeAsciiName("CON", "x", 100) == "_CON", "reserved name");
            Assert(ToolDispatcher.SafeAsciiName(new string('a', 300), "x", 100).Length == 100, "length");
            await Task.CompletedTask; return "ok";
        });

        await T("group_msg_by_sender is destructive (asks for confirmation)", async () =>
        {
            Assert(ToolDispatcher.IsDestructive("group_msg_by_sender"), "classification");
            await Task.CompletedTask; return "ok";
        });

        await T("files are renamed to ASCII, then moved into one ASCII folder per sender", InWork(async dir =>
        {
            File.WriteAllBytes(Path.Combine(dir, "Rapport trimestriel é.msg"), Msg("Bob Martin", "bob@example.com"));
            File.WriteAllBytes(Path.Combine(dir, "Note de service (v2).msg"), Msg("Éloïse Garcia", "eloise@example.com"));
            File.WriteAllBytes(Path.Combine(dir, "x.msg"), Msg("Bob Martin", "bob@example.com"));
            var r = await ToolDispatcher.DispatchAsync("group_msg_by_sender", "{}", false);
            Assert(r.Contains("3 file(s), 2 renamed, 3 moved into 2 sender folder(s), 0 error(s)"), r);
            Assert(Names(dir).Length == 0, "no .msg left at the root: " + string.Join(",", Names(dir)));
            Assert(Names(Path.Combine(dir, "Bob_Martin")).SequenceEqual(new[] { "Rapport_trimestriel_e.msg", "x.msg" }), string.Join(",", Names(Path.Combine(dir, "Bob_Martin"))));
            Assert(Names(Path.Combine(dir, "Eloise_Garcia")).SequenceEqual(new[] { "Note_de_service_v2.msg" }), string.Join(",", Names(Path.Combine(dir, "Eloise_Garcia"))));
            return "ok";
        }));

        await T("names that become identical are kept apart; an unreadable file is renamed, left in place and reported", InWork(async dir =>
        {
            File.WriteAllBytes(Path.Combine(dir, "a b.msg"), Msg("Bob Martin", "bob@example.com"));
            File.WriteAllBytes(Path.Combine(dir, "a_b.msg"), Msg("Bob Martin", "bob@example.com"));
            File.WriteAllText(Path.Combine(dir, "cassé é.msg"), "this is not a message");
            var r = await ToolDispatcher.DispatchAsync("group_msg_by_sender", "{}", false);
            Assert(r.Contains("3 file(s)") && r.Contains("1 error(s)") && r.Contains("casse_e.msg is not a readable .msg"), r);
            var bob = Names(Path.Combine(dir, "Bob_Martin"));
            Assert(bob.Length == 2 && bob.Contains("a_b.msg") && bob.Contains("a_b (1).msg"), string.Join(",", bob));
            Assert(Names(dir).SequenceEqual(new[] { "casse_e.msg" }), "root: " + string.Join(",", Names(dir)));
            return "ok";
        }));

        await T("a file already in a sender folder is never overwritten", InWork(async dir =>
        {
            Directory.CreateDirectory(Path.Combine(dir, "Bob_Martin"));
            File.WriteAllText(Path.Combine(dir, "Bob_Martin", "x.msg"), "old");
            File.WriteAllBytes(Path.Combine(dir, "x.msg"), Msg("Bob Martin", "bob@example.com"));
            await ToolDispatcher.DispatchAsync("group_msg_by_sender", "{}", false);
            Assert(File.ReadAllText(Path.Combine(dir, "Bob_Martin", "x.msg")) == "old", "old file kept");
            Assert(File.Exists(Path.Combine(dir, "Bob_Martin", "x (1).msg")), "new one beside it");
            return "ok";
        }));

        await T("rename=false keeps the file names; group_by=email separates two people with the same name", InWork(async dir =>
        {
            File.WriteAllBytes(Path.Combine(dir, "é 1.msg"), Msg("Bob Martin", "bob@a.com"));
            File.WriteAllBytes(Path.Combine(dir, "é 2.msg"), Msg("Bob Martin", "bob@b.com"));
            var r = await ToolDispatcher.DispatchAsync("group_msg_by_sender", "{\"rename\":false,\"group_by\":\"email\"}", false);
            Assert(r.Contains("0 renamed") && r.Contains("2 sender folder(s)"), r);
            Assert(File.Exists(Path.Combine(dir, "bob_a.com", "é 1.msg")) && File.Exists(Path.Combine(dir, "bob_b.com", "é 2.msg")), r);
            return "ok";
        }));

        await T("a folder outside the working directory, a missing folder and an empty folder are refused or reported", InWork(async dir =>
        {
            var outside = await ToolDispatcher.DispatchAsync("group_msg_by_sender", "{\"directory\":\"" + Path.GetTempPath().Replace("\\", "\\\\") + "\"}", false);
            Assert(outside.StartsWith("Error: group_msg_by_sender") && outside.Contains("not allowed"), outside);
            var missing = await ToolDispatcher.DispatchAsync("group_msg_by_sender", "{\"directory\":\"nope\"}", false);
            Assert(missing.Contains("not found"), missing);
            var empty = await ToolDispatcher.DispatchAsync("group_msg_by_sender", "{}", false);
            Assert(empty.StartsWith("No .msg file"), empty);
            var bad = await ToolDispatcher.DispatchAsync("group_msg_by_sender", "{\"group_by\":\"subject\"}", false);
            Assert(bad.Contains("group_by must be"), bad);
            return "ok";
        }));
    }
}
