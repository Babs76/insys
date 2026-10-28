# Wave 2 batch 4: plan, for Ade's approval

27 October 2026. Branch `wave2-4`, from `main` at `1416683` (batch 3). Nothing is built until this plan is approved.

## Scope, in build order

| # | Item | What gets built | Proof (tests) | Size |
|---|---|---|---|---|
| 0 | **BR-084, BR-085** static data required fields (requested after this plan was sent) | Built ahead of the plan's approval, at your request. Every static data table was swept: the catalogue's mandatory flag, the API's validation and the OpenAPI `required` list now agree, and an architecture test holds them together. See the batch 4 report. | `StaticDataRequiredFieldTests` (two theories over every table) | Done |
| 1 | **OQ-052** (actuary's brief) | Nothing more to build. The addendum `docs/specs/addenda/life-policy-values.md` (branch `life-values-brief`, `1c6e889`) was sent earlier today. It is waiting on your four decisions. Once approved, it merges with this batch. | None (a document) | Done, waiting on review |
| 2 | **D-34 legal holds on a party, policy or claim** | As `wave2-3-proposals.md` §4 proposes:<br>• `POST /documents/legal-holds` to place a hold. The scope types are `PARTY`, `LIFE_POLICY`, `GENERAL_POLICY`, `HEALTH_MEMBERSHIP`, `LIFE_CLAIM`, `GENERAL_CLAIM` and `HEALTH_CLAIM`. The target is checked through its module's record query.<br>• `GET /documents/legal-holds` to list them.<br>• `POST /documents/legal-holds/{id}/release`, through a `LEGAL_HOLD_RELEASE` approval.<br>• Functions `LEGAL_HOLDS_PLACE` and `LEGAL_HOLDS_RELEASE`, both privileged.<br>• A hold covers the record and its documents.<br>No migration: `document.legal_hold` already has `scope_type` and `scope_id`. | • A hold on each scope blocks anonymisation, and its release unblocks it.<br>• A held record's document is not disposed of.<br>• A missing target gives 404; an out-of-scope one gives 403.<br>• The release's approval endings rows. | Medium |
| 3 | **BR-022** dedicated test | `MeReturnsTheTenantsLocale_BR022`:<br>• `/identity/me` gives `locale {tag, country, baseCurrency}` from the tenant's locale defaults in force.<br>• The tag follows the request's language.<br>• A tenant with other defaults gets its own values. | The test itself | Small |
| 4 | **BR-031** concurrency on General and claims | **ETag** on every General and claims-framework read the request lists: quote, proposal, policy, endorsement, claim, assessment, recovery, risk object, claim register and payment instruction.<br>**`If-Match`** on all their updates: 428 when missing, 412 when stale. This is the Party pattern (`RequireIfMatch`, `SetETag`) over about 44 write operations.<br>**`Idempotency-Key`** on the claim creates: First Notification of Loss (FNOL) and the claims-framework creates. | • A sweep test: every General and claims write operation in the contract declares If-Match (or Idempotency-Key for a create), and refuses without it.<br>• A lost-update test: two people editing one claim.<br>• A replayed claim create returns the first claim.<br>• The end-to-end tests send the headers. | Large |
| 5 | **BR-057** concurrency on billing and distribution | ETags on billing account, commission entitlement and commission statement reads. If-Match on `BillingAmendSchedule` and `DistributionClawBack`. | • Both operations: a stale tag gives 412, a missing tag 428.<br>• The ETag headers on each read. | Small |
| 6 | **BR-016** static data import approved as one change | As `wave2-3-proposals.md` §2 proposes:<br>• Submitting an import makes one `IMPORT` change request, approved once at the table's tier.<br>• The checker reads every line (`GET …/imports/{id}/lines`).<br>• All or nothing: a line changed since parks the approval (D-5).<br>• Rejection returns the drafts.<br>Migration: `change_request.import_batch_id`, and the statuses of `import_batch`. | • A 100-row import is approved with one decision.<br>• A rejected import is corrected and approved.<br>• A line changed underneath: nothing is applied, and the approval parks.<br>• Tier 2 with maker-checker off.<br>• A batch's draft submitted alone is refused.<br>• The endings rows. | Medium |
| 7 | **BR-009** catalogue labels written for people | Labels for every function (about 600) written from `INSYS_NAVIGATION.md` ("Approve", "Beneficiary management"), not generated from the code. They are kept as data beside the catalogue, so the business can reword them without a code change. **This changes your decision of 25 October (OQ-100: generated labels until the pre-go-live review). See decision 1 below.** | • Every function has a written label.<br>• No label is the code turned into words.<br>• The menu returns them. | Medium |
| 8 | **BR-017** dead-letter error has no personal data | Already met at `ea84183` and stated in the contract, but no test proves it. Add one per sink: outbox, webhook and parking ledger. Each sink dead-letters a failure whose exception message quotes the payload (a name and an ID number), and each test checks that the stored `error` holds only the type, the status or the fixed sentence. | Three tests | Small |
| 9 | **BR-008** MFA step-up | Answered in the integration guide §5 at `ea84183`. `PORTAL_MFA_REQUIRED` is already tested end to end. Add a realm test that checks the guide stays true:<br>• the portal clients are bound to `portal-mfa-browser`;<br>• the member client to `member-browser`;<br>• no `acr.loa.map` is configured. | One test | Small |

## Front-end requests raised since the triage (decision 2)
The front end's triage stops at BR-061. Its `backend-requests.md` now goes to BR-085, from phases 5 and 6 (Life and Health), and none of those requests is grouped yet. By the triage's own definition, three of them are group A:
- **BR-064:** ETags and If-Match for Life (proposal, case, scheme, policy, loan, claim).
- **BR-071:** ETags and If-Match for Health.
- **BR-073:** a defect. An over-length reason or code throws instead of giving a 422.

**Proposed: put all three in this batch.** BR-064 and BR-071 use the same pattern and the same sweep test as BR-031, so one concurrency sweep covers General, claims, billing, distribution, Life and Health together. That adds about 62 write operations. BR-073 is a defect.

**If you'd rather keep batch 4 to your list,** they open batch 5.

BR-082 and BR-083 wait for OQ-052 (the actuarial basis).

## Group B mapped to batches
Group B items already done:
- `wave2-2`: BR-001, 002, 004, 005 and 010.
- `wave2-3`: BR-011, 012, 015, 019, 020 and 022.

BR-003 is not planned (OQ-114).

The remaining 30 are grouped by the screens they serve, in the front end's phase order, so each batch unblocks whole screens:

| Batch | Requests | Screens | Notes |
|---|---|---|---|
| **Wave 2 batch 5** | BR-021, 024, 025, 026, 027, 028, 029 | Phase 1–2: names for user IDs; product versions; rating; parties; documents; workflow | BR-021 needs your sign-off on `wave2-3-proposals.md` §3. BR-026 (masked identifier) and BR-029 (party number) touch Party's identity data. |
| **Wave 2 batch 5** | BR-032, 033, 034, 035, 036, 039 | Phase 3: General and claims | These follow BR-031's ETags in batch 4. BR-036 and BR-039 are mostly contract declarations. |
| **Wave 2 batch 6, first (group A)** | BR-095, BR-092 (added by Ade, 28 October 2026) | Phase 7: broker portal quoting | **BR-095:** a broker's portal quote is rated as it is made, as the staff calculation rates a quote, so it is not left a Draft with no premium (the alternative the front end offered is a broker rating operation scoped to its own quotes). **BR-092:** a broker can register a risk object for a client it services, and list a client's, scoped as its quotes are (the alternative offered is the item's details inline on the quote). The plan sets out the choice in each. |
| **Wave 2 batch 6 (group B)** | BR-093 (added by Ade, 28 October 2026) | Phase 7: member loss reporting | A portal read of the causes and perils in force for a policy's product on the portal channel, so a member picks them from the code lists instead of typing codes. |
| **Wave 2 batch 6** | BR-050, 051, 052, 053, 054, 055, 056, 058, 059 | Phase 4: Payments, Billing and Distribution | Lists, reads and missing fields. BR-053's ETags and If-Match go here unless the batch 4 sweep takes them (it will, if you accept decision 2). |
| **Wave 2 batch 6** | BR-043, 044, 045, 046, 047, 048, 049 | Phase 4: Finance and Reinsurance | Journals, posting exceptions, periods, GL export, bordereaux, cessions, recoveries. |
| **Wave 2 batch 7** | Phase 5–6 requests BR-062 to 085, less those placed above | Life and Health | Grouped once the front end triages them, apart from BR-082 and 083 (OQ-052). |

The group C items (BR-014 done; BR-023, 030, 037, 038, 041, 060) are mostly declarations and answers. They ride with the batch that touches the same module: BR-023 with batch 5, BR-030 with batch 5, BR-037 and 038 with batch 5, BR-041 and BR-060 with batch 6.

## How the batch runs
- The work runs in the order above. Items 2, 4 and 5 (holds, concurrency) come before the larger rework (6, 7).
- Every item has its own commit and its own tracker row in `docs/frontend-requests.md`, with `contract-changes.md` updated for every contract change.
- Before hand-back: the full local integration suite, plus `OperatorTenantTests` and `FunctionAccessTests` (endpoints and functions change).
- The scope table and the head's CI counts per job come to you for review. It merges into `main` only on your approval and green CI on the exact head.

## Decided (Ade, 27 October 2026)
1. **BR-009:** the 25 October decision stands (OQ-100). Labels are written once, in the business wording review before go-live. BR-009 is scheduled with that review, not in batch 4; item 7 is dropped.
2. **BR-064, BR-071 and BR-073 are in batch 4.** The concurrency sweep (item 4) covers General, claims, billing, distribution, Life and Health.
3. **Legal holds:** the scopes are party, policy, claim and case. Payments are covered through their policy or claim. A hold on a party blocks anonymisation through all its linked records.
4. **BR-016 is signed off on three conditions:**
   - the import is approved or rejected as a whole, never partly;
   - the checker sees every line;
   - the approval is bound to a hash of the complete import, so any change needs a new approval.

   `wave2-3-proposals.md` §2 is adjusted to match.
5. **Group B mapping accepted:**
   - batch 5: phases 1–3 (BR-021 needs your sign-off on proposal §3);
   - batch 6: money;
   - batch 7: Life and Health.

   BR-082 and BR-083 wait on OQ-052.

Added after approval:
- **BR-087** (static data paging lost the row on a page boundary, group A): first in the batch, shipped on its own on `br-087`.
- **BR-088** (the default role templates through the API, group A): read only, from the same source as the template tests.

The shard times are watched; they are rebalanced only if the gap persists.
