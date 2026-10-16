namespace SimCore.Tests;

internal static class TestPaths
{
    /// <summary>The testdata directory copied next to the test assembly.</summary>
    public static string TestData(string name) => Path.Combine(AppContext.BaseDirectory, "testdata", name);

    /// <summary>The repository root: the nearest ancestor holding SimCore.sln.</summary>
    public static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "SimCore.sln")))
            {
                return d.FullName;
            }
        }
        throw new DirectoryNotFoundException("SimCore.sln not found above " + AppContext.BaseDirectory);
    }
}
