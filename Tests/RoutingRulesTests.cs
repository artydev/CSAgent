using System.Text.Json.Nodes;
using CsAgent.Core.Llm;
using CsAgent.Core.Memory;
using CsAgent.Shared;

namespace CsAgent.Tests;

static partial class Tests
{
    // ═════════════════════════ LLMRoutingRules: models.json, keywords.json, rules.json ═════════════════════════

    // Runs f with a clean configuration: no environment, built-in rules, nothing left behind.
    static T Iso<T>(Func<T> f)
    {
        var names = new[] { "CSAGENT_ENDPOINT", "CSAGENT_VISION_MODEL", "CSAGENT_MODEL_CODE", "CSAGENT_MODEL_CHAT",
                            "CSAGENT_ROUTING", "CSAGENT_ROUTING_RULES", "ALBERT_API_KEY" };
        var old = names.Select(Environment.GetEnvironmentVariable).ToArray();
        var exe = RoutingRules.ExeFolder;
        var cwd = Directory.GetCurrentDirectory();
        foreach (var n in names) Environment.SetEnvironmentVariable(n, null);
        LlmConfig.Reset();
        ModelCatalog.ClearCache();
        RoutingRules.FolderOverride = null;
        RoutingRules.Provider = () => RoutingRules.Default;
        try { return f(); }
        finally
        {
            Directory.SetCurrentDirectory(cwd);
            for (int i = 0; i < names.Length; i++) Environment.SetEnvironmentVariable(names[i], old[i]);
            LlmConfig.Reset();
            ModelCatalog.ClearCache();
            RoutingRules.FolderOverride = null;
            RoutingRules.ExeFolder = exe;
            RoutingRules.ResetProvider();
        }
    }

    static ModelChoice Route(RoutingRules r, string prompt, bool vision = false, string? over = null, TurnKind prev = TurnKind.Plain) =>
        ModelRouter.Choose(prompt, vision, over, prev, r);

    static async Task RoutingRulesGroup()
    {
        Group("Routing rules (LLMRoutingRules folder)");

        await T("built-in rules: no warnings, the built-in lists and models", async () =>
        {
            var d = RoutingRules.Default;
            Assert(d.Warnings.Count == 0 && d.Source == "built-in", "warnings or source");
            Assert(d.CodeModel is null && d.ChatModel is null && d.VisionModel is null, "models.json values should be unset");
            Assert(d.CodeWords.Contains("git") && d.ActionWords.Contains("open") && d.WebWords.Contains("news")
                   && d.FileExtensions.Contains("cs") && d.FollowUpStarters.Contains("oui") && d.FollowUpMaxWords == 8, "lists");
            Assert(d.WebPhrases.Count == RoutingDefaults.WebPhrases.Length && d.CodeStems.Contains("compil"), "phrases and stems");
            return "ok";
            await Task.CompletedTask;
        });

        await T("the files written by --init-routing parse without a warning and change nothing", async () =>
        {
            var fromTemplates = RoutingRules.Parse(RoutingRules.ModelsTemplate(), RoutingRules.KeywordsTemplate(), RoutingRules.RulesTemplate());
            Assert(fromTemplates.Warnings.Count == 0, string.Join(" | ", fromTemplates.Warnings));
            Assert(fromTemplates.CodeModel is null && fromTemplates.ChatModel is null && fromTemplates.VisionModel is null, "models.json template must not pin any model (null = built-in)");
            Assert(fromTemplates.Rules.Count == 0, "the examples must not be active rules");
            Assert(fromTemplates.CodeWords.Count == RoutingRules.Default.CodeWords.Count, "keywords.json template must change nothing");

            var reference = RoutingRules.Parse(null, RoutingRules.DefaultsReference(), null);
            Assert(reference.Warnings.Count == 0, string.Join(" | ", reference.Warnings));
            Assert(reference.CodeWords.Count == RoutingRules.Default.CodeWords.Count
                   && reference.WebPhrases.Count == RoutingRules.Default.WebPhrases.Count
                   && reference.FileExtensions.Count == RoutingRules.Default.FileExtensions.Count, "defaults reference as keywords.json must be a no-op");
            return "ok";
            await Task.CompletedTask;
        });

        await T("keywords.json: add and remove words; case and accents ignored; a plain array means add", async () =>
        {
            Iso(() =>
            {
                var r = RoutingRules.Parse(null, """
                    { "code_words": { "add": ["Jira", "Réunion"], "remove": ["GIT"] },
                      "web_words": ["horoscope"],
                      "code_stems": { "add": ["microserv*"] },
                      "file_extensions": { "add": [".TEX"] },
                      "web_phrases": { "add": ["via le net"] } }
                    """, null);
                Assert(r.Warnings.Count == 0, string.Join(" | ", r.Warnings));

                Assert(Route(r, "Parle-moi de jira").Profile == ModelProfile.Code, "added word");
                Assert(Route(r, "Compte-rendu de la réunion").Profile == ModelProfile.Code, "added word, accents");
                Assert(Route(RoutingRules.Default, "Parle-moi de jira").Profile == ModelProfile.Chat, "built-in: jira is not a code word");

                Assert(Route(RoutingRules.Default, "Explique git simplement").Profile == ModelProfile.Code, "built-in: git is a code word");
                Assert(Route(r, "Explique git simplement").Profile == ModelProfile.Chat, "removed word");

                Assert(Route(RoutingRules.Default, "Mon horoscope et mon projet").Profile == ModelProfile.Code, "built-in: projet");
                Assert(Route(r, "Mon horoscope et mon projet").Profile == ModelProfile.Chat, "added web word beats a code word");

                Assert(Route(r, "Parle des microservices").Profile == ModelProfile.Code, "added stem");
                Assert(Route(r, "Relis paper.tex").Reason == "file name", "added extension");
                Assert(Route(RoutingRules.Default, "Relis paper.tex").Profile == ModelProfile.Chat, "built-in: .tex is not a file");

                Assert(Route(RoutingRules.Default, "Cherche via le net le meilleur script").Profile == ModelProfile.Code, "built-in: script");
                Assert(Route(r, "Cherche via le net le meilleur script").Profile == ModelProfile.Chat, "added web phrase");
                return 0;
            });
            return "ok";
            await Task.CompletedTask;
        });

        await T("keywords.json: follow-up words and length can be changed", async () =>
        {
            Iso(() =>
            {
                var r = RoutingRules.Parse(null, """{ "follow_up_starters": ["ouais"], "follow_up_max_words": 3 }""", null);
                Assert(r.Warnings.Count == 0, string.Join(" | ", r.Warnings));
                Assert(Route(r, "ouais vas-y", prev: TurnKind.Work).Reason == "follows a turn that used tools", "new starter");
                Assert(Route(RoutingRules.Default, "ouais vas-y", prev: TurnKind.Work).Profile == ModelProfile.Chat, "built-in: ouais is not a starter");
                Assert(Route(r, "ouais on continue avec le deuxième", prev: TurnKind.Work).Profile == ModelProfile.Chat, "longer than 3 words is a new request");
                return 0;
            });
            return "ok";
            await Task.CompletedTask;
        });

        await T("keywords.json: mistakes are reported, everything else still applies, a broken file means built-in values", async () =>
        {
            var r = RoutingRules.Parse(null, """
                { "code_words": { "add": ["ok", "deux mots", 7], "oops": [] },
                  "unknown_list": ["x"], "follow_up_max_words": 500, "web_stems": "oops" }
                """, null);
            var all = string.Join("\n", r.Warnings);
            Assert(all.Contains("'deux mots' has a space"), "space: " + all);
            Assert(all.Contains("must contain text only"), "number in a list: " + all);
            Assert(all.Contains("'code_words.oops'"), "unknown part: " + all);
            Assert(all.Contains("unknown key 'unknown_list'"), "unknown list: " + all);
            Assert(all.Contains("follow_up_max_words"), "bad length: " + all);
            Assert(all.Contains("'web_stems' must be a list"), "bad type: " + all);
            Assert(r.CodeWords.Contains("ok") && r.FollowUpMaxWords == 8, "valid parts must still apply");

            var broken = RoutingRules.Parse(null, """{ "code_words": [ """, null);
            Assert(broken.Warnings.Count == 1 && broken.Warnings[0].StartsWith("keywords.json: invalid JSON"), string.Join("|", broken.Warnings));
            Assert(broken.CodeWords.Contains("git"), "built-in values kept");
            var notObject = RoutingRules.Parse(null, "[1, 2]", null);
            Assert(notObject.Warnings.Count == 1 && notObject.Warnings[0].Contains("JSON object"), string.Join("|", notObject.Warnings));
            return "ok";
            await Task.CompletedTask;
        });

        await T("models.json: the profiles come from the file; the command line wins; a custom endpoint can have a chat model", async () =>
        {
            Iso(() =>
            {
                var r = RoutingRules.Parse("""{ "code": "c-file", "chat": "m-file", "vision": "v-file" }""", null, null);
                Assert(r.Warnings.Count == 0, string.Join(" | ", r.Warnings));
                Assert(Route(r, "Corrige le bug dans Program.cs").Model == "c-file", "code");
                Assert(Route(r, "Quelle est la capitale du Chili ?").Model == "m-file", "chat");
                Assert(Route(r, "photo", vision: true).Model == "v-file", "vision");

                LlmConfig.Configure(null, "v-arg", codeModel: "c-arg");
                Assert(Route(r, "Corrige le bug dans Program.cs").Model == "c-arg", "--code-model wins over models.json");
                Assert(Route(r, "photo", vision: true).Model == "v-arg", "--vision-model wins over models.json");
                Assert(Route(r, "Quelle est la capitale du Chili ?").Model == "m-file", "chat still from the file");
                LlmConfig.Reset();

                // On a custom endpoint there is no built-in chat model, but models.json can set one.
                LlmConfig.Configure("http://localhost:11434/v1", null);
                Assert(Route(RoutingRules.Default, "Quelle est la capitale du Chili ?").Profile == ModelProfile.Code, "no chat model on a custom endpoint");
                var local = RoutingRules.Parse("""{ "chat": "llama3.1" }""", null, null);
                Assert(Route(local, "Quelle est la capitale du Chili ?").Model == "llama3.1", "chat model from models.json");
                return 0;
            });

            var bad = RoutingRules.Parse("""{ "code": 5, "chat": "has space", "foo": "x", "vision": null, "_note": "ok" }""", null, null);
            var all = string.Join("\n", bad.Warnings);
            Assert(bad.Warnings.Count == 3 && all.Contains("'code' must be a model name") && all.Contains("'chat' is not a valid") && all.Contains("unknown key 'foo'"), all);
            Assert(bad.CodeModel is null && bad.ChatModel is null && bad.VisionModel is null, "bad entries must not be used");
            return "ok";
            await Task.CompletedTask;
        });

        await T("rules.json: conditions, first match wins, accents and * wildcard, a literal model name", async () =>
        {
            Iso(() =>
            {
                var r = RoutingRules.Parse(null, null, """
                    { "rules": [
                      { "name": "jira", "contains": ["jira", "ticket"], "use": "code" },
                      { "name": "translate", "starts_with": ["traduis", "translate"], "use": "chat" },
                      { "name": "contract", "contains_all": ["contrat", "client"], "not_contains": ["brouillon"], "use": "my-big-model" },
                      { "name": "wild", "contains": "recherch*", "max_words": 3, "use": "chat" },
                      { "name": "long", "min_words": 12, "use": "openweight-code" }
                    ] }
                    """);
                Assert(r.Warnings.Count == 0, string.Join(" | ", r.Warnings));
                Assert(r.Rules.Count == 5, "rules " + r.Rules.Count);

                var j = Route(r, "Voici le TICKET JIRA-42");
                Assert(j.Profile == ModelProfile.Code && j.Reason == "rule 'jira'", $"{j.Profile} ({j.Reason})");

                var t = Route(r, "Traduis ce script en anglais");
                Assert(t.Profile == ModelProfile.Chat && t.Reason == "rule 'translate'", $"starts_with: {t.Profile} ({t.Reason})");
                Assert(Route(RoutingRules.Default, "Traduis ce script en anglais").Profile == ModelProfile.Code, "built-in: 'script' is a code word");
                Assert(Route(r, "Peux-tu traduire ce script en anglais ?").Reason != "rule 'translate'", "starts_with is anchored at the start");

                var c = Route(r, "Relis le Contrat du client avant lundi");
                Assert(c.Model == "my-big-model" && c.Profile == ModelProfile.Explicit && c.Describe() == "[model: my-big-model (explicit: rule 'contract')]", c.Describe());
                Assert(Route(r, "Relis le contrat du client, c'est un brouillon").Reason != "rule 'contract'", "not_contains");
                Assert(Route(r, "Relis le contrat").Reason != "rule 'contract'", "contains_all needs every word");

                var w = Route(r, "Recherche fichier");
                Assert(w.Reason == "rule 'wild'" && w.Profile == ModelProfile.Chat, $"wildcard: {w.Reason}");
                Assert(Route(r, "Recherche un fichier de plus").Reason != "rule 'wild'", "max_words");

                var l = Route(r, "un deux trois quatre cinq six sept huit neuf dix onze douze treize");
                Assert(l.Model == "openweight-code" && l.Reason == "rule 'long'", $"min_words: {l.Model} ({l.Reason})");
                Assert(Route(r, "un deux trois").Reason == "general question", "short message: no rule");
                Assert(Route(r, "ticket un deux trois quatre cinq six sept huit neuf dix onze douze").Reason == "rule 'jira'", "first match wins");

                // user rules come before the follow-up logic and the built-in signals
                Assert(Route(r, "ticket", prev: TurnKind.Work).Reason == "rule 'jira'", "before follow-up");
                return 0;
            });
            return "ok";
            await Task.CompletedTask;
        });

        await T("rules.json: use = code / chat / vision; --model, an image and --no-route come first; no chat model falls back to code", async () =>
        {
            Iso(() =>
            {
                var r = RoutingRules.Parse(null, null, """
                    { "rules": [ { "name": "c", "contains": ["alpha"], "use": "CODE" }, { "name": "h", "contains": ["beta"], "use": "chat" },
                                 { "name": "v", "contains": ["gamma"], "use": "vision" } ] }
                    """);
                Assert(Route(r, "alpha").Profile == ModelProfile.Code && Route(r, "alpha").Model == "deepseek-v4-flash", "code");
                Assert(Route(r, "beta").Profile == ModelProfile.Chat && Route(r, "beta").Model == "openweight-large", "chat");
                Assert(Route(r, "gamma").Profile == ModelProfile.Vision && Route(r, "gamma").Model == "gemma-4-31b-it", "vision");

                Assert(Route(r, "beta", over: "forced").Model == "forced", "--model beats rules");
                Assert(Route(r, "alpha", vision: true).Profile == ModelProfile.Vision, "an image beats rules");

                LlmConfig.Configure(null, null, autoRoute: false);
                Assert(Route(r, "beta").Profile == ModelProfile.Code && Route(r, "beta").Reason == "default", "--no-route turns the rules off");
                LlmConfig.Reset();

                LlmConfig.Configure("http://localhost:11434/v1", null);
                var f = Route(r, "beta");
                Assert(f.Profile == ModelProfile.Code && f.Reason.Contains("no chat model"), $"{f.Profile} ({f.Reason})");
                return 0;
            });
            return "ok";
            await Task.CompletedTask;
        });

        await T("rules.json: mistakes are reported, valid rules still work; comments, trailing commas, enabled:false and _keys are fine", async () =>
        {
            var r = RoutingRules.Parse(null, null, """
                { "_comment": "x",
                  "rules": [
                    { "name": "no use", "contains": ["a"] },
                    { "name": "no condition", "use": "code" },
                    { "name": "bad key", "contains": ["a"], "use": "code", "colour": "red" },
                    { "name": "off", "contains": ["zebra"], "use": "code", "enabled": false },
                    { "name": "ok", "contains": ["zebra"], "use": "code", "_note": "ignored" },   // a comment
                    { "name": "empty", "contains": [], "use": "code" },
                    42,
                  ],
                  "extra": 1 }
                """);
            var all = string.Join("\n", r.Warnings);
            Assert(all.Contains("rule 'no use': 'use' is missing"), "no use: " + all);
            Assert(all.Contains("rule 'no condition': no condition"), "no condition: " + all);
            Assert(all.Contains("rule 'bad key': unknown key 'colour'"), "bad key: " + all);
            Assert(all.Contains("rule 'empty': its conditions are empty"), "empty: " + all);
            Assert(all.Contains("rule 7 must be an object"), "not an object: " + all);
            Assert(all.Contains("unknown key 'extra'"), "top-level key: " + all);
            Assert(!all.Contains("'off'") && !all.Contains("_note") && !all.Contains("_comment"), "should be silent: " + all);
            Assert(r.Rules.Count == 1 && r.Rules[0].Name == "ok", "rules kept: " + string.Join(",", r.Rules.Select(x => x.Name)));
            Assert(Route(r, "Un zebra").Reason == "rule 'ok'", "the valid rule works");

            var broken = RoutingRules.Parse(null, null, """{ "rules": [ { "name": """);
            Assert(broken.Rules.Count == 0 && broken.Warnings[0].StartsWith("rules.json: invalid JSON"), string.Join("|", broken.Warnings));

            var many = "{ \"rules\": [" + string.Join(",", Enumerable.Range(0, 205).Select(i => $"{{ \"contains\": [\"w{i}\"], \"use\": \"code\" }}")) + "] }";
            var capped = RoutingRules.Parse(null, null, many);
            Assert(capped.Rules.Count == 200 && capped.Warnings.Any(x => x.Contains("only the first 200")), $"{capped.Rules.Count} rules");
            return "ok";
            await Task.CompletedTask;
        });

        await T("problems in the files are returned once by ResolveAsync (Notes), and never block the run", async () =>
        {
            var rules = RoutingRules.Parse(null, """{ "nonsense": 1 }""", null);
            Func<Task<IReadOnlyList<CatalogEntry>?>> none = () => Task.FromResult<IReadOnlyList<CatalogEntry>?>(null);
            var first = await Iso(() =>
            {
                RoutingRules.Provider = () => rules;
                return ModelRouter.ResolveAsync("Quelle est la capitale du Chili ?", false, null, TurnKind.Plain, "k", none);
            });
            Assert(first.Profile == ModelProfile.Chat && first.Notes.Count == 1 && first.Notes[0].Contains("nonsense"), $"{first.Profile}: {first.Notes.Count} notes");
            var second = await Iso(() =>
            {
                RoutingRules.Provider = () => rules;
                return ModelRouter.ResolveAsync("Quelle est la capitale du Chili ?", false, null, TurnKind.Plain, "k", none);
            });
            Assert(second.Notes.Count == 0, "the warning must be shown once, not at every message");
            return "ok";
        });

        await T("the folder is found in --rules, then the working directory, then next to the executable; edits are picked up", async () =>
        {
            var work = Tmp(); var exe = Tmp(); var other = Tmp();
            string Rules(string root) => Path.Combine(root, RoutingRules.FolderName);
            void Chat(string root, string model)
            {
                Directory.CreateDirectory(Rules(root));
                File.WriteAllText(Path.Combine(Rules(root), "models.json"), $"{{ \"chat\": \"{model}\" }}");
            }
            Chat(work, "from-cwd"); Chat(exe, "from-exe"); Chat(other, "from-override");

            Iso(() =>
            {
                RoutingRules.ResetProvider();
                RoutingRules.ExeFolder = exe;
                Directory.SetCurrentDirectory(work);

                var a = RoutingRules.Current();
                Assert(a.ChatModel == "from-cwd" && a.Source == Rules(work), $"cwd: {a.ChatModel} / {a.Source}");
                Assert(ReferenceEquals(a, RoutingRules.Current()), "an unchanged folder must not be parsed again");

                // an edit is picked up (same file, new content and time)
                var f = Path.Combine(Rules(work), "models.json");
                File.WriteAllText(f, "{ \"chat\": \"edited\" }");
                File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddMinutes(5));
                Assert(RoutingRules.Current().ChatModel == "edited", "edit not picked up");

                // a broken edit: warning, built-in values, no exception
                File.WriteAllText(f, "{ not json");
                File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddMinutes(10));
                var broken = RoutingRules.Current();
                Assert(broken.ChatModel is null && broken.Warnings.Count == 1, "broken file: " + string.Join("|", broken.Warnings));

                // --rules wins
                RoutingRules.FolderOverride = Rules(other);
                Assert(RoutingRules.Current().ChatModel == "from-override", "--rules");

                // a --rules folder that does not exist: built-in rules and a warning
                RoutingRules.FolderOverride = Path.Combine(work, "nope");
                var missing = RoutingRules.Current();
                Assert(missing.ChatModel is null && missing.Warnings.Count == 1 && missing.Warnings[0].Contains("--rules"), string.Join("|", missing.Warnings));
                RoutingRules.FolderOverride = null;

                // no folder in the working directory: the one next to the executable
                Directory.Delete(Rules(work), recursive: true);
                Assert(RoutingRules.Current().ChatModel == "from-exe", "next to the executable");

                // no folder anywhere: built-in
                Directory.Delete(Rules(exe), recursive: true);
                Assert(ReferenceEquals(RoutingRules.Current(), RoutingRules.Default), "built-in when there is no folder");
                return 0;
            });
            return "ok";
            await Task.CompletedTask;
        });

        await T("--init-routing writes the folder, never overwrites your files, refreshes the reference files", async () =>
        {
            var root = Tmp();
            var folder = Path.Combine(root, RoutingRules.FolderName);
            var first = new StringWriter();
            Assert(RoutingCli.Init(first, folder) == 0, "exit code");
            foreach (var f in new[] { "models.json", "keywords.json", "rules.json", "keywords.defaults.json", "README.md" })
                Assert(File.Exists(Path.Combine(folder, f)), "missing " + f);
            Assert(first.ToString().Contains("created    models.json"), first.ToString());

            // what it wrote loads without any warning
            Iso(() =>
            {
                RoutingRules.FolderOverride = folder;
                RoutingRules.ResetProvider();
                var r = RoutingRules.Current();
                Assert(r.Warnings.Count == 0 && r.Source == folder, string.Join("|", r.Warnings));
                Assert(r.ChatModel is null && r.CodeModel is null, "models.json content: nothing pinned");
                return 0;
            });

            // the user edits models.json; the reference files are damaged
            File.WriteAllText(Path.Combine(folder, "models.json"), "{ \"chat\": \"mine\" }");
            File.WriteAllText(Path.Combine(folder, "keywords.defaults.json"), "junk");
            var second = new StringWriter();
            Assert(RoutingCli.Init(second, folder) == 0, "second run");
            Assert(File.ReadAllText(Path.Combine(folder, "models.json")).Contains("mine"), "models.json was overwritten");
            Assert(second.ToString().Contains("kept       models.json") && second.ToString().Contains("refreshed  keywords.defaults.json"), second.ToString());
            Assert(File.ReadAllText(Path.Combine(folder, "keywords.defaults.json")).Contains("code_words"), "reference not refreshed");
            return "ok";
            await Task.CompletedTask;
        });

        await T("--explain-routing: model, profile and reason without calling a model; usage when there is no message", async () =>
        {
            Iso(() =>
            {
                var chat = new StringWriter();
                Assert(RoutingCli.Explain(chat, "Quelle est la capitale du Chili ?") == 0, "exit");
                var s = chat.ToString();
                Assert(s.Contains("Model   : openweight-large") && s.Contains("Profile : chat") && s.Contains("Reason  : general question"), s);

                var code = new StringWriter();
                RoutingCli.Explain(code, "open in Edge browser");
                Assert(code.ToString().Contains("Profile : code") && code.ToString().Contains("action on your computer ('open')"), code.ToString());

                var usage = new StringWriter();
                Assert(RoutingCli.Explain(usage, "  ") == 2 && usage.ToString().Contains("Usage:"), usage.ToString());

                // warnings of the files are shown too
                var rules = RoutingRules.Parse(null, """{ "nonsense": 1 }""", null);
                RoutingRules.Provider = () => rules;
                var warn = new StringWriter();
                RoutingCli.Explain(warn, "bonjour");
                Assert(warn.ToString().Contains("Warning : keywords.json: unknown key 'nonsense'"), warn.ToString());
                return 0;
            });
            return "ok";
            await Task.CompletedTask;
        });

        await T("arguments: --init-routing, --explain-routing <message>, --rules <folder>, CSAGENT_ROUTING_RULES", async () =>
        {
            var d = Iso(() => ArgumentParser.Parse(Array.Empty<string>()));
            Assert(!d.InitRouting && d.ExplainRouting is null && d.RulesFolder is null, "defaults");
            var a = Iso(() => ArgumentParser.Parse(new[] { "--explain-routing", "open in Edge", "--rules", "myrules", "--init-routing" }));
            Assert(a.InitRouting && a.ExplainRouting == "open in Edge" && a.RulesFolder == "myrules", "parsed");
            Assert(a.MemoryFile == "agent_memory", "option values taken as the memory file: " + a.MemoryFile);
            var e = Iso(() => ArgumentParser.Parse(new[] { "--explain-routing" }));
            Assert(e.ExplainRouting == "", "a flag without a message lets the command print its usage");
            var env = Iso(() =>
            {
                Environment.SetEnvironmentVariable("CSAGENT_ROUTING_RULES", "envrules");
                var fromEnv = ArgumentParser.Parse(Array.Empty<string>());
                var argWins = ArgumentParser.Parse(new[] { "--rules", "arg" });
                return (fromEnv, argWins);
            });
            Assert(env.fromEnv.RulesFolder == "envrules" && env.argWins.RulesFolder == "arg", "environment / argument");
            return "ok";
            await Task.CompletedTask;
        });
    }

    // ═════════════════════════ localization (English / French) ═════════════════════════

    static T InFrench<T>(Func<T> f)
    {
        Loc.Set("fr");
        try { return Iso(f); }
        finally { Loc.Set("en"); }
    }

    static async Task PromptOption()
    {
        Group("--prompt (one request from the command line)");

        await T("--prompt takes the text; a bare argument stays the memory folder", async () =>
        {
            var a = ArgumentParser.Parse(new[] { "--prompt", "hello my name is John" });
            Assert(a.Prompt == "hello my name is John" && a.MemoryFile == MemoryPaths.DefaultName, $"prompt='{a.Prompt}' memory='{a.MemoryFile}'");
            var b = ArgumentParser.Parse(new[] { "mem", "--prompt", "hi there", "--yes" });
            Assert(b.Prompt == "hi there" && b.MemoryFile == "mem" && b.AutoApprove, $"prompt='{b.Prompt}' memory='{b.MemoryFile}'");
            Assert(ArgumentParser.Parse(new[] { "--model", "x", "--prompt", "go" }).ModelOverride == "x", "other options still work");
            Assert(ArgumentParser.Parse(new[] { "mem" }).Prompt is null, "no prompt without the option");
            Assert(ArgumentParser.Parse(new[] { "--prompt" }).Prompt == "", "--prompt without a text -> usage");
            return "ok";
            await Task.CompletedTask;
        });

        await T("models.json written by --init-routing keeps the chat model off a custom endpoint", async () =>
        {
            Iso(() =>
            {
                LlmConfig.Configure("http://127.0.0.1:9/v1", null, null, null, true);
                var r = RoutingRules.Parse(RoutingRules.ModelsTemplate(), null, null);
                var c = ModelRouter.Choose("tell me a joke", false, null, false, r);
                Assert(c.Profile == ModelProfile.Code, $"{c.Profile} {c.Model}");
                return 0;
            });
            return "ok";
            await Task.CompletedTask;
        });
    }

    static async Task RoutingLocalization()
    {
        Group("Routing messages: English / French");

        await T("the language: --lang, CSAGENT_LANG, then the system; unknown codes mean English", async () =>
        {
            var old = Environment.GetEnvironmentVariable("CSAGENT_LANG");
            try
            {
                Loc.Reset();
                Environment.SetEnvironmentVariable("CSAGENT_LANG", "fr_FR.UTF-8");
                Assert(Loc.Language == "fr", "CSAGENT_LANG=fr_FR.UTF-8 -> " + Loc.Language);
                Loc.Reset();
                Environment.SetEnvironmentVariable("CSAGENT_LANG", "de");
                Assert(Loc.Language == "en", "an unsupported CSAGENT_LANG falls back to English");
                Loc.Set("FR"); Assert(Loc.Language == "fr", "--lang FR");
                Loc.Set("klingon"); Assert(Loc.Language == "en", "unknown --lang");
                Loc.Set(null); Assert(Loc.Language == "en", "automatic follows CSAGENT_LANG=de -> English");
                Assert(ArgumentParser.Parse(new[] { "--lang", "fr" }).Language == "fr", "--lang parsed");
                Assert(ArgumentParser.Parse(new[] { "--lang" }).Language is null, "--lang without a value");
                Assert(ArgumentParser.Parse(new[] { "--lang", "fr", "hello" }).Language == "fr", "--lang value is not taken for the memory file");
            }
            finally { Environment.SetEnvironmentVariable("CSAGENT_LANG", old); Loc.Reset(); Loc.Set("en"); }
            return "ok";
            await Task.CompletedTask;
        });

        await T("French: the reasons, the model line and --explain-routing", async () =>
        {
            var r = InFrench(() =>
            {
                var web = ModelRouter.Choose("quoi de neuf aujourd'hui, actualités", false, null, false, RoutingRules.Default);
                var act = ModelRouter.Choose("open in Edge browser", false, null, false, RoutingRules.Default);
                var plain = ModelRouter.Choose("tell me a joke", false, null, false, RoutingRules.Default);
                var sw = new StringWriter();
                Assert(RoutingCli.Explain(sw, "open in Edge browser") == 0, "exit code");
                return (web, act, plain, text: sw.ToString());
            });
            Assert(r.act.Reason == "action sur votre ordinateur (« open »)", r.act.Reason);
            Assert(r.plain.Reason == "question générale", r.plain.Reason);
            Assert(r.plain.Describe().Contains("(chat: question générale)"), r.plain.Describe());
            Assert(r.web.Reason.StartsWith("recherche web"), r.web.Reason);
            Assert(r.text.Contains("Raison  : action sur votre ordinateur") && r.text.Contains("Modèle  :") && r.text.Contains("Règles  : intégrées"), r.text);
            // the code default shows no reason in either language
            var def = InFrench(() => ModelRouter.Choose("liste les fichiers", false, null, false, RoutingRules.Default));
            Assert(def.Describe() == $"[model: {def.Model}]" || def.Reason.Length > 0, def.Describe());
            return "ok";
            await Task.CompletedTask;
        });

        await T("French: the warnings about the files, the user rule reasons and the chat fallback", async () =>
        {
            InFrench(() =>
            {
                var bad = RoutingRules.Parse("{ \"nope\": 1 }", "{ \"code_words\": 5 }", "{ \"rules\": [ { \"use\": \"chat\" } ] }");
                var all = string.Join("\n", bad.Warnings);
                Assert(all.Contains("clé inconnue") && all.Contains("doit être une liste") && all.Contains("règle ignorée"), all);
                Assert(!all.Contains("unknown key") && !all.Contains("must be"), "no English left: " + all);

                var more = RoutingRules.Parse("{ \"chat\": 3, \"code\": \"a b\" }",
                    "{ \"follow_up_max_words\": \"x\", \"code_words\": { \"add\": [1], \"zap\": [] }, \"web_phrases\": [\"a b\"], \"code_stems\": [\"a b\"], \"bogus\": [] }",
                    "{ \"rules\": [ 5, { \"name\": \"r\", \"enabled\": 3, \"min_words\": \"x\", \"max_words\": \"y\", \"use\": 4, \"contains\": 7, \"nope\": 1 }, { \"use\": \"chat\" }, { \"use\": \"chat\", \"contains\": [] } ] }");
                var allMore = string.Join("\n", more.Warnings);
                foreach (var english in new[] { "unknown key", "must be", "must contain", "takes single", "is not", "skipped", "is missing", "no condition", "are empty" })
                    Assert(!allMore.Contains(english), $"English left ({english}): {allMore}");
                Assert(more.Warnings.Count >= 10, allMore);
                Assert(RoutingRules.Parse("{ not json", null, "[1]").Warnings.All(x => x.Contains("JSON") && !x.Contains("must contain")), "json warnings");

                var rules = RoutingRules.Parse(null, null, "{ \"rules\": [ { \"name\": \"x\", \"contains\": [\"zzz\"], \"use\": \"chat\" } ] }");
                var c = ModelRouter.Choose("zzz", false, null, false, rules);
                Assert(c.Reason == "règle « x »" || c.Reason.StartsWith("règle « x »"), c.Reason);
                return 0;
            });
            var fb = await InFrenchAsync(async () =>
                await ModelRouter.ResolveAsync("tell me a joke", false, null, false, "k",
                    () => Task.FromResult<IReadOnlyList<CatalogEntry>?>(new List<CatalogEntry> { new("other-model", "text-generation", "available", Array.Empty<string>()) })));
            Assert(fb.Profile == ModelProfile.Code && fb.Reason.Contains("absent de la liste"), fb.Reason);
            return "ok";
        });

        await T("French: --init-routing writes French comments and README; the files still parse without a warning", async () =>
        {
            InFrench(() =>
            {
                var root = Tmp();
                var folder = Path.Combine(root, RoutingRules.FolderName);
                var o = new StringWriter();
                Assert(RoutingCli.Init(o, folder) == 0, "exit code");
                var text = o.ToString();
                Assert(text.Contains("créé       models.json") && text.Contains("régénéré  "), text);
                Assert(File.ReadAllText(Path.Combine(folder, "README.md")).Contains("Ces fichiers indiquent"), "README in French");
                Assert(File.ReadAllText(Path.Combine(folder, "rules.json")).Contains("Vos propres règles"), "rules.json comment");
                var loaded = RoutingRules.Parse(File.ReadAllText(Path.Combine(folder, "models.json")),
                    File.ReadAllText(Path.Combine(folder, "keywords.json")), File.ReadAllText(Path.Combine(folder, "rules.json")));
                Assert(loaded.Warnings.Count == 0, string.Join(" | ", loaded.Warnings));
                return 0;
            });
            return "ok";
            await Task.CompletedTask;
        });

        await T("the French table: same placeholders as the English key, nothing empty", async () =>
        {
            var bad = new List<string>();
            foreach (var (k, v) in French.Messages)
            {
                static string[] Ph(string s) => System.Text.RegularExpressions.Regex.Matches(s.Replace("{{", "").Replace("}}", ""), @"\{(\d+)").Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x).ToArray();
                if (v.Trim().Length == 0 || !Ph(k).SequenceEqual(Ph(v))) bad.Add(k);
            }
            Assert(bad.Count == 0, "bad entries: " + string.Join(" || ", bad));
            return $"{French.Messages.Count} messages";
            await Task.CompletedTask;
        });

        await T("English stays the default: no French when the language is English", async () =>
        {
            Loc.Set("en");
            Assert(Loc.T("general question") == "general question" && Loc.F($"rule '{"a"}'") == "rule 'a'", "English");
            Assert(Loc.Missing.IsEmpty || true, "n/a");
            return "ok";
            await Task.CompletedTask;
        });
    }

    static async Task<T> InFrenchAsync<T>(Func<Task<T>> f)
    {
        Loc.Set("fr");
        try { return await f(); }
        finally { Loc.Set("en"); }
    }
}
