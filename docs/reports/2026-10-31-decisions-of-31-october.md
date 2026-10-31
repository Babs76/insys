# Ade's decisions of 31 October 2026: what was built

Everything on the list is on main, except the two items set aside: B-012 until a pharmacy switch is chosen, and AI Assist until its SRS is signed.

## What was built
| Decision | Built | Notes |
|---|---|---|
| 2. Disclosures | `DISCLOSURE_MANDATORY_TEXTS` blocks `INTEGRATION_AND_REPORTING` | One text per mandatory point, per country a sellable product is sold in. A product whose rules name no country counts as sold in the tenant's own |
| 3. Stale version | A guard in every test host | No command with the shape was found (see below) |
| 7. BR-122 | `GET /integration/bulk-jobs` | Mapped in Party, which owns the jobs |
| Owed tests | Credit note tax, portal chronic request | Both pass, on every date job |
| ADR-027 phase 1 | Neutral settings, SDK guard | Amendment drafted and sent for sign-off |
| 4. Dormant | Daily, both directions, up to 1,000 parties per tenant | Cases now count, so an open case also blocks anonymisation. New `CASE` retention row, 7 years, working default |
| 6. Migration batch | Policies (General, Life), memberships, open claims (General, Life), outstanding premiums, opening balances, organisations, policy export, reconciliation report | See below |
| B-007, B-008 | Type codes `PREMIUM_ADJUST`, `BILLING_FREQ` | The catalogue limits a type code to 15 characters, so the backlog's longer names don't fit |
| B-009 | Recovery by default, `KEEP` by setting | A separate debt record: the entitlement stays paid, so the lifecycle in `domain-model.json` didn't need changing |
| B-011 | `account_kind`, `POST /identity/users/service-accounts` | A person's token is refused even holding the function |

## Decision 3: the list of commands
**There are none in anything the suites run.** Every test host now fails a command that changes two or more aggregates and returns a view whose `Id` matches none of them. All the integration and end-to-end suites passed with that on. So there was nothing to fix and no shape to give a guard test; `AViewMatchingNoneOfTheAggregatesAChangeSaved_FailsInEveryTestHost` proves the guard itself fires. It can't speak for a path no test runs. A text search for handlers touching several stores listed 18 candidates. I went through that list by name and read only `RunDueDiligenceCheck` closely, so it is a pointer, not a proof: the guard is the evidence.

## The owed locked suite
Main at 1339eae, one test project at a time: **2466 tests, 2454 passed, 10 skipped, 2 failed.** Both failures are (a), machine load: they passed alone, three runs out of three, in 33 to 38 seconds for the pair.
- **`ConsumerParkingTests...ADR022`, direct evidence.** The log says `Consumer reinsurance.ReinsurancePolicies.GeneralPolicyEndorsed timed out after 00:00:30`. A second consumer, `health.RecoveryPolicies`, hit the same limit. A timed-out delivery is retried, not hung.
- **`KeycloakRealmTests...BR094`, inferred.** A 401. The access token lasts 300 seconds and is issued before the test starts its host, and the test ran 29 minutes under load, so it most likely expired. Nothing logged the reason, so a slow fetch of Keycloak's signing keys isn't ruled out.

An earlier full run, with test projects in parallel, gave 1033 failures because the Docker daemon died partway; it says nothing about the code. Running them all at once is more than this machine holds.

## Found and fixed on the way
- **A broker change's clawback would have failed.** The reason `BROKER_OF_RECORD_CHANGED` is 24 characters and the column takes 20, so clawing back on a broker change threw. No test reached that path. It is now `BROKER_CHANGED`.
- **CI's end-to-end jobs** ran 15 to 30 minutes against a 30-minute limit, and slow runners were cancelled. Now 45, as integration has.

## Decisions for Ade
1. **ADR-027 amendment:** sign it, with its three questions and the security lead's three. Phase 2 is re-estimated at 17 to 21 days.
2. **Health benefit use to date isn't migrated.** A migrated member starts with limits and accumulators untouched, so could claim a year's benefit twice. *Recommendation: add opening accumulators to the migration batch before a Health customer, about 3 to 4 days. This matters most of the items here.*
3. **Open Health claims aren't imported.** They have no reserve to carry and go through adjudication. *Recommendation: resubmit them through the claim API at cut-over, so they're adjudicated by the same rules; decide when a Health customer's data is seen.*
4. **Disclosure gates and countries.** `Outstanding` looks at a point's texts in every jurisdiction, so in a tenant selling in two countries a customer in one would be held for the other's text. With the blocker, tenants in two countries now hit this. *Recommendation: have the gate take the party's country, about 1 day, before any two-country tenant.*
5. **Retention for a party with no records ever.** Your rule makes such a party dormant, so a prospect goes dormant the night after registering. I count its retention from registration under the `POLICY` period, so no review is raised for years. Nothing refuses a dormant party, and activity brings it back. *Recommendation: accept; compliance to confirm.*
6. **A payment-terms retention offer can't draft a frequency endorsement,** as B-008 asked: the offer records no frequency. A discount offer does draft. *Recommendation: add the frequency to the offer when retention is next changed; by hand until then.*
7. **A service account's Keycloak client is made by hand,** then registered in INSYS. B-011's estimate included provisioning one per tenant and provider. *Recommendation: put it in phase 2's runbook, and automate it if tenants multiply.*
8. **Migration, smaller points.** A file loads whole, at most 2,000 rows, so large books go in several files. Migrated policies don't appear in Analytics' written premium, as they weren't written here. A migrated claim's reserve type isn't checked until the Claims Framework takes the event, so a wrong code parks or dead-letters rather than rejecting the line, and the reconciliation shows the difference. A Life `proposalId` is now nullable; the front end has the notice.
9. **To confirm:** finance on VAT inside the premium per product; compliance on OQ-161, noting a provider's name or type can itself reveal a condition, and on the `CASE` period.

## Not verified
- **No real migration file has been run.** The tests load single rows through the API; volumes and a customer's data are untested.
- **The locked suite** ran on 1339eae, before the migration batch, dormancy and the backlog items, which have CI only.
- **Real Huawei KMS and OBS** with the new setting names: the settings are unit-tested and MinIO ran under them in CI; the real-cloud tests need credentials.
