using System.Collections.ObjectModel;

namespace LlamaLauncher.App.ViewModels.Lab;

/// <summary>概率条。</summary>
public sealed record AnswerBar(string Label, double Percent, string ValueText);

/// <summary>决策答案卡片（按问题类型呈现 choice/score/noul）。</summary>
public sealed class AnswerCard
{
    public required string QuestionId { get; init; }

    public required string Type { get; init; }

    /// <summary>结论摘要（选中项 / 期望等级 / noul 概率）。</summary>
    public required string Summary { get; init; }

    /// <summary>置信度（可空）。</summary>
    public double? Confidence { get; init; }

    public required ObservableCollection<AnswerBar> Bars { get; init; }

    public bool HasBars => Bars.Count > 0;
}
