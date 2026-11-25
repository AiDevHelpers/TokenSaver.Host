using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace AiDevTools.TokenSaver.OllamaIntegration.Tests;

/// <summary>
///     In-process substitute for a live Ollama/SimpleModelServer endpoint — intercepts
///     <c>POST /api/generate</c> and <c>POST /api/embed</c> deterministically so RepoIndexer/
///     SemanticSearchService can be exercised end-to-end (including their writes/reads against the
///     real Chunks table) without a running model. This is the "fake HTTP transport" pattern, not a
///     live-Ollama test — no network call ever leaves the process.
/// </summary>
#pragma warning disable CA1515 // Shared test fake used by sibling test projects; must stay public.
public sealed class FakeOllamaHandler : HttpMessageHandler
{
    public IList<string> GeneratePrompts { get; } = [];
    public IList<string> EmbedInputs { get; } = [];

    /// <summary>Maps a /api/generate prompt to the summary text returned. Falls back to echoing a fixed string.</summary>
    public Func<string, string> GenerateResponder { get; set; } = _ => "summary";

    /// <summary>Maps a /api/embed input string to its embedding vector. Falls back to a hash-derived vector.</summary>
    public Func<string, float[]> EmbedResponder { get; set; } = DefaultEmbed;

    public static float[] DefaultEmbed(string input)
    {
        int hash = input.GetHashCode(StringComparison.Ordinal);
        return [(hash % 1000) / 1000f, ((hash / 1000) % 1000) / 1000f, ((hash / 1_000_000) % 1000) / 1000f];
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string path = request.RequestUri?.AbsolutePath ?? "";
        string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        JsonDocument doc = JsonDocument.Parse(body);

        if (path == "/api/generate")
        {
            string prompt = doc.RootElement.GetProperty("prompt").GetString() ?? "";
            GeneratePrompts.Add(prompt);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { response = GenerateResponder(prompt) })
            };
        }

        if (path == "/api/embed")
        {
            string input = doc.RootElement.GetProperty("input").GetString() ?? "";
            EmbedInputs.Add(input);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { embeddings = new[] { EmbedResponder(input) } })
            };
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}
