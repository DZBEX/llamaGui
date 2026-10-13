using System.Windows;
using LlamaLauncher.App.Services;

namespace LlamaLauncher.App.Dialogs;

/// <summary>通用确认对话框（可带风险勾选）。</summary>
public partial class ConfirmDialog : Window
{
    private readonly bool _requiresRiskConfirmation;

    public ConfirmDialog(ConfirmDialogOptions options)
        : this(options, checkboxText: null, defaultChecked: false)
    {
    }

    public ConfirmDialog(ConfirmDialogOptions options, string? checkboxText, bool defaultChecked)
    {
        InitializeComponent();
        Title = options.Title;
        TitleText.Text = options.Title;
        MessageText.Text = options.Message;
        PrimaryButton.Content = options.PrimaryButtonText;
        SecondaryButton.Content = options.SecondaryButtonText ?? "取消";
        SecondaryButton.Visibility = options.SecondaryButtonText is null
            ? Visibility.Collapsed
            : Visibility.Visible;

        if (options.IsDestructive)
        {
            PrimaryButton.Style = (Style)FindResource("App.DestructiveButtonStyle");
        }

        _requiresRiskConfirmation = options.RiskConfirmationText is not null;
        if (_requiresRiskConfirmation)
        {
            RiskCheckBox.Content = options.RiskConfirmationText;
            RiskCheckBox.Visibility = Visibility.Visible;
            RiskCheckBox.Checked += (_, _) => PrimaryButton.IsEnabled = true;
            RiskCheckBox.Unchecked += (_, _) => PrimaryButton.IsEnabled = false;
            PrimaryButton.IsEnabled = false;
        }

        if (checkboxText is not null)
        {
            RiskCheckBox.Content = checkboxText;
            RiskCheckBox.Visibility = Visibility.Visible;
            RiskCheckBox.IsChecked = defaultChecked;
            RiskCheckBox.Checked += (_, _) => IsCheckboxChecked = true;
            RiskCheckBox.Unchecked += (_, _) => IsCheckboxChecked = false;
            if (!_requiresRiskConfirmation)
            {
                PrimaryButton.IsEnabled = true;
            }

            IsCheckboxChecked = defaultChecked;
        }
    }

    public bool IsCheckboxChecked { get; private set; }

    private void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        if (_requiresRiskConfirmation && RiskCheckBox.IsChecked != true)
        {
            return;
        }

        DialogResult = true;
    }

    private void OnSecondaryClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
