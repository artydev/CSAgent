using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: DoNotParallelize]

namespace CsAgent.Tests;

/// <summary>
/// Test Explorer entry point. Every test written in the *Tests.cs files is listed here as its own row
/// ("Group | test name"); the same tests also run from the console (dotnet run --project Tests).
/// </summary>
[TestClass]
public class MemoryTestSuite
{
    public static IEnumerable<object[]> Cases =>
        Tests.Discover().Select(c => new object[] { c.Id });

    public static string DisplayName(MethodInfo method, object?[]? data) =>
        data is { Length: > 0 } ? (string)data[0]! : method.Name;

    [TestMethod]
    [DynamicData(nameof(Cases), DynamicDataDisplayName = nameof(DisplayName))]
    public async Task Run(string id) => await Tests.RunCase(id);

    [AssemblyCleanup]
    public static void Cleanup() => Tests.CleanupTemp();
}
