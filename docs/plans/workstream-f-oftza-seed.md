# Workstream F: the OFT Insurance South Africa sample data

**Plan for Ade's sign-off, 2 November 2026. Nothing is built.** Answers front-end request BR-131. Order: after batch 7.1 merges, before 7.2. Needed for demos and the front end's Phase 13 exit; it doesn't block go-live. The sizes are my estimates, made without building it, so low confidence.

**Tenant code `OFTZA`.** The validator takes capitals, digits and underscores, so `OFT-ZA` is refused.

## What it contains
All of it is invented. Names come from a fixed list of made-up people and companies; identity numbers are generated to pass format checks and belong to nobody; addresses are fictional; emails use `example.test`.

| Area | Contents |
|---|---|
| Tenant | `OFTZA`, ZAR, Africa/Johannesburg, English, with Afrikaans, isiZulu and isiXhosa marked "sample translation, requires professional review" (localisation addendum), every onboarding step complete, and active |
| Users | One persona per role template, at least two administrators, and a maker and a checker wherever a second person is needed, in Keycloak and IAM. Portal users: a member, a broker and a provider |
| Life | A funeral product and a term product, each with a rate table |
| General | Motor and household |
| Health | One scheme, one plan with two options, a tariff, and a formulary |
| Parties | About 30: 20 individuals, 5 organisations (a sponsor among them), 3 providers, 2 brokers |
| Policies | About 12 per line, across statuses: in force, in grace, lapsed, cancelled, and awaiting renewal |
| Claims | About 8 per line, one at each main stage: registered, assessed, approved, partly approved, declined, paid, and one disputed through a case |
| Billing and payments | Invoices with collections through the simulator, one failed and one unknown outcome, a payout batch with second-person release, a refund, and a statement import |
| Finance | Chart of accounts, posting rules, 12 periods with last month closed and the current one open, journals from the above, and one VAT return prepared |
| Also | Commission statements, one quota share treaty with cessions, cases of each family, notification templates, document templates |

## How it loads
**Only through the governed API, never by writing to the database.**
- **A new tool, `tools/Insys.Seed`:** it calls the API as the personas, with real tokens, and stops at the first refusal. Run again, it skips what exists, by business key.
- **Static data:** through the static data imports, with a checker approving each.
- **Business records:** through the normal processes (quote to policy, claim to payment), so every record has its events, audit and journals. The migration loads (B-013) carry only what must be older than today allows, such as a policy that began last year.
- **Two steps aren't its own API calls, and both exist already:** the migrator's operator bootstrap (`--bootstrap-operator`), and Keycloak's admin API to create the tenant's first administrators' users.
- **Dates are relative to the day it runs,** so a seed stays current and its test doesn't fail with the calendar.

**Where it runs:** demo and test only. It uses the payment simulator, so the environment must allow it, and start-up refuses it elsewhere. The tool refuses to run where `ASPNETCORE_ENVIRONMENT` is `Production` unless declared non-production. Persona passwords are generated at the run and written to its own output, never to the repository.

**Its test:** a CI job loads it on a fresh database and reads it back through the API. It makes CI longer, so I'd run it on main only.

## Size
| Slice | Days (Est) |
|---|---|
| Minimal: tool, tenant, personas, static data (GAP-004), usable on its own | 2 to 3 |
| Parties, products, policies | 2 to 3 |
| Claims, billing, payments, finance | 2 to 3 |
| Commission, treaty, cases, the CI job | 1 to 2 |
| **Total** | **7 to 11** |

Each slice merges on green by itself, so the minimal seed can reach the front end first. Batch 7.1's measured time will say how far to trust these.

## Decisions needed from Ade
1. **Role templates (OQ-111):** I propose loading all the signed-off templates as they stand. They're optional starting points for other tenants, so this doesn't change that.
2. **Authority bands (OQ-112):** I propose the samples in `frontend-integration-guide.md` §3, marked as samples for the business to confirm. Limits are in ZAR.
3. **Privileged functions:** I propose the seed gives no one a privileged function, except where a template names it.
4. **Which demos come first:** I've assumed all three lines. If one matters most, the seed can load it first.
5. **Real-looking South African terms:** product wordings will be invented samples, not legal text, and say so. Tell me if a demo needs reviewed wording.
