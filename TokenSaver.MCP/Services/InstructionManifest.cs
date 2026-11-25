namespace AiDevTools.TokenSaver.MCP.Services;

/// <summary>One sub-doc entry recorded in <see cref="InstructionManifest" /> — mirrors <see cref="DiscoveredSubDoc" /> minus the content, which lives alongside the manifest on disk.</summary>
internal sealed record InstructionManifestSubDoc(
    string RelativeSourcePath,
    string Slug,
    IReadOnlyList<string> PathGlobs,
    IReadOnlyList<string> TaskTypes);

/// <summary>
///     Persisted at <c>.tokensaver/context/instructions/manifest.json</c> — see
///     <see cref="InstructionCacheService" />. <see cref="SourceHash" /> is the single field that
///     decides whether a cache regeneration is needed; everything else is metadata for consumers.
/// </summary>
internal sealed record InstructionManifest
{
    public string SourceHash { get; init; } = "";
    public DateTimeOffset GeneratedAtUtc { get; init; }
    public IReadOnlyList<InstructionManifestSubDoc> SubDocs { get; init; } = [];
}
