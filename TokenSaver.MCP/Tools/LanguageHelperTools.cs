using System.ComponentModel;
using System.Globalization;
using System.Text;

using Microsoft.Extensions.Options;

using ModelContextProtocol.Server;

using AiDevTools.TokenSaver.Utilities.Services;
using AiDevTools.TokenSaver.Common.Core.Configuration;
using AiDevTools.TokenSaver.LanguageHelpers.Core.Contracts;
using AiDevTools.TokenSaver.LanguageHelpers.Core.Models;
using AiDevTools.TokenSaver.MCP.Services;

namespace AiDevTools.TokenSaver.MCP.Tools;

/// <summary>
///     MCP surface for the per-language code-quality helpers (AiDevTools.TokenSaver.LanguageHelpers.*). Exposes the
///     seven operations as two action-dispatch tools split by side effect: <see cref="CheckCode" /> is
///     read-only (syntax check, lint) and <see cref="FixCode" /> mutates files (enable analysis config,
///     fix syntax/imports/namespace, format). Language is auto-detected from the target's extension —
///     C# (.cs/.csx), PowerShell (.ps1/.psm1/.psd1), Python (.py/.pyi) — or forced with the
///     <c>language</c> argument when pointing at a directory. Operations that don't apply to a language
///     (namespace/imports in Python or PowerShell) report "not applicable" rather than failing, and a
///     missing external tool (dotnet, pwsh, ruff) reports "tool unavailable" with an install hint.
/// </summary>
[McpServerToolType]
internal sealed class LanguageHelperTools
{
    private readonly ILanguageHelperRegistry _registry;
    private readonly TokenSaverConfig _config;
    private readonly WorkspaceRootSessionResolver _workspaceRootResolver;

    public LanguageHelperTools(
        ILanguageHelperRegistry registry,
        IOptions<TokenSaverConfig> config,
        WorkspaceRootSessionResolver workspaceRootResolver)
    {
        _registry = registry;
        _config = config.Value;
        _workspaceRootResolver = workspaceRootResolver;
    }

    // ── CheckCode (read-only) ─────────────────────────────────────────────────────

    [McpServerTool]
    [Description(
        "Read-only code-quality inspection for a file or directory. Never edits files. operation:\n" +
        "  syntax — run a fast validator and report basic syntax/parse errors (C#: Roslyn, " +
        "PowerShell: parser, Python: ruff E9/F).\n" +
        "  lint   — run the language linter and report style/analyzer findings (C#: dotnet format " +
        "analyzers, PowerShell: PSScriptAnalyzer, Python: ruff check).\n" +
        "Language is detected from the file extension; pass 'language' (csharp|powershell|python) when " +
        "'path' is a directory. Use FixCode to actually apply fixes.")]
    public async Task<string> CheckCode(
        [Description("Action: 'syntax' or 'lint'")]
        string operation,
        [Description("File or directory to inspect (absolute, or relative to the workspace root).")]
        string path,
        [Description("Force the language: 'csharp', 'powershell', or 'python'. Omit to detect from the file extension.")]
        string? language = null,
        [Description("Workspace root. Omit to use the session's resolved workspace root.")]
        string? workspaceRoot = null,
        McpServer server = null!,
        CancellationToken ct = default)
    {
        await _workspaceRootResolver.EnsureResolvedAsync(server, ct);

        if (!TryResolve(operation, path, language, workspaceRoot, out ILanguageHelper helper, out AnalysisScope scope, out string error))
        {
            return error;
        }

        return operation.Trim().ToUpperInvariant() switch
        {
            "SYNTAX" => Render("syntax", helper, await helper.RunSyntaxCheckerAsync(scope, ct)),
            "LINT" => Render("lint", helper, await helper.RunLintAsync(scope, LintOptions.Default, ct)),
            _ => $"Unknown operation '{operation}'. Use 'syntax' or 'lint'.",
        };
    }

    // ── FixCode (mutating) ────────────────────────────────────────────────────────

    [McpServerTool]
    [Description(
        "Apply an automated code-quality fix to a file or directory. THIS EDITS FILES IN PLACE — show " +
        "the result to the user and rely on version control to review the diff. operation:\n" +
        "  enable-analysis — write/merge the language's analysis config (C#: .editorconfig, PowerShell: " +
        "PSScriptAnalyzerSettings.psd1, Python: ruff.toml) as an idempotent managed block.\n" +
        "  fix-syntax      — auto-fix the fixable issues (C#: dotnet format style, PowerShell: " +
        "Invoke-ScriptAnalyzer -Fix, Python: ruff --fix); unfixable syntax errors are reported.\n" +
        "  fix-imports     — organize/remove imports (C#: unused usings IDE0005, Python: isort + F401; " +
        "not applicable to PowerShell).\n" +
        "  fix-namespace   — correct namespaces to match folder layout (C# only; not applicable to " +
        "PowerShell/Python).\n" +
        "  format          — reformat to the standard (C#: dotnet format whitespace, PowerShell: " +
        "Invoke-Formatter, Python: ruff format). Set preferExtensionTool=true to use CSharpier/black " +
        "when installed.\n" +
        "Language is detected from the file extension; pass 'language' when 'path' is a directory.")]
    public async Task<string> FixCode(
        [Description("Action: 'enable-analysis', 'fix-syntax', 'fix-imports', 'fix-namespace', or 'format'")]
        string operation,
        [Description("File or directory to fix (absolute, or relative to the workspace root).")]
        string path,
        [Description("Force the language: 'csharp', 'powershell', or 'python'. Omit to detect from the file extension.")]
        string? language = null,
        [Description("Workspace root. Omit to use the session's resolved workspace root.")]
        string? workspaceRoot = null,
        [Description("For 'format': prefer an editor-extension formatter (CSharpier for C#, black for Python) when installed.")]
        bool preferExtensionTool = false,
        McpServer server = null!,
        CancellationToken ct = default)
    {
        await _workspaceRootResolver.EnsureResolvedAsync(server, ct);

        if (!TryResolve(operation, path, language, workspaceRoot, out ILanguageHelper helper, out AnalysisScope scope, out string error))
        {
            return error;
        }

        return operation.Trim().ToUpperInvariant() switch
        {
            "ENABLE-ANALYSIS" or "ENABLE" => Render("enable-analysis", helper, await helper.EnableCodeAnalysisAsync(scope, null, ct)),
            "FIX-SYNTAX" => Render("fix-syntax", helper, await helper.FixSyntaxAsync(scope, ct)),
            "FIX-IMPORTS" => Render("fix-imports", helper, await helper.FixImportsAsync(scope, ct)),
            "FIX-NAMESPACE" => Render("fix-namespace", helper, await helper.FixNamespaceAsync(scope, ct)),
            "FORMAT" => Render("format", helper, await helper.FormatCodeAsync(scope, new FormatOptions { PreferExtensionTool = preferExtensionTool }, ct)),
            _ => $"Unknown operation '{operation}'. Use 'enable-analysis', 'fix-syntax', 'fix-imports', 'fix-namespace', or 'format'.",
        };
    }

    // ── Resolution ────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Resolves the workspace root, the language helper (from <paramref name="language" /> or the target
    ///     extension), and the analysis scope. Returns false with a caller-facing message when the language
    ///     can't be determined or isn't registered.
    /// </summary>
    private bool TryResolve(
        string operation, string path, string? language, string? workspaceRoot,
        out ILanguageHelper helper, out AnalysisScope scope, out string error)
    {
        helper = null!;
        scope = null!;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "A 'path' (file or directory) is required.";
            return false;
        }

        string root = workspaceRoot ?? _config.WorkspaceRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            error = "No workspace root is available yet. Pass 'workspaceRoot' explicitly.";
            return false;
        }

        // The analyzers and formatters run on (and may rewrite) this path, so it must stay inside the workspace.
        PathCheck target = WorkspacePathGuard.Resolve(path, root);
        if (!target.Ok)
        {
            error = target.Error!;
            return false;
        }

        string absPath = target.FullPath;

        if (language is not null)
        {
            if (!TryParseLanguage(language, out LanguageId forced))
            {
                error = $"Unknown language '{language}'. Use 'csharp', 'powershell', or 'python'.";
                return false;
            }

            helper = _registry.Resolve(forced)!;
            if (helper is null)
            {
                error = $"No language helper is registered for {forced}.";
                return false;
            }
        }
        else
        {
            helper = _registry.TryGetForFile(absPath)!;
            if (helper is null)
            {
                error =
                    $"Could not detect the language from '{path}'. Pass 'language' (csharp|powershell|python) " +
                    "when the path is a directory or has an unrecognized extension.";
                return false;
            }
        }

        // A path equal to the root scopes the whole workspace; anything else is a single target.
        bool wholeWorkspace = string.Equals(
            Path.TrimEndingDirectorySeparator(absPath),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)),
            StringComparison.OrdinalIgnoreCase);

        scope = wholeWorkspace
            ? AnalysisScope.ForWorkspace(root)
            : new AnalysisScope { WorkspaceRoot = root, Targets = [absPath] };

        return true;
    }

    private static bool TryParseLanguage(string value, out LanguageId language)
    {
        switch (value.Trim().ToUpperInvariant())
        {
            case "CSHARP" or "C#" or "CS" or "DOTNET":
                language = LanguageId.CSharp;
                return true;
            case "POWERSHELL" or "PWSH" or "PS" or "PS1":
                language = LanguageId.PowerShell;
                return true;
            case "PYTHON" or "PY":
                language = LanguageId.Python;
                return true;
            default:
                language = LanguageId.CSharp;
                return false;
        }
    }

    // ── Rendering ─────────────────────────────────────────────────────────────────

    private const int MaxIssuesShown = 50;

    /// <summary>Formats any operation result into a concise, token-efficient report.</summary>
    private static string Render(string operation, ILanguageHelper helper, OperationResult result)
    {
        StringBuilder sb = new();
        _ = sb.AppendLine(CultureInfo.InvariantCulture,
            $"{helper.Language} {operation}: {Describe(result.Status)}" +
            $"{(result.Tool is null ? "" : $" [{result.Tool}]")} ({result.Duration.TotalMilliseconds:F0} ms)");

        foreach (string message in result.Messages)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  {message}");
        }

        AppendResultSpecifics(sb, result);

        if (result.ChangedFiles.Count > 0)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  changed {result.ChangedFiles.Count} file(s):");
            foreach (string file in result.ChangedFiles.Take(MaxIssuesShown))
            {
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"    {file}");
            }
        }

        IReadOnlyList<CodeIssue> issues = result.Issues;
        if (issues.Count > 0)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture,
                $"  {issues.Count} finding(s) — {result.ErrorCount} error(s), {result.WarningCount} warning(s):");
            foreach (CodeIssue issue in issues.Take(MaxIssuesShown))
            {
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"    {FormatIssue(issue)}");
            }

            if (issues.Count > MaxIssuesShown)
            {
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"    ... and {issues.Count - MaxIssuesShown} more.");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static void AppendResultSpecifics(StringBuilder sb, OperationResult result)
    {
        switch (result)
        {
            case EnableAnalysisResult e when e.ConfigFilesWritten.Count > 0:
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  wrote: {string.Join(", ", e.ConfigFilesWritten)}");
                break;
            case FixResult f:
                _ = sb.AppendLine(CultureInfo.InvariantCulture,
                    $"  fixed {f.FixedCount}, {f.RemainingIssues.Count} remaining (need manual attention).");
                break;
            case ImportFixResult i:
                _ = sb.AppendLine(CultureInfo.InvariantCulture,
                    $"  imports: removed {i.ImportsRemoved}, added {i.ImportsAdded}, reordered {i.ImportsReordered}.");
                break;
            case NamespaceFixResult n:
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"  namespaces corrected: {n.NamespacesCorrected}.");
                break;
            default:
                break;
        }
    }

    private static string FormatIssue(CodeIssue issue)
    {
        string location = issue.Location is { } loc
            ? $"{loc.FilePath}:{loc.Line}:{loc.Column}"
            : "(no location)";
        string rule = issue.RuleId is null ? "" : $"{issue.RuleId} ";
        return $"[{issue.Severity}] {location} {rule}{issue.Message}";
    }

    private static string Describe(OperationStatus status) => status switch
    {
        OperationStatus.NoChanges => "no changes needed",
        OperationStatus.SucceededWithChanges => "changes applied",
        OperationStatus.Succeeded => "completed",
        OperationStatus.NotApplicable => "not applicable to this language",
        OperationStatus.ToolUnavailable => "required tool not installed",
        OperationStatus.Failed => "FAILED",
        _ => status.ToString(),
    };
}
