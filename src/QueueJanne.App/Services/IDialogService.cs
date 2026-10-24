using QueueJanne.App.ViewModels;
using QueueJanne.Core.Messages;

namespace QueueJanne.App.Services;

/// <summary>What view models need from the window: dialogs, the clipboard and files.</summary>
public interface IDialogService
{
    Task<ConnectionDraft?> ShowAddConnectionAsync();

    Task<bool> ConfirmAsync(string title, string message);

    Task ShowMessageAsync(string title, string message);

    Task CopyToClipboardAsync(string text);

    Task<bool> SaveTextFileAsync(string suggestedFileName, string content);

    /// <summary>Confirms an operation that changes messages. Returns the state of each option, or null when cancelled.</summary>
    Task<IReadOnlyList<bool>?> ConfirmOperationAsync(OperationPrompt prompt);

    /// <summary>The Send message dialog. Returns the messages to send (empty for copy and move), or null when cancelled.</summary>
    Task<IReadOnlyList<OutgoingMessage>?> ShowSendAsync(SendMessageViewModel dialog);

    /// <summary>Shows a folder in Explorer or Finder.</summary>
    Task OpenFolderAsync(string path);

    /// <summary>The command palette. Returns the chosen item, or null when closed.</summary>
    Task<PaletteItem?> ShowPaletteAsync(IReadOnlyList<PaletteItem> items);
}

public sealed record OperationOption(string Label, bool IsChecked);

/// <param name="ConfirmText">When set, the person must type this text before the operation can start.</param>
/// <param name="IsDestructive">Shows the action button in red, for operations that remove messages.</param>
public sealed record OperationPrompt(
    string Title,
    string ActionLabel,
    string Summary,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<OperationOption> Options,
    string? ConfirmText,
    bool IsDestructive = false);
