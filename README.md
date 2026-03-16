# Conduit

A lightweight mediator + messaging library for .NET 10. Like MediatR and MassTransit, but without the licensing drama.

## Packages

| Package | Description |
|---------|-------------|
| `Conduit` | In-process mediator — request/response, notifications, streaming, pipeline behaviors, causality tracking |
| `Conduit.Messaging` | Cross-process messaging abstractions — publisher/consumer interfaces, serialization, pipeline context bridge, in-memory transport |
| `Conduit.Messaging.RabbitMq` | RabbitMQ transport provider for Conduit.Messaging |

## Installation

```bash
# Mediator only
dotnet add package Conduit

# Messaging (includes Conduit core)
dotnet add package Conduit.Messaging

# RabbitMQ transport
dotnet add package Conduit.Messaging.RabbitMq
```

---

# Conduit (Mediator)

## Quick Start

### Define a request and handler

```csharp
public record Ping(string Message) : IRequest<Pong>;
public record Pong(string Reply);

public class PingHandler : IRequestHandler<Ping, Pong>
{
    public Task<Pong> Handle(Ping request, CancellationToken cancellationToken)
    {
        return Task.FromResult(new Pong($"Pong: {request.Message}"));
    }
}
```

### Register services

```csharp
services.AddMediator(cfg => cfg.RegisterServicesFromAssemblyContaining<Program>());
```

### Send requests

```csharp
var dispatcher = serviceProvider.GetRequiredService<IDispatcher>();
var response = await dispatcher.Send(new Ping("Hello"));
// response.Reply == "Pong: Hello"
```

## Features

### Request/Response

```csharp
public record GetUser(int Id) : IRequest<User>;

public class GetUserHandler : IRequestHandler<GetUser, User>
{
    public async Task<User> Handle(GetUser request, CancellationToken ct)
    {
        // fetch user...
    }
}
```

### Void Requests

```csharp
public record DeleteUser(int Id) : IRequest;

public class DeleteUserHandler : IRequestHandler<DeleteUser>
{
    public async Task<Unit> Handle(DeleteUser request, CancellationToken ct)
    {
        // delete user...
        return Unit.Value;
    }
}
```

### Notifications

```csharp
public record UserCreated(int UserId) : INotification;

public class SendWelcomeEmail : INotificationHandler<UserCreated>
{
    public async Task Handle(UserCreated notification, CancellationToken ct)
    {
        // send email...
    }
}

// Publish to all handlers
await dispatcher.Publish(new UserCreated(42));
```

### Streaming

```csharp
public record GetItems(string Query) : IStreamRequest<Item>;

public class GetItemsHandler : IStreamRequestHandler<GetItems, Item>
{
    public async IAsyncEnumerable<Item> Handle(
        GetItems request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var item in FetchItemsAsync(request.Query, ct))
        {
            yield return item;
        }
    }
}

// Consume the stream
await foreach (var item in dispatcher.CreateStream(new GetItems("search")))
{
    // process item...
}
```

### Pipeline Behaviors

Wrap request handling with cross-cutting concerns:

```csharp
public class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken ct)
    {
        Console.WriteLine($"Handling {typeof(TRequest).Name}");
        var response = await next();
        Console.WriteLine($"Handled {typeof(TRequest).Name}");
        return response;
    }
}

// Register
services.AddMediator(cfg =>
{
    cfg.RegisterServicesFromAssemblyContaining<Program>();
    cfg.AddBehavior(typeof(LoggingBehavior<,>));
});
```

### Pre/Post Processors

```csharp
public class ValidationPreProcessor<TRequest> : IRequestPreProcessor<TRequest>
    where TRequest : notnull
{
    public Task Process(TRequest request, CancellationToken ct)
    {
        // validate request...
    }
}

public class AuditPostProcessor<TRequest, TResponse> : IRequestPostProcessor<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public Task Process(TRequest request, TResponse response, CancellationToken ct)
    {
        // audit logging...
    }
}
```

### Exception Handlers

```csharp
public class GlobalExceptionHandler<TRequest, TResponse> : IRequestExceptionHandler<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public Task Handle(
        TRequest request,
        Exception exception,
        RequestExceptionHandlerState<TResponse> state,
        CancellationToken ct)
    {
        // log, set state.Response to provide fallback, or let it bubble up
    }
}
```

### Pipeline Context (Cross-Cutting State)

The `IPipelineContext` is a **scoped, thread-safe context** that flows through your entire pipeline - across behaviors, pre/post processors, handlers, and even nested requests within the same DI scope. This is the killer feature for cross-cutting concerns that need to share state.

```csharp
services.AddMediator(cfg =>
{
    cfg.RegisterServicesFromAssemblyContaining<Program>();
    cfg.EnablePipelineContext = true;
});
```

#### Primitives

Inject `IPipelineContext` anywhere in your pipeline:

```csharp
public class MyBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
{
    private readonly IPipelineContext _context;

    public MyBehavior(IPipelineContext context) => _context = context;

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken ct)
    {
        // Store arbitrary data - accessible from ANY pipeline component
        _context.Items["UserId"] = GetCurrentUserId();
        _context.Items["CorrelationId"] = Guid.NewGuid().ToString();

        var response = await next();
        return response;
    }
}
```

#### Built-in Timers

Measure execution time across pipeline stages:

```csharp
public class TimingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
{
    private readonly IPipelineContext _context;

    public async Task<TResponse> Handle(...)
    {
        using var timer = _context.StartTimer($"Handler:{typeof(TRequest).Name}");
        var response = await next();
        // timer automatically records elapsed time on dispose
        return response;
    }
}

// Later, in a post-processor or logging middleware:
var timings = _context.GetTimings();
foreach (var t in timings)
{
    Console.WriteLine($"{t.Name}: {t.Elapsed.TotalMilliseconds}ms");
}
```

#### Built-in Metrics

Counters and aggregates that accumulate across the pipeline:

```csharp
// Increment counters
_context.Increment("db.queries");
_context.Increment("cache.hits", 5);

// Record values (tracks count, total, min, max)
_context.Record("response.size", responseBytes);

// Read metrics
var metrics = _context.GetMetrics();
var dbQueries = metrics["db.queries"]; // Count, Total, Min, Max
```

#### Baggage (Flowing Context)

Key-value pairs that propagate through all requests in the scope:

```csharp
// Set in an early behavior
_context.SetBaggage("tenant-id", "acme-corp");
_context.SetBaggage("feature-flags", "new-ui,beta");

// Read anywhere in the pipeline, including nested Send() calls
var tenantId = _context.GetBaggage("tenant-id");
var allBaggage = _context.GetAllBaggage();
```

#### Cross-Request Visibility

The context is **scoped to the DI scope** (typically an HTTP request), so when a handler dispatches additional requests, they all share the same context:

```csharp
public class CreateOrderHandler : IRequestHandler<CreateOrder, Order>
{
    private readonly IDispatcher _dispatcher;
    private readonly IPipelineContext _context;

    public async Task<Order> Handle(CreateOrder request, CancellationToken ct)
    {
        // This nested request shares the same IPipelineContext
        var inventory = await _dispatcher.Send(new CheckInventory(request.ProductId), ct);

        // Metrics from CheckInventory's pipeline are already in _context
        _context.Increment("orders.created");

        return new Order(...);
    }
}
```

### Ambient Context (`PipelineContext.Current`)

`PipelineContext` has an `AsyncLocal` static accessor, so you can access it anywhere in the async flow — not just where DI is available:

```csharp
// Set ambient context (returns IDisposable that restores previous)
using var scope = PipelineContext.SetCurrent(myContext);

// Access anywhere
var ctx = PipelineContext.Current; // null if not set
ctx?.SetBaggage("tenant-id", tenantId);
var tenant = ctx?.GetBaggage("tenant-id");
```

This is especially useful for:
- **Background services** where DI scopes don't exist
- **Static helpers** that need context without constructor injection
- **Cross-process consumers** where context is hydrated from message headers

The ambient context nests correctly — `SetCurrent` returns an `IDisposable` that restores the previous context on dispose, making it safe for concurrent async flows.

### Causality Tracking

When enabled, Conduit automatically tracks parent-child relationships between requests, giving you a full call graph:

```csharp
services.AddMediator(cfg =>
{
    cfg.RegisterServicesFromAssemblyContaining<Program>();
    cfg.EnablePipelineContext = true;
    cfg.EnableCausalityTracking = true;
});

// In any pipeline component:
var currentId = _context.GetCurrentRequestId();
var parentId = _context.GetParentRequestId();
var fullChain = _context.GetCausalityChain();

foreach (var entry in fullChain)
{
    Console.WriteLine($"{entry.RequestId} <- {entry.ParentId}: {entry.RequestType} @ {entry.Timestamp}");
}
```

This is invaluable for debugging complex flows, distributed tracing integration, and understanding "who called whom" in your request pipeline.

### Validation at Startup

Catch missing handlers early:

```csharp
var app = builder.Build();
app.Services.ValidateConduitRegistrations(typeof(Program).Assembly);
```

## Configuration Options

```csharp
services.AddMediator(cfg =>
{
    // Assembly scanning
    cfg.RegisterServicesFromAssembly(typeof(Program).Assembly);
    cfg.RegisterServicesFromAssemblyContaining<Program>();

    // Service lifetime (default: Transient)
    cfg.Lifetime = ServiceLifetime.Scoped;

    // Pipeline features
    cfg.EnablePipelineContext = true;
    cfg.EnableCausalityTracking = true;

    // Custom notification publishing strategy
    cfg.NotificationPublisherType = typeof(TaskWhenAllPublisher); // parallel
    // cfg.NotificationPublisherType = typeof(ForeachAwaitPublisher); // sequential (default)

    // Register pipeline components
    cfg.AddBehavior(typeof(LoggingBehavior<,>));
    cfg.AddPreProcessor(typeof(ValidationPreProcessor<>));
    cfg.AddPostProcessor(typeof(AuditPostProcessor<,>));
    cfg.AddExceptionHandler(typeof(GlobalExceptionHandler<,>));
    cfg.AddStreamBehavior(typeof(StreamLoggingBehavior<,>));
});
```

---

# Conduit.Messaging

Cross-process messaging with pluggable transport providers. Define message contracts once, swap transports without changing application code.

## Quick Start

### Define a message

```csharp
public record OrderPlaced : EventMessage
{
    public required string OrderId { get; init; }
    public required decimal Total { get; init; }
}
```

Three base types are provided:
- `EventMessage` — something happened (pub/sub, fan-out)
- `CommandMessage` — do something (point-to-point)
- `QueryMessage` — request data (point-to-point)

All include `MessageId`, `CreatedAt`, `CorrelationId`, `TenantId`, `SessionId`, `UserId`, and `Metadata`.

### Define a consumer

```csharp
public class OrderPlacedConsumer : IMessageConsumer<OrderPlaced>
{
    public async Task ConsumeAsync(
        OrderPlaced message,
        MessageContext context,
        CancellationToken cancellationToken = default)
    {
        // Handle the message
        // context.MessageId, context.CorrelationId, context.Headers available
    }
}
```

### Register services

```csharp
services.AddConduitMessaging(cfg =>
{
    cfg.ServiceName = "service-orders";
    cfg.UseRabbitMq(settings);              // or cfg.UseInMemory() for tests
    cfg.PropagateContextHeaders = true;     // auto-propagate PipelineContext baggage
    cfg.AddConsumer<OrderPlacedConsumer>();
    cfg.AddConsumersFromAssembly(typeof(Program).Assembly);
});
```

### Publish messages

```csharp
public class PlaceOrderHandler(IMessagePublisher publisher)
{
    public async Task Handle(PlaceOrder request, CancellationToken ct)
    {
        // Process order...

        await publisher.PublishAsync(new OrderPlaced
        {
            OrderId = order.Id,
            Total = order.Total,
            TenantId = request.TenantId,
            SessionId = request.SessionId,
            UserId = request.UserId
        }, ct);
    }
}
```

## Publishing Patterns

```csharp
// Pub/sub — all subscribers receive the message
await publisher.PublishAsync(message, ct);

// Topic-based routing
await publisher.PublishAsync(message, "orders.eu", ct);

// Point-to-point — exactly one consumer receives it
await publisher.SendAsync(message, "process-payment-queue", ct);
```

All methods accept optional `IReadOnlyDictionary<string, string>? contextHeaders` for explicit cross-process context propagation:

```csharp
var headers = PipelineContextBridge.ExtractHeaders(pipelineContext);
await publisher.PublishAsync(message, headers, ct);
```

## Transport Providers

### RabbitMQ (`Conduit.Messaging.RabbitMq`)

```csharp
services.AddConduitMessaging(cfg =>
{
    cfg.ServiceName = "service-audit";
    cfg.UseRabbitMq(new RabbitMqSettings
    {
        Host = "rabbitmq",
        Port = 5671,
        UseSsl = true,
        VirtualHost = "myapp",
        Username = "myapp",
        Password = "secret",
        PrefetchCount = 10,
        RetryCount = 3
    });
    cfg.AddConsumersFromAssembly(typeof(Program).Assembly);
});
```

Features:
- **Auto-topology**: exchanges and queues declared on first use
- **Dead-letter queues**: failed messages routed to `*.dlq` after retry limit
- **Retry with requeue**: configurable retry count before dead-lettering
- **Persistent delivery**: messages survive broker restart
- **SSL/TLS**: TLS 1.2/1.3 support
- **Auto-recovery**: reconnects on connection loss

Exchange strategy:
- `PublishAsync` → fanout exchange named `Namespace:TypeName`
- `PublishAsync` with topic → topic exchange with routing key
- `SendAsync` → direct to queue via default exchange

### In-Memory (testing)

```csharp
services.AddConduitMessaging(cfg =>
{
    cfg.UseInMemory();
    cfg.AddConsumer<OrderPlacedConsumer>();
});
```

The in-memory transport dispatches synchronously to consumers and records all messages for assertions:

```csharp
var bus = sp.GetRequiredService<InMemoryMessageBus>();

// Query recorded messages
var published = bus.GetPublished<OrderPlaced>().ToList();
var consumed = bus.GetConsumed<OrderPlaced>().ToList();

// Wait for async consumer completion
var result = await bus.WaitForConsume<OrderPlaced>(
    m => m.OrderId == "123",
    timeout: TimeSpan.FromSeconds(5));

// Reset state between tests
bus.Clear();
```

### Writing a Custom Provider

Implement a transport by providing an extension method on `MessagingConfiguration`:

```csharp
public static class MyTransportExtensions
{
    public static void UseMyTransport(this MessagingConfiguration config, MySettings settings)
    {
        config.TransportRegistrar = (services, cfg) =>
        {
            foreach (var reg in cfg.ConsumerRegistrations)
                services.AddScoped(reg.ConsumerType);

            services.AddSingleton<IMessageBus>(sp =>
                new MyMessageBus(settings, cfg.ConsumerRegistrations, sp));

            services.AddSingleton<IMessagePublisher>(sp =>
                sp.GetRequiredService<IMessageBus>().Publisher);
        };
    }
}
```

The `MessageBusHostedService` is registered by the core and will call `StartAsync`/`StopAsync` on your `IMessageBus` automatically.

## Pipeline Context Bridge

When `IPipelineContext` is enabled in the mediator, the bridge propagates context (baggage, causality chain, correlation ID) across process boundaries via message headers.

### Automatic Context Propagation

Enable `PropagateContextHeaders` to automatically extract `PipelineContext.Current` baggage into message headers on every publish/send — no manual header extraction needed:

```csharp
services.AddConduitMessaging(cfg =>
{
    cfg.ServiceName = "my-service";
    cfg.UseRabbitMq(settings);
    cfg.PropagateContextHeaders = true; // auto-propagate context
});

// Now every publish automatically includes baggage, causality, correlation headers
await publisher.PublishAsync(new OrderPlaced { ... }, ct);
```

This wraps `IMessagePublisher` with a `ContextPropagatingPublisher` decorator that reads from `PipelineContext.Current` and merges headers via `PipelineContextBridge.ExtractHeaders`. The decorator uses lazy publisher resolution, so it works even when resolved before the bus hosted service has started.

### Manual Context Propagation

If you prefer explicit control, extract headers manually:

```csharp
var headers = PipelineContextBridge.ExtractHeaders(pipelineContext);
await publisher.PublishAsync(message, headers, ct);
```

Serialized headers:
- `conduit.baggage.*` — arbitrary key-value pairs
- `conduit.correlation-id` — correlation ID for tracing
- `conduit.origin-request-id` — publishing process request ID
- `conduit.causality-chain` — pipe-delimited causality entries

### Consumer side — automatic hydration

Both RabbitMQ and in-memory transports automatically hydrate `IPipelineContext` in the consumer's DI scope before invoking the consumer. No manual code needed — baggage, correlation, and causality are restored transparently.

## Serialization

Messages are wrapped in a JSON envelope for transport:

```json
{
    "messageType": "MyApp.Orders.OrderPlaced",
    "payload": { "orderId": "123", "total": 99.99 },
    "headers": { "conduit.baggage.tenant-id": "acme" },
    "timestamp": "2026-03-16T10:30:00Z"
}
```

Exchange names: `Namespace:TypeName`. Queue names: `serviceName:ConsumerTypeName`.

## License

MIT
