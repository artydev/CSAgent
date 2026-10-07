using System.Net;

namespace CsAgent.Core.Llm;

/// <summary>
/// Run-time LLM connection settings: endpoint, API key and vision model.
/// Defaults come from <see cref="LlmSettings"/>; they can be overridden with
/// <c>--endpoint</c> / <c>--vision-model</c> or the <c>CSAGENT_ENDPOINT</c> /
/// <c>CSAGENT_VISION_MODEL</c> environment variables. Any OpenAI-compatible
/// server works (Ollama: <c>http://localhost:11434/v1</c>).
/// </summary>
public static class LlmConfig
{
    public const string LocalPlaceholderKey = "local";

    public static string Endpoint { get; private set; } = LlmSettings.Endpoint;
    public static string VisionModel { get; private set; } = LlmSettings.VisionModel;

    /// <summary>Applies the overrides (null/blank keeps the current value).</summary>
    public static void Configure(string? endpoint, string? visionModel)
    {
        if (!string.IsNullOrWhiteSpace(endpoint)) Endpoint = endpoint.Trim().TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(visionModel)) VisionModel = visionModel.Trim();
    }

    /// <summary>Back to the built-in defaults (used by tests).</summary>
    public static void Reset()
    {
        Endpoint = LlmSettings.Endpoint;
        VisionModel = LlmSettings.VisionModel;
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
