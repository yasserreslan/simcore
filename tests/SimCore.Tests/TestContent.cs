using SimCore.Content;
using SimCore.Model;

namespace SimCore.Tests;

// TestContent loads the MVP content that ships with the engine.
internal static class TestContent
{
    private static readonly Lazy<ContentSet> MvpSet = new(() => ContentLoader.LoadDirectory(MvpDir));

    /// <summary>The repository's content/mvp directory.</summary>
    public static string MvpDir => Path.Combine(TestPaths.RepoRoot(), "content", "mvp");

    /// <summary>The compiled MVP content, loaded once.</summary>
    public static ContentSet Mvp => MvpSet.Value;

    /// <summary>The MVP content files, as they are on disk.</summary>
    public static List<ContentSource> MvpSources() =>
        [.. Directory.EnumerateFiles(MvpDir, "*.json", SearchOption.AllDirectories)
            .Select(p => new ContentSource(Path.GetRelativePath(MvpDir, p).Replace('\\', '/'), File.ReadAllText(p)))];
}
