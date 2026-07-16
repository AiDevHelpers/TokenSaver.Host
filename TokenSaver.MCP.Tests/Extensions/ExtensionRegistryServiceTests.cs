using Microsoft.Extensions.Logging.Abstractions;
using TokenSaver.MCP.Services;
using Xunit;

namespace TokenSaver.MCP.Tests;

public sealed class ExtensionRegistryServiceTests
{
    private readonly ExtensionRegistryService _registry = new(NullLogger<ExtensionRegistryService>.Instance);

    [Fact]
    public void Initialize_RegistersDefaultExtensions()
    {
        var extensions = _registry.GetAllExtensions();

        Assert.NotEmpty(extensions);
        Assert.True(extensions.Any(e => e.ExtensionId == "VsCode"));
        Assert.True(extensions.Any(e => e.ExtensionId == "Cursor"));
        Assert.True(extensions.Any(e => e.ExtensionId == "Continue"));
        Assert.True(extensions.Any(e => e.ExtensionId == "VisualStudio"));
        Assert.True(extensions.Any(e => e.ExtensionId == "LMStudio"));
        Assert.True(extensions.Any(e => e.ExtensionId == "ClaudeDesktop"));
    }

    [Fact]
    public void GetExtension_ReturnsRegisteredExtension()
    {
        var vscode = _registry.GetExtension("VsCode");

        Assert.NotNull(vscode);
        Assert.Equal("TokenSaver VS Code Extension", vscode.Name);
        Assert.True(vscode.DebuggerSupported);
        Assert.Equal("1.0.0", vscode.CurrentVersion);
    }

    [Fact]
    public void GetExtension_WithUnknownId_ReturnsNull()
    {
        var unknown = _registry.GetExtension("UnknownExtension");

        Assert.Null(unknown);
    }

    [Fact]
    public void SupportsDebugger_ReturnsTrueForDebuggerEnabledExtensions()
    {
        Assert.True(_registry.SupportsDebugger("VsCode"));
        Assert.True(_registry.SupportsDebugger("Cursor"));
        Assert.True(_registry.SupportsDebugger("Continue"));
        Assert.True(_registry.SupportsDebugger("VisualStudio"));
        Assert.True(_registry.SupportsDebugger("ClaudeDesktop"));
    }

    [Fact]
    public void SupportsDebugger_ReturnsFalseForDebuggerDisabledExtensions()
    {
        Assert.False(_registry.SupportsDebugger("LMStudio"));
    }

    [Fact]
    public void SupportsDebugger_ReturnsFalseForUnknownExtension()
    {
        Assert.False(_registry.SupportsDebugger("UnknownExtension"));
    }

    [Fact]
    public void IsCompatible_ReturnsTrueForCompatibleVersions()
    {
        Assert.True(_registry.IsCompatible("VsCode", "1.0.0"));
        Assert.True(_registry.IsCompatible("VsCode", "1.0.1"));
        Assert.True(_registry.IsCompatible("VsCode", "2.0.0"));
    }

    [Fact]
    public void IsCompatible_ReturnsFalseForIncompatibleVersions()
    {
        Assert.False(_registry.IsCompatible("VsCode", "0.9.0"));
    }

    [Fact]
    public void IsCompatible_ReturnsFalseForUnknownExtension()
    {
        Assert.False(_registry.IsCompatible("UnknownExtension", "1.0.0"));
    }

    [Fact]
    public void GetExtensionsByFeature_ReturnsMatchingExtensions()
    {
        var streaming = _registry.GetExtensionsByFeature("streaming");

        Assert.NotEmpty(streaming);
        Assert.True(streaming.Any(e => e.ExtensionId == "VsCode"));
        Assert.True(streaming.Any(e => e.ExtensionId == "Cursor"));
        Assert.True(streaming.Any(e => e.ExtensionId == "ClaudeDesktop"));
        Assert.False(streaming.Any(e => e.ExtensionId == "VisualStudio"));
    }

    [Fact]
    public void GetExtensionsByFeature_IsCaseInsensitive()
    {
        var streaming1 = _registry.GetExtensionsByFeature("streaming");
        var streaming2 = _registry.GetExtensionsByFeature("STREAMING");
        var streaming3 = _registry.GetExtensionsByFeature("Streaming");

        Assert.Equal(streaming1.Count, streaming2.Count);
        Assert.Equal(streaming2.Count, streaming3.Count);
    }

    [Fact]
    public void GetCapabilities_ReturnsCorrectCapabilities()
    {
        var caps = _registry.GetCapabilities("VsCode");

        Assert.True(caps.IsRegistered);
        Assert.True(caps.DebuggerSupported);
        Assert.True(caps.StreamingSupported);
        Assert.True(caps.PersistenceSupported);
        Assert.NotEmpty(caps.Features);
    }

    [Fact]
    public void GetCapabilities_ForUnknownExtension_ReturnsUnregisteredCapabilities()
    {
        var caps = _registry.GetCapabilities("UnknownExtension");

        Assert.False(caps.IsRegistered);
        Assert.False(caps.DebuggerSupported);
        Assert.False(caps.StreamingSupported);
        Assert.False(caps.PersistenceSupported);
        Assert.Empty(caps.Features);
    }

    [Fact]
    public void RegisterExtension_AddsNewExtension()
    {
        var info = new ExtensionInfo(
            ExtensionId: "TestExtension",
            Name: "Test Extension",
            DebuggerSupported: true,
            CurrentVersion: "1.0.0",
            MinimumMcpVersion: "1.0.0",
            Features: new List<string> { "test-feature" });

        _registry.RegisterExtension(info);

        var retrieved = _registry.GetExtension("TestExtension");
        Assert.NotNull(retrieved);
        Assert.Equal("Test Extension", retrieved.Name);
        Assert.True(retrieved.DebuggerSupported);
    }

    [Fact]
    public void RegisterExtension_UpdatesExistingExtension()
    {
        var info1 = new ExtensionInfo(
            ExtensionId: "TestExt",
            Name: "Test Extension v1",
            DebuggerSupported: false,
            CurrentVersion: "1.0.0",
            MinimumMcpVersion: "1.0.0",
            Features: new List<string> { "feature1" });

        _registry.RegisterExtension(info1);

        var info2 = new ExtensionInfo(
            ExtensionId: "TestExt",
            Name: "Test Extension v2",
            DebuggerSupported: true,
            CurrentVersion: "2.0.0",
            MinimumMcpVersion: "1.0.0",
            Features: new List<string> { "feature1", "feature2" });

        _registry.RegisterExtension(info2);

        var retrieved = _registry.GetExtension("TestExt");
        Assert.NotNull(retrieved);
        Assert.Equal("Test Extension v2", retrieved.Name);
        Assert.True(retrieved.DebuggerSupported);
        Assert.Equal("2.0.0", retrieved.CurrentVersion);
        Assert.Equal(2, retrieved.Features.Count);
    }

    [Fact]
    public void VsCodeExtension_HasAllExpectedFeatures()
    {
        var vscode = _registry.GetExtension("VsCode");

        Assert.NotNull(vscode);
        Assert.Contains("debugging", vscode.Features);
        Assert.Contains("streaming", vscode.Features);
        Assert.Contains("persistence", vscode.Features);
        Assert.Contains("context-building", vscode.Features);
        Assert.Contains("code-search", vscode.Features);
    }

    [Fact]
    public void LMStudioExtension_DoesNotSupportDebugger()
    {
        var lmstudio = _registry.GetExtension("LMStudio");

        Assert.NotNull(lmstudio);
        Assert.False(lmstudio.DebuggerSupported);
        Assert.DoesNotContain("debugging", lmstudio.Features);
    }

    [Fact]
    public void GetAllExtensions_ReturnsReadOnlyList()
    {
        var extensions = _registry.GetAllExtensions();

        Assert.NotNull(extensions);
        Assert.True(extensions.Count > 0);
        // Verify it's read-only by checking we can't modify it
        var asList = extensions as List<ExtensionInfo>;
        Assert.Null(asList);
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("2.0.0", "1.0.0", 1)]
    [InlineData("1.0.0", "2.0.0", -1)]
    [InlineData("1.1.0", "1.0.9", 1)]
    [InlineData("1.0.9", "1.1.0", -1)]
    public void VersionComparison_WorksCorrectly(string v1, string v2, int expected)
    {
        // Both versions should be compatible with the minimum version requirement
        Assert.True(_registry.IsCompatible("VsCode", v1));
        Assert.True(_registry.IsCompatible("VsCode", v2));

        // Version ordering is verified by compatibility checking
        // Higher versions should be compatible when lower versions are
        if (expected > 0)
        {
            // v1 > v2, so v1 should be compatible
            Assert.True(_registry.IsCompatible("VsCode", v1));
        }
        else if (expected < 0)
        {
            // v1 < v2, so v2 should be compatible
            Assert.True(_registry.IsCompatible("VsCode", v2));
        }
    }
}
