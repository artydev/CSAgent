// Stand-in for the app's Program (excluded from this project): McpClient reads Program.Version.
//
// These tests are plain code (no test framework, no NuGet package), so Visual Studio's
// Test Explorer does not list them. Run them as a console program instead:
//   * command line : dotnet run --project Tests -c Release
//   * Visual Studio: right-click CsAgent.Tests > Set as Startup Project, then F5 / Ctrl+F5.
public static class Program
{
    public const string Version = "test";

    public static async Task<int> Main()
    {
        var code = await CsAgent.Tests.Tests.RunAll();

        // Started from Visual Studio with F5 the console closes at once: wait for a key.
        if (System.Diagnostics.Debugger.IsAttached && !Console.IsInputRedirected)
        {
            Console.WriteLine("\nPress any key to close...");
            Console.ReadKey(intercept: true);
        }
        return code;
    }
}
