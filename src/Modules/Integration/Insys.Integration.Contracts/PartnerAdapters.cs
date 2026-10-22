using System.Text.Json;

namespace Insys.Integration.Contracts;

/// <summary>A partner's answer to a call: accepted with its reference, or why not.</summary>
public sealed record PartnerCallResult(bool Accepted, string? PartnerRef, string? Error);

/// <summary>
/// Partner adapter to reinsurers (§8, §17): bordereaux and recovery bills. The platform makes no real calls (PROMPT
/// §11); the stub implementation accepts each and records it in integration.external_call_log (INT-006).
/// </summary>
public interface IReinsurerAdapter
{
    Task<PartnerCallResult> SendBordereau(Guid tenantId, string partnerCode, string treatyCode, string periodCode, string content, CancellationToken cancellationToken);

    Task<PartnerCallResult> BillRecovery(Guid tenantId, string partnerCode, string treatyCode, string claimNumber, decimal amount, string currency,
        CancellationToken cancellationToken);
}

/// <summary>Partner adapter to regulators: returns are submitted through it; the stub accepts and records each call.</summary>
public interface IRegulatorAdapter
{
    Task<PartnerCallResult> SubmitReturn(Guid tenantId, string regulator, string returnCode, string periodCode, JsonElement values, CancellationToken cancellationToken);
}
