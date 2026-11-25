using AiDevTools.TokenSaver.OllamaIntegration.Services;
using AiDevTools.TokenSaver.WslControl.Contracts;

namespace AiDevTools.TokenSaver.MCP.Services;

/// <summary>
///     Binds <see cref="IWslCommandModel" /> (declared in AiDevTools.TokenSaver.WslControl) to the local
///     model transport (<see cref="OllamaService" />, backed by the SimpleModelServer/Ollama router).
///     This adapter lives in the host so the WslControl library carries no dependency on the
///     Ollama integration — it just asks "complete this prompt" and the host decides where.
/// </summary>
internal sealed class OllamaWslCommandModel(OllamaService ollama) : IWslCommandModel
{
    private readonly OllamaService _ollama = ollama;

    public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default) =>
        _ollama.CallModelAsync(_ollama.EffectiveModel, userPrompt, systemPrompt, ct);
}
