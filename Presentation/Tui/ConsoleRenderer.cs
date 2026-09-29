using System.Text.Json;
using System.Text.Json.Nodes;

namespace CsAgent.Presentation.Tui;

/// <summary>
/// Console rendering helpers for the TUI interface.
/// </summary>
public static class UI
{
    public static void Banner()
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine();
        Console.WriteLine(@"   ██████╗███████╗ █████╗  ██████╗ ███████╗███╗   ██╗████████╗");
        Console.WriteLine(@"  ██╔════╝██╔════╝██╔══██╗██╔════╝ ██╔════╝████╗  ██║╚══██╔══╝");
        Console.WriteLine(@"  ██║     ███████╗███████║██║  ███╗█████╗  ██╔██╗ ██║   ██║   ");
        Console.WriteLine(@"  ██║     ╚════██║██╔══██║██║   ██║██╔══╝  ██║╚██╗██║   ██║   ");
        Console.WriteLine(@"  ╚██████╗███████║██║  ██║╚██████╔╝███████╗██║ ╚████║   ██║   ");
        Console.WriteLine(@"   ╚═════╝╚══════╝╚═╝  ╚═╝ ╚═════╝ ╚══════╝╚═╝  ╚═══╝   ╚═╝  ");
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  Cross-platform autonomous coding agent  |  zero NuGet deps");
        Console.WriteLine();
        Console.ResetColor();
    }

    public static void Step(int n, int max)
    {
        Console.ForegroundColor = ConsoleColor.DarkMagenta;
        Console.WriteLine($"\n-- Step {n}/{max} ----------------------------------------");
        Console.ResetColor();
    }

    public static void AssistantText(string text)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("\n+- ASSISTANT --------------------------------------------------");
        foreach (var line in text.Split('\n'))
            Console.WriteLine($"|  {line}");
        Console.WriteLine("+--------------------------------------------------------------");
        Console.ResetColor();
    }

    public static void ToolCall(string name, string args)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"\n+- TOOL CALL: {name}");

        try
        {
            var json = JsonNode.Parse(args);
            if (json is JsonObject obj)
            {
                foreach (var kvp in obj)
                {
                    var val = kvp.Value?.ToString() ?? "";
                    if (val.Length > 200)
                        val = val[..200] + $"... ({val.Length} chars total)";
                    Console.WriteLine($"|   {kvp.Key}: {val}");
                }
            }
            else
            {
                foreach (var line in args.Split('\n'))
                    Console.WriteLine($"|  {line}");
            }
        }
        catch
        {
            foreach (var line in args.Split('\n'))
                Console.WriteLine($"|  {line}");
        }

        Console.WriteLine("+--------------------------------------------------------------");
        Console.ResetColor();
    }

    public static void ToolResult(string result, bool isError)
    {
        Console.ForegroundColor = isError ? ConsoleColor.Red : ConsoleColor.DarkGray;
        Console.WriteLine("\n+- RESULT -----------------------------------------------------");
        var lines = result.Split('\n');
        foreach (var line in lines.Take(30))
            Console.WriteLine($"|  {line}");
        if (lines.Length > 30)
            Console.WriteLine($"|  ... ({lines.Length - 30} more lines)");
        Console.WriteLine("+--------------------------------------------------------------");
        Console.ResetColor();
    }

    public static void Success(string msg)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\n✓ {msg}");
        Console.ResetColor();
    }

    public static void Error(string msg)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"\n✗ {msg}");
        Console.ResetColor();
    }

    public static void Warning(string msg)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"\n⚠ {msg}");
        Console.ResetColor();
    }

    public static void Danger(string msg)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"\n☠ {msg}");
        Console.ResetColor();
    }

    public static void Info(string msg)
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine($"ℹ {msg}");
        Console.ResetColor();
    }

    public static bool Confirm(string question)
    {
        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.Write($"? {question} [Y/n] ");
        Console.ResetColor();
        var answer = Console.ReadLine()?.Trim().ToUpperInvariant();
        return answer is null or "" or "Y";
    }

    public static string ReadPassword(string prompt)
    {
        Console.Write(prompt);
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace && sb.Length > 0)
                sb.Remove(sb.Length - 1, 1);
            else if (key.Key != ConsoleKey.Backspace)
                sb.Append(key.KeyChar);
        }
        Console.WriteLine();
        return sb.ToString();
    }
}
