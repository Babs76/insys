using Insys.Api.Platform;
using Insys.BuildingBlocks.Api;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Infrastructure;
using Insys.Integration.Api;

var builder = WebApplication.CreateBuilder(args);

// The modules in this host. Each module is a bounded context from domain-model.json.
IModule[] modules = [new IntegrationModule()];

builder.AddInsysTelemetry();
builder.Services.AddInsysInfrastructure(builder.Configuration);
builder.Services.AddInsysApi();
builder.Services.AddSingleton<ITenantDirectory, SqlTenantDirectory>();
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
