using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Conduit;

/// <summary>
/// Extension methods for registering Conduit services with the DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddConduit(Action<ConduitConfiguration> configure)
        {
            var config = new ConduitConfiguration();
            configure(config);

            return services.AddConduit(config);
        }

        public IServiceCollection AddConduit(ConduitConfiguration config)
        {
            var lifetime = config.Lifetime;

            // Register the dispatcher
            services.TryAdd(new ServiceDescriptor(typeof(IDispatcher), config.DispatcherImplementationType, lifetime));
            services.TryAdd(new ServiceDescriptor(typeof(ISender), sp => sp.GetRequiredService<IDispatcher>(), lifetime));
            services.TryAdd(new ServiceDescriptor(typeof(IPublisher), sp => sp.GetRequiredService<IDispatcher>(), lifetime));

            // Register notification publisher
            services.TryAdd(new ServiceDescriptor(typeof(INotificationPublisher), config.NotificationPublisherType, lifetime));

            // Register pipeline context (always scoped regardless of lifetime setting)
            // Use TryAddEnumerable so behaviors can inject IEnumerable<IPipelineContext> for optional access
            if (config.EnablePipelineContext)
            {
                services.TryAddEnumerable(new ServiceDescriptor(typeof(IPipelineContext), typeof(PipelineContext), ServiceLifetime.Scoped));
            }

            // Register causality behavior if enabled (requires pipeline context)
            if (config is { EnableCausalityTracking: true, EnablePipelineContext: true })
            {
                services.Add(new ServiceDescriptor(typeof(IPipelineBehavior<,>), typeof(CausalityBehavior<,>), lifetime));
            }

            // Scan assemblies for handlers
            foreach (var assembly in config.AssembliesToRegister)
            {
                RegisterHandlersFromAssembly(services, assembly, lifetime);
            }

            // Register behaviors
            foreach (var behaviorType in config.BehaviorTypes)
            {
                RegisterOpenGenericOrConcrete(services, behaviorType, typeof(IPipelineBehavior<,>), lifetime);
            }

            // Register pre-processors
            foreach (var preProcessorType in config.PreProcessorTypes)
            {
                RegisterOpenGenericOrConcrete(services, preProcessorType, typeof(IRequestPreProcessor<>), lifetime);
            }

            // Register post-processors
            foreach (var postProcessorType in config.PostProcessorTypes)
            {
                RegisterOpenGenericOrConcrete(services, postProcessorType, typeof(IRequestPostProcessor<,>), lifetime);
            }

            // Register exception handlers
            foreach (var exceptionHandlerType in config.ExceptionHandlerTypes)
            {
                RegisterOpenGenericOrConcrete(services, exceptionHandlerType, typeof(IRequestExceptionHandler<,>), lifetime);
            }

            // Register stream behaviors
            foreach (var streamBehaviorType in config.StreamBehaviorTypes)
            {
                RegisterOpenGenericOrConcrete(services, streamBehaviorType, typeof(IStreamPipelineBehavior<,>), lifetime);
            }

            return services;
        }
    }

    private static void RegisterHandlersFromAssembly(
        IServiceCollection services,
        Assembly assembly,
        ServiceLifetime lifetime)
    {
        var handlerInterfaces = new[]
        {
            typeof(IRequestHandler<,>),
            typeof(INotificationHandler<>),
            typeof(IStreamRequestHandler<,>)
        };

        foreach (var type in assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false }))
        {
            foreach (var interfaceType in type.GetInterfaces())
            {
                if (!interfaceType.IsGenericType)
                    continue;

                var genericTypeDef = interfaceType.GetGenericTypeDefinition();

                if (handlerInterfaces.Contains(genericTypeDef))
                {
                    services.TryAddEnumerable(new ServiceDescriptor(interfaceType, type, lifetime));
                }
            }
        }
    }

    private static void RegisterOpenGenericOrConcrete(
        IServiceCollection services,
        Type implementationType,
        Type serviceInterfaceType,
        ServiceLifetime lifetime)
    {
        if (implementationType.IsGenericTypeDefinition)
        {
            // Open generic registration
            services.Add(new ServiceDescriptor(serviceInterfaceType, implementationType, lifetime));
        }
        else
        {
            // Closed/concrete type - find the specific interface it implements
            var matchingInterface = implementationType.GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == serviceInterfaceType);

            if (matchingInterface != null)
            {
                services.Add(new ServiceDescriptor(matchingInterface, implementationType, lifetime));
            }
        }
    }

    /// <summary>
    /// Validates that all request types discovered in the registered assemblies have corresponding handlers.
    /// Call this at startup to catch misconfiguration early instead of at first dispatch.
    /// </summary>
    /// <param name="serviceProvider">The built service provider.</param>
    /// <param name="assemblies">Assemblies to scan for request types. Should match assemblies passed to <see cref="ConduitConfiguration.RegisterServicesFromAssembly"/>.</param>
    /// <exception cref="InvalidOperationException">Thrown when one or more request types are missing handlers.</exception>
    /// <remarks>
    /// <para><b>Example usage in ASP.NET Core:</b></para>
    /// <code>
    /// var app = builder.Build();
    /// app.Services.ValidateConduitRegistrations(typeof(Program).Assembly);
    /// </code>
    /// </remarks>
    public static void ValidateConduitRegistrations(this IServiceProvider serviceProvider, params Assembly[] assemblies)
    {
        var errors = new List<string>();

        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false }))
            {
                // Check IRequest<TResponse> implementations
                var requestInterface = type.GetInterfaces()
                    .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequest<>));

                if (requestInterface != null)
                {
                    var responseType = requestInterface.GetGenericArguments()[0];
                    var handlerType = typeof(IRequestHandler<,>).MakeGenericType(type, responseType);

                    using var scope = serviceProvider.CreateScope();
                    var handler = scope.ServiceProvider.GetService(handlerType);

                    if (handler == null)
                    {
                        errors.Add($"No handler registered for request type '{type.FullName}'. Expected handler implementing IRequestHandler<{type.Name}, {responseType.Name}>.");
                    }
                }

                // Check IStreamRequest<TResponse> implementations
                var streamInterface = type.GetInterfaces()
                    .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IStreamRequest<>));

                if (streamInterface != null)
                {
                    var responseType = streamInterface.GetGenericArguments()[0];
                    var handlerType = typeof(IStreamRequestHandler<,>).MakeGenericType(type, responseType);

                    using var scope = serviceProvider.CreateScope();
                    var handler = scope.ServiceProvider.GetService(handlerType);

                    if (handler == null)
                    {
                        errors.Add($"No handler registered for stream request type '{type.FullName}'. Expected handler implementing IStreamRequestHandler<{type.Name}, {responseType.Name}>.");
                    }
                }
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                $"Conduit handler registration validation failed:\n{string.Join("\n", errors.Select(e => $"  - {e}"))}");
        }
    }
}
