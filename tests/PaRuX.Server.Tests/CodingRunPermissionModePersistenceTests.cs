using System;
using System.IO;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class CodingRunPermissionModePersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "parux-coding-mode-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Root_mode_survives_task_store_reload_and_child_inherits_it()
    {
        var store = new TaskStore(_directory);
        AgentTaskDto root = store.Create(new CreateAgentTaskRequest
        {
            Goal = "Implement a coding task",
            Role = "coding",
            CodingRunPermissionMode = CodingRunPermissionModes.Ask
        });

        Assert.Equal(CodingRunPermissionModes.Ask, root.CodingRunPermissionMode);
        Assert.Equal(CodingRunPermissionModes.Ask, new TaskStore(_directory).Get(root.Id)!.CodingRunPermissionMode);

        AgentTaskDto child = store.Create(new CreateAgentTaskRequest
        {
            ParentId = root.Id,
            Goal = "Implement a bounded delegated change"
        });
        Assert.Equal(CodingRunPermissionModes.Ask, child.CodingRunPermissionMode);

        Assert.Throws<InvalidDataException>(() => store.Create(new CreateAgentTaskRequest
        {
            ParentId = root.Id,
            Goal = "A child cannot weaken Ask mode",
            CodingRunPermissionMode = CodingRunPermissionModes.Bypass
        }));
    }

    [Fact]
    public void Missing_or_unknown_mode_keeps_existing_bypass_default()
    {
        var store = new TaskStore(_directory);
        AgentTaskDto legacy = store.Create(new CreateAgentTaskRequest { Goal = "Legacy task" });
        AgentTaskDto unknown = store.Create(new CreateAgentTaskRequest
        {
            Goal = "Unknown mode task",
            CodingRunPermissionMode = "unexpected"
        });

        Assert.Equal(CodingRunPermissionModes.Bypass, legacy.CodingRunPermissionMode);
        Assert.Equal(CodingRunPermissionModes.Bypass, unknown.CodingRunPermissionMode);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch { }
    }
}
