using System.Text.Json;

namespace Insys.Testing;

/// <summary>Reads stateTransitions from docs/specs/domain-model.json, so tests can compare an aggregate's state machine with the model.</summary>
public static class DomainModelSpec
{
    public static List<string> Transitions(string context, string aggregate)
    {
        using var model = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryPaths.Specs, "domain-model.json")));
        return model.RootElement.GetProperty("contexts").EnumerateArray()
            .Single(c => c.GetProperty("id").GetString() == context)
            .GetProperty("aggregates").EnumerateArray().Single(a => a.GetProperty("name").GetString() == aggregate)
            .GetProperty("stateTransitions").EnumerateArray()
            .Select(t => $"{Normalise(t.GetProperty("from").GetString()!)}-{t.GetProperty("trigger").GetString()}->{Normalise(t.GetProperty("to").GetString()!)}")
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>"Pending approval" becomes PendingApproval; triggers that are events ("Window ends") are left as they are.</summary>
    public static string Normalise(string state) => string.Concat(state.Split('-', ' ').Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
}
