# Cypher

A lightweight mediator library for .NET 10. Like MediatR, but without the licensing drama.

## Installation

```bash
dotnet add package Cypher
```

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
services.AddCypher(cfg => cfg.RegisterServicesFromAssemblyContaining<Program>());
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
services.AddCypher(cfg =>
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
services.AddCypher(cfg =>
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

### Causality Tracking

When enabled, Cypher automatically tracks parent-child relationships between requests, giving you a full call graph:

```csharp
services.AddCypher(cfg =>
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
app.Services.ValidateCypherRegistrations(typeof(Program).Assembly);
```

## Configuration Options

```csharp
services.AddCypher(cfg =>
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

## License

MIT
