namespace Insys.EndToEndTests;

/// <summary>
/// The six cross-context processes in domain-model.json, end to end with their compensations. Each is implemented in
/// the phase that builds its modules and is skipped until then.
/// </summary>
public sealed class ProcessTests
{
    [Fact(Skip = "Phase 2: Product Factory, Rating, Rules, Workflow and Documents")]
    public void ProductPublication() { }

    [Fact(Skip = "Phase 4 or 5: General domain")]
    public void GeneralQuoteToCash() { }

    [Fact(Skip = "Phase 4 or 5: Life domain")]
    public void LifeProposalToPolicy() { }

    [Fact(Skip = "Phase 4 or 5: General domain")]
    public void GeneralClaimToSettlement() { }

    [Fact(Skip = "Phase 4 or 5: Health domain")]
    public void HealthCareToProviderPayment() { }

    [Fact(Skip = "Phase 4 or 5: Life domain, with Billing and Payments from Phase 3")]
    public void MissedPremiumToLapse() { }

    [Fact]
    public void DomainModel_ListsExactlyTheseSixProcesses()
    {
        using var model = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(Insys.Testing.RepositoryPaths.Specs, "domain-model.json")));
        model.RootElement.GetProperty("processes").EnumerateArray().Select(p => p.GetProperty("name").GetString()).ShouldBe(
        [
            "General quote to cash", "Life proposal to policy", "General claim to settlement",
            "Health care to provider payment", "Product publication", "Missed premium to lapse",
        ]);
    }
}
