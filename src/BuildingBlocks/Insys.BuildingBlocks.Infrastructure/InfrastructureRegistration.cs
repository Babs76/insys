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

/// <summary>Keys of the shared data sources. Module data sources are keyed by the module's primary schema.</summary>
public static class DataSourceKeys
{
    /// <summary>The API building block's role (insys_api): integration.idempotency_key only.</summary>
    public const string Api = "api";

    /// <summary>A login that is a member of insys_platform_admin, used only to write global static data rows.</summary>
    public const string PlatformAdmin = "platform-admin";
}

public static class InfrastructureRegistration
{

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
        services.AddOptions<PlatformOptions>().Bind(configuration.GetSection("Platform"));
        services.AddDataSource(DataSourceKeys.Api, "Api", "insys_api");
        services.AddDataSource(DataSourceKeys.PlatformAdmin, "PlatformAdmin", "insys_platform_admin_login");

        services.AddScoped<ExecutionScope>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<ExecutionScope>());
        services.AddScoped<IExecutionContext>(sp => sp.GetRequiredService<ExecutionScope>());
        services.AddScoped<ModuleDbContextDependencies>();
        services.AddSingleton<TenantTransactionInterceptor>();
        services.AddScoped<IDispatcher, Dispatcher>();

        services.AddScoped<AuditTrail>();
        services.AddScoped<IAuditTrail>(sp => sp.GetRequiredService<AuditTrail>());
        services.AddScoped<AfterCommitActions>();
        services.AddScoped<IAfterCommit>(sp => sp.GetRequiredService<AfterCommitActions>());

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

    /// <summary>
    /// Registers a module DbContext on its own data source (ConnectionStrings:{connectionName}, the module's own
    /// database role; OQ-004), with the tenant interceptor, and adds the schema to the outbox relay.
    /// </summary>
    public static IServiceCollection AddModuleDbContext<TDbContext>(this IServiceCollection services, string schema, string connectionName)
        where TDbContext : ModuleDbContext
    {
        services.AddDataSource(schema, connectionName, $"insys_{schema}");
        services.AddDbContext<TDbContext>((sp, options) => options
            .UseNpgsql(sp.GetRequiredKeyedService<NpgsqlDataSource>(schema), npgsql => npgsql.UseNodaTime())
            .AddInterceptors(sp.GetRequiredService<TenantTransactionInterceptor>()));
        services.AddSingleton(new OutboxSchema(schema));
        return services;
    }

    /// <summary>
    /// A keyed data source, built on first use so configuration added after registration applies. It uses
    /// ConnectionStrings:{connectionName}, or else ConnectionStrings:Template with {role} replaced by the role name
    /// (local development and tests, where every role shares one password). NodaTime is enabled on the data source as well
    /// as in EF, so interval columns with months read and write as Period.
    /// </summary>
    public static IServiceCollection AddDataSource(this IServiceCollection services, string key, string connectionName, string roleName)
    {
        services.TryAddKeyedSingleton(key, (sp, _) =>
        {
            var configuration = sp.GetRequiredService<IConfiguration>();
            var connectionString = configuration.GetConnectionString(connectionName);
            if (string.IsNullOrWhiteSpace(connectionString) && configuration.GetConnectionString("Template") is { Length: > 0 } template)
            {
                connectionString = template.Replace("{role}", roleName, StringComparison.Ordinal);
            }

            return string.IsNullOrWhiteSpace(connectionString)
                ? throw new InvalidOperationException($"Neither ConnectionStrings:{connectionName} nor ConnectionStrings:Template is configured.")
                : new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build();
        });
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
    /// consumer module's transaction. The consumer name is "{schema}.{HandlerName}" plus ".{suffix}" when one handler
    /// class consumes several events (max 60 characters).
    /// </summary>
    public static IServiceCollection AddIntegrationEventHandler<TEvent, THandler, TDbContext>(this IServiceCollection services, string schema,
        string? suffix = null)
        where TEvent : class, IIntegrationEvent
        where THandler : class, IIntegrationEventHandler<TEvent>
        where TDbContext : ModuleDbContext
    {
        var handlerName = typeof(THandler).Name;
        var consumer = $"{schema}.{(handlerName.IndexOf('`') is var tick and > 0 ? handlerName[..tick] : handlerName)}{(suffix is null ? "" : "." + suffix)}";
        if (consumer.Length > 60)
        {
            throw new InvalidOperationException($"Consumer name '{consumer}' is longer than inbox_message.consumer allows (60).");
        }

        services.TryAddScoped<THandler>();
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
