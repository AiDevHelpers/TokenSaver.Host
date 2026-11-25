using Microsoft.AspNetCore.Http;

using AiDevTools.TokenSaver.Common.Core.Configuration;
using AiDevTools.TokenSaver.MCP.Middleware;
using AiDevTools.TokenSaver.MCP.Services;

using Xunit;

namespace AiDevTools.TokenSaver.MCP.Tests;

public sealed class WorkspaceScopeTests
{
    [Fact]
    public void OutsideARequest_ConfigAnswersLastResolvedRootThenConfiguredDefault()
    {
        TokenSaverConfig config = new() { WorkspaceRoot = "configured" };
        Assert.Equal("configured", config.WorkspaceRoot);

        config.RecordResolvedRoot("resolved");
        Assert.Equal("resolved", config.WorkspaceRoot);
    }

    [Fact]
    public async Task InsideARequest_AnUnresolvedSessionNeverSeesAnotherClientsRoot()
    {
        TokenSaverConfig config = new() { WorkspaceRoot = "configured" };
        config.RecordResolvedRoot("someone-elses-root");

        string seen = await Task.Run(() =>
        {
            _ = WorkspaceScope.Begin(null);
            return config.WorkspaceRoot;
        });

        Assert.Equal("configured", seen);
    }

    [Fact]
    public async Task InsideARequest_TheSessionRootWinsOverTheConfiguredDefault()
    {
        TokenSaverConfig config = new() { WorkspaceRoot = "configured" };

        string seen = await Task.Run(() =>
        {
            _ = WorkspaceScope.Begin("session-root");
            return config.WorkspaceRoot;
        });

        Assert.Equal("session-root", seen);
    }

    [Fact]
    public async Task Middleware_SeedsTheScopeFromTheSessionHeaderAndForgetsOnDelete()
    {
        WorkspaceSessionRoots roots = new();
        roots.Set("s1", "root-1");
        string? seenFirst = null;
        string? seenUnknown = null;
        string? current = null;
        WorkspaceScopeMiddleware middleware = new(_ =>
        {
            current = WorkspaceScope.Current?.Root;
            return Task.CompletedTask;
        });

        DefaultHttpContext known = new();
        known.Request.Headers[WorkspaceScopeMiddleware.SessionHeader] = "s1";
        known.Request.Method = HttpMethods.Post;
        await middleware.InvokeAsync(known, roots);
        seenFirst = current;

        DefaultHttpContext unknown = new();
        unknown.Request.Headers[WorkspaceScopeMiddleware.SessionHeader] = "other";
        unknown.Request.Method = HttpMethods.Post;
        await middleware.InvokeAsync(unknown, roots);
        seenUnknown = current;

        Assert.Equal("root-1", seenFirst);
        Assert.Null(seenUnknown);

        DefaultHttpContext end = new();
        end.Request.Headers[WorkspaceScopeMiddleware.SessionHeader] = "s1";
        end.Request.Method = HttpMethods.Delete;
        await middleware.InvokeAsync(end, roots);

        Assert.Null(roots.Get("s1"));
    }
}
