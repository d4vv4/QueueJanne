using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using QueueJanne.Core.Bodies;
using QueueJanne.Core.Connections;
using QueueJanne.Core.Entities;
using QueueJanne.Core.Messages;

namespace QueueJanne.App.ViewModels;

/// <summary>
/// The Send message dialog. It sends a new message, sends an edited copy of one ("Edit and resend"), or copies or moves
/// the selected messages to another queue or topic. Only connections that aren't read-only are offered as targets.
/// </summary>
public sealed partial class SendMessageViewModel : ObservableObject
{
    public const int MaxCopies = 100;

    private byte[]? _keptBody;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Targets), nameof(IsProductionTarget), nameof(ConfirmHint), nameof(Warnings), nameof(HasWarnings))]
    private ConnectionNodeViewModel? _connection;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfirmHint), nameof(Warnings), nameof(HasWarnings))]
    private string? _target;

    [ObservableProperty]
    private string? _subject;

    [ObservableProperty]
    private string? _contentType = "application/json";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Warnings), nameof(HasWarnings))]
    private string? _messageId;

    [ObservableProperty]
    private string? _correlationId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Warnings), nameof(HasWarnings))]
    private string? _sessionId;

    [ObservableProperty]
    private string? _propertiesText;

    [ObservableProperty]
    private string? _bodyText;

    [ObservableProperty]
    private decimal _copies = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionLabel))]
    private bool _move;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Warnings), nameof(HasWarnings))]
    private bool _newMessageId;

    [ObservableProperty]
    private string? _confirmText;

    [ObservableProperty]
    private string? _error;

    private SendMessageViewModel(IReadOnlyList<ConnectionNodeViewModel> connections, string title)
    {
        Connections = connections;
        Title = title;
    }

    public string Title { get; }

    /// <summary>Set for copy and move: the messages and where they are now.</summary>
    public IReadOnlyList<QueueMessage>? Transfer { get; private init; }

    public EntityAddress? TransferSource { get; private init; }

    /// <summary>The connection the messages are copied from; moving deletes from it, so it must allow changes.</summary>
    public ConnectionNodeViewModel? SourceConnection { get; private init; }

    public bool SourceIsReadOnly => SourceConnection?.Profile.ReadOnly == true;

    public bool IsTransfer => Transfer is not null;

    public bool IsCompose => Transfer is null;

    public string ActionLabel => IsCompose ? "Send" : Move ? "Move" : "Copy";

    public string TransferSummary => Transfer is { } messages && TransferSource is { } source
        ? $"{Formatting.Count(messages.Count)} message{(messages.Count == 1 ? string.Empty : "s")} from {source.DisplayName}"
        : string.Empty;

    /// <summary>False when the original body isn't text; it's then sent unchanged.</summary>
    public bool IsBodyEditable => _keptBody is null;

    public string? BodyNote { get; private init; }

    public IReadOnlyList<ConnectionNodeViewModel> Connections { get; }

    /// <summary>Queues and topics of the chosen connection. Subscriptions can't be sent to.</summary>
    public IReadOnlyList<string> Targets => Connection?.Snapshot is { } snapshot
        ? snapshot.Queues.Select(q => q.Name).Concat(snapshot.Topics.Select(t => t.Topic.Name)).Order(StringComparer.OrdinalIgnoreCase).ToList()
        : [];

    /// <summary>What to know about the chosen target, worked out from its live settings.</summary>
    public IReadOnlyList<string> Warnings
    {
        get
        {
            var target = Target?.Trim();
            var entity = Connection?.Snapshot is { } snapshot && !string.IsNullOrEmpty(target)
                ? snapshot.Queues.Concat(snapshot.Topics.Select(t => t.Topic)).FirstOrDefault(e => e.Name.Equals(target, StringComparison.OrdinalIgnoreCase))
                : null;
            if (entity is null)
            {
                return [];
            }

            var warnings = new List<string>();
            var keepsIds = IsTransfer ? !NewMessageId : !string.IsNullOrWhiteSpace(MessageId);
            if (entity.Settings.RequiresDuplicateDetection && keepsIds)
            {
                warnings.Add($"'{entity.Name}' has duplicate detection ({Formatting.Duration(entity.Settings.DuplicateDetectionWindow)}): "
                    + (IsTransfer ? "copies whose message ID it has seen in that time are dropped. Choose a new message ID." : "a message ID it has seen in that time is dropped. Clear or change the message ID."));
            }

            if (entity.Settings.RequiresSession && IsCompose && string.IsNullOrWhiteSpace(SessionId))
            {
                warnings.Add($"'{entity.Name}' requires sessions, so the message needs a session ID.");
            }

            return warnings;
        }
    }

    public bool HasWarnings => Warnings.Count > 0;

    public bool IsProductionTarget => Connection?.Profile.EnvironmentKind == EnvironmentKind.Production;

    public string ConfirmHint => $"This is a production namespace. Type {Target} to confirm.";

    public static SendMessageViewModel New(IReadOnlyList<ConnectionNodeViewModel> connections, ConnectionNodeViewModel? connection, string? target) =>
        new(Writable(connections), "Send message") { Connection = Pick(connections, connection), Target = target, BodyText = "{\n  \n}" };

    /// <summary>Prefilled from a peeked message: the same body and properties, without its dead-letter details.</summary>
    public static SendMessageViewModel EditAndResend(IReadOnlyList<ConnectionNodeViewModel> connections, ConnectionNodeViewModel connection, string target, QueueMessage message)
    {
        var copy = OutgoingMessage.CopyOf(message, newMessageId: false);
        var isText = BodyFormatter.LooksLikeText(message.Body);
        var format = isText ? BodyFormatter.Detect(message.Body, message.ContentType) : BodyFormat.Hex;
        return new SendMessageViewModel(Writable(connections), "Edit and resend")
        {
            Connection = Pick(connections, connection),
            Target = target,
            Subject = copy.Subject,
            ContentType = copy.ContentType,
            MessageId = copy.MessageId,
            CorrelationId = copy.CorrelationId,
            SessionId = copy.SessionId,
            PropertiesText = FormatProperties(copy.ApplicationProperties),
            BodyText = isText ? BodyFormatter.Format(message.Body, format) : $"[{Formatting.Bytes(message.Body.Length)} binary body, sent unchanged]",
            _keptBody = isText ? null : message.Body,
            BodyNote = isText ? null : "The body isn't text, so it's sent exactly as it was received.",
        };
    }

    public static SendMessageViewModel CopyOrMove(IReadOnlyList<ConnectionNodeViewModel> connections, ConnectionNodeViewModel connection, EntityAddress source, IReadOnlyList<QueueMessage> messages) =>
        new(Writable(connections), "Copy or move messages")
        {
            Connection = Pick(connections, connection),
            Transfer = messages,
            TransferSource = source,
            SourceConnection = connection,
        };

    /// <summary>Checks the input and builds the messages to send; sets <see cref="Error"/> and returns null when something is wrong.</summary>
    public IReadOnlyList<OutgoingMessage>? Validate()
    {
        Error = Check();
        if (Error is not null || IsTransfer)
        {
            return Error is null ? [] : null;
        }

        if (!TryParseProperties(PropertiesText, out var properties, out var problem))
        {
            Error = problem;
            return null;
        }

        var message = new OutgoingMessage
        {
            Body = _keptBody ?? Encoding.UTF8.GetBytes(BodyText ?? string.Empty),
            ContentType = Clean(ContentType),
            Subject = Clean(Subject),
            MessageId = Clean(MessageId),
            CorrelationId = Clean(CorrelationId),
            SessionId = Clean(SessionId),
            ApplicationProperties = properties,
        };
        var copies = (int)Math.Clamp(Copies, 1, MaxCopies);

        // Copies get their own IDs unless one was given, so duplicate detection doesn't drop them.
        return Enumerable.Range(0, copies)
            .Select(i => copies > 1 && message.MessageId is { } id ? message with { MessageId = $"{id}-{i + 1}" } : message)
            .ToList();
    }

    public static string FormatProperties(IReadOnlyDictionary<string, object?> properties) =>
        string.Join('\n', properties.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => $"{p.Key} = {PropertyRow.FormatValue(p.Value)}"));

    /// <summary>
    /// One <c>name = value</c> per line. Whole numbers, decimals and true/false become those types; anything else, or a value
    /// in double quotes, is a string.
    /// </summary>
    public static bool TryParseProperties(string? text, out Dictionary<string, object?> properties, out string? problem)
    {
        properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        problem = null;
        var lineNumber = 0;
        foreach (var raw in (text ?? string.Empty).Split('\n'))
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                problem = $"Property line {lineNumber} needs the form name = value.";
                return false;
            }

            var name = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim();
            properties[name] = value switch
            {
                ['"', .., '"'] => value[1..^1],
                "true" or "True" => true,
                "false" or "False" => false,
                _ when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole) => whole,
                _ when value.Contains('.', StringComparison.Ordinal) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => number,
                _ => value,
            };
        }

        return true;
    }

    private string? Check()
    {
        if (Connection is null)
        {
            return "Choose a connection that allows changes.";
        }

        if (string.IsNullOrWhiteSpace(Target) || !Targets.Contains(Target.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            return $"Choose a queue or topic in {Connection.Profile.Name}.";
        }

        if (IsProductionTarget && ConfirmText?.Trim() != Target.Trim())
        {
            return $"Type {Target.Trim()} to confirm sending to production.";
        }

        if (IsTransfer && Move && SourceIsReadOnly)
        {
            return $"Can't move from {TransferSource?.DisplayName}: its connection is read-only. Copy instead, or turn read-only off.";
        }

        return null;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IReadOnlyList<ConnectionNodeViewModel> Writable(IEnumerable<ConnectionNodeViewModel> connections) =>
        connections.Where(c => !c.Profile.ReadOnly && c.Snapshot is not null).ToList();

    private static ConnectionNodeViewModel? Pick(IReadOnlyList<ConnectionNodeViewModel> all, ConnectionNodeViewModel? preferred) =>
        preferred is { Profile.ReadOnly: false, Snapshot: not null } ? preferred : Writable(all).FirstOrDefault();

    partial void OnConnectionChanged(ConnectionNodeViewModel? value)
    {
        if (Target is { } target && !Targets.Contains(target, StringComparer.OrdinalIgnoreCase))
        {
            Target = null;
        }
    }
}
