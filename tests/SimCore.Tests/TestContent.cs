using SimCore.Content;
using SimCore.Model;

namespace SimCore.Tests;

// TestContent loads the sample content that ships with the engine.
internal static class TestContent
{
    private static readonly Lazy<ContentSet> SampleSet = new(() => ContentLoader.LoadDirectory(SampleDir));

    /// <summary>The repository's content/sample directory.</summary>
    public static string SampleDir => Path.Combine(TestPaths.RepoRoot(), "content", "sample");

    /// <summary>The compiled sample content, loaded once.</summary>
    public static ContentSet Sample => SampleSet.Value;

    /// <summary>The sample content files, as they are on disk.</summary>
    public static List<ContentSource> SampleSources() =>
        [.. Directory.EnumerateFiles(SampleDir, "*.json", SearchOption.AllDirectories)
            .Select(p => new ContentSource(Path.GetRelativePath(SampleDir, p).Replace('\\', '/'), File.ReadAllText(p)))];
}
