# Role template changes: two proposals for Ade's sign-off

**Signed by Ade, 3 November 2026, and built.** A: the six functions as proposed (option 1, with audit: each rule evaluation and premium calculation is now on the audit trail, `EvaluateRuleSet` and `CalculatePremium`). Option 2 is in the go-live register as blocking (row 33, about 2 days, after the Preview). B: `LINE_CONFIGURATION_MANAGER` as proposed, with `LINECFG01/02` in the Preview (51 accounts); the fourth segregation rule is in the register as hardening (row 34). The text below is the proposal as signed. Both answer findings from building the sample tenant (handover, Open items). The templates are signed (OQ-111), so each change needs Ade's decision. Both are small changes to `src/Modules/Identity/Insys.Identity.Api/RoleTemplates.cs`, from which the addendum is generated and `RoleTemplateTests` checks every rule.

## A. Underwriters can quote and issue on their own

**The finding:** `GENERAL_UNDERWRITER` and `LIFE_UNDERWRITER` can't rate a quote or proposal, evaluate the rules on it, or record the customer's acknowledgement of the terms. Issuing needs all three, so today the product manager and the integration administrator do them. The same is true of `HEALTH_MEMBERSHIP_ADMINISTRATOR` for the acknowledgement at enrolment.

**Proposed: add six functions, as single grants, not whole domains.**

| Template | Add | What it allows |
|---|---|---|
| `GENERAL_UNDERWRITER`, `LIFE_UNDERWRITER` | `RATING_CALCULATIONS`, `RATING_CALCULATE_PREMIUM` | Rate a quote or proposal, and see the calculation |
| `GENERAL_UNDERWRITER`, `LIFE_UNDERWRITER` | `RULE_DECISIONS`, `RULES_EVALUATE_RULE_SET` | Evaluate the product's eligibility and underwriting rules on it, and see the decision |
| `GENERAL_UNDERWRITER`, `LIFE_UNDERWRITER`, `HEALTH_MEMBERSHIP_ADMINISTRATOR` | `INTEGRATION_DISCLOSURES`, `INTEGRATION_ACKNOWLEDGE_DISCLOSURE` | Read the notice, and record that the customer acknowledged it |

**Left out on purpose:** `RATING_SIMULATE_PREMIUM` and the rest of `RATING` and `RULES`. Changing rates, tables and rules stays with the product manager.

**Approvals stay separate.**
- An underwriter's decision is still bounded by their authority matrix (`rules.cfg_authority_matrices`). Beyond it, a referral still goes to a second person in Workflow, as now.
- Nothing here approves anything. No template gains an `Approves` entry.

**Segregation of duties:** the three rules (claim instruction draft and release, payout batch build and release, payee account add and verify) touch none of these functions, so `EveryTemplate_PassesEverySegregationOfDutiesRule_OQ111` still passes. None of the six is privileged, so `NoTemplate_HoldsAPrivilegedFunction_ItDoesNotName_OQ111` does too.

**A risk to weigh, for Ade:** a rules evaluation takes the inputs its caller gives it. An underwriter who both evaluates the underwriting rules and decides the case could give inputs that keep a case within their own authority. Each decision records its inputs and is audited, so it can be found, but not prevented. Two ways to close it:
1. **Accept it** with the audit, as the product manager's evaluation today has the same exposure.
2. **Build:** the rules take the case's facts from the proposal itself, not from the caller (about 2 days, Est). The grant can go ahead now, and this later.

**Recommendation:** grant the six now (option 1), and plan option 2 after the Preview.

## B. A live tenant can change its Life, General and Health configuration

**The finding:** `CONFIG_LIFE`, `CONFIG_GENERAL` and `CONFIG_HEALTH` are only in the platform steward's template, in the operator tenant. Once a tenant is live, none of its own staff can change, say, a scheme's contribution rule or a grace period. The seed's loaders stay until the tenant is active for this reason.

**Proposed: one new template, `LINE_CONFIGURATION_MANAGER`.**

| | |
|---|---|
| **Name** | Line configuration manager (STAFF) |
| **For** | Life, General and Health configuration: rules, codes and parameters of each line, under maker-checker |
| **Domains** | `CONFIG_LIFE`, `CONFIG_GENERAL`, `CONFIG_HEALTH` (each line's configuration: view, maintain and approve for its tables) |
| **Extra** | `WORKFLOW_APPROVALS`, `WORKFLOW_APPROVE`, `WORKFLOW_REJECT`, to decide another person's change in the inbox |
| **Approves** | `STATIC_DATA_T1`, `STATIC_DATA_T2` |

**Maker-checker:** two people hold the template. One drafts and submits; the other approves. The static data engine refuses an approval by the person who made the change, as it does for every tenant's static data, so one person can't do both. A tier 3 change needs no approval, as now.

**Why a new template rather than additions:**
- **Not `PRODUCT_MANAGER`:** products and rating are a different job from the lines' operational rules (grace, lapse, claims limits, member categories), and different people often own them.
- **Not the line templates:** an underwriter or claims handler changing the rules they work under weakens the control the rules give.
- **Not `TENANT_ADMIN`:** it already holds users and roles; adding the lines' business rules makes it too wide.

**Segregation of duties:** no rule involves the configuration functions, so the test passes. If Ade wants it, a fourth rule is possible: a line configuration manager can't hold a template that works under those rules (`GENERAL_UNDERWRITER`, `LIFE_UNDERWRITER`, `CLAIMS_HANDLER`, `HEALTH_CLAIMS_ASSESSOR`). That's a check on roles assigned to a user, which INSYS checks per role today, not per user, so it would need building (about 1 day, Est).

**The Preview:** two new accounts, `LINECFG01/02`, would hold it.

## What happens on sign-off
For each signed proposal: the template change, the regenerated addendum (`docs/specs/addenda/role-templates.md`), the tests above, and the seed using the underwriter itself (A) or `LINECFG01/02` for the scheme's contribution rule (B). About half a day each (Est).
