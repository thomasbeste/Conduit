using Conduit.Messaging.Serialization;

namespace Conduit.Messaging.Tests;

public class SerializationTests
{
    private record TestMessage(string Name, int Count);

    [Fact]
    public void Roundtrip_serialization_preserves_data()
    {
        var original = new TestMessage("test", 42);
        var bytes = MessageSerializer.Serialize(original, typeof(TestMessage).FullName!);
        var (deserialized, envelope) = MessageSerializer.Deserialize(bytes, typeof(TestMessage));

        var msg = Assert.IsType<TestMessage>(deserialized);
        Assert.Equal("test", msg.Name);
        Assert.Equal(42, msg.Count);
        Assert.Equal(typeof(TestMessage).FullName, envelope.MessageType);
    }

    [Fact]
    public void GetExchangeName_returns_namespace_colon_typename()
    {
        var name = MessageSerializer.GetExchangeName(typeof(TestMessage));
        Assert.Equal("Conduit.Messaging.Tests:TestMessage", name);
    }

    [Fact]
    public void GetQueueName_returns_service_colon_consumer()
    {
        var name = MessageSerializer.GetQueueName("service-audit", typeof(SerializationTests));
        Assert.Equal("service-audit:SerializationTests", name);
    }
}
