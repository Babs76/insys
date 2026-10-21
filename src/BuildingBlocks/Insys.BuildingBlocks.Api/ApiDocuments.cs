namespace Insys.BuildingBlocks.Api;

/// <summary>Marks a static data endpoint (generated tables and their governance), which belongs in the static-data document.</summary>
public sealed record StaticDataEndpointMetadata;

/// <summary>
/// The three OpenAPI documents (Phase 1 review, item 4). Every endpoint is in exactly one:
/// platform operator endpoints first, then static data, and everything else in core.
/// </summary>
public static class ApiDocuments
{
    public const string Core = "core";
    public const string StaticData = "static-data";
    public const string PlatformOperator = "platform-operator";

    public static readonly IReadOnlyList<(string Name, string Title, string Description)> All =
    [
        (Core, "INSYS Core API",
            "Multi-tenant insurance administration platform. The tenant comes from the bearer token's tenant_id claim."),
        (StaticData, "INSYS Core static data API",
            "Generated endpoints for the static data tables of every module, and their governance: change requests, approval and imports."),
        (PlatformOperator, "INSYS Core platform operator API",
            "For the platform operator tenant only: tenant administration and global static data (scopes G and GT)."),
    ];

    public static string For(IEnumerable<object> endpointMetadata)
    {
        var metadata = endpointMetadata as IList<object> ?? endpointMetadata.ToList();
        return metadata.OfType<PlatformOperatorEndpointMetadata>().Any() ? PlatformOperator
            : metadata.OfType<StaticDataEndpointMetadata>().Any() ? StaticData
            : Core;
    }
}
