using Insys.BuildingBlocks.Api;
using Insys.Documents.Api.StaticData;
using Insys.Documents.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Documents.Api;

/// <summary>The Documents module. So far it serves its static data only; its aggregates are built in a later phase.</summary>
public sealed class DocumentsModule : IModule
{
    public string Name => "Documents";

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddDocumentsInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api) => DocumentsStaticDataEndpoints.MapStaticData(api);
}
