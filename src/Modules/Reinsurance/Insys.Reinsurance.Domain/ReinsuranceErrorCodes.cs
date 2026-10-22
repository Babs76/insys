using Insys.BuildingBlocks.Domain;

namespace Insys.Reinsurance.Domain;

public static class ReinsuranceErrorCodes
{
    [ErrorCode(422, "error.reinsurance.treaty_not_supported")]
    public const string TreatyNotSupported = "RI_TREATY_NOT_SUPPORTED";

    [ErrorCode(409, "error.reinsurance.nothing_ceded")]
    public const string NothingCeded = "RI_NOTHING_CEDED";

    [ErrorCode(409, "error.reinsurance.nothing_recoverable")]
    public const string NothingRecoverable = "RI_NOTHING_RECOVERABLE";

    [ErrorCode(422, "error.reinsurance.layout_field_unknown")]
    public const string LayoutFieldUnknown = "RI_LAYOUT_FIELD_UNKNOWN";

    [ErrorCode(409, "error.reinsurance.bordereau_exists")]
    public const string BordereauExists = "RI_BORDEREAU_EXISTS";

    [ErrorCode(422, "error.reinsurance.period_invalid")]
    public const string PeriodInvalid = "RI_PERIOD_INVALID";
}
