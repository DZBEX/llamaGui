using System.Collections.Specialized;
using System.Windows.Controls;
using LlamaLauncher.App.ViewModels;

namespace LlamaLauncher.App.Views;

public partial class LogsView : UserControl
{
    private INotifyCollectionChanged? _observed;

    public LogsView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Attach(e.NewValue as LogsViewModel);
        Unloaded += (_, _) => Attach(null);
    }

    private void Attach(LogsViewModel? viewModel)
    {
        if (_observed is not null)
        {
            _observed.CollectionChanged -= OnChanged;
            _observed = null;
        }

        if (viewModel is not null)
        {
            _observed = viewModel.Lines;
            _observed.CollectionChanged += OnChanged;
        }
    }

    private void OnChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && LinesList.Items.Count > 0)
        {
            LinesList.ScrollIntoView(LinesList.Items[^1]);
        }
    }
}
