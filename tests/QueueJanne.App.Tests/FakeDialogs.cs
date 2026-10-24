using QueueJanne.App.Services;
using QueueJanne.App.ViewModels;
using QueueJanne.Core.Messages;

namespace QueueJanne.App.Tests;

/// <summary>Answers dialogs the way a test says, and remembers what was asked.</summary>
public sealed class FakeDialogs : IDialogService
{
    public List<string> Messages { get; } = [];

    public List<OperationPrompt> Prompts { get; } = [];

    /// <summary>Confirms operations with each option as the dialog proposed it. Set to false to cancel instead.</summary>
    public bool ConfirmOperations { get; set; } = true;

    /// <summary>Picks a command palette item; the default closes the palette.</summary>
    public Func<IReadOnlyList<PaletteItem>, PaletteItem?> ChoosePaletteItem { get; set; } = _ => null;

    public List<IReadOnlyList<PaletteItem>> Palettes { get; } = [];

    /// <summary>Fills in the Send dialog before it's sent; the default cancels it.</summary>
    public Func<SendMessageViewModel, bool> FillSend { get; set; } = _ => false;

    public Task<ConnectionDraft?> ShowAddConnectionAsync() => Task.FromResult<ConnectionDraft?>(null);

    public Task<bool> ConfirmAsync(string title, string message)
    {
        Messages.Add(message);
        return Task.FromResult(true);
    }

    public Task ShowMessageAsync(string title, string message)
    {
        Messages.Add(message);
        return Task.CompletedTask;
    }

    public Task CopyToClipboardAsync(string text) => Task.CompletedTask;

    public Task<bool> SaveTextFileAsync(string suggestedFileName, string content) => Task.FromResult(false);

    public Task<IReadOnlyList<bool>?> ConfirmOperationAsync(OperationPrompt prompt)
    {
        Prompts.Add(prompt);
        return Task.FromResult<IReadOnlyList<bool>?>(ConfirmOperations ? prompt.Options.Select(o => o.IsChecked).ToList() : null);
    }

    public Task<IReadOnlyList<OutgoingMessage>?> ShowSendAsync(SendMessageViewModel dialog) =>
        Task.FromResult(FillSend(dialog) ? dialog.Validate() : null);

    public Task OpenFolderAsync(string path) => Task.CompletedTask;

    public Task<PaletteItem?> ShowPaletteAsync(IReadOnlyList<PaletteItem> items)
    {
        Palettes.Add(items);
        return Task.FromResult(ChoosePaletteItem(items));
    }
}
