using CsAgent.Core.Llm;
using System.Text;
using CsAgent.Infrastructure.Clipboard;
using CsAgent.Presentation.LeanUI;
using CsAgent.Presentation.Tui;
using CsAgent.Presentation.Web;
using CsAgent.Shared;

namespace CsAgent;

public static class Program
{
    public const string Version = "0.8.0";

    [STAThread]
    public static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; }
        catch { }

        var parsed = ArgumentParser.Parse(args);

        LlmConfig.Configure(parsed.Endpoint, parsed.VisionModel);

        if (parsed.ShowHelp) { HelpDisplay.Show(Version); return 0; }
        if (parsed.ShowVersion) { Console.WriteLine($"CSAgent version {Version}"); return 0; }
        if (parsed.ShowDoc) { DocDisplay.Show(); return 0; }

        // Headless API mode: no clipboard, no browser, no UI assets.
        if (parsed.IsApiMode)
            return ApiHost.Run(parsed);

        using var clipboard = new WindowsClipboardMonitor();
        clipboard.Start();

        if (parsed.IsLeanUiMode)
            LeanUIHost.Run(parsed, clipboard);
        else if (parsed.IsUiMode)
            WebHost.Run(parsed, clipboard);
        else
            TuiHost.RunAsync(parsed, clipboard).GetAwaiter().GetResult();

        return 0;
    }
}
