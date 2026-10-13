using System.Windows;
using LlamaLauncher.Domain;

namespace LlamaLauncher.App.Dialogs;

/// <summary>新建实例对话框：选择配置档案 + 实例名。</summary>
public partial class InstanceCreateDialog : Window
{
    public InstanceCreateDialog(IReadOnlyList<LaunchProfile> profiles)
    {
        InitializeComponent();
        ProfileCombo.ItemsSource = profiles;
        ProfileCombo.DisplayMemberPath = nameof(LaunchProfile.Name);
        ProfileCombo.SelectedIndex = 0;
    }

    public LaunchProfile? SelectedProfile => ProfileCombo.SelectedItem as LaunchProfile;

    public string InstanceName => NameBox.Text;

    private void OnCreateClick(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is null)
        {
            MessageBox.Show(this, "请选择一个启动配置。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
