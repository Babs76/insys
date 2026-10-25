/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Integration
 * CONFIDENTIALITY AND INTELLECTUAL PROPERTY NOTICE
 * This source code and its associated documentation contain confidential and
 * proprietary information belonging to OpenFactor Technology Group or its
 * applicable intellectual property rights holder.
 * This material is provided solely for authorised development, maintenance,
 * operation or evaluation purposes, as applicable. No person may reproduce,
 * modify, distribute, disclose, publish or otherwise use this material except
 * as expressly permitted by an applicable written agreement or licence.
 * No ownership rights or licence to any intellectual property are granted
 * by the inclusion of this notice, except as expressly provided in an
 * applicable written agreement or licence.
 * Third-party software, open-source components and customer-owned materials
 * remain subject to their respective ownership rights and applicable licences
 * or agreements.
 * UNAUTHORISED ACCESS, USE, COPYING OR DISCLOSURE IS PROHIBITED.
 * ============================================================================
 */
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
        // Its dashboard figure codes, for the OpenAPI enum of DashboardFigure.code.
        BuildingBlocks.Application.DashboardFigureCodes.Declare(Insys.Integration.Application.Parking.DeliveryFigures.Parked, Insys.Integration.Application.Parking.DeliveryFigures.Failing, Insys.Integration.Application.Parking.DeliveryFigures.DeadLettersOpen,
            Insys.Integration.Application.Parking.DeliveryFigures.WebhooksFailing, Insys.Integration.Application.Parking.DeliveryFigures.OldestParkedAt, Insys.Integration.Application.Parking.DeliveryFigures.JobFunctionsOff);
        services.AddModuleDbContext<IntegrationDbContext>(IntegrationDbContext.SchemaName, "Integration");
        services.AddStaticData<IntegrationDbContext>(IntegrationDbContext.SchemaName, IntegrationStaticData.Descriptors);
        services.AddSingleton<IStaticRecordValidator, Channels.ChannelRowValidator>();
        services.AddModuleHandlers<IntegrationDbContext>(typeof(DeadLetterMessage).Assembly);
        services.AddScoped<IDeadLetterRepository, DeadLetterRepository>();
        services.AddScoped<BuildingBlocks.Application.Context.IChannelPolicy, Channels.ChannelPolicy>();
        services.AddScoped<IDeadLetterQueries, DeadLetterQueries>();
        services.AddScoped<IDeadLetterSink, DeadLetterSink>();
        services.AddMemoryCache();
        services.AddSingleton<MessageCatalogue>();
        services.AddSingleton<Insys.BuildingBlocks.Application.Localisation.IMessageCatalogue>(sp => sp.GetRequiredService<MessageCatalogue>());
        services.AddIntegrationEventHandler<Insys.BuildingBlocks.Contracts.StaticData.StaticDataVersionActivated, MessageCachePolicy, IntegrationDbContext>(IntegrationDbContext.SchemaName, "MessagesActivated");

        // ADR-022: the delivery ledger, its job and its operations view. Registered after the building blocks' default.
        services.AddOptions<Parking.ParkingOptions>().BindConfiguration("Outbox:Parking");
        services.AddSingleton<IDeliveryLedger, Parking.PostgresDeliveryLedger>();
        services.AddSingleton<Parking.ParkingJob>();
        services.AddScoped<Application.Parking.IParkedDeliveryStore, Parking.ParkedDeliveryStore>();
        services.AddSingleton<IConfigurationOwner, Parking.ConfigurationOwner>();
        services.AddIntegrationEventHandler<Insys.BuildingBlocks.Contracts.StaticData.StaticDataVersionActivated, Parking.RetryParkedOnConfiguration, IntegrationDbContext>(
            IntegrationDbContext.SchemaName, "ParkedRetry");
        services.AddHealthChecks().AddCheck<Parking.ParkedDeliveriesHealthCheck>("parked-deliveries");
        if (!InfrastructureRegistration.IsBuildTimeDocumentGeneration)
        {
            services.AddHostedService<Parking.ParkingJobService>();
        }

        services.AddSingleton<Partners.StubPartnerAdapters>();
        services.AddSingleton<IReinsurerAdapter>(sp => sp.GetRequiredService<Partners.StubPartnerAdapters>());
        services.AddSingleton<IRegulatorAdapter>(sp => sp.GetRequiredService<Partners.StubPartnerAdapters>());
        services.AddSingleton<IGeneralLedgerAdapter>(sp => sp.GetRequiredService<Partners.StubPartnerAdapters>());

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
        // Onboarding readiness (addenda/onboarding-readiness.md §2): the configuration this module owns.
        BuildingBlocks.Infrastructure.Readiness.ReadinessRegistration.AddStaticReadiness(services, Persistence.IntegrationDbContext.SchemaName, IntegrationStaticData.Descriptors,
            new BuildingBlocks.Infrastructure.Readiness.StaticCheck("INTEGRATION_AND_REPORTING", "WEBHOOKS_OR_NONE", "integration.cfg_webhook_subscriptions", "Webhook subscriptions, or none recorded with a reason"));
        services.AddSingleton<BuildingBlocks.Application.IReadinessCheck, Channels.ChannelReadiness>();
        return services;
    }
}
