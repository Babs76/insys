using Insys.BuildingBlocks.Domain;

namespace Insys.Life.Domain;

public static class LifeErrorCodes
{
    [ErrorCode(422, "error.life.product_not_available")]
    public const string ProductNotAvailable = "LIFE_PRODUCT_NOT_AVAILABLE";

    [ErrorCode(422, "error.life.code_not_in_force")]
    public const string CodeNotInForce = "LIFE_CODE_NOT_IN_FORCE";

    [ErrorCode(422, "error.life.benefit_not_in_product")]
    public const string BenefitNotInProduct = "LIFE_BENEFIT_NOT_IN_PRODUCT";

    [ErrorCode(422, "error.life.beneficiary_shares_invalid")]
    public const string BeneficiarySharesInvalid = "BENEFICIARY_SHARES_INVALID";

    [ErrorCode(422, "error.life.nomination_rule_breached")]
    public const string NominationRuleBreached = "NOMINATION_RULE_BREACHED";

    [ErrorCode(409, "error.life.proposal_not_eligible")]
    public const string ProposalNotEligible = "PROPOSAL_NOT_ELIGIBLE";

    [ErrorCode(409, "error.life.proposal_frozen")]
    public const string ProposalFrozen = "PROPOSAL_FROZEN";

    [ErrorCode(422, "error.life.rating_mismatch")]
    public const string RatingMismatch = "LIFE_RATING_MISMATCH";

    [ErrorCode(409, "error.life.evidence_outstanding")]
    public const string EvidenceOutstanding = "UW_EVIDENCE_OUTSTANDING";

    [ErrorCode(422, "error.life.terms_required")]
    public const string TermsRequired = "UNDERWRITING_TERMS_REQUIRED";

    [ErrorCode(409, "error.life.decision_pending")]
    public const string DecisionPending = "UNDERWRITING_DECISION_PENDING";

    [ErrorCode(409, "error.life.approval_subject_changed")]
    public const string ApprovalSubjectChanged = "LIFE_APPROVAL_SUBJECT_CHANGED";

    /// <summary>A loaded premium needs the rating of loadings, which actuarial has not defined (OQ-052).</summary>
    [ErrorCode(409, "error.life.loaded_terms_not_rated")]
    public const string LoadedTermsNotRated = "LOADED_TERMS_NOT_RATED";

    /// <summary>A surrender, paid-up or maturity value needs a calculation actuarial has not defined (OQ-052).</summary>
    [ErrorCode(409, "error.life.calculation_not_configured")]
    public const string CalculationNotConfigured = "LIFE_VALUE_NOT_CONFIGURED";

    [ErrorCode(409, "error.life.reinstatement_not_permitted")]
    public const string ReinstatementNotPermitted = "LIFE_REINSTATEMENT_REFUSED";

    [ErrorCode(409, "error.life.alteration_not_allowed")]
    public const string AlterationNotAllowed = "ALTERATION_NOT_ALLOWED";

    [ErrorCode(422, "error.life.not_covered")]
    public const string NotCovered = "LIFE_EVENT_NOT_COVERED";

    [ErrorCode(422, "error.life.claimant_not_authorised")]
    public const string ClaimantNotAuthorised = "CLAIMANT_NOT_AUTHORISED";

    [ErrorCode(409, "error.life.claim_already_open")]
    public const string ClaimAlreadyOpen = "LIFE_CLAIM_ALREADY_OPEN";

    [ErrorCode(422, "error.life.rationale_required")]
    public const string RationaleRequired = "LIFE_RATIONALE_REQUIRED";

    [ErrorCode(422, "error.life.payee_invalid")]
    public const string PayeeInvalid = "LIFE_PAYEE_INVALID";

    [ErrorCode(422, "error.life.bank_account_not_payable")]
    public const string BankAccountNotPayable = "LIFE_BANK_ACCOUNT_NOT_PAYABLE";

    [ErrorCode(422, "error.life.loan_not_supported")]
    public const string LoanNotSupported = "LOAN_NOT_SUPPORTED";

    [ErrorCode(422, "error.life.loan_exceeds_cash_value")]
    public const string LoanExceedsCashValue = "LOAN_EXCEEDS_CASH_VALUE";

    [ErrorCode(422, "error.life.repayment_exceeds_balance")]
    public const string RepaymentExceedsBalance = "REPAYMENT_EXCEEDS_BALANCE";

    [ErrorCode(422, "error.life.member_not_eligible")]
    public const string MemberNotEligible = "GROUP_MEMBER_NOT_ELIGIBLE";

    [ErrorCode(422, "error.life.rule_not_evaluable")]
    public const string RuleNotEvaluable = "LIFE_RULE_NOT_EVALUABLE";
}
