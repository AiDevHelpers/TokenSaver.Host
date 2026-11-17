#pragma warning disable CA2000 // Test factories hand disposables to the code under test; each test is short-lived.
using System.Text;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using TokenSaver.CodeHelper.Configuration;
using TokenSaver.ContextHelper.Configuration;
using TokenSaver.MCP.Middleware;

using Xunit;

namespace TokenSaver.MCP.Tests;

/// <summary>
///     Malformed or non-tool-call JSON-RPC bodies must pass through to the next middleware
///     untouched instead of throwing. The service parameters are never reached on these paths.
/// </summary>
public sealed class ContextInterceptMiddlewareTests
{
    [Theory]
    [InlineData("[1,2,3]")]
    [InlineData("\"just a string\"")]
    [InlineData("not json at all")]
    [InlineData("{\"method\":5}")]
    [InlineData("{\"method\":\"tools/list\"}")]
    [InlineData("{\"method\":\"tools/call\",\"params\":[]}")]
    [InlineData("{\"method\":\"tools/call\",\"params\":{\"name\":7}}")]
    [InlineData("{\"method\":\"tools/call\",\"params\":{\"name\":\"x\",\"arguments\":{\"prompt\":42}}}")]
    public async Task InvokeAsync_PassesMalformedOrIrrelevantBodiesThrough(string body)
    {
        bool nextCalled = false;
        ContextInterceptMiddleware middleware = new(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            NullLogger<ContextInterceptMiddleware>.Instance);

        DefaultHttpContext context = new();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/mcp";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));

        await middleware.InvokeAsync(
            context,
            Options.Create(new CodeHelperConfig { AutoEnabled = true }),
            Options.Create(new ContextHelperConfig()),
            null!,
            null!,
            null!);

        Assert.True(nextCalled);
    }

    [Fact]
    public void AsString_ReturnsOnlyStringValues()
    {
        Assert.Equal("a", ContextInterceptMiddleware.AsString(JsonValue.Create("a")));
        Assert.Null(ContextInterceptMiddleware.AsString(JsonValue.Create(5)));
        Assert.Null(ContextInterceptMiddleware.AsString(new JsonArray()));
        Assert.Null(ContextInterceptMiddleware.AsString(new JsonObject()));
        Assert.Null(ContextInterceptMiddleware.AsString(null));
    }
}
