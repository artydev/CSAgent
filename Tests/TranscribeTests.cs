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

        await T("the tool is read-only and known to the model", Run(async (dir, mock) =>
        {
            Assert(!ToolDispatcher.IsDestructive("transcribe_audio"), "must not require confirmation");
            Assert(ToolDispatcher.ToolDefinitions.Any(t => t?["function"]?["name"]?.GetValue<string>() == "transcribe_audio"), "definition");
            await Task.CompletedTask;
            return "ok";
        }));
    }
}
