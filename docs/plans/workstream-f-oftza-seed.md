# Workstream F: the OFT Insurance South Africa sample data

**Signed by Ade as written, with all five proposals accepted, 2 November 2026. Partly built: see State.** Answers front-end request BR-131. Order: before the INSYS Preview (`docs/plans/insys-preview.md`), which uses this data, and so before 7.2. Needed for demos and the front end's Phase 13 exit; it doesn't block go-live. The sizes are my estimates, made without building it, so low confidence.

**Tenant code `OFTZA`.** The validator takes capitals, digits and underscores, so `OFT-ZA` is refused.

## What it contains
All of it is invented. Names come from a fixed list of made-up people and companies, and addresses are fictional. How identity numbers, phones and bank accounts avoid real people is under Fake personal details.

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

## Fake personal details
A checksum-valid South African ID number can't be guaranteed to belong to nobody, so the scheme makes a match with a real person as unlikely as it can:
- **Identity numbers** are 13 digits with a valid checksum, that Home Affairs wouldn't issue: every one has the date 30 February, and a citizenship digit of 9, where 0 and 1 are in use. INSYS checks the format and nothing parses the date (confirmed in the build). A party's `birthDate` is a separate, plausible field, so rating works.
- **Phone numbers:** `+27` then a `0`. A national number after a country code never starts with 0, so it can't be dialled on any network; no reserved range was needed.
- **Bank accounts:** the tenant's sample account is `000000000` at "SAMPLE BANK". Parties' accounts follow the same pattern when they're added.
- **Names** carry "Sample" in every surname and company name.
- **Email** stays on `example.test`.

This reduces the risk and doesn't remove it. The block below is what stops anything reaching a person.

## OFTZA can't reach real channels or real money
**A tenant-level block, built and tested in the first slice.** A tenant marked as a sample tenant has its notifications and payments sent only to stubs, even if a real SMS, email or payment provider is connected to that environment later. It fails closed: a stub missing means refused, never sent for real. **The mark can't be removed by anyone, the operator included (Ade, 2 November).** It's set when the tenant is created and stays for the tenant's life, and a real customer always gets a new tenant. No API sets or clears it afterwards; any route that would is refused with a catalogued error, and a test proves it. Tests prove it with a real provider registered beside the stub: nothing reaches it for `OFTZA`, and another tenant's notifications and payments still do.

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
| Minimal: tool, tenant, personas, static data (GAP-004), the sample tenant block, usable on its own | 3 to 4 |
| Parties, products, policies | 2 to 3 |
| Claims, billing, payments, finance | 2 to 3 |
| Commission, treaty, cases, the CI job | 1 to 2 |
| **Total** | **8 to 12** |

Each slice merges on green by itself, so the minimal seed can reach the front end first. Batch 7.1's measured time will say how far to trust these.

## State
- **Built:** the sample tenant block; the tool, with global reference data, the tenant, two administrators, every tenant role template, a persona per template, the onboarding configuration, five products published by the real route (motor, household, funeral, term with age-band rates, and a medical scheme, its table there only to pass publication's test case), the Life and Health configuration behind them, 3 providers and 2 brokers with verified bank accounts, addresses and cleared due diligence, about 12 policies per line (Life in force, migrated from last year, in grace and lapsed; General in force, cancelled and awaiting renewal; Health active, suspended and terminated), claims at each main stage on every line (registered, assessed, approved, partly approved, declined, paid), the week's provider payment run and the claim and refund payout batches, each released by a second person, collections through the simulator with one bounced and one with no clear answer, a refund, a provider statement import, last month closed, the current month open and a VAT return prepared, one quota share treaty on the General products with their cessions, a broker's commission statement approved by the finance manager, a claim disputed through a complaint case, and **`OFTZA` active**. It signs tokens with the development key, so it runs only against Development and Testing.
- **Beyond the templates:** `OFTZA_APPROVER`, a sample role. Readiness needs a role holding approval and the type's own function for the three due diligence approvals and the non-renewal, and no template combines them. Readiness no longer waives treaties: the sample treaty is in.
- **Loading (Ade, 2 November):** the two administrators hold exactly `TENANT_ADMIN`'s template. Two loader users, a maker and its checker, hold the other modules' non-privileged static data functions, and end disabled with their roles revoked. A persona's privileged role gets its second approval from the second administrator; nothing bypasses it.
- **Rerunning:** a run stopped anywhere can be run again. Every create has a deterministic key, good for the API's 24 hours; past that, parties fall back to a search by identifier, then by exact name for one stopped before its identifier went on. While the tenant is onboarding, ended loaders come back by the routes an administrator uses: a revoked role is assigned again, and a disabled loader is replaced by a new user, `oftza-seed-loader-1-2`, since disabling is final (no route or transition leads back). All end disabled. The loaders now end after activation, since a scheme can only be registered on an active tenant and its contribution rule names its number; with no live loader left on an active tenant, loading is over. The seed waits out a 429 for its `Retry-After`, or backs off without one; the API's limit is unchanged.
- **INSYS has no route that cancels a Life policy,** so Life has no cancelled status here. Grace, lapse and renewal come from the hourly jobs, so on a live environment they appear within the hour.
- **Payments reach only the simulator.** A callback's key and secret are shown once, so each run that reports money issues its own endpoint.
- **Next:** Keycloak sign-in (design in the handover); cases of the other families; portal users linked to a member, a broker and a provider; commission, the treaty, cases, and the CI job.

## Decisions, by Ade
1. **Role templates (OQ-111):** all the signed-off templates as they stand.
2. **Authority bands (OQ-112):** the samples in `frontend-integration-guide.md` §3, in ZAR, marked for the business to confirm.
3. **Privileged functions:** none, except where a template names one.
4. **Lines:** all three.
5. **Product wording:** invented, and labelled as such.
