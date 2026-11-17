using System.Runtime.CompilerServices;

namespace SpireLens.Core.Tests;

internal static class RepoPaths
{
    // Found from this file's compile-time path, not the test output folder: the CI suites
    // build the tests outside the checkout.
    public static string Root { get; } = Find();

    private static string Find([CallerFilePath] string file = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", ".."));
}
