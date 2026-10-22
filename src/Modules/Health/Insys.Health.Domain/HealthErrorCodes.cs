using Insys.BuildingBlocks.Domain;

namespace Insys.Health.Domain;

public static class HealthErrorCodes
{
    [ErrorCode(422, "error.health.code_not_in_force")]
    public const string CodeNotInForce = "HEALTH_CODE_NOT_IN_FORCE";

    [ErrorCode(422, "error.health.dependant_not_eligible")]
    public const string DependantNotEligible = "DEPENDANT_NOT_ELIGIBLE";

    [ErrorCode(409, "error.health.member_not_covered")]
    public const string MemberNotCovered = "MEMBER_NOT_COVERED";

    [ErrorCode(409, "error.health.limit_exceeded")]
    public const string LimitExceeded = "BENEFIT_LIMIT_EXCEEDED";

    [ErrorCode(422, "error.health.authorisation_above_benefit")]
    public const string AuthorisationAboveBenefit = "AUTHORISATION_ABOVE_BENEFIT";

    [ErrorCode(422, "error.health.claim_invalid")]
    public const string ClaimInvalid = "MEDICAL_CLAIM_INVALID";

    [ErrorCode(409, "error.health.claim_pended")]
    public const string ClaimPended = "MEDICAL_CLAIM_PENDED";

    [ErrorCode(422, "error.health.provider_not_payable")]
    public const string ProviderNotPayable = "PROVIDER_NOT_PAYABLE";

    [ErrorCode(409, "error.health.approval_subject_changed")]
    public const string ApprovalSubjectChanged = "PAYMENT_RUN_SUBJECT_CHANGED";

    [ErrorCode(409, "error.health.run_empty")]
    public const string RunEmpty = "PAYMENT_RUN_EMPTY";

    [ErrorCode(422, "error.health.rationale_required")]
    public const string RationaleRequired = "HEALTH_RATIONALE_REQUIRED";
}
