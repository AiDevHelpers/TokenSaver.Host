using System.Reflection;
using System.Text.RegularExpressions;

using ModelContextProtocol.Server;

using Xunit;

namespace AiDevTools.TokenSaver.MCP.Tests.Tools;

/// <summary>
///     Every chat-facing MCP tool method must be called by at least one test in this project.
///     A method counts as covered when its name appears as a call (<c>.Name(</c>) in a test source file.
/// </summary>
public sealed class ToolCoverageTests
{
    [Fact]
    public void Every_McpServerTool_HasATestThatCallsIt()
    {
        var toolNames = typeof(AiDevTools.TokenSaver.MCP.Services.InstructionStoreService).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .Select(m => m.Name)
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        string testSource = ReadTestSources();

        List<string> untested = toolNames
            .Where(name => !Regex.IsMatch(testSource, @"\." + Regex.Escape(name) + @"\s*\("))
            .ToList();

        Assert.True(untested.Count == 0,
            $"{untested.Count} MCP tool method(s) have no test: {string.Join(", ", untested)}");
    }

    private static string ReadTestSources()
    {
        // Walk up from the test binary to the source tree. CallerFilePath is not used: CI's deterministic
        // path mapping rewrites it (D:\a\...\TokenSaver -> /_/), so it does not point at the checkout.
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "TokenSaver.MCP.Tests", "TokenSaver.MCP.Tests.csproj")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        if (dir is null)
        {
            throw new InvalidOperationException("AiDevTools.TokenSaver.MCP.Tests source tree not found above the test binary.");
        }
        dir = Path.Combine(dir, "TokenSaver.MCP.Tests");

        IEnumerable<string> files = Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                     && !f.EndsWith("ToolCoverageTests.cs", StringComparison.Ordinal));

        return string.Join('\n', files.Select(File.ReadAllText));
    }
}
