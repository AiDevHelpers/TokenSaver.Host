#pragma warning disable CA2000 // Test factories hand disposables to the code under test; each test is short-lived.
using System.Text;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using AiDevTools.TokenSaver.CodeHelper.Configuration;
using AiDevTools.TokenSaver.ContextHelper.Configuration;
using AiDevTools.TokenSaver.MCP.Middleware;

using Xunit;

namespace AiDevTools.TokenSaver.MCP.Tests;

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
            null!,
            null!);

        Assert.True(nextCalled);
    }

    [Theory]
    [InlineData("model_route")]
    [InlineData("manage_plan")]
    [InlineData("edit_file")]
    [InlineData("code_work")]
    [InlineData("clean_and_chat")]
    public async Task InvokeAsync_ForwardsToolsOutsideTheAllowlistUntouched(string tool)
    {
        string body = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool +
                      "\",\"arguments\":{\"prompt\":\"implement a new method in Foo.cs\"}}}";
        bool nextCalled = false;
        string? forwarded = null;
        ContextInterceptMiddleware middleware = new(
            async ctx =>
            {
                nextCalled = true;
                using StreamReader reader = new(ctx.Request.Body);
                forwarded = await reader.ReadToEndAsync();
            },
            NullLogger<ContextInterceptMiddleware>.Instance);

        DefaultHttpContext context = new();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/mcp";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));

        // Every service is null: an allowlisted tool would touch them, so reaching next proves
        // the tool was left alone (and nothing could have written a file or answered the call).
        await middleware.InvokeAsync(
            context,
            Options.Create(new CodeHelperConfig { AutoEnabled = true }),
            Options.Create(new ContextHelperConfig { AutoIntercept = true }),
            null!,
            null!,
            null!,
            null!);

        Assert.True(nextCalled);
        Assert.Equal(body, forwarded);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Body.Length);
    }

    [Theory]
    [InlineData("build_adaptive_context", true)]
    [InlineData("BUILD_ADAPTIVE_CONTEXT", true)]
    [InlineData("model_route", false)]
    [InlineData("manage_plan", false)]
    [InlineData("code_work", false)]
    [InlineData(null, false)]
    public void ShouldIntercept_IsAnOptInAllowlist(string? tool, bool expected)
    {
        Assert.Equal(expected, ContextInterceptMiddleware.ShouldIntercept(tool));
    }

    [Fact]
    public void DraftInterceptionIsOffByDefault()
    {
        Assert.False(new CodeHelperConfig().AutoEnabled);
        Assert.NotEqual(true, AiDevTools.TokenSaver.Config.CodeHelperBehaviorConfig.Defaults().AutoEnabled);
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
