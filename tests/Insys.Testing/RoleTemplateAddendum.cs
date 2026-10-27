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
using Insys.Identity.Api;

namespace Insys.Testing;

/// <summary>
/// The addendum (docs/specs/addenda/role-templates.md), generated from the role templates the API publishes
/// (<see cref="RoleTemplates"/>, BR-088) and the catalogue as built. Regenerate with INSYS_UPDATE_SNAPSHOTS=1.
/// </summary>
public static class RoleTemplateAddendum
{
    public static string FilePath => Path.Combine(RepositoryPaths.Root, "docs", "specs", "addenda", "role-templates.md");

    /// <summary>The addendum, generated from the templates and the catalogue as built.</summary>
    public static string Document(IReadOnlyList<CatalogueFunction> catalogue)
    {
        var s = new StringBuilder();
        s.AppendLine("# Addendum: default role templates (OQ-111)").AppendLine();
        s.AppendLine("**Signed off once its two fixes are made (25 October 2026, Ade): the search log only for audit roles (made), and a second person "
            + "for releasing a legal hold (proposed below). Not seeded.**").AppendLine();
        s.AppendLine("Generated from `src/Modules/Identity/Insys.Identity.Api/RoleTemplates.cs` (published at `GET /api/v1/identity/role-templates`, BR-088) from the function catalogue as built; "
            + "`RoleTemplateTests` checks every template against every segregation-of-duties rule and the privileged list. Optional starting points a tenant "
            + "copies and adjusts: copying a template creates the tenant's own role (`iam.cfg_roles_and_permission_sets`) and its function grants "
            + "(`iam.cfg_role_function_assignments`), which go through approval like any Tier 1 change. INSYS never changes a tenant's copy when a template changes.").AppendLine();
        s.AppendLine($"- **Basis:** ADR-026 (a role holds functions), the function catalogue as built ({catalogue.Count} functions), `INSYS_NAVIGATION.md` (sub-domains), "
            + "the approval types and authority (`frontend-integration-guide.md` §3).");
        s.AppendLine("- **How a template is made:** the sub-domains it covers, every screen and action in them for its actor type, less JOB functions, privileged "
            + "functions and named exclusions; then further functions, and the privileged functions it is meant to hold, named one by one. An action brings its "
            + "screen. A checker template adds the Workflow approvals screen with approve and reject; what it may approve, and up to what amount, is its authority rows.");
        s.AppendLine("- **Separation of duties:** makers and checkers are separate templates, and no template holds both functions of any rule below (tested).");
        s.AppendLine($"- **Audit read:** only the audit roles ({Codes(RoleTemplates.AuditRoles.Order(StringComparer.Ordinal))}) hold a function that needs `{RoleTemplates.AuditRead}`: the "
            + "audit trail, a record's audit history and the party search log (tested). The tenant and platform administrators no longer read the audit trail.");
        s.AppendLine("- **Revised (25 October 2026)** after the first draft was not signed off: the claims and finance templates no longer hold both sides of a payment; "
            + "only the finance manager verifies payee accounts; privileged functions moved to a data protection officer and an integration administrator; the "
            + "operations analyst decides no approvals and discards nothing; the platform administrator is split.");
        s.AppendLine("- **Revised again (25 October 2026):** every operational template held the party search log, pulled in from `PARTIES`; only audit roles "
            + "now hold a function that needs audit read. The Health auditor is approved. The Wave 2 case functions joined: complaints and enquiries for "
            + "the customer service agent and the compliance officer (who also decides appeals, PPR 18.6), disputes and appeals for the claims templates, "
            + "case legal holds for the data protection officer, case redress approved by the finance manager; recording redress and reopening a case are "
            + "granted one by one.").AppendLine();

        s.AppendLine("## Templates").AppendLine();
        s.AppendLine("| Template | For | Sub-domains | Screens | Actions | Privileged it holds | Approves (authority rows) |");
        s.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var t in RoleTemplates.All)
        {
            var functions = RoleTemplates.Expand(t, catalogue);
            var name = t.Status is null ? $"**{t.Name}**" : $"**{t.Name}** ({t.Status})";
            s.AppendLine($"| {name} (`{t.Code}`, {t.ActorType}) | {t.For} | {(t.SubDomains.Contains("*") ? "every staff screen" : Codes(t.SubDomains))} | "
                + $"{functions.Count(f => f.Kind == FunctionKind.Screen)} | {functions.Count(f => f.Kind == FunctionKind.Action)} | "
                + $"{Codes(t.PrivilegedGrants)} | {Codes(t.Approves)} |");
        }

        s.AppendLine().AppendLine("## Segregation-of-duties rules").AppendLine();
        s.AppendLine("| Function A | Function B | Conflict | Why |").AppendLine("|---|---|---|---|");
        foreach (var rule in RoleTemplates.SodRules)
        {
            s.AppendLine($"| `{rule.A}` | `{rule.B}` | BLOCK | {rule.Why} |");
        }

        s.AppendLine().AppendLine("Approvals already need a second person (the checker is never the maker); these rules also keep one person from holding both sides of a payment.");
        s.AppendLine().AppendLine("## The privileged flag").AppendLine();
        s.AppendLine("Assigning a role that holds a privileged function is itself an approval (`PRIVILEGED_ROLE`). Every function below is flagged. "
            + "Eight were flagged first; **the other 20 were approved (25 October 2026, Ade) and switched on with the fix for front-end request BR-013** "
            + "(a new tenant has at least two administrators), since flagging `ASSIGN_ROLE` and the role-grant functions makes the tenant administrator a "
            + "privileged role, which a tenant with one administrator could not have assigned. None is in an everyday template, and each is named where a "
            + "template holds it.").AppendLine();
        s.AppendLine("| Function | Flag | Why | Held by |").AppendLine("|---|---|---|---|");
        foreach (var p in RoleTemplates.Privileged)
        {
            var holders = RoleTemplates.All.Where(t => (t.PrivilegedGrants ?? []).Contains(p.Function)).Select(t => t.Code).ToArray();
            s.AppendLine($"| `{p.Function}` | {(p.Flagged ? "Flagged" : "Proposed")} | {p.Why} | {(holders.Length == 0 ? "granted one by one" : Codes(holders))} |");
        }

        s.AppendLine().AppendLine("## Who reads medical information and the search log (HLT-008)").AppendLine();
        s.AppendLine("**Decided (25 October 2026, Ade): a separate Health auditor.** HLT-008 restricts medical information by role, purpose and minimum necessary "
            + "access, and the party search log shows who looked up whom. `AUDITOR` reads every staff screen except "
            + $"{Codes(RoleTemplates.SensitiveReadScreens)}; `HEALTH_AUDITOR` reads those with the audit trail.").AppendLine();
        s.AppendLine("## A second person for releasing a legal hold (OQ-111)").AppendLine();
        s.AppendLine("The data protection officer places and releases legal holds and disposes of documents, so one person could release a hold and destroy "
            + "what it protected. **Decided (25 October 2026, Ade): an approval type, `LEGAL_HOLD_RELEASE`, not a segregation-of-duties rule.** Releasing a "
            + "hold on documents (`DOCUMENTS_RELEASE_LEGAL_HOLD`) or on the party search log (`PARTY_RELEASE_SEARCH_LOG_HOLD`) is a request that a second "
            + "person approves in Workflow; the hold stays in force until then, and a rejection, expiry or withdrawal leaves it in force. The compliance "
            + "officer approves it.").AppendLine();
        s.AppendLine("## Open points");
        s.AppendLine("- Deferred (25 October 2026): which templates OFT Insurance South Africa loads and their authority bands (Workstream F, OQ-112); "
            + "whether a tenant may hide templates it does not use; Keycloak step-up (`acr_values`) before the portals launch.").AppendLine();
        s.AppendLine("## Appendix: the functions of each template").AppendLine();
        foreach (var t in RoleTemplates.All)
        {
            var functions = RoleTemplates.Expand(t, catalogue);
            s.AppendLine($"### `{t.Code}` ({functions.Count})");
            s.AppendLine(string.Join(", ", functions.Select(f => $"`{f.Code}`"))).AppendLine();
        }

        return s.ToString().TrimEnd() + Environment.NewLine;
    }

    private static string Codes(IEnumerable<string>? codes) => codes?.Any() == true ? string.Join(", ", codes.Select(c => $"`{c}`")) : "—";
}
