using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.BuildingBlocks.Api;

/// <summary>A bounded-context module as the host sees it. Each module's Api project provides one.</summary>
public interface IModule
{
    /// <summary>The module's name, also its OpenAPI tag.</summary>
    string Name { get; }

    void Register(IServiceCollection services, IConfiguration configuration);

    /// <summary>Maps the module's endpoints onto the versioned /api/v1 group.</summary>
    void MapEndpoints(IEndpointRouteBuilder api);
}
