using Insys.BuildingBlocks.Domain;

namespace Insys.ProductFactory.Domain.Products;

public enum SimulationStatus
{
    /// <summary>Every test case passed.</summary>
    Passed,

    /// <summary>At least one test case failed.</summary>
    Failed,

    /// <summary>The library has no test cases for the version: nothing shows the rating is right, so it does not count as passed.</summary>
    NoCases,

    /// <summary>The version's configuration changed after the run.</summary>
    Stale,
}

/// <summary>Entity SimulationResult (product.simulation_result): one test case's expected and actual outcome.</summary>
public sealed class SimulationResult : Entity<Guid>, ITenantScoped
{
    private SimulationResult()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid RunId { get; private set; }

    public string CaseId { get; private set; } = "";

    /// <summary>JSON: the expected premium and tolerance.</summary>
    public string Expected { get; private set; } = "{}";

    /// <summary>JSON: the premium rated, or the error.</summary>
    public string Actual { get; private set; } = "{}";

    public bool Passed { get; private set; }

    internal static SimulationResult Create(Guid runId, string caseId, string expected, string actual, bool passed) =>
        new() { Id = Guid.CreateVersion7(), RunId = runId, CaseId = caseId.Length <= 20 ? caseId : caseId[..20], Expected = expected, Actual = actual, Passed = passed };
}

/// <summary>One case's outcome, as Rating reported it.</summary>
public sealed record SimulationCase(string CaseId, string ExpectedJson, string ActualJson, bool Passed);

/// <summary>
/// Command RunSimulation (product.simulation_run): the rating test case library run against a validated version
/// (PRD-005). Recorded with every case's result; marked stale when the version's configuration changes again.
/// </summary>
public sealed class SimulationRun : AggregateRoot<Guid>
{
    private readonly List<SimulationResult> _results = [];

    private SimulationRun()
    {
    }

    public string RunNumber { get; private set; } = "";

    public string ProductCode { get; private set; } = "";

    public int ProductVersion { get; private set; }

    public Guid StartedBy { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public SimulationStatus Status { get; private set; }

    public int CasesTotal { get; private set; }

    public int CasesFailed { get; private set; }

    public IReadOnlyList<SimulationResult> Results => _results;

    public static SimulationRun Record(Guid id, ProductVersion version, Guid startedBy, IReadOnlyList<SimulationCase> cases, DateTimeOffset now)
    {
        var run = new SimulationRun
        {
            Id = Guard.NotEmpty(id),
            RunNumber = "SIM-" + id.ToString("N")[..16].ToUpperInvariant(),
            ProductCode = version.ProductCode,
            ProductVersion = version.Version,
            StartedBy = startedBy,
            StartedAt = now,
            CasesTotal = cases.Count,
            CasesFailed = cases.Count(c => !c.Passed),
        };
        run.Status = run.CasesTotal == 0 ? SimulationStatus.NoCases : run.CasesFailed == 0 ? SimulationStatus.Passed : SimulationStatus.Failed;
        run._results.AddRange(cases.Select(c => SimulationResult.Create(id, c.CaseId, c.ExpectedJson, c.ActualJson, c.Passed)));
        run.MarkChanged();
        return run;
    }

    public void MarkStale()
    {
        if (Status != SimulationStatus.Stale)
        {
            Status = SimulationStatus.Stale;
            MarkChanged();
        }
    }
}
