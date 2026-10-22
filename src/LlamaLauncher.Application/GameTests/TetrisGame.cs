using System.Text.Json.Nodes;

namespace LlamaLauncher.Application.GameTests;

/// <summary>俄罗斯方块落点动画中的一帧。</summary>
public sealed record TetrisAnimationFrame(
    int Rotation,
    int X,
    int Y,
    IReadOnlyList<TetrisAnimationCell> Cells);

/// <summary>动画帧中当前方块占据的棋盘格。</summary>
public readonly record struct TetrisAnimationCell(int Row, int Column);

/// <summary>完整落点预览；只用于视觉过渡，不改变规则计算。</summary>
public sealed record TetrisPlacementAnimation(
    string PlacementId,
    string PieceType,
    int RotationCount,
    int LinesCleared,
    int MinColumn,
    int MaxColumn,
    IReadOnlyList<TetrisAnimationFrame> Frames);

/// <summary>俄罗斯方块测试：引擎枚举完整落点，模型每次选择一个已评估的放置候选。</summary>
public sealed class TetrisGame : IModelGame
{
    public const int BoardWidth = 10;
    public const int BoardHeight = 20;
    private static readonly int[][][] BaseShapes =
    [
        [[0, 1], [1, 1], [2, 1], [3, 1]],
        [[0, 0], [1, 0], [0, 1], [1, 1]],
        [[0, 1], [1, 1], [2, 1], [1, 0]],
        [[1, 0], [2, 0], [0, 1], [1, 1]],
        [[0, 0], [1, 0], [1, 1], [2, 1]],
        [[0, 0], [0, 1], [1, 1], [2, 1]],
        [[2, 0], [0, 1], [1, 1], [2, 1]],
    ];

    private readonly int[,] _board = new int[BoardHeight, BoardWidth];
    private readonly Random _random;
    private readonly Queue<int> _bag = new();
    private readonly Queue<int> _upcoming = new();
    private int _piece;
    private int _rotation;
    private int _pieceX;
    private int _pieceY;

    public TetrisGame(int? seed = null)
    {
        _random = seed is null ? new Random() : new Random(seed.Value);
        Reset();
    }

    public string Key => "tetris";

    public string DisplayName => "俄罗斯方块";

    public int Rows => BoardHeight;

    public int Columns => BoardWidth;

    public bool IsGameOver { get; private set; }

    public bool IsModelTurn => !IsGameOver;

    public string StatusText { get; private set; } = string.Empty;

    public string SecondaryStatusText =>
        $"分数 {Score} · 消行 {Lines} · 下一块 {PieceLetter(_upcoming.Peek())}";

    public int Score { get; private set; }

    public int Lines { get; private set; }

    public int Decisions { get; private set; }

    public void Reset()
    {
        Array.Clear(_board);
        _bag.Clear();
        _upcoming.Clear();
        EnsureUpcoming();
        Score = 0;
        Lines = 0;
        Decisions = 0;
        IsGameOver = false;
        SpawnNext();
        StatusText = "准备好：模型控制当前方块。";
    }

    public JsonObject BuildState()
    {
        var placements = BuildPlacementCandidates();
        var rendered = RenderBoard();
        var board = new JsonArray();
        foreach (var row in rendered)
        {
            board.Add(row);
        }

        var next = new JsonArray();
        foreach (var type in _upcoming.Take(5))
        {
            next.Add(PieceLetter(type));
        }

        var candidateFacts = new JsonArray();
        foreach (var placement in placements)
        {
            candidateFacts.Add(PlacementToJson(placement));
        }

        var metrics = BoardMetrics.From(_board);
        return new JsonObject
        {
            ["game"] = "tetris",
            ["board"] = board,
            ["boardSize"] = new JsonObject { ["rows"] = BoardHeight, ["columns"] = BoardWidth },
            ["activePiece"] = new JsonObject
            {
                ["type"] = PieceLetter(_piece),
                ["rotation"] = _rotation,
                ["x"] = _pieceX,
                ["y"] = _pieceY,
            },
            ["nextPieces"] = next,
            ["score"] = Score,
            ["lines"] = Lines,
            ["decisionNumber"] = Decisions + 1,
            ["decisionMode"] = "Choose exactly one placement candidate; the engine applies rotation and drop atomically.",
            ["derived"] = new JsonObject
            {
                ["maxHeight"] = metrics.MaxHeight,
                ["holes"] = metrics.Holes,
                ["bumpiness"] = metrics.Bumpiness,
                ["aggregateHeight"] = metrics.AggregateHeight,
                ["rowTransitions"] = metrics.RowTransitions,
                ["columnTransitions"] = metrics.ColumnTransitions,
                ["wells"] = metrics.Wells,
                ["columnHeights"] = new JsonArray(metrics.Heights.Select(h => JsonValue.Create(h)).ToArray()),
                ["candidateCount"] = placements.Count,
                ["candidates"] = candidateFacts,
                ["ranking"] =
                    "p1 has the highest two-piece lookahead score. It combines cleared lines, holes, row/column transitions, wells, stack height and bumpiness.",
            },
            ["goal"] =
                "Choose one legal placement candidate. Clear rows while keeping holes, stack height and bumpiness low.",
            ["legend"] = "I,O,T,S,Z,J,L are tetromino letters; . is an empty cell.",
        };
    }

    public JsonObject BuildQuestions()
    {
        var placements = BuildPlacementCandidates();
        var criteria = new JsonObject();
        foreach (var placement in placements)
        {
            criteria[placement.Id] =
                $"旋转 {placement.Rotation * 90}°，落到第 {placement.MinColumn + 1}" +
                (placement.MaxColumn == placement.MinColumn ? string.Empty : $"–{placement.MaxColumn + 1}") +
                $" 列；预计消 {placement.LinesCleared} 行，空洞 {placement.Holes}，" +
                $"最高 {placement.MaxHeight}，凹凸 {placement.Bumpiness}，" +
                $"考虑下一块 {placement.NextPiece} 后评分 {placement.LookaheadScore}";
        }

        return new JsonObject
        {
            ["action"] = new JsonObject
            {
                ["type"] = "choice",
                ["instructions"] =
                    "你是俄罗斯方块落点选择模型。每个 criteria 都是一次完整的旋转并落底操作。" +
                    "评分已经模拟当前块和下一块的两层落点。" +
                    "先避免新增空洞、井和过高堆叠，再追求消行；p1 是两层搜索当前排名最高的安全候选。",
                ["criteria"] = criteria,
            },
        };
    }

    /// <summary>把原子落点展开为旋转、横移和逐格下落的预览帧。</summary>
    public bool TryBuildPlacementAnimation(string choice, out TetrisPlacementAnimation? animation)
    {
        animation = null;
        if (IsGameOver || !TryParsePlacementId(choice.Trim().ToLowerInvariant(), out var id))
        {
            return false;
        }

        var placement = BuildPlacementCandidates().FirstOrDefault(candidate => candidate.Id == id);
        if (placement is null)
        {
            return false;
        }

        var frames = new List<TetrisAnimationFrame>();
        var rotation = _rotation;
        var x = _pieceX;
        var y = _pieceY;
        void CaptureFrame()
        {
            var cells = PieceCells(_piece, rotation, x, y)
                .Where(cell => cell.Row >= 0 && cell.Row < Rows
                               && cell.Column >= 0 && cell.Column < Columns)
                .Select(cell => new TetrisAnimationCell(cell.Row, cell.Column))
                .ToArray();
            frames.Add(new TetrisAnimationFrame(rotation, x, y, cells));
        }

        var rotationCount = (placement.Rotation - rotation + 4) % 4;
        for (var i = 0; i < rotationCount; i++)
        {
            rotation = (rotation + 1) % 4;
            CaptureFrame();
        }

        var horizontal = Math.Sign(placement.OriginX - x);
        while (x != placement.OriginX)
        {
            x += horizontal;
            CaptureFrame();
        }

        while (y < placement.LandingY)
        {
            y++;
            CaptureFrame();
        }

        if (frames.Count == 0)
        {
            CaptureFrame();
        }

        animation = new TetrisPlacementAnimation(
            placement.Id,
            PieceLetter(_piece),
            rotationCount,
            placement.LinesCleared,
            placement.MinColumn,
            placement.MaxColumn,
            frames);
        return true;
    }

    public bool TryApplyModelChoice(string choice, out string actionText, out string resultText)
    {
        var action = choice.Trim().ToLowerInvariant();
        actionText = action switch
        {
            "left" => "← 左移",
            "right" => "→ 右移",
            "rotate" => "↻ 旋转",
            "drop" => "⇣ 硬降",
            _ => action,
        };
        if (IsGameOver)
        {
            resultText = "对局已结束。";
            return false;
        }

        if (TryParsePlacementId(action, out var placementId))
        {
            var placement = BuildPlacementCandidates()
                .FirstOrDefault(candidate => candidate.Id == placementId);
            if (placement is null)
            {
                actionText = choice;
                resultText = $"落点候选“{choice}”不存在或已经不可用。";
                return false;
            }

            return ApplyPlacement(placement, out actionText, out resultText);
        }

        if (action is not ("left" or "right" or "rotate" or "drop"))
        {
            resultText = $"未知动作“{choice}”；请选择 state.derived.candidates 中的 p1、p2 等候选。";
            return false;
        }

        Decisions++;
        switch (action)
        {
            case "left":
                resultText = TryMove(-1, 0) ? "左移成功。" : "左侧受阻，位置不变。";
                break;
            case "right":
                resultText = TryMove(1, 0) ? "右移成功。" : "右侧受阻，位置不变。";
                break;
            case "rotate":
                resultText = TryRotate() ? "顺时针旋转成功。" : "旋转受阻，位置与旋转不变。";
                break;
            default:
            {
                var dropped = 0;
                while (IsValid(_piece, _rotation, _pieceX, _pieceY + 1))
                {
                    _pieceY++;
                    dropped++;
                }

                Score += dropped * 2;
                LockAndSpawn();
                resultText = $"硬降 {dropped} 格并锁定，飞行分 +{dropped * 2}。";
                StatusText = IsGameOver ? "新方块无法生成，对局结束。" : $"已锁定方块，当前 {Score} 分。";
                return true;
            }
        }

        Gravity();
        StatusText = IsGameOver
            ? "方块堆到顶部，对局结束。"
            : $"模型选择“{actionText}”，当前 {Score} 分。";
        return true;
    }

    public IReadOnlyList<ModelGameCell> GetCells()
    {
        var active = ActiveCells().ToHashSet();
        var cells = new List<ModelGameCell>(Rows * Columns);
        for (var row = 0; row < Rows; row++)
        {
            for (var column = 0; column < Columns; column++)
            {
                var fixedType = _board[row, column];
                var activeHere = active.Contains((row, column));
                var type = activeHere ? _piece : fixedType;
                var tag = type == 0 ? "empty" : (activeHere ? "active-" : "fixed-") + PieceLetter(type);
                cells.Add(new ModelGameCell(
                    row,
                    column,
                    tag,
                    string.Empty,
                    activeHere
                        ? $"当前方块 {PieceLetter(type)} ({row}, {column})"
                        : type == 0 ? $"空位 ({row}, {column})" : $"已固定方块 {PieceLetter(type)} ({row}, {column})"));
            }
        }

        return cells;
    }

    private void Gravity()
    {
        if (IsValid(_piece, _rotation, _pieceX, _pieceY + 1))
        {
            _pieceY++;
            return;
        }

        LockAndSpawn();
    }

    private bool TryMove(int deltaX, int deltaY)
    {
        if (!IsValid(_piece, _rotation, _pieceX + deltaX, _pieceY + deltaY))
        {
            return false;
        }

        _pieceX += deltaX;
        _pieceY += deltaY;
        return true;
    }

    private bool TryRotate()
    {
        var nextRotation = (_rotation + 1) % 4;
        foreach (var (deltaX, deltaY) in new[] { (0, 0), (-1, 0), (1, 0), (-2, 0), (2, 0), (0, -1) })
        {
            if (IsValid(_piece, nextRotation, _pieceX + deltaX, _pieceY + deltaY))
            {
                _rotation = nextRotation;
                _pieceX += deltaX;
                _pieceY += deltaY;
                return true;
            }
        }

        return false;
    }

    private void LockAndSpawn()
    {
        foreach (var (row, column) in PieceCells(_piece, _rotation, _pieceX, _pieceY))
        {
            if (row >= 0 && row < Rows && column >= 0 && column < Columns)
            {
                _board[row, column] = _piece;
            }
        }

        ClearLines();
        SpawnNext();
    }

    private bool ApplyPlacement(
        TetrisPlacement placement,
        out string actionText,
        out string resultText)
    {
        var linesBefore = Lines;
        var scoreBefore = Score;
        _rotation = placement.Rotation;
        _pieceX = placement.OriginX;
        _pieceY = placement.LandingY;
        Score += Math.Max(0, placement.LandingY) * 2;
        Decisions++;
        LockAndSpawn();
        var cleared = Lines - linesBefore;
        actionText =
            $"{placement.Id}：旋转 {placement.Rotation * 90}°，落到第 {placement.MinColumn + 1} 列";
        resultText =
            $"完整落点已执行；实际消行 {cleared}，本步加 {Score - scoreBefore} 分，" +
            $"当前空洞预算 {placement.Holes}。";
        StatusText = IsGameOver
            ? "下一块无法生成，方块堆到顶部，对局结束。"
            : $"模型选择 {placement.Id}，消行 {cleared}，当前 {Score} 分。";
        return true;
    }

    private void ClearLines()
    {
        var cleared = 0;
        for (var row = Rows - 1; row >= 0; row--)
        {
            var full = true;
            for (var column = 0; column < Columns; column++)
            {
                if (_board[row, column] == 0)
                {
                    full = false;
                    break;
                }
            }

            if (!full)
            {
                continue;
            }

            cleared++;
            for (var source = row; source > 0; source--)
            {
                for (var column = 0; column < Columns; column++)
                {
                    _board[source, column] = _board[source - 1, column];
                }
            }

            for (var column = 0; column < Columns; column++)
            {
                _board[0, column] = 0;
            }

            row++;
        }

        if (cleared > 0)
        {
            Lines += cleared;
            Score += cleared switch { 1 => 100, 2 => 300, 3 => 500, _ => 800 };
        }
    }

    private void SpawnNext()
    {
        EnsureUpcoming();
        _piece = _upcoming.Dequeue();
        EnsureUpcoming();
        _rotation = 0;
        _pieceX = 3;
        _pieceY = 0;
        if (!IsValid(_piece, _rotation, _pieceX, _pieceY))
        {
            IsGameOver = true;
        }
    }

    private void EnsureUpcoming()
    {
        while (_upcoming.Count < 6)
        {
            if (_bag.Count == 0)
            {
                var pieces = Enumerable.Range(1, 7).OrderBy(_ => _random.Next()).ToArray();
                foreach (var piece in pieces)
                {
                    _bag.Enqueue(piece);
                }
            }

            _upcoming.Enqueue(_bag.Dequeue());
        }
    }

    private List<TetrisPlacement> BuildPlacementCandidates()
    {
        var nextPiece = _upcoming.Peek();
        var immediate = EnumeratePlacements(_piece, _board);
        var raw = new List<TetrisPlacement>();
        foreach (var candidate in immediate)
        {
            var future = EnumeratePlacements(nextPiece, candidate.Board);
            var nextBest = future.Count == 0
                ? -10_000
                : future.Max(options => options.HeuristicScore);
            var lookahead = candidate.HeuristicScore + (int)Math.Round(nextBest * 0.35);
            raw.Add(new TetrisPlacement(
                string.Empty,
                candidate.Rotation,
                candidate.OriginX,
                candidate.LandingY,
                candidate.MinColumn,
                candidate.MaxColumn,
                candidate.LinesCleared,
                candidate.Metrics.MaxHeight,
                candidate.Metrics.Holes,
                candidate.Metrics.Bumpiness,
                candidate.Metrics.AggregateHeight,
                candidate.Metrics.RowTransitions,
                candidate.Metrics.ColumnTransitions,
                candidate.Metrics.Wells,
                candidate.LandingHeight,
                candidate.HeuristicScore,
                nextBest,
                lookahead,
                PieceLetter(nextPiece)));
        }

        var top = raw
            .OrderByDescending(candidate => candidate.LookaheadScore)
            .ThenBy(candidate => candidate.MaxHeight)
            .ThenBy(candidate => candidate.Holes)
            .Take(10)
            .ToList();
        if (top.Count > 1 && top[0].LookaheadScore - top[1].LookaheadScore >= 60)
        {
            top.RemoveRange(1, top.Count - 1);
        }

        return top
            .Select((candidate, index) => candidate with { Id = $"p{index + 1}" })
            .ToList();
    }

    private List<PlacementSimulation> EnumeratePlacements(int piece, int[,] board)
    {
        var raw = new List<PlacementSimulation>();
        var signatures = new HashSet<string>(StringComparer.Ordinal);
        for (var rotation = 0; rotation < 4; rotation++)
        {
            for (var originX = -3; originX < Columns; originX++)
            {
                var startCells = PieceCells(piece, rotation, originX, 0).ToArray();
                if (startCells.Any(cell => cell.Column < 0 || cell.Column >= Columns)
                    || !CanPlaceInBoard(board, piece, rotation, originX, 0))
                {
                    continue;
                }

                var landingY = 0;
                while (CanPlaceInBoard(board, piece, rotation, originX, landingY + 1))
                {
                    landingY++;
                }

                var cells = PieceCells(piece, rotation, originX, landingY)
                    .OrderBy(cell => cell.Row)
                    .ThenBy(cell => cell.Column)
                    .ToArray();
                var signature = string.Join(';', cells.Select(cell => $"{cell.Row},{cell.Column}"));
                if (!signatures.Add(signature))
                {
                    continue;
                }

                var virtualBoard = (int[,])board.Clone();
                foreach (var (row, column) in cells)
                {
                    if (row >= 0 && row < Rows && column >= 0 && column < Columns)
                    {
                        virtualBoard[row, column] = piece;
                    }
                }

                var cleared = ClearVirtualLines(virtualBoard);
                var metrics = BoardMetrics.From(virtualBoard);
                var minColumn = cells.Min(cell => cell.Column);
                var maxColumn = cells.Max(cell => cell.Column);
                var landingHeight = BoardHeight - (int)Math.Round(cells.Average(cell => cell.Row));
                var heuristic = cleared * 900
                                - metrics.Holes * 90
                                - metrics.RowTransitions * 6
                                - metrics.ColumnTransitions * 5
                                - metrics.Wells * 14
                                - metrics.MaxHeight * 10
                                - metrics.AggregateHeight * 2
                                - metrics.Bumpiness * 3
                                - landingHeight * 2;
                raw.Add(new PlacementSimulation(
                    rotation,
                    originX,
                    landingY,
                    minColumn,
                    maxColumn,
                    cleared,
                    metrics.MaxHeight,
                    metrics.Holes,
                    metrics.Bumpiness,
                    metrics.AggregateHeight,
                    metrics,
                    landingHeight,
                    heuristic,
                    virtualBoard));
            }
        }

        return raw
            .OrderByDescending(candidate => candidate.HeuristicScore)
            .ThenBy(candidate => candidate.MaxHeight)
            .ThenBy(candidate => candidate.Holes)
            .ToList();
    }

    private static bool CanPlaceInBoard(int[,] board, int piece, int rotation, int x, int y)
    {
        foreach (var (row, column) in PieceCells(piece, rotation, x, y))
        {
            if (column < 0 || column >= BoardWidth || row >= BoardHeight)
            {
                return false;
            }

            if (row >= 0 && board[row, column] != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static int ClearVirtualLines(int[,] board)
    {
        var cleared = 0;
        for (var row = BoardHeight - 1; row >= 0; row--)
        {
            var full = true;
            for (var column = 0; column < BoardWidth; column++)
            {
                if (board[row, column] == 0)
                {
                    full = false;
                    break;
                }
            }

            if (!full)
            {
                continue;
            }

            cleared++;
            for (var source = row; source > 0; source--)
            {
                for (var column = 0; column < BoardWidth; column++)
                {
                    board[source, column] = board[source - 1, column];
                }
            }

            for (var column = 0; column < BoardWidth; column++)
            {
                board[0, column] = 0;
            }

            row++;
        }

        return cleared;
    }

    private static JsonObject PlacementToJson(TetrisPlacement placement) => new()
    {
        ["id"] = placement.Id,
        ["rotation"] = placement.Rotation,
        ["originX"] = placement.OriginX,
        ["landingY"] = placement.LandingY,
        ["minColumn"] = placement.MinColumn,
        ["maxColumn"] = placement.MaxColumn,
        ["linesCleared"] = placement.LinesCleared,
        ["maxHeight"] = placement.MaxHeight,
        ["holes"] = placement.Holes,
        ["bumpiness"] = placement.Bumpiness,
        ["aggregateHeight"] = placement.AggregateHeight,
        ["rowTransitions"] = placement.RowTransitions,
        ["columnTransitions"] = placement.ColumnTransitions,
        ["wells"] = placement.Wells,
        ["landingHeight"] = placement.LandingHeight,
        ["heuristicScore"] = placement.HeuristicScore,
        ["nextPiece"] = placement.NextPiece,
        ["nextBestScore"] = placement.NextBestScore,
        ["lookaheadScore"] = placement.LookaheadScore,
    };

    private static bool TryParsePlacementId(string choice, out string id)
    {
        id = choice.Trim().ToLowerInvariant();
        return id.Length >= 2
               && id[0] == 'p'
               && int.TryParse(id.AsSpan(1), out var number)
               && number > 0;
    }

    private bool IsValid(int piece, int rotation, int x, int y)
    {
        foreach (var (row, column) in PieceCells(piece, rotation, x, y))
        {
            if (column < 0 || column >= Columns || row >= Rows)
            {
                return false;
            }

            if (row >= 0 && _board[row, column] != 0)
            {
                return false;
            }
        }

        return true;
    }

    private IEnumerable<(int Row, int Column)> ActiveCells() =>
        PieceCells(_piece, _rotation, _pieceX, _pieceY)
            .Where(p => p.Row >= 0 && p.Row < Rows && p.Column >= 0 && p.Column < Columns);

    private static IEnumerable<(int Row, int Column)> PieceCells(int piece, int rotation, int x, int y)
    {
        var shape = BaseShapes[piece - 1];
        foreach (var cell in shape)
        {
            var localX = cell[0];
            var localY = cell[1];
            for (var i = 0; i < rotation; i++)
            {
                var nextX = 3 - localY;
                localY = localX;
                localX = nextX;
            }

            yield return (y + localY, x + localX);
        }
    }

    private string[] RenderBoard()
    {
        var chars = new char[Rows, Columns];
        for (var row = 0; row < Rows; row++)
        {
            for (var column = 0; column < Columns; column++)
            {
                chars[row, column] = _board[row, column] == 0 ? '.' : PieceLetter(_board[row, column])[0];
            }
        }

        foreach (var (row, column) in ActiveCells())
        {
            if (row >= 0 && row < Rows)
            {
                chars[row, column] = PieceLetter(_piece)[0];
            }
        }

        var lines = new string[Rows];
        for (var row = 0; row < Rows; row++)
        {
            var text = new char[Columns];
            for (var column = 0; column < Columns; column++)
            {
                text[column] = chars[row, column];
            }

            lines[row] = new string(text);
        }

        return lines;
    }

    private static string PieceLetter(int type) => type switch
    {
        1 => "I",
        2 => "O",
        3 => "T",
        4 => "S",
        5 => "Z",
        6 => "J",
        7 => "L",
        _ => ".",
    };

    private sealed record TetrisPlacement(
        string Id,
        int Rotation,
        int OriginX,
        int LandingY,
        int MinColumn,
        int MaxColumn,
        int LinesCleared,
        int MaxHeight,
        int Holes,
        int Bumpiness,
        int AggregateHeight,
        int RowTransitions,
        int ColumnTransitions,
        int Wells,
        int LandingHeight,
        int HeuristicScore,
        int NextBestScore,
        int LookaheadScore,
        string NextPiece);

    private sealed record PlacementSimulation(
        int Rotation,
        int OriginX,
        int LandingY,
        int MinColumn,
        int MaxColumn,
        int LinesCleared,
        int MaxHeight,
        int Holes,
        int Bumpiness,
        int AggregateHeight,
        BoardMetrics Metrics,
        int LandingHeight,
        int HeuristicScore,
        int[,] Board);

    private sealed record BoardMetrics(
        int MaxHeight,
        int Holes,
        int Bumpiness,
        int AggregateHeight,
        int RowTransitions,
        int ColumnTransitions,
        int Wells,
        int[] Heights)
    {
        public static BoardMetrics From(int[,] board)
        {
            var heights = new int[BoardWidth];
            var holes = 0;
            for (var column = 0; column < BoardWidth; column++)
            {
                var firstBlock = BoardHeight;
                for (var row = 0; row < BoardHeight; row++)
                {
                    if (board[row, column] != 0)
                    {
                        firstBlock = row;
                        break;
                    }
                }

                heights[column] = BoardHeight - firstBlock;
                for (var row = firstBlock + 1; row < BoardHeight; row++)
                {
                    if (board[row, column] == 0)
                    {
                        holes++;
                    }
                }
            }

            var bumpiness = 0;
            for (var column = 1; column < BoardWidth; column++)
            {
                bumpiness += Math.Abs(heights[column] - heights[column - 1]);
            }

            var rowTransitions = 0;
            for (var row = 0; row < BoardHeight; row++)
            {
                var previous = 1;
                for (var column = 0; column < BoardWidth; column++)
                {
                    var current = board[row, column] == 0 ? 0 : 1;
                    if (current != previous)
                    {
                        rowTransitions++;
                    }

                    previous = current;
                }

                if (previous != 1)
                {
                    rowTransitions++;
                }
            }

            var columnTransitions = 0;
            for (var column = 0; column < BoardWidth; column++)
            {
                var previous = 0;
                for (var row = 0; row < BoardHeight; row++)
                {
                    var current = board[row, column] == 0 ? 0 : 1;
                    if (current != previous)
                    {
                        columnTransitions++;
                    }

                    previous = current;
                }

                if (previous != 1)
                {
                    columnTransitions++;
                }
            }

            var wells = 0;
            for (var row = 0; row < BoardHeight; row++)
            {
                for (var column = 0; column < BoardWidth; column++)
                {
                    if (board[row, column] != 0)
                    {
                        continue;
                    }

                    var leftBlocked = column == 0 || board[row, column - 1] != 0;
                    var rightBlocked = column == BoardWidth - 1 || board[row, column + 1] != 0;
                    if (!leftBlocked || !rightBlocked)
                    {
                        continue;
                    }

                    for (var depthRow = row; depthRow < BoardHeight && board[depthRow, column] == 0; depthRow++)
                    {
                        wells++;
                    }
                }
            }

            return new BoardMetrics(
                heights.Max(),
                holes,
                bumpiness,
                heights.Sum(),
                rowTransitions,
                columnTransitions,
                wells,
                heights);
        }
    }
}
