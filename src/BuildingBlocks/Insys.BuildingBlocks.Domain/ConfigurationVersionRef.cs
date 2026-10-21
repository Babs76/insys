namespace Insys.BuildingBlocks.Domain;

/// <summary>
/// The version of a configuration item that governed a transaction: product, rating, rule set, posting rule,
/// template, commission plan or cession rule (PRD-008, DBA-010).
/// </summary>
public sealed record ConfigurationVersionRef(string Code, int Version)
{
    public override string ToString() => $"{Code}@{Version}";
}
