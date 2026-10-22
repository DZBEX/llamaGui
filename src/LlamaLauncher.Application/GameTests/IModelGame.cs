using System.Text.Json.Nodes;

namespace LlamaLauncher.Application.GameTests;

/// <summary>游戏棋盘单元格的语义快照；颜色与布局由 UI 层负责。</summary>
public sealed record ModelGameCell(
    int Row,
    int Column,
    string Tag,
    string Glyph = "",
    string ToolTip = "");

/// <summary>
/// 可由决策模型逐步操作的游戏会话。实现只负责规则、JSON 状态与候选问题，
/// 不依赖 WPF，便于单独测试并在其他客户端复用。
/// </summary>
public interface IModelGame
{
    string Key { get; }

    string DisplayName { get; }

    int Rows { get; }

    int Columns { get; }

    bool IsGameOver { get; }

    /// <summary>当前是否轮到模型；五子棋等待真人落子时为 false。</summary>
    bool IsModelTurn { get; }

    string StatusText { get; }

    string SecondaryStatusText { get; }

    /// <summary>保持当前游戏类型并重置棋盘与会话统计。</summary>
    void Reset();

    /// <summary>本轮发送给模型的完整游戏状态。</summary>
    JsonObject BuildState();

    /// <summary>本轮发送给模型的 TypeSafe System One 问题。</summary>
    JsonObject BuildQuestions();

    /// <summary>应用模型返回的 choice；失败时给出可供界面展示的原因。</summary>
    bool TryApplyModelChoice(string choice, out string actionText, out string resultText);

    IReadOnlyList<ModelGameCell> GetCells();
}

/// <summary>从 /v1/systemone 响应中读取 choice 与置信度。</summary>
public static class ModelGameDecisionReader
{
    public static bool TryReadChoice(
        JsonObject? response,
        string questionId,
        out string choice,
        out double? confidence)
    {
        choice = string.Empty;
        confidence = null;
        if (response?["answers"] is not JsonObject answers
            || answers[questionId] is not { } answer)
        {
            return false;
        }

        if (answer is JsonValue value && value.TryGetValue<string>(out var direct))
        {
            choice = direct.Trim();
            return choice.Length > 0;
        }

        if (answer is not JsonObject answerObject)
        {
            return false;
        }

        confidence = answerObject["confidence"]?.GetValue<double?>();
        foreach (var property in new[] { "choice", "key", "label", "value" })
        {
            if (answerObject[property] is JsonValue candidate
                && candidate.TryGetValue<string>(out var text)
                && !string.IsNullOrWhiteSpace(text))
            {
                choice = text.Trim();
                return true;
            }
        }

        return false;
    }
}
