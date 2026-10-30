# GAP-113: what ends a periodic due diligence review (OQ-138): report

Branch `gap113-dd-review`, on `wave4-2`. It builds Ade's decision of 30 October 2026 (OQ-138), recorded as an amendment to the due diligence addendum, with the identity document expiry date he added the same day. It merges under the standing approval after 4.2, once CI is green on its head.

## What was there before
Any fresh screening (the monthly automatic re-screen, a list update, or a clerk's screening) cleared an expired party with nothing else outstanding, and the system closed its review task. Completing the review task did nothing to the party. `next_review_on` counted from the day it cleared.

## Scope
| # | Item | What | Test |
|---|---|---|---|
| 1 | Review mode per risk class | `party.cfg_customer_risk_classes.review_mode`: `PERSON` (default, and every existing row) or `AUTOMATIC` (V0098). | (static data; the tests below) |
| 2 | PERSON: a person ends the review | An expired party stays `EXPIRED` after a fresh screening, with `PERIODIC_REVIEW` outstanding, until a person with `PARTY_COMPLETE_DUE_DILIGENCE_REVIEW` completes the review: `POST …/due-diligence/review` (`detailsCurrent: true`). The review runs a fresh screening and records who and when (`reviewed_at`, `reviewed_by`). A hit found meanwhile is reviewed first, and the review stays due until then (`review_due`). | `DueDiligenceTests.APassedReviewDate_ExpiresTheParty_ARescreenLeavesItExpired_AndOnlyAPersonsReviewClearsIt_OQ124_OQ138`; `PeriodicReviewTests` (Party, 5) |
| 3 | AUTOMATIC | A fresh screening ends the review, with the system requester recorded as the reviewer. | `DueDiligenceTests.AnAutomaticClass_ClearsOnAFreshScreening_WithTheSystemAsReviewer_OQ138` |
| 4 | The Workflow task | Completing `DUE_DILIGENCE_REVIEW` directly is refused (409 `TASK_CLOSED_BY_ITS_RECORD`); it closes when the party clears. | the PERSON test above |
| 5 | Identity document expiry | Party identifiers gain an optional `expiry_date`. The review is refused (422 `DD_IDENTITY_DOCUMENT_EXPIRED`, naming the document type only) while the identity check's document has expired by the business date and no newer one of its kind is on file. It does not change the party's status between reviews. | `DueDiligenceTests.AReview_IsRefusedOnAnExpiredPassport_AndCompletesOnceANewOneIsRecorded_OQ138`; the PERSON test (a national ID with no expiry date does not block) |
| 6 | The reviewer is a contributor | The reviewer cannot give the party's enhanced approval; someone who took no part does. | `DueDiligenceTests.TheReviewerOfAnEnhancedParty_IsRefusedItsEnhancedApproval_AnotherPersonGivesIt_OQ138` |
| 7 | `next_review_on` | Counts from the day the review completes (unchanged). | the PERSON test |
| 8 | Readiness | With the gates on, `DUE_DILIGENCE_REVIEWER` also lists `PARTY_COMPLETE_DUE_DILIGENCE_REVIEW` when no role grants it. | `DueDiligenceTests.Readiness_FlagsALifeTenant…_OQ123` |

## For the front end
- **New action:** `POST /party/parties/{id}/due-diligence/review`, body `{ "detailsCurrent": true, "note": "…" }`, If-Match the due diligence's ETag; function `PARTY_COMPLETE_DUE_DILIGENCE_REVIEW`.
- **New fields:** due diligence `reviewDue`, `reviewedAt`, `reviewedBy`, `reviewedBySystem`, and `PERIODIC_REVIEW` in `outstanding`; identifier `expiryDate` (request and view); risk class `reviewMode`.
- **New error codes:** 422 `DD_IDENTITY_DOCUMENT_EXPIRED`, 409 `DUE_DILIGENCE_NOT_UNDER_REVIEW`, 409 `TASK_CLOSED_BY_ITS_RECORD`.

## Choices made in building it (none changes the decision)
- **The error code is `DD_IDENTITY_DOCUMENT_EXPIRED`**, not `DUE_DILIGENCE_IDENTITY_DOCUMENT_EXPIRED` as suggested: error codes are at most 30 characters (the error code catalogue's column).
- **A renewed document sits beside the old one.** A party held one identifier per document type and country. A document with an expiry date can now be renewed: a new one of the same kind is accepted when it expires later than every one on file. A kind held without an expiry date (a national ID) is still held once. "Newer" means expiring later and not yet expired.
- **The review needs the screening provider.** The fresh screening is the provider's. With no active screening provider (which readiness flags when the gates are on), the review is recorded but the party stays expired.
- **`detailsCurrent` must be true**, as the reviewer's confirmation; the note is audited with the review.

## Local results
(filled after the locked run)
