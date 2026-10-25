/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Testing
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
using System.Text;
using Insys.BuildingBlocks.Api.Functions;

namespace Insys.Testing;

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
/// The default role templates and the review of the privileged flag, in one place: the addendum
/// (docs/specs/addenda/role-templates.md) is generated from them, and RoleTemplateTests checks every template against
/// every segregation-of-duties rule and the privileged list. Regenerate with INSYS_UPDATE_SNAPSHOTS=1.
/// </summary>
public static class RoleTemplates
{
    public static string FilePath => Path.Combine(RepositoryPaths.Root, "docs", "specs", "addenda", "role-templates.md");

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
        new("SUSPEND_TENANT", "Stops every user of a tenant from working"),
        new("CHANGE_ISOLATION_PROFILE", "Changes where a tenant's data is held and how it is separated"),
        new("ASSIGN_ROLE", "Grants a user functions; the way to every other privilege"),
        new("REVOKE_ROLE", "Takes functions away; misuse locks people out of their work"),
        new("SD_IDENTITY_AND_ACCESS_MANAGEMENT_MAINTAIN", "Maintains roles and their function grants (iam.cfg_role_function_assignments)"),
        new("SD_IDENTITY_AND_ACCESS_MANAGEMENT_APPROVE", "Approves changes to roles and their function grants"),
        new("PARTY_ANONYMISE", "Irreversibly removes a person's identity (POPIA erasure)"),
        new("PARTY_MERGE_PARTIES", "Joins two people's records; a wrong merge exposes one person's data to another"),
        new("PARTY_PLACE_SEARCH_LOG_HOLD", "Keeps the party search log past its retention (legal hold)"),
        new("PARTY_RELEASE_SEARCH_LOG_HOLD", "Lets held search log entries be disposed of"),
        new("DOCUMENTS_DISPOSE", "Destroys a document"),
        new("DOCUMENTS_PLACE_LEGAL_HOLD", "Keeps documents past their retention (legal hold)"),
        new("DOCUMENTS_RELEASE_LEGAL_HOLD", "Lets held documents be disposed of"),
        new("DISCARD_PARKED_DELIVERY", "Drops an event a module never applied: the record stays as it was"),
        new("DISCARD_MESSAGE", "Drops a dead-lettered message for good"),
        new("PAYMENTS_ISSUE_CALLBACK_ENDPOINT", "Issues a signing secret a payment provider uses to report payments"),
        new("PAYMENTS_REVOKE_CALLBACK_ENDPOINT", "Stops a payment provider's reports being accepted"),
        new("COMMUNICATIONS_ISSUE_CALLBACK_ENDPOINT", "Issues a signing secret a messaging provider uses to report deliveries"),
        new("COMMUNICATIONS_REVOKE_CALLBACK_ENDPOINT", "Stops a messaging provider's reports being accepted"),
        new("PAYMENTS_REVERSE", "Reverses a payment already made"),
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
            ["CLAIM_REGISTER", "CLAIM_FINANCIALS", "GENERAL_CLAIMS", "GENERAL_RECOVERIES", "LIFE_CLAIMS", "PARTIES", "DOCUMENTS"],
            Excluded: ["CLAIMS_RELEASE_INSTRUCTION", "PARTY_BANK_ACCOUNT_VERIFY"]),
        new("CLAIMS_MANAGER", "Claims manager", "STAFF", "Releases payment instructions and approves settlements and Life claims within authority; does not draft instructions",
            ["CLAIM_REGISTER", "CLAIM_FINANCIALS", "GENERAL_CLAIMS", "GENERAL_RECOVERIES", "LIFE_CLAIMS", "PARTIES", "DOCUMENTS"], Extra: Approving,
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
            Approves: ["REFUND", "CLAIM_PAYMENT", "PAYOUT_RELEASE", "PERIOD_REOPEN", "PROVIDER_PAYMENT_RUN", "COMMISSION_STATEMENT", "BANK_ACCOUNT_VERIFICATION"]),
        new("DISTRIBUTION_ADMINISTRATOR", "Distribution administrator", "STAFF", "Intermediaries, commission and their configuration",
            ["INTERMEDIARIES", "COMMISSION", "CONFIG_DISTRIBUTION"]),
        new("REINSURANCE_OFFICER", "Reinsurance officer", "STAFF", "Treaties, cessions and bordereaux", ["REINSURANCE", "CONFIG_REINSURANCE"]),
        new("COMPLIANCE_OFFICER", "Compliance officer", "STAFF", "Regulatory returns, risk limits, consent, the audit trail and the party search log",
            ["REGULATORY_REPORTING", "RISK_LIMITS", "CONSENT_PREFERENCES", "AUDIT", "CONFIG_COMPLIANCE"], Extra: ["PARTY_SEARCH_LOG"]),
        new("DATA_PROTECTION_OFFICER", "Data protection officer", "STAFF",
            "Anonymises and merges parties, disposes of documents, and places and releases legal holds on documents and the party search log",
            ["CONSENT_PREFERENCES", "AUDIT"], Extra: ["PARTY_SEARCH_LOG"],
            PrivilegedGrants: ["PARTY_ANONYMISE", "PARTY_MERGE_PARTIES", "DOCUMENTS_DISPOSE", "DOCUMENTS_PLACE_LEGAL_HOLD", "DOCUMENTS_RELEASE_LEGAL_HOLD",
                "PARTY_PLACE_SEARCH_LOG_HOLD", "PARTY_RELEASE_SEARCH_LOG_HOLD"]),
        new("CUSTOMER_SERVICE_AGENT", "Customer service agent", "STAFF", "Looks up parties, policies and claims, and their notices and documents (cases join it in Wave 2)",
            ["PARTIES", "CONSENT_PREFERENCES", "NOTIFICATIONS", "DOCUMENTS"], Excluded: ["PARTY_BANK_ACCOUNT_VERIFY", .. CallbackScreens]),
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

    /// <summary>The addendum, generated from the templates and the catalogue as built.</summary>
    public static string Document(IReadOnlyList<CatalogueFunction> catalogue)
    {
        var s = new StringBuilder();
        s.AppendLine("# Addendum: default role templates (OQ-111)").AppendLine();
        s.AppendLine("**Signed off once its two fixes are made (25 October 2026, Ade): the search log only for audit roles (made), and a second person "
            + "for releasing a legal hold (proposed below). Not seeded.**").AppendLine();
        s.AppendLine("Generated by `tests/Insys.Testing/RoleTemplates.cs` from the function catalogue as built; "
            + "`RoleTemplateTests` checks every template against every segregation-of-duties rule and the privileged list. Optional starting points a tenant "
            + "copies and adjusts: copying a template creates the tenant's own role (`iam.cfg_roles_and_permission_sets`) and its function grants "
            + "(`iam.cfg_role_function_assignments`), which go through approval like any Tier 1 change. INSYS never changes a tenant's copy when a template changes.").AppendLine();
        s.AppendLine($"- **Basis:** ADR-026 (a role holds functions), the function catalogue as built ({catalogue.Count} functions), `INSYS_NAVIGATION.md` (sub-domains), "
            + "the approval types and authority (`frontend-integration-guide.md` §3).");
        s.AppendLine("- **How a template is made:** the sub-domains it covers, every screen and action in them for its actor type, less JOB functions, privileged "
            + "functions and named exclusions; then further functions, and the privileged functions it is meant to hold, named one by one. An action brings its "
            + "screen. A checker template adds the Workflow approvals screen with approve and reject; what it may approve, and up to what amount, is its authority rows.");
        s.AppendLine("- **Separation of duties:** makers and checkers are separate templates, and no template holds both functions of any rule below (tested).");
        s.AppendLine($"- **Audit read:** only the audit roles ({Codes(AuditRoles.Order(StringComparer.Ordinal))}) hold a function that needs `{AuditRead}`: the "
            + "audit trail, a record's audit history and the party search log (tested). The tenant and platform administrators no longer read the audit trail.");
        s.AppendLine("- **Revised (25 October 2026)** after the first draft was not signed off: the claims and finance templates no longer hold both sides of a payment; "
            + "only the finance manager verifies payee accounts; privileged functions moved to a data protection officer and an integration administrator; the "
            + "operations analyst decides no approvals and discards nothing; the platform administrator is split. Cases functions join after Wave 2 is built.");
        s.AppendLine("- **Revised again (25 October 2026):** every operational template held the party search log, pulled in from `PARTIES`; only audit roles "
            + "now hold a function that needs audit read. The Health auditor is approved.").AppendLine();

        s.AppendLine("## Templates").AppendLine();
        s.AppendLine("| Template | For | Sub-domains | Screens | Actions | Privileged it holds | Approves (authority rows) |");
        s.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var t in All)
        {
            var functions = Expand(t, catalogue);
            var name = t.Status is null ? $"**{t.Name}**" : $"**{t.Name}** ({t.Status})";
            s.AppendLine($"| {name} (`{t.Code}`, {t.ActorType}) | {t.For} | {(t.SubDomains.Contains("*") ? "every staff screen" : Codes(t.SubDomains))} | "
                + $"{functions.Count(f => f.Kind == FunctionKind.Screen)} | {functions.Count(f => f.Kind == FunctionKind.Action)} | "
                + $"{Codes(t.PrivilegedGrants)} | {Codes(t.Approves)} |");
        }

        s.AppendLine().AppendLine("## Segregation-of-duties rules").AppendLine();
        s.AppendLine("| Function A | Function B | Conflict | Why |").AppendLine("|---|---|---|---|");
        foreach (var rule in SodRules)
        {
            s.AppendLine($"| `{rule.A}` | `{rule.B}` | BLOCK | {rule.Why} |");
        }

        s.AppendLine().AppendLine("Approvals already need a second person (the checker is never the maker); these rules also keep one person from holding both sides of a payment.");
        s.AppendLine().AppendLine("## The privileged flag").AppendLine();
        s.AppendLine("Assigning a role that holds a privileged function is itself an approval (`PRIVILEGED_ROLE`). Two functions were flagged; `TERMINATE_TENANT` is "
            + "flagged as decided. **The other 20 are approved (25 October 2026, Ade) and are switched on in the same batch as the fix for front-end request "
            + "BR-013 (a new tenant needs at least two administrators), not before:** flagging `ASSIGN_ROLE` and the role-grant functions makes the tenant "
            + "administrator a privileged role, so a new tenant with one administrator could not assign anything. The templates already treat every one as "
            + "privileged: none is in an everyday template, and each is named where a template holds it.").AppendLine();
        s.AppendLine("| Function | Flag | Why | Held by |").AppendLine("|---|---|---|---|");
        foreach (var p in Privileged)
        {
            var holders = All.Where(t => (t.PrivilegedGrants ?? []).Contains(p.Function)).Select(t => t.Code).ToArray();
            s.AppendLine($"| `{p.Function}` | {(p.Flagged ? "Flagged" : "Proposed")} | {p.Why} | {(holders.Length == 0 ? "granted one by one" : Codes(holders))} |");
        }

        s.AppendLine().AppendLine("## Who reads medical information and the search log (HLT-008)").AppendLine();
        s.AppendLine("**Decided (25 October 2026, Ade): a separate Health auditor.** HLT-008 restricts medical information by role, purpose and minimum necessary "
            + "access, and the party search log shows who looked up whom. `AUDITOR` reads every staff screen except "
            + $"{Codes(SensitiveReadScreens)}; `HEALTH_AUDITOR` reads those with the audit trail.").AppendLine();
        s.AppendLine("## Proposed: a second person for releasing a legal hold").AppendLine();
        s.AppendLine("The data protection officer places and releases legal holds and disposes of documents, so one person could release a hold and destroy "
            + "what it protected. **Proposed: a new approval type, `LEGAL_HOLD_RELEASE`, not a segregation-of-duties rule.** Releasing a hold on documents "
            + "(`DOCUMENTS_RELEASE_LEGAL_HOLD`) or on the party search log (`PARTY_RELEASE_SEARCH_LOG_HOLD`) becomes a request that a second person approves in "
            + "Workflow; the hold stays in force until then, and a rejection, expiry or withdrawal leaves it in force (the approval endings rule). The checker "
            + "is whoever has `LEGAL_HOLD_RELEASE` authority, suggested the compliance officer.");
        s.AppendLine("- **Why not a rule:** a rule that no role holds both releasing and disposing still lets one person release a hold with no one checking, "
            + "and a tenant with one data protection officer could no longer release at all. An approval puts the second person on the release itself, "
            + "whoever later disposes.");
        s.AppendLine("- **To build, once confirmed:** the release request and its approval in Documents and Party (with the approval endings and D-5 handling, "
            + "and two rows in the approval endings table test), the approval kind, and the compliance officer approving it.").AppendLine();
        s.AppendLine("## Open points");
        s.AppendLine("- Confirm the legal hold release approval above.");
        s.AppendLine("- Deferred (25 October 2026): which templates OFT Insurance South Africa loads and their authority bands (Workstream F, OQ-112); "
            + "whether a tenant may hide templates it does not use; Keycloak step-up (`acr_values`) before the portals launch.").AppendLine();
        s.AppendLine("## Appendix: the functions of each template").AppendLine();
        foreach (var t in All)
        {
            var functions = Expand(t, catalogue);
            s.AppendLine($"### `{t.Code}` ({functions.Count})");
            s.AppendLine(string.Join(", ", functions.Select(f => $"`{f.Code}`"))).AppendLine();
        }

        return s.ToString().TrimEnd() + Environment.NewLine;
    }

    private static string Codes(IEnumerable<string>? codes) => codes?.Any() == true ? string.Join(", ", codes.Select(c => $"`{c}`")) : "—";
}
