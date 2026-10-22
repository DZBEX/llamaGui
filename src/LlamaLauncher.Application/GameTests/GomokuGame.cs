using System.Text.Json.Nodes;

namespace LlamaLauncher.Application.GameTests;

/// <summary>五子棋测试：模型执黑 X，真人执白 O。</summary>
public sealed class GomokuGame : IModelGame
{
    public const int BoardSize = 15;
    private readonly int[,] _board = new int[BoardSize, BoardSize];
    private readonly HashSet<(int Row, int Column)> _winningCells = new();
    private (int Row, int Column)? _lastMove;
    private int _lastPlayer;

    public GomokuGame() => Reset();

    public string Key => "gomoku";

    public string DisplayName => "五子棋";

    public int Rows => BoardSize;

    public int Columns => BoardSize;

    public bool IsGameOver { get; private set; }

    public bool IsModelTurn => !IsGameOver && CurrentPlayer == 1;

    public bool IsHumanTurn => !IsGameOver && CurrentPlayer == 2;

    public int CurrentPlayer { get; private set; }

    public int MoveCount { get; private set; }

    public int Winner { get; private set; }

    public string StatusText { get; private set; } = string.Empty;

    public string SecondaryStatusText => $"落子 {MoveCount}/{BoardSize * BoardSize} · 模型 X · 你 O";

    public void Reset()
    {
        Array.Clear(_board);
        _winningCells.Clear();
        _lastMove = null;
        _lastPlayer = 0;
        MoveCount = 0;
        Winner = 0;
        IsGameOver = false;
        CurrentPlayer = 1;
        StatusText = "轮到模型（黑 X）落子。";
    }

    public JsonObject BuildState()
    {
        var analyses = AnalyzeCandidates();
        var board = new JsonArray();
        for (var row = 0; row < BoardSize; row++)
        {
            var text = new char[BoardSize];
            for (var column = 0; column < BoardSize; column++)
            {
                text[column] = _board[row, column] switch
                {
                    1 => 'X',
                    2 => 'O',
                    _ => '.',
                };
            }

            board.Add(new string(text));
        }

        var candidateFacts = new JsonArray();
        foreach (var analysis in analyses.Take(24))
        {
            candidateFacts.Add(new JsonObject
            {
                ["move"] = CandidateKey(analysis.Row, analysis.Column),
                ["immediateWin"] = analysis.IsImmediateWin,
                ["mustBlock"] = analysis.IsMustBlock,
                ["urgentDefense"] = analysis.IsUrgentDefense,
                ["fork"] = analysis.IsFork,
                ["strongThreats"] = analysis.StrongThreats,
                ["modelLineLength"] = analysis.ModelLineLength,
                ["humanLineLength"] = analysis.HumanLineLength,
                ["reason"] = analysis.Reason,
            });
        }

        var state = new JsonObject
        {
            ["game"] = "gomoku",
            ["boardSize"] = BoardSize,
            ["board"] = board,
            ["model"] = new JsonObject { ["color"] = "X", ["name"] = "黑棋（模型）", ["starts"] = true },
            ["human"] = new JsonObject { ["color"] = "O", ["name"] = "白棋（真人）" },
            ["currentPlayer"] = CurrentPlayer == 1 ? "X" : "O",
            ["moveNumber"] = MoveCount + 1,
            ["winner"] = Winner switch { 1 => "X", 2 => "O", _ => null },
            ["goal"] = "Place X on an empty cell. Form five X cells in a row in any direction and block O.",
            ["coordinates"] = "state.board is indexed from 0. choice keys use r{row}c{column}, also 0-based.",
            ["decisionFacts"] = new JsonObject
            {
                ["candidateCount"] = analyses.Count,
                ["candidates"] = candidateFacts,
                ["rule"] =
                    "If immediateWin is true, any such move wins. If mustBlock is true, it prevents O from winning next turn.",
            },
        };
        if (_lastMove is { } last)
        {
            state["lastMove"] = new JsonObject
            {
                ["row"] = last.Row,
                ["column"] = last.Column,
                ["player"] = _lastPlayer == 1 ? "X" : "O",
            };
        }

        return state;
    }

    public JsonObject BuildQuestions()
    {
        var analyses = AnalyzeCandidates();
        var criteria = new JsonObject();
        foreach (var analysis in analyses.Take(24))
        {
            criteria[CandidateKey(analysis.Row, analysis.Column)] =
                $"{CoordinateText(analysis.Row, analysis.Column)}；{analysis.Reason}";
        }

        return new JsonObject
        {
            ["move"] = new JsonObject
            {
                ["type"] = "choice",
                ["instructions"] =
                    "你是五子棋黑棋 X。criteria 已按战术优先级裁剪。" +
                    "若存在 immediateWin，直接选择形成五连的落点；若存在 mustBlock，只在这些封堵点中选择。" +
                    "其余情况优先提高自己的连续长度，同时降低白棋下一手的威胁。",
                ["criteria"] = criteria,
            },
        };
    }

    public bool TryApplyModelChoice(string choice, out string actionText, out string resultText)
    {
        if (!IsModelTurn)
        {
            actionText = choice;
            resultText = IsGameOver ? "对局已结束。" : "当前轮到真人落子。";
            return false;
        }

        if (!TryParseChoice(choice, out var row, out var column))
        {
            actionText = choice;
            resultText = $"无法解析落点“{choice}”；应使用 r{{row}}c{{column}}，例如 r7c7。";
            return false;
        }

        actionText = $"落子 {CoordinateText(row, column)}";
        return TryPlace(row, column, 1, out resultText);
    }

    public bool TryPlayHuman(int row, int column, out string resultText)
    {
        if (!IsHumanTurn)
        {
            resultText = IsGameOver ? "对局已结束。" : "当前不是真人回合。";
            return false;
        }

        return TryPlace(row, column, 2, out resultText);
    }

    public IReadOnlyList<ModelGameCell> GetCells()
    {
        var cells = new List<ModelGameCell>(BoardSize * BoardSize);
        for (var row = 0; row < BoardSize; row++)
        {
            for (var column = 0; column < BoardSize; column++)
            {
                var player = _board[row, column];
                var winning = _winningCells.Contains((row, column));
                var isLast = _lastMove == (row, column);
                var tag = player switch
                {
                    1 when winning => "black-winning",
                    2 when winning => "white-winning",
                    1 when isLast => "black-last",
                    2 when isLast => "white-last",
                    1 => "black",
                    2 => "white",
                    _ => "empty",
                };
                var glyph = player switch { 1 => "X", 2 => "O", _ => string.Empty };
                cells.Add(new ModelGameCell(
                    row,
                    column,
                    tag,
                    glyph,
                    CoordinateText(row, column)));
            }
        }

        return cells;
    }

    private bool TryPlace(int row, int column, int player, out string resultText)
    {
        if (row < 0 || row >= BoardSize || column < 0 || column >= BoardSize)
        {
            resultText = "落点超出棋盘。";
            return false;
        }

        if (_board[row, column] != 0)
        {
            resultText = $"落点 {CoordinateText(row, column)} 已有棋子。";
            return false;
        }

        _board[row, column] = player;
        _lastMove = (row, column);
        _lastPlayer = player;
        MoveCount++;
        var winningLine = FindWinningLine(row, column, player);
        if (winningLine.Count >= 5)
        {
            Winner = player;
            IsGameOver = true;
            foreach (var point in winningLine)
            {
                _winningCells.Add(point);
            }

            StatusText = player == 1 ? "模型（黑 X）连成五子，获胜。" : "你（白 O）连成五子，获胜。";
            resultText = $"{CoordinateText(row, column)} 形成五连，对局结束。";
            return true;
        }

        if (MoveCount >= BoardSize * BoardSize)
        {
            IsGameOver = true;
            Winner = 0;
            StatusText = "棋盘已满，双方和棋。";
            resultText = StatusText;
            return true;
        }

        CurrentPlayer = player == 1 ? 2 : 1;
        StatusText = CurrentPlayer == 1 ? "轮到模型（黑 X）落子。" : "轮到你（白 O）落子。";
        resultText = $"{CoordinateText(row, column)} 落子成功。";
        return true;
    }

    private List<(int Row, int Column)> FindWinningLine(int row, int column, int player)
    {
        foreach (var (deltaRow, deltaColumn) in new[] { (0, 1), (1, 0), (1, 1), (1, -1) })
        {
            var line = new List<(int, int)> { (row, column) };
            for (var sign = -1; sign <= 1; sign += 2)
            {
                var nextRow = row + deltaRow * sign;
                var nextColumn = column + deltaColumn * sign;
                while (InBounds(nextRow, nextColumn) && _board[nextRow, nextColumn] == player)
                {
                    line.Add((nextRow, nextColumn));
                    nextRow += deltaRow * sign;
                    nextColumn += deltaColumn * sign;
                }
            }

            if (line.Count >= 5)
            {
                return line;
            }
        }

        return new List<(int, int)>();
    }

    private List<GomokuCandidateAnalysis> AnalyzeCandidates()
    {
        var winningMoves = FindImmediateWins(1);
        var blockingMoves = FindImmediateWins(2);
        var threeThreatBlocks = FindThreeThreatBlocks(2);
        var candidates = new HashSet<(int Row, int Column)>();
        candidates.UnionWith(winningMoves);
        candidates.UnionWith(blockingMoves);
        candidates.UnionWith(threeThreatBlocks);

        var hasStone = false;
        for (var row = 0; row < BoardSize; row++)
        {
            for (var column = 0; column < BoardSize; column++)
            {
                if (_board[row, column] == 0)
                {
                    continue;
                }

                hasStone = true;
                for (var deltaRow = -2; deltaRow <= 2; deltaRow++)
                {
                    for (var deltaColumn = -2; deltaColumn <= 2; deltaColumn++)
                    {
                        var candidateRow = row + deltaRow;
                        var candidateColumn = column + deltaColumn;
                        if (InBounds(candidateRow, candidateColumn)
                            && _board[candidateRow, candidateColumn] == 0)
                        {
                            candidates.Add((candidateRow, candidateColumn));
                        }
                    }
                }
            }
        }

        if (!hasStone)
        {
            for (var row = 5; row <= 9; row++)
            {
                for (var column = 5; column <= 9; column++)
                {
                    candidates.Add((row, column));
                }
            }
        }

        var analyses = candidates
            .Select(point => AnalyzeCandidate(
                point.Row,
                point.Column,
                winningMoves.Contains(point),
                blockingMoves.Contains(point),
                threeThreatBlocks.Contains(point)))
            .OrderByDescending(candidate => candidate.IsImmediateWin)
            .ThenByDescending(candidate => candidate.IsMustBlock)
            .ThenByDescending(candidate => candidate.PatternScore)
            .ThenBy(candidate => Math.Abs(candidate.Row - BoardSize / 2)
                                 + Math.Abs(candidate.Column - BoardSize / 2))
            .ToList();

        // 强制局面只保留必胜或必封点，避免模型在无关候选中分散概率。
        if (winningMoves.Count > 0)
        {
            return analyses.Where(candidate => candidate.IsImmediateWin).ToList();
        }

        if (blockingMoves.Count > 0)
        {
            return analyses.Where(candidate => candidate.IsMustBlock).ToList();
        }

        if (threeThreatBlocks.Count > 0)
        {
            return analyses.Where(candidate => candidate.IsUrgentDefense).ToList();
        }

        var forkCandidates = analyses.Where(candidate => candidate.IsFork).ToList();
        if (forkCandidates.Count > 0)
        {
            return forkCandidates;
        }

        return analyses;
    }

    private HashSet<(int Row, int Column)> FindImmediateWins(int player)
    {
        var moves = new HashSet<(int Row, int Column)>();
        for (var row = 0; row < BoardSize; row++)
        {
            for (var column = 0; column < BoardSize; column++)
            {
                if (_board[row, column] == 0 && WouldWin(row, column, player))
                {
                    moves.Add((row, column));
                }
            }
        }

        return moves;
    }

    private bool WouldWin(int row, int column, int player)
    {
        foreach (var (deltaRow, deltaColumn) in new[] { (0, 1), (1, 0), (1, 1), (1, -1) })
        {
            var count = 1
                        + CountDirection(row, column, deltaRow, deltaColumn, player)
                        + CountDirection(row, column, -deltaRow, -deltaColumn, player);
            if (count >= 5)
            {
                return true;
            }
        }

        return false;
    }

    private HashSet<(int Row, int Column)> FindThreeThreatBlocks(int player)
    {
        var blocks = new HashSet<(int Row, int Column)>();
        foreach (var (deltaRow, deltaColumn) in new[] { (0, 1), (1, 0), (1, 1), (1, -1) })
        {
            for (var row = 0; row < BoardSize; row++)
            {
                for (var column = 0; column < BoardSize; column++)
                {
                    if (_board[row, column] != player
                        || (InBounds(row - deltaRow, column - deltaColumn)
                            && _board[row - deltaRow, column - deltaColumn] == player))
                    {
                        continue;
                    }

                    var length = 0;
                    var nextRow = row;
                    var nextColumn = column;
                    while (InBounds(nextRow, nextColumn) && _board[nextRow, nextColumn] == player)
                    {
                        length++;
                        nextRow += deltaRow;
                        nextColumn += deltaColumn;
                    }

                    if (length < 3)
                    {
                        continue;
                    }

                    var beforeRow = row - deltaRow;
                    var beforeColumn = column - deltaColumn;
                    if (InBounds(beforeRow, beforeColumn) && _board[beforeRow, beforeColumn] == 0)
                    {
                        blocks.Add((beforeRow, beforeColumn));
                    }

                    if (InBounds(nextRow, nextColumn) && _board[nextRow, nextColumn] == 0)
                    {
                        blocks.Add((nextRow, nextColumn));
                    }
                }
            }
        }

        return blocks;
    }

    private int CountDirection(int row, int column, int deltaRow, int deltaColumn, int player)
    {
        var count = 0;
        var nextRow = row + deltaRow;
        var nextColumn = column + deltaColumn;
        while (InBounds(nextRow, nextColumn) && _board[nextRow, nextColumn] == player)
        {
            count++;
            nextRow += deltaRow;
            nextColumn += deltaColumn;
        }

        return count;
    }

    private GomokuCandidateAnalysis AnalyzeCandidate(
        int row,
        int column,
        bool immediateWin,
        bool mustBlock,
        bool urgentDefense)
    {
        var modelLine = 1;
        var humanLine = 1;
        var modelOpenEnds = 0;
        var humanOpenEnds = 0;
        var modelThreatScore = 0;
        var humanThreatScore = 0;
        var strongThreats = 0;
        foreach (var (deltaRow, deltaColumn) in new[] { (0, 1), (1, 0), (1, 1), (1, -1) })
        {
            var modelShape = AnalyzeLine(row, column, deltaRow, deltaColumn, 1);
            modelLine = Math.Max(modelLine, modelShape.Length);
            modelOpenEnds += modelShape.OpenEnds;
            var modelLevel = ThreatLevel(modelShape);
            modelThreatScore += ThreatWeight(modelLevel);
            if (modelLevel >= 3)
            {
                strongThreats++;
            }

            var humanShape = AnalyzeLine(row, column, deltaRow, deltaColumn, 2);
            humanLine = Math.Max(humanLine, humanShape.Length);
            humanOpenEnds += humanShape.OpenEnds;
            humanThreatScore += ThreatWeight(ThreatLevel(humanShape));
        }

        var isFork = strongThreats >= 2;
        var patternScore = modelThreatScore + (int)Math.Round(humanThreatScore * 0.85)
                           + modelOpenEnds * 8 + humanOpenEnds * 5
                           - Math.Abs(row - BoardSize / 2) - Math.Abs(column - BoardSize / 2);
        var reason = immediateWin
            ? "必胜点：立即形成五连"
            : mustBlock
                ? "关键封堵：白棋下一步可五连"
                : urgentDefense
                    ? "关键防守：压制白棋连续三子"
                    : isFork
                        ? $"双重威胁：{strongThreats} 个方向形成活三/冲四"
                        : $"进攻连续 {modelLine}，防守连续 {humanLine}";
        return new GomokuCandidateAnalysis(
            row,
            column,
            immediateWin,
            mustBlock,
            urgentDefense,
            modelLine,
            humanLine,
            strongThreats,
            isFork,
            patternScore,
            reason);
    }

    private (int Length, int OpenEnds) AnalyzeLine(
        int row,
        int column,
        int deltaRow,
        int deltaColumn,
        int player)
    {
        var forward = CountDirection(row, column, deltaRow, deltaColumn, player);
        var backward = CountDirection(row, column, -deltaRow, -deltaColumn, player);
        var openEnds = (IsOpenEnd(row, column, deltaRow, deltaColumn, forward) ? 1 : 0)
                       + (IsOpenEnd(row, column, -deltaRow, -deltaColumn, backward) ? 1 : 0);
        return (1 + forward + backward, openEnds);
    }

    private static int ThreatLevel((int Length, int OpenEnds) shape) => shape switch
    {
        { Length: >= 5 } => 5,
        { Length: 4, OpenEnds: >= 1 } => 4,
        { Length: 3, OpenEnds: 2 } => 3,
        { Length: 3, OpenEnds: 1 } => 2,
        { Length: 2, OpenEnds: 2 } => 1,
        _ => 0,
    };

    private static int ThreatWeight(int level) => level switch
    {
        5 => 1_000_000,
        4 => 50_000,
        3 => 5_000,
        2 => 500,
        1 => 50,
        _ => 0,
    };

    private bool IsOpenEnd(int row, int column, int deltaRow, int deltaColumn, int contiguous)
    {
        var endRow = row + deltaRow * (contiguous + 1);
        var endColumn = column + deltaColumn * (contiguous + 1);
        return InBounds(endRow, endColumn) && _board[endRow, endColumn] == 0;
    }

    private static bool TryParseChoice(string choice, out int row, out int column)
    {
        row = -1;
        column = -1;
        var text = new string(choice.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
        if (!text.StartsWith('r'))
        {
            return false;
        }

        var cIndex = text.IndexOf('c');
        return cIndex > 1
            && int.TryParse(text.AsSpan(1, cIndex - 1), out row)
            && int.TryParse(text.AsSpan(cIndex + 1), out column);
    }

    private static bool InBounds(int row, int column) =>
        row >= 0 && row < BoardSize && column >= 0 && column < BoardSize;

    private static string CoordinateText(int row, int column) =>
        $"r{row}c{column}（第 {row + 1} 行，第 {column + 1} 列）";

    private static string CandidateKey(int row, int column) => $"r{row}c{column}";

    private sealed record GomokuCandidateAnalysis(
        int Row,
        int Column,
        bool IsImmediateWin,
        bool IsMustBlock,
        bool IsUrgentDefense,
        int ModelLineLength,
        int HumanLineLength,
        int StrongThreats,
        bool IsFork,
        int PatternScore,
        string Reason);
}
