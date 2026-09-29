namespace CsAgent.Core.Agent;

public static partial class ToolDispatcher
{
    private static bool IsSafePath(string fullPath)
    {
        try
        {
            var currentDir = Directory.GetCurrentDirectory();
            var normalizedCurrent = Path.GetFullPath(currentDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedPath = Path.GetFullPath(fullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return normalizedPath.StartsWith(normalizedCurrent, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
