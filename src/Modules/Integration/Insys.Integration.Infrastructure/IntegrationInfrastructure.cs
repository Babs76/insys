using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Infrastructure;
using Insys.Integration.Application.DeadLetters;
using Insys.BuildingBlocks.Contracts;
using Insys.Integration.Application.Webhooks;
using Insys.Integration.Contracts;
using Insys.Integration.Infrastructure.Webhooks;
using Microsoft.Extensions.Options;
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Integration.Infrastructure.Persistence;
using Insys.Integration.Infrastructure.StaticData;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Integration.Infrastructure;

public static class IntegrationInfrastructure
{
    public static IServiceCollection AddIntegrationInfrastructure(this IServiceCollection services)
    {
        services.AddModuleDbContext<IntegrationDbContext>(IntegrationDbContext.SchemaName, "Integration");
        services.AddStaticData<IntegrationDbContext>(IntegrationDbContext.SchemaName, IntegrationStaticData.Descriptors);
        services.AddModuleHandlers<IntegrationDbContext>(typeof(DeadLetterMessage).Assembly);
        services.AddScoped<IDeadLetterRepository, DeadLetterRepository>();
        services.AddScoped<IDeadLetterQueries, DeadLetterQueries>();
        services.AddScoped<IDeadLetterSink, DeadLetterSink>();
        services.AddMemoryCache();
        services.AddSingleton<MessageCatalogue>();
        services.AddSingleton<Insys.BuildingBlocks.Application.Localisation.IMessageCatalogue>(sp => sp.GetRequiredService<MessageCatalogue>());
        services.AddIntegrationEventHandler<Insys.BuildingBlocks.Contracts.StaticData.StaticDataVersionActivated, MessageCachePolicy, IntegrationDbContext>(IntegrationDbContext.SchemaName, "MessagesActivated");
        services.AddSingleton<Partners.StubPartnerAdapters>();
        services.AddSingleton<IReinsurerAdapter>(sp => sp.GetRequiredService<Partners.StubPartnerAdapters>());
        services.AddSingleton<IRegulatorAdapter>(sp => sp.GetRequiredService<Partners.StubPartnerAdapters>());

        // Partner webhooks (INT-006): deliveries for every subscribed event, sent and retried by the webhook job.
        services.AddOptions<WebhookOptions>().BindConfiguration("Integration:Webhooks");
        services.AddScoped<IWebhookRepository, WebhookRepository>();
        services.AddScoped<IWebhookCodes, WebhookCodes>();
        services.AddSingleton<ISecretStore, ConfigurationSecretStore>();
        services.AddHttpClient("webhooks");
        services.AddSingleton<HttpWebhookTransport>();
        services.AddSingleton<StubWebhookTransport>();
        services.AddSingleton<IWebhookTransport>(sp => sp.GetRequiredService<IOptions<WebhookOptions>>().Value.Transport == "stub"
            ? sp.GetRequiredService<StubWebhookTransport>()
            : sp.GetRequiredService<HttpWebhookTransport>());
        services.AddIntegrationEventHandler<AnyIntegrationEvent, WebhookPolicy, IntegrationDbContext>(IntegrationDbContext.SchemaName, "Webhooks");
        services.AddSingleton<WebhookJobs>();
        if (!InfrastructureRegistration.IsBuildTimeDocumentGeneration)
        {
            services.AddHostedService<WebhookJobsService>();
        }
        return services;
    }
}
