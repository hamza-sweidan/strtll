using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nse.Tests.Support;

/// <summary>Where the test data lives, and throwaway story folders to work in.</summary>
public static class Fixtures
{
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string HarbourLetter => Path.Combine(RepoRoot, "tests", "Nse.Tests", "Fixtures", "harbour-letter");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Nse.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Cannot find the repository root from the test directory.");
    }

    public static JsonElement Json(string name) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(HarbourLetter, name))).RootElement;

    public static JsonNode Node(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(HarbourLetter, name)))!;

    public static string Sha256OfFile(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>A new, empty folder under the system temp directory. The caller deletes it.</summary>
    public static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "nse-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Copy a story folder, without the engine's cache. The index under .nse is rebuilt wherever it is missing, so
    /// a copy has no use for it; and whoever else is reading the folder at this moment, another test or someone
    /// trying the command line on the example, may be halfway through replacing it, so that the file listed a
    /// moment ago is gone by the time it is copied.
    /// </summary>
    public static void CopyDirectory(string from, string to)
    {
        bool Cache(string path) => Path.GetRelativePath(from, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains(".nse");
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories).Where(d => !Cache(d)))
            Directory.CreateDirectory(dir.Replace(from, to));
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories).Where(f => !Cache(f)))
            File.Copy(file, file.Replace(from, to), overwrite: true);
    }

    public static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    /// <summary>A story folder holding the example manuscript and an empty ledger.</summary>
    public static Story NewHarbourStory(out string root)
    {
        root = NewTempDir();
        var story = Story.Init(root);
        CopyDirectory(Path.Combine(HarbourLetter, "manuscript"), story.ManuscriptDir);
        return story;
    }
}
