using LlamaLauncher.Application.Workflows;
using LlamaLauncher.Domain.Workflows;
using LlamaLauncher.Infrastructure.Sqlite;
using LlamaLauncher.Infrastructure.Workflows;
using LlamaLauncher.IntegrationTests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LlamaLauncher.IntegrationTests.Workflows;

/// <summary>工作流定义/测试用例存储（JSON 文件）与运行历史（SQLite）的持久化行为。</summary>
public sealed class WorkflowStoreTests
{
    private static JsonWorkflowDefinitionStore CreateDefinitionStore(TestAppPaths paths)
        => new(paths, NullLogger<JsonWorkflowDefinitionStore>.Instance);

    private static JsonWorkflowTestCaseStore CreateTestCaseStore(TestAppPaths paths)
        => new(paths, NullLogger<JsonWorkflowTestCaseStore>.Instance);

    private static WorkflowDefinition SampleDefinition(string id = "wf1")
    {
        var workflow = new WorkflowDefinition
        {
            Id = id,
            Name = "退款流程",
            Description = "示例",
        };
        workflow.Nodes.Add(NodeTypeCatalog.CreateNode(NodeTypeCatalog.Start, "n1", 0, 0));
        workflow.Nodes.Add(NodeTypeCatalog.CreateNode(NodeTypeCatalog.Decision, "n2", 200, 0));
        workflow.Nodes.Add(NodeTypeCatalog.CreateNode(NodeTypeCatalog.End, "n3", 400, 0));
        workflow.Edges.Add(new WorkflowEdge { Id = "e1", SourceNodeId = "n1", TargetNodeId = "n2" });
        workflow.Edges.Add(new WorkflowEdge { Id = "e2", SourceNodeId = "n2", TargetNodeId = "n3" });
        return workflow;
    }

    [Fact]
    public async Task 保存后可以重新读取()
    {
        using var temp = new TempDirectory();
        var paths = new TestAppPaths(temp.Path);
        paths.EnsureCreated();
        var store = CreateDefinitionStore(paths);

        await store.SaveAsync(SampleDefinition());
        var loaded = await store.LoadAsync("wf1");

        Assert.NotNull(loaded.Definition);
        Assert.False(loaded.HasErrors);
        Assert.Equal("退款流程", loaded.Definition!.Name);
        Assert.Equal(3, loaded.Definition.Nodes.Count);
        Assert.Equal(2, loaded.Definition.Edges.Count);

        var list = await store.ListAsync();
        Assert.Single(list);
        Assert.Equal(3, list[0].NodeCount);
    }

    [Fact]
    public async Task 覆盖保存使用原子写入()
    {
        using var temp = new TempDirectory();
        var paths = new TestAppPaths(temp.Path);
        paths.EnsureCreated();
        var store = CreateDefinitionStore(paths);

        var definition = SampleDefinition();
        await store.SaveAsync(definition);
        definition.Name = "新的名称";
        definition.Nodes.RemoveAt(2);
        await store.SaveAsync(definition);

        var loaded = await store.LoadAsync("wf1");
        Assert.Equal("新的名称", loaded.Definition!.Name);
        Assert.Equal(2, loaded.Definition.Nodes.Count);
        Assert.Empty(Directory.EnumerateFiles(paths.WorkflowsDirectory, "*.tmp-*"));
    }

    [Fact]
    public async Task 版本快照不可变()
    {
        using var temp = new TempDirectory();
        var paths = new TestAppPaths(temp.Path);
        paths.EnsureCreated();
        var store = CreateDefinitionStore(paths);

        var definition = SampleDefinition();
        await store.SaveAsync(definition, "v1", "初始版本");
        definition.Name = "修改后的名称";
        await store.SaveAsync(definition);

        var snapshots = await store.ListSnapshotsAsync("wf1");
        Assert.Single(snapshots);
        Assert.Equal("v1", snapshots[0].Label);
        Assert.Equal("退款流程", snapshots[0].Definition!.Name);

        var loaded = await store.LoadSnapshotAsync("wf1", snapshots[0].SnapshotId);
        Assert.NotNull(loaded);
        Assert.Equal("退款流程", loaded!.Definition!.Name);
    }

    [Fact]
    public async Task 导入非法JSON给出错误而不崩溃()
    {
        using var temp = new TempDirectory();
        var paths = new TestAppPaths(temp.Path);
        paths.EnsureCreated();
        var store = CreateDefinitionStore(paths);

        var bad = temp.Combine("bad.workflow.json");
        await File.WriteAllTextAsync(bad, "{ this is not json ");
        var result = await store.ImportAsync(bad);

        Assert.Null(result.Definition);
        Assert.True(result.HasErrors);
        Assert.Contains("JSON", result.Issues[0].Message);
    }

    [Fact]
    public async Task 导入超大文件被拒绝()
    {
        using var temp = new TempDirectory();
        var paths = new TestAppPaths(temp.Path);
        paths.EnsureCreated();
        var store = CreateDefinitionStore(paths);

        var big = temp.Combine("big.workflow.json");
        await File.WriteAllTextAsync(big, new string('x', WorkflowLimits.MaxDefinitionFileBytes + 100));
        var result = await store.ImportAsync(big);

        Assert.Null(result.Definition);
        Assert.True(result.HasErrors);
        Assert.Contains("过大", result.Issues[0].Message);
    }

    [Fact]
    public async Task 未知节点类型被保留并报告()
    {
        using var temp = new TempDirectory();
        var paths = new TestAppPaths(temp.Path);
        paths.EnsureCreated();
        var store = CreateDefinitionStore(paths);

        var definition = SampleDefinition();
        definition.Nodes.Add(new WorkflowNode { Id = "n9", TypeId = "extension.custom", Name = "自定义节点" });
        var path = temp.Combine("x.workflow.json");
        await File.WriteAllTextAsync(path, WorkflowJson.Serialize(definition));

        var result = await store.ImportAsync(path);
        Assert.NotNull(result.Definition);
        Assert.Equal(4, result.Definition!.Nodes.Count);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Issues, i => i.Code == "WF-NODE-UNKNOWN");
    }

    [Fact]
    public async Task 不安全的ID被拒绝()
    {
        using var temp = new TempDirectory();
        var paths = new TestAppPaths(temp.Path);
        paths.EnsureCreated();
        var store = CreateDefinitionStore(paths);

        var definition = SampleDefinition("../escape");
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(definition));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.LoadAsync("..\\escape"));
    }

    [Fact]
    public async Task 导出再导入结构一致()
    {
        using var temp = new TempDirectory();
        var paths = new TestAppPaths(temp.Path);
        paths.EnsureCreated();
        var store = CreateDefinitionStore(paths);

        var definition = SampleDefinition();
        var exportPath = temp.Combine("export.workflow.json");
        await store.ExportAsync(definition, exportPath);

        var imported = await store.ImportAsync(exportPath);
        Assert.NotNull(imported.Definition);
        Assert.False(imported.HasErrors);
        Assert.Equal(
            WorkflowJson.Serialize(definition, indented: false),
            WorkflowJson.Serialize(imported.Definition!, indented: false));
    }

    [Fact]
    public async Task 测试用例保存读取与校验()
    {
        using var temp = new TempDirectory();
        var paths = new TestAppPaths(temp.Path);
        paths.EnsureCreated();
        var store = CreateTestCaseStore(paths);

        var testCase = new WorkflowTestCase
        {
            Id = "tc1",
            Name = "账单路由",
            WorkflowId = "wf1",
            Mode = WorkflowTestMode.Simulated,
            Assertions =
            {
                new WorkflowAssertion
                {
                    Id = "a1",
                    Name = "路由为 billing",
                    Target = "output.route",
                    Operator = AssertionOperator.Equals,
                    ExpectedJson = "\"billing\"",
                },
            },
        };

        await store.SaveAsync(testCase);
        var loaded = await store.LoadAsync("tc1");
        Assert.NotNull(loaded);
        Assert.Equal("账单路由", loaded!.Name);
        Assert.Single(loaded.Assertions);

        var list = await store.ListAsync();
        Assert.Single(list);

        Assert.True(await store.DeleteAsync("tc1"));
        Assert.Null(await store.LoadAsync("tc1"));
    }

    [Fact]
    public async Task 测试用例导入校验断言表达式()
    {
        using var temp = new TempDirectory();
        var paths = new TestAppPaths(temp.Path);
        paths.EnsureCreated();
        var store = CreateTestCaseStore(paths);

        var testCase = new WorkflowTestCase
        {
            Id = "tc2",
            Name = "坏断言",
            WorkflowId = "wf1",
            Assertions = { new WorkflowAssertion { Id = "a1", Target = "1 +", Operator = AssertionOperator.Exists } },
        };
        var path = temp.Combine("bad.testcase.json");
        await File.WriteAllTextAsync(path, WorkflowJson.Serialize(testCase));

        var result = await store.ImportAsync(path);
        Assert.NotNull(result.TestCase);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Issues, i => i.Code == "TC-ASSERT-001");
    }

    [Fact]
    public async Task 运行历史写入查询与清理()
    {
        using var temp = new TempDirectory();
        var database = await TestDatabase.CreateAsync(temp);
        var store = new SqliteWorkflowRunStore(database, NullLogger<SqliteWorkflowRunStore>.Instance);

        for (var i = 0; i < 5; i++)
        {
            await store.AppendAsync(new WorkflowRunRecord
            {
                RunId = "run-" + i,
                WorkflowId = "wf1",
                WorkflowNameSnapshot = "退款流程",
                AppVersion = "0.9.0",
                StartedUtc = DateTimeOffset.Parse("2026-10-17T00:00:00Z").AddMinutes(i),
                EndedUtc = DateTimeOffset.Parse("2026-10-17T00:00:10Z").AddMinutes(i),
                DurationMs = 10_000,
                State = i % 2 == 0 ? WorkflowRunState.Succeeded : WorkflowRunState.Failed,
                TestMode = WorkflowTestMode.Simulated,
                ErrorSummary = i % 2 == 0 ? null : "节点失败",
                DefinitionSnapshotJson = "{}",
                Nodes =
                {
                    new NodeRunRecord { NodeId = "n1", Name = "开始", State = NodeRunState.Succeeded },
                },
                Logs =
                {
                    new WorkflowLogEntry { Sequence = 1, TimestampUtc = DateTimeOffset.UtcNow, Message = "开始运行" },
                },
            });
        }

        Assert.Equal(5, await store.CountAsync());

        var failed = await store.ListAsync(new WorkflowRunQuery(State: WorkflowRunState.Failed));
        Assert.Equal(2, failed.Count);

        var search = await store.ListAsync(new WorkflowRunQuery(SearchText: "run-3"));
        Assert.Single(search);

        var record = await store.GetAsync("run-1");
        Assert.NotNull(record);
        Assert.Single(record!.Nodes);
        Assert.Single(record.Logs);
        Assert.Equal(NodeRunState.Succeeded, record.Nodes[0].State);

        var removed = await store.PruneAsync(2);
        Assert.Equal(3, removed);
        Assert.Equal(2, await store.CountAsync());

        Assert.True(await store.DeleteAsync("run-4"));
        Assert.Equal(1, await store.CountAsync());
    }
}
