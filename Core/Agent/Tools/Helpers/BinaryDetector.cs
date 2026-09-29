namespace CsAgent.Core.Agent;

public static partial class ToolDispatcher
{
    /// <summary>
    /// Detects whether a file is binary by scanning the first 8 KB for null bytes
    /// or a high proportion of non-printable characters.
    /// </summary>
    private static bool IsBinaryFile(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[Math.Min(fs.Length, 8192)];
            int read = fs.Read(buffer, 0, buffer.Length);

            int nullCount = 0;
            int nonPrintableCount = 0;
            for (int i = 0; i < read; i++)
            {
                if (buffer[i] == 0) nullCount++;
                else if (buffer[i] < 9 || (buffer[i] > 13 && buffer[i] < 32)) nonPrintableCount++;
            }

            // Binary if there are null bytes or a high ratio of control characters.
            if (nullCount > 0) return true;
            if (read > 0 && (double)nonPrintableCount / read > 0.30) return true;
            return false;
        }
        catch
        {
            return true; // If we can't read it, treat as binary to be safe.
        }
    }
}
