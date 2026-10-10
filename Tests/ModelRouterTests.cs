using System.Text.Json.Nodes;
using CsAgent.Core.Abstractions;
using CsAgent.Core.Agent;
using CsAgent.Core.Llm;
using CsAgent.Core.Memory;
using CsAgent.Services;
using CsAgent.Shared;

namespace CsAgent.Tests;

static partial class Tests
{
    // ═════════════════════════ automatic model choice: code / chat / vision ═════════════════════════
    static async Task ModelRouting()
    {
        Group("Model routing (code / chat / vision)");

        static T Clean<T>(Func<T> f)
        {
            var names = new[] { "CSAGENT_ENDPOINT", "CSAGENT_VISION_MODEL", "CSAGENT_MODEL_CODE", "CSAGENT_MODEL_CHAT", "CSAGENT_ROUTING", "ALBERT_API_KEY" };
            var old = names.Select(Environment.GetEnvironmentVariable).ToArray();
            foreach (var n in names) Environment.SetEnvironmentVariable(n, null);
            LlmConfig.Reset();
            ModelCatalog.ClearCache();
            try { return f(); }
            finally
            {
                for (int i = 0; i < names.Length; i++) Environment.SetEnvironmentVariable(names[i], old[i]);
                LlmConfig.Reset();
                ModelCatalog.ClearCache();
            }
        }

        static ModelChoice Pick(string prompt, bool vision = false, string? over = null, bool tools = false) =>
            ModelRouter.Choose(prompt, vision, over, tools);

        // The Albert list as returned by /models?status=true (shape of the real answer).
        const string AlbertModels = """
            {"object":"list","data":[
              {"id":"gpt-oss-120b","type":"text-generation","status":"available","aliases":["openweight-large","openai/gpt-oss-120b"]},
              {"id":"qwen3-coder-30b-a3b-instruct","type":"text-generation","status":"available","aliases":["openweight-code"]},
              {"id":"mistral-small-3-2-24b-instruct-2506","type":"image-text-to-text","status":"available","aliases":["openweight-medium"]},
              {"id":"bge-m3","type":"text-embeddings-inference","status":"available","aliases":["openweight-embeddings"]},
              {"id":"whisper-large-v3","type":"automatic-speech-recognition","status":"available","aliases":["openweight-audio"]},
              {"id":"deepseek-v4-flash-0731","type":"text-generation","status":"degraded","aliases":["deepseek-v4-flash"]},
              {"id":"gemma-4-31b-it","type":"image-text-to-text","status":"available"}
            ]}
            """;

        await T("defaults: code = deepseek-v4-flash, chat = openweight-large (Albert only), routing on", async () =>
        {
            Clean(() =>
            {
                Assert(LlmConfig.CodeModel == "deepseek-v4-flash", LlmConfig.CodeModel);
                Assert(LlmConfig.ChatModel == "openweight-large", LlmConfig.ChatModel ?? "null");
                Assert(LlmConfig.AutoRoute, "routing should be on");
                // A custom endpoint (Ollama) has no Albert alias: no chat model unless set explicitly.
                LlmConfig.Configure("http://localhost:11434/v1", null);
                Assert(LlmConfig.ChatModel is null, "no chat model on a custom endpoint");
                LlmConfig.Configure(null, null, chatModel: "llama3.1");
                Assert(LlmConfig.ChatModel == "llama3.1", "explicit chat model");
                return 0;
            });
            return "ok";
            await Task.CompletedTask;
        });

        await T("arguments and environment: --code-model, --chat-model, --no-route, CSAGENT_MODEL_*, CSAGENT_ROUTING", async () =>
        {
            var d = Clean(() => ArgumentParser.Parse(Array.Empty<string>()));
            Assert(d.CodeModel is null && d.ChatModel is null && d.AutoRoute, "defaults");
            var a = Clean(() => ArgumentParser.Parse(new[] { "--code-model", "c1", "--chat-model", "m1", "--no-route" }));
            Assert(a.CodeModel == "c1" && a.ChatModel == "m1" && !a.AutoRoute, "arguments");
            var e = Clean(() =>
            {
                Environment.SetEnvironmentVariable("CSAGENT_MODEL_CODE", "ec");
                Environment.SetEnvironmentVariable("CSAGENT_MODEL_CHAT", "em");
                Environment.SetEnvironmentVariable("CSAGENT_ROUTING", "off");
                return ArgumentParser.Parse(Array.Empty<string>());
            });
            Assert(e.CodeModel == "ec" && e.ChatModel == "em" && !e.AutoRoute, "environment");
            var w = Clean(() => ArgumentParser.Parse(new[] { "--chat-model", "m1", "--code-model", "c1" }));
            Assert(w.MemoryFile == "agent_memory", "option values taken as the memory file: " + w.MemoryFile);
            return "ok";
            await Task.CompletedTask;
        });

        await T("general conversation (French and English) goes to the chat model", async () =>
        {
            Clean(() =>
            {
                foreach (var p in new[]
                {
                    "Explique-moi la théorie de la relativité en termes simples",
                    "Quelle est la capitale de l'Australie ?",
                    "Write a short poem about autumn",
                    "Peux-tu me donner des conseils pour préparer un entretien d'embauche ?",
                    "Comment fonctionne la photosynthèse ?",
                    "Quel jour de la semaine était le 10/10/2026 ?",
                    "Préfères-tu le thé et/ou le café ?",
                    "Le directeur a-t-il raison ?",
                    "Regarde https://example.com et résume",
                    "Lis https://www.sciencedaily.com/releases/2026/10/x.pdf et résume-le",
                })
                {
                    var c = Pick(p);
                    Assert(c.Profile == ModelProfile.Chat && c.Model == "openweight-large", $"'{p}' -> {c.Profile} ({c.Reason})");
                }
                return 0;
            });
            return "ok";
            await Task.CompletedTask;
        });

        await T("code, files, shell, mail and links stay on the code model, with the reason", async () =>
        {
            Clean(() =>
            {
                foreach (var p in new[]
                {
                    "Corrige le bug dans Program.cs",
                    "Add a unit test for the parser",
                    "Lis le fichier de config et dis-moi ce qu'il contient",
                    "Fais un commit de mes changements",
                    "Envoie un email à Paul avec le résumé",
                    "Mets à jour src/Core/Llm pour le nouveau modèle",
                    "Le fichier est dans C:\\Users\\sdidio\\notes",
                    "Pourquoi cela plante ?\n```\nvar x = null;\n```",
                    "Transcris l'enregistrement de la réunion",
                    "Refactor this method to be shorter",
                    "Compile le projet",
                    "Quelle est la différence entre une classe et une interface ?",
                    "Résume ceci\n\n[Attached text file: transcripts/rec_1.txt]",
                    "Écris une fonction qui trie une liste",
                    "How do I write a C# loop?",
                })
                {
                    var c = Pick(p);
                    Assert(c.Profile == ModelProfile.Code && c.Model == "deepseek-v4-flash", $"'{p}' -> {c.Profile} ({c.Reason})");
                    Assert(c.Reason != "default" && c.Reason.Length > 0, "reason should name the signal: " + p);
                }
                return 0;
            });
            return "ok";
            await Task.CompletedTask;
        });

        await T("searches go to the chat model: news, 'sur internet', 'search the web', links to read; code words do not override an explicit web search", async () =>
        {
            Clean(() =>
            {
                foreach (var p in new[]
                {
                    "fetch latest scientific news",
                    "Quelles sont les actualités du jour ?",
                    "Donne-moi la météo à Paris demain",
                    "Cherche sur internet le prix d'un billet Paris Lisbonne",
                    "Fais une recherche web sur les nouveautés en physique quantique",
                    "Search the web for the best pasta recipe",
                    "Résume la page Wikipedia de Marie Curie",
                    // explicit web search wins over words that only suggest code
                    "Search the web for C# async best practices",
                    "Cherche sur internet comment corriger un bug de projet Visual Studio",
                })
                {
                    var c = Pick(p);
                    Assert(c.Profile == ModelProfile.Chat && c.Model == "openweight-large", $"'{p}' -> {c.Profile} ({c.Reason})");
                }
                var r = Pick("Quelles sont les actualités du jour ?");
                Assert(r.Reason.StartsWith("web search"), "reason should say web search: " + r.Reason);
                return 0;
            });
            return "ok";
            await Task.CompletedTask;
        });

        await T("work signs still beat a search: file names, paths, code and attachments are code", async () =>
        {
            Clean(() =>
            {
                foreach (var p in new[]
                {
                    "Cherche sur internet puis écris le résultat dans notes.md",
                    "Search the web and save it to C:\\temp\\out.txt",
                    "Search the web for this error: `NullReferenceException`",
                    "Actualités du jour, résume-les\n\n[Attached text file: transcripts/x.txt]",
                })
                {
                    var c = Pick(p);
                    Assert(c.Profile == ModelProfile.Code, $"'{p}' -> {c.Profile} ({c.Reason})");
                }
                // but a plain code question with the word "search" is not a web search
                Assert(Pick("Search for bugs in this function").Profile == ModelProfile.Code, "search for bugs");
                return 0;
            });
            return "ok";
            await Task.CompletedTask;
        });

        await T("after a web search, an acknowledgement stays on the chat model; after other tools, on the code model", async () =>
        {
            Clean(() =>
            {
                var w = ModelRouter.Choose("oui, vas-y", false, null, TurnKind.Web);
                Assert(w.Profile == ModelProfile.Chat && w.Reason == "follows a web search", $"{w.Profile} ({w.Reason})");
                var k = ModelRouter.Choose("oui, vas-y", false, null, TurnKind.Work);
                Assert(k.Profile == ModelProfile.Code && k.Reason == "follows a turn that used tools", $"{k.Profile} ({k.Reason})");
                var p = ModelRouter.Choose("oui, vas-y", false, null, TurnKind.Plain);
                Assert(p.Profile == ModelProfile.Chat, "plain: " + p.Profile);
                return 0;
            });
            return "ok";
            await Task.CompletedTask;
        });

        await T("LastTurnKind: web tools only is a search; any other tool is work; no tool is plain", async () =>
        {
            static JsonObject Call(string tool) => new()
            {
                ["role"] = "assistant", ["content"] = "",
                ["tool_calls"] = new JsonArray { new JsonObject { ["id"] = "1", ["function"] = new JsonObject { ["name"] = tool, ["arguments"] = "{}" } } },
            };
            static JsonObject Result() => new() { ["role"] = "tool", ["tool_call_id"] = "1", ["content"] = "..." };

            var web = new JsonArray { JsonHelpers.Message("system", "s"), JsonHelpers.Message("user", "news"),
                Call("web_search"), Result(), Call("fetch_url"), Result(), JsonHelpers.Message("assistant", "voici"), JsonHelpers.Message("user", "oui") };
            Assert(ModelRouter.LastTurnKind(web) == TurnKind.Web, "web only");

            var mixed = new JsonArray { JsonHelpers.Message("system", "s"), JsonHelpers.Message("user", "news"),
                Call("web_search"), Result(), Call("write_file"), Result(), JsonHelpers.Message("user", "oui") };
            Assert(ModelRouter.LastTurnKind(mixed) == TurnKind.Work, "search then write_file is work");

            var work = new JsonArray { JsonHelpers.Message("system", "s"), JsonHelpers.Message("user", "lis"), Call("read_file"), Result(), JsonHelpers.Message("user", "ok") };
            Assert(ModelRouter.LastTurnKind(work) == TurnKind.Work, "read_file");

            var plain = new JsonArray { JsonHelpers.Message("system", "s"), JsonHelpers.Message("user", "salut"), JsonHelpers.Message("assistant", "bonjour"), JsonHelpers.Message("user", "ok") };
            Assert(ModelRouter.LastTurnKind(plain) == TurnKind.Plain, "plain");
            return "ok";
            await Task.CompletedTask;
        });

        await T("an image selects the vision model; --model beats everything", async () =>
        {
            Clean(() =>
            {
                var v = Pick("Que vois-tu sur cette photo ?", vision: true);
                Assert(v.Profile == ModelProfile.Vision && v.Model == "gemma-4-31b-it", v.Model);
                var e = Pick("Quelle est la capitale de l'Australie ?", over: "my-model");
                Assert(e.Profile == ModelProfile.Explicit && e.Model == "my-model", e.Model);
                var ev = Pick("photo", vision: true, over: "my-model");
                Assert(ev.Model == "my-model", "--model should win over vision");
                return 0;
            });
            return "ok";
            await Task.CompletedTask;
        });

        await T("a short acknowledgement after a turn that used tools stays on the code model; a new request does not, however short", async () =>
        {
            Clean(() =>
            {
                var f = Pick("oui vas-y", tools: true);
                Assert(f.Profile == ModelProfile.Code, "short follow-up -> " + f.Profile);
                foreach (var ok in new[] { "ok, continue avec le deuxième", "D'accord", "Merci beaucoup", "yes go ahead", "Fais-le" })
                    Assert(Pick(ok, tools: true).Profile == ModelProfile.Code, $"'{ok}' should carry on the code turn");
                // The case seen in real use: a 4-word NEW request after a tool-using turn is not a follow-up.
                foreach (var fresh in new[] { "fetch latest scientific news", "Quelle est la capitale du Chili ?", "latest news" })
                {
                    var c = Pick(fresh, tools: true);
                    Assert(c.Profile == ModelProfile.Chat && c.Model == "openweight-large", $"'{fresh}' -> {c.Profile} ({c.Reason})");
                }
                Assert(Pick("oui, et maintenant explique-moi en détail comment les plantes vertes fabriquent leur énergie", tools: true).Profile == ModelProfile.Chat,
                    "an acknowledgement followed by a long new subject is a new request");
                var n = Pick("Maintenant change de sujet : peux-tu m'expliquer comment les plantes vertes transforment la lumière en énergie ?", tools: true);
                Assert(n.Profile == ModelProfile.Chat, "new general subject -> " + n.Profile);
                var s = Pick("oui vas-y", tools: false);
                Assert(s.Profile == ModelProfile.Chat, "no tools before: a short message is a plain message -> " + s.Profile);
                return 0;
            });
            return "ok";
            await Task.CompletedTask;
        });

        await T("no chat routing when: --no-route, custom endpoint without chat model, chat model = code model", async () =>
        {
            Clean(() =>
            {
                LlmConfig.Configure(null, null, autoRoute: false);
                Assert(Pick("Quelle est la capitale de l'Australie ?").Profile == ModelProfile.Code, "--no-route");
                LlmConfig.Reset();

                LlmConfig.Configure("http://localhost:11434/v1", null);
                var c = Pick("Quelle est la capitale de l'Australie ?");
                Assert(c.Profile == ModelProfile.Code && c.Model == "deepseek-v4-flash", "custom endpoint: " + c.Profile);
                LlmConfig.Reset();

                LlmConfig.Configure(null, null, chatModel: "deepseek-v4-flash");
                Assert(Pick("Quelle est la capitale de l'Australie ?").Profile == ModelProfile.Code, "same model");
                return 0;
            });
            return "ok";
            await Task.CompletedTask;
        });

        await T("the code model can be changed; the chat profile follows --chat-model", async () =>
        {
            Clean(() =>
            {
                LlmConfig.Configure(null, null, codeModel: "openweight-code", chatModel: "openweight-medium");
                Assert(Pick("Corrige le bug dans Program.cs").Model == "openweight-code", "code model");
                Assert(Pick("Quelle est la capitale de l'Australie ?").Model == "openweight-medium", "chat model");
                return 0;
            });
            return "ok";
            await Task.CompletedTask;
        });

        await T("LastTurnUsedTools: looks at the turn before the current message only", async () =>
        {
            var none = new JsonArray { JsonHelpers.Message("system", "s"), JsonHelpers.Message("user", "bonjour") };
            Assert(!ModelRouter.LastTurnUsedTools(none), "first message");

            var chatOnly = new JsonArray
            {
                JsonHelpers.Message("system", "s"), JsonHelpers.Message("user", "salut"),
                JsonHelpers.Message("assistant", "bonjour"), JsonHelpers.Message("user", "merci"),
            };
            Assert(!ModelRouter.LastTurnUsedTools(chatOnly), "plain chat before");

            var withTools = new JsonArray
            {
                JsonHelpers.Message("system", "s"), JsonHelpers.Message("user", "lis a.txt"),
                new JsonObject { ["role"] = "assistant", ["content"] = "", ["tool_calls"] = new JsonArray { new JsonObject { ["id"] = "1" } } },
                new JsonObject { ["role"] = "tool", ["tool_call_id"] = "1", ["content"] = "..." },
                JsonHelpers.Message("assistant", "voilà"), JsonHelpers.Message("user", "ok"),
            };
            Assert(ModelRouter.LastTurnUsedTools(withTools), "tools before");

            // Tools two turns ago do not count: the previous turn was plain chat.
            var old = new JsonArray
            {
                JsonHelpers.Message("system", "s"), JsonHelpers.Message("user", "lis a.txt"),
                new JsonObject { ["role"] = "tool", ["tool_call_id"] = "1", ["content"] = "..." },
                JsonHelpers.Message("assistant", "voilà"), JsonHelpers.Message("user", "merci"),
                JsonHelpers.Message("assistant", "de rien"), JsonHelpers.Message("user", "et la lune ?"),
            };
            Assert(!ModelRouter.LastTurnUsedTools(old), "older turn must not count");
            return "ok";
            await Task.CompletedTask;
        });

        await T("ModelCatalog.Parse reads ids, types, status and aliases; Find matches an alias", async () =>
        {
            var list = ModelCatalog.Parse(AlbertModels);
            Assert(list.Count == 7, "count " + list.Count);
            var large = ModelCatalog.Find(list, "openweight-large");
            Assert(large is { Id: "gpt-oss-120b", Type: "text-generation", Status: "available" }, "alias lookup");
            Assert(ModelCatalog.Find(list, "GPT-OSS-120B") is not null, "case-insensitive id");
            Assert(ModelCatalog.Find(list, "nope") is null, "unknown");
            Assert(ModelCatalog.Parse("not json").Count == 0 && ModelCatalog.Parse("{}").Count == 0, "bad bodies");
            return "ok";
            await Task.CompletedTask;
        });

        await T("ResolveAsync: a chat model that is listed and available is used; the list is fetched for a chat choice only", async () =>
        {
            await Task.CompletedTask;
            var calls = 0;
            Func<Task<IReadOnlyList<CatalogEntry>?>> load = () => { calls++; return Task.FromResult<IReadOnlyList<CatalogEntry>?>(ModelCatalog.Parse(AlbertModels)); };

            var chat = await Clean(() => ModelRouter.ResolveAsync("Quelle est la capitale de l'Australie ?", false, null, false, "k", load));
            Assert(chat.Profile == ModelProfile.Chat && chat.Model == "openweight-large" && calls == 1, $"chat: {chat.Profile}, calls {calls}");

            calls = 0;
            var code = await Clean(() => ModelRouter.ResolveAsync("Corrige le bug dans Program.cs", false, null, false, "k", load));
            var vis = await Clean(() => ModelRouter.ResolveAsync("photo", true, null, false, "k", load));
            var exp = await Clean(() => ModelRouter.ResolveAsync("capitale ?", false, "x", false, "k", load));
            Assert(calls == 0, "the list must not be fetched for code, vision or --model: " + calls);
            Assert(code.Profile == ModelProfile.Code && vis.Profile == ModelProfile.Vision && exp.Profile == ModelProfile.Explicit, "profiles");
            return "ok";
        });

        await T("ResolveAsync: unknown, unavailable or non-chat model falls back to the code model, with the reason", async () =>
        {
            await Task.CompletedTask;
            static Func<Task<IReadOnlyList<CatalogEntry>?>> Of(string json) =>
                () => Task.FromResult<IReadOnlyList<CatalogEntry>?>(ModelCatalog.Parse(json));

            var unknown = await Clean(() => ModelRouter.ResolveAsync("capitale ?", false, null, false, "k",
                Of("""{"data":[{"id":"other","type":"text-generation"}]}""")));
            Assert(unknown.Profile == ModelProfile.Code && unknown.Model == "deepseek-v4-flash" && unknown.Reason.Contains("openweight-large"), "unknown: " + unknown.Reason);

            var down = await Clean(() => ModelRouter.ResolveAsync("capitale ?", false, null, false, "k",
                Of("""{"data":[{"id":"gpt-oss-120b","type":"text-generation","status":"unavailable","aliases":["openweight-large"]}]}""")));
            Assert(down.Profile == ModelProfile.Code && down.Reason.Contains("unavailable"), "down: " + down.Reason);

            var embed = await Clean(() => ModelRouter.ResolveAsync("capitale ?", false, null, false, "k",
                Of("""{"data":[{"id":"gpt-oss-120b","type":"text-embeddings-inference","aliases":["openweight-large"]}]}""")));
            Assert(embed.Profile == ModelProfile.Code && embed.Reason.Contains("cannot chat"), "type: " + embed.Reason);

            // "degraded" still works: only "unavailable" is refused.
            var degraded = await Clean(() => ModelRouter.ResolveAsync("capitale ?", false, null, false, "k",
                Of("""{"data":[{"id":"gpt-oss-120b","type":"text-generation","status":"degraded","aliases":["openweight-large"]}]}""")));
            Assert(degraded.Profile == ModelProfile.Chat, "degraded: " + degraded.Reason);
            return "ok";
        });

        await T("ResolveAsync: no information (offline, error) keeps the chat model; the run is never blocked", async () =>
        {
            await Task.CompletedTask;
            var offline = await Clean(() => ModelRouter.ResolveAsync("capitale ?", false, null, false, "k",
                () => Task.FromResult<IReadOnlyList<CatalogEntry>?>(null)));
            Assert(offline.Profile == ModelProfile.Chat, "null catalog: " + offline.Profile);

            var boom = await Clean(() => ModelRouter.ResolveAsync("capitale ?", false, null, false, "k",
                () => throw new InvalidOperationException("network down")));
            Assert(boom.Profile == ModelProfile.Chat, "loader exception: " + boom.Profile);
            return "ok";
        });

        await T("Label: the requested model, plus the served name when the server reports another one", async () =>
        {
            Assert(ModelRouter.Label("openweight-large", null) == "openweight-large", "no served name");
            Assert(ModelRouter.Label("openweight-large", "") == "openweight-large", "blank served name");
            Assert(ModelRouter.Label("deepseek-v4-flash", "DeepSeek-V4-Flash") == "deepseek-v4-flash", "same name, other case");
            Assert(ModelRouter.Label("openweight-large", "openai/gpt-oss-120b") == "openweight-large (served: openai/gpt-oss-120b)", "alias");
            return "ok";
            await Task.CompletedTask;
        });

        await T("every assistant message is followed by the model that wrote it; tool-only steps show nothing", async () =>
        {
            var prevCwd = Directory.GetCurrentDirectory();
            var work = Tmp();
            Directory.SetCurrentDirectory(work);
            try
            {
                await File.WriteAllTextAsync(Path.Combine(work, "a.txt"), "x");
                int n = 0;
                var mock = new MockLlm
                {
                    Handler = req =>
                    {
                        n++;
                        JsonNode body = n switch
                        {
                            1 => MockLlm.Text("Let me look.", "tool_calls"),
                            2 => MockLlm.ToolCall("read_file", "{\"path\":\"a.txt\"}"),
                            _ => MockLlm.Text("Done."),
                        };
                        if (n == 1) body["choices"]![0]!["message"]!["tool_calls"] = new JsonArray { new JsonObject {
                            ["id"] = "c0", ["type"] = "function",
                            ["function"] = new JsonObject { ["name"] = "list_dir", ["arguments"] = "{\"path\":\".\"}" } } };
                        body["model"] = n == 3 ? "m" : "served-model-x";   // step 3: the server reports the same name
                        return (200, body, 0);
                    }
                };
                var obs = new ModelRecorder();
                var msgs = new JsonArray { CodingAgent.SystemMessage(false), JsonHelpers.Message("user", "go") };
                using var agent = new CodingAgent("k", mock.BaseUrl, "m", new AgentOptions(Confirm: false), obs, null,
                    new HybridMemoryManager(new SemanticMemory(), new ExactMemory()));
                await agent.RunAsync(msgs, "agent_memory.json");

                // step 1: text + tool call, step 2: tool call only (nothing shown), step 3: text
                Assert(obs.Log.SequenceEqual(new[]
                {
                    "thought:Let me look.", "model:m (served: served-model-x)",
                    "thought:Done.", "model:m",
                }), "log was:\n" + string.Join("\n", obs.Log));
                return "ok";
            }
            finally { Directory.SetCurrentDirectory(prevCwd); }
        });

        await T("--quiet keeps the model under each assistant message", async () =>
        {
            var rec = new ModelRecorder();
            var q = QuietObserver.Wrap(rec, quiet: true);
            await q.OnThought("hello");
            await q.OnAssistantModel("openweight-large");
            Assert(rec.Log.SequenceEqual(new[] { "thought:hello", "model:openweight-large" }), string.Join("|", rec.Log));
            // An observer that does not implement OnAssistantModel is not affected.
            IAgentObserver silent = new SilentObserver();
            await silent.OnAssistantModel("x");
            return "ok";
        });

        await T("ModelChoice.Describe: plain for the default, explained otherwise", async () =>
        {
            Assert(new ModelChoice("m", ModelProfile.Code, "default").Describe() == "[model: m]", "default");
            Assert(new ModelChoice("m", ModelProfile.Chat, "general question").Describe() == "[model: m (chat: general question)]", "chat");
            Assert(new ModelChoice("m", ModelProfile.Code, "mentions 'git'").Describe() == "[model: m (code: mentions 'git')]", "code");
            return "ok";
            await Task.CompletedTask;
        });
    }

    // Records the order of assistant messages and the model shown under each.
    sealed class ModelRecorder : IAgentObserver
    {
        public List<string> Log { get; } = new();
        public Task OnStep(int n, int max) => Task.CompletedTask;
        public Task OnThought(string t) { Log.Add("thought:" + t); return Task.CompletedTask; }
        public Task OnToolCall(string n, string a) => Task.CompletedTask;
        public Task OnToolResult(string r, bool e) => Task.CompletedTask;
        public Task OnDone(string m) => Task.CompletedTask;
        public Task OnError(string m) { Log.Add("error:" + m); return Task.CompletedTask; }
        public Task OnWarning(string m) => Task.CompletedTask;
        public Task OnDanger(string m) => Task.CompletedTask;
        public Task OnAssistantModel(string model) { Log.Add("model:" + model); return Task.CompletedTask; }
        public Task<bool> OnConfirm(string t) => Task.FromResult(true);
    }
}
