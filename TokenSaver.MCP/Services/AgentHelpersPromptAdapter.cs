using AiDevHelpers.AgentHelpers.Core.Contracts;
using AiDevHelpers.AgentHelpers.Core.Models;
using AiDevHelpers.AgentHelpers.Core.Services;

using TokenSaver.Common.Core.Configuration;
using TokenSaver.OllamaIntegration.Services;

using Microsoft.Extensions.Options;


namespace TokenSaver.MCP.Services;

/// <summary>
///     <see cref="IPromptAdapter" /> implementation backed by <see cref="AgentAdapterRegistry" />.
///     Resolves the appropriate adapter for the configured local model and applies its
///     full structuring (role-priming, output schema, self-check, token budget trimming).
///     Registered as a singleton in <c>Program.cs</c> so <see cref="OllamaService" />
///     and any other consumers receive adaptation automatically.
/// </summary>
internal sealed class AgentHelpersPromptAdapter(
    AgentAdapterRegistry registry,
    IOptions<TokenSaverConfig> config) : IPromptAdapter
{
    private readonly string _defaultModelHint = config.Value.OllamaModel ?? "HelperModel";
    private readonly AgentAdapterRegistry _registry = registry;

    public string Adapt(string prompt, string modelHint)
    {
        string hint = string.IsNullOrWhiteSpace(modelHint) ? _defaultModelHint : modelHint;
        CallerInfo caller = new() { ModelHint = hint.ToUpperInvariant(), Source = DetectionSource.OllamaConfig };
        IAgentAdapter adapter = _registry.Resolve(caller);
        ContextFormat format = adapter.GetFormat(caller);
        return adapter.AdaptContext(prompt, caller, format);
    }
}
