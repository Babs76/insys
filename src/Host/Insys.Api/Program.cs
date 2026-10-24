/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Api.Host
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
using Insys.BuildingBlocks.Cloud;
using Insys.Api.Platform;
using Insys.BuildingBlocks.Api;
using Insys.BuildingBlocks.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// The modules in this host, one per bounded context in domain-model.json (Portals and Channels has none).
IModule[] modules =
[
    new Insys.Integration.Api.IntegrationModule(),
    new Insys.StaticData.Api.StaticDataModule(),
    new Insys.Party.Api.PartyModule(),
    new Insys.ProductFactory.Api.ProductFactoryModule(),
    new Insys.Rating.Api.RatingModule(),
    new Insys.Rules.Api.RulesModule(),
    new Insys.Workflow.Api.WorkflowModule(),
    new Insys.Documents.Api.DocumentsModule(),
    new Insys.Billing.Api.BillingModule(),
    new Insys.Payments.Api.PaymentsModule(),
    new Insys.ClaimsFramework.Api.ClaimsFrameworkModule(),
    new Insys.Life.Api.LifeModule(),
    new Insys.General.Api.GeneralModule(),
    new Insys.Health.Api.HealthModule(),
    new Insys.Finance.Api.FinanceModule(),
    new Insys.Distribution.Api.DistributionModule(),
    new Insys.Reinsurance.Api.ReinsuranceModule(),
    new Insys.Compliance.Api.ComplianceModule(),
    new Insys.Analytics.Api.AnalyticsModule(),
    new Insys.Tenancy.Api.TenancyModule(),
    new Insys.Identity.Api.IdentityModule(),
    new Insys.Audit.Api.AuditModule(),
];

builder.AddInsysTelemetry();
builder.Services.AddInsysInfrastructure(builder.Configuration);
builder.Services.AddInsysCloud(builder.Configuration);
builder.Services.AddInsysApi();
builder.Services.AddDailyJobRunner(builder.Configuration);
foreach (var module in modules)
{
    module.Register(builder.Services, builder.Configuration);
}

var app = builder.Build();

app.UseInsysApi();
app.MapInsysHealth();
app.MapOpenApi("/openapi/{documentName}.json").AllowAnonymous();

var v1 = app.MapInsysV1();
foreach (var module in modules)
{
    // ADR-024: business modules refuse the operator tenant; platform modules are mapped as they are.
    var moduleGroup = ((RouteGroupBuilder)v1).MapGroup("").ApplyRequestScope();
    if (module.ChannelScoped)
    {
        moduleGroup.WithMetadata(new ChannelScopedMetadata());
    }

    module.MapEndpoints(module.HoldsBusinessData ? moduleGroup.RefuseOperatorTenant() : moduleGroup);
}

// Test only (ADR-025), mapped last and kept out of the documents.
v1.MapDailyJobRunner(app.Configuration, app.Environment);

app.Run();

/// <summary>Entry point, public for WebApplicationFactory in the tests.</summary>
public partial class Program;
