using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Mediator;

public class MediatorConfiguration
{
    public ServiceLifetime Lifetime { get; set; } = ServiceLifetime.Transient;

    public Type DispatcherImplementationType { get; set; } = typeof(Dispatcher);

    public Type NotificationPublisherType { get; set; } = typeof(ForeachAwaitPublisher);

    /// <summary>
    /// Enables the scoped IPipelineContext for accumulating timing, metrics, and data across pipeline calls.
    /// Defaults to true.
    /// </summary>
    public bool EnablePipelineContext { get; set; } = true;

    /// <summary>
    /// Enables automatic causality tracking across nested Send() calls.
    /// Tracks which request spawned which, useful for debugging and distributed tracing prep.
    /// Requires EnablePipelineContext to be true.
    /// Defaults to false.
    /// </summary>
    public bool EnableCausalityTracking { get; set; } = false;

    internal List<Assembly> AssembliesToRegister { get; } = [];

    internal List<Type> BehaviorTypes { get; } = [];

    internal List<Type> PreProcessorTypes { get; } = [];

    internal List<Type> PostProcessorTypes { get; } = [];

    internal List<Type> ExceptionHandlerTypes { get; } = [];

    internal List<Type> StreamBehaviorTypes { get; } = [];

    public MediatorConfiguration RegisterServicesFromAssembly(Assembly assembly)
    {
        AssembliesToRegister.Add(assembly);
        return this;
    }

    public MediatorConfiguration RegisterServicesFromAssemblies(params Assembly[] assemblies)
    {
        AssembliesToRegister.AddRange(assemblies);
        return this;
    }

    public MediatorConfiguration RegisterServicesFromAssemblyContaining<T>()
    {
        return RegisterServicesFromAssembly(typeof(T).Assembly);
    }

    public MediatorConfiguration RegisterServicesFromAssemblyContaining(Type type)
    {
        return RegisterServicesFromAssembly(type.Assembly);
    }

    public MediatorConfiguration AddBehavior<TBehavior>()
        where TBehavior : class
    {
        BehaviorTypes.Add(typeof(TBehavior));
        return this;
    }

    public MediatorConfiguration AddBehavior(Type behaviorType)
    {
        BehaviorTypes.Add(behaviorType);
        return this;
    }

    public MediatorConfiguration AddOpenBehavior(Type openBehaviorType)
    {
        if (!openBehaviorType.IsGenericTypeDefinition)
        {
            throw new ArgumentException($"Type {openBehaviorType.Name} must be an open generic type", nameof(openBehaviorType));
        }

        BehaviorTypes.Add(openBehaviorType);
        return this;
    }

    public MediatorConfiguration AddPreProcessor<TPreProcessor>()
        where TPreProcessor : class
    {
        PreProcessorTypes.Add(typeof(TPreProcessor));
        return this;
    }

    public MediatorConfiguration AddOpenPreProcessor(Type openPreProcessorType)
    {
        if (!openPreProcessorType.IsGenericTypeDefinition)
        {
            throw new ArgumentException($"Type {openPreProcessorType.Name} must be an open generic type", nameof(openPreProcessorType));
        }

        PreProcessorTypes.Add(openPreProcessorType);
        return this;
    }

    public MediatorConfiguration AddPostProcessor<TPostProcessor>()
        where TPostProcessor : class
    {
        PostProcessorTypes.Add(typeof(TPostProcessor));
        return this;
    }

    public MediatorConfiguration AddOpenPostProcessor(Type openPostProcessorType)
    {
        if (!openPostProcessorType.IsGenericTypeDefinition)
        {
            throw new ArgumentException($"Type {openPostProcessorType.Name} must be an open generic type", nameof(openPostProcessorType));
        }

        PostProcessorTypes.Add(openPostProcessorType);
        return this;
    }

    public MediatorConfiguration AddExceptionHandler<TExceptionHandler>()
        where TExceptionHandler : class
    {
        ExceptionHandlerTypes.Add(typeof(TExceptionHandler));
        return this;
    }

    public MediatorConfiguration AddOpenExceptionHandler(Type openExceptionHandlerType)
    {
        if (!openExceptionHandlerType.IsGenericTypeDefinition)
        {
            throw new ArgumentException($"Type {openExceptionHandlerType.Name} must be an open generic type", nameof(openExceptionHandlerType));
        }

        ExceptionHandlerTypes.Add(openExceptionHandlerType);
        return this;
    }

    public MediatorConfiguration AddStreamBehavior<TStreamBehavior>()
        where TStreamBehavior : class
    {
        StreamBehaviorTypes.Add(typeof(TStreamBehavior));
        return this;
    }

    public MediatorConfiguration AddOpenStreamBehavior(Type openStreamBehaviorType)
    {
        if (!openStreamBehaviorType.IsGenericTypeDefinition)
        {
            throw new ArgumentException($"Type {openStreamBehaviorType.Name} must be an open generic type", nameof(openStreamBehaviorType));
        }

        StreamBehaviorTypes.Add(openStreamBehaviorType);
        return this;
    }
}
