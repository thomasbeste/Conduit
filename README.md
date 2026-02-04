# Conduit

A lightweight mediator library for .NET 10. Like MediatR, but without the licensing drama.

## Installation

```bash
dotnet add package Conduit
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
services.AddConduit(cfg => cfg.RegisterServicesFromAssemblyContaining<Program>());
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
services.AddConduit(cfg =>
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

### Pipeline Context

Share data across the pipeline within a request scope:

```csharp
services.AddConduit(cfg =>
{
    cfg.RegisterServicesFromAssemblyContaining<Program>();
    cfg.EnablePipelineContext = true;
});

public class MyBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
{
    private readonly IPipelineContext _context;

    public MyBehavior(IPipelineContext context) => _context = context;

    public async Task<TResponse> Handle(...)
    {
        _context.Set("StartTime", DateTime.UtcNow);
        var response = await next();
        var elapsed = DateTime.UtcNow - _context.Get<DateTime>("StartTime");
        return response;
    }
}
```

### Causality Tracking

Track request chains with W3C Baggage:

```csharp
services.AddConduit(cfg =>
{
    cfg.RegisterServicesFromAssemblyContaining<Program>();
    cfg.EnablePipelineContext = true;
    cfg.EnableCausalityTracking = true;
});
```

### Validation at Startup

Catch missing handlers early:

```csharp
var app = builder.Build();
app.Services.ValidateConduitRegistrations(typeof(Program).Assembly);
```

## Configuration Options

```csharp
services.AddConduit(cfg =>
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
