using TokenSaver.CodeHelper.Indexing;
using TokenSaver.Common.Core.Configuration;
using TokenSaver.MCP.Services;
using TokenSaver.MCP.Tools;

using Microsoft.Extensions.Options;

using Xunit;

namespace TokenSaver.MCP.Tests.Tools;

public sealed class IndexChangeToolsTests
{
    private static IndexChangeTools CreateTools(TokenSaverConfig config) =>
        new(index: null!, new IndexChangeBuffer(), WorkspaceRootSessionResolverTestFactory.Create(config), Options.Create(config));

    [Fact]
    public async Task IndexFilesChanged_NoPaths_ReturnsNoPathsMessageWithoutIndexing()
    {
        IndexChangeTools tools = CreateTools(new TokenSaverConfig { WorkspaceRoot = Path.GetTempPath() });

        string result = await tools.IndexFilesChanged(Array.Empty<string>());

        Assert.Equal("No paths given.", result);
    }

    [Fact]
    public async Task IndexFilesChanged_NullPaths_ReturnsNoPathsMessage()
    {
        IndexChangeTools tools = CreateTools(new TokenSaverConfig { WorkspaceRoot = Path.GetTempPath() });

        string result = await tools.IndexFilesChanged(null!);

        Assert.Equal("No paths given.", result);
    }
}
