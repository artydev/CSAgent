using CsAgent.Core.Abstractions;
using CsAgent.Shared;

namespace CsAgent.Presentation.Tui;

/// <summary>
/// Output of <c>--prompt</c> when its result is meant to be piped or captured by a script:
/// standard output carries only the assistant's final answer, as plain text.
/// Steps, tool calls and results, model and token lines, the banner and the routine memory notices
/// are not written at all. Failures (a failed tool call, a warning, a danger notice, an error) go to
/// standard error, so they never end up in the next command's input. <see cref="HadError"/> tells the
/// caller to exit with a non-zero code. <c>--verbose</c> brings the full display back.
/// </summary>
public sealed class ScriptObserver : IAgentObserver
{
    private readonly TextWriter _out;
    private readonly TextWriter _err;
    private readonly bool _canAsk;
    private readonly Func<string?> _readLine;
    private string? _text;
    private string _lastTool = "tool";

    /// <param name="canAsk">True when a person can answer on the keyboard (input not redirected).</param>
    public ScriptObserver(TextWriter output, TextWriter error, bool canAsk, Func<string?>? readLine = null)
    {
        _out = output; _err = error; _canAsk = canAsk;
        _readLine = readLine ?? Console.ReadLine;
    }

    public bool HadError { get; private set; }

    public Task OnStep(int n, int max) { _text = null; return Task.CompletedTask; }
    public Task OnThought(string text) { _text = text; return Task.CompletedTask; }

    public Task OnToolCall(string name, string args) { _lastTool = name; return Task.CompletedTask; }

    public Task OnToolResult(string result, bool isError)
    {
        if (isError) _err.WriteLine($"warning: {_lastTool} failed: {FirstLine(result)}");
        return Task.CompletedTask;
    }

    public Task OnDone(string message)
    {
        if (!string.IsNullOrWhiteSpace(_text)) _out.WriteLine(_text.TrimEnd());
        _text = null;
        return Task.CompletedTask;
    }

    public Task OnError(string message) { HadError = true; _err.WriteLine($"error: {message}"); return Task.CompletedTask; }

    public Task OnWarning(string message)
    {
        if (!IsRoutineNotice(message)) _err.WriteLine($"warning: {message}");
        return Task.CompletedTask;
    }

    public Task OnDanger(string message) { _err.WriteLine($"danger: {message}"); return Task.CompletedTask; }

    public Task<bool> OnConfirm(string toolName)
    {
        // Input is the pipe (or a file): nobody can answer, so the action is declined, never guessed.
        if (!_canAsk)
        {
            _err.WriteLine($"declined: '{toolName}' needs confirmation and there is no keyboard to ask (input is redirected). Use --yes to allow tool calls.");
            return Task.FromResult(false);
        }
        _err.Write($"Allow destructive action '{toolName}'? [y/N] ");
        var answer = _readLine()?.Trim();
        return Task.FromResult(answer is not null && (answer.Equals("y", StringComparison.OrdinalIgnoreCase)
                                                      || answer.Equals("yes", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Status lines about the session summary that succeeded: nothing a script needs.</summary>
    internal static bool IsRoutineNotice(string m) =>
        m.StartsWith("Session summary updated", StringComparison.Ordinal)
        || m.StartsWith("Session summary unchanged", StringComparison.Ordinal)
        || m.StartsWith("Session summary loaded", StringComparison.Ordinal);

    private static string FirstLine(string s)
    {
        var line = s.Split('\n')[0].Trim();
        return line.Length > 200 ? line[..200] + "..." : line;
    }
}
