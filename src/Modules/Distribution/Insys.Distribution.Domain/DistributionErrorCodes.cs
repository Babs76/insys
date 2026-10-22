using Insys.BuildingBlocks.Domain;

namespace Insys.Distribution.Domain;

public static class DistributionErrorCodes
{
    [ErrorCode(422, "error.distribution.plan_not_applicable")]
    public const string PlanNotApplicable = "COMMISSION_PLAN_NOT_APPLICABLE";

    [ErrorCode(409, "error.distribution.nothing_to_accrue")]
    public const string NothingToAccrue = "COMMISSION_NOTHING_TO_ACCRUE";

    [ErrorCode(409, "error.distribution.statement_not_draft")]
    public const string StatementNotDraft = "STATEMENT_NOT_DRAFT";

    [ErrorCode(409, "error.distribution.statement_empty")]
    public const string StatementEmpty = "STATEMENT_EMPTY";

    [ErrorCode(409, "error.distribution.statement_changed")]
    public const string StatementChanged = "STATEMENT_CHANGED";

    [ErrorCode(422, "error.distribution.intermediary_not_payable")]
    public const string IntermediaryNotPayable = "INTERMEDIARY_NOT_PAYABLE";

    [ErrorCode(409, "error.distribution.intermediary_transition")]
    public const string IntermediaryTransition = "INTERMEDIARY_TRANSITION";

    [ErrorCode(422, "error.distribution.intermediary_not_permitted")]
    public const string IntermediaryNotPermitted = "INTERMEDIARY_NOT_PERMITTED";

    [ErrorCode(422, "error.distribution.intermediary_suspended")]
    public const string IntermediarySuspended = "INTERMEDIARY_SUSPENDED";
}
