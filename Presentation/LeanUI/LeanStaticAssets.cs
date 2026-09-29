using System.Reflection;

namespace CsAgent.Presentation.LeanUI;

public static class LeanStaticAssets
{
    public static string HtmlUI => LoadEmbeddedResource("CsAgent.src.Presentation.LeanUI.assets.index.html");
    public static string JsUI => LoadEmbeddedResource("CsAgent.src.Presentation.LeanUI.assets.app.js");
    public static string CssUI => LoadEmbeddedResource("CsAgent.src.Presentation.LeanUI.assets.styles.css");

    private static string LoadEmbeddedResource(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            throw new FileNotFoundException($"Embedded resource not found: {resourceName}");
            
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
