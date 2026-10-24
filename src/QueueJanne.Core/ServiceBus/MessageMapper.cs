using System.Text;
using System.Text.Json;
using Azure.Core.Amqp;
using Azure.Messaging.ServiceBus;
using QueueJanne.Core.Messages;

namespace QueueJanne.Core.ServiceBus;

internal static class MessageMapper
{
    public static QueueMessage Map(ServiceBusReceivedMessage message)
    {
        var (body, kind) = ReadBody(message.GetRawAmqpMessage().Body);
        return new QueueMessage
        {
            SequenceNumber = message.SequenceNumber,
            MessageId = message.MessageId,
            Subject = message.Subject,
            ContentType = message.ContentType,
            CorrelationId = message.CorrelationId,
            SessionId = message.SessionId,
            ReplyToSessionId = message.ReplyToSessionId,
            PartitionKey = message.PartitionKey,
            To = message.To,
            ReplyTo = message.ReplyTo,
            EnqueuedTime = message.EnqueuedTime,
            ScheduledEnqueueTime = message.ScheduledEnqueueTime == default ? null : message.ScheduledEnqueueTime,
            ExpiresAt = message.ExpiresAt,
            TimeToLive = message.TimeToLive,
            DeliveryCount = message.DeliveryCount,
            DeadLetterReason = message.DeadLetterReason,
            DeadLetterErrorDescription = message.DeadLetterErrorDescription,
            DeadLetterSource = message.DeadLetterSource,
            State = message.State switch
            {
                ServiceBusMessageState.Deferred => MessageState.Deferred,
                ServiceBusMessageState.Scheduled => MessageState.Scheduled,
                _ => MessageState.Active,
            },
            ApplicationProperties = message.ApplicationProperties.ToDictionary(p => p.Key, p => (object?)p.Value, StringComparer.Ordinal),
            Body = body,
            BodyKind = kind,
        };
    }

    private static (byte[] Body, BodyKind Kind) ReadBody(AmqpMessageBody body)
    {
        if (body.TryGetData(out var segments) && segments is not null)
        {
            using var stream = new MemoryStream();
            foreach (var segment in segments)
            {
                stream.Write(segment.Span);
            }

            return (stream.ToArray(), BodyKind.Data);
        }

        if (body.TryGetValue(out var value))
        {
            return value switch
            {
                byte[] bytes => (bytes, BodyKind.Value),
                string text => (Encoding.UTF8.GetBytes(text), BodyKind.Value),
                _ => (Encoding.UTF8.GetBytes(Describe(value)), BodyKind.Value),
            };
        }

        if (body.TryGetSequence(out var sequence))
        {
            return (Encoding.UTF8.GetBytes(Describe(sequence)), BodyKind.Sequence);
        }

        return ([], BodyKind.Data);
    }

    private static string Describe(object? value)
    {
        try
        {
            return JsonSerializer.Serialize(value);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or JsonException)
        {
            return value?.ToString() ?? string.Empty;
        }
    }
}
