using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace TokenSaver.MCP.Services;

/// <summary>
/// Lists running Visual Studio instances from the manifests each instance writes to
/// %ProgramData%\TokenSaver\vs-instances\&lt;pid&gt;.json (see TokenSaver.Vs.Extension Bridge/InstanceRegistry).
/// Manifests for processes that have exited are removed.
/// </summary>
internal static class VsInstanceRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>One running IDE window. <c>Ide</c> is "VisualStudio" or "VsCode"; SolutionPath is the .sln or, for VS Code, the workspace folder.</summary>
    /// <summary><c>LogRoots</c> lists the exthost folders a VS Code window reports; empty for Visual Studio or when none are reported.</summary>
    internal sealed record VsInstance(int Pid, string PipeName, string? SolutionPath, DateTime UpdatedUtc, string Ide, IReadOnlyList<string> LogRoots);

    public static string Directory =>
        Path.Combine(Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData", "TokenSaver", "vs-instances");

    public static IReadOnlyList<VsInstance> ListRunning()
    {
        var instances = new List<VsInstance>();
        if (!System.IO.Directory.Exists(Directory))
        {
            return instances;
        }

        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
        {
            var instance = TryRead(file);
            if (instance is null)
            {
                continue;
            }

            if (IsRunning(instance.Pid))
            {
                instances.Add(instance);
            }
            else
            {
                TryDelete(file);
            }
        }

        return instances;
    }

    private static VsInstance? TryRead(string file)
    {
        try
        {
            return Parse(File.ReadAllText(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Parses one manifest. Returns null when the JSON is malformed or empty.</summary>
    internal static VsInstance? Parse(string json)
    {
        try
        {
            var record = JsonSerializer.Deserialize<VsInstanceManifest>(json, JsonOptions);
            if (record is null)
            {
                return null;
            }

            return new VsInstance(
                record.Pid,
                record.PipeName,
                record.SolutionPath,
                record.UpdatedUtc,
                record.Ide ?? "VisualStudio",
                record.LogRoots ?? Array.Empty<string>());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
            // Locked by another reader; it will be removed on a later listing.
        }
    }

    private sealed record VsInstanceManifest(int Pid, string PipeName, string? SolutionPath, DateTime UpdatedUtc, string? Ide, string[]? LogRoots);
}
