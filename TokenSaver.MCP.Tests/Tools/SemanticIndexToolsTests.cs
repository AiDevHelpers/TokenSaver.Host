using TokenSaver.Common.Core.Configuration;
using TokenSaver.Database.Context;
using TokenSaver.TokenEconomy.Models.TokenEconomy;
using TokenSaver.TokenEconomy.Contracts;
using TokenSaver.TokenEconomy.Services;
using TokenSaver.MCP.Services;
using TokenSaver.MCP.Tools;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using System.Text.Json;

using Xunit;

namespace TokenSaver.MCP.Tests;

/// <summary>
///     Covers SemanticGrep's JSON-shape wiring only — the abstain/conditional-rerank decision
///     logic itself lives in <c>IConditionalRetrievalService</c> and is exercised at that level in
///     ConditionalRetrievalServiceTests (TokenSaver.Library.Tests). This file just checks that an
///     abstained <c>RetrievalOutcome</c> serializes to <c>{"match":"none", best_candidate,
///     confidence}</c> and a normal one to a plain hit array — the fix for the 100% negative-query
///     false-positive rate measured in docs/retrieval-benchmark/PRODUCTION-EVAL.md. IndexRepo
///     itself needs a live Ollama process and is exercised at the service level (RepoIndexerTests
///     in TokenSaver.Library.Tests), so this file only wires a no-op IRepoIndexer.
/// </summary>
public class SemanticIndexToolsTests
{
    private sealed class FakeConditionalRetrievalService : IConditionalRetrievalService
    {
        public RetrievalOutcome Outcome { get; set; } = new([], Abstained: false, BestCandidate: null, Confidence: null, Reranked: false);

        public Task<RetrievalOutcome> RetrieveAsync(
            string workspaceRoot, string query, string? pathFilter, int topK, CancellationToken ct = default) =>
            Task.FromResult(Outcome);
    }

    private sealed class NoOpRepoIndexer : IRepoIndexer
    {
        public Task<RepoIndexStats> IndexRepoAsync(
            string workspaceRoot, string[]? extensions = null, int concurrency = 4, CancellationToken ct = default) =>
            Task.FromResult(new RepoIndexStats(0, 0, 0, 0, TimeSpan.Zero));
    }

    private static SemanticIndexTools CreateTools(FakeConditionalRetrievalService retrieval)
    {
        TokenSaverConfig config = new() { WorkspaceRoot = Path.GetTempPath() };
        WorkspaceRootSessionResolver workspaceRootResolver = WorkspaceRootSessionResolverTestFactory.Create(config);
        NoOpRepoIndexer indexer = new();
        SemanticIndexBackgroundTrigger indexTrigger = new(
            indexer, new TokenEconomyDbAccessor(), NullLogger<SemanticIndexBackgroundTrigger>.Instance);
        return new SemanticIndexTools(indexer, retrieval, Options.Create(config), workspaceRootResolver, indexTrigger);
    }

    [Fact]
    public async Task SemanticGrep_AbstainedOutcome_ReturnsAbstainShape()
    {
        SemanticSearchHit best = new("Foo.cs", "method", "Foo", 1, 5, "does something", 0.6);
        FakeConditionalRetrievalService retrieval = new()
        {
            Outcome = new([], Abstained: true, BestCandidate: best, Confidence: 0.6, Reranked: false)
        };
        SemanticIndexTools tools = CreateTools(retrieval);

        string json = await tools.SemanticGrep("does this exist", rootPath: Path.GetTempPath());
        JsonElement result = JsonElement.Parse(json);

        Assert.Equal("none", result.GetProperty("match").GetString());
        Assert.Equal(0.6, result.GetProperty("confidence").GetDouble());
        Assert.Equal("Foo.cs", result.GetProperty("best_candidate").GetProperty("file").GetString());
    }

    [Fact]
    public async Task SemanticGrep_NonAbstainedOutcome_ReturnsHitArray()
    {
        SemanticSearchHit hit = new("Foo.cs", "method", "Foo", 1, 5, "does something", 0.9);
        FakeConditionalRetrievalService retrieval = new()
        {
            Outcome = new([hit], Abstained: false, BestCandidate: null, Confidence: null, Reranked: false)
        };
        SemanticIndexTools tools = CreateTools(retrieval);

        string json = await tools.SemanticGrep("find Foo", rootPath: Path.GetTempPath());
        JsonElement result = JsonElement.Parse(json);

        Assert.Equal(JsonValueKind.Array, result.ValueKind);
        Assert.Equal("Foo.cs", result[0].GetProperty("file").GetString());
    }

    [Fact]
    public async Task IndexRepo_ReturnsStartedImmediately_WithoutWaitingForTheRun()
    {
        SemanticIndexTools tools = CreateTools(new FakeConditionalRetrievalService());

        string json = await tools.IndexRepo(rootPath: Path.GetTempPath());
        JsonElement result = JsonElement.Parse(json);

        Assert.Equal("started", result.GetProperty("status").GetString());
    }

    [Fact]
    public async Task SemanticGrep_EmptyHitsNotAbstained_ReturnsEmptyArray()
    {
        FakeConditionalRetrievalService retrieval = new()
        {
            Outcome = new([], Abstained: false, BestCandidate: null, Confidence: null, Reranked: false)
        };
        SemanticIndexTools tools = CreateTools(retrieval);

        string json = await tools.SemanticGrep("anything", rootPath: Path.GetTempPath());
        JsonElement result = JsonElement.Parse(json);

        Assert.Equal(JsonValueKind.Array, result.ValueKind);
        Assert.Equal(0, result.GetArrayLength());
    }
}
