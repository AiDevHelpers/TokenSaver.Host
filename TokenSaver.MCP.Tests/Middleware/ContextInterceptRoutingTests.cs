using System.Text;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using AiDevTools.TokenSaver.CodeHelper.Calibration;
using AiDevTools.TokenSaver.CodeHelper.Configuration;
using AiDevTools.TokenSaver.ContextHelper.Configuration;
using AiDevTools.TokenSaver.MCP.Middleware;

using Xunit;

namespace AiDevTools.TokenSaver.MCP.Tests;

/// <summary>
///     Which requests the intercept middleware touches at all. Every case here must reach the next middleware with the
///     body byte-for-byte unchanged: when both steps are off, for a tool outside the allowlist, for a non-POST, a path
///     other than /mcp, an empty prompt, or a body too large to buffer.
/// </summary>
public sealed class ContextInterceptRoutingTests
{
    private const string InterceptedTool = "build_adaptive_context";

    private static string Call(string tool, string prompt) =>
        "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool +
        "\",\"arguments\":{\"prompt\":\"" + prompt + "\"}}}";

    private static async Task<(bool Next, string Body)> RunAsync(
        string body,
        string method = "POST",
        string path = "/mcp",
        bool autoIntercept = false,
        bool autoEnabled = false,
        long? contentLength = null)
    {
        bool next = false;
        string forwarded = "";
        ContextInterceptMiddleware middleware = new(
            async ctx =>
            {
                next = true;
                using StreamReader reader = new(ctx.Request.Body, Encoding.UTF8);
                forwarded = await reader.ReadToEndAsync();
            },
            NullLogger<ContextInterceptMiddleware>.Instance);

        DefaultHttpContext context = new();
        context.RequestServices = new ServiceCollection().BuildServiceProvider();
        context.Request.Method = method;
        context.Request.Path = path;
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = contentLength ?? bytes.Length;

        // The cleaner, draft interceptor and instruction optimizer are not reached on these paths; calibration is
        // (it resolves whether the draft step is on), so a real one is supplied.
        CodeHelperConfig codeHelper = new() { AutoEnabled = autoEnabled };
        await middleware.InvokeAsync(
            context,
            Options.Create(codeHelper),
            Options.Create(new ContextHelperConfig { AutoIntercept = autoIntercept }),
            null!,
            null!,
            null!,
            new CalibrationService(Options.Create(codeHelper), NullLogger<CalibrationService>.Instance));

        return (next, forwarded);
    }

    [Fact]
    public async Task WhenBothStepsAreOff_AnAllowlistedCallIsForwardedUnchanged()
    {
        string body = Call(InterceptedTool, "add a method to Foo");

        (bool next, string forwarded) = await RunAsync(body);

        Assert.True(next);
        Assert.Equal(body, forwarded);
    }

    [Theory]
    [InlineData("model_route")]
    [InlineData("manage_plan")]
    [InlineData("edit_file")]
    [InlineData("code_work")]
    public async Task ToolsOutsideTheAllowlist_AreForwardedUnchangedEvenWithAPrompt(string tool)
    {
        string body = Call(tool, "implement a feature");

        (bool next, string forwarded) = await RunAsync(body, autoIntercept: true, autoEnabled: true);

        Assert.True(next);
        Assert.Equal(body, forwarded);
    }

    [Fact]
    public async Task ANonPostRequest_IsForwardedUnchanged()
    {
        string body = Call(InterceptedTool, "x");

        (bool next, string forwarded) = await RunAsync(body, method: "GET", autoIntercept: true);

        Assert.True(next);
        Assert.Equal(body, forwarded);
    }

    [Fact]
    public async Task ARequestOutsideMcp_IsForwardedUnchanged()
    {
        string body = Call(InterceptedTool, "x");

        (bool next, string forwarded) = await RunAsync(body, path: "/api/health", autoIntercept: true);

        Assert.True(next);
        Assert.Equal(body, forwarded);
    }

    [Fact]
    public async Task AnEmptyPrompt_IsForwardedUnchanged()
    {
        string body = Call(InterceptedTool, "   ");

        (bool next, string forwarded) = await RunAsync(body, autoIntercept: true);

        Assert.True(next);
        Assert.Equal(body, forwarded);
    }

    [Fact]
    public async Task ABodyLargerThanTheBufferCap_IsForwardedWithoutBuffering()
    {
        // The declared length alone is enough to skip interception (25 MB cap).
        string body = Call(InterceptedTool, "x");

        (bool next, string forwarded) = await RunAsync(body, autoIntercept: true, contentLength: 26L * 1024 * 1024);

        Assert.True(next);
        Assert.Equal(body, forwarded);
    }
}
