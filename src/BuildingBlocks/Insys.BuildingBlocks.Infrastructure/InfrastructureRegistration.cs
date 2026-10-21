using System.Reflection;
using Insys.BuildingBlocks.Application;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Application.Security;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Infrastructure.Auditing;
using Insys.BuildingBlocks.Infrastructure.Idempotency;
using Insys.BuildingBlocks.Infrastructure.Messaging;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.BuildingBlocks.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Insys.BuildingBlocks.Infrastructure;

public static class InfrastructureRegistration
{
    public const string ConnectionStringName = "Insys";

    /// <summary>
    /// True while Microsoft.Extensions.ApiDescription.Server starts the host at build time to write the OpenAPI
    /// document. Background work that needs the database is not started then.
    /// </summary>
    public static bool IsBuildTimeDocumentGeneration =>
        Assembly.GetEntryAssembly()?.GetName().Name?.StartsWith("GetDocument.", StringComparison.Ordinal) == true;

    /// <summary>Registers the shared infrastructure: data source, execution scope, pipeline, outbox, inbox, idempotency, audit.</summary>
    public static IServiceCollection AddInsysInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IClock, SystemClock>();
        services.AddSingleton(_ => new NpgsqlDataSourceBuilder(
            configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"ConnectionStrings:{ConnectionStringName} is not configured.")).Build());

        services.AddScoped<ExecutionScope>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<ExecutionScope>());
        services.AddScoped<IExecutionContext>(sp => sp.GetRequiredService<ExecutionScope>());
        services.AddScoped<ModuleDbContextDependencies>();
        services.AddSingleton<TenantTransactionInterceptor>();
        services.AddScoped<IDispatcher, Dispatcher>();

        services.AddScoped<AuditTrail>();
        services.AddScoped<IAuditTrail>(sp => sp.GetRequiredService<AuditTrail>());
        services.AddSingleton<AuditEventWriter>();

        services.AddOptions<IdempotencyOptions>().Bind(configuration.GetSection("Idempotency"));
        services.AddScoped<IIdempotencyStore, PostgresIdempotencyStore>();

        services.AddOptions<FieldProtectionOptions>().Bind(configuration.GetSection("FieldProtection"));
        services.AddSingleton<IFieldProtector, AesGcmFieldProtector>();

        services.AddOptions<OutboxOptions>().Bind(configuration.GetSection("Outbox"));
        services.AddSingleton<IEventBus, InProcessEventBus>();
        services.AddSingleton<OutboxProcessor>();
        if (!IsBuildTimeDocumentGeneration)
        {
            services.AddHostedService<OutboxRelayService>();
        }

        services.AddHealthChecks().AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"]);
        return services;
    }

    /// <summary>Registers a module DbContext for its schema, with the tenant interceptor, and adds the schema to the outbox relay.</summary>
    public static IServiceCollection AddModuleDbContext<TDbContext>(this IServiceCollection services, string schema)
        where TDbContext : ModuleDbContext
    {
        services.AddDbContext<TDbContext>((sp, options) => options
            .UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>())
            .AddInterceptors(sp.GetRequiredService<TenantTransactionInterceptor>()));
        services.AddSingleton(new OutboxSchema(schema));
        return services;
    }

    /// <summary>
    /// Registers every validator and every command and query handler in the module's Application assembly, wrapped as
    /// validation → transaction on <typeparamref name="TDbContext"/> → handler.
    /// </summary>
    public static IServiceCollection AddModuleHandlers<TDbContext>(this IServiceCollection services, Assembly applicationAssembly)
        where TDbContext : ModuleDbContext
    {
        foreach (var type in applicationAssembly.GetTypes().Where(t => t is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false }))
        {
            foreach (var validatorInterface in type.GetInterfaces().Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(FluentValidation.IValidator<>)))
            {
                services.AddScoped(validatorInterface, type);
            }

            foreach (var handlerInterface in type.GetInterfaces().Where(i => i.IsGenericType))
            {
                var definition = handlerInterface.GetGenericTypeDefinition();
                if (definition != typeof(ICommandHandler<,>) && definition != typeof(IQueryHandler<,>))
                {
                    continue;
                }

                var arguments = handlerInterface.GetGenericArguments();
                var isCommand = definition == typeof(ICommandHandler<,>);
                var transactional = (isCommand ? typeof(TransactionalCommandHandler<,,>) : typeof(TransactionalQueryHandler<,,>))
                    .MakeGenericType(arguments[0], arguments[1], typeof(TDbContext));
                var validating = (isCommand ? typeof(ValidatingCommandHandler<,>) : typeof(ValidatingQueryHandler<,>))
                    .MakeGenericType(arguments[0], arguments[1]);

                services.AddScoped(type);
                services.AddScoped(handlerInterface, sp =>
                {
                    var handler = sp.GetRequiredService(type);
                    var inTransaction = ActivatorUtilities.CreateInstance(sp, transactional, handler);
                    return ActivatorUtilities.CreateInstance(sp, validating, inTransaction);
                });
            }
        }

        return services;
    }

    /// <summary>Registers a module's domain event mapper for its schema's outbox.</summary>
    public static IServiceCollection AddDomainEventMapper<TMapper>(this IServiceCollection services, string schema)
        where TMapper : class, IDomainEventMapper =>
        services.AddKeyedSingleton<IDomainEventMapper, TMapper>(schema);

    /// <summary>
    /// Subscribes a consuming module's policy to an integration event. The handler runs behind the inbox in the
    /// consumer module's transaction. The consumer name is "{schema}.{HandlerName}" (max 60 characters).
    /// </summary>
    public static IServiceCollection AddIntegrationEventHandler<TEvent, THandler, TDbContext>(this IServiceCollection services, string schema)
        where TEvent : class, IIntegrationEvent
        where THandler : class, IIntegrationEventHandler<TEvent>
        where TDbContext : ModuleDbContext
    {
        var consumer = $"{schema}.{typeof(THandler).Name}";
        if (consumer.Length > 60)
        {
            throw new InvalidOperationException($"Consumer name '{consumer}' is longer than inbox_message.consumer allows (60).");
        }

        services.AddScoped<THandler>();
        services.AddScoped<InboxEventHandler<TEvent, THandler, TDbContext>>();
        services.AddSingleton(new EventSubscription(
            IntegrationEventTypes.Describe(typeof(TEvent)).Type,
            consumer,
            typeof(TEvent),
            (sp, integrationEvent, envelope, cancellationToken) =>
                sp.GetRequiredService<InboxEventHandler<TEvent, THandler, TDbContext>>().Handle(consumer, (TEvent)integrationEvent, envelope, cancellationToken)));
        return services;
    }
}
