using System.Text.Json;
using SimCore.Model;

namespace SimCore.Content;

/// <summary>
/// Thrown when content files are malformed or inconsistent. The message names
/// the file and the definition at fault.
/// </summary>
public sealed class ContentException(string message) : Exception(message);

/// <summary>One content file: its name (a path relative to the content root, with '/') and its JSON text.</summary>
/// <param name="Name">The name used in error messages.</param>
/// <param name="Json">The file's text.</param>
public readonly record struct ContentSource(string Name, string Json);

/// <summary>Loads content files and compiles them into a <see cref="ContentSet"/>.</summary>
/// <remarks>
/// Every <c>*.json</c> file under the root, in any subdirectory, is read. A
/// file is an object with any of the arrays <c>warheads</c>,
/// <c>archetypes</c>, <c>components</c> and <c>factions</c>; how definitions
/// are split across files does not matter. Unknown fields are errors, so
/// typos fail loudly. Comments and trailing commas are allowed.
/// </remarks>
public static class ContentLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>Loads every <c>*.json</c> file under dir.</summary>
    /// <exception cref="ContentException">The directory is missing or empty, or the content is invalid.</exception>
    public static ContentSet LoadDirectory(string dir)
    {
        if (!Directory.Exists(dir))
        {
            throw new ContentException($"content: directory not found: {dir}");
        }
        var sources = new List<ContentSource>();
        foreach (var path in Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories))
        {
            var name = Path.GetRelativePath(dir, path).Replace(Path.DirectorySeparatorChar, '/');
            sources.Add(new ContentSource(name, File.ReadAllText(path)));
        }
        if (sources.Count == 0)
        {
            throw new ContentException($"content: no .json files in {dir}");
        }
        return Load(sources);
    }

    /// <summary>Compiles content from in-memory files.</summary>
    /// <exception cref="ContentException">The content is invalid.</exception>
    public static ContentSet Load(IEnumerable<ContentSource> sources)
    {
        var files = new List<(string, ContentFile)>();
        foreach (var s in sources.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            ContentFile? f;
            try
            {
                f = JsonSerializer.Deserialize<ContentFile>(s.Json, JsonOptions);
            }
            catch (JsonException e)
            {
                throw new ContentException($"content: {s.Name}: invalid JSON: {e.Message}");
            }
            files.Add((s.Name, f ?? throw new ContentException($"content: {s.Name}: null document")));
        }
        return ContentCompiler.Compile(files);
    }
}
