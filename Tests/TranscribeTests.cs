using System.Net;
using System.Net.Sockets;
using System.Text;
using CsAgent.Core.Agent;
using CsAgent.Core.Llm;

namespace CsAgent.Tests;

/// <summary>A tiny Whisper stand-in: keeps every request body and answers {"text": ...}.</summary>
sealed class MockWhisper : IDisposable
{
    readonly TcpListener _l = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource _cts = new();
    public string BaseUrl { get; }
    public List<string> Requests { get; } = new();
    public Func<int, (int status, string body)> Reply { get; set; } = n => (200, $"{{\"text\":\"part{n}\"}}");

    public MockWhisper()
    {
        _l.Start();
        BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_l.LocalEndpoint).Port}/v1";
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await _l.AcceptTcpClientAsync(_cts.Token); } catch { return; }
                _ = Task.Run(() => Serve(c));
            }
        });
    }

    async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var body = await MockLlm.ReadBodyAsync(stream);
                (int status, string body) r;
                lock (Requests) { Requests.Add(body); r = Reply(Requests.Count); }
                var payload = Encoding.UTF8.GetBytes(r.body);
                await stream.WriteAsync(Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {r.status} X\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n"));
                await stream.WriteAsync(payload);
            }
            catch { /* client went away */ }
        }
    }

    public void Dispose() { _cts.Cancel(); _l.Stop(); }
}

static partial class Tests
{
    // ═════════════════════════ transcribe_audio ═════════════════════════
    static async Task TranscribeAudio()
    {
        Group("transcribe_audio");

        // Each test: its own working directory (tool paths are limited to it), a mock Whisper as the
        // endpoint, no real ffmpeg (CSAGENT_FFMPEG points nowhere unless the test says otherwise).
        Func<Func<string, MockWhisper, Task<string?>>, Func<Task<string?>>> Run = body => async () =>
        {
            var names = new[] { "CSAGENT_FFMPEG", "CSAGENT_TRANSCRIBE_MODEL", "ALBERT_API_KEY" };
            var saved = names.Select(Environment.GetEnvironmentVariable).ToArray();
            var prev = Directory.GetCurrentDirectory();
            var dir = Tmp();
            Directory.SetCurrentDirectory(dir);
            using var mock = new MockWhisper();
            try
            {
                Environment.SetEnvironmentVariable("CSAGENT_FFMPEG", Path.Combine(dir, "no-such-ffmpeg"));
                Environment.SetEnvironmentVariable("CSAGENT_TRANSCRIBE_MODEL", null);
                LlmConfig.Reset();
                LlmConfig.Configure(mock.BaseUrl, null);   // local endpoint: no key needed
                return await body(dir, mock);
            }
            finally
            {
                Directory.SetCurrentDirectory(prev);
                LlmConfig.Reset();
                for (int i = 0; i < names.Length; i++) Environment.SetEnvironmentVariable(names[i], saved[i]);
            }
        };

        Task<string> Call(string json) => ToolDispatcher.DispatchAsync("transcribe_audio", json, false);

        await T("without ffmpeg a small .wav is sent as is, with the default model", Run(async (dir, mock) =>
        {
            File.WriteAllBytes("a.wav", Encoding.ASCII.GetBytes("RIFFwavedata"));
            var r = await Call("{\"path\":\"a.wav\"}");
            Assert(r == "part1", "result: " + r);
            Assert(mock.Requests.Count == 1, "one request");
            var req = mock.Requests[0];
            Assert(req.Contains("RIFFwavedata"), "audio bytes sent");
            Assert(req.Contains("name=model") && req.Contains(LlmSettings.TranscribeModel), "default model");
            Assert(!req.Contains("name=language"), "no language unless asked");
            return "ok";
        }));

        await T("language and CSAGENT_TRANSCRIBE_MODEL are sent", Run(async (dir, mock) =>
        {
            File.WriteAllBytes("a.mp3", Encoding.ASCII.GetBytes("ID3mp3data"));
            Environment.SetEnvironmentVariable("CSAGENT_TRANSCRIBE_MODEL", "my-whisper");
            var r = await Call("{\"path\":\"a.mp3\",\"language\":\"fr\"}");
            Assert(r == "part1", "result: " + r);
            var req = mock.Requests[0];
            Assert(req.Contains("my-whisper") && req.Contains("name=language") && req.Contains("fr"), "model/language");
            Assert(req.Contains("audio/mpeg"), "mp3 content type");
            return "ok";
        }));

        await T("without ffmpeg another format is refused with a clear message and nothing is sent", Run(async (dir, mock) =>
        {
            File.WriteAllBytes("a.ogg", new byte[100]);
            var r = await Call("{\"path\":\"a.ogg\"}");
            Assert(r.StartsWith("Error: transcribe_audio") && r.Contains("ffmpeg"), r);
            Assert(mock.Requests.Count == 0, "no request");
            return "ok";
        }));

        await T("ffmpeg parts are sent in order and joined (fake ffmpeg script)", Run(async (dir, mock) =>
        {
            if (OperatingSystem.IsWindows()) return "skipped (the fake ffmpeg is a /bin/sh script)";
            var fake = Path.Combine(dir, "fake-ffmpeg");
            File.WriteAllText(fake,
                "#!/bin/sh\nfor a; do last=\"$a\"; done\necho \"$@\" > \"" + Path.Combine(dir, "args.txt") + "\"\n" +
                "printf 'RIFFpart0' > \"$(echo \"$last\" | sed 's/%03d/000/')\"\n" +
                "printf 'RIFFpart1' > \"$(echo \"$last\" | sed 's/%03d/001/')\"\n");
            File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Environment.SetEnvironmentVariable("CSAGENT_FFMPEG", fake);
            mock.Reply = n => (200, n == 1 ? "{\"text\":\"first\"}" : "{\"text\":\"second\"}");

            File.WriteAllBytes("réunion (1).m4a", new byte[50]);   // accents and spaces in the name
            var r = await Call("{\"path\":\"réunion (1).m4a\"}");
            Assert(r == "first second", "joined: " + r);
            Assert(mock.Requests.Count == 2, "two requests");
            Assert(mock.Requests[0].Contains("RIFFpart0") && mock.Requests[1].Contains("RIFFpart1"), "order");
            var args = File.ReadAllText(Path.Combine(dir, "args.txt"));
            Assert(args.Contains("-ar 16000 -ac 1") && args.Contains("-segment_time 600"), "ffmpeg args: " + args);
            Assert(!Directory.GetDirectories(Path.GetTempPath(), "csagent-audio-*").Any(d => Directory.GetFiles(d).Length > 0), "temp parts removed");
            return "ok";
        }));

        await T("an ffmpeg failure is reported with its message", Run(async (dir, mock) =>
        {
            if (OperatingSystem.IsWindows()) return "skipped (the fake ffmpeg is a /bin/sh script)";
            var fake = Path.Combine(dir, "bad-ffmpeg");
            File.WriteAllText(fake, "#!/bin/sh\necho 'Invalid data found' >&2\nexit 1\n");
            File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Environment.SetEnvironmentVariable("CSAGENT_FFMPEG", fake);
            File.WriteAllBytes("x.m4a", new byte[50]);
            var r = await Call("{\"path\":\"x.m4a\"}");
            Assert(r.Contains("ffmpeg failed") && r.Contains("Invalid data found"), r);
            Assert(mock.Requests.Count == 0, "no request");
            return "ok";
        }));

        await T("an API error is returned, not thrown", Run(async (dir, mock) =>
        {
            mock.Reply = _ => (401, "{\"detail\":\"bad key\"}");
            File.WriteAllBytes("a.wav", Encoding.ASCII.GetBytes("RIFFx"));
            var r = await Call("{\"path\":\"a.wav\"}");
            Assert(r.StartsWith("Error: transcribe_audio") && r.Contains("401") && r.Contains("bad key"), r);
            return "ok";
        }));

        await T("a response without text is an error; an empty text says no speech", Run(async (dir, mock) =>
        {
            File.WriteAllBytes("a.wav", Encoding.ASCII.GetBytes("RIFFx"));
            mock.Reply = _ => (200, "{\"oops\":1}");
            var r1 = await Call("{\"path\":\"a.wav\"}");
            Assert(r1.StartsWith("Error: transcribe_audio") && r1.Contains("text"), r1);
            mock.Reply = _ => (200, "{\"text\":\"  \"}");
            var r2 = await Call("{\"path\":\"a.wav\"}");
            Assert(r2 == "(no speech detected)", r2);
            return "ok";
        }));

        await T("max_chars truncates and says how much was left", Run(async (dir, mock) =>
        {
            File.WriteAllBytes("a.wav", Encoding.ASCII.GetBytes("RIFFx"));
            mock.Reply = _ => (200, "{\"text\":\"" + new string('a', 300) + "\"}");
            var r = await Call("{\"path\":\"a.wav\",\"max_chars\":100}");
            Assert(r.StartsWith(new string('a', 100) + "\n[... truncated: 300 characters"), r);
            return "ok";
        }));

        await T("paths outside the working directory, missing and empty files are refused", Run(async (dir, mock) =>
        {
            var outside = Path.Combine(Tmp(), "o.wav");
            File.WriteAllBytes(outside, Encoding.ASCII.GetBytes("RIFFx"));
            var r1 = await Call("{\"path\":" + System.Text.Json.Nodes.JsonValue.Create(outside)!.ToJsonString() + "}");
            Assert(r1.Contains("not allowed"), r1);
            var r2 = await Call("{\"path\":\"nope.wav\"}");
            Assert(r2.Contains("not found"), r2);
            File.WriteAllBytes("e.wav", Array.Empty<byte>());
            var r3 = await Call("{\"path\":\"e.wav\"}");
            Assert(r3.Contains("empty"), r3);
            Assert(mock.Requests.Count == 0, "no request");
            return "ok";
        }));

        await T("a remote endpoint without ALBERT_API_KEY is refused before anything is read", Run(async (dir, mock) =>
        {
            LlmConfig.Reset();   // back to the default (remote) endpoint
            Environment.SetEnvironmentVariable("ALBERT_API_KEY", null);
            File.WriteAllBytes("a.wav", Encoding.ASCII.GetBytes("RIFFx"));
            var r = await Call("{\"path\":\"a.wav\"}");
            Assert(r.Contains("ALBERT_API_KEY"), r);
            return "ok";
        }));

        await T("the progress callback gets (part, total) before each part (fake ffmpeg script)", Run(async (dir, mock) =>
        {
            if (OperatingSystem.IsWindows()) return "skipped (the fake ffmpeg is a /bin/sh script)";
            var fake = Path.Combine(dir, "fake-ffmpeg");
            File.WriteAllText(fake,
                "#!/bin/sh\nfor a; do last=\"$a\"; done\n" +
                "printf 'RIFFpart0' > \"$(echo \"$last\" | sed 's/%03d/000/')\"\n" +
                "printf 'RIFFpart1' > \"$(echo \"$last\" | sed 's/%03d/001/')\"\n");
            File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Environment.SetEnvironmentVariable("CSAGENT_FFMPEG", fake);
            File.WriteAllBytes("long.m4a", new byte[50]);

            var seen = new List<string>();
            var text = await ToolDispatcher.TranscribeFileAsync("long.m4a", "fr", (n, t) => { seen.Add($"{n}/{t}"); return Task.CompletedTask; });
            Assert(string.Join(",", seen) == "1/2,2/2", "progress: " + string.Join(",", seen));
            Assert(text == "part1 part2", "text: " + text);
            return "ok";
        }));

        await T("TranscribeFileAsync throws a readable message (the web UI shows it)", Run(async (dir, mock) =>
        {
            try { await ToolDispatcher.TranscribeFileAsync("nope.wav", null, null); throw new Exception("no exception"); }
            catch (Exception ex) when (ex.Message.Contains("not found")) { }
            await Task.CompletedTask;
            return "ok";
        }));

        await T("the tool is read-only and known to the model", Run(async (dir, mock) =>
        {
            Assert(!ToolDispatcher.IsDestructive("transcribe_audio"), "must not require confirmation");
            Assert(ToolDispatcher.ToolDefinitions.Any(t => t?["function"]?["name"]?.GetValue<string>() == "transcribe_audio"), "definition");
            await Task.CompletedTask;
            return "ok";
        }));
    }
    // ═════════════════════════ web UI recorder: AudioRecordings ═════════════════════════
    static async Task AudioRecordingsTests()
    {
        Group("Web recorder / AudioRecordings");

        Func<Func<string, Task<string?>>, Func<Task<string?>>> InWork = body => async () =>
        {
            var prev = Directory.GetCurrentDirectory();
            var dir = Tmp();
            Directory.SetCurrentDirectory(dir);
            try { return await body(dir); }
            finally { Directory.SetCurrentDirectory(prev); }
        };
        static MemoryStream Bytes(string s) => new(Encoding.ASCII.GetBytes(s));

        await T("start: an audio type gets recordings/rec_<date>_<id>.<ext>; codecs parameters are ignored", InWork(async dir =>
        {
            var rec = new CsAgent.Services.AudioRecordings();
            var a = rec.Start("audio/webm;codecs=opus");
            Assert(a is not null && a.Value.Path.StartsWith("recordings/rec_") && a.Value.Path.EndsWith(".webm"), "path: " + a?.Path);
            Assert(File.Exists(a!.Value.Path) && new FileInfo(a.Value.Path).Length == 0, "empty file created");
            Assert(rec.Start("audio/mp4")!.Value.Path.EndsWith(".m4a") && rec.Start("audio/ogg")!.Value.Path.EndsWith(".ogg"), "other extensions");
            Assert(rec.Start("audio/webm")!.Value.Path != rec.Start("audio/webm")!.Value.Path, "two recordings never share a name");
            await Task.CompletedTask;
            return "ok";
        }));

        await T("start: anything that is not on the audio list is refused (no client-chosen extension)", InWork(async dir =>
        {
            var rec = new CsAgent.Services.AudioRecordings();
            foreach (var bad in new[] { "text/html", "application/octet-stream", "audio/evil", "../../x", "" })
                Assert(rec.Start(bad) is null, "accepted: " + bad);
            Assert(!Directory.Exists("recordings") || Directory.GetFiles("recordings").Length == 0, "nothing written");
            await Task.CompletedTask;
            return "ok";
        }));

        await T("append: chunks are concatenated in order; unknown ids are refused", InWork(async dir =>
        {
            var rec = new CsAgent.Services.AudioRecordings();
            var s = rec.Start("audio/webm")!.Value;
            Assert(await rec.AppendAsync(s.Id, Bytes("AAA")) && await rec.AppendAsync(s.Id, Bytes("BB")) && await rec.AppendAsync(s.Id, Bytes("C")), "append");
            Assert(File.ReadAllText(s.Path) == "AAABBC", "content: " + File.ReadAllText(s.Path));
            Assert(!await rec.AppendAsync("unknown", Bytes("x")) && !await rec.AppendAsync("../../etc/passwd", Bytes("x")), "unknown id");
            Assert(rec.PathOf(s.Id) == s.Path.Replace('/', Path.DirectorySeparatorChar) && rec.PathOf("unknown") is null, "PathOf");
            return "ok";
        }));

        await T("purge: deletes old audio only; recent audio, transcripts and foreign files stay", InWork(async dir =>
        {
            Directory.CreateDirectory("recordings"); Directory.CreateDirectory("transcripts");
            var old = DateTime.UtcNow.AddDays(-40);
            string Make(string path, DateTime when) { File.WriteAllText(path, "x"); File.SetLastWriteTimeUtc(path, when); return path; }
            var oldAudio = Make("recordings/rec_20260101_000000_aaaaaa.webm", old);
            var oldM4a = Make("recordings/rec_20260101_000001_bbbbbb.m4a", old);
            var recent = Make("recordings/rec_20261001_000000_cccccc.webm", DateTime.UtcNow.AddDays(-2));
            var foreign = Make("recordings/notes.txt", old);                       // not ours
            var foreignAudio = Make("recordings/meeting.webm", old);               // not named rec_*
            var transcript = Make("transcripts/rec_20260101_000000_aaaaaa.txt", old);

            var n = CsAgent.Services.AudioRecordings.PurgeOld(30);
            Assert(n == 2, "deleted: " + n);
            Assert(!File.Exists(oldAudio) && !File.Exists(oldM4a), "old audio deleted");
            Assert(File.Exists(recent), "recent audio kept");
            Assert(File.Exists(foreign) && File.Exists(foreignAudio), "files we did not create are kept");
            Assert(File.Exists(transcript), "transcript kept, even when old");
            Assert(CsAgent.Services.AudioRecordings.PurgeOld(30) == 0, "second run: nothing left to delete");
            await Task.CompletedTask;
            return "ok";
        }));

        await T("purge: no recordings folder, or a non-positive number of days, deletes nothing", InWork(async dir =>
        {
            Assert(CsAgent.Services.AudioRecordings.PurgeOld(30) == 0, "no folder");
            Directory.CreateDirectory("recordings");
            var f = "recordings/rec_20200101_000000_dddddd.webm"; File.WriteAllText(f, "x"); File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-900));
            Assert(CsAgent.Services.AudioRecordings.PurgeOld(0) == 0 && CsAgent.Services.AudioRecordings.PurgeOld(-5) == 0 && File.Exists(f), "0 / negative: nothing deleted");
            Assert(CsAgent.Services.AudioRecordings.PurgeOld(30, DateTime.UtcNow.AddDays(-1000)) == 0 && File.Exists(f), "a file newer than the limit stays");
            await Task.CompletedTask;
            return "ok";
        }));

        await T("purge: CSAGENT_AUDIO_KEEP_DAYS is off by default and ignores garbage", async () =>
        {
            var prev = Environment.GetEnvironmentVariable("CSAGENT_AUDIO_KEEP_DAYS");
            try
            {
                Environment.SetEnvironmentVariable("CSAGENT_AUDIO_KEEP_DAYS", null);
                Assert(CsAgent.Services.AudioRecordings.KeepDaysFromEnvironment() is null, "unset => keep everything");
                foreach (var bad in new[] { "", "abc", "0", "-3", "1.5" })
                {
                    Environment.SetEnvironmentVariable("CSAGENT_AUDIO_KEEP_DAYS", bad);
                    Assert(CsAgent.Services.AudioRecordings.KeepDaysFromEnvironment() is null, "accepted: '" + bad + "'");
                }
                Environment.SetEnvironmentVariable("CSAGENT_AUDIO_KEEP_DAYS", " 30 ");
                Assert(CsAgent.Services.AudioRecordings.KeepDaysFromEnvironment() == 30, "30");
            }
            finally { Environment.SetEnvironmentVariable("CSAGENT_AUDIO_KEEP_DAYS", prev); }
            await Task.CompletedTask;
            return "ok";
        });

        await T("append: a recording cannot grow past the limit", InWork(async dir =>
        {
            var rec = new CsAgent.Services.AudioRecordings(maxBytes: 10);
            var s = rec.Start("audio/webm")!.Value;
            Assert(await rec.AppendAsync(s.Id, Bytes("123456")), "first chunk");
            try { await rec.AppendAsync(s.Id, Bytes("7890123")); throw new Exception("no exception"); }
            catch (InvalidOperationException ex) { Assert(ex.Message.Contains("too large"), ex.Message); }
            Assert(new FileInfo(s.Path).Length == 6, "file unchanged by the refused chunk");
            return "ok";
        }));

        await T("transcript: saved next to the recording's name, accents intact; unknown id is an error", InWork(async dir =>
        {
            var rec = new CsAgent.Services.AudioRecordings();
            var s = rec.Start("audio/webm")!.Value;
            var t = rec.SaveTranscript(s.Id, "Bonjour, ça va très bien.");
            Assert(t.StartsWith("transcripts/rec_") && t.EndsWith(".txt"), t);
            Assert(Path.GetFileNameWithoutExtension(t) == Path.GetFileNameWithoutExtension(s.Path), "same stem");
            Assert(File.ReadAllText(t, Encoding.UTF8) == "Bonjour, ça va très bien.", "content");
            try { rec.SaveTranscript("unknown", "x"); throw new Exception("no exception"); }
            catch (InvalidOperationException) { }
            await Task.CompletedTask;
            return "ok";
        }));
    }
}
