using System.Text.Json.Nodes;
using LlamaLauncher.Application.GameTests;
using Xunit;

namespace LlamaLauncher.UnitTests.GameTests;

/// <summary>决策模型三款测试游戏的规则、状态与候选动作。</summary>
public sealed class ModelGameTests
{
    [Fact]
    public void 贪吃蛇只暴露安全候选并记录行动后果()
    {
        var game = new SnakeGame(seed: 7);

        var state = game.BuildState();
        Assert.Equal("snake", state["game"]!.GetValue<string>());
        Assert.Equal(3, state["snake"]!.AsArray().Count);
        var criteria = game.BuildQuestions()["action"]!["criteria"]!.AsObject();
        Assert.InRange(criteria.Count, 1, 2);
        Assert.False(criteria.ContainsKey("left"));
        Assert.Equal(4, state["decisionFacts"]!["actions"]!.AsObject().Count);
        Assert.InRange(
            state["decisionFacts"]!["candidateActions"]!.AsArray().Count,
            1,
            2);

        Assert.False(game.TryApplyModelChoice("left", out _, out var reverseResult));
        Assert.Contains("不能立即反向", reverseResult);
        Assert.False(game.IsGameOver);

        Assert.True(game.TryApplyModelChoice("up", out _, out _));
        Assert.Equal("up", game.BuildState()["direction"]!.GetValue<string>());
        Assert.Equal(1, game.Decisions);
    }

    [Fact]
    public void 贪吃蛇撞墙后结束()
    {
        var game = new SnakeGame(seed: 11);
        Assert.True(game.TryApplyModelChoice("up", out _, out _));

        for (var i = 0; i < 20 && !game.IsGameOver; i++)
        {
            game.TryApplyModelChoice("up", out _, out _);
        }

        Assert.True(game.IsGameOver);
        Assert.Contains("边界", game.StatusText);
    }

    [Fact]
    public void 五子棋模型与真人交替落子并可判定五连()
    {
        var game = new GomokuGame();
        var criteria = game.BuildQuestions()["move"]!["criteria"]!.AsObject();
        Assert.InRange(criteria.Count, 1, 24);
        Assert.True(criteria.ContainsKey("r7c7"));

        for (var row = 7; row <= 10; row++)
        {
            Assert.True(game.TryApplyModelChoice($"r{row}c7", out _, out _));
            Assert.True(game.TryPlayHuman(row, 8, out _));
        }

        Assert.True(game.TryApplyModelChoice("r11c7", out _, out _));
        Assert.True(game.IsGameOver);
        Assert.Equal(1, game.Winner);
        Assert.Contains("模型", game.StatusText);
        Assert.Contains(game.GetCells(), c => c.Tag == "black-winning");
    }

    [Fact]
    public void 五子棋拒绝占用格并只暴露合法空位()
    {
        var game = new GomokuGame();
        Assert.True(game.TryApplyModelChoice("r7c7", out _, out _));
        Assert.True(game.TryPlayHuman(8, 8, out _));
        Assert.False(game.TryApplyModelChoice("r7c7", out _, out var result));
        Assert.Contains("已有棋子", result);

        var criteria = game.BuildQuestions()["move"]!["criteria"]!.AsObject();
        Assert.False(criteria.ContainsKey("r7c7"));
        Assert.False(criteria.ContainsKey("r8c8"));
        Assert.InRange(criteria.Count, 1, 24);
    }

    [Fact]
    public void 五子棋一步获胜时只提供必胜落点()
    {
        var game = new GomokuGame();
        for (var column = 3; column <= 6; column++)
        {
            Assert.True(game.TryApplyModelChoice($"r7c{column}", out _, out _));
            Assert.True(game.TryPlayHuman(0, column - 3, out _));
        }

        var criteria = game.BuildQuestions()["move"]!["criteria"]!.AsObject();
        Assert.Equal(2, criteria.Count);
        Assert.True(criteria.ContainsKey("r7c2"));
        Assert.True(criteria.ContainsKey("r7c7"));
    }

    [Fact]
    public void 五子棋唯一封堵时移除无关落点()
    {
        var game = new GomokuGame();
        Assert.True(game.TryApplyModelChoice("r7c2", out _, out _));
        for (var column = 3; column <= 6; column++)
        {
            Assert.True(game.TryPlayHuman(7, column, out _));
            if (column < 6)
            {
                var coordinate = $"r{9 + column}c10";
                var placed = game.TryApplyModelChoice(coordinate, out _, out var result);
                Assert.True(placed, $"{coordinate}: {result}");
            }
        }

        var criteria = game.BuildQuestions()["move"]!["criteria"]!.AsObject();
        Assert.Single(criteria);
        Assert.True(criteria.ContainsKey("r7c7"));
    }

    [Fact]
    public void 五子棋优先保留可形成双重威胁的落点()
    {
        var game = new GomokuGame();
        Assert.True(game.TryApplyModelChoice("r7c5", out _, out _));
        Assert.True(game.TryPlayHuman(0, 0, out _));
        Assert.True(game.TryApplyModelChoice("r7c6", out _, out _));
        Assert.True(game.TryPlayHuman(0, 2, out _));
        Assert.True(game.TryApplyModelChoice("r5c7", out _, out _));
        Assert.True(game.TryPlayHuman(0, 4, out _));
        Assert.True(game.TryApplyModelChoice("r6c7", out _, out _));
        Assert.True(game.TryPlayHuman(0, 6, out _));

        var state = game.BuildState();
        var criteria = game.BuildQuestions()["move"]!["criteria"]!.AsObject();
        Assert.True(criteria.ContainsKey("r7c7"));
        var fork = state["decisionFacts"]!["candidates"]!.AsArray()
            .OfType<JsonObject>()
            .Single(candidate => candidate["move"]!.GetValue<string>() == "r7c7");
        Assert.True(fork["fork"]!.GetValue<bool>());
        Assert.True(fork["strongThreats"]!.GetValue<int>() >= 2);
    }

    [Fact]
    public void 俄罗斯方块只提供预评估落点并原子放置()
    {
        var game = new TetrisGame(seed: 42);
        var initialState = game.BuildState();
        var firstPiece = initialState["activePiece"]!["type"]!.GetValue<string>();
        Assert.Equal(5, initialState["nextPieces"]!.AsArray().Count);
        Assert.InRange(initialState["derived"]!["candidateCount"]!.GetValue<int>(), 1, 10);
        var firstCandidate = initialState["derived"]!["candidates"]![0]!.AsObject();
        Assert.InRange(firstCandidate["lookaheadScore"]!.GetValue<int>(), -1_000_000, 1_000_000);
        Assert.False(string.IsNullOrWhiteSpace(firstCandidate["nextPiece"]!.GetValue<string>()));
        Assert.True(firstCandidate["rowTransitions"]!.GetValue<int>() >= 0);
        Assert.True(firstCandidate["wells"]!.GetValue<int>() >= 0);

        var criteria = game.BuildQuestions()["action"]!["criteria"]!.AsObject();
        Assert.InRange(criteria.Count, 1, 10);
        Assert.True(criteria.ContainsKey("p1"));

        var decisionsBeforeAnimation = game.Decisions;
        Assert.True(game.TryBuildPlacementAnimation("p1", out var animation));
        Assert.NotNull(animation);
        Assert.NotEmpty(animation!.Frames);
        Assert.All(animation.Frames, frame => Assert.NotEmpty(frame.Cells));
        Assert.Equal(decisionsBeforeAnimation, game.Decisions);

        Assert.True(game.TryApplyModelChoice("p1", out _, out var placementResult));
        Assert.Contains("实际消行", placementResult);
        var afterDrop = game.BuildState();
        Assert.Equal(2, afterDrop["decisionNumber"]!.GetValue<int>());
        Assert.NotEqual(firstPiece, afterDrop["activePiece"]!["type"]!.GetValue<string>());
        Assert.Equal(1, game.Decisions);
    }

    [Fact]
    public void 俄罗斯方块保留基础动作兼容路径()
    {
        var game = new TetrisGame(seed: 42);
        var initialY = game.BuildState()["activePiece"]!["y"]!.GetValue<int>();

        Assert.True(game.TryApplyModelChoice("left", out _, out _));
        var afterLeft = game.BuildState()["activePiece"]!.AsObject();
        Assert.Equal(initialY + 1, afterLeft["y"]!.GetValue<int>());

        Assert.True(game.TryApplyModelChoice("drop", out _, out var dropResult));
        Assert.Contains("硬降", dropResult);
    }

    [Fact]
    public void 俄罗斯方块拒绝未知动作()
    {
        var game = new TetrisGame(seed: 1);

        Assert.False(game.TryApplyModelChoice("teleport", out _, out var result));
        Assert.Contains("未知动作", result);
        Assert.Equal(0, game.Decisions);
    }

    [Fact]
    public void 决策读取器兼容对象与直接字符串()
    {
        var objectResponse = new JsonObject
        {
            ["answers"] = new JsonObject
            {
                ["action"] = new JsonObject
                {
                    ["type"] = "choice",
                    ["choice"] = "up",
                    ["confidence"] = 0.82,
                },
            },
        };
        Assert.True(ModelGameDecisionReader.TryReadChoice(objectResponse, "action", out var choice, out var confidence));
        Assert.Equal("up", choice);
        Assert.Equal(0.82, confidence);

        var stringResponse = new JsonObject
        {
            ["answers"] = new JsonObject { ["move"] = "r7c7" },
        };
        Assert.True(ModelGameDecisionReader.TryReadChoice(stringResponse, "move", out choice, out confidence));
        Assert.Equal("r7c7", choice);
        Assert.Null(confidence);
    }
}
