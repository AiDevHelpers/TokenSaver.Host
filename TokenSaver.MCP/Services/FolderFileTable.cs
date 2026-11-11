namespace TokenSaver.MCP.Services;

/// <summary>One row of the folder/file index: metadata plus cross-links into the code and semantic indexes.</summary>
public sealed record FolderFileRow(
    string Path,
    string Kind,
    string Title,
    string Summary,
    string CodeKey,
    string? SemanticKey);

/// <summary>
///     Builds the generated folder/file table (metadata only). Rendering is deterministic.
/// </summary>
public static class FolderFileTable
{
    private const string PendingSemantic = "sem:pending";

    private static readonly HashSet<string> SkippedFolders =
        new(StringComparer.OrdinalIgnoreCase) { "archive", "build", "bin", "obj", "node_modules", "TestResults" };

    /// <summary>Renders the rows as a markdown table, sorted by path.</summary>
    public static string Render(IEnumerable<FolderFileRow> rows)
    {
        var lines = new List<string>
        {
            "| Path | Kind | Title | Summary | Code | Semantic |",
            "| --- | --- | --- | --- | --- | --- |",
        };

        foreach (FolderFileRow row in rows.OrderBy(r => r.Path, StringComparer.Ordinal))
        {
            lines.Add(string.Concat(
                "| ", Cell(row.Path),
                " | ", Cell(row.Kind),
                " | ", Cell(row.Title),
                " | ", Cell(row.Summary),
                " | ", Cell(row.CodeKey),
                " | ", Cell(row.SemanticKey ?? PendingSemantic),
                " |"));
        }

        return string.Join('\n', lines) + "\n";
    }

    /// <summary>Collects folders and instruction/doc files under <paramref name="root" />. Bodies are never kept.</summary>
    public static IReadOnlyList<FolderFileRow> Collect(string root)
    {
        var rows = new List<FolderFileRow>();

        foreach (string dir in Directory.EnumerateDirectories(root))
        {
            string name = Path.GetFileName(dir);
            if (name.StartsWith('.') || SkippedFolders.Contains(name))
            {
                continue;
            }

            rows.Add(new FolderFileRow(name + "/", "folder", name, string.Empty, "code:" + name, null));
        }

        var files = new List<string>();
        AddIfExists(files, Path.Combine(root, "CLAUDE.md"));
        AddAll(files, Path.Combine(root, ".claude", "rules"), "*.md", SearchOption.TopDirectoryOnly);
        AddAll(files, Path.Combine(root, "docs"), "*.md", SearchOption.AllDirectories);
        AddAll(files, Path.Combine(root, "instructions", "skills"), "SKILL.md", SearchOption.AllDirectories);

        foreach (string file in files)
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            DocMetadata meta = DocMetadataExtractor.Extract(File.ReadAllText(file));
            rows.Add(new FolderFileRow(relative, "file", meta.Title, meta.Summary, "code:" + relative, null));
        }

        return rows;
    }

    /// <summary>Writes <paramref name="content" /> to <paramref name="path" /> only when it differs. Returns true when written.</summary>
    public static bool WriteIfChanged(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content)
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return true;
    }

    private static void AddIfExists(List<string> files, string path)
    {
        if (File.Exists(path))
        {
            files.Add(path);
        }
    }

    private static void AddAll(List<string> files, string dir, string pattern, SearchOption option)
    {
        if (Directory.Exists(dir))
        {
            files.AddRange(Directory.EnumerateFiles(dir, pattern, option));
        }
    }

    private static string Cell(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\n', ' ');
}
