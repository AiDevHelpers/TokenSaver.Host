using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace TokenSaver.MCP.Services;

/// <summary>
/// Central registry of known extensions and their capabilities.
/// Maintains compatibility matrix and feature support across extension versions.
/// </summary>
internal sealed class ExtensionRegistryService
{
    private readonly ILogger<ExtensionRegistryService> _logger;
    private readonly ConcurrentDictionary<string, ExtensionInfo> _registry;

    public ExtensionRegistryService(ILogger<ExtensionRegistryService> logger)
    {
        _logger = logger;
        _registry = new();
        InitializeDefaultRegistry();
    }

    /// <summary>
    /// Register or update an extension in the registry.
    /// </summary>
    public void RegisterExtension(ExtensionInfo info)
    {
        if (_registry.TryAdd(info.ExtensionId, info))
        {
            _logger.LogInformation(
                "Extension registered: {ExtensionName} v{Version}",
                info.Name,
                info.CurrentVersion);
        }
        else
        {
            _registry[info.ExtensionId] = info;
            _logger.LogInformation(
                "Extension updated: {ExtensionName} v{Version}",
                info.Name,
                info.CurrentVersion);
        }
    }

    /// <summary>
    /// Get extension info by ID.
    /// </summary>
    public ExtensionInfo? GetExtension(string extensionId)
    {
        _registry.TryGetValue(extensionId, out var info);
        return info;
    }

    /// <summary>
    /// Check if extension is compatible with a minimum MCP version.
    /// </summary>
    public bool IsCompatible(string extensionId, string mcpVersion)
    {
        if (!_registry.TryGetValue(extensionId, out var ext))
            return false;

        return CompareVersions(mcpVersion, ext.MinimumMcpVersion) >= 0;
    }

    /// <summary>
    /// Get all registered extensions.
    /// </summary>
    public IReadOnlyList<ExtensionInfo> GetAllExtensions()
    {
        return _registry.Values.ToList().AsReadOnly();
    }

    /// <summary>
    /// Get extensions by feature support.
    /// </summary>
    public IReadOnlyList<ExtensionInfo> GetExtensionsByFeature(string feature)
    {
        return _registry.Values
            .Where(e => e.Features.Contains(feature, StringComparer.OrdinalIgnoreCase))
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Check debugger support availability.
    /// </summary>
    public bool SupportsDebugger(string extensionId)
    {
        return _registry.TryGetValue(extensionId, out var ext) && ext.DebuggerSupported;
    }

    /// <summary>
    /// Get capability summary for an extension.
    /// </summary>
    public ExtensionCapabilities GetCapabilities(string extensionId)
    {
        if (!_registry.TryGetValue(extensionId, out var ext))
        {
            return new ExtensionCapabilities(
                ExtensionId: extensionId,
                IsRegistered: false,
                DebuggerSupported: false,
                StreamingSupported: false,
                PersistenceSupported: false,
                Features: new List<string>());
        }

        return new ExtensionCapabilities(
            ExtensionId: extensionId,
            IsRegistered: true,
            DebuggerSupported: ext.DebuggerSupported,
            StreamingSupported: ext.Features.Contains("streaming", StringComparer.OrdinalIgnoreCase),
            PersistenceSupported: ext.Features.Contains("persistence", StringComparer.OrdinalIgnoreCase),
            Features: ext.Features);
    }

    private void InitializeDefaultRegistry()
    {
        // VS Code Extension
        RegisterExtension(new ExtensionInfo(
            ExtensionId: "VsCode",
            Name: "TokenSaver VS Code Extension",
            DebuggerSupported: true,
            CurrentVersion: "1.0.0",
            MinimumMcpVersion: "1.0.0",
            Features: new[]
            {
                "debugging",
                "streaming",
                "persistence",
                "context-building",
                "code-search"
            }.ToList()));

        // Cursor IDE
        RegisterExtension(new ExtensionInfo(
            ExtensionId: "Cursor",
            Name: "Cursor IDE Integration",
            DebuggerSupported: true,
            CurrentVersion: "1.0.0",
            MinimumMcpVersion: "1.0.0",
            Features: new[]
            {
                "debugging",
                "streaming",
                "context-building",
                "code-search"
            }.ToList()));

        // Continue IDE
        RegisterExtension(new ExtensionInfo(
            ExtensionId: "Continue",
            Name: "Continue IDE Plugin",
            DebuggerSupported: true,
            CurrentVersion: "1.0.0",
            MinimumMcpVersion: "1.0.0",
            Features: new[]
            {
                "debugging",
                "streaming",
                "context-building"
            }.ToList()));

        // Visual Studio
        RegisterExtension(new ExtensionInfo(
            ExtensionId: "VisualStudio",
            Name: "TokenSaver Visual Studio Extension",
            DebuggerSupported: true,
            CurrentVersion: "1.0.0",
            MinimumMcpVersion: "1.0.0",
            Features: new[]
            {
                "debugging",
                "context-building",
                "code-search"
            }.ToList()));

        // LM Studio
        RegisterExtension(new ExtensionInfo(
            ExtensionId: "LMStudio",
            Name: "LM Studio Integration",
            DebuggerSupported: false,
            CurrentVersion: "1.0.0",
            MinimumMcpVersion: "1.0.0",
            Features: new[]
            {
                "context-building",
                "code-search"
            }.ToList()));

        // Claude Desktop
        RegisterExtension(new ExtensionInfo(
            ExtensionId: "ClaudeDesktop",
            Name: "Claude Desktop",
            DebuggerSupported: true,
            CurrentVersion: "1.0.0",
            MinimumMcpVersion: "1.0.0",
            Features: new[]
            {
                "debugging",
                "streaming",
                "persistence",
                "context-building"
            }.ToList()));

        _logger.LogInformation("Extension registry initialized with {Count} extensions", _registry.Count);
    }

    private static int CompareVersions(string version1, string version2)
    {
        var v1 = ParseVersion(version1);
        var v2 = ParseVersion(version2);

        for (int i = 0; i < Math.Max(v1.Length, v2.Length); i++)
        {
            int part1 = i < v1.Length ? v1[i] : 0;
            int part2 = i < v2.Length ? v2[i] : 0;

            if (part1 != part2)
                return part1.CompareTo(part2);
        }

        return 0;
    }

    private static int[] ParseVersion(string version)
    {
        return version.Split('.')
            .Take(3)
            .Select(p => int.TryParse(p, out var i) ? i : 0)
            .ToArray();
    }
}

/// <summary>Information about a registered extension.</summary>
public sealed record ExtensionInfo(
    string ExtensionId,
    string Name,
    bool DebuggerSupported,
    string CurrentVersion,
    string MinimumMcpVersion,
    List<string> Features);

/// <summary>Capabilities of an extension for feature negotiation.</summary>
public sealed record ExtensionCapabilities(
    string ExtensionId,
    bool IsRegistered,
    bool DebuggerSupported,
    bool StreamingSupported,
    bool PersistenceSupported,
    IReadOnlyList<string> Features);
