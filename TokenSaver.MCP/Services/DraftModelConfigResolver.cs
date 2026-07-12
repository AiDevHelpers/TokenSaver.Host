using TokenSaver.Common.Core.Configuration;

using System.Text.Json;
using System.Text.Json.Nodes;

namespace TokenSaver.MCP.Services;

/// <summary>
///     Resolves the effective Ollama/DraftModel router endpoint for this process, by reading
///     DraftModel.Loader's own ProgramData config file when installed as a Windows service,
///     and synthesizes a matching named endpoint entry so <c>OllamaEndpointRegistry</c> can
///     select it like any other configured endpoint. Pulled out of <c>Program.cs</c> so the
///     composition root only calls one method instead of embedding this parsing/synthesis
///     logic inline.
/// </summary>
internal static class DraftModelConfigResolver
{
    private const string DraftModelLoaderEndpointName = "draftmodel-loader";

    /// <summary>
    ///     If <see cref="TokenSaverConfig.DraftModelServiceInstalled" /> is set, reads
    ///     DraftModel.Loader's ProgramData config (JSONC — comments/trailing commas allowed)
    ///     and, when found, updates <paramref name="config" />.OllamaUrl to the discovered
    ///     router host/port and adds a "draftmodel-loader" entry to
    ///     <paramref name="config" />.Endpoints (unless already present). Any failure to read
    ///     or parse the file is swallowed and the caller falls through to its already-configured
    ///     OllamaUrl — this is a best-effort auto-discovery, not a hard requirement.
    /// </summary>
    public static void ApplyIfInstalled(TokenSaverConfig config)
    {
        if (!config.DraftModelServiceInstalled)
        {
            return;
        }

        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string draftModelConfigPath = Path.Combine(programData, "DraftModel", "DraftModel.config.json");
        if (!File.Exists(draftModelConfigPath))
        {
            return;
        }

        try
        {
            JsonObject? global = ReadGlobalSection(draftModelConfigPath);
            if (global is null)
            {
                return;
            }

            string host = global["routerHost"]?.GetValue<string>() ?? "localhost";
            int port = global["routerPort"]?.GetValue<int>() ?? 11435;
            config.OllamaUrl = $"http://{host}:{port}";

            AddSynthesizedEndpointIfMissing(config);
        }
        catch
        {
            /* fall through to configured OllamaUrl */
        }
    }

    private static JsonObject? ReadGlobalSection(string draftModelConfigPath)
    {
        JsonDocumentOptions docOpts = new()
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
        JsonObject? root = JsonNode.Parse(File.ReadAllText(draftModelConfigPath), null, docOpts)?.AsObject();
        return root?["global"]?.AsObject();
    }

    /// <summary>
    ///     Also expose the router as a named, selectable endpoint (e.g. via CleanAndChat's
    ///     endpointName param or explicit/round-robin strategies) alongside any other endpoints
    ///     configured in TokenSaver:Endpoints, rather than only ever being reachable as the
    ///     flat OllamaUrl fallback. Per the plan's "DraftModel.Loader as an optional endpoint"
    ///     integration point. Skipped if the user already defined an endpoint with this name.
    /// </summary>
    private static void AddSynthesizedEndpointIfMissing(TokenSaverConfig config)
    {
        if (config.Endpoints.Any(e =>
                string.Equals(e.Name, DraftModelLoaderEndpointName, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        config.Endpoints.Add(new OllamaEndpointConfig
        {
            Name = DraftModelLoaderEndpointName,
            Url = config.OllamaUrl,
            // Only flag as primary when no other endpoint has already claimed it —
            // OllamaEndpointRegistry falls back to "first entry" primary anyway, but
            // being explicit here keeps this endpoint from silently outranking a
            // user-declared primary in TokenSaver:Endpoints.
            Primary = !config.Endpoints.Any(e => e.Primary),
            Models = string.IsNullOrWhiteSpace(config.OllamaModel)
                ? []
                : [config.OllamaModel]
        });
    }
}
