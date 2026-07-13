using Microsoft.Extensions.Logging.Abstractions;
using TokenSaver.Common.Core.Contracts;
using TokenSaver.MCP.Services;
using Xunit;

namespace TokenSaver.MCP.Tests;

public sealed class DebuggerIntegrationTests
{
    private readonly DebuggerEventService _eventService = new(NullLogger<DebuggerEventService>.Instance);
    private readonly DebuggerProblemService _problemService = new(NullLogger<DebuggerProblemService>.Instance);
    private readonly ClientDebugContextService _clientService = new(NullLogger<ClientDebugContextService>.Instance);
    private readonly DebuggerEventStreamManager _streamManager;
    private readonly ExtensionRegistryService _extensionRegistry = new(NullLogger<ExtensionRegistryService>.Instance);

    public DebuggerIntegrationTests()
    {
        _streamManager = new DebuggerEventStreamManager(
            NullLogger<DebuggerEventStreamManager>.Instance,
            _eventService);
    }

    [Fact]
    public async Task CompleteWorkflow_RecordsAndStreamsEvents()
    {
        // 1. Register a client
        var clientId = _clientService.RegisterClient("VsCode", "1.80.0", 1234);
        var client = _clientService.GetClient(clientId);
        Assert.NotNull(client);

        // 2. Start a debug session
        var sessionId = _clientService.StartSession(clientId, "TestSession");
        var session = _clientService.GetSession(sessionId);
        Assert.NotNull(session);
        Assert.True(session.IsActive);

        // 3. Record events
        _eventService.RecordToolExecution("BuildContext", 500, "VsCode", "1.80.0");
        _eventService.RecordMetric("ContextSize", 2048, "tokens", "BuildContext", "VsCode");
        _eventService.RecordToolExecution("SearchCode", 300, "VsCode", "1.80.0", null, true, "No matches found");

        var events = _eventService.GetAllEvents();
        Assert.Equal(3, events.Count);

        // 4. Record problems
        _problemService.RecordProblem(
            "CompilationError",
            "Type not found",
            "src/Service.cs",
            42,
            15,
            "Cannot resolve type 'IService'",
            "Add using statement",
            "Roslyn",
            "VsCode",
            "CS0246");

        var problems = _problemService.GetAllProblems();
        Assert.Single(problems);

        // 5. Update session counters
        _clientService.IncrementSessionEventCount(sessionId);
        _clientService.IncrementSessionEventCount(sessionId);
        _clientService.IncrementSessionBreakpointCount(sessionId);

        var updatedSession = _clientService.GetSession(sessionId)!;
        Assert.Equal(2, updatedSession.EventCount);
        Assert.Equal(1, updatedSession.BreakpointCount);

        // 6. Stream events to subscription
        var receivedEvents = new List<DebuggerEvent>();
        var subscriptionId = _streamManager.AddSubscription(
            onEventAsync: async evt =>
            {
                receivedEvents.Add(evt);
                await Task.CompletedTask;
            });

        // 7. Broadcast events
        await _streamManager.BroadcastEventAsync(events[0]);
        await _streamManager.BroadcastEventAsync(events[1]);

        Assert.Equal(2, receivedEvents.Count);

        // 8. Query with filters. "BuildContext" is the tool name of BOTH the tool execution and
        //    the ContextSize metric recorded above, so also filter by event type to isolate the
        //    tool execution.
        var toolEvents = _eventService.QueryEvents(
            eventType: DebuggerEventType.ToolExecution, toolName: "BuildContext");
        Assert.Single(toolEvents);

        var errorEvents = _eventService.QueryEvents(severity: DebugSeverity.Error);
        Assert.Single(errorEvents);

        // 9. Get statistics
        var eventStats = _eventService.GetEventStats();
        Assert.Equal(3, eventStats.TotalEventCount);
        Assert.Equal(2, eventStats.EventCountBySeverity[DebugSeverity.Info]);

        var problemStats = _problemService.GetProblemStats();
        Assert.Equal(1, problemStats.TotalProblemCount);
        Assert.Equal(1, problemStats.ErrorCount);

        var streamStats = _streamManager.GetStreamStats();
        Assert.Equal(1, streamStats.TotalSubscriptions);
        Assert.Equal(2, streamStats.TotalEventsSent);

        // 10. End session
        _clientService.EndSession(sessionId);
        var endedSession = _clientService.GetSession(sessionId);
        Assert.False(endedSession!.IsActive);
        Assert.NotNull(endedSession.EndedAt);

        // Cleanup
        _streamManager.RemoveSubscription(subscriptionId);
    }

    [Fact]
    public async Task MultiClientScenario_TracksSeparateSessions()
    {
        // Register multiple clients
        var vscodeId = _clientService.RegisterClient("VsCode", "1.80.0");
        var cursorId = _clientService.RegisterClient("Cursor", "0.5.0");
        var continueId = _clientService.RegisterClient("Continue", "0.8.0");

        // Start sessions
        var vscodeSession = _clientService.StartSession(vscodeId);
        var cursorSession = _clientService.StartSession(cursorId);
        var continueSession = _clientService.StartSession(continueId);

        // Record events from each client
        _eventService.RecordToolExecution("Tool1", 100, "VsCode");
        _eventService.RecordToolExecution("Tool2", 200, "Cursor");
        _eventService.RecordToolExecution("Tool3", 300, "Continue");

        // Record problems
        _problemService.RecordProblem("Error", "Error 1", "file1.cs", clientName: "VsCode");
        _problemService.RecordProblem("Warning", "Warning 1", "file2.cs", clientName: "Cursor");

        // Verify separation
        var vscodeEvents = _eventService.QueryEvents(clientName: "VsCode");
        Assert.Single(vscodeEvents);

        var cursorProblems = _problemService.QueryProblems(clientName: "Cursor");
        Assert.Single(cursorProblems);

        var allSessions = _clientService.GetActiveSessions();
        Assert.Equal(3, allSessions.Count);

        // Verify extension registry
        Assert.True(_extensionRegistry.SupportsDebugger("VsCode"));
        Assert.True(_extensionRegistry.SupportsDebugger("Cursor"));
        Assert.True(_extensionRegistry.SupportsDebugger("Continue"));

        var vscodeCaps = _extensionRegistry.GetCapabilities("VsCode");
        Assert.True(vscodeCaps.StreamingSupported);
        Assert.True(vscodeCaps.PersistenceSupported);
    }

    [Fact]
    public async Task StreamingWithFiltering_RoutesCorrectly()
    {
        var errorEvents = new List<DebuggerEvent>();
        var infoEvents = new List<DebuggerEvent>();

        // Subscribe to errors only
        var errorSub = _streamManager.AddSubscription(
            onEventAsync: async evt =>
            {
                errorEvents.Add(evt);
                await Task.CompletedTask;
            },
            filter: evt => evt.Severity == DebugSeverity.Error);

        // Subscribe to info only
        var infoSub = _streamManager.AddSubscription(
            onEventAsync: async evt =>
            {
                infoEvents.Add(evt);
                await Task.CompletedTask;
            },
            filter: evt => evt.Severity == DebugSeverity.Info);

        // Record mixed events
        _eventService.RecordToolExecution("Tool1", 100, null, null, null, false); // Info
        _eventService.RecordToolExecution("Tool2", 200, null, null, null, true);  // Error
        _eventService.RecordToolExecution("Tool3", 300, null, null, null, false); // Info
        _eventService.RecordToolExecution("Tool4", 400, null, null, null, true);  // Error

        var allEvents = _eventService.GetAllEvents();

        // Broadcast all events
        foreach (var evt in allEvents)
        {
            await _streamManager.BroadcastEventAsync(evt);
        }

        // Verify routing
        Assert.Equal(2, errorEvents.Count);
        Assert.All(errorEvents, e => Assert.Equal(DebugSeverity.Error, e.Severity));

        Assert.Equal(2, infoEvents.Count);
        Assert.All(infoEvents, e => Assert.Equal(DebugSeverity.Info, e.Severity));

        _streamManager.RemoveSubscription(errorSub);
        _streamManager.RemoveSubscription(infoSub);
    }

    [Fact]
    public void ProblemAnalysis_ByFileAndSeverity()
    {
        // Record problems from multiple files
        _problemService.RecordProblem("CompilationError", "Error 1", "Service.cs", 10, clientName: "VsCode");
        _problemService.RecordProblem("CompilationError", "Error 2", "Service.cs", 20, clientName: "VsCode");
        _problemService.RecordProblem("Warning", "Warning 1", "Controller.cs", 5, clientName: "VsCode");
        _problemService.RecordProblem("CompilationError", "Error 3", "Model.cs", 15, clientName: "Continue");

        // Group by file
        var byFile = _problemService.GetProblemsByFile();
        Assert.Equal(3, byFile.Count);
        Assert.Equal(2, byFile["Service.cs"].Count);
        Assert.Single(byFile["Controller.cs"]);

        // Group by severity
        var bySeverity = _problemService.GetProblemsBySeverity();
        Assert.Equal(3, bySeverity[DebugSeverity.Error].Count);
        Assert.Single(bySeverity[DebugSeverity.Warning]);

        // Get statistics
        var stats = _problemService.GetProblemStats();
        Assert.Equal(4, stats.TotalProblemCount);
        Assert.Equal(3, stats.ErrorCount);
        Assert.Equal(1, stats.WarningCount);
        Assert.Equal(3, stats.AffectedFiles.Count);
    }

    [Fact]
    public void ClientSessionLifecycle_Complete()
    {
        // Register
        var clientId = _clientService.RegisterClient("VsCode", "1.80.0", 1234);
        var client1 = _clientService.GetClient(clientId);
        Assert.NotNull(client1);
        Assert.Null(client1.ActiveSessionId);

        // Start session 1
        var session1Id = _clientService.StartSession(clientId, "Session1");
        var client2 = _clientService.GetClient(clientId);
        Assert.Equal(session1Id, client2!.ActiveSessionId);

        // Record activity in session 1
        _clientService.IncrementSessionEventCount(session1Id);
        _clientService.IncrementSessionBreakpointCount(session1Id);

        var session1 = _clientService.GetSession(session1Id)!;
        Assert.Equal(1, session1.EventCount);
        Assert.Equal(1, session1.BreakpointCount);

        // End session 1
        _clientService.EndSession(session1Id);
        var client3 = _clientService.GetClient(clientId);
        Assert.Null(client3!.ActiveSessionId);

        // Start session 2
        var session2Id = _clientService.StartSession(clientId, "Session2");
        var client4 = _clientService.GetClient(clientId);
        Assert.Equal(session2Id, client4!.ActiveSessionId);

        // Get all sessions for client
        var allSessions = _clientService.GetClientSessions(clientId);
        Assert.Equal(2, allSessions.Count);

        // Unregister
        _clientService.UnregisterClient(clientId);
        var unregistered = _clientService.GetClient(clientId);
        Assert.Null(unregistered);
    }

    [Fact]
    public void EventQueryingWithMultipleFilters()
    {
        // Record diverse events
        for (int i = 0; i < 10; i++)
        {
            _eventService.RecordToolExecution(
                toolName: $"Tool{i % 3}",
                durationMs: 100 + (i * 10),
                clientName: i % 2 == 0 ? "VsCode" : "Cursor",
                isError: i % 4 == 0);
        }

        // Query by multiple filters
        var vscodeErrors = _eventService.QueryEvents(
            clientName: "VsCode",
            severity: DebugSeverity.Error);

        Assert.NotEmpty(vscodeErrors);
        Assert.All(vscodeErrors, e =>
        {
            Assert.Equal("VsCode", e.CallerInfo.ClientName);
            Assert.Equal(DebugSeverity.Error, e.Severity);
        });

        // Query by tool and severity
        var tool0Errors = _eventService.QueryEvents(
            toolName: "Tool0",
            severity: DebugSeverity.Error);

        Assert.NotEmpty(tool0Errors);
        Assert.All(tool0Errors, e => Assert.Equal("Tool0", e.ToolName));
    }
}
