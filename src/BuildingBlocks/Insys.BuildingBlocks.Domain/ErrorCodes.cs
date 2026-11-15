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

    /// <summary>What the refusal means, in plain words a user acts on (AI Assist's refusal explanations, AIA-031). Never names a person or a record.</summary>
    public string? Means { get; init; }

    /// <summary>What to do next (AIA-031): who can help, or what to change. Empty when nothing the user can do would help.</summary>
    public string? Remedy { get; init; }
}

/// <summary>One catalogue entry, read from an <see cref="ErrorCodeAttribute"/>.</summary>
/// <param name="Means">The plain meaning, and <paramref name="Remedy"/> the next step, where the code's wording is written (AIA-031); null otherwise.</param>
public sealed record ErrorCodeEntry(string Code, int HttpStatus, string MessageKey, bool Retryable, string Description, string? Means = null, string? Remedy = null);

/// <summary>Platform-wide error codes (max 30 characters, the size of error_code).</summary>
public static class ErrorCodes
{
    [ErrorCode(422, "error.request.validation_failed", Means = "The request is missing a required value, or a value isn't in the form or range the record allows.",
        Remedy = "Correct the fields named in the field errors and send it again.")]
    public const string ValidationFailed = "VALIDATION_FAILED";

    /// <summary>
    /// A step a due diligence gate covers (addenda/due-diligence.md §5): the party is not cleared for it. The problem's
    /// outstanding extension names what is missing, never a hit or a list. Staff only; a portal user gets PORTAL_UNDER_REVIEW.
    /// </summary>
    [ErrorCode(422, "error.due_diligence.not_cleared", Means = "The party hasn't been cleared by customer due diligence for this step, so INSYS won't go on with it.",
        Remedy = "Ask a compliance officer to complete the outstanding due diligence items for the party; the step can then be done.")]
    public const string DueDiligenceNotCleared = "DUE_DILIGENCE_NOT_CLEARED";

    /// <summary>A portal user's step waits on a review of their details (due diligence §6): neutral, it says nothing of why.</summary>
    [ErrorCode(422, "error.portal.under_review", Means = "The step waits on a review of the customer's details.",
        Remedy = "Nothing to do now; the step becomes available once the review is finished.")]
    public const string PortalUnderReview = "PORTAL_UNDER_REVIEW";

    [ErrorCode(409, "error.state.invalid_transition", Means = "The record isn't in a state that allows this action: it has already moved on, or hasn't reached the step yet.",
        Remedy = "Open the record again to see its current status, and take the action that status allows.")]
    public const string InvalidStateTransition = "INVALID_STATE_TRANSITION";

    [ErrorCode(404, "error.resource.not_found", Means = "The record wasn't found, or you aren't allowed to see it.",
        Remedy = "Check the reference. If you should be able to see it, ask your administrator about your access.")]
    public const string NotFound = "NOT_FOUND";

    [ErrorCode(403, "error.access.forbidden", Means = "You aren't allowed to do this.",
        Remedy = "Ask your administrator whether your role should allow it.")]
    public const string Forbidden = "FORBIDDEN";

    /// <summary>ADR-026 §2: the user's roles in effect do not grant the operation's function.</summary>
    [ErrorCode(403, "error.access.function_not_granted", Means = "None of your roles in force grants this action.",
        Remedy = "Ask your tenant administrator for a role that includes it, or ask a colleague who holds it.")]
    public const string FunctionNotGranted = "FUNCTION_NOT_GRANTED";

    /// <summary>ADR-026 §2: the function is not available on the platform, or the tenant has disabled it.</summary>
    [ErrorCode(403, "error.access.function_disabled", Means = "This action is switched off for your organisation, or isn't available on the platform.",
        Remedy = "Ask your tenant administrator whether it should be switched on.")]
    public const string FunctionDisabled = "FUNCTION_DISABLED";

    /// <summary>ADR-026 §2: the function needs a feature the tenant is not entitled to (OQ-091).</summary>
    [ErrorCode(403, "error.access.feature_not_entitled", Means = "Your organisation isn't entitled to the feature this action belongs to.",
        Remedy = "Ask your tenant administrator; the entitlement is set by the platform operator.")]
    public const string FeatureNotEntitled = "FEATURE_NOT_ENTITLED";

    /// <summary>F103: an action on a record outside the caller's product or organisation scope for the permission.</summary>
    [ErrorCode(403, "error.access.out_of_scope", Means = "The record is outside the products or organisation units your role covers.",
        Remedy = "Ask a colleague whose scope covers it, or ask your administrator to widen your scope.")]
    public const string OutOfScope = "OUT_OF_SCOPE";

    /// <summary>F005: the request's client is on no channel in force in the tenant, or the channel does not enable the transaction.</summary>
    [ErrorCode(403, "error.access.channel_not_enabled", Means = "The application you're using isn't on a channel your organisation has enabled for this.",
        Remedy = "Use the application your organisation has enabled for it, or ask your administrator to enable the channel.")]
    public const string ChannelNotEnabled = "CHANNEL_NOT_ENABLED";

    /// <summary>F005: the caller's roles may not make this transaction, for this product, on this channel.</summary>
    [ErrorCode(403, "error.access.channel_not_allowed", Means = "Your roles may not make this transaction, for this product, on this channel.",
        Remedy = "Ask your administrator whether the channel's permissions should allow it, or use another channel that does.")]
    public const string ChannelNotAllowed = "CHANNEL_NOT_ALLOWED";

    /// <summary>F005: the channel requires approval for this transaction, which has no approval flow.</summary>
    [ErrorCode(403, "error.access.channel_approval_required", Means = "The channel requires an approval for this transaction, and the transaction has no approval step.",
        Remedy = "Make the transaction on a channel that doesn't require approval, or ask your administrator to change the channel's setting.")]
    public const string ChannelApprovalRequired = "CHANNEL_APPROVAL_REQUIRED";

    /// <summary>External-user access §5: an external user may use only the endpoints of their portal.</summary>
    [ErrorCode(403, "error.access.external_user_not_allowed", Means = "Portal users can use only their own portal's functions.",
        Remedy = "Use the portal for this, or contact the insurer.")]
    public const string ExternalUserNotAllowed = "EXTERNAL_USER_NOT_ALLOWED";

    /// <summary>
    /// BR-018: the token is for a user of the tenant whose identity is not linked yet (its Keycloak user is being created).
    /// Retryable: the response carries Retry-After, and the same call works once the identity is linked.
    /// </summary>
    [ErrorCode(403, "error.access.identity_not_linked", Means = "Your sign-in identity is still being set up.",
        Remedy = "Wait a moment and try again; it works once the set-up has finished.")]
    public const string IdentityNotLinked = "IDENTITY_NOT_LINKED";

    /// <summary>External-user access §6: the portal requires MFA, and the token shows none.</summary>
    [ErrorCode(403, "error.access.portal_mfa_required", Means = "This portal needs a second factor, and your sign-in didn't include one.",
        Remedy = "Sign in again and complete the one-time code step.")]
    public const string PortalMfaRequired = "PORTAL_MFA_REQUIRED";

    /// <summary>A business command in a tenant still Onboarding: only identity, static data and configuration are open until it is activated.</summary>
    [ErrorCode(409, "error.access.tenant_not_active", Means = "Your organisation is still being set up, so business actions aren't open yet.",
        Remedy = "Only set-up and configuration work is possible until the organisation is activated.")]
    public const string TenantNotActive = "TENANT_NOT_ACTIVE";

    /// <summary>ADR-024: a business module's endpoint was called in the platform operator tenant.</summary>
    [ErrorCode(403, "error.access.operator_tenant_not_business", Means = "The platform operator's organisation holds no business records, so this action isn't available there.",
        Remedy = "Use an insurer's tenant for business work.")]
    public const string OperatorTenantNotBusiness = "OPERATOR_TENANT_NOT_BUSINESS";

    [ErrorCode(403, "error.access.tenant_context_missing", Means = "The request didn't say which organisation it's for.",
        Remedy = "Sign in again; if it persists, report it to support with the reference.")]
    public const string TenantContextMissing = "TENANT_CONTEXT_MISSING";

    [ErrorCode(401, "error.access.unauthenticated", Means = "You aren't signed in, or your sign-in has expired.",
        Remedy = "Sign in again.")]
    public const string Unauthenticated = "UNAUTHENTICATED";

    /// <summary>ADR-023: a provider callback whose key, signature, timestamp or client token is not valid; one answer for all of them.</summary>
    [ErrorCode(401, "error.callback.signature", Means = "A provider's callback wasn't accepted, as its signature, key, timestamp or token isn't valid.",
        Remedy = "Check the provider's endpoint set-up with your integration administrator.")]
    public const string CallbackSignatureInvalid = "CALLBACK_SIGNATURE_INVALID";

    /// <summary>ADR-023: a callback ID the endpoint already received, inside the replay window.</summary>
    [ErrorCode(409, "error.callback.replayed", Means = "This callback was already received.",
        Remedy = "Nothing to do: the first one was recorded.")]
    public const string CallbackReplayed = "CALLBACK_REPLAYED";

    [ErrorCode(412, "error.concurrency.precondition_failed", Means = "The record changed after you opened it, so your change wasn't saved.",
        Remedy = "Open the record again to see the latest version, then make your change on it.")]
    public const string PreconditionFailed = "PRECONDITION_FAILED";

    [ErrorCode(428, "error.concurrency.precondition_required", Means = "The change needs the version of the record it's made on.",
        Remedy = "Open the record again and make the change from there.")]
    public const string PreconditionRequired = "PRECONDITION_REQUIRED";

    [ErrorCode(409, "error.concurrency.conflict", retryable: true, Means = "Someone else changed the record at the same moment.",
        Remedy = "Try again; if it repeats, open the record again first.")]
    public const string ConcurrencyConflict = "CONCURRENCY_CONFLICT";

    [ErrorCode(409, "error.data.duplicate_record", Means = "A record with the same identifying values already exists.",
        Remedy = "Look for the existing record and work on it, or change the identifying values.")]
    public const string DuplicateRecord = "DUPLICATE_RECORD";

    [ErrorCode(409, "error.data.database_rule_violated", Means = "The change breaks a rule the records must always keep.",
        Remedy = "Check the values against the related records; if they look right, report it to support with the reference.")]
    public const string DatabaseRuleViolated = "DATABASE_RULE_VIOLATED";

    [ErrorCode(422, "error.idempotency.key_required", Means = "The request was sent without the key that stops it being made twice.",
        Remedy = "Send it again from the application; this is a technical step it should add.")]
    public const string IdempotencyKeyRequired = "IDEMPOTENCY_KEY_REQUIRED";

    [ErrorCode(422, "error.idempotency.key_reused", Means = "The request's key was already used for a different request.",
        Remedy = "Start the action again, so a new key is used.")]
    public const string IdempotencyKeyReused = "IDEMPOTENCY_KEY_REUSED";

    [ErrorCode(409, "error.idempotency.in_progress", retryable: true, Means = "The same request is still being processed.",
        Remedy = "Wait a moment, then check the record before trying again.")]
    public const string IdempotencyInProgress = "IDEMPOTENCY_IN_PROGRESS";

    /// <summary>A repeat of a request whose response showed a secret once: the secret is not kept, so the response cannot be repeated.</summary>
    [ErrorCode(409, "error.idempotency.response_not_kept", Means = "A repeat of a request whose answer showed a secret once: the secret isn't kept, so it can't be shown again.",
        Remedy = "If the secret was lost, issue a new one.")]
    public const string IdempotencyResponseNotKept = "IDEMPOTENCY_RESPONSE_NOT_KEPT";

    /// <summary>BR-124: a claim's event date (Health: date of service) is after the tenant's business date (ADR-025).</summary>
    [ErrorCode(422, "error.claims.event_date_in_future", Means = "The claim's event date is after today's business date.",
        Remedy = "Correct the event date.")]
    public const string ClaimEventDateInFuture = "CLAIM_EVENT_DATE_IN_FUTURE";

    [ErrorCode(422, "error.money.currency_mismatch", Means = "The amounts are in different currencies, which this action doesn't allow.",
        Remedy = "Use amounts in the record's currency.")]
    public const string CurrencyMismatch = "CURRENCY_MISMATCH";

    /// <summary>A mandatory disclosure hasn't been acknowledged at this point of the process (F060, OQ-157).</summary>
    [ErrorCode(409, "error.disclosure.not_acknowledged", Means = "A mandatory notice hasn't been acknowledged at this point of the process.",
        Remedy = "Record the customer's acknowledgement of the notice, then try again.")]
    public const string DisclosureRequired = "DISCLOSURE_REQUIRED";

    [ErrorCode(403, "error.approval.maker_is_checker", Means = "You made or requested this, and the same person can't also approve it.",
        Remedy = "Ask another person with approval authority to decide it.")]
    public const string MakerCheckerSamePerson = "MAKER_CHECKER_SAME_PERSON";

    [ErrorCode(429, "error.request.rate_limited", retryable: true, Means = "Too many requests in a short time.",
        Remedy = "Wait a moment and try again.")]
    public const string RateLimited = "RATE_LIMITED";

    [ErrorCode(503, "error.service.not_ready", retryable: true, Means = "The service is starting up.",
        Remedy = "Try again shortly.")]
    public const string NotReady = "NOT_READY";

    /// <summary>No database connection was free in time, or the database refused more (GAP-039). Retry after Retry-After.</summary>
    [ErrorCode(503, "error.service.database_busy", retryable: true, Means = "The service is too busy to take the request just now.",
        Remedy = "Try again shortly.")]
    public const string DatabaseBusy = "DATABASE_BUSY";

    [ErrorCode(500, "error.internal", retryable: true, Means = "Something went wrong inside INSYS, not with what you entered.",
        Remedy = "Try again; if it repeats, report it to support with the reference.")]
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
                    : new ErrorCodeEntry(code, attribute.HttpStatus, attribute.MessageKey, attribute.Retryable, $"{f.DeclaringType!.Name}.{f.Name}", attribute.Means, attribute.Remedy);
            })
            .OrderBy(e => e.Code, StringComparer.Ordinal)
            .ToList();
}

/// <summary>The refusal when a step's mandatory disclosures aren't acknowledged (F060): it names the point and the notices.</summary>
public static class DisclosureRefusal
{
    public static Error For(string displayPoint, IReadOnlyList<string> noticeTypes) =>
        new(ErrorCodes.DisclosureRequired, $"The {displayPoint} disclosures aren't acknowledged yet.", ErrorKind.Conflict)
        {
            Extensions = new Dictionary<string, object?> { ["displayPoint"] = displayPoint, ["noticeTypes"] = noticeTypes },
        };
}
