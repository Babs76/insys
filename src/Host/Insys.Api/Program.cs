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
builder.Services.AddInsysApi();
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
    module.MapEndpoints(v1);
}

app.Run();

/// <summary>Entry point, public for WebApplicationFactory in the tests.</summary>
public partial class Program;
