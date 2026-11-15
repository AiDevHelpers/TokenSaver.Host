namespace TokenSaver.MCP.Services;

/// <summary>
///     Read-only drift checks for the global instruction layers: source (<c>claude_rules</c>),
///     installed (<c>~/.claude</c>) and the instruction store. Never edits any of them.
/// </summary>
internal static class GlobalInstructionCheck
{
    /// <summary>Returns human-readable problems; empty when all layers agree.</summary>
    public static IReadOnlyList<string> Check(string sourceRoot, string installedRoot, string storeRoot)
    {
        var problems = new List<string>();

        string sourceRules = Path.Combine(sourceRoot, "rules");
        if (Directory.Exists(sourceRules))
        {
            foreach (string src in Directory.EnumerateFiles(sourceRules, "*.md"))
            {
                string name = Path.GetFileName(src);
                string installed = Path.Combine(installedRoot, "rules", name);
                if (!File.Exists(installed))
                {
                    problems.Add($"rules/{name} is missing from the installed layer; run install.ps1.");
                }
                else if (File.ReadAllText(src) != File.ReadAllText(installed))
                {
                    problems.Add($"rules/{name} differs from source; run install.ps1.");
                }
            }
        }

        if (Directory.Exists(storeRoot))
        {
            foreach (string dir in Directory.EnumerateDirectories(storeRoot))
            {
                string folder = Path.GetFileName(dir);
                string file = Path.Combine(dir, "SKILL.md");
                if (!File.Exists(file))
                {
                    continue;
                }

                (string? name, _) = InstructionStoreService.ParseFrontmatter(File.ReadAllText(file));
                if (name != folder)
                {
                    problems.Add($"store skill '{folder}' has frontmatter name '{name ?? "(none)"}'; name must match the folder.");
                }
            }
        }

        string installedSkills = Path.Combine(installedRoot, "skills");
        if (Directory.Exists(installedSkills))
        {
            foreach (string dir in Directory.EnumerateDirectories(installedSkills))
            {
                string folder = Path.GetFileName(dir);
                string file = Path.Combine(dir, "SKILL.md");
                if (File.Exists(file) && !File.ReadAllText(file).Contains("get_instruction", StringComparison.Ordinal))
                {
                    problems.Add($"installed skill stub '{folder}' does not point at get_instruction; reinstall the stub.");
                }
            }
        }

        return problems;
    }
}
