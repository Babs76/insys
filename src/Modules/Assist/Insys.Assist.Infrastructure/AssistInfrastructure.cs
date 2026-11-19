/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Assist
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
using Insys.Assist.Application;
using Insys.Assist.Domain;
using Insys.Assist.Infrastructure.Persistence;
using Insys.BuildingBlocks.Application.Ai;
using Insys.BuildingBlocks.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Insys.Assist.Infrastructure;

public static class AssistInfrastructure
{
    public static IServiceCollection AddAssistInfrastructure(this IServiceCollection services)
    {
        const string schema = AssistDbContext.SchemaName;
        services.AddModuleDbContext<AssistDbContext>(schema, "Assist");
        services.AddModuleHandlers<AssistDbContext>(typeof(GetAssistSettings).Assembly);
        services.AddScoped<IAssistRepository, AssistRepository>();
        services.AddScoped<IStarterPacks, StarterPacks>();
        services.AddScoped<ISetupRuns, SetupRuns>();
        // AIA-086: a setup submitted as a SETUP change set, which Static Data records and puts to one approval; the run follows the set's outcome.
        services.AddDomainEventMapper<AssistEventMapper>(schema);
        services.AddIntegrationEventHandler<StaticData.Contracts.SetupChangeSetApplied, SetupChangeSetOutcomes, AssistDbContext>(schema, "SetupApplied");
        services.AddIntegrationEventHandler<StaticData.Contracts.SetupChangeSetNotApproved, SetupChangeSetOutcomes, AssistDbContext>(schema, "SetupNotApproved");
        services.AddIntegrationEventHandler<StaticData.Contracts.SetupChangeSetAbandoned, SetupChangeSetOutcomes, AssistDbContext>(schema, "SetupAbandoned");
        // No model unless the deployment connects one (the Cloud adapter project registers Claude when Assist:Provider is claude).
        // The stand-in model only where Assist:Provider says so, and only in Development and Testing (its guard stops any other host).
        services.TryAddSingleton<IAiModelProvider>(sp =>
            string.Equals(sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()["Assist:Provider"], DevelopmentAiModelProvider.Provider,
                StringComparison.OrdinalIgnoreCase)
                ? new DevelopmentAiModelProvider(sp.GetRequiredService<Microsoft.Extensions.Hosting.IHostEnvironment>())
                : new NoAiModelProvider());
        services.AddHostedService<DevelopmentModelGuard>();
        services.AddScoped<AssistRunner>();
        services.AddScoped<Explainer>();
        // AI-1 (7.4): runs, extraction, and the job that acts for the user who asked (AIA-017).
        services.AddScoped<IProductDraftRuns, ProductDraftRuns>();
        services.AddScoped<ProductDraftStarter>();
        // AI-1's review (7.5): decisions (AIA-019), the provenance report (AIA-020) and the simulation cases' hand-off (AIA-018).
        services.AddScoped<ProductDraftReviewer>();
        services.AddScoped<ProductDraftProvenance>();
        services.AddScoped<ProductDraftSimulationCases>();
        // OQ-190 (b): Product Factory's submission asks whether an AI-1 draft's review is complete (the second narrow exception to SRS §9.2).
        services.AddScoped<ProductFactory.Contracts.IProductSubmissionCheck, ProductDraftSubmissionCheck>();
        services.AddSingleton<IDraftDocumentExtractor, Extraction.DraftDocumentExtractor>();
        services.TryAddSingleton<BuildingBlocks.Application.Context.ActingUserScopes>();
        services.AddOptions<ProductDraftOptions>().BindConfiguration("Assist:ProductDrafts");
        services.AddSingleton<ProductDraftWorker>();
        services.AddSingleton<ProductDraftJob>();
        if (!BuildingBlocks.Infrastructure.InfrastructureRegistration.IsBuildTimeDocumentGeneration)
        {
            services.AddHostedService<ProductDraftService>();
        }
        // The prompt templates are configuration (AIA-074): the host doesn't start without one for each AI-2 feature.
        services.AddOptions<AssistOptions>().BindConfiguration("Assist")
            .Validate(o => o.PromptFor(AssistFeatures.ExplainRefusal) is not null && o.PromptFor(AssistFeatures.ExplainPremium) is not null,
                "Assist:Prompts needs a version and text for EXPLAIN_REFUSAL and EXPLAIN_PREMIUM.")
            .Validate(o => o.InferenceRegions.Length > 0, "Assist:InferenceRegions needs the regions a tenant may name, in the AI provider's terms.")
            .ValidateOnStart();
        return services;
    }
}

/// <summary>Assist's domain events that other modules hear.</summary>
internal sealed class AssistEventMapper : BuildingBlocks.Application.Messaging.IDomainEventMapper
{
    public BuildingBlocks.Contracts.IIntegrationEvent? Map(BuildingBlocks.Domain.IDomainEvent domainEvent) => domainEvent switch
    {
        SetupChangeSetReady ready => new StaticData.Contracts.SetupChangeSetRequested(ready.SetId, ready.RunId, ready.MakerId,
            [.. ready.Batches.Select(b => new StaticData.Contracts.SetupChangeSetBatch(b.Seq, b.Table, b.EntityCode, b.BatchId, b.ChangeRequestId, b.Lines, b.Hash))]),
        SetupSubmissionWithdrawn withdrawn => new StaticData.Contracts.SetupChangeSetWithdrawn(withdrawn.SetId, withdrawn.RunId, withdrawn.MakerId),
        _ => null,
    };
}
