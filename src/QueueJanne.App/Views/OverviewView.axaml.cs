using Avalonia.Controls;
using Avalonia.Input;
using QueueJanne.App.ViewModels;

namespace QueueJanne.App.Views;

public partial class OverviewView : UserControl
{
    public OverviewView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_shown is not null)
            {
                _shown.Rows.CollectionChanged -= OnRowsChanged;
            }

            _shown = DataContext as OverviewViewModel;
            if (_shown is not null)
            {
                Grid.Columns[0].IsVisible = _shown.ShowConnectionColumn;
                _shown.Rows.CollectionChanged += OnRowsChanged;
                OnRowsChanged(null, EventArgs.Empty);
            }
        };
    }

    private OverviewViewModel? _shown;

    // Like the tree and the tabs, the overview only shows transfer dead letters when there are some.
    private void OnRowsChanged(object? sender, EventArgs e) =>
        Grid.Columns[4].IsVisible = _shown?.Rows.Any(r => r.Entity.Counts.TransferDeadLetter > 0) == true;

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e) =>
        (DataContext as OverviewViewModel)?.OpenSelectedCommand.Execute(null);

    private void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is OverviewViewModel overview)
        {
            overview.OpenSelectedCommand.Execute(null);
            e.Handled = true;
        }
    }
}
