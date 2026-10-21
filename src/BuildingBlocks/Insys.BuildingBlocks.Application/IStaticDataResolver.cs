namespace Insys.BuildingBlocks.Application;

/// <summary>
/// Returns the version of a static data record effective at a business date, with tenant overrides applied over
/// global baselines (scopes G, GT and T). Implemented by the Static Data module in Phase 1.
/// </summary>
public interface IStaticDataResolver
{
    Task<T?> Resolve<T>(string code, DateOnly asAt, CancellationToken cancellationToken)
        where T : class;
}
