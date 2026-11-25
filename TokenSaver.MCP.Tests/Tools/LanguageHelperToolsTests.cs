using AiDevTools.TokenSaver.Common.Core.Configuration;
using AiDevTools.TokenSaver.LanguageHelpers.Core.Contracts;
using AiDevTools.TokenSaver.LanguageHelpers.Core.Models;
using AiDevTools.TokenSaver.MCP.Tools;

using Microsoft.Extensions.Options;

using Xunit;

namespace AiDevTools.TokenSaver.MCP.Tests.Tools;

/// <summary>
///     Covers LanguageHelperTools on its argument-resolution and unknown-operation branches. A fake
///     registry returns a fake helper whose analysis methods throw, so no dotnet, pwsh or ruff process
///     is ever started. The branches that run a real analyzer are not reached by these tests.
/// </summary>
public sealed class LanguageHelperToolsTests
{
    private const string Root = "F:\\workspace";

    private static LanguageHelperTools CreateTools(ILanguageHelperRegistry registry)
    {
        TokenSaverConfig cfg = new() { WorkspaceRoot = Root };
        return new LanguageHelperTools(
            registry,
            Options.Create(cfg),
            WorkspaceRootSessionResolverTestFactory.Create(cfg));
    }

    [Fact]
    public async Task CheckCode_EmptyPath_ReturnsPathRequired()
    {
        LanguageHelperTools tools = CreateTools(new FakeRegistry());

        string result = await tools.CheckCode("syntax", "  ");

        Assert.Equal("A 'path' (file or directory) is required.", result);
    }

    [Theory]
    [InlineData("../elsewhere/x.cs")]
    [InlineData("..")]
    public async Task CheckCode_PathOutsideTheWorkspace_IsRefused(string path)
    {
        LanguageHelperTools tools = CreateTools(new FakeRegistry());

        string result = await tools.CheckCode("lint", path, language: "csharp");

        Assert.Contains("outside the workspace root", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckCode_UnknownForcedLanguage_ReturnsUnknownLanguage()
    {
        LanguageHelperTools tools = CreateTools(new FakeRegistry());

        string result = await tools.CheckCode("lint", "src", language: "cobol");

        Assert.Equal("Unknown language 'cobol'. Use 'csharp', 'powershell', or 'python'.", result);
    }

    [Fact]
    public async Task CheckCode_ExtensionNotRecognized_ReturnsCouldNotDetect()
    {
        LanguageHelperTools tools = CreateTools(new FakeRegistry { Helper = null });

        string result = await tools.CheckCode("syntax", "notes.txt");

        Assert.StartsWith("Could not detect the language from 'notes.txt'.", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckCode_ForcedLanguageWithoutHelper_ReturnsNotRegistered()
    {
        LanguageHelperTools tools = CreateTools(new FakeRegistry { Helper = null });

        string result = await tools.CheckCode("syntax", "src", language: "python");

        Assert.Equal("No language helper is registered for Python.", result);
    }

    [Fact]
    public async Task CheckCode_UnknownOperation_ReturnsUnknownOperationWithoutRunningHelper()
    {
        LanguageHelperTools tools = CreateTools(new FakeRegistry { Helper = new FakeHelper() });

        string result = await tools.CheckCode("explode", "Program.cs");

        Assert.Equal("Unknown operation 'explode'. Use 'syntax' or 'lint'.", result);
    }

    [Fact]
    public async Task FixCode_UnknownOperation_ReturnsUnknownOperationWithoutRunningHelper()
    {
        LanguageHelperTools tools = CreateTools(new FakeRegistry { Helper = new FakeHelper() });

        string result = await tools.FixCode("rewrite", "Program.cs");

        Assert.StartsWith("Unknown operation 'rewrite'.", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FixCode_EmptyPath_ReturnsPathRequired()
    {
        LanguageHelperTools tools = CreateTools(new FakeRegistry { Helper = new FakeHelper() });

        string result = await tools.FixCode("format", string.Empty);

        Assert.Equal("A 'path' (file or directory) is required.", result);
    }

    private sealed class FakeRegistry : ILanguageHelperRegistry
    {
        public ILanguageHelper? Helper { get; set; }

        public IReadOnlyCollection<ILanguageHelper> Helpers => Helper is null ? [] : [Helper];

        public ILanguageHelper? Resolve(LanguageId language) => Helper;

        public ILanguageHelper? TryGetForFile(string filePath) => Helper;
    }

    private sealed class FakeHelper : ILanguageHelper
    {
        public LanguageId Language => LanguageId.CSharp;
        public IReadOnlyCollection<string> FileExtensions => [".cs"];
        public bool CanHandle(string filePath) => filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

        public Task<EnableAnalysisResult> EnableCodeAnalysisAsync(AnalysisScope scope, CodeStandard? standard = null, CancellationToken ct = default) =>
            throw new NotSupportedException("fake helper must not run analysis in these tests");

        public Task<SyntaxCheckReport> RunSyntaxCheckerAsync(AnalysisScope scope, CancellationToken ct = default) =>
            throw new NotSupportedException("fake helper must not run analysis in these tests");

        public Task<FixResult> FixSyntaxAsync(AnalysisScope scope, CancellationToken ct = default) =>
            throw new NotSupportedException("fake helper must not run analysis in these tests");

        public Task<ImportFixResult> FixImportsAsync(AnalysisScope scope, CancellationToken ct = default) =>
            throw new NotSupportedException("fake helper must not run analysis in these tests");

        public Task<NamespaceFixResult> FixNamespaceAsync(AnalysisScope scope, CancellationToken ct = default) =>
            throw new NotSupportedException("fake helper must not run analysis in these tests");

        public Task<LintReport> RunLintAsync(AnalysisScope scope, LintOptions? options = null, CancellationToken ct = default) =>
            throw new NotSupportedException("fake helper must not run analysis in these tests");

        public Task<FormatResult> FormatCodeAsync(AnalysisScope scope, FormatOptions? options = null, CancellationToken ct = default) =>
            throw new NotSupportedException("fake helper must not run analysis in these tests");
    }
}
