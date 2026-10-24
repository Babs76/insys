/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Domain
 * CONFIDENTIALITY AND INTELLECTUAL PROPERTY NOTICE
 * This source code and its associated documentation contain confidential and
 * proprietary information belonging to OpenFactor Technology Group or its
 * applicable intellectual property rights holder.
 * This material is provided solely for authorised development, maintenance,
 * operation or evaluation purposes, as applicable. No person may reproduce,
 * modify, distribute, disclose, publish or otherwise use this material except
 * as expressly permitted by an applicable written agreement or licence.
 * No ownership rights or licence to any intellectual property are granted
 * by the inclusion of this notice, except as expressly provided in an
 * applicable written agreement or licence.
 * Third-party software, open-source components and customer-owned materials
 * remain subject to their respective ownership rights and applicable licences
 * or agreements.
 * UNAUTHORISED ACCESS, USE, COPYING OR DISCLOSURE IS PROHIBITED.
 * ============================================================================
 */
using System.Reflection;

namespace Insys.BuildingBlocks.Domain;

/// <summary>
/// Describes an error code for the error code catalogue (integration.ref_error_code_catalogue). The constants in
/// code are the single source: db/migrations/R__error_code_catalogue.sql is generated from them, and a test fails
/// if the two differ.
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class ErrorCodeAttribute(int httpStatus, string messageKey, bool retryable = false) : Attribute
{
    public int HttpStatus { get; } = httpStatus;

    public string MessageKey { get; } = messageKey;

    public bool Retryable { get; } = retryable;
}

/// <summary>One catalogue entry, read from an <see cref="ErrorCodeAttribute"/>.</summary>
public sealed record ErrorCodeEntry(string Code, int HttpStatus, string MessageKey, bool Retryable, string Description);

/// <summary>Platform-wide error codes (max 30 characters, the size of error_code).</summary>
public static class ErrorCodes
{
    [ErrorCode(422, "error.request.validation_failed")]
    public const string ValidationFailed = "VALIDATION_FAILED";

    [ErrorCode(409, "error.state.invalid_transition")]
    public const string InvalidStateTransition = "INVALID_STATE_TRANSITION";

    [ErrorCode(404, "error.resource.not_found")]
    public const string NotFound = "NOT_FOUND";

    [ErrorCode(403, "error.access.forbidden")]
    public const string Forbidden = "FORBIDDEN";

    /// <summary>F103: an action on a record outside the caller's product or organisation scope for the permission.</summary>
    [ErrorCode(403, "error.access.out_of_scope")]
    public const string OutOfScope = "OUT_OF_SCOPE";

    /// <summary>F005: the request's client is on no channel in force in the tenant, or the channel does not enable the transaction.</summary>
    [ErrorCode(403, "error.access.channel_not_enabled")]
    public const string ChannelNotEnabled = "CHANNEL_NOT_ENABLED";

    /// <summary>F005: the caller's roles may not make this transaction, for this product, on this channel.</summary>
    [ErrorCode(403, "error.access.channel_not_allowed")]
    public const string ChannelNotAllowed = "CHANNEL_NOT_ALLOWED";

    /// <summary>F005: the channel requires approval for this transaction, which has no approval flow.</summary>
    [ErrorCode(403, "error.access.channel_approval_required")]
    public const string ChannelApprovalRequired = "CHANNEL_APPROVAL_REQUIRED";

    /// <summary>External-user access §5: an external user may use only the endpoints of their portal.</summary>
    [ErrorCode(403, "error.access.external_user_not_allowed")]
    public const string ExternalUserNotAllowed = "EXTERNAL_USER_NOT_ALLOWED";

    /// <summary>External-user access §6: the portal requires MFA, and the token shows none.</summary>
    [ErrorCode(403, "error.access.portal_mfa_required")]
    public const string PortalMfaRequired = "PORTAL_MFA_REQUIRED";

    /// <summary>A business command in a tenant still Onboarding: only identity, static data and configuration are open until it is activated.</summary>
    [ErrorCode(409, "error.access.tenant_not_active")]
    public const string TenantNotActive = "TENANT_NOT_ACTIVE";

    /// <summary>ADR-024: a business module's endpoint was called in the platform operator tenant.</summary>
    [ErrorCode(403, "error.access.operator_tenant_not_business")]
    public const string OperatorTenantNotBusiness = "OPERATOR_TENANT_NOT_BUSINESS";

    [ErrorCode(403, "error.access.tenant_context_missing")]
    public const string TenantContextMissing = "TENANT_CONTEXT_MISSING";

    [ErrorCode(401, "error.access.unauthenticated")]
    public const string Unauthenticated = "UNAUTHENTICATED";

    /// <summary>ADR-023: a provider callback whose key, signature, timestamp or client token is not valid; one answer for all of them.</summary>
    [ErrorCode(401, "error.callback.signature")]
    public const string CallbackSignatureInvalid = "CALLBACK_SIGNATURE_INVALID";

    /// <summary>ADR-023: a callback ID the endpoint already received, inside the replay window.</summary>
    [ErrorCode(409, "error.callback.replayed")]
    public const string CallbackReplayed = "CALLBACK_REPLAYED";

    [ErrorCode(412, "error.concurrency.precondition_failed")]
    public const string PreconditionFailed = "PRECONDITION_FAILED";

    [ErrorCode(428, "error.concurrency.precondition_required")]
    public const string PreconditionRequired = "PRECONDITION_REQUIRED";

    [ErrorCode(409, "error.concurrency.conflict", retryable: true)]
    public const string ConcurrencyConflict = "CONCURRENCY_CONFLICT";

    [ErrorCode(409, "error.data.duplicate_record")]
    public const string DuplicateRecord = "DUPLICATE_RECORD";

    [ErrorCode(409, "error.data.database_rule_violated")]
    public const string DatabaseRuleViolated = "DATABASE_RULE_VIOLATED";

    [ErrorCode(422, "error.idempotency.key_required")]
    public const string IdempotencyKeyRequired = "IDEMPOTENCY_KEY_REQUIRED";

    [ErrorCode(422, "error.idempotency.key_reused")]
    public const string IdempotencyKeyReused = "IDEMPOTENCY_KEY_REUSED";

    [ErrorCode(409, "error.idempotency.in_progress", retryable: true)]
    public const string IdempotencyInProgress = "IDEMPOTENCY_IN_PROGRESS";

    [ErrorCode(422, "error.money.currency_mismatch")]
    public const string CurrencyMismatch = "CURRENCY_MISMATCH";

    [ErrorCode(403, "error.approval.maker_is_checker")]
    public const string MakerCheckerSamePerson = "MAKER_CHECKER_SAME_PERSON";

    [ErrorCode(429, "error.request.rate_limited", retryable: true)]
    public const string RateLimited = "RATE_LIMITED";

    [ErrorCode(503, "error.service.not_ready", retryable: true)]
    public const string NotReady = "NOT_READY";

    /// <summary>No database connection was free in time, or the database refused more (GAP-039). Retry after Retry-After.</summary>
    [ErrorCode(503, "error.service.database_busy", retryable: true)]
    public const string DatabaseBusy = "DATABASE_BUSY";

    [ErrorCode(500, "error.internal", retryable: true)]
    public const string InternalError = "INTERNAL_ERROR";

    /// <summary>Reads every [ErrorCode] constant in the given types.</summary>
    public static IReadOnlyList<ErrorCodeEntry> Describe(IEnumerable<Type> codeClasses) =>
        codeClasses
            .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Static))
            .Where(f => f.IsLiteral && f.GetCustomAttribute<ErrorCodeAttribute>() is not null)
            .Select(f =>
            {
                var attribute = f.GetCustomAttribute<ErrorCodeAttribute>()!;
                var code = (string)f.GetRawConstantValue()!;
                return code.Length > 30
                    ? throw new InvalidOperationException($"Error code {code} is longer than 30 characters.")
                    : new ErrorCodeEntry(code, attribute.HttpStatus, attribute.MessageKey, attribute.Retryable, $"{f.DeclaringType!.Name}.{f.Name}");
            })
            .OrderBy(e => e.Code, StringComparer.Ordinal)
            .ToList();
}
