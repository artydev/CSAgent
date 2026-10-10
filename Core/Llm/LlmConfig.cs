using System.Net;

namespace CsAgent.Core.Llm;

/// <summary>
/// Run-time LLM connection settings: endpoint, API key and the model profiles
/// (code, chat, vision). Defaults come from <see cref="LlmSettings"/>; they can be
/// overridden with <c>--endpoint</c> / <c>--vision-model</c> / <c>--code-model</c> /
/// <c>--chat-model</c> or the <c>CSAGENT_ENDPOINT</c> / <c>CSAGENT_VISION_MODEL</c> /
/// <c>CSAGENT_MODEL_CODE</c> / <c>CSAGENT_MODEL_CHAT</c> environment variables.
/// Any OpenAI-compatible server works (Ollama: <c>http://localhost:11434/v1</c>).
/// </summary>
public static class LlmConfig
{
    public const string LocalPlaceholderKey = "local";

    public static string Endpoint { get; private set; } = LlmSettings.Endpoint;
    public static string VisionModel { get; private set; } = LlmSettings.VisionModel;

    private static string? _codeModel;
    private static string? _chatModel;
    private static bool _visionExplicit;

    /// <summary>The code model set with --code-model / CSAGENT_MODEL_CODE, or null.</summary>
    public static string? ExplicitCodeModel => _codeModel;

    /// <summary>The chat model set with --chat-model / CSAGENT_MODEL_CHAT, or null.</summary>
    public static string? ExplicitChatModel => _chatModel;

    /// <summary>True when the vision model was set with --vision-model / CSAGENT_VISION_MODEL.</summary>
    public static bool VisionIsExplicit => _visionExplicit;

    /// <summary>Profile "code": the model for coding tasks (the default model unless overridden).</summary>
    public static string CodeModel => _codeModel ?? LlmSettings.Model;

    /// <summary>
    /// Profile "chat": the model for general conversation. Null means "no chat model": on a custom
    /// endpoint (Ollama...) the built-in Albert alias does not exist, so nothing is routed to it
    /// unless a chat model is set explicitly.
    /// </summary>
    public static string? ChatModel =>
        _chatModel ?? (Endpoint == LlmSettings.Endpoint ? LlmSettings.ChatModel : null);

    /// <summary>False with <c>--no-route</c> / <c>CSAGENT_ROUTING=off</c>: always use the code model.</summary>
    public static bool AutoRoute { get; private set; } = true;

    /// <summary>Applies the overrides (null/blank keeps the current value).</summary>
    public static void Configure(string? endpoint, string? visionModel,
        string? codeModel = null, string? chatModel = null, bool? autoRoute = null)
    {
        if (!string.IsNullOrWhiteSpace(endpoint)) Endpoint = endpoint.Trim().TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(visionModel)) { VisionModel = visionModel.Trim(); _visionExplicit = true; }
        if (!string.IsNullOrWhiteSpace(codeModel)) _codeModel = codeModel.Trim();
        if (!string.IsNullOrWhiteSpace(chatModel)) _chatModel = chatModel.Trim();
        if (autoRoute is { } r) AutoRoute = r;
    }

    /// <summary>Back to the built-in defaults (used by tests).</summary>
    public static void Reset()
    {
        Endpoint = LlmSettings.Endpoint;
        VisionModel = LlmSettings.VisionModel;
        _codeModel = null;
        _chatModel = null;
        _visionExplicit = false;
        AutoRoute = true;
    }

    /// <summary>True when the endpoint is this machine (localhost, 127.x, ::1).</summary>
    public static bool IsLocalEndpoint(string? endpoint = null)
    {
        if (!Uri.TryCreate(endpoint ?? Endpoint, UriKind.Absolute, out var uri)) return false;
        var h = uri.Host.Trim('[', ']');
        return h.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(h, out var ip) && IPAddress.IsLoopback(ip));
    }

    /// <summary>
    /// The key sent as "Authorization: Bearer". Reads ALBERT_API_KEY. A local
    /// endpoint (e.g. Ollama) needs no key, so a placeholder is used when none is set.
    /// Returns "" when a key is required but missing.
    /// </summary>
    public static string ResolveApiKey()
    {
        var key = Environment.GetEnvironmentVariable("ALBERT_API_KEY") ?? "";
        if (key.Length == 0 && IsLocalEndpoint()) return LocalPlaceholderKey;
        return key;
    }

    public static string MissingKeyMessage =>
        "ALBERT_API_KEY env var not set (not needed for a local --endpoint such as http://localhost:11434/v1).";
}
