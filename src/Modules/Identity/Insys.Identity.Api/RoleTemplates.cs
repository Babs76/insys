/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Identity.Api
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
using Insys.BuildingBlocks.Api.Functions;
using Insys.BuildingBlocks.Domain;

namespace Insys.Identity.Api;

/// <summary>
/// A default role template (OQ-111): the sub-domains it covers (every screen and action in them for its actor type, less
/// JOB functions, privileged functions and named exclusions), any further functions, and the privileged functions it is
/// meant to hold, named one by one. An action brings its screen. Not seeded: templates are a tenant's starting points.
/// </summary>
public sealed record RoleTemplate(string Code, string Name, string ActorType, string For, string[] SubDomains, string[]? Excluded = null,
    string[]? Extra = null, string[]? PrivilegedGrants = null, string[]? Approves = null, bool ScreensOnly = false, string? Status = null);

/// <summary>A segregation-of-duties rule: no role (and so no template) holds both functions.</summary>
public sealed record SodRule(string A, string B, string Why);

/// <summary>A function proposed as privileged in the review of the catalogue's flag, with why.</summary>
public sealed record PrivilegedProposal(string Function, string Why, bool Flagged = false);

/// <summary>
/// The default role templates and the review of the privileged flag, in one place (OQ-111): the API publishes them
/// (BR-088, <c>GET /identity/role-templates</c>), the addendum (docs/specs/addenda/role-templates.md) is generated from
/// them, and RoleTemplateTests checks every template against every segregation-of-duties rule and the privileged list.
/// </summary>
public static class RoleTemplates
{
    public static readonly IReadOnlyList<SodRule> SodRules =
    [
        new("CLAIMS_DRAFT_INSTRUCTION", "CLAIMS_RELEASE_INSTRUCTION", "Who prepares a claim payment does not release it"),
        new("PAYMENTS_BUILD_BATCH", "PAYMENTS_RELEASE_BATCH", "Who builds a payout batch does not release it"),
        new("PARTY_ADD_BANK_ACCOUNT", "PARTY_BANK_ACCOUNT_VERIFY", "Who adds a payee account does not verify it"),
    ];

    /// <summary>
    /// The privileged functions: two flagged before the review, TERMINATE_TENANT flagged now (decided), and the rest
    /// proposed for sign-off. Flagging one makes assigning a role that holds it a PRIVILEGED_ROLE approval (IAM-004).
    /// Templates treat every one as privileged already, so none is in an everyday template.
    /// </summary>
    public static readonly IReadOnlyList<PrivilegedProposal> Privileged =
    [
        new("TENANT_SWITCH_OFF_MONEY_JOBS", "Stops a tenant's billing and payment jobs (OQ-098)", Flagged: true),
        new("STATIC_DATA_CHANGE_TIER2_MAKER_CHECKER", "Changes which static data changes need a second person", Flagged: true),
        new("TERMINATE_TENANT", "Ends a tenant for good: its users lose access and its data enters retention", Flagged: true),
        new("CASE_DECIDE_APPEAL", "Decides an appeal against a case's decision (OQ-109)", Flagged: true),
        new("CASE_RECORD_REDRESS", "Records compensation or goodwill to be paid on a case (OQ-109)", Flagged: true),
        new("CASE_REOPEN", "Reopens a closed case, a complaint among them (OQ-109)", Flagged: true),
        new("CASE_PLACE_LEGAL_HOLD", "Keeps a case past its retention (legal hold)", Flagged: true),
        new("CASE_RELEASE_LEGAL_HOLD", "Lets a held case be disposed of when its retention ends", Flagged: true),
        new("SUSPEND_TENANT", "Stops every user of a tenant from working", Flagged: true),
        new("CHANGE_ISOLATION_PROFILE", "Changes where a tenant's data is held and how it is separated", Flagged: true),
        new("ASSIGN_ROLE", "Grants a user functions; the way to every other privilege", Flagged: true),
        new("REVOKE_ROLE", "Takes functions away; misuse locks people out of their work", Flagged: true),
        new("SD_IDENTITY_AND_ACCESS_MANAGEMENT_MAINTAIN", "Maintains roles and their function grants (iam.cfg_role_function_assignments)", Flagged: true),
        new("SD_IDENTITY_AND_ACCESS_MANAGEMENT_APPROVE", "Approves changes to roles and their function grants", Flagged: true),
        new("PARTY_ANONYMISE", "Irreversibly removes a person's identity (POPIA erasure)", Flagged: true),
        new("PARTY_MERGE_PARTIES", "Joins two people's records; a wrong merge exposes one person's data to another", Flagged: true),
        new("PARTY_PLACE_SEARCH_LOG_HOLD", "Keeps the party search log past its retention (legal hold)", Flagged: true),
        new("PARTY_RELEASE_SEARCH_LOG_HOLD", "Lets held search log entries be disposed of", Flagged: true),
        new("DOCUMENTS_DISPOSE", "Destroys a document", Flagged: true),
        new("DOCUMENTS_PLACE_LEGAL_HOLD", "Keeps documents past their retention (legal hold)", Flagged: true),
        new("DOCUMENTS_RELEASE_LEGAL_HOLD", "Lets held documents be disposed of", Flagged: true),
        new("DISCARD_PARKED_DELIVERY", "Drops an event a module never applied: the record stays as it was", Flagged: true),
        new("DISCARD_MESSAGE", "Drops a dead-lettered message for good", Flagged: true),
        new("PAYMENTS_ISSUE_CALLBACK_ENDPOINT", "Issues a signing secret a payment provider uses to report payments", Flagged: true),
        new("PAYMENTS_REVOKE_CALLBACK_ENDPOINT", "Stops a payment provider's reports being accepted", Flagged: true),
        new("COMMUNICATIONS_ISSUE_CALLBACK_ENDPOINT", "Issues a signing secret a messaging provider uses to report deliveries", Flagged: true),
        new("COMMUNICATIONS_REVOKE_CALLBACK_ENDPOINT", "Stops a messaging provider's reports being accepted", Flagged: true),
        new("PAYMENTS_REVERSE", "Reverses a payment already made", Flagged: true),
    ];

    private static readonly string[] Approving = ["WORKFLOW_APPROVALS", "WORKFLOW_APPROVE", "WORKFLOW_REJECT"];

    /// <summary>Screens with medical information (HLT-008) and the party search log: the Health auditor's, not the auditor's (proposed).</summary>
    public static readonly string[] SensitiveReadScreens = ["HEALTH_CLAIMS", "HEALTH_PREAUTHORISATIONS", "HEALTH_FRAUD_CASES", "HEALTH_UTILISATION_CASES", "PARTY_SEARCH_LOG"];

    /// <summary>The permission that reads audit events, the party search log among them.</summary>
    public const string AuditRead = "audit_event.read";

    /// <summary>
    /// The audit roles: only they hold a function that needs audit read (decided 25 October 2026). The party search log is
    /// "for audit roles only"; the general auditor reads the audit trail but not the search log (HLT-008 decision).
    /// </summary>
    public static readonly IReadOnlySet<string> AuditRoles =
        new HashSet<string>(["AUDITOR", "HEALTH_AUDITOR", "COMPLIANCE_OFFICER", "DATA_PROTECTION_OFFICER"], StringComparer.Ordinal);

    private static readonly string[] CallbackScreens = ["PAYMENT_CALLBACK_ENDPOINTS", "COMMUNICATION_CALLBACK_ENDPOINTS"];

    private static readonly string[] Configuration =
    [
        "CONFIG_ANALYTICS", "CONFIG_AUDIT", "CONFIG_BILLING", "CONFIG_CLAIMS_FRAMEWORK", "CONFIG_COMMUNICATIONS", "CONFIG_COMPLIANCE", "CONFIG_DISTRIBUTION",
        "CONFIG_DOCUMENTS", "CONFIG_FINANCE", "CONFIG_GENERAL", "CONFIG_HEALTH", "CONFIG_IDENTITY", "CONFIG_INTEGRATION", "CONFIG_LIFE", "CONFIG_PARTY",
        "CONFIG_PAYMENTS", "CONFIG_PRODUCT_FACTORY", "CONFIG_RATING", "CONFIG_REINSURANCE", "CONFIG_RULES", "CONFIG_STATIC_DATA", "CONFIG_TENANCY", "CONFIG_WORKFLOW",
    ];

    public static readonly IReadOnlyList<RoleTemplate> All =
    [
        new("TENANT_ADMIN", "Tenant administrator", "STAFF", "Users, roles and their grants, access reviews, the tenant's static data governance and identity and tenant configuration",
            ["IDENTITY_ACCESS", "STATIC_DATA", "CONFIG_IDENTITY", "CONFIG_TENANCY", "CONFIG_STATIC_DATA", "CONFIG_WORKFLOW", "AUDIT"], Extra: Approving,
            PrivilegedGrants: ["ASSIGN_ROLE", "REVOKE_ROLE", "SD_IDENTITY_AND_ACCESS_MANAGEMENT_MAINTAIN", "SD_IDENTITY_AND_ACCESS_MANAGEMENT_APPROVE"],
            Approves: ["PRIVILEGED_ROLE", "EXTERNAL_SUBJECT_LINK", "STATIC_DATA_T1", "STATIC_DATA_T2"]),
        new("PRODUCT_MANAGER", "Product manager", "STAFF", "Products, rating and rules, and their configuration",
            ["PRODUCT_FACTORY", "RATING", "RULES", "CONFIG_PRODUCT_FACTORY", "CONFIG_RATING", "CONFIG_RULES"], Approves: ["PRODUCT_PUBLICATION"]),
        new("GENERAL_UNDERWRITER", "General underwriter", "STAFF", "Risk objects, quotes, policies, endorsements and renewals for General",
            ["GENERAL_RISK_OBJECTS", "GENERAL_QUOTATION", "GENERAL_POLICY_ADMIN", "PARTIES", "CONSENT_PREFERENCES", "DOCUMENTS"],
            Excluded: ["PARTY_BANK_ACCOUNT_VERIFY"], Approves: ["ENDORSEMENT"]),
        new("LIFE_UNDERWRITER", "Life underwriter", "STAFF", "Proposals, underwriting cases, policies, values and group schemes for Life",
            ["LIFE_NEW_BUSINESS", "LIFE_UNDERWRITING", "LIFE_POLICY_ADMIN", "LIFE_POLICY_VALUES", "LIFE_GROUP_SCHEMES", "PARTIES", "CONSENT_PREFERENCES", "DOCUMENTS"],
            Excluded: ["PARTY_BANK_ACCOUNT_VERIFY"], Approves: ["LIFE_UNDERWRITING"]),
        new("CLAIMS_HANDLER", "Claims handler", "STAFF", "Registers, assesses and reserves General and Life claims and recoveries, and drafts payment instructions (the maker)",
            ["CLAIM_REGISTER", "CLAIM_FINANCIALS", "GENERAL_CLAIMS", "GENERAL_RECOVERIES", "LIFE_CLAIMS", "PARTIES", "DOCUMENTS", "DISPUTES_APPEALS"],
            Excluded: ["CLAIMS_RELEASE_INSTRUCTION", "PARTY_BANK_ACCOUNT_VERIFY"]),
        new("CLAIMS_MANAGER", "Claims manager", "STAFF", "Releases payment instructions and approves settlements and Life claims within authority; does not draft instructions",
            ["CLAIM_REGISTER", "CLAIM_FINANCIALS", "GENERAL_CLAIMS", "GENERAL_RECOVERIES", "LIFE_CLAIMS", "PARTIES", "DOCUMENTS", "DISPUTES_APPEALS"], Extra: Approving,
            Excluded: ["CLAIMS_DRAFT_INSTRUCTION", "PARTY_BANK_ACCOUNT_VERIFY"], Approves: ["CLAIM_SETTLEMENT", "LIFE_CLAIM", "CLAIM_PAYMENT"]),
        new("HEALTH_CLAIMS_ASSESSOR", "Health claims assessor", "STAFF", "Medical claims, preauthorisations, fraud and utilisation cases",
            ["HEALTH_MEDICAL_CLAIMS", "HEALTH_ACCESS_TO_CARE", "HEALTH_FRAUD_UTILISATION", "PARTIES", "DOCUMENTS"], Excluded: ["PARTY_BANK_ACCOUNT_VERIFY"]),
        new("HEALTH_MEMBERSHIP_ADMINISTRATOR", "Health membership administrator", "STAFF", "Schemes, plans and memberships",
            ["HEALTH_SCHEMES_PLANS", "HEALTH_MEMBERSHIP", "PARTIES", "CONSENT_PREFERENCES", "DOCUMENTS"], Excluded: ["PARTY_BANK_ACCOUNT_VERIFY"]),
        new("FINANCE_OFFICER", "Finance officer", "STAFF", "Billing, journals and provider payment runs, and builds payout batches (the maker)",
            ["BILLING", "PAYMENTS", "FINANCE", "HEALTH_PROVIDER_PAYMENTS", "CONFIG_BILLING", "CONFIG_PAYMENTS", "CONFIG_FINANCE"],
            Excluded: ["PAYMENTS_RELEASE_BATCH", .. CallbackScreens]),
        new("FINANCE_MANAGER", "Finance manager", "STAFF", "Releases payout batches, verifies payee accounts and approves money movements within authority; does not build batches",
            ["BILLING", "PAYMENTS", "FINANCE", "HEALTH_PROVIDER_PAYMENTS", "CONFIG_BILLING", "CONFIG_PAYMENTS", "CONFIG_FINANCE"],
            Excluded: ["PAYMENTS_BUILD_BATCH", .. CallbackScreens], Extra: [.. Approving, "PARTY_BANK_ACCOUNTS", "PARTY_BANK_ACCOUNT_VERIFY"],
            PrivilegedGrants: ["TENANT_SWITCH_OFF_MONEY_JOBS", "PAYMENTS_REVERSE"],
            Approves: ["REFUND", "CLAIM_PAYMENT", "PAYOUT_RELEASE", "PERIOD_REOPEN", "PROVIDER_PAYMENT_RUN", "COMMISSION_STATEMENT", "BANK_ACCOUNT_VERIFICATION", "CASE_REDRESS",
                "UNKNOWN_PAYMENT_RESOLUTION"]),
        new("DISTRIBUTION_ADMINISTRATOR", "Distribution administrator", "STAFF", "Intermediaries, commission and their configuration",
            ["INTERMEDIARIES", "COMMISSION", "CONFIG_DISTRIBUTION"]),
        new("REINSURANCE_OFFICER", "Reinsurance officer", "STAFF", "Treaties, cessions and bordereaux", ["REINSURANCE", "CONFIG_REINSURANCE"]),
        new("COMPLIANCE_OFFICER", "Compliance officer", "STAFF",
            "Regulatory returns, risk limits, consent, the audit trail and the party search log; complaints, and the impartial review of appeals (PPR 18.6)",
            ["REGULATORY_REPORTING", "RISK_LIMITS", "CONSENT_PREFERENCES", "AUDIT", "CONFIG_COMPLIANCE", "CUSTOMER_SERVICE"], Extra: ["PARTY_SEARCH_LOG"],
            PrivilegedGrants: ["CASE_DECIDE_APPEAL"], Approves: ["LEGAL_HOLD_RELEASE"]),
        new("DATA_PROTECTION_OFFICER", "Data protection officer", "STAFF",
            "Anonymises and merges parties, disposes of documents, and places and releases legal holds on documents and the party search log",
            ["CONSENT_PREFERENCES", "AUDIT"], Extra: ["PARTY_SEARCH_LOG"],
            PrivilegedGrants: ["PARTY_ANONYMISE", "PARTY_MERGE_PARTIES", "DOCUMENTS_DISPOSE", "DOCUMENTS_PLACE_LEGAL_HOLD", "DOCUMENTS_RELEASE_LEGAL_HOLD",
                "PARTY_PLACE_SEARCH_LOG_HOLD", "PARTY_RELEASE_SEARCH_LOG_HOLD", "CASE_PLACE_LEGAL_HOLD", "CASE_RELEASE_LEGAL_HOLD"]),
        new("CUSTOMER_SERVICE_AGENT", "Customer service agent", "STAFF", "Looks up parties, policies and claims, and their notices and documents; handles enquiries and complaints",
            ["PARTIES", "CONSENT_PREFERENCES", "NOTIFICATIONS", "DOCUMENTS", "CUSTOMER_SERVICE"], Excluded: ["PARTY_BANK_ACCOUNT_VERIFY", .. CallbackScreens]),
        new("OPERATIONS_ANALYST", "Operations analyst", "STAFF", "Dashboards, parked deliveries and dead letters (retry and replay), webhooks and work queues; decides no approvals and discards nothing",
            ["ANALYTICS", "INTEGRATION_OPERATIONS", "WORKFLOW"], Excluded: ["WORKFLOW_APPROVE", "WORKFLOW_REJECT"]),
        new("INTEGRATION_ADMINISTRATOR", "Integration administrator", "STAFF",
            "Payment and messaging providers' callback endpoints and their secrets, parking settings, and discarding deliveries and messages",
            ["INTEGRATION_OPERATIONS", "CONFIG_INTEGRATION"], Extra: CallbackScreens,
            PrivilegedGrants: ["PAYMENTS_ISSUE_CALLBACK_ENDPOINT", "PAYMENTS_REVOKE_CALLBACK_ENDPOINT", "COMMUNICATIONS_ISSUE_CALLBACK_ENDPOINT",
                "COMMUNICATIONS_REVOKE_CALLBACK_ENDPOINT", "DISCARD_PARKED_DELIVERY", "DISCARD_MESSAGE"]),
        new("AUDITOR", "Auditor (read only)", "STAFF", "Every staff screen except medical information and the party search log; no actions", ["*"],
            Excluded: SensitiveReadScreens, ScreensOnly: true),
        new("HEALTH_AUDITOR", "Health auditor (read only)", "STAFF", "Medical information and the party search log, read only (HLT-008)",
            ["HEALTH_MEDICAL_CLAIMS", "HEALTH_ACCESS_TO_CARE", "HEALTH_FRAUD_UTILISATION", "AUDIT"], Extra: ["PARTY_SEARCH_LOG"], ScreensOnly: true),
        new("BROKER", "Broker (portal)", "BROKER", "The broker portal", ["BROKER_PORTAL"]),
        new("MEMBER", "Member (portal)", "CUSTOMER", "The customer portal", ["CUSTOMER_PORTAL"]),
        new("PROVIDER", "Provider (portal)", "PROVIDER", "The provider portal", ["PROVIDER_PORTAL"]),
        new("PLATFORM_ADMIN", "Platform administrator (operator tenant)", "PLATFORM", "Tenants and their lifecycle, first administrators, and the operator's own users",
            ["TENANTS", "IDENTITY_ACCESS", "AUDIT"], Extra: Approving,
            PrivilegedGrants: ["SUSPEND_TENANT", "TERMINATE_TENANT", "CHANGE_ISOLATION_PROFILE", "ASSIGN_ROLE", "REVOKE_ROLE"], Approves: ["TENANT_ADMINISTRATOR"]),
        new("PLATFORM_STATIC_DATA_STEWARD", "Platform static data steward (operator tenant)", "PLATFORM", "Global static data and every module's configuration baseline",
            ["STATIC_DATA", .. Configuration], Extra: Approving, Approves: ["STATIC_DATA_T1", "STATIC_DATA_T2"]),
    ];

    /// <summary>Every function code the catalogue treats, or this review proposes to treat, as privileged.</summary>
    public static IReadOnlySet<string> PrivilegedCodes(IEnumerable<CatalogueFunction> catalogue) =>
        catalogue.Where(f => f.Function.Privileged).Select(f => f.Function.Code).Concat(Privileged.Select(p => p.Function)).ToHashSet(StringComparer.Ordinal);

    /// <summary>The template's functions, from the catalogue as built.</summary>
    public static IReadOnlyList<FunctionDeclaration> Expand(RoleTemplate template, IReadOnlyList<CatalogueFunction> catalogue)
    {
        var privileged = PrivilegedCodes(catalogue);
        var byCode = catalogue.ToDictionary(f => f.Function.Code, f => f.Function, StringComparer.Ordinal);
        var excluded = (template.Excluded ?? []).ToHashSet(StringComparer.Ordinal);
        var auditRole = AuditRoles.Contains(template.Code);
        var codes = catalogue.Where(f => auditRole || !f.Permissions.Contains(AuditRead)).Select(f => f.Function)
            .Where(f => f.Kind != FunctionKind.Job && f.Audiences.Contains(template.ActorType)
                && (template.SubDomains.Contains("*") || template.SubDomains.Contains(f.SubDomain))
                && (!template.ScreensOnly || f.Kind == FunctionKind.Screen)
                && !privileged.Contains(f.Code) && !excluded.Contains(f.Code))
            .Select(f => f.Code)
            .Concat(template.Extra ?? []).Concat(template.PrivilegedGrants ?? [])
            .ToHashSet(StringComparer.Ordinal);

        // An action brings the screen it acts on.
        foreach (var code in codes.ToList())
        {
            if (byCode.TryGetValue(code, out var function) && function.Parent is { } parent)
            {
                codes.Add(parent);
            }
        }

        return [.. codes.Select(c => byCode.TryGetValue(c, out var f) ? f : throw new InvalidOperationException($"{template.Code} names {c}, which is not in the catalogue."))
            .OrderBy(f => f.Code, StringComparer.Ordinal)];
    }

    /// <summary>Every template as the API lists it (BR-088), in the order above.</summary>
    public static IReadOnlyList<RoleTemplateSummary> Summaries(IReadOnlyList<CatalogueFunction> catalogue) =>
        [.. All.Select(t => (Template: t, Functions: Expand(t, catalogue))).Select(x => new RoleTemplateSummary(x.Template.Code, x.Template.Name, x.Template.ActorType,
            x.Template.For, x.Functions.Count(f => f.Kind == FunctionKind.Screen), x.Functions.Count(f => f.Kind == FunctionKind.Action)))];

    /// <summary>
    /// One template with its functions as the API returns it (BR-088), or null when no template has the code. Read for a
    /// tenant, <paramref name="refusal"/> gives why the tenant cannot hold a function (FUNCTION_DISABLED,
    /// FEATURE_NOT_ENTITLED), or null when it can, and each function is marked available or not.
    /// </summary>
    public static RoleTemplateView? View(string code, IReadOnlyList<CatalogueFunction> catalogue, Guid? tenantId = null, Func<FunctionDeclaration, string?>? refusal = null) =>
        All.FirstOrDefault(t => t.Code == code) is not { } t
            ? null
            : new RoleTemplateView(t.Code, t.Name, t.ActorType, t.For, t.SubDomains, t.PrivilegedGrants ?? [], t.Approves ?? [], tenantId,
                [.. Expand(t, catalogue).Select(f => (Function: f, Refusal: refusal?.Invoke(f))).Select(x => new RoleTemplateFunction(x.Function.Code,
                    x.Function.Kind == FunctionKind.Screen ? "SCREEN" : "ACTION", x.Function.SubDomain, x.Function.Parent, x.Function.Audiences, x.Function.Privileged,
                    x.Function.Feature, refusal is null ? null : x.Refusal is null, x.Refusal))]);
}

/// <summary>A default role template in the list (BR-088): a starting point for a tenant's own role, never seeded.</summary>
/// <param name="ActorType">Who the role is for: STAFF, PLATFORM (the operator tenant) or a portal's BROKER, CUSTOMER or PROVIDER.</param>
/// <param name="For">What the role does, in a sentence.</param>
public sealed record RoleTemplateSummary(
    string Code, string Name, [property: OneOf("STAFF", "PLATFORM", "BROKER", "CUSTOMER", "PROVIDER")] string ActorType, string For, int Screens, int Actions);

/// <summary>
/// A default role template with its functions (BR-088), expanded from the function catalogue as built. A tenant's copy
/// becomes its own role and function grants, approved like any Tier 1 change; the first-administrator request names
/// TENANT_ADMIN's functions.
/// </summary>
/// <param name="SubDomains">The sub-domains whose functions it covers; <c>*</c> for every staff screen.</param>
/// <param name="PrivilegedGrants">The privileged functions it is meant to hold, named one by one.</param>
/// <param name="Approves">The approval types its authority rows are expected to cover.</param>
/// <param name="TenantId">The tenant the functions' availability is for (the <c>tenantId</c> asked for); null when none was.</param>
public sealed record RoleTemplateView(
    string Code, string Name, [property: OneOf("STAFF", "PLATFORM", "BROKER", "CUSTOMER", "PROVIDER")] string ActorType, string For,
    IReadOnlyList<string> SubDomains, IReadOnlyList<string> PrivilegedGrants, IReadOnlyList<string> Approves, Guid? TenantId,
    IReadOnlyList<RoleTemplateFunction> Functions);

/// <summary>A function of a role template, as the catalogue declares it.</summary>
/// <param name="Parent">The screen an action acts on; null for a screen.</param>
/// <param name="Audiences">The actor types the function is for.</param>
/// <param name="Privileged">Whether assigning a role that holds it needs a PRIVILEGED_ROLE approval (IAM-004).</param>
/// <param name="Feature">The feature a tenant must be entitled to for it (OQ-091); null for the base platform.</param>
/// <param name="Available">
/// Read for a tenant: whether the tenant can hold it now (it has not disabled it and is entitled to its feature), so a
/// console prefills only these. Null when no tenant was asked for.
/// </param>
/// <param name="UnavailableReason">Why it is not available: the refusal a user holding it would get.</param>
public sealed record RoleTemplateFunction(
    string Code, [property: OneOf("SCREEN", "ACTION")] string Kind, string SubDomain, string? Parent, IReadOnlyList<string> Audiences, bool Privileged,
    [property: OneOf("LIFE", "GENERAL", "HEALTH", "REINSURANCE", "BROKER_PORTAL", "PROVIDER_PORTAL", "CUSTOMER_PORTAL")] string? Feature, bool? Available,
    [property: OneOf("FUNCTION_DISABLED", "FEATURE_NOT_ENTITLED")] string? UnavailableReason);
