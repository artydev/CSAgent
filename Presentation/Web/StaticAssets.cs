using System.Reflection;

namespace CsAgent.Presentation.Web;

public static class StaticAssets
{
    public static string HtmlUI => LoadEmbeddedResource("CsAgent.src.Presentation.Web.assets.index.html");
    public static string JsUI => LoadEmbeddedResource("CsAgent.src.Presentation.Web.assets.app.js");
    public static string CssUI => LoadEmbeddedResource("CsAgent.src.Presentation.Web.assets.styles.css");

    // The README is embedded from the project root (README.md)
    public static string ReadmeMd => LoadEmbeddedResource("CsAgent.README.md");
    
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
