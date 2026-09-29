using System.IO.Compression;

namespace CsAgent.Core.Agent;

public static partial class ToolDispatcher
{
    private static string Zip(string source, string destination)
    {
        try
        {
            var srcFull = Path.GetFullPath(source);
            var dstFull = Path.GetFullPath(destination);
            if (!IsSafePath(srcFull) || !IsSafePath(dstFull))
                return $"Error: zip - Paths must be inside the current working directory.";

            if (!File.Exists(srcFull) && !Directory.Exists(srcFull))
                return $"Error: zip - source not found '{srcFull}'";

            var dir = Path.GetDirectoryName(dstFull);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

            // Remove any pre-existing archive so creation is clean.
            if (File.Exists(dstFull)) File.Delete(dstFull);

            if (Directory.Exists(srcFull))
            {
                ZipFile.CreateFromDirectory(srcFull, dstFull, CompressionLevel.Optimal, includeBaseDirectory: false);
            }
            else
            {
                // Single file: create the archive and add the file as its only entry.
                using var archive = ZipFile.Open(dstFull, ZipArchiveMode.Create);
                archive.CreateEntryFromFile(srcFull, Path.GetFileName(srcFull), CompressionLevel.Optimal);
            }

            return $"OK: created archive '{dstFull}' ({Sz(new FileInfo(dstFull).Length)})";
        }
        catch (Exception ex) { return $"Error: zip — {ex.Message}"; }
    }
    private static string Unzip(string archive, string destination)
    {
        try
        {
            var arcFull = Path.GetFullPath(archive);
            var dstFull = Path.GetFullPath(destination);
            if (!IsSafePath(arcFull) || !IsSafePath(dstFull))
                return $"Error: unzip - Paths must be inside the current working directory.";

            if (!File.Exists(arcFull)) return $"Error: unzip - archive not found '{arcFull}'";

            Directory.CreateDirectory(dstFull);
            ZipFile.ExtractToDirectory(arcFull, dstFull, overwriteFiles: true);

            var count = Directory.EnumerateFiles(dstFull, "*", SearchOption.AllDirectories).Count();
            return $"OK: extracted '{arcFull}' -> '{dstFull}' ({count} file(s))";
        }
        catch (Exception ex) { return $"Error: unzip — {ex.Message}"; }
    }
}
