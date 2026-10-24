using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;

namespace QueueJanne.App.ViewModels;

/// <summary>A tab in the document area: a queue or subscription, or the dead-letter overview.</summary>
public abstract partial class DocumentViewModel(Action<DocumentViewModel> close) : ObservableObject, IDisposable
{
    /// <summary>A preview tab is the single reusable tab a single click in the tree opens.</summary>
    [ObservableProperty]
    private bool _isPreview;

    public abstract string Header { get; }

    public abstract string ToolTip { get; }

    public abstract MaterialIconKind Icon { get; }

    public virtual void Dispose()
    {
    }

    // Screen readers and UI automation name the tab after its view model.
    public override string ToString() => Header;

    partial void OnIsPreviewChanged(bool value) => OnPropertyChanged(nameof(ToolTip));

    [RelayCommand]
    private void Close() => close(this);
}
