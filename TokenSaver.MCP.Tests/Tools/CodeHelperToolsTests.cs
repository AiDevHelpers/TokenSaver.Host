using AiDevTools.TokenSaver.Common.Core.Configuration;
using AiDevTools.TokenSaver.CodeHelper.Planning;
using AiDevTools.TokenSaver.MCP.Tools;

using Microsoft.Extensions.Options;

using Xunit;

namespace AiDevTools.TokenSaver.MCP.Tests.Tools;

/// <summary>
///     Covers CodeHelperTools on its argument-validation and unknown-action branches, plus the
///     CodeIndex 'table' action and the ManagePlan 'status' lookup against an empty PlanStore. Every
///     constructor dependency that a covered path would dereference is real (config, resolver, PlanStore).
///     The rest are passed as null because no covered path reaches them: scan, find and dependencies
///     with valid input, the EditFile actions that write files, and plan creation/execution would
///     all start indexing, the model, or a patch engine.
/// </summary>
public sealed class CodeHelperToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tokensaver-codehelper-tests-" + Guid.NewGuid().ToString("N"));

    public CodeHelperToolsTests()
    {
        _ = Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "readme.txt"), "hello");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private CodeHelperTools CreateTools(PlanStore? store = null)
    {
        TokenSaverConfig cfg = new() { WorkspaceRoot = _root };
        return new CodeHelperTools(
            index: null!,
            symbolSearch: null!,
            dependencies: null!,
            patch: null!,
            planBuilder: null!,
            executor: null!,
            store: store ?? new PlanStore(),
            config: Options.Create(cfg),
            cleaner: null!,
            detection: null!,
            registry: null!,
            httpContext: null!,
            workspaceRootResolver: WorkspaceRootSessionResolverTestFactory.Create(cfg),
            semanticIndex: null!,
            symbolScan: null!);
    }

    // ── CodeIndex ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task CodeIndex_UnknownAction_ReturnsUnknownAction()
    {
        CodeHelperTools tools = CreateTools();

        string result = await tools.CodeIndex("reindex");

        Assert.Equal("Unknown action. Use 'scan', 'find', 'dependencies', 'status', or 'table'.", result);
    }

    [Fact]
    public async Task CodeIndex_FindWithoutName_ReturnsNameRequired()
    {
        CodeHelperTools tools = CreateTools();

        string result = await tools.CodeIndex("find");

        Assert.Equal("name is required for action 'find'.", result);
    }

    [Fact]
    public async Task CodeIndex_DependenciesWithoutFilePath_ReturnsFilePathRequired()
    {
        CodeHelperTools tools = CreateTools();

        string result = await tools.CodeIndex("dependencies", filePath: " ");

        Assert.Equal("filePath is required for action 'dependencies'.", result);
    }

    [Fact]
    public async Task CodeIndex_Table_WritesFolderIndexIntoWorkspace()
    {
        CodeHelperTools tools = CreateTools();

        string result = await tools.CodeIndex("table", root: _root);

        Assert.True(
            result.StartsWith("Wrote ", StringComparison.Ordinal) || result.EndsWith("is already up to date.", StringComparison.Ordinal),
            $"unexpected result: {result}");
    }

    // ── EditFile ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task EditFile_UnknownAction_ReturnsUnknownAction()
    {
        CodeHelperTools tools = CreateTools();

        string result = await tools.EditFile("shred");

        Assert.Equal("Unknown action. Use 'apply', 'insert', 'delete', 'rename', or 'rollback'.", result);
    }

    [Fact]
    public async Task EditFile_ApplyWithoutFilePath_ReturnsFilePathRequired()
    {
        CodeHelperTools tools = CreateTools();

        string result = await tools.EditFile("apply", oldText: "a", newText: "b");

        Assert.Equal("filePath is required for action 'apply'.", result);
    }

    [Fact]
    public async Task EditFile_ApplyWithoutOldText_ReturnsOldTextRequired()
    {
        CodeHelperTools tools = CreateTools();

        string result = await tools.EditFile("apply", filePath: "a.cs", newText: "b");

        Assert.Equal("oldText is required for action 'apply'.", result);
    }

    [Fact]
    public async Task EditFile_ApplyWithoutNewText_ReturnsNewTextRequired()
    {
        CodeHelperTools tools = CreateTools();

        string result = await tools.EditFile("apply", filePath: "a.cs", oldText: "a");

        Assert.Equal("newText is required for action 'apply'.", result);
    }

    [Fact]
    public async Task EditFile_InsertWithInvalidPosition_ReturnsPositionError()
    {
        CodeHelperTools tools = CreateTools();

        string result = await tools.EditFile("insert", filePath: "a.cs", anchor: "x", content: "y", position: "sideways");

        Assert.Equal("position must be 'before' or 'after'.", result);
    }

    [Fact]
    public async Task EditFile_DeleteWithoutPattern_ReturnsPatternRequired()
    {
        CodeHelperTools tools = CreateTools();

        string result = await tools.EditFile("delete", filePath: "a.cs");

        Assert.Equal("pattern is required for action 'delete'.", result);
    }

    [Fact]
    public async Task EditFile_RenameWithoutNewName_ReturnsNewNameRequired()
    {
        CodeHelperTools tools = CreateTools();

        string result = await tools.EditFile("rename", filePath: "a.cs", oldName: "Old");

        Assert.Equal("newName is required for action 'rename'.", result);
    }

    [Fact]
    public async Task EditFile_RollbackWithoutBackupPath_ReturnsBackupPathRequired()
    {
        CodeHelperTools tools = CreateTools();

        string result = await tools.EditFile("rollback", targetFilePath: "a.cs");

        Assert.Equal("backupPath is required for action 'rollback'.", result);
    }

    [Fact]
    public async Task EditFile_RollbackWithoutTargetFilePath_ReturnsTargetRequired()
    {
        CodeHelperTools tools = CreateTools();

        string result = await tools.EditFile("rollback", backupPath: "a.bak");

        Assert.Equal("targetFilePath is required for action 'rollback'.", result);
    }

    // ── ManagePlan ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ManagePlan_UnknownAction_ReturnsUnknownAction()
    {
        CodeHelperTools tools = CreateTools();

        string result = await tools.ManagePlan("abandon");

        Assert.Equal("Unknown action. Use 'create', 'execute', or 'status'.", result);
    }

    [Fact]
    public async Task ManagePlan_CreateWithoutPrompt_ReturnsPromptRequired()
    {
        CodeHelperTools tools = CreateTools();

        string result = await tools.ManagePlan("create");

        Assert.Equal("prompt is required for action 'create'.", result);
    }

    [Fact]
    public async Task ManagePlan_ExecuteWithoutPlanId_ReturnsPlanIdRequired()
    {
        CodeHelperTools tools = CreateTools();

        string result = await tools.ManagePlan("execute", stepNumber: 1);

        Assert.Equal("planId is required for action 'execute'.", result);
    }

    [Fact]
    public async Task ManagePlan_ExecuteWithoutStepNumber_ReturnsStepNumberRequired()
    {
        CodeHelperTools tools = CreateTools();

        string result = await tools.ManagePlan("execute", planId: "p1");

        Assert.Equal("stepNumber is required for action 'execute'.", result);
    }

    [Fact]
    public async Task ManagePlan_ExecuteUnknownPlan_ReturnsNotFound()
    {
        CodeHelperTools tools = CreateTools();

        string result = await tools.ManagePlan("execute", planId: "missing", stepNumber: 1);

        Assert.Equal("Plan 'missing' not found. Use ManagePlan(action: 'create') first.", result);
    }

    [Fact]
    public async Task ManagePlan_StatusWithoutPlanId_ReturnsPlanIdRequired()
    {
        CodeHelperTools tools = CreateTools();

        string result = await tools.ManagePlan("status");

        Assert.Equal("planId is required for action 'status'.", result);
    }

    [Fact]
    public async Task ManagePlan_StatusUnknownPlan_ReturnsNotFound()
    {
        CodeHelperTools tools = CreateTools();

        string result = await tools.ManagePlan("status", planId: "nope");

        Assert.Equal("Plan 'nope' not found.", result);
    }
}
