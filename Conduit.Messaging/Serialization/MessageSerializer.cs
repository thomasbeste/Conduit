using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Conduit.Messaging.Serialization;

/// <summary>
/// Serializes/deserializes messages for transport.
/// Uses an envelope format: { messageType, payload, headers }
/// </summary>
public static class MessageSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static ReadOnlyMemory<byte> Serialize(object message, string messageType, Dictionary<string, string>? headers = null)
    {
        var envelope = new MessageEnvelope
        {
            MessageType = messageType,
            Payload = JsonSerializer.SerializeToElement(message, message.GetType(), Options),
            Headers = headers,
            Timestamp = DateTime.UtcNow
        };

        return new ReadOnlyMemory<byte>(JsonSerializer.SerializeToUtf8Bytes(envelope, Options));
    }

    public static (object? Message, MessageEnvelope Envelope) Deserialize(ReadOnlyMemory<byte> body, Type messageType)
    {
        var envelope = JsonSerializer.Deserialize<MessageEnvelope>(body.Span, Options)
            ?? throw new InvalidOperationException("Failed to deserialize message envelope");

        var message = envelope.Payload.Deserialize(messageType, Options);
        return (message, envelope);
    }

    /// <summary>
    /// Gets the exchange name for a message type.
    /// Format: Namespace:TypeName
    /// </summary>
    public static string GetExchangeName(Type messageType)
    {
        return $"{messageType.Namespace}:{messageType.Name}";
    }

    /// <summary>
    /// Gets the queue name for a consumer.
    /// Format: serviceName:{ConsumerTypeName}
    /// </summary>
    public static string GetQueueName(string serviceName, Type consumerType)
    {
        // Strip "Consumer" suffix for cleaner queue names
        var name = consumerType.Name;
        return $"{serviceName}:{name}";
    }
}

public class MessageEnvelope
{
    public required string MessageType { get; set; }
    public JsonElement Payload { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
    public DateTime Timestamp { get; set; }
}
