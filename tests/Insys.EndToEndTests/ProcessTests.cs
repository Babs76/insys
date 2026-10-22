namespace Insys.EndToEndTests;

/// <summary>
/// The six cross-context processes in domain-model.json, end to end with their compensations: product publication in
/// <see cref="ProductPublicationTests"/>, the General processes in <see cref="GeneralProcessTests"/>, the Life processes
/// in <see cref="LifeProcessTests"/> and the Health process in <see cref="HealthProcessTests"/>.
/// </summary>
public sealed class ProcessTests
{
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
