namespace Conduit.Messaging.RabbitMq;

/// <summary>
/// Configuration settings for RabbitMQ connection.
/// </summary>
public class RabbitMqSettings
{
    public const string SectionName = "RabbitMQ";

    public string Host { get; set; } = "rabbitmq";
    public int Port { get; set; } = 5672;
    public bool UseSsl { get; set; } = false;
    public string VirtualHost { get; set; } = "gpi";
    public string Username { get; set; } = "gpi";
    public string Password { get; set; } = "PLACEHOLDER";

    /// <summary>
    /// Number of concurrent message consumers per endpoint.
    /// </summary>
    public ushort PrefetchCount { get; set; } = 10;

    /// <summary>
    /// Retry interval in seconds for transient failures.
    /// </summary>
    public int RetryIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// Number of retry attempts for transient failures.
    /// </summary>
    public int RetryCount { get; set; } = 3;
}
