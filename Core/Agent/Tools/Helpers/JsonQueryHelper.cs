using System.Text;
using System.Text.Json.Nodes;

namespace CsAgent.Core.Agent;

public static partial class ToolDispatcher
{
    /// <summary>
    /// Resolves a dot-path query (e.g. "a.b[0].c") against a JSON node.
    /// Supports object properties and array indices. Returns null if not found.
    /// </summary>
    private static JsonNode? QueryJson(JsonNode node, string query)
    {
        var current = node;
        var token = new StringBuilder();

        for (int i = 0; i < query.Length; i++)
        {
            var ch = query[i];

            if (ch == '.')
            {
                if (token.Length > 0)
                {
                    current = Step(current, token.ToString());
                    if (current is null) return null;
                    token.Clear();
                }
            }
            else if (ch == '[')
            {
                if (token.Length > 0)
                {
                    current = Step(current, token.ToString());
                    if (current is null) return null;
                    token.Clear();
                }

                // Read the index until ']'.
                var idxEnd = query.IndexOf(']', i);
                if (idxEnd < 0) return null;
                var idxText = query[(i + 1)..idxEnd].Trim().Trim('"', '\'');
                if (!int.TryParse(idxText, out var idx)) return null;

                if (current is JsonArray arr)
                {
                    if (idx < 0 || idx >= arr.Count) return null;
                    current = arr[idx];
                }
                else
                {
                    return null;
                }

                i = idxEnd;
            }
            else
            {
                token.Append(ch);
            }
        }

        if (token.Length > 0)
        {
            current = Step(current, token.ToString());
            if (current is null) return null;
        }

        return current;
    }
    /// <summary>
    /// Steps one level into an object property or array index.
    /// </summary>
    private static JsonNode? Step(JsonNode node, string key)
    {
        if (node is JsonObject obj)
        {
            if (obj.TryGetPropertyValue(key, out var value)) return value;
            return null;
        }
        if (node is JsonArray arr && int.TryParse(key, out var idx))
        {
            if (idx >= 0 && idx < arr.Count) return arr[idx];
            return null;
        }
        return null;
    }
}
