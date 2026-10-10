using CsAgent.Core.Llm;

namespace CsAgent.Shared;

/// <summary>
/// The two command-line helpers of the routing rules: <c>--init-routing</c> writes the LLMRoutingRules
/// folder, <c>--explain-routing "message"</c> shows which model a message would get. Neither calls a model.
/// The output goes to a writer, so tests can read it.
/// </summary>
public static class RoutingCli
{
    /// <summary>
    /// Creates the LLMRoutingRules folder (in <paramref name="folder"/>, or in the working directory) with
    /// models.json, keywords.json, rules.json, keywords.defaults.json and a README. A file that already
    /// exists is never overwritten, except keywords.defaults.json and README.md, which are references.
    /// </summary>
    public static int Init(TextWriter o, string? folder = null)
    {
        folder ??= Path.Combine(Directory.GetCurrentDirectory(), RoutingRules.FolderName);
        try
        {
            Directory.CreateDirectory(folder);
            WriteIfMissing(o, folder, RoutingRules.ModelsFile, RoutingRules.ModelsTemplate());
            WriteIfMissing(o, folder, RoutingRules.KeywordsFile, RoutingRules.KeywordsTemplate());
            WriteIfMissing(o, folder, RoutingRules.RulesFile, RoutingRules.RulesTemplate());
            Write(o, folder, RoutingRules.DefaultsFile, RoutingRules.DefaultsReference(), Loc.T("refreshed"));
            Write(o, folder, "README.md", RoutingRules.ReadmeTemplate(), Loc.T("refreshed"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            o.WriteLine(Loc.F($"Error: cannot write the routing rules in '{folder}': {ex.Message}"));
            return 1;
        }

        o.WriteLine();
        o.WriteLine(Loc.F($"Routing rules folder: {folder}"));
        o.WriteLine(Loc.T("Edit models.json, keywords.json and rules.json; changes apply to the next message."));
        o.WriteLine(Loc.T("Try a message without calling any model:  csagent --explain-routing \"open in Edge browser\""));
        return 0;
    }

    private static void WriteIfMissing(TextWriter o, string folder, string file, string content)
    {
        var path = Path.Combine(folder, file);
        if (File.Exists(path)) { o.WriteLine(Loc.F($"  kept       {file} (already exists)")); return; }
        File.WriteAllText(path, content, new System.Text.UTF8Encoding(false));
        o.WriteLine(Loc.F($"  created    {file}"));
    }

    private static void Write(TextWriter o, string folder, string file, string content, string verb)
    {
        File.WriteAllText(Path.Combine(folder, file), content, new System.Text.UTF8Encoding(false));
        o.WriteLine(Loc.F($"  {verb,-10} {file}"));
    }

    /// <summary>Shows the model a message would get, and why. Returns 2 when there is no message.</summary>
    public static int Explain(TextWriter o, string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            o.WriteLine(Loc.T("Usage: csagent --explain-routing \"<message>\""));
            return 2;
        }

        var rules = RoutingRules.Current();
        var choice = ModelRouter.Choose(message, needsVision: false, modelOverride: null, previousTurnUsedTools: false, rules);

        o.WriteLine(Loc.F($"Message : {message}"));
        o.WriteLine(Loc.F($"Rules   : {(rules.Source == "built-in" ? Loc.T("built-in (no LLMRoutingRules folder found)") : rules.Source)}"));
        o.WriteLine(Loc.F($"Model   : {choice.Model}"));
        o.WriteLine(Loc.F($"Profile : {choice.Profile.ToString().ToLowerInvariant()}"));
        o.WriteLine(Loc.F($"Reason  : {choice.Reason}"));
        foreach (var w in rules.Warnings) o.WriteLine(Loc.F($"Warning : {w}"));
        if (!LlmConfig.AutoRoute) o.WriteLine(Loc.T("Note    : automatic routing is off (--no-route or CSAGENT_ROUTING=off): the code model is always used."));
        o.WriteLine();
        o.WriteLine(Loc.T("(A chat choice is also checked against the endpoint's model list when a run starts; a message that"));
        o.WriteLine(Loc.T(" follows a tool-using turn or contains an image can be routed differently.)"));
        return 0;
    }
}
