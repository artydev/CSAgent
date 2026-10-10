namespace CsAgent.Core.Llm;

public static class LlmSettings
{
    public const string Endpoint = "https://albert.api.etalab.gouv.fr/v1";

    /// <summary>Profile "code": the default model for coding tasks.</summary>
    public const string Model = "deepseek-v4-flash";

    /// <summary>
    /// Profile "chat": general conversation without code or files. An Albert alias (stable across
    /// model versions). Only used on the default Albert endpoint, unless set explicitly.
    /// </summary>
    public const string ChatModel = "openweight-large";

    /// <summary>Profile "vision": used when the conversation contains an image.</summary>
    public const string VisionModel = "gemma-4-31b-it";

    public const string TranscribeModel = "openai/whisper-large-v3";
}
