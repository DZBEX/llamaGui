using System.Windows;

namespace LlamaLauncher.App.Dialogs;

/// <summary>文本输入对话框（可要求二次确认输入同值）。</summary>
public partial class InputDialog : Window
{
    private readonly string? _requiredKeyword;

    public InputDialog(string title, string message, string initialValue, string? requiredKeyword)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        InputBox.Text = initialValue;
        _requiredKeyword = requiredKeyword;
        if (requiredKeyword is not null)
        {
            ConfirmHint.Visibility = Visibility.Visible;
            ConfirmHint.Text = requiredKeyword;
            OkButton.IsEnabled = false;
            InputBox.TextChanged += (_, _) => OkButton.IsEnabled = IsConfirmationSatisfied();
        }

        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.CaretIndex = InputBox.Text.Length;
        };
    }

    public string InputText => InputBox.Text;

    private bool IsConfirmationSatisfied()
        => _requiredKeyword is null
           || (InputBox.Text.Length > 0 && _requiredKeyword.Contains(InputBox.Text.Trim(), StringComparison.Ordinal));

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (IsConfirmationSatisfied())
        {
            DialogResult = true;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
