namespace LlamaLauncher.App.ViewModels;

/// <summary>下拉选项：原始值 + 本地化显示名（用于绑定 ComboBox 的 SelectedValue/DisplayMemberPath）。</summary>
public sealed record ChoiceOption<T>(T Value, string DisplayName);
