using System.Windows;
using System.IO;
using Microsoft.Win32;

namespace LlamaLauncher.App.Services;

/// <summary>基于 WPF 窗口的对话框服务实现。</summary>
public sealed class DialogService : IDialogService
{
    public Task<bool> ConfirmAsync(ConfirmDialogOptions options)
    {
        var dialog = new Dialogs.ConfirmDialog(options)
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        return Task.FromResult(dialog.ShowDialog() == true);
    }

    public Task ShowInfoAsync(string title, string message)
    {
        ShowMessage(title, message, MessageBoxImage.Information);
        return Task.CompletedTask;
    }

    public Task ShowWarningAsync(string title, string message)
    {
        ShowMessage(title, message, MessageBoxImage.Warning);
        return Task.CompletedTask;
    }

    public Task ShowErrorAsync(string title, string message)
    {
        ShowMessage(title, message, MessageBoxImage.Error);
        return Task.CompletedTask;
    }

    public Task<string?> PromptAsync(string title, string message, string initialValue = "")
    {
        var dialog = new Dialogs.InputDialog(title, message, initialValue, requiredKeyword: null)
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        return Task.FromResult(dialog.ShowDialog() == true ? dialog.InputText : null);
    }

    public Task<string?> PromptWithConfirmationAsync(
        string title,
        string message,
        string requiredConfirmationKeyword)
    {
        var dialog = new Dialogs.InputDialog(title, message, string.Empty, requiredConfirmationKeyword)
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        return Task.FromResult(dialog.ShowDialog() == true ? dialog.InputText : null);
    }

    public Task<(bool Confirmed, bool CheckboxChecked)> ConfirmWithCheckboxAsync(
        ConfirmDialogOptions options,
        string checkboxText,
        bool defaultChecked = false)
    {
        var dialog = new Dialogs.ConfirmDialog(options, checkboxText, defaultChecked)
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        var confirmed = dialog.ShowDialog() == true;
        return Task.FromResult((confirmed, dialog.IsCheckboxChecked));
    }

    public string? PickOpenFile(string title, string filter, string? initialDirectory = null)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            CheckFileExists = true,
        };
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickFolder(string title, string? initialDirectory = null)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
        };
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public string? PickSaveFile(
        string title,
        string filter,
        string? defaultFileName = null,
        string? initialDirectory = null)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            Filter = filter,
            FileName = defaultFileName,
            OverwritePrompt = true,
        };
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static void ShowMessage(string title, string message, MessageBoxImage icon)
    {
        var owner = System.Windows.Application.Current?.MainWindow;
        if (owner is not null && owner.IsVisible)
        {
            MessageBox.Show(owner, message, title, MessageBoxButton.OK, icon);
        }
        else
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, icon);
        }
    }
}
