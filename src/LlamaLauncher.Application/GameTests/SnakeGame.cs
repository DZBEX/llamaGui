using System.Text.Json.Nodes;

namespace LlamaLauncher.Application.GameTests;

/// <summary>贪吃蛇测试：模型每一步从上下左右中选择一个方向。</summary>
public sealed class SnakeGame : IModelGame
{
    private const int MaxDecisions = 500;
    private readonly Random _random;
    private readonly List<Point> _snake = new();
    private Point _food;

    public SnakeGame(int size = 16, int? seed = null)
    {
        if (size is < 8 or > 30)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "蛇棋盘尺寸需在 8–30 之间。");
        }

        Rows = size;
        Columns = size;
        _random = seed is null ? new Random() : new Random(seed.Value);
        Reset();
    }

    public string Key => "snake";

    public string DisplayName => "贪吃蛇";

    public int Rows { get; }

    public int Columns { get; }

    public bool IsGameOver { get; private set; }

    public bool IsModelTurn => !IsGameOver;

    public string StatusText { get; private set; } = string.Empty;

    public string SecondaryStatusText =>
        $"长度 {_snake.Count} · 分数 {Score} · 第 {Decisions} 步";

    public string Direction { get; private set; } = "right";

    public int Score { get; private set; }

    public int Decisions { get; private set; }

    public void Reset()
    {
        _snake.Clear();
        var row = Rows / 2;
        var column = Columns / 2;
        _snake.Add(new Point(row, column));
        _snake.Add(new Point(row, column - 1));
        _snake.Add(new Point(row, column - 2));
        Direction = "right";
        Score = 0;
        Decisions = 0;
        IsGameOver = false;
        PlaceFood();
        StatusText = "准备好：模型控制蛇寻找食物。";
    }

    public JsonObject BuildState()
    {
        var analyses = AnalyzeActions();
        var snake = new JsonArray();
        foreach (var segment in _snake)
        {
            snake.Add(new JsonObject
            {
                ["row"] = segment.Row,
                ["column"] = segment.Column,
            });
        }

        var legal = new JsonArray();
        var facts = new JsonObject();
        foreach (var analysis in analyses)
        {
            if (analysis.IsSafe)
            {
                legal.Add(analysis.Action);
            }

            facts[analysis.Action] = new JsonObject
            {
                ["currentlyAllowed"] = analysis.IsCurrentlyAllowed,
                ["wallCollision"] = analysis.WallCollision,
                ["bodyCollision"] = analysis.BodyCollision,
                ["safe"] = analysis.IsSafe,
                ["distanceToFoodAfter"] = analysis.DistanceToFoodAfter,
                ["openNeighborsAfter"] = analysis.OpenNeighborsAfter,
            };
        }

        var candidates = new JsonArray();
        foreach (var analysis in RankSafeActions(analyses).Take(2))
        {
            candidates.Add(new JsonObject
            {
                ["action"] = analysis.Action,
                ["distanceToFoodAfter"] = analysis.DistanceToFoodAfter,
                ["openNeighborsAfter"] = analysis.OpenNeighborsAfter,
            });
        }

        return new JsonObject
        {
            ["game"] = "snake",
            ["board"] = new JsonObject
            {
                ["rows"] = Rows,
                ["columns"] = Columns,
                ["coordinates"] = "row 0 is top; snake[0] is the head",
            },
            ["snake"] = snake,
            ["head"] = new JsonObject { ["row"] = _snake[0].Row, ["column"] = _snake[0].Column },
            ["food"] = new JsonObject { ["row"] = _food.Row, ["column"] = _food.Column },
            ["direction"] = Direction,
            ["legalActions"] = legal,
            ["score"] = Score,
            ["length"] = _snake.Count,
            ["decisionNumber"] = Decisions + 1,
            ["decisionFacts"] = new JsonObject
            {
                ["distanceToFood"] = Manhattan(_snake[0], _food),
                ["candidateActions"] = candidates,
                ["actions"] = facts,
                ["guidance"] =
                    "Choose from candidateActions. A smaller distanceToFoodAfter is better; " +
                    "avoid moves with too few openNeighborsAfter when alternatives exist.",
            },
            ["goal"] = "Eat food, avoid walls and the snake body. Never reverse direction.",
        };
    }

    public JsonObject BuildQuestions()
    {
        var analyses = AnalyzeActions();
        var ranked = RankSafeActions(analyses).Take(2).ToArray();
        var selected = ranked.Length > 1
                       && ranked[0].DistanceToFoodAfter < ranked[1].DistanceToFoodAfter
                       && ranked[0].OpenNeighborsAfter > 0
            ? [ranked[0]]
            : ranked;
        if (selected.Length == 0)
        {
            selected = analyses.Where(a => a.IsCurrentlyAllowed).ToArray();
        }

        var criteria = new JsonObject();
        foreach (var analysis in selected)
        {
            criteria[analysis.Action] =
                $"{ActionDisplay(analysis.Action)}；移动后距食物 {analysis.DistanceToFoodAfter} 格，" +
                $"可走邻格 {analysis.OpenNeighborsAfter} 个";
        }

        return new JsonObject
        {
            ["action"] = new JsonObject
            {
                ["type"] = "choice",
                ["instructions"] =
                    "你是贪吃蛇控制模型。criteria 已经移除撞墙、撞身体、反向和明显不安全的方向。" +
                    "读取 state.decisionFacts：优先选择移动后距食物更小、同时保留逃生空间的方向。",
                ["criteria"] = criteria,
            },
        };
    }

    public bool TryApplyModelChoice(string choice, out string actionText, out string resultText)
    {
        var action = choice.Trim().ToLowerInvariant();
        actionText = ActionDisplay(action);
        if (IsGameOver)
        {
            resultText = "对局已结束。";
            return false;
        }

        var (deltaRow, deltaColumn) = Delta(action);
        if (deltaRow == 0 && deltaColumn == 0)
        {
            resultText = $"未知动作“{choice}”；可选 up/down/left/right。";
            return false;
        }

        if (IsOpposite(action, Direction))
        {
            resultText = $"动作“{action}”与当前方向“{Direction}”相反；蛇不能立即反向。";
            return false;
        }

        if (Decisions >= MaxDecisions)
        {
            IsGameOver = true;
            StatusText = $"达到 {MaxDecisions} 步上限，测试结束。";
            resultText = StatusText;
            return false;
        }

        var head = _snake[0];
        var next = new Point(head.Row + deltaRow, head.Column + deltaColumn);
        if (next.Row < 0 || next.Row >= Rows || next.Column < 0 || next.Column >= Columns)
        {
            IsGameOver = true;
            StatusText = "蛇撞到边界，对局结束。";
            resultText = StatusText;
            return false;
        }

        var eating = next == _food;
        for (var i = 0; i < _snake.Count; i++)
        {
            // 不吃食物时尾端会在本步离开，因此移动到当前尾端是合法的。
            if (!eating && i == _snake.Count - 1)
            {
                continue;
            }

            if (_snake[i] == next)
            {
                IsGameOver = true;
                StatusText = "蛇撞到自己，对局结束。";
                resultText = StatusText;
                return false;
            }
        }

        Decisions++;
        Direction = action;
        _snake.Insert(0, next);
        if (eating)
        {
            Score += 10;
            PlaceFood();
            StatusText = $"吃到食物，长度增加到 {_snake.Count}。";
            resultText = $"吃到食物，+10 分，当前 {Score} 分。";
            if (_snake.Count >= Rows * Columns)
            {
                IsGameOver = true;
                StatusText = "蛇占满棋盘，测试完成。";
                resultText = StatusText;
            }
        }
        else
        {
            _snake.RemoveAt(_snake.Count - 1);
            StatusText = $"模型选择“{actionText}”，继续寻找食物。";
            resultText = $"移动成功，头部到达 ({next.Row}, {next.Column})。";
        }

        return true;
    }

    public IReadOnlyList<ModelGameCell> GetCells()
    {
        var body = _snake.Skip(1).ToHashSet();
        var cells = new List<ModelGameCell>(Rows * Columns);
        for (var row = 0; row < Rows; row++)
        {
            for (var column = 0; column < Columns; column++)
            {
                var point = new Point(row, column);
                var tag = "empty";
                var glyph = string.Empty;
                var tooltip = $"({row}, {column})";
                if (point == _snake[0])
                {
                    tag = "snake-head";
                    glyph = Direction switch
                    {
                        "up" => "▲",
                        "down" => "▼",
                        "left" => "◀",
                        _ => "▶",
                    };
                    tooltip = $"蛇头 {tooltip}，方向 {Direction}";
                }
                else if (body.Contains(point))
                {
                    tag = "snake-body";
                    tooltip = $"蛇身 {tooltip}";
                }
                else if (point == _food)
                {
                    tag = "food";
                    glyph = "◆";
                    tooltip = $"食物 {tooltip}";
                }

                cells.Add(new ModelGameCell(row, column, tag, glyph, tooltip));
            }
        }

        return cells;
    }

    private void PlaceFood()
    {
        var free = new List<Point>();
        for (var row = 0; row < Rows; row++)
        {
            for (var column = 0; column < Columns; column++)
            {
                var point = new Point(row, column);
                if (!_snake.Contains(point))
                {
                    free.Add(point);
                }
            }
        }

        if (free.Count == 0)
        {
            IsGameOver = true;
            StatusText = "蛇占满棋盘，测试完成。";
            return;
        }

        _food = free[_random.Next(free.Count)];
    }

    private List<SnakeActionAnalysis> AnalyzeActions()
    {
        var result = new List<SnakeActionAnalysis>(4);
        var head = _snake[0];
        foreach (var action in new[] { "up", "down", "left", "right" })
        {
            var (deltaRow, deltaColumn) = Delta(action);
            var next = new Point(head.Row + deltaRow, head.Column + deltaColumn);
            var allowed = deltaRow != 0 || deltaColumn != 0;
            var reverse = IsOpposite(action, Direction);
            var wall = next.Row < 0 || next.Row >= Rows || next.Column < 0 || next.Column >= Columns;
            var eating = next == _food;
            var body = false;
            for (var i = 0; i < _snake.Count; i++)
            {
                if (!eating && i == _snake.Count - 1)
                {
                    continue;
                }

                if (_snake[i] == next)
                {
                    body = true;
                    break;
                }
            }

            var virtualBody = new HashSet<Point>(_snake);
            if (!eating && _snake.Count > 0)
            {
                virtualBody.Remove(_snake[^1]);
            }

            virtualBody.Add(next);
            var openNeighbors = new[]
                {
                    new Point(next.Row - 1, next.Column),
                    new Point(next.Row + 1, next.Column),
                    new Point(next.Row, next.Column - 1),
                    new Point(next.Row, next.Column + 1),
                }
                .Count(point => point.Row >= 0 && point.Row < Rows
                                && point.Column >= 0 && point.Column < Columns
                                && !virtualBody.Contains(point));
            result.Add(new SnakeActionAnalysis(
                action,
                allowed && !reverse,
                wall,
                body,
                !wall && !body && !reverse,
                wall ? 999 : Manhattan(next, _food),
                openNeighbors));
        }

        return result;
    }

    private static IEnumerable<SnakeActionAnalysis> RankSafeActions(
        IEnumerable<SnakeActionAnalysis> analyses) =>
        analyses
            .Where(a => a.IsSafe)
            .OrderBy(a => a.DistanceToFoodAfter)
            .ThenByDescending(a => a.OpenNeighborsAfter);

    private static (int Row, int Column) Delta(string action) => action switch
    {
        "up" => (-1, 0),
        "down" => (1, 0),
        "left" => (0, -1),
        "right" => (0, 1),
        _ => (0, 0),
    };

    private static bool IsOpposite(string action, string direction) =>
        (action == "up" && direction == "down")
        || (action == "down" && direction == "up")
        || (action == "left" && direction == "right")
        || (action == "right" && direction == "left");

    private static int Manhattan(Point first, Point second) =>
        Math.Abs(first.Row - second.Row) + Math.Abs(first.Column - second.Column);

    private static string ActionDisplay(string action) => action switch
    {
        "up" => "↑ 上",
        "down" => "↓ 下",
        "left" => "← 左",
        "right" => "→ 右",
        _ => action,
    };

    private sealed record SnakeActionAnalysis(
        string Action,
        bool IsCurrentlyAllowed,
        bool WallCollision,
        bool BodyCollision,
        bool IsSafe,
        int DistanceToFoodAfter,
        int OpenNeighborsAfter);

    private readonly record struct Point(int Row, int Column);
}
