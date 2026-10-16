using System.Text.Json;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Application.Workflows;
using LlamaLauncher.Domain.Workflows;
using Microsoft.Extensions.Logging;

namespace LlamaLauncher.Infrastructure.Workflows;

/// <summary>测试用例的 JSON 文件存储：<c>workflows/testcases/&lt;id&gt;.testcase.json</c>。</summary>
public sealed class JsonWorkflowTestCaseStore : IWorkflowTestCaseStore
{
    private const string FileSuffix = ".testcase.json";

    private readonly string _rootDirectory;
    private readonly ILogger<JsonWorkflowTestCaseStore> _logger;

    public JsonWorkflowTestCaseStore(IAppPaths paths, ILogger<JsonWorkflowTestCaseStore> logger)
    {
        _rootDirectory = paths.WorkflowTestCasesDirectory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<WorkflowTestCase>> ListAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<WorkflowTestCase>();
        if (!Directory.Exists(_rootDirectory))
        {
            return results;
        }

        foreach (var file in Directory.EnumerateFiles(_rootDirectory, "*" + FileSuffix))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var text = await WorkflowFileIo.ReadAllTextLimitedAsync(
                        file,
                        WorkflowLimits.MaxTestCaseFileBytes,
                        cancellationToken)
                    .ConfigureAwait(false);
                var testCase = WorkflowJson.Deserialize<WorkflowTestCase>(text);
                if (testCase is not null)
                {
                    results.Add(testCase);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException)
            {
                _logger.LogWarning(ex, "读取测试用例失败：{File}", file);
            }
        }

        return results.OrderByDescending(c => c.UpdatedUtc).ToList();
    }

    public async Task<WorkflowTestCase?> LoadAsync(string testCaseId, CancellationToken cancellationToken = default)
    {
        var path = PathFor(testCaseId);
        if (!File.Exists(path))
        {
            return null;
        }

        var text = await WorkflowFileIo.ReadAllTextLimitedAsync(
                path,
                WorkflowLimits.MaxTestCaseFileBytes,
                cancellationToken)
            .ConfigureAwait(false);
        return WorkflowJson.Deserialize<WorkflowTestCase>(text);
    }

    public async Task SaveAsync(WorkflowTestCase testCase, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        if (!WorkflowFileIo.IsSafeId(testCase.Id))
        {
            throw new InvalidOperationException("测试用例 ID 无效（只允许字母、数字、连字符与下划线）。");
        }

        if (string.IsNullOrWhiteSpace(testCase.Name))
        {
            throw new InvalidOperationException("测试用例名称不能为空。");
        }

        testCase.SchemaVersion = 1;
        testCase.UpdatedUtc = DateTimeOffset.UtcNow;
        if (testCase.CreatedUtc == default)
        {
            testCase.CreatedUtc = testCase.UpdatedUtc;
        }

        await WorkflowFileIo.WriteAtomicAsync(PathFor(testCase.Id), WorkflowJson.Serialize(testCase), cancellationToken)
            .ConfigureAwait(false);
        _logger.LogInformation("测试用例已保存：{Name}（{Id}）", testCase.Name, testCase.Id);
    }

    public Task<bool> DeleteAsync(string testCaseId, CancellationToken cancellationToken = default)
    {
        var path = PathFor(testCaseId);
        if (!File.Exists(path))
        {
            return Task.FromResult(false);
        }

        File.Delete(path);
        _logger.LogInformation("测试用例已删除：{Id}", testCaseId);
        return Task.FromResult(true);
    }

    public async Task<WorkflowTestCaseLoadResult> ImportAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        string text;
        try
        {
            text = await WorkflowFileIo.ReadAllTextLimitedAsync(
                    sourcePath,
                    WorkflowLimits.MaxTestCaseFileBytes,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return new WorkflowTestCaseLoadResult(null, new[]
            {
                new WorkflowIssue(WorkflowIssueSeverity.Error, "TC-IO-001", $"无法读取测试用例文件：{ex.Message}"),
            });
        }

        WorkflowTestCase? testCase;
        try
        {
            testCase = WorkflowJson.Deserialize<WorkflowTestCase>(text);
        }
        catch (JsonException ex)
        {
            return new WorkflowTestCaseLoadResult(null, new[]
            {
                new WorkflowIssue(WorkflowIssueSeverity.Error, "TC-IO-002", $"测试用例文件不是有效的 JSON：{ex.Message}"),
            });
        }

        if (testCase is null)
        {
            return new WorkflowTestCaseLoadResult(null, new[]
            {
                new WorkflowIssue(WorkflowIssueSeverity.Error, "TC-IO-003", "测试用例文件为空或格式不正确。"),
            });
        }

        var issues = new List<WorkflowIssue>();
        if (testCase.SchemaVersion > 1)
        {
            issues.Add(new WorkflowIssue(
                WorkflowIssueSeverity.Error,
                "TC-SCHEMA-001",
                $"测试用例 schema 版本为 {testCase.SchemaVersion}，高于当前支持的 1。"));
        }

        if (!WorkflowFileIo.IsSafeId(testCase.Id))
        {
            issues.Add(new WorkflowIssue(
                WorkflowIssueSeverity.Error,
                "TC-IO-004",
                "测试用例 ID 缺失或包含不允许的字符。",
                hint: "ID 只允许字母、数字、连字符与下划线。"));
        }

        foreach (var assertion in testCase.Assertions)
        {
            var error = Domain.Workflows.Expressions.WorkflowExpression.Validate(assertion.Target);
            if (error is not null)
            {
                issues.Add(new WorkflowIssue(
                    WorkflowIssueSeverity.Error,
                    "TC-ASSERT-001",
                    $"断言“{assertion.Name}”的目标表达式无效：{error}",
                    hint: "请修正断言的目标表达式。"));
            }
        }

        return new WorkflowTestCaseLoadResult(testCase, issues);
    }

    public async Task ExportAsync(
        WorkflowTestCase testCase,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new InvalidOperationException("导出目标路径无效。");
        }

        await WorkflowFileIo.WriteAtomicAsync(destinationPath, WorkflowJson.Serialize(testCase), cancellationToken)
            .ConfigureAwait(false);
        _logger.LogInformation("测试用例已导出：{Path}", destinationPath);
    }

    private string PathFor(string testCaseId)
    {
        if (!WorkflowFileIo.IsSafeId(testCaseId))
        {
            throw new InvalidOperationException("测试用例 ID 无效（只允许字母、数字、连字符与下划线）。");
        }

        return Path.Combine(_rootDirectory, testCaseId + FileSuffix);
    }
}
