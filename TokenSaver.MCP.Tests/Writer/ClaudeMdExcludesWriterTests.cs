using AiDevTools.TokenSaver.MCP.Services;

using Microsoft.Extensions.Logging.Abstractions;

using System.Text.Json.Nodes;

using Xunit;

namespace AiDevTools.TokenSaver.MCP.Tests;

public sealed class ClaudeMdExcludesWriterTests : IDisposable
{
    private readonly string _workspaceRoot = Directory.CreateTempSubdirectory("cmewTests-").FullName;

    private string SettingsPath => Path.Combine(_workspaceRoot, ".claude", "settings.local.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspaceRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void EnsureExcluded_NoExistingFile_CreatesFileWithArray()
    {
        ClaudeMdExcludesWriter.EnsureExcluded(_workspaceRoot, [@"C:\global\CLAUDE.md"], NullLogger.Instance);

        Assert.True(File.Exists(SettingsPath));
        JsonNode root = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        JsonArray excludes = root["claudeMdExcludes"]!.AsArray();
        Assert.Single(excludes);
        Assert.Equal(@"C:\global\CLAUDE.md", excludes[0]!.GetValue<string>());
    }

    [Fact]
    public void EnsureExcluded_ExistingFileWithOtherKeys_PreservesThemAndMergesArray()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, """{"permissions":{"allow":["Bash(git *)"]},"claudeMdExcludes":["C:\\already\\CLAUDE.md"]}""");

        ClaudeMdExcludesWriter.EnsureExcluded(_workspaceRoot, [@"C:\global\CLAUDE.md"], NullLogger.Instance);

        JsonNode root = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        Assert.Equal("Bash(git *)", root["permissions"]!["allow"]![0]!.GetValue<string>());
        JsonArray excludes = root["claudeMdExcludes"]!.AsArray();
        Assert.Equal(2, excludes.Count);
        Assert.Contains(excludes, n => n!.GetValue<string>() == @"C:\already\CLAUDE.md");
        Assert.Contains(excludes, n => n!.GetValue<string>() == @"C:\global\CLAUDE.md");
    }

    [Fact]
    public void EnsureExcluded_PathAlreadyPresent_LeavesFileUntouched()
    {
        ClaudeMdExcludesWriter.EnsureExcluded(_workspaceRoot, [@"C:\global\CLAUDE.md"], NullLogger.Instance);
        DateTime firstWriteTimeUtc = File.GetLastWriteTimeUtc(SettingsPath);

        ClaudeMdExcludesWriter.EnsureExcluded(_workspaceRoot, [@"C:\global\CLAUDE.md"], NullLogger.Instance);

        Assert.Equal(firstWriteTimeUtc, File.GetLastWriteTimeUtc(SettingsPath));
    }

    [Fact]
    public void EnsureExcluded_EmptyPathsList_DoesNotCreateFile()
    {
        ClaudeMdExcludesWriter.EnsureExcluded(_workspaceRoot, [], NullLogger.Instance);

        Assert.False(File.Exists(SettingsPath));
    }
}
