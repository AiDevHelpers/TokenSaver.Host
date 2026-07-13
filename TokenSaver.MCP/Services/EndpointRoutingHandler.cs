using TokenSaver.Common.Models;
using TokenSaver.OllamaIntegration.Services;


namespace TokenSaver.MCP.Services;

/// <summary>
///     DelegatingHandler that routes every request through <see cref="OllamaEndpointRegistry" />
///     instead of a fixed HttpClient.BaseAddress. Registered on all typed HttpClients that talk
///     to Ollama (OllamaService, DraftModelCapacityClient, PromptAssessor, TaskClassifier, and the
///     context pipeline stages) so a single registry/config change (multiple <c>Endpoints</c>,
///     failover/round-robin/explicit selection) affects every caller without touching their code.
///     Each typed client still gets a BaseAddress at registration (pointing at the legacy/default
///     OllamaUrl) purely as a fallback for the rare case the registry has no endpoints; in normal
///     operation this handler always overwrites scheme/host/port with the selected endpoint's URL
///     before the request is sent.
///     Callers that want a specific endpoint (e.g. CleanAndChat's optional endpointName parameter)
///     attach it via <see cref="HttpRequestOptionsKey" /> on the request; this handler reads it and
///     passes it through to <see cref="OllamaEndpointRegistry.SelectEndpoint" />.
/// </summary>
internal sealed class EndpointRoutingHandler(OllamaEndpointRegistry registry) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _ = request.Options.TryGetValue(OllamaRequestOptions.EndpointName, out string? requestedEndpoint);
        OllamaEndpointStatus endpoint = registry.SelectEndpoint(requestedEndpoint);

        Uri endpointBaseUri = new(endpoint.Url, UriKind.Absolute);
        Uri? originalUri = request.RequestUri;

        // Rewrite scheme/host/port to the selected endpoint while preserving the
        // path+query the caller built off their client's (fallback) BaseAddress.
        if (originalUri is not null)
        {
            UriBuilder builder = new(endpointBaseUri)
            {
                Path = originalUri.AbsolutePath,
                Query = originalUri.Query.TrimStart('?')
            };
            request.RequestUri = builder.Uri;
        }

        try
        {
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                registry.RecordSuccess(endpoint.Name);
            }
            else
            {
                registry.RecordFailure(endpoint.Name);
            }

            return response;
        }
        catch (HttpRequestException ex)
        {
            registry.RecordFailure(endpoint.Name);
            throw new HttpRequestException(
                $"Endpoint '{endpoint.Name}' ({endpointBaseUri}) appears to be down: {ex.Message}", ex);
        }
        catch (Exception)
        {
            registry.RecordFailure(endpoint.Name);
            throw;
        }
    }
}
