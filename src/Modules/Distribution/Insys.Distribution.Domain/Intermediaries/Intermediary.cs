using Insys.BuildingBlocks.Domain;

namespace Insys.Distribution.Domain.Intermediaries;

public enum IntermediaryStatus
{
    Appointed,
    Suspended,
    Terminated,
}

public enum IntermediaryTrigger
{
    SuspendIntermediary,
    Reinstated,
    TerminateIntermediary,
}

/// <summary>
/// The Intermediary aggregate's lifecycle. An intermediary is Tier 1 static data (commission.mst_intermediary_register):
/// AppointIntermediary is a new code, GrantProductPermission a permission row, and Suspend, Reinstate and Terminate a
/// new register version with another status, each through static data maker-checker (OQ-062 item 9). A new version's
/// status must follow these transitions; a suspended or terminated intermediary starts no new business (DST-001).
/// </summary>
public static class Intermediary
{
    public static readonly StateMachine<IntermediaryStatus, IntermediaryTrigger> Lifecycle = new StateMachine<IntermediaryStatus, IntermediaryTrigger>()
        .Allow(IntermediaryStatus.Appointed, IntermediaryTrigger.SuspendIntermediary, IntermediaryStatus.Suspended)
        .Allow(IntermediaryStatus.Suspended, IntermediaryTrigger.Reinstated, IntermediaryStatus.Appointed)
        .Allow(IntermediaryStatus.Appointed, IntermediaryTrigger.TerminateIntermediary, IntermediaryStatus.Terminated)
        .Allow(IntermediaryStatus.Suspended, IntermediaryTrigger.TerminateIntermediary, IntermediaryStatus.Terminated);

    /// <summary>The register's status codes: ACTIVE is Appointed.</summary>
    public static IntermediaryStatus FromRegister(string status) => status switch
    {
        "ACTIVE" => IntermediaryStatus.Appointed,
        "SUSPENDED" => IntermediaryStatus.Suspended,
        "TERMINATED" => IntermediaryStatus.Terminated,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Not an intermediary status."),
    };

    /// <summary>A new register version: a new code is appointed (ACTIVE); a change of status follows the lifecycle; the same status is any other change.</summary>
    public static Result CheckChange(string? currentStatus, string proposedStatus)
    {
        var proposed = FromRegister(proposedStatus);
        if (currentStatus is null)
        {
            return proposed == IntermediaryStatus.Appointed
                ? Result.Success()
                : Error.Validation(DistributionErrorCodes.IntermediaryTransition, "A new intermediary is appointed with status ACTIVE.");
        }

        var current = FromRegister(currentStatus);
        if (current == proposed)
        {
            return current == IntermediaryStatus.Terminated
                ? Error.Conflict(DistributionErrorCodes.IntermediaryTransition, "A terminated intermediary is not changed.")
                : Result.Success();
        }

        return Lifecycle.Transitions.Any(t => t.From == current && t.To == proposed)
            ? Result.Success()
            : Error.Conflict(DistributionErrorCodes.IntermediaryTransition, $"An intermediary cannot go from {currentStatus} to {proposedStatus}.");
    }
}
