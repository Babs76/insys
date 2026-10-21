namespace Insys.BuildingBlocks.Domain;

/// <summary>
/// The rule every approval shares: the checker must be someone other than the maker (SRS-002).
/// Authority for the amount and type is checked by the Workflow module's Approval aggregate (Phase 2).
/// </summary>
public static class MakerChecker
{
    public static Result EnsureDifferentPeople(string makerId, string checkerId) =>
        string.Equals(makerId, checkerId, StringComparison.OrdinalIgnoreCase)
            ? Error.Forbidden(ErrorCodes.MakerCheckerSamePerson, "The checker must be a different person from the maker.")
            : Result.Success();
}
