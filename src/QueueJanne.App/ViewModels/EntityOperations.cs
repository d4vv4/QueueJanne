using QueueJanne.App.Services;
using QueueJanne.Core.Connections;
using QueueJanne.Core.Entities;
using QueueJanne.Core.Messages;
using QueueJanne.Core.ServiceBus;

namespace QueueJanne.App.ViewModels;

/// <summary>What an operation did, for the status line and for updating the open tab.</summary>
/// <param name="Complete">True when it finished without errors, so the messages it acted on are gone from the sub-queue.</param>
/// <param name="SequenceNumbers">The messages that left the sub-queue (none for a copy), or null when it acted on a whole sub-queue.</param>
internal sealed record OperationOutcome(string Summary, bool Complete, IReadOnlyList<long>? SequenceNumbers = null);

/// <summary>
/// Resubmit, delete and purge, shared by tabs and the Servers tree. Each checks the read-only flag, confirms with warnings
/// from the entity's live settings, runs, reports problems and refreshes the connection's counts.
/// Returns null when nothing ran.
/// </summary>
internal static class EntityOperations
{
    public static async Task<OperationOutcome?> ResubmitAsync(ConnectionNodeViewModel connection, EntityInfo entity, SubQueueKind subQueue, IReadOnlyList<MessageRowViewModel>? rows, IDialogService dialogs, IProgress<string> progress)
    {
        if (subQueue == SubQueueKind.Active)
        {
            return new OperationOutcome("Resubmit works on the dead-letter queue. Switch to it first.", Complete: false);
        }

        if (!await EnsureWritableAsync(connection, dialogs))
        {
            return null;
        }

        var address = EntityAddress.For(entity, subQueue);
        var target = ResubmitAdvice.TargetOf(address);
        var snapshot = connection.Snapshot;
        var choice = await dialogs.ConfirmOperationAsync(new OperationPrompt(
            "Resubmit messages",
            "Resubmit",
            $"Resubmit {Describe(rows, entity, subQueue)} from {address.DisplayName} to '{target}'? "
                + "Each copy is sent first, and the original is removed from the dead-letter queue only after that.",
            snapshot is null ? [] : ResubmitAdvice.Warnings(snapshot, address, newMessageId: false),
            [new OperationOption("Give each copy a new message ID (the old one is kept in the OriginalMessageId property)", snapshot is not null && ResubmitAdvice.TargetDetectsDuplicates(snapshot, address))],
            ConfirmText(connection, entity)));
        if (choice is null)
        {
            return null;
        }

        return await RunAsync(connection, dialogs, "Resubmit", progress, "Resubmitting", rows?.Select(r => r.SequenceNumber).ToList(), async reporter =>
        {
            var request = new ResubmitRequest
            {
                SequenceNumbers = rows?.Select(r => r.SequenceNumber).ToList(),
                NewMessageId = choice[0],
            };
            var result = await Journal.RecordAsync(
                Entry(connection, "resubmit", target),
                connection.Client,
                address,
                rows?.Select(r => r.Message).ToList(),
                request.SequenceNumbers,
                () => connection.Client.ResubmitAsync(address, request, reporter),
                Describe);
            return Describe(result);

            static (string, IReadOnlyList<string>) Describe(ResubmitResult r) =>
                (Summary($"Resubmitted {Formatting.Count(r.Resubmitted)} to '{r.Target}'", r.NotFound), r.Errors);
        });
    }

    public static async Task<OperationOutcome?> DeleteAsync(ConnectionNodeViewModel connection, EntityInfo entity, SubQueueKind subQueue, IReadOnlyList<MessageRowViewModel> rows, IDialogService dialogs, IProgress<string> progress)
    {
        if (rows.Count == 0 || !await EnsureWritableAsync(connection, dialogs))
        {
            return null;
        }

        var address = EntityAddress.For(entity, subQueue);
        var choice = await dialogs.ConfirmOperationAsync(new OperationPrompt(
            "Delete messages",
            "Delete",
            $"Delete {Describe(rows, entity, subQueue)} from {address.DisplayName}? This can't be undone; export them first if you might need them.",
            DeleteAdvice.ForDelete(entity, subQueue),
            [],
            ConfirmText(connection, entity),
            IsDestructive: true));
        if (choice is null)
        {
            return null;
        }

        return await RunAsync(connection, dialogs, "Delete", progress, "Deleting", rows.Select(r => r.SequenceNumber).ToList(), async reporter =>
        {
            var request = new DeleteRequest
            {
                SequenceNumbers = rows.Select(r => r.SequenceNumber).ToList(),
                ScheduledSequenceNumbers = rows.Where(r => r.Message.State == MessageState.Scheduled).Select(r => r.SequenceNumber).ToList(),
            };
            var result = await Journal.RecordAsync(
                Entry(connection, "delete"),
                connection.Client,
                address,
                rows.Select(r => r.Message).ToList(),
                request.SequenceNumbers,
                () => connection.Client.DeleteAsync(address, request, reporter),
                Describe);
            return Describe(result);

            static (string, IReadOnlyList<string>) Describe(MessageOperationResult r) =>
                (Summary($"Deleted {Formatting.Count(r.Count)}", r.NotFound), r.Errors);
        });
    }

    public static async Task<OperationOutcome?> PurgeAsync(ConnectionNodeViewModel connection, EntityInfo entity, SubQueueKind preselected, IDialogService dialogs, IProgress<string> progress)
    {
        if (entity.Kind == EntityKind.Topic || !await EnsureWritableAsync(connection, dialogs))
        {
            return entity.Kind == EntityKind.Topic ? new OperationOutcome("Select a queue or subscription to purge.", Complete: false) : null;
        }

        SubQueueKind[] kinds = [SubQueueKind.Active, SubQueueKind.DeadLetter, SubQueueKind.TransferDeadLetter];
        var counts = entity.Counts;
        var choice = await dialogs.ConfirmOperationAsync(new OperationPrompt(
            "Purge",
            "Purge",
            $"Remove every message from {entity.Path}? Choose what to empty. This can't be undone.",
            DeleteAdvice.ForPurge(entity, kinds),
            [
                new OperationOption($"Active messages ({Formatting.Count(counts.Active)})", preselected == SubQueueKind.Active),
                new OperationOption($"Dead-letter queue ({Formatting.Count(counts.DeadLetter)})", preselected == SubQueueKind.DeadLetter),
                new OperationOption($"Transfer dead-letter queue ({Formatting.Count(counts.TransferDeadLetter)})", preselected == SubQueueKind.TransferDeadLetter),
            ],
            ConfirmText(connection, entity),
            IsDestructive: true));
        var chosen = choice is null ? [] : kinds.Where((_, i) => choice[i]).ToList();
        if (chosen.Count == 0)
        {
            return null;
        }

        return await RunAsync(connection, dialogs, "Purge", progress, "Purging", null, async reporter =>
        {
            var parts = new List<string>();
            var errors = new List<string>();
            foreach (var kind in chosen)
            {
                var address = EntityAddress.For(entity, kind);
                progress.Report($"Backing up {Name(kind)} to the journal…");
                var result = await Journal.RecordAsync(
                    Entry(connection, "purge"),
                    connection.Client,
                    address,
                    known: null,
                    sequenceNumbers: null,
                    () => connection.Client.PurgeAsync(address, reporter),
                    r => ($"Purged {Formatting.Count(r.Count)}", r.Errors));
                parts.Add($"{Formatting.Count(result.Count)} {Name(kind)}");
                errors.AddRange(result.Errors);
            }

            return ($"Purged {string.Join(" and ", parts)} from {entity.Path}", errors);
        });
    }

    private static async Task<OperationOutcome> RunAsync(
        ConnectionNodeViewModel connection,
        IDialogService dialogs,
        string operation,
        IProgress<string> progress,
        string verb,
        IReadOnlyList<long>? sequenceNumbers,
        Func<IProgress<int>, Task<(string Summary, IReadOnlyList<string> Errors)>> run)
    {
        progress.Report($"{verb}…");
        try
        {
            var (summary, errors) = await run(new Progress<int>(done => progress.Report($"{verb}… {Formatting.Count(done)} done")));
            if (errors.Count > 0)
            {
                await dialogs.ShowMessageAsync($"{operation} didn't finish", summary + ".\n\n" + string.Join("\n", errors.Take(10)));
            }

            return new OperationOutcome(summary, errors.Count == 0, sequenceNumbers);
        }
        catch (Exception ex)
        {
            await dialogs.ShowMessageAsync($"{operation} failed", ConnectionErrors.Explain(ex));
            return new OperationOutcome($"{operation} failed", Complete: false);
        }
        finally
        {
            await connection.LoadAsync();
        }
    }

    /// <summary>Sends what the Send dialog built. Sending adds messages and removes none, so it isn't journalled.</summary>
    public static async Task<string> SendAsync(SendMessageViewModel dialog, IReadOnlyList<OutgoingMessage> messages, IDialogService dialogs)
    {
        var connection = dialog.Connection!;
        var target = CanonicalTarget(dialog);
        try
        {
            await connection.Client.SendAsync(target, messages);
            return $"Sent {Formatting.Count(messages.Count)} to '{target}' on {connection.Profile.Name}";
        }
        catch (Exception ex)
        {
            await dialogs.ShowMessageAsync("Send failed", ConnectionErrors.Explain(ex));
            return "Send failed";
        }
        finally
        {
            await connection.LoadAsync();
        }
    }

    /// <summary>Copies or moves the dialog's messages. A move is journalled, and deletes the originals only after the copies were sent.</summary>
    public static async Task<OperationOutcome> TransferAsync(SendMessageViewModel dialog, IDialogService dialogs, IProgress<string> progress)
    {
        var source = dialog.SourceConnection!;
        var targetConnection = dialog.Connection!;
        var address = dialog.TransferSource!;
        var target = CanonicalTarget(dialog);
        var messages = dialog.Transfer!;
        var numbers = messages.Select(m => m.SequenceNumber).ToList();
        var move = dialog.Move;
        try
        {
            return await RunAsync(source, dialogs, move ? "Move" : "Copy", progress, move ? "Moving" : "Copying", move ? numbers : [], async _ =>
            {
                Task<TransferResult> Run() => MessageTransfer.CopyAsync(source.Client, address, numbers, targetConnection.Client, target, move, dialog.NewMessageId);
                var result = move
                    ? await Journal.RecordAsync(Entry(source, "move", $"{targetConnection.Profile.Name}/{target}"), source.Client, address, messages, numbers, Run, Describe)
                    : await Run();
                return Describe(result);
            });
        }
        finally
        {
            if (targetConnection != source)
            {
                await targetConnection.LoadAsync();
            }
        }

        (string, IReadOnlyList<string>) Describe(TransferResult r) =>
            (Summary($"{(move ? "Moved" : "Copied")} {Formatting.Count(r.Sent)} to '{target}' on {targetConnection.Profile.Name}", r.NotFound), r.Errors);
    }

    private static string CanonicalTarget(SendMessageViewModel dialog) =>
        dialog.Targets.First(t => t.Equals(dialog.Target?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Every change made from the app is journalled with a backup of the messages; see View → Operation journal.</summary>
    internal static OperationJournal Journal => OperationJournal.Default;

    private static JournalEntry Entry(ConnectionNodeViewModel connection, string operation, string? target = null) => new()
    {
        Source = "app",
        Operation = operation,
        Connection = connection.Profile.Name,
        Namespace = connection.Profile.Namespace ?? string.Empty,
        Target = target,
    };

    internal static async Task<bool> EnsureWritableAsync(ConnectionNodeViewModel connection, IDialogService dialogs)
    {
        if (!connection.Profile.ReadOnly)
        {
            return true;
        }

        await dialogs.ShowMessageAsync(
            "Read-only connection",
            $"'{connection.Profile.Name}' is read-only, so QueueJanne won't change its messages. Right-click the connection and choose Read-only on/off to allow changes.");
        return false;
    }

    private static string? ConfirmText(ConnectionNodeViewModel connection, EntityInfo entity) =>
        connection.Profile.EnvironmentKind == EnvironmentKind.Production ? entity.Path : null;

    private static string Describe(IReadOnlyList<MessageRowViewModel>? rows, EntityInfo entity, SubQueueKind subQueue) => rows switch
    {
        null => $"all {Formatting.Count(subQueue == SubQueueKind.DeadLetter ? entity.Counts.DeadLetter : entity.Counts.TransferDeadLetter)} messages",
        [_] => "1 message",
        _ => $"{Formatting.Count(rows.Count)} messages",
    };

    private static string Summary(string done, int notFound) =>
        notFound > 0 ? $"{done}; {Formatting.Count(notFound)} were already gone" : done;

    private static string Name(SubQueueKind kind) => kind switch
    {
        SubQueueKind.DeadLetter => "dead letters",
        SubQueueKind.TransferDeadLetter => "transfer dead letters",
        _ => "active messages",
    };
}
