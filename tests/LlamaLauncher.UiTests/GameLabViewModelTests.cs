using System.Text.Json.Nodes;
using LlamaLauncher.App.Services;
using LlamaLauncher.App.ViewModels.Lab;
using LlamaLauncher.Application.Abstractions;
using LlamaLauncher.Application.Workflows;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LlamaLauncher.UiTests;

/// <summary>游戏测试页：三款游戏都能调用模型，并把当轮状态与答复写入可见记录。</summary>
public sealed class GameLabViewModelTests
{
    [Fact]
    public async Task 游戏页首次激活优先选择Kev模型()
    {
        var vm = new GameLabViewModel(
            new FirstChoiceClient(),
            new MultiReadyResolver(),
            new NotificationService(),
            NullLogger<GameLabViewModel>.Instance);

        await vm.OnActivatedAsync();

        Assert.Equal(2, vm.Instances.Count);
        Assert.Equal("Kev-4B-Q4_K_M 实例", vm.SelectedInstance?.Target.InstanceName);
        Assert.True(vm.SelectedInstance?.IsUsable);
    }

    [Fact]
    public async Task 三款游戏逐步调用模型并保存完整决策记录()
    {
        var client = new FirstChoiceClient();
        var resolver = new ReadyResolver();
        var vm = new GameLabViewModel(
            client,
            resolver,
            new NotificationService(),
            NullLogger<GameLabViewModel>.Instance);

        await vm.OnActivatedAsync();
        Assert.Single(vm.Instances);

        foreach (var game in vm.Games)
        {
            vm.SelectedGame = game;
            vm.NewGameCommand.Execute(null);
            await vm.StepCommand.ExecuteAsync(null);

            var record = Assert.Single(vm.Decisions);
            Assert.True(record.Success, $"{game.Title}: {record.Outcome}");
            Assert.Contains("\"game\"", record.StateJson, StringComparison.Ordinal);
            if (record.Source == GameDecisionSource.AiModel)
            {
                Assert.Contains("\"answers\"", record.RawResponseJson, StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains("\"algorithm\"", record.RawResponseJson, StringComparison.Ordinal);
            }

            Assert.NotNull(record.AnswerJson);
            Assert.Equal(record, vm.SelectedDecision);
            Assert.NotEmpty(record.SourceText);
            Assert.InRange(record.CandidateCount, 1, 24);
            if (record.Source == GameDecisionSource.Algorithm)
            {
                Assert.Equal(1, record.CandidateCount);
            }
            else
            {
                Assert.True(record.CandidateCount > 1);
            }
        }

        Assert.InRange(client.CallCount, 1, 3);
    }

    private sealed class FirstChoiceClient : IModelDecisionClient
    {
        public int CallCount { get; private set; }

        public Task<DecisionCallResult> CallAsync(
            DecisionCallRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Assert.False(string.IsNullOrWhiteSpace(request.State["game"]?.GetValue<string>()));
            var firstChoice = request.Questions
                .Select(pair => pair.Value)
                .OfType<JsonObject>()
                .Select(question => question["criteria"] as JsonObject)
                .First(criteria => criteria is { Count: > 0 })!
                .First()
                .Key;
            var response = new JsonObject
            {
                ["answers"] = new JsonObject
                {
                    [request.Questions.ContainsKey("move") ? "move" : "action"] = new JsonObject
                    {
                        ["type"] = "choice",
                        ["choice"] = firstChoice,
                        ["confidence"] = 0.77,
                    },
                },
                ["usage"] = new JsonObject { ["input_tokens"] = 120 },
            };
            return Task.FromResult(DecisionCallResult.Ok(response, response.ToJsonString(), TimeSpan.FromMilliseconds(42)));
        }
    }

    private sealed class ReadyResolver : IWorkflowModelResolver
    {
        private readonly WorkflowModelTarget _target = new(
            Guid.NewGuid().ToString("D"),
            "测试模型",
            "http://127.0.0.1:8081",
            true,
            null,
            new JsonObject());

        public Task<IReadOnlyList<WorkflowModelTarget>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkflowModelTarget>>([_target]);

        public Task<WorkflowModelTarget?> ResolveAsync(
            string? instanceId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<WorkflowModelTarget?>(_target);

        public Task<string?> GetApiKeyAsync(string instanceId, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
    }

    private sealed class MultiReadyResolver : IWorkflowModelResolver
    {
        private readonly IReadOnlyList<WorkflowModelTarget> _targets =
        [
            new(
                Guid.NewGuid().ToString("D"),
                "Laya-Q8_0 实例",
                "http://127.0.0.1:8081",
                true,
                null,
                new JsonObject()),
            new(
                Guid.NewGuid().ToString("D"),
                "Kev-4B-Q4_K_M 实例",
                "http://127.0.0.1:8082",
                true,
                null,
                new JsonObject()),
        ];

        public Task<IReadOnlyList<WorkflowModelTarget>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_targets);

        public Task<WorkflowModelTarget?> ResolveAsync(
            string? instanceId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_targets.FirstOrDefault(target => target.InstanceId == instanceId));

        public Task<string?> GetApiKeyAsync(string instanceId, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
    }
}
