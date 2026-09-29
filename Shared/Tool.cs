using System.Text.Json.Serialization;

namespace CsAgent.Shared;

public record Tool(
    string Name,
    string Description,
    object InputSchema
);

[JsonSerializable(typeof(Tool[]))]
internal partial class AppJsonContext : JsonSerializerContext
{
}
