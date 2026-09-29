namespace CsAgent.Core.Agent;

public static partial class ToolDispatcher
{
    private static string Sz(long b) =>
        b < 1024 ? $"{b} B" : b < 1_048_576 ? $"{b / 1024} KB" : $"{b / 1_048_576} MB";
}

