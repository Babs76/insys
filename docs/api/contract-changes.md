# API contract changes

What changed in the API contract, newest first, so the front end can sync. The OpenAPI documents in this folder are the contract; this list says what moved.

## Wave 5b batch 5b.2 (branch `wave5b2`): medication controls (addendum §4.2; F046, F047, F092, F093)

### New
- **`health.cfg_medication_limits`** (static data, tenant, Tier 2): `limitCode`, `planCode`, `productCode` or `activeIngredient`, `maxQuantity`, `maxAmount`, `period` (ISO 8601).
- **Reason** `DUPLICATE_THERAPY`: the same ingredient before the last supply has run out; pends for a person. A medicine flagged `priorAuthRequired` pends with `PREAUTH_REQUIRED` without an authorisation.

## Wave 5b batch 5b.1 (branch `wave5b1`): pharmacy claims (addendum §4.1; F039 to F045) and OQ-161

### New
- **`health.cfg_prescription_benefits`** (static data, tenant, Tier 2): per plan and tier, `covered`, `benefitCategory`, `copayType`, `copayValue`, `limitAmount`, `prescriptionValidityDays`, `refillTolerancePercent`.
- **`GET /health/claims/{id}/clinical?purpose=`** [`health_case.read_clinical`]: a claim's codes, diagnoses and prescriptions; every read is audited. Function `HEALTH_READ_CLAIM_CLINICAL`.

### Changed
- **`health.ref_medicine_formulary`** gains `benefitCategory`, `priorAuthRequired`, `maxQuantity`, `maxDaysSupply`, `referenceGroup`. **`health.cfg_claim_format_definitions`** gains `lineKind` (`PHARMACY`) and `requiredLineFields`.
- **Claims:** a line in `POST /health/claims` and `/portal/health/claims` takes `pharmacy`: `daysSupply`, `prescriptionNumber`, `prescriber`, `prescribedOn`, `repeatNumber`, `repeatsAuthorised`; `tariffCode` is then the product code, up to 20 characters. A format that isn't `PHARMACY` rejects such a line.
- **`ClaimLineView`:** a pharmacy line's `tariffCode` is the word `PHARMACY`. New `paidQuantity`; new reasons `QUANTITY_CUT`, `NOT_ON_FORMULARY`, `TIER_NOT_COVERED`, `NO_PRESCRIPTION_BENEFIT`, `PRESCRIPTION_NOT_VALID`, `REPEAT_EXCEEDED`.
- **`PortalClaimLineView.tariffCode` can be null** (OQ-161): a principal member looking at an adult dependant's claim. **The front end must handle this.**

## Wave 5a batch 5a.3 (branch `wave5a-3`): recoveries, credit notes and disputes (addendum §4.6 to §4.8; F048 to F050)

### New
- **`health.cfg_provider_recovery_settings`** (static data, tenant, Tier 2): `settingCode`, `medicalScheme` (the default of the offset flag), `reviewAfterDays`, `invoiceCollectionMethod`. **`health.cfg_provider_contracts.offsetAllowed`** (nullable): null takes the tenant's default.
- **`GET`, `POST /health/provider-recoveries`, `GET /health/provider-recoveries/{id}`**, and on a recovery **`/invoice`**, **`/write-off`**, **`/reverse`**, **`/hold`**. A `POST` is a person's finding: a second person approves it before anything is owed.
- **`GET`, `POST /health/provider-credit-notes`, `GET /health/provider-credit-notes/{id}`.**
- **`GET /health/remittances/{id}`, `GET /portal/health/remittances/{id}`:** gross, offsets (original claim, reversal date, new claim), repayments and net. `paymentDue` is false for a net of nil.
- **Portal:** `GET /portal/health/recoveries`, `GET`, `POST /portal/health/credit-notes`. `POST /portal/cases` may link a provider's own remittance (`REMITTANCE`), and a dispute linking a remittance, run or claim holds its recoveries from offsets until the case closes.
- **Approval types** `PROVIDER_RECOVERY_WRITE_OFF`, `PROVIDER_RECOVERY_FINDING`, `PROVIDER_CREDIT_NOTE`; **business references** `HEALTH_RECOVERY`, `HEALTH_CREDIT_NOTE`. **Screens** `HEALTH_RECOVERIES`, `HEALTH_CREDIT_NOTES`, `HEALTH_REMITTANCES` and the provider portal's. **Notice** `PROVIDER_RECOVERY_NOTICE`. **Posting rules** `RECOVERY_RAISED`, `RECOVERY_OFFSET`, `RECOVERY_REPAID`, `RECOVERY_WRITTEN_OFF`, `RECOVERY_REVERSED`, `RECOVERY_SETTLED` (readiness `POSTING_RULES` asks for them).

### Changed
- **Remittances** (`RemittanceView`, `PortalRemittanceView`) gain `grossAmount`, `offsetAmount`, `repaidAmount`; `amount` stays what is paid and may be 0. A run's `totalAmount` is the sum of the nets.
- **Claims:** `POST /health/claims` and `/portal/health/claims` take `replacesClaimId`; `MedicalClaimView` and `PortalMedicalClaimView` show `replacesClaimId` and `replacedByClaimId`.
- **Statements:** `recoveryStatus` is now filled: `OWED`, `OFFSET`, `INVOICED` or `RECOVERED`.
- **Preauthorisations:** a reversed claim can return an authorisation from `Utilised` to `Approved`.

## Wave 5a batch 5a.2 (branch `wave5a-2`): complex cases, care plans and referrals (addendum §4.3 to §4.5; F034 to F038)

### New
- **`/health/complex-cases`**: `POST`, `GET` (by `membershipId`, `caseManagerId`, `status`), `GET /{id}`. These views hold **no clinical detail**.
- **`GET /health/complex-cases/{id}/clinical?purpose=`**: condition, care plans and notes, for the current case manager or the clinical function. A purpose is required, and every read and refusal is audited. Errors `HEALTH_PURPOSE_REQUIRED`, `NOT_THE_CASE_MANAGER` (403).
- **`POST .../case-manager`** (`caseManagerId`, `reason`): the person must hold the case manager function (`NOT_A_CASE_MANAGER`); reassigning needs a reason and **ends the last manager's clinical access**.
- **`POST .../hold`, `.../resume`, `.../close`** (`reason`), **`.../care-plan`** (`items`, `reason` for a change), **`.../care-plan/items/{itemId}/progress`** (`DONE`, `MISSED`, `CHANGED`), **`.../notes`** (one with `sharedWithProviderId` needs `consentReference`: `CARE_CONSENT_REQUIRED`).
- **`/health/referrals`**: `POST`, `GET`, `GET /{id}` (no clinical reason), **`GET /{id}/clinical?purpose=`** (`health_case.read_clinical`), **`POST /{id}/decide`**, **`POST /{id}/visits`**. States `REQUESTED`, `APPROVED`, `DECLINED`, `USED`, `EXPIRED`. They affect no claim (OQ-144).
- **Provider portal:** `POST /portal/health/referrals` (as itself; optional `consentReference`), `GET /portal/health/referrals` (made or received), **`GET /portal/health/referrals/{id}/clinical?purpose=`**: for the provider who wrote it, or the one it names with consent (otherwise 403 `CARE_CONSENT_REQUIRED`); any other provider gets 404. Staff `POST /health/referrals` also takes `consentReference`.
- **`POST /health/referrals/{id}/consent`** (`consentReference`, `reason`) and **`POST /health/referrals/{id}/consent/withdraw`** (`reason`), If-Match, permission `health_referral.consent`: withdrawal ends the receiving provider's access at once. The view gains `consentReference`, `consentWithdrawnAt`.
- **Permissions** `health_complex_case.read`, `.update`, `.manage` (the case manager function), `health_case.read_clinical` (the clinical function), `health_referral.read`, `.create`, `.decide`.
- **Workflow task** `FOLLOW_UP_CARE` (process `HEALTH_CARE_ACTIVITY_FOLLOW_UP`), assigned to the case manager. **Event** `health.CareActivityOverdue`: references only.
## Communications callbacks (branch `comms-callback`): a client token must hold the function

- **`POST /communications/callbacks/{endpointKey}`:** at an endpoint with `requireClientToken`, a token must now hold `communication_callback.create`. Before, any authenticated token of the tenant passed. A signature is still required.
- **Function** `COMMUNICATIONS_CALLBACK_CLIENT`, privileged and in no role template. **Readiness** `COMMUNICATION_CALLBACK_CLIENT` on `SHARED_OPERATIONAL_SETUP`, blocking.
## BR-116 (branch `br-116`): a portal user can answer a renewal

- **`GET /portal/general/renewals/{id}`** now returns an `ETag`, and `PortalRenewalView` has `rowVersion` (also in the list). Accept and decline return the new `ETag`. Send it as `If-Match` on accept and decline: a stale one gets 412. Before this, a portal user had no way to get the version these operations require.
- **Preferences:** `CommunicationsSetPreference` and `PortalCommunicationsSetPreference` are unchanged. `If-Match` stays optional, and each item of the list read has its `rowVersion`.

## Wave 5a batch 5a.1 (branch `wave5a-1`): provider measures, quality reviews and statements (addendum §4.1, §4.2; F028 to F030)

### New
- **`health.cfg_provider_measures`** (static data, tenant, Tier 2): `measureCode` (`CLAIMS_SUBMITTED`, `REJECTION_RATE`, `AVERAGE_PAID`, `DECISION_DAYS`, `REPEAT_SERVICE_RATE`, `DISPUTES_PER_100`), `providerType` (none: all), `threshold`, `direction` (`ABOVE`, `BELOW`), `minClaims`.
- **`GET /health/providers/{providerId}/measures`** (`monthKey`), staff only (OQ-141): each measure's `value`, `claimCount`, `threshold`, `judged`, `breached`.
- **`GET`, `POST /health/providers/{providerId}/quality-reviews`**: `score` 1 to 5, `reason`.
- **`GET`, `POST /health/provider-statements`, `GET /health/provider-statements/{id}`, `POST /health/provider-statements/import`** (`csv`: a header `claim_number,amount`, then one line per claim, OQ-142), **`POST /health/provider-statements/{id}/lines/{lineId}/resolve`** (`reason`, If-Match). Lines have `matchResult`: `MATCHED`, `AMOUNT_DIFFERS`, `NOT_ON_OUR_RECORDS`, `NOT_ON_STATEMENT`, `REVERSED_BY_US`; with the last, `reversedOn` and `recoveryStatus` (`OWED`, `OFFSET`, `INVOICED`, `RECOVERED`; `OWED` until 5a.3). Status: `EXCEPTIONS`, `RECONCILED`. Error `PROVIDER_STATEMENT_DUPLICATE`.
- **Permissions** `health_provider.read`, `health_provider.update`. **Screens** `HEALTH_PROVIDER_PERFORMANCE`, `HEALTH_PROVIDER_STATEMENTS`.
- **Workflow task** `REVIEW_MEASURE` (process `HEALTH_PROVIDER_MEASURE_REVIEW`, reference `HEALTH_PROVIDER_MEASURE`) on a breach. Its queue comes from the task type's routing. **Event** `health.ProviderMeasureBreached`.
- **Readiness warning `PROVIDER_MEASURES`** (`DOMAIN_REFERENCE_DATA`, advisory).

### Changed behaviour
- **The Health daily job** measures last month's providers once each. A breach only opens a task: it changes nothing about the provider.

## Defect: a reversal gives back the right accumulators (branch `fix-accumulator-giveback`)

### Changed behaviour
- **`POST /health/claims/{id}/reverse`** now also gives back the claim's deductible and co-payment, and changes only the patient's own and the family's accumulators. No request or response changes. A dependant's accumulators no longer drop when someone else's claim is reversed.

## Wave 5, B-010 (branch `wave5-b010`): both brokers are told of a broker of record change

### New
- **`commission.mst_intermediary_register`:** optional **`contactPartyId`**: who is told of a broker of record change. With none, the notice goes to the intermediary's `partyId`; with neither, it is logged and no notice goes.
- **Notification type `BROKER_CHANGE_NOTICE`** (merge fields `policy_number`, `product_code`, `effective_date`, `incoming_intermediary`, `outgoing_intermediary`, `broker_role`: `INCOMING` or `OUTGOING`). A tenant configures the type and a template; until then the delivery parks, as for any notice.
- **Event `general.GeneralBrokerChangeApproved`.**
- **Readiness warning `INTERMEDIARY_NO_CONTACT`** (`PAYMENTS_AND_DISTRIBUTION`, advisory): active intermediaries with neither a contact party nor a party.

### Changed behaviour
- **Told at approval, not when the change applies.** A change approved for a later date tells both brokers at once.
## Defect: a reversed Health claim is posted to the ledger (branch `fix-health-reversal`)

### Changed behaviour
- **`POST /health/claims/{id}/reverse`** on a paid claim now posts to the ledger: new Finance transaction type **`CLAIM_PAID_REVERSED`**, business reference `MEDICAL_CLAIM`, with the variable `recover_from` (`PROVIDER` or `MEMBER`). No request or response changes.
- **Setup needed:** a tenant entitled to HEALTH needs a posting rule for it: debit an overpayment receivable, credit the account its `CLAIM_PAID` rule debits. Readiness `POSTING_RULES` lists it when missing, and this blocks `FINANCIAL_FOUNDATIONS`. Without a rule, each reversal waits as a posting exception.
- **Readiness warning `CLAIM_REVERSAL_ACCOUNTS`** (`FINANCIAL_FOUNDATIONS`, advisory): the reversal rule credits an account no `CLAIM_PAID` rule debits.

## Wave 4 batch 4.3c (branch `wave4-3c`): B-001, broker of record change (addendum §4.8; OQ-134 to OQ-136; addition 3)

### New
- **`POST /general/policies/{id}/endorsements`** takes an optional **`intermediaryCode`**: with `endorsementType` `INTERMEDIARY` (a type the tenant configures, premium method `NONE`) and `changes: []`, it drafts a broker of record change to that intermediary from `effectiveDate`. The intermediary must be allowed to sell the product (DST-001 refusals as for a quote). It is `Rated` at once with no premium change.
- **`POST /general/endorsements/{id}/appointment-letter`** (`GeneralRecordAppointmentLetter`), body `{ "documentId" }`: the policyholder's appointment letter, a document filed against the endorsement (`businessRefType` `GENERAL_ENDORSEMENT`). Submitting an `INTERMEDIARY` endorsement without it is 422 **`APPOINTMENT_LETTER_REQUIRED`**. Approval is the usual endorsement approval by a second person (OQ-134).
- **Endorsement view:** `newIntermediaryCode`, `appointmentLetterId`.
- **Event `general.GeneralPolicyIntermediaryChanged`** (previous and new intermediary, effective date).

### Changed behaviour
- **The policy moves at the effective date.** Approved for today or earlier, the policy's `intermediaryCode` changes at once; for a later date it waits on the policy (`pending_intermediary_code`, `_from`) and the daily job moves it on the day. Until then only the current broker sees the policy on the broker portal (OQ-136).
- **Commission splits pro rata at the date (OQ-135).** On each of the outgoing broker's invoices whose cover (the frequency's months from its due date, to the period end) runs over the date, the outgoing broker keeps the days before and the incoming broker accrues the days from it on its own plan. Commission not yet on a statement is cut, published as a `commission.CommissionClawedBack` of the cut part so Finance reverses it; commission on an unpaid statement is clawed back and the kept share accrued again (source `BROKER_SHARE`); commission already paid is not recovered (see the report). Later invoices accrue to the new broker as usual.

## Wave 4 batch 4.3b (branch `wave4-3b`): retention and the renewal figures (addendum §4.3, §4.7; F026, F027)

### New
- **`general.cfg_retention_offers`** (static data, tenant, Tier 2): `offerCode`, `productCode` (none: any), `kind` (`DISCOUNT_PERCENT`, `EXCESS_CHANGE`, `COVER_CHANGE`, `PAYMENT_TERMS`), `value`, `maxUsesPerPolicy`, `authorityRole` (none: any holder of the function). **`general.cfg_renewal_settings.lapseRiskDays`** (optional).
- **Retention cases:** `GET /general/retention-cases` (`status`, `policyId`), `GET /general/retention-cases/{id}`, `POST /general/retention-cases` (`policyId`, `trigger`, `note`; Idempotency-Key), `POST …/{id}/offers` (`offerCode`), `POST …/{id}/offers/{offerId}/answer` (`accepted`), `POST …/{id}/outcome` (`RETAINED`, `LOST`, `WITHDRAWN`, `note`). Screen `GENERAL_RETENTION_CASES`, action `GENERAL_WORK_RETENTION_CASE`; permissions `general_retention.read`, `general_retention.update`. Errors 403 `RETENTION_OFFER_NOT_PERMITTED`, 409 `RETENTION_OFFER_USED_UP`. One open case per policy.
- **Workflow process `GENERAL_RETENTION`** (business reference `GENERAL_RETENTION_CASE`): the case's owner task, closed with the case.
- **Analytics summary figures (F027):** `RENEWALS_DUE`, `RENEWALS_INVITED`, `RENEWALS_ACCEPTED`, `RENEWALS_RENEWED`, `RENEWALS_DECLINED_BY_CUSTOMER`, `RENEWALS_NOT_RENEWED_BY_INSURER`, `RENEWALS_LAPSED` (counts, by the month of the renewal date); `RENEWAL_RATE` (renewed over due) and `RETENTION_RATE` (retained over retention cases closed), as `rate` from 0 to 1; `AVERAGE_RENEWAL_PREMIUM_CHANGE` (money per currency, renewed less current, over renewals renewed); `RETENTION_OFFERS_MADE` and `RETENTION_OFFERS_ACCEPTED` (counts, `by` kind). Retention figures count in the month they happened. **`DashboardFigure` gains `rate`** (null with no denominator).
- **Events:** `general.GeneralRenewalOpened`, `GeneralRenewalRenewed`, `GeneralRenewalNotRenewed`, `GeneralRetentionCaseOpened`, `GeneralRetentionOfferMade`, `GeneralRetentionOfferAccepted`, `GeneralRetentionCaseClosed`; `GeneralRenewalAccepted`, `Declined` and `Lapsed` gain `productCode` and `periodEnd`.

### Changed behaviour
- **A declined renewal opens a `NOT_RENEWING` case**, and a renewal still awaiting acceptance `lapseRiskDays` before its end opens a `LAPSE_RISK` case (the daily job), unless one is open.
- **An accepted `DISCOUNT_PERCENT` on a case with a renewal** lowers the renewal's offered premium by the per cent and records it as accepted (channel `STAFF`), even after a decline; the policy renews on it.

## Wave 4 batch 4.3a (branch `wave4-3a`): renewal acceptance and insurer non-renewal (addendum §4.3, §4.4, §4.6; OQ-130, OQ-132)

### New
- **`general.cfg_renewal_rules.acceptance`** (static data, optional): `AUTOMATIC` (none means this), `ON_ACCEPTANCE` or `ON_PAYMENT`.
- **`general.cfg_non_renewal_reasons`** (new static data, tenant, Tier 2): `reasonCode`, `noticeDays`, `customerWording`, `requiresApproval`.
- **`POST /general/renewals/{id}/accept`** and **`/decline`** (`GeneralAcceptRenewal`, `GeneralDeclineRenewal`; function `GENERAL_RECORD_RENEWAL_ANSWER`, permission `general_renewal.respond`): staff record the customer's answer (channel `STAFF`). 409 `GENERAL_RENEWAL_NOT_OFFERED` unless offered (or, to decline, accepted).
- **`POST /general/policies/{id}/non-renewal`** (`GeneralProposeNonRenewal`; function `GENERAL_PROPOSE_NON_RENEWAL`, permission `general_policy.non_renew`), body `{ "reasonCode" }`. A reason with `requiresApproval` raises Workflow approval `GENERAL_NON_RENEWAL` (business reference `GENERAL_RENEWAL`, the renewal's ID; the approver needs `general_policy.non_renew` and authority); otherwise it is decided at once.
- **Member and broker portals:** `GET /portal/general/renewals`, `GET /portal/general/renewals/{id}` (`PortalRenewalView`: status `OFFERED`, `ACCEPTED`, `RENEWED`, `DECLINED_BY_CUSTOMER`, `NOT_RENEWED_BY_INSURER`, `LAPSED`; the current and offered premium, `needsAcceptance`, and `coverEndsOn` for a non-renewal), `POST …/accept` and `…/decline` (channel `PORTAL` for a member, `BROKER` for a broker). Functions `CUSTOMER_PORTAL_GENERAL_RENEWALS` / `…_ANSWER_RENEWAL` and `BROKER_PORTAL_GENERAL_RENEWALS` / `…_ANSWER_RENEWAL`. A renewal under review is never shown; one is shown once invited.
- **Renewal view:** `answer` (`acceptance`, `needsAcceptance`, accepted and declined at, by and `…Channel`, `renewalPolicyId`) and `nonRenewal` (`reasonCode`, `status` `REQUESTED`/`DECIDED`/`REJECTED`, requested and decided by and at, `noticeOn`, `coverExtendedTo`). Status gains `Accepted`, `DeclinedByCustomer`, `NotRenewedByInsurer`, `Lapsed`.
- **Notification type `NON_RENEWAL_NOTICE`** (merge fields `policy_number`, `product_code`, `period_end`, `cover_ends_on`, `reason`).
- **Events:** `general.GeneralRenewalAccepted`, `GeneralRenewalDeclined`, `GeneralRenewalLapsed`, `GeneralCoverExtended`.

### Changed behaviour
- **Every invited renewal has a record.** A product that is not reviewed gets a renewal offered at its current terms when it is invited.
- **Acceptance (OQ-130):** `AUTOMATIC` renews at the period end unless declined (as before, when `auto_renew`); `ON_ACCEPTANCE` renews only once accepted; `ON_PAYMENT` renews at the period end and its first premium paid is the acceptance; if grace ends unpaid, the renewal policy is cancelled for non-payment (4.1) and the renewal is `Lapsed`. A renewal whose premium rises by more than the tenant's `worse_terms_pct` (default 15 %) needs acceptance whatever the product says. A renewal awaiting acceptance at the period end lapses, and the policy expires.
- **Policy view:** an expired policy may carry `expiryReason`: `NOT_RENEWED_BY_INSURER`, `DECLINED_BY_CUSTOMER` or `LAPSED`.
- **Non-renewal (F025, OQ-132):** decided, the notice is sent at once and the renewal is `NotRenewedByInsurer`; decided less than the reason's notice before the period end, the policy's period end moves to the notice's end and the extra days (annual premium × days / 365) are billed as one instalment due the day after the old end.

## GAP-113 (branch `gap113-dd-review`): what ends a periodic due diligence review (OQ-138, Ade 30 October 2026)

### New
- **`POST /party/parties/{id}/due-diligence/review`** (`PartyCompleteDueDiligenceReview`; function `PARTY_COMPLETE_DUE_DILIGENCE_REVIEW`, permission `due_diligence.review`; If-Match is the due diligence's ETag). Body: `detailsCurrent` (must be true) and an optional `note`. It completes the periodic review of a party past its review date, runs a fresh screening, and assesses the party: it clears when nothing else is outstanding (at ENHANCED, after the enhanced approval, which the reviewer cannot give). Errors: 409 `DUE_DILIGENCE_NOT_UNDER_REVIEW` (no review due, or already completed); 422 `DD_IDENTITY_DOCUMENT_EXPIRED` (the identity check's document has expired and no newer one of its kind is on file; the detail names the document type, never the number); 422 `VALIDATION_FAILED` when `detailsCurrent` is not true.
- **Due diligence view:** `reviewDue` (a review is due and not ended), `reviewedAt`, `reviewedBy` (the person, or the system requester `00000000-0000-0000-0000-00000000005e` for an AUTOMATIC class) and `reviewedBySystem`. `outstanding` lists `PERIODIC_REVIEW` while a PERSON class's party waits for its review after a fresh screening.
- **Customer risk classes** (`party.cfg_customer_risk_classes`, static data) gain **`reviewMode`**: `PERSON` (the default, and the value of every existing row) or `AUTOMATIC`. Required when a tenant creates a row.
- **Party identifiers** gain an optional **`expiryDate`** on `POST /party/parties/{id}/identifiers` and on the identifier view. A renewed document (an expiry later than every one of its kind on file) is added beside the one it replaces; a kind held without an expiry date is still held once (409 `PARTY_IDENTIFIER_IN_USE`).

### Changed behaviour
- **A party past its review date stays `EXPIRED`** after an automatic re-screen when its class reviews by `PERSON` (the default); only the review action ends the review. For an `AUTOMATIC` class a fresh screening ends it, as before, with the system recorded as the reviewer.
- **The `DUE_DILIGENCE_REVIEW` task** cannot be completed through Workflow (409 `TASK_CLOSED_BY_ITS_RECORD`); it closes when the party clears.
- **The reviewer cannot give the party's enhanced approval** (403 `MAKER_CHECKER_SAME_PERSON`).
- **Readiness `DUE_DILIGENCE_REVIEWER`** also lists `PARTY_COMPLETE_DUE_DILIGENCE_REVIEW` when no staff role grants it (with the gates on).

## Wave 4 batch 4.2 (branch `wave4-2`): claim decision rights on every decline, renewal review

### Fixed first: BR-109 / GAP-112 (group A), an enhanced due diligence approval requested by a provider's result
- **What was wrong.** When the identity provider's result was the last check to complete a party needing enhanced due diligence, no person was acting, so the approval was requested with an empty requester. Workflow refused to create it; the delivery failed and retried until dead-lettered, and the party stayed `IN_PROGRESS` with `ENHANCED_APPROVAL` outstanding and no approval.
- **Now.** An approval the platform raises with no person acting names the **system requester**, `makerId` `00000000-0000-0000-0000-00000000005e`. Any person with authority for the type (and its permission) can decide it; a person who completed the last check is still the maker of the approvals they cause, and cannot approve them.
- **`ApprovalView.raisedBySystem`** (new, always present): true for such an approval, so the screen can show "System" instead of a person.
- **Parties already stuck** are asked again at their next assessment, which a change to the party's details or any check or hit review recorded for it starts: a request recorded with no requester is treated as never sent. The daily jobs do not reassess them.
- **A request Workflow cannot create** still fails its delivery: it is listed at once in `GET /integration/parked-deliveries?status=Failing` and retried with back-off, then dead-lettered (ADR-022). Nothing is swallowed.
- **Nobody who contributed to the assessment approves the enhanced approval** (Ade, 30 October 2026): anyone who recorded a check or reviewed a hit since the party was last cleared gets 403 `MAKER_CHECKER_SAME_PERSON`, and the approval is not in their decidable list.
- **The system requester is reserved**: no user account may have it (V0097); it never signs in, approves or rejects.
- **One waiting approval per record** is now enforced by a unique index (V0097), as Workflow already assumed.
- **No requester follow-up task** for a system-raised approval that expires or is withdrawn: the raising module receives the ending (for due diligence, the party is assessed again and a new approval requested).

### Changed (OQ-102, Ade, 29 October 2026: any decision that declines all or part of a claim sends `CLAIM_DECISION_RIGHTS`)
- **A full decline now sends it too**, in every domain, to the same recipient as a partial approval: `GeneralDeclineClaim`, `LifeDeclineClaim`, a Health claim rejected by staff or at validation, and a Health claim declined for contribution arrears. Before, only a partial approval sent it. `amount` is `0.00` for a full decline.
- **`decision_reasons`** is the decline reason's customer wording (claims decision reasons), when the reason is one in force. Health now carries it too, on a partial approval and a decline. It is only the configured wording, never the claim's own reason text, lines or diagnoses (HLT-008; settles OQ-116 for declines).
- **New merge fields** (OQ-101) from the tenant's dispute case type for claims (`workflow.cfg_case_types`, family `DISPUTE`, link type `CLAIM`):
  - `appeal_route`: `PPR_NFO` for an insurer, `CMS` for a medical scheme. A Health claim takes a CMS type where the tenant has one.
  - `review_options`;
  - `ombud_details`.
  Nothing is added if the tenant has no such case type in force.

### New (addenda/renewals-and-retention.md §4.2, §4.5; OQ-131; addition 2)
- **Renewal review.** `general.cfg_renewal_rules` gains `review`. A reviewed product's policy is reviewed before its invitation, from `general.cfg_renewal_review_rules` (`productCode`, `eligibilityRuleset`, `underwritingRules`, `claimsLookbackYears`, `reviewDaysBeforeInvitation`). The review:
  - reads the claims of the policy and the policies it renewed over the lookback (count, paid, loss ratio against the premium earned), this period's endorsements, and claims referred for fraud;
  - checks that the product is on sale for the renewal period and that the policyholder is cleared for issue;
  - asks Rules to evaluate the eligibility rule set (`INELIGIBLE` or `REFER`) and the product's underwriting rules (`DECLINE` or `REFER`). The inputs are `renewal`, `claims_count`, `claims_paid`, `loss_ratio`, `endorsements`, `fraud_referred_claims`, `sum_insured` and `premium`;
  - for a product that re-rates, creates a renewal quote (`renewalId`) priced through Rating, times the claims experience factor (`general.cfg_claims_experience_factors`; 1.0 when no band applies).
- **`GET /general/renewals`** (`GeneralListRenewals`; `status`, `policyId`) and **`GET /general/renewals/{id}`** (`GeneralGetRenewal`): status `Reviewing`, `Referred`, `NotEligible`, `Offered`, `Renewed` or `NotRenewed`, plus the figures read, each check and its result, the re-rating, `loadingPct`, `offeredPremium` and `changedTerms`. Screen `GENERAL_RENEWALS`.
- **`POST /general/renewals/{id}/decision`** (`GeneralDecideRenewal`, `general_quote.underwrite`): `{ offer, decisionCode, loadingPct? }` on a referred or ineligible renewal. It needs underwriting authority covering the product and the sum insured; otherwise 403 `UNDERWRITING_AUTHORITY_EXCEEDED`. A referred or ineligible renewal opens a Workflow task on `GENERAL_RENEWAL_REVIEW` (reference `GENERAL_RENEWAL`), routed by authority, and the decision closes it.
- **Invitation.** A reviewed renewal is invited once offered, with merge fields `renewal_premium` and `currency`. With changed terms it is invited at least the tenant's notice before the renewal date (`general.cfg_renewal_settings.changedTermsNoticeDays`, default 31); otherwise with the rule's `noticeDays`.
- **Renewal.** At the period's end the policy renews on the offered premium. Renewing a reviewed product before its review is offered is 409 `GENERAL_RENEWAL_NOT_OFFERED`.
- **Readiness `DOMAIN_CODES`:**
  - a reviewed product needs a review rule;
  - a re-rated product whose renewal `notice_days` is under the tenant's changed-terms notice is listed (addition 2);
  - a Health scheme re-rated on `EXPERIENCE` or `BLENDED` is listed until its next year's contribution rule (`health.cfg_contribution_rules` effective on the renewal date) is loaded.
- **Health scheme renewal notice (§4.5).** `SCHEME_RENEWAL_NOTICE` (merge fields `scheme_number`, `renewal_date`) goes to the scheme's sponsor `notice_days` before its renewal date (`health.cfg_scheme_renewal_rules`), once per scheme year.
- **Events:** `rules.RuleEvaluationRequested` (a module asks Rules to evaluate a rule set; a rule set not in force parks the delivery, `RULE_SET_NOT_IN_FORCE`), `general.GeneralRenewalRatingRequested`, `GeneralRenewalReferred`, `GeneralRenewalNotEligible`, `GeneralRenewalDecided`, `GeneralRenewalOffered`. `GeneralRenewalInvited` gains `offeredPremium` and `currency`.

## Wave 4 batch 4.1 (branch `wave4-1`): grace and short-period refunds (addenda/renewals-and-retention.md §4.1)

### New
- **General grace (F006).** Static data `general.cfg_grace_period_rules` (T, Tier 2): `productCode`, `frequency`, `graceDays`, `coverDuringGrace`, `cancellationReason`, `claimDeclineReason`, `deductArrearsFromClaims` (optional, default false). An instalment unpaid past its due date (Billing's arrears, as of its detection date) starts grace from that due date. The policy view gains **`grace`** (`arrearsSince`, `unpaidInstalments`, `graceUntil`, `coverDuringGrace`), null when nothing is unpaid. A payment that settles the arrears clears it. Still unpaid after `graceUntil`, the daily job cancels the policy **for non-payment**, with the rule's `cancellationReason` and its refund rule; the cancellation has no `cancelledBy`. It is cancelled from `arrearsSince` without cover in grace, and from the day after `graceUntil` with it.
- **A loss in grace without cover** (`coverDuringGrace` false, loss from `arrearsSince`) is always accepted: notification, registration, assessment and settlement all work as usual (Ade, 29 October 2026).
  - Its payment instruction is **held** with `heldReason` **`PREMIUM_ARREARS`** until the arrears are paid, and then released automatically. Releasing it by hand is 409 `PREMIUM_ARREARS`.
  - With `deductArrearsFromClaims`, a settlement larger than the arrears is instead paid **less them**: the instruction's `amount` is the settlement less the arrears, which Billing settles from it.
  - If grace ends unpaid, the claim is **declined** with `claimDeclineReason`, whatever its stage short of payment, and a held instruction becomes `Rejected`. The policyholder gets `CLAIM_DECISION_RIGHTS` with the reason's customer wording (OQ-102).
- **A claim paid less the arrears (OQ-137, post gross).**
  - The payment instruction's `amount` is the net and gains `premiumOffset`.
  - The claim's financials payment movement has `amount` = the gross and gains `premiumOffset` and `netAmount`.
  - The General claim view gains `paymentSplit` (`gross`, `premiumDeducted`, `net`).
  - `claims.ClaimPaymentCompleted` and `general.GeneralClaimSettled` gain `premiumOffset`.
  - The settled `CLAIM_STATUS` notice gains merge fields `premium_deducted` and `net_amount`, with `amount` the gross.
  - New Finance transaction type `PREMIUM_OFFSET` (the non-cash premium receipt) needs a posting rule; without one the posting is parked, as for any type.
- **Notices** (Wave 4 addition 1), to the policyholder, from the tenant's templates:
  - `GRACE_NOTICE`, with merge fields `policy_number`, `product_code`, `unpaid_since`, `grace_until`, `cover_ends_on` (the last day of grace with cover in grace, else the day before `unpaid_since`);
  - `NON_PAYMENT_CANCELLATION`, with merge fields `policy_number`, `product_code`, `cancelled_from`, `refund_amount`, `currency`.
- **Short-period refunds (F007, OQ-133).** Static data `general.cfg_short_period_scales` (T, Tier 2): `scaleCode`, `daysInForceTo`, `percentRetained`, one row per band. `general.cfg_cancellation_reasons_and_refund_rules` gains `shortPeriodScale`. A `SHORT_PERIOD` cancellation keeps the first band's per cent covering the days in force (period start to the day before the effective date), past the last band all of it; the refund is the annual premium less that, less the fee, never below zero. A `SHORT_PERIOD` reason with no scale is still 422 `GENERAL_RULE_NOT_SPECIFIED`. There is no platform scale: each tenant loads its own.
- **Health contribution grace (F006).** Static data `health.cfg_contribution_grace_rules` (T, Tier 2): `scheme` (optional; a rule naming none applies to every scheme without its own), `graceDays`, `suspendAfterDays`, `terminateNoticeDays`, `reinstateOnPayment` (optional, default true). Unpaid member or employer contributions start grace. A claim for a service from the first unpaid due date is **held**: adjudicated as `Pended` with `pendReason` `CONTRIBUTIONS_UNPAID`. Once paid, the daily job adjudicates it again. Still unpaid on the suspension day, the membership is suspended (reason `ARREARS`), its held claims are declined (`CONTRIBUTIONS_ARREARS`), and the member gets `CONTRIBUTION_SUSPENSION` (merge fields `member_number`, `suspended_from`, `notice_ends`). When the notice ends, Workflow opens a task on the built-in process `HEALTH_ARREARS_TERMINATION` (reference `HEALTH_MEMBERSHIP`, queue `HEALTH_MEMBERSHIP`) for a person to decide; termination is never automatic. Paying the arrears closes that task. It reinstates the membership if the tenant's `reinstateOnPayment` says so (the default); otherwise the membership stays suspended for staff. Claims declined during the suspension stay declined unless staff reopen them.
- **Readiness `DOMAIN_CODES`**: for GENERAL, every product and frequency in `general.cfg_product_billing_frequencies` needs a grace rule, and every `SHORT_PERIOD` reason needs a scale. For HEALTH, a contribution grace rule is needed. Each missing one is listed.
- **Events:** `health.MembershipTerminationDue`, `health.MembershipArrearsCleared`; `general.GeneralPolicyArrearsCleared`, `general.GeneralPolicyLapsedUnpaid`, `general.GeneralPremiumOffsetFromClaim`. `general.GeneralClaimApproved` gains `policyId`, `holdReason` and `premiumOffset`.

## Wave 3 batch 4 (branch `wave3-4`): code lists name their column (BR-105), Health service types, audited sensitive reads

### Changed (group A: audited reads, HLT-008)
- **`GET /health/preauthorisations/{id}/clinical` (`HealthGetAuthorisationClinical`) now needs `?purpose=`** (up to 200 characters): without it, 422 `HEALTH_PURPOSE_REQUIRED`. **Breaking:** send the reviewer's purpose. Each read leaves one audit entry (`ReadClinical`: who, when, which preauthorisation, the purpose). Before this batch the read was not audited at all, though the contract said it was.
- **A refused read is audited as `DENIED`**, with why (`details.refused`): the clinical read with no purpose (`PURPOSE_REQUIRED`) or of a preauthorisation not found or outside your scope (`NOT_FOUND`); a document content read with no purpose (`PURPOSE_REQUIRED`) or without the category's allowed role (`ROLE_REQUIRED`); a portal document read of a document not yours (`NOT_FOUND`); a report export your roles may not download (`REPORT_NOT_ALLOWED`). A read refused for want of the permission (403) is audited as before (`AccessDenied`). The responses are unchanged.
- The document read's refused entry is now operation `ReadDocument` with outcome `DENIED` (it was `ReadDocumentDenied` with outcome `FAILURE`); the audit trail filters by outcome.

### Changed
- **Every field with `x-code-list`** (131 in the core document) also has **`x-code-list-column`**: the column of that table holding the value the field carries. It is `code` where the table's own code is the value, and a named column where the table has one (`method_code` on `billing.ref_collection_methods`, `plan_code` / `option_code` on `health.cfg_plans_and_options`, `value_code` on `party.ref_personal_attribute_code_lists` with its `x-code-list-filter`, and so on). Offer the table's rows in force and send that column's value.
- **Health eligibility** (`POST /health/eligibility`, `HealthCheckEligibility`, and `EligibilityDecisionSummary.serviceType`): `serviceType` now comes from the new tenant table **`health.cfg_service_types`** (column `service_type`), every service the tenant offers, a general consultation included. A service type not in force there is 422 `HEALTH_CODE_NOT_IN_FORCE`; before, it was not checked.
- **Preauthorisation** (`HealthRequestAuthorisation`, the portal preauthorisation request) keeps `health.cfg_authorisation_validity_periods` (column `service_type`): the services that need authorisation.

### New
- Static data `health.cfg_service_types` (scope T, Tier 2): `serviceType` (Code(20), required). Its endpoints and functions come from the generator like every Health table. Health readiness (`DOMAIN_CODES`) lists it while a tenant has none in force.
- `docs/api/code-field-allowances.tsv`: the reviewed list of code-named fields (…Code, …Type and plurals) in the core and platform-operator documents that do not yet declare a code list or values, each with its reason or "to declare". A wave declares a field when it touches it and takes it off the list; the list is empty by the hardening phase.

## Wave 3 batch 3 (branch `wave3-3`): the API descriptions

### Changed
- **`GET /openapi/{core|static-data|platform-operator}.json`** serves the committed documents (`docs/api`), built into the host, never generated on request. Their content is unchanged.
- **Outside Development and Testing** (staging, production) they are served only to a signed-in user: without a valid token, 401. In Development and Testing anyone may read them, as before.
- Any other document name is 404.

## Wave 3 batch 2 (branch `wave3-2`): customer due diligence (addenda/due-diligence.md)

### Changed (gates, OQ-123, §5)
- **A tenant's due diligence gates** apply when its settings say so (`party.cfg_due_diligence_settings.gates_enabled`) and, with no settings row, when it is entitled to LIFE. Every tenant that existed before V0092 has a row with the gates **off**.
- **With the gates on**, by the platform defaults (`party.cfg_due_diligence_gates`, tenant-configurable):
  - `LifeIssuePolicy` is refused with 422 `DUE_DILIGENCE_NOT_CLEARED` unless the policyholder's due diligence is `CLEARED`. The problem's `errors.outstanding` lists what is missing (check types, `HIT_REVIEW`, `ENHANCED_APPROVAL`, `SCREENING`, `BLOCKED`, `NOT_STARTED`), never a hit.
  - `LifeIssueLoan` is refused the same way (a loan advance is a payout to the policyholder).
  - A claim payment instruction is **held, not refused**, when its payee is not cleared (Life: CDD cleared and screened in 30 days; General and Health: screened in 30 days, no hit waiting). `InstructionView.heldReason` is `DUE_DILIGENCE_NOT_CLEARED`, nothing is paid, and it is released automatically once Party clears the payee. `ClaimsReleaseInstruction` on it is 422 `DUE_DILIGENCE_NOT_CLEARED`.
  - A refund or case redress payout is created but **held out of payout batches** while its payee is not screened (`PaymentView.heldReason`), and joins the next batch once cleared.
  - Quotes (Life proposals, General quotes) and General and Health issue are allowed by default; a tenant may gate them.
- **A portal user** never sees `DUE_DILIGENCE_NOT_CLEARED`: any portal operation answers 422 `PORTAL_UNDER_REVIEW`, which says only that their details are being reviewed (§6).

### New
- `GET /party/parties/{id}/due-diligence` (`PartyGetDueDiligence`): status (`NOT_STARTED`, `IN_PROGRESS`, `CLEARED`, `REFERRED`, `BLOCKED`, `EXPIRED`), risk class and score, level, `outstanding`, clearance, `nextReviewOn`, `lastScreenedAt`, checks and beneficial owners. `hits` only for holders of `due_diligence.review_hit` (else null). Each read is audited.
- `POST .../due-diligence/checks` (`PartyRunDueDiligenceCheck`, Idempotency-Key): `SCREENING` or `IDENTITY` through the provider, or `ADDRESS`, `BENEFICIAL_OWNERSHIP`, `SOURCE_OF_FUNDS` recorded against a document filed against the party.
- `POST .../due-diligence/hits/{hitId}/review` (`PartyReviewScreeningHit`, If-Match): `TRUE_MATCH` or `FALSE_POSITIVE` with a reason.
- `POST .../due-diligence/risk-class` (`PartyOverrideRiskClass`): through Workflow approval `DUE_DILIGENCE_RISK_OVERRIDE`.
- `POST .../due-diligence/report` (`PartyRecordFicReport`): the FIC reference of an s28A or s29 report.
- `POST .../due-diligence/unblock` (`PartyRequestDueDiligenceUnblock`, privileged): through Workflow approval `DUE_DILIGENCE_UNBLOCK`.
- `POST /party/parties/{id}/beneficial-owners` (`PartyRecordBeneficialOwner`, Idempotency-Key).
- `GET /party/due-diligence` (`PartyListDueDiligence`): the compliance work list, by status, risk class, review date and open hits.
- Approval types `DUE_DILIGENCE_ENHANCED` (reference `DUE_DILIGENCE`), `DUE_DILIGENCE_RISK_OVERRIDE` and `DUE_DILIGENCE_UNBLOCK`, opening the `PARTY_DUE_DILIGENCE` screen. `WorkflowApprove` and `WorkflowReject` also check their required permissions.
- Functions: `PARTY_DUE_DILIGENCE` (screen); `PARTY_RUN_DUE_DILIGENCE_CHECK`, `PARTY_REVIEW_SCREENING_HIT`, `PARTY_OVERRIDE_RISK_CLASS`, `PARTY_RECORD_FIC_REPORT`; privileged `PARTY_UNBLOCK_DUE_DILIGENCE` and `PARTY_APPROVE_ENHANCED_DD`; job `PARTY_DUE_DILIGENCE_JOBS`.
- Workflow tasks in queue `COMPLIANCE`: `DUE_DILIGENCE_HIT_REVIEW`, `DUE_DILIGENCE_SANCTIONS` (the compliance case for a true sanctions match) and `DUE_DILIGENCE_REVIEW` (an expired party).
- Events (party ID and status only): `party.DueDiligenceChanged`, `party.DueDiligenceCleared`, `party.DueDiligenceReferred`, `party.DueDiligenceBlocked`, `party.DueDiligenceExpired`; `party.PayeeScreeningRequested`.
- Readiness (SHARED_OPERATIONAL_SETUP): `DUE_DILIGENCE_LIFE_GATE`, `DUE_DILIGENCE_PROVIDER`, `DUE_DILIGENCE_REVIEWER`.
- Static data: `party.cfg_due_diligence_requirements`, `cfg_customer_risk_factors`, `cfg_customer_risk_classes`, `cfg_due_diligence_gates` (GT, with platform defaults) and `cfg_due_diligence_settings` (T).
- The test-only daily job runner's result gains `dueDiligence`.
- `PartyListIdentityChecks` lists identity checks only; screening and the other checks are on the due diligence view.

### Migrations
- **V0092:** the five configuration tables with their defaults, `party.due_diligence`, `party.screening_hit`, `party.beneficial_owner`, `payment.payment.held_reason`, and `workflow.process_instance.process_code` widened to 30.
- **V0093 (one time source):** `payment` and `communication.resolve_callback_endpoint` take the application's time, and `audit.ensure_monthly_partitions` the application's UTC date. A retiring callback endpoint's cut-over is judged by the application's clock, not the database's. No API change.

## BR-103 (branch `br-103`): arrears counted as of their detection date

### Changed
- **`billing.ArrearsDetected`** gains `detectedOn` (the business date Billing's ageing ran on) and `overdueInvoices` (the account's invoices unpaid past due that day). Both are null on events raised before BR-103.
- **Life** counts a policy's missed premiums, and takes the grace rule, as of `detectedOn`, never the date it processes the event. A late, retried or parked delivery therefore gives the same count and grace period. An instalment paid since is not counted; an event without `detectedOn` is counted as of the business date it occurred on.

## Wave 3 batch 1 (branch `wave3-1`, merged at `7eeed73`): identity verification, staff duplicates, front-end requests

### Breaking
- **`PartyVerifyIdentifier`** requires `documentId`: the identity document the person checked the number against, filed against the party (422 otherwise). It is recorded as a due diligence check (F001).
- **`PartyRegisterParty`** refuses a person who matches an existing party, by the same names (ignoring case) and birth date, with 409 `PARTY_POSSIBLE_DUPLICATE` and `candidates` (`partyId`, `partyNumber`, `displayName`, `identifierLastFour`). Registering again with `confirmNew: true` and a `reason` creates the party, records the override as `CONFIRMED_NEW`, and audits it as `OverrideDuplicateMatch` (OQ-127).

### New
- `PartyListIdentityChecks` (`GET /party/parties/{id}/identity-checks`): each check by a provider (`partnerCode`, `providerRef`) or a person (`documentId`, `checkedBy`), with its outcome.
- With an active `IDENTITY_VERIFICATION` partner (a new partner type, as is `SCREENING`), every identifier added is checked by the provider. A pass marks it verified, and a failure leaves it unverified with the reason.
- **BR-101:** `PortalRaiseCase` accepts a `CLAIM` link to a claim the member's portal read shows (General, Life or Health). Any other claim gives 404.
- **BR-089:** `ComplianceGetReturn`, `CompliancePrepareReturn` and the return actions declare their ETag. `CommunicationsSetPreference` returns the preference's version as its ETag. The list and preference reads say each row's `rowVersion` is its If-Match.
- **BR-090:** `CommunicationsListNotifications` says `sourceId` needs `sourceType` (422 otherwise).
- **BR-096:** a workflow definition's `domainValidation` is optional (V0088): a definition made through the static data API can name none.
- **BR-097:** `PortalQuoteView.pricedOnCreation`.

### Fixed
- **Cessions by period** (`ReinsuranceListCessions` with `periodCode`) and the cessions a bordereau takes now use the tenant's business month, not the UTC month. A cession at 23:30 UTC on the 31st belongs to the next month in Johannesburg.
- **Ledger partitions:** opening an accounting period creates its partitions (V0089), so a reversal or a retried posting dated in any open period posts.

### Migrations
- **V0088:** `domainValidation` is optional.
- **V0089:** ledger partitions for open periods.
- **V0090:** `party.duplicate_candidate.status` widened, with `CONFIRMED_NEW`.
- **V0091:** the partner types and `party.due_diligence_check`.

## Wave 2 batch 7 (branch `wave2-7`, merged at `67576bc`): the front end's Life and Health requests

### Changed: my-authority lists all the authority approving checks (BR-081)
- `WorkflowMyAuthority` rows gain `basis` (`DELEGATION`, `MODULE_LIMIT`, `PERMISSION`) and `appliesTo` (`claimType=DEATH`, or null).
- Life claims now list Life's own limits by claim type (`MODULE_LIMIT`), and no longer the delegation table's `LIFE_CLAIM` rows, which approving never used.
- A holder of `static_data.approve` also sees each static data approval type the delegation table does not configure (`PERMISSION`, limit 0, role `(static_data.approve)`).

### New: Life (BR-062, 063, 065, 066, 074 to 078, 086)
- `LifeListLoans` (`GET /life/loans`, by `policyId` and `status`): the loan gives `currency`, the policy's, as does `LoanView` (BR-062, BR-063).
- `LifeClaimView` gives `approvalId`, `requestedBy`, `decidedBy`, `decidedAt`, `notifiedAt` and `reserveType` (BR-065).
- `LifePolicyView` gives `cashValue` and `cashValueDate`: the latest stored on or before today. They stay null until the actuarial basis (OQ-052) defines a valuation (BR-065).
- `LifeListUnderwritingCases` (`GET /life/underwriting-cases`, by `status`, `underwriterId` and `decisionPending`) (BR-074).
- `CaseView` gives `requestedBy` (who proposed the waiting decision), `approvalId`, `decidedAt` and `assignedTo` (BR-076).
- `ProposalView.policyId` once converted; `LifePolicySummary.proposalId`; `LifeListPolicies` takes `proposalId` (BR-075, BR-086).
- `x-eventual-effects` are declared on capture, submit, the case decision and issue, and on Rating's and Rules' evaluations for a Life subject. `RulesEvaluateRuleSet.subjectType` lists its values, and its `code` says which rule set a proposal and a case use (BR-077, BR-086).
- Code fields name their table (`x-code-list`): claim type, cause, beneficiary relationship, alteration, loan rate, reserve type, claim decision reason, underwriting decision and the exclusion of a decision term, frequency, collection method and scheme type. A case decision's `reason` (20) and `decisionCode` (15), and a claim's `reserveType` (15) and `decisionReason` (20), state their limits. Evidence type states its 20-character limit; no static data table lists evidence types yet (BR-066, BR-078).

### New: Health (BR-067 to 072, 079, 080)
- `HealthListSchemes` (`GET /health/schemes`, by `schemeNumber`, `sponsorCode` and `status`) (BR-067).
- `currency` on the membership, accumulator and preauthorisation views, from the membership (BR-068).
- `HealthSearchMemberships` (`POST /health/memberships/search`): the list's filters, with the member number in the body (BR-069).
- `MembershipView` gives `events` (type, effective date, reason), `payerPartyId`, `billingAccountRef` and `currency` (BR-070).
- The preauthorisation gives `clinicalReviewer` and `decidedBy`. The diagnosis codes come only from `HealthGetAuthorisationClinical` (`GET /health/preauthorisations/{id}/clinical`): it needs the clinical review permission, and each read is audited (HLT-008). It is the new function `HEALTH_READ_AUTHORISATION_CLINICAL` (BR-072).
- `HealthListEligibilityDecisions` (`POST /health/eligibility-decisions/search`, by member number and dependant) (BR-072).
- Plan, option, dependant type, service type, member category, collection method, sponsor and diagnosis fields name their tables (BR-072).
- `MedicalClaimView` gives `approvedBy` and `approvedAt`. `PaymentRunView` gives `approvalId`, `approvedBy`, `releasedAt`, and each remittance its advice `documentId`. Remittances are paid one by one, so the payout batch is the remittance's payment's (`paymentId`): a run has no single batch. The utilisation and fraud cases give `openedAt` and `concludedAt` (BR-079).
- `domain-model.json`: `ProviderPaymentRun` Calculated → Rejected (`RejectRun`) (BR-080).

No migration: every new field was already stored.

### Defect fixed: billing schedules starting late in a month
A schedule starting on the 29th to 31st drifted to the 28th after February and gave a year a thirteenth instalment. Due dates now count from the start: the 30th stays the 30th (28 or 29 in February), and a year has 12 monthly instalments totalling the annual premium.

## Wave 2 batch 6 (branch `wave2-6`, merged at `c2a5870`)

### Breaking: lists that were arrays are cursor pages (phase 4)
- **`FinanceListJournals`**, **`FinanceListPostingExceptions`**, **`ReinsuranceListCessions`** and **`ReinsuranceListRecoveries`** now return a `CursorPage` (`items`, `nextCursor`; `cursor` and `limit` parameters). `ReinsuranceListRecoveries` no longer takes `after`. `FinanceListJournals` and `ReinsuranceListCessions` no longer require a business record or policy.
- **`PaymentsRevokeCallbackEndpoint`** requires If-Match (428 without it, 412 when stale), from the new `PaymentsGetCallbackEndpoint`.

### New: phase 4 lists, reads and fields (BR-041, BR-043 to BR-056, BR-058, BR-059)
- **Payments.**
  - `PaymentsList` searches by `reference`, `status` and `from`/`to` without a business record (BR-050).
  - `PaymentsListBatches` lists by status, purpose and currency; the batch gives `builtAt` and `approvalId` (BR-051).
  - `PaymentsGetException` reads one exception, with `currency`, `statementLine`, `raisedAt` and `resolvedAt`; `exceptionType` is declared (BR-052).
  - The payment gives `initiatedAt`, `counterpartyPartyId`, `bankAccountId`, `failureReason` and `rowVersion`, with ETags; the allocation gives `rule`; the history's `source` is declared; `PaymentsGetCallbackEndpoint` is new (BR-053).
- **Billing.**
  - `BillingListAccounts` lists by payer, policy and status (BR-054).
  - `BillingListRefunds` lists by account and status; the account read gives `refunds`, and the refund `approvalId` (BR-055).
  - Billing's code fields declare their values or table, and `BillingArrearsView.currency` is new (BR-056).
- **Distribution.**
  - `DistributionListStatements` lists by intermediary, period and status; the statement gives `approvalId` (BR-058).
  - `DistributionListEntitlements` takes `statementId`; the entitlement gives `rateBasis` and `rateUnit` (BR-059).
- **Finance.**
  - Journals are found by number, period and accounting dates, and give `reversedByJournalId` and `baseCurrency` (BR-043).
  - `FinanceGetPostingException` is new, and the exception gives the failed event's amount, currency, accounting date, transaction type and business record (BR-044).
  - `FinanceGetPeriod` is new; calendar periods not yet opened are listed as Future, with a null `id`; the period gives `reopenRequestedAt` and `reopenApprovalId` (BR-045).
  - The GL export gives `sentAt` and `sentBy` (BR-046).
- **Reinsurance.**
  - `ReinsuranceListBordereaux` is new, and `ReinsuranceGetBordereauContent` serves the CSV as `bordereau-{treaty}-{period}.csv`; the bordereau gives `fileName` (BR-047).
  - Cessions are found by treaty or month, and `sourceType` is declared (BR-048).
  - The recovery gives `billedBy` and `settledBy` (BR-049).
- **ETags in the contract (BR-041):** every 2xx response that carries a record's `rowVersion` as its ETag declares the `ETag` header: 161 core operations and every static data version operation.
- **Migrations V0083 to V0087:** payment failure reason; batch build time and approval; exception raise and resolve times; refund approval; entitlement rate basis; GL export sender; recovery actors. Existing rows take their creation time where it stands for the event, and the plan's basis; anything not recorded before stays null.

### Breaking: a loss is notified only with a peril and cause of the policy's line (Ade)
- **`GeneralNotifyLoss` and `PortalGeneralNotifyLoss`** take only a `perilCode` among the perils of the policy's product line, and a `causeCode` that is a cause of that peril: the BR-093 list. Anything else is 422 `GENERAL_LOSS_CODE_NOT_FOR_LINE` (new), naming the code and the line. A code not in force at all is still 422 `GENERAL_CODE_NOT_IN_FORCE`.

### Changed: the instalment loading carries through to the policy and Billing (Ade)
- **`GeneralIssuePolicy`:** for a quote priced in a frequency, the policy is billed in that frequency; another `billingFrequency` is 422 `GENERAL_FREQUENCY_NOT_QUOTED` (new). The policy keeps the quote's loading: `GeneralPolicyView.instalmentLoadingPct` (null when none). `general.GeneralPolicyIssued` carries `InstalmentLoadingPct`.
- **Billing** charges each instalment as quoted (the annual premium over the periods, with the loading, to the cent). The year totals exactly the loaded annual premium, the last instalment taking the rounding. An endorsement keeps the loading.
- **`totalPayable`** on `GeneralQuoteView` and `GeneralPortalQuoteView`, when a loading applies: the annual premium with the loading, to the cent, which the instalments total. Null otherwise; hidden on the portal with the premium.
- Migration V0082 adds `general.policy.instalment_loading_pct` and `billing.premium_schedule.loading_pct`.

### New: the perils and loss causes a member can report a loss under (BR-093)
- **`GET /api/v1/portal/general/policies/{id}/loss-codes`** (`PortalGeneralListLossCodes`, member portal; a read of the `CUSTOMER_PORTAL_GENERAL_CLAIMS` screen). It returns `{ perils: [{ code, label }], causes: [{ code, perilCode, label }] }`:
  - the perils in force today of the policy's product line (its class, or its family when it has no class; `general.ref_peril_codes.line`);
  - the loss causes in force of those perils (`general.ref_loss_cause_codes.peril`).

  These are the codes `PortalGeneralNotifyLoss` takes as `perilCode` and `causeCode`. Another member's policy is 404.

### New: a referral reaches someone with the authority to decide it (Ade)
- **A Workflow task for every General referral:** when a quote is referred (by hand, at issue over a zone limit, or by the rules on a broker's portal quote), General publishes `general.GeneralQuoteReferred`. Workflow opens a one-step task (process `GENERAL_REFERRAL`, task type `DECIDE_REFERRAL`, business reference `GENERAL_QUOTE` and the quote's ID), due in one business day. The task is in the queue of the role whose underwriting authority covers the quote's product and total sum insured: the smallest such authority. It is listed by `GET /workflow/tasks?queueCode=` like any task.
- **Decided:** `general.GeneralQuoteReferralDecided` completes the task (state `DECIDED`). Any role whose authority covers the decision may still decide it, from the queue or not.
- **No role covering it:** the task goes to the queue configured for `DECIDE_REFERRAL`, or the default queue.
- **Onboarding readiness `GENERAL_REFERRAL_AUTHORITY`** (step `PRODUCT_FACTORY`) lists each sellable General product that no role's authority may decide referrals for.

### Fixed: a task can be in any role's queue
- `workflow.task.queue_code` (and `TaskView.queueCode`) held 20 characters, while a role code may have up to 80. A referral task or an aggregation review for a role with a longer code could not be written, so the event delivery failed. Migration V0081 widens it to 80.

### New: an instalment loading per product and frequency (Ade)
- **`general.cfg_product_billing_frequencies.instalment_loading_pct`** (optional, per cent, not negative; none means 0). A quote priced in that frequency keeps the loading. Its `instalmentPremium` is the annual premium over the periods per year, times (1 + loading / 100), to the cent. `premium` stays annual.

### Fixed: a General policy's instalments total its annual premium exactly (Ade)
- Billing rounded every instalment on its own, so 3 050.00 a year billed monthly was twelve of 254.17: 3 050.04. A General policy's schedule now keeps the annual premium (`billing.premium_schedule.annual_premium`, migration V0080). The last instalment it generates takes the rounding: eleven of 254.17 and one of 254.13.
- Amended mid-term, the regenerated instalments total their share of the new annual premium. Schedules of a fixed instalment (Life, Health, by hand) are unchanged, and so are schedules created before V0080.

### New: a quote is priced in the frequency chosen, when its product allows it (follow-up to BR-095, Ade)
- **New static data table `general.cfg_product_billing_frequencies`** (CFG, tenant scope, Tier 2; `product_code`, `frequency`), maintained under `/api/v1/general/static/product-billing-frequencies`. It lists the billing frequencies (`billing.ref_billing_frequencies`) each General product allows.
- **`PortalGeneralCreateQuote` and `GeneralCreateQuote`** take an optional `frequency` (`x-code-list: billing.ref_billing_frequencies`). A code that is not a billing frequency in force is 422 `GENERAL_CODE_NOT_IN_FORCE`. One the product does not allow is accepted, and the quote is shown as annual.
- **`GeneralQuoteView` and `GeneralPortalQuoteView`** gain two fields beside `premium`, which stays the annual premium:
  - `premiumFrequency`: the frequency chosen when the product allows it, otherwise `ANNUAL`;
  - `instalmentPremium`: each instalment in that frequency, as Billing will charge it (the annual premium over the frequency's periods per year, to the cent), or the premium itself when annual. It is null until rated, and hidden with the premium on the portal while the quote is Referred or Declined.
- Rating still gives one annual premium; the frequency changes nothing in it. The policy's billing frequency is still chosen when the policy is issued.

### Breaking: a referral is decided under the tenant's underwriting authority (follow-up to BR-095, Ade)
- **`GeneralDecideReferral`** now checks the decider's roles against the tenant's underwriting authority (`rules.cfg_authority_matrices`, as Life's underwriting decisions do). An authority row must be for the quote's product (or all products), cover the quote's total sum insured, and list the decision code among its `decision_types`. Otherwise the decision is refused with 403 `GENERAL_UW_AUTHORITY_EXCEEDED` (new), and the quote stays Referred.
- It applies to every General referral: one a broker's portal quote got from the rules, one the rules raised at issue, and one staff raised by hand. Until now any user holding `general_quote.underwrite` could decide one. **A tenant needs authority rows for its General underwriters before they can decide referrals.**
- Unchanged: whoever referred a quote by hand still cannot decide it (403 `MAKER_CHECKER_SAME_PERSON`), and a rules referral has no referrer.

## Wave 2 batch 6, group A (BR-095, BR-092; shipped ahead, merged at `80d9a3f`)

### Breaking: a broker quotes only on its client's own items (BR-092, decided by Ade)
- **`PortalGeneralCreateQuote`:** each `riskObjectId` must be an item the client owns (`ownerPartyId` is the proposer), and the broker must service the client now (below). Otherwise 403 `OUT_OF_SCOPE`, the same answer as for an unknown ID. Until now a broker could quote on any risk object whose ID it knew.

### New: a broker registers and lists its clients' items (BR-092, decided by Ade)
- **`POST /api/v1/portal/general/risk-objects`** (`PortalGeneralRegisterRiskObject`, function `BROKER_PORTAL_GENERAL_REGISTER_RISK_OBJECT`, `Idempotency-Key`). It takes `clientPartyId` and the staff registration's fields: `objectType`, `attributes`, `valuation`, `valuationBasis`, `valuationCurrency`, `subdivision`, `postalCode`, `riskZone`. It returns 201 with `GeneralPortalRiskObjectView`, whose `ownerPartyId` is the client. A client the broker does not service now is 403 `OUT_OF_SCOPE`. A registration staff linked to an existing party is registered for as that party, as quotes are.
- **`GET /api/v1/portal/general/risk-objects?clientPartyId=`** (`PortalGeneralListRiskObjects`, a `CursorPage`; `clientPartyId` is required, 422 without it) and **`GET /api/v1/portal/general/risk-objects/{id}`** (`PortalGeneralGetRiskObject`), on the new screen function `BROKER_PORTAL_GENERAL_RISK_OBJECTS`. A client the broker does not service now is 404, and so is an item of one.
- **"Services now":** the client's live General policy (Issued or In force) is under one of the broker's codes; with no live policy, the client's latest quote is; with neither, the broker registered the client. When the client's business moves to another intermediary (its policy with the broker ends and another's is issued), the broker loses the items and the new one gains them.
- **The portal view** leaves out the schema version and row version.

### New: a risk object's owner (BR-092, decided by Ade)
- **`GeneralRiskObjectView.ownerPartyId`:** the client the item belongs to, or null when not known. **`GeneralRegisterRiskObject`** takes an optional `ownerPartyId`, which must be a party of the tenant (422 otherwise). **`GeneralListRiskObjects`** filters by `ownerPartyId`.
- **`POST /api/v1/general/risk-objects/{id}/owner`** (`GeneralChangeRiskObjectOwner`, function `GENERAL_CHANGE_RISK_OBJECT_OWNER`, `If-Match`), with `{ "ownerPartyId": "..." }` or null to clear it. It is audited with the owner before and after. The same owner again is 409 `INVALID_STATE_TRANSITION`.
- **Existing items (migration V0077)** have an owner only where every policy they are on has one policyholder and no quote for them names anyone else; the rest stay null, for staff to set.

### Changed: a broker's portal quote is priced as it is made (BR-095, decided by Ade)
- **`PortalGeneralCreateQuote`** still answers 201 with the quote as Draft, and now asks Rating for its premium itself (`x-eventual-effects`). Read it again (`GET /portal/general/quotes/{id}`):
  - **Rated** with `premium`: Rating's annual premium, from the quote's risk objects' attributes (as the rating factors name them) and its total sum insured as `sum_insured`.
  - **Draft** with `ratingIssue` `{ code, reason }`: Rating could not price it, for example `RATING_INPUT_MISSING` when a risk object lacks an attribute the rating reads, or `RATING_INPUTS_DIFFER` (new, General) when two risk objects give the same input different values. Nothing is guessed.
  - **Referred**, with no `premium`: over a zone and peril limit, the rules refer it as soon as it is rated. It reads Rated, with its premium, once an underwriter approves the referral. A rules referral has no referrer, so `referredBy` is null; the decision is made under the tenant's underwriting authority (see above).
- **`GeneralPortalQuoteView.premium`** is null while the quote is Referred or Declined, whatever was rated. **`ratingIssue`** is new on it.
- **The broker never sets or changes the price.** No portal operation takes one, and Rating's calculation endpoints stay closed to portal users (403).
- **Later versions** of such a quote (`GeneralCreateQuote` with `fromQuoteId`) are priced the same way, with no calculation requested. `GeneralQuoteView` gains `pricedOnCreation` and `ratingIssue`. Staff quotes are priced as before, by `POST /rating/calculations`.
- **Events:** General publishes `general.GeneralQuotePricingRequested`, which Rating consumes; Rating publishes `rating.PremiumNotCalculated` when it cannot price one, which General consumes. Migration V0076 adds the quote's `priced_on_creation`, `rating_issue_code` and `rating_issue`.

## BR-094 (branch `br-094`, shipped alone, merged at `e3cb138`)

### Fixed: broker and provider tokens keep their second factor (realm, no API change)
- **The realm's method references** `amr-pwd` and `amr-otp` (`deploy/keycloak/insys-realm.json`) now set `default.reference.maxAge` to 36000 seconds, the SSO session's maximum. Without it Keycloak's AMR mapper used 0 seconds: a method counted only in the second it was used, so a broker's or provider's first token sometimes had no `otp`, and every refreshed token or silent sign-in had none, which INSYS refused with 403 `PORTAL_MFA_REQUIRED`.
- **Environments** take it from the realm export on import. One already imported needs the same two values set (the admin console's authenticator config, or the admin API as the live run and demo did, D-48).
- The API and its contract are unchanged.

## Wave 2 batch 5 (branch `wave2-5`, merged at `99ac395`)

### Breaking: a risk object's valuation requires its currency (BR-033, decided by Ade)
- **`valuationCurrency`** on `GeneralRegisterRiskObject` and `GeneralRevalueRiskObject` is **required with a valuation**, and allowed only with one: a 3-letter code. A valuation without it, or a currency without a valuation, is 422 `VALUATION_CURRENCY_REQUIRED` (new). A missing or negative valuation basis is still 422 `VALUATION_BASIS_REQUIRED`.
- **`GeneralRiskObjectView.valuationCurrency`** is present whenever `valuation` is. Valuations recorded without one took the tenant's base currency (migration V0074).

### Breaking: a claim party's role comes from the tenant's claim party roles (OQ-122, decided by Ade)
- **New static data table `general.cfg_claim_party_roles`** (CFG, tenant scope, Tier 2; `code` and `label`), maintained like any configuration table under `/api/v1/general/static/claim-party-roles`.
- **`GeneralAddClaimParty`:** `role` must be a code in force in that table, otherwise 422 `GENERAL_CODE_NOT_IN_FORCE` (it was any text up to 15 characters). `role` on `GeneralPartyRequest` and `GeneralClaimPartyView` declares `x-code-list: general.cfg_claim_party_roles`.
- **Starter list**, seeded as drafts in each existing tenant for the claims team to confirm (submit and activate) or change: `CLAIMANT`, `INSURED`, `THIRD_PARTY`, `DRIVER`, `WITNESS`, `ASSESSOR`, `REPAIRER`, `LEGAL_REP` (legal representative; a role code is at most 15 characters) and `PAYEE`. Until a tenant has roles in force, adding a claim party is refused.

### New: staff display names for user IDs (BR-021, decided)
- **`POST /api/v1/identity/users/display-names`** (`IdentityLookupDisplayNames`) with `{ "ids": [...] }`, 1 to 100 IDs (422 otherwise), returns `[{ "id", "displayName" }]`:
  - each ID that is a user of the caller's own tenant, active or not, with its display name;
  - a platform operator's ID with the label `Platform operator`, never a personal name;
  - any other ID left out, never an error.
- **No function or permission:** any signed-in staff user may call it. Portal users are refused (403 `EXTERNAL_USER_NOT_ALLOWED`); their views show a role or team.
- It changes nothing and takes no `Idempotency-Key`. Use it to show the people behind `requestedBy`, `decidedBy`, `approvedBy`, `makerId`, `checkerId`, `assigneeId` and the like.

### Riders: audit actors, the policy lifecycle and referrals, and asynchronous effects (BR-023, BR-037, BR-038)
- **BR-023:** `AuditEventView.actorType` is an enum: `USER` (a person: staff, a platform operator or a portal user; `actorId` is their user ID) or `SERVICE` (INSYS itself: a job, an event consumer or an integration).
- **BR-037 (decided by Ade):**
  - **An Issued policy not yet in force** can be cancelled, from inception only: `GeneralCancelPolicy` with `effectiveDate` its period start, otherwise 422 `OUTSIDE_POLICY_PERIOD`. It is never renewed (`GeneralRenewPolicy` 409, as before). Reinstated before its period starts, it is `Issued` again, otherwise `InForce`.
  - **The referrer** of a quote cannot decide its referral: `GeneralDecideReferral` 403 `MAKER_CHECKER_SAME_PERSON`. A referral raised by rules may be decided by any underwriter.
  - **Names:** the domain model's state names are words ("Pending approval"); the contract's are the same words capitalised and joined (`PendingApproval`).
  - **Instruction redirect and hold:** neither changes an instruction's state. A redirect waits as a pending payee, and a hold keeps it `Approved` with `heldReason`. Both are now noted in the model.
  - The operations' descriptions say all this.
- **BR-038:**
  - **`x-eventual-effects`** on `GeneralAppointAssessor` (the loss assessment), `GeneralCreateQuote` and `GeneralDraftEndorsement` (Rated after Rating's `PremiumCalculated`).
  - **`GeneralPolicyView.effects`** (on the policy read): `billingAccountRef`, and the `invoiceIds`, `documentIds` and `cessionIds` made for the policy, each listed as its module's event arrives.

### Breaking: evidence and fraud schemas (BR-039)
- **Recording evidence** (`ClaimsRecordEvidence`) has its own schema, `ClaimsRecordEvidenceRequest`: `evidenceType` and `documentId`, both required (the stored document that is the evidence). Without `documentId` it is 422 `VALIDATION_FAILED` (it was a bare 400).
- **Requesting evidence** (`ClaimsRequestEvidence`) takes `evidenceType` only; its schema no longer has `documentId`, which was ignored.
- **Fraud facts:** `GET /api/v1/claims/fraud-indicators` (`ClaimsListFraudIndicatorFacts`, with the fraud evaluation's function) lists each indicator in force with the `facts` it reads, by the names `ClaimsEvaluateFraud` takes. The condition, score and threshold are not shown. `ClaimsEvaluateFraud`'s description says the body is an object of facts by name; an indicator missing a fact is skipped and recorded with the missing ones.

### Changed: General and claims code fields declare their values or code table (BR-036)
- **Named code tables (`x-code-list`), on the views and the requests:**
  - billing frequency (`billing.ref_billing_frequencies`) and collection method (`billing.ref_collection_methods`);
  - cancellation reason (`general.cfg_cancellation_reasons_and_refund_rules`) and referral reason (`general.ref_referral_reason_codes`);
  - decision codes: a referral's (`general.ref_underwriting_decision_codes`) and a claim's (`claims.ref_claim_decision_reason_codes`);
  - endorsement type (`general.cfg_endorsement_types`) and risk object type (`general.cfg_risk_object_types_and_attribute_schemas`);
  - valuation basis (`general.ref_valuation_bases`) and risk zone (`general.ref_risk_zones`);
  - loss cause (`general.ref_loss_cause_codes`), peril (`general.ref_peril_codes`) and catastrophe event (`general.mst_catastrophe_event_register`);
  - reserve type (`claims.ref_reserve_types`), recovery type (`general.ref_recovery_types`), salvage disposal method (`general.ref_salvage_disposal_methods`) and evidence type (`claims.ref_evidence_types`).
- **Enums:**
  - refund basis: `PRO_RATA`, `SHORT_PERIOD`, `NONE`;
  - cover start: `ISSUE`, `FIRST_PAYMENT`;
  - a claim's domain: `LIFE`, `GENERAL`, `HEALTH`;
  - a financial movement's type: `Reserve`, `Payment`, `Recovery`.
- **`x-known-values`:** a claim communication's `channel` (`EMAIL`, `SMS`, `PUSH`, `WHATSAPP`, the channels INSYS sends by); other channels may be recorded.
- **A claim party's `role`** names `general.cfg_claim_party_roles` (OQ-122, decided; see the breaking change above).

### New: forward links and missing fields on General and claims (BR-035)
- **Links:** `GeneralQuoteView.proposalId` and `orgNodeCode`; `GeneralProposalView.policyId`; `GeneralPolicyView.quoteId` and `proposalId`. The reverse links (a quote's proposal, a proposal's policy, a policy's proposal) are filled on the read by ID and are null on lists and command responses; `orgNodeCode` and the policy's `quoteId` are always there.
- **The claim:** `GeneralClaimView.lossDescription`, `decidedBy` and `decidedAt`; its `settlement` gains `requestedBy` and `bankAccountId` (beside `currency`).
- **A payment instruction:** `ClaimsInstructionView.payeeName` (the payee party's name) and `maskedAccount` (`•••• 2345`, the account's last four; never the number), on its read and on the claim's instruction list.
- **An aggregation alert:** `status`, `EXCEEDED` when the exposure was over the zone's maximum that day, else `ALERT`.
- **Claim financials:** `currency` (BR-033) is null for a claim with no movement yet.

### New and breaking: lists for General and a claim register search (BR-034)
Each is a `CursorPage` (`cursor`, `limit`), by ID, within the caller's product and branch scope (F103).
- **`GET /api/v1/general/risk-objects`** (`GeneralListRiskObjects`): by `objectType`, `riskZone` and `policyId` (the risk objects a policy insures; only a policy in scope).
- **`GET /api/v1/general/proposals`** (`GeneralListProposals`): by `quoteId` and `status`, through their quote's scope.
- **`GET /api/v1/general/policies/{id}/endorsements`** (`GeneralListPolicyEndorsements`): 404 for a policy outside scope.
- **`GET /api/v1/general/assessments`** (`GeneralListAllAssessments`): by `status` and `assessorPartyId`, on claims in scope.
- **Breaking: `GET /api/v1/general/claims/{id}/assessments`** (`GeneralListAssessments`) returns a `CursorPage` instead of a plain array, and is 404 for a claim outside scope (it was an empty or unscoped array).
- **Fixed:** `GeneralGetAssessment` reads an assessment through its claim's scope; one on a claim outside scope is 404 (it was returned).
- **`GET /api/v1/claims/claims`** (`ClaimsSearchRegister`): the claim register across domains, by exact `claimNumber`, `domain` (`LIFE`, `GENERAL`, `HEALTH`), `policyRef`, `registeredFrom` (at or after) and `registeredTo` (before), both instants; `registeredTo` not after `registeredFrom` is 422. `ClaimRegisterSummary`: `id`, `claimNumber`, `domain`, `domainClaimId`, `policyRef`, `registeredAt`, `productCode`.

### New: General and claims amounts carry their currency (BR-033)
- **`currency`** on `GeneralAssessmentView` (with each estimate), `GeneralRecoveryView` (with each salvage item), `GeneralEndorsementView` and the claim's `FinancialsView` (`ClaimsGetFinancials`: the currency of every movement and every outstanding reserve; `outstandingReserves` is unchanged). Assessments, recoveries and endorsements are in their policy's currency.
- **A risk object's valuation** names its own currency: `valuationCurrency` on `GeneralRegisterRiskObject`, `GeneralRevalueRiskObject` and `GeneralRiskObjectView`. It is now required with a valuation (see the breaking change above).

### New: a claim's recoveries (BR-032)
- **`GET /api/v1/general/claims/{id}/recoveries`** (`GeneralListClaimRecoveries`, the recoveries screen): a `CursorPage` of `RecoveryView`, with their salvage, by ID. A claim that does not exist or is outside the caller's product and branch scope is 404.
- **Fixed:** `GeneralGetRecovery` now reads a recovery through its claim's scope (F103): a recovery on a claim outside the caller's scope is 404. It was returned.

### New: the party number (BR-029, decided)
- **`partyNumber`** on `PartyView` and `PartySummary`: `P`, 8 digits and a Luhn (mod 10) check digit over those 8 (for example `P000000018`). INSYS assigns it when a party is registered, in any way (staff, portal or import); it is per tenant, never changed or reused, and holds no personal data. Existing parties are numbered in registration order.
- **Search:** `PartySearchParties` takes `partyNumber`, an exact match (`^P[0-9]{9}$`); a number whose check digit is wrong is 422. It may be the only filter. The search log's entries gain `partyNumberFilter`.

### Breaking: open tasks page with a cursor; a process lists its allowed transitions (BR-028)
- **`GET /api/v1/workflow/tasks`** (`WorkflowListOpenTasks`) returns a `CursorPage` (`items`, `nextCursor`) instead of a plain array, and takes `cursor`. The order is unchanged: earliest due first (then by task ID), and the cursor follows it.
- **`ProcessView.allowedTransitions`** (new, on the read and the list): each `toState` the process may move to from its current state, in the definition version it started on, with `conditional` true when the transition has a guard evaluated on the completion's variables. Empty unless the process is `Running`. Completing a task with a `toState` not listed is refused, 422 `TRANSITION_NOT_DEFINED` (as before).

### Changed: documents declare channels and hold reasons, keep the file name, and say what reading needs (BR-027)
- **`DocumentsDeliver`:** `channel` is an enum (`EMAIL`, `SMS`, `POST`, `PORTAL`, the channels the delivery settings allow) and names `document.cfg_delivery_channel_settings` as its code list: a channel the tenant has not configured is still refused (`CODE_NOT_IN_FORCE`). `DeliveryView.channel` declares the same enum.
- **Legal hold reasons:** `reasonCode` on the document hold, the record hold and their views names `document.ref_legal_hold_reason_codes`.
- **File name:** `DocumentsStore` takes an optional `fileName` (at most 255 characters, no control characters; any path before it, as `C:\fakepath\` from a browser, is dropped). `DocumentView.fileName` returns it; null for generated documents and earlier uploads.
- **What reading needs:** `DocumentView` gains `purposeRequired` (CONFIDENTIAL, PERSONAL and HIGHLY_SENSITIVE: `GET …/content` needs `purpose`, DOC-003) and `allowedRoleRequired` (HIGHLY_SENSITIVE: also one of the category's allowed roles, DOC-004). The `purpose` parameter's description lists the classifications. `categoryCode` names `document.ref_document_categories`.

### Changed: parties mask identifiers, declare their kind and name their code lists (BR-026)
- **`PartyIdentifierView.maskedNumber`** (new): the identifier's last four characters, masked (`•••• 9087`). A number of four characters or fewer is `••••`, so it is never shown whole; the number itself is still never returned (PTY-005). Identifiers added before this change are masked the same way.
- **`PartyView.kind`** is now the `PartyKind` enum (`Individual`, `Organisation`), as on `PartySummary`; the values are unchanged.
- **Code lists:** each Party code field with a static data table names it as `x-code-list`, on the views and the requests:
  - identity document type, contact channel, role, relationship, consent purpose and legal form (`party.*`);
  - country, subdivision and language (`refdata.*`).
  - Marital status adds `x-code-list-filter: list_code=MARITAL_STATUS` (a new extension, for a table holding several lists). Title and gender name the table only.
  - Address type, capture channel and industry code have no table to name (OQ-121).
- **`GET /api/v1/party/search-log`:** `from` and `to` are described as instants (ISO 8601 with an offset): searches at or after `from`, before `to`.

### New: product versions say who submitted them, and a product reads as a whole (BR-024)
- **`ProductVersionView`** gains `submittedBy` and `submittedAt`: who last submitted the version for approval and when (the maker, as `approvedBy` is the checker); null if it was never submitted. On the version read, the versions list and the product read.
- **`GET /api/v1/product-factory/products/{productCode}`** (`ProductGetProduct`, the products screen): `ProductView` with the code, the name, domain, family and class of its latest version, and every version, newest first. An unknown code is 404.
- **Declared values:** `domain` (`LIFE`, `GENERAL`, `HEALTH`), `coverStartsOn` (`Issue`, `FirstPayment`) and `reinstatementArrears` (`None`, `Required`).

### Changed: a Published version not yet Effective can be withdrawn, not retired (BR-030, decided)
- **`ProductWithdrawVersion`** now also accepts a Published version whose effective date has not come: it is withdrawn at once, never comes into force, and keeps its dates (`effectiveTo` in the request is not used for it). `ProductRetireVersion` stays Effective-only (409 otherwise).
- **Refused while referenced:** 409 `PRODUCT_VERSION_REFERENCED` while any quote, proposal or policy references the version, its detail naming each (for example "GENERAL quote Q-000123", "LIFE proposal P-000045", "HEALTH membership M-0001"), at most 50 then "and more".

### Breaking: rating calculations and rule decisions page with a cursor (BR-025)
- **`GET /api/v1/rating/calculations`** (`RatingListCalculations`) and **`GET /api/v1/rules/decisions`** (`RulesListDecisions`) return a `CursorPage` (`items`, `nextCursor`) instead of a plain array, and take `cursor`. Newest first, as before.
- **Declared values:** `sourceDomain` is an enum (`LIFE`, `GENERAL`, `HEALTH`, `PRODUCT`). `subjectType` lists `x-known-values` (Rating: `GENERAL_QUOTE`, `GENERAL_ENDORSEMENT`, `LIFE_PROPOSAL`; Rules: `LIFE_PROPOSAL`, `LIFE_UNDERWRITING_CASE`), as does a decision's `outcome` (`ELIGIBLE`, `REFER`, `INELIGIBLE`, `ACCEPT`, `DECLINE`): other values are allowed, these are the ones INSYS itself uses. A request's `frequency` names its code list.
- **New contract extensions:** `x-known-values` on a schema property (an open field's usual values, as already on parameters), and `x-code-list` (`schema.table`): the static data table a code field takes its codes from.

### Breaking: the case's own legal hold is retired; a case is held by a record hold (decided, Ade)
- **Removed:** `POST /workflow/cases/{id}/legal-hold` (`WorkflowPlaceCaseLegalHold`) and `POST /workflow/cases/{id}/legal-hold/release` (`WorkflowReleaseCaseLegalHold`), and their functions `CASE_PLACE_LEGAL_HOLD` and `CASE_RELEASE_LEGAL_HOLD`. One person could place and release them.
- **Instead:** place a record hold with `scopeType` `CASE` (`POST /documents/legal-holds`, since batch 4); releasing it needs a second person (`LEGAL_HOLD_RELEASE`).
- **Existing holds are kept:** every case still under its own hold now has a record hold on it, placed by the same person at the same time, with reason code `CASE_HOLD` (migration V0068). Nothing held was released.
- **`CaseView.legalHold`** now says whether a legal hold keeps the case: a record hold on it or on one of its documents. It was the case's own flag.

### New: readiness item CASE_TYPE_PROCESS_NOT_IN_FORCE (decided, Ade)
- The `SHARED_OPERATIONAL_SETUP` step gains the check `CASE_TYPE_PROCESS_NOT_IN_FORCE`: every case type in force must name a process in force. `missing` lists each that does not as `caseType:process`. Until now such a case type opened its cases without a process, with only a logged warning. Like every readiness check, it must be ready for the step to complete.

## Portal privacy (branch `portal-privacy`, shipped alone, merged at `8cb4981`)

### Breaking: an insured life and a Health dependant see only their own cover (privacy check, Ade)
- **`PortalLifePolicyView`** (`PortalLifeGetPolicy`, `PortalLifeListPolicies`) gains `viewedAs` (`POLICYHOLDER`, `INSURED_LIFE`, `INTERMEDIARY`). For an `INSURED_LIFE` (an insured life who is not the policyholder), `benefits` lists only the benefits on their own life, each benefit's `premium` is null, `policyholderPartyId` is null and `beneficiaries` null. `premium` and `policyholderPartyId` are now nullable. The policyholder and the broker see the policy as before.
- **Life policy documents** in the portal (`GET /portal/documents?businessRefType=LIFE_POLICY`) are the policyholder's (and the broker's) only: an insured life who is not the policyholder no longer reaches them, as the schedule shows the whole policy.
- **`PortalMembershipView`** (`PortalHealthGetMembership`, `PortalHealthListMemberships`) gains `viewedAs` (`PRINCIPAL`, `DEPENDANT`). For a `DEPENDANT`, `principalPartyId` is null (now nullable) and `dependants` holds only their own entry; the plan and option are shown. The principal sees the membership as before.
- **Unchanged, confirmed:** a dependant's Health claims (list and read) are only those where they are the patient; the principal's and other dependants' claims are 404.

## BR-091 (branch `br-091`, shipped alone, merged at `41e237d`)

### Breaking: `GET /api/v1/portal/life/claims` is a cursor page
- **`PortalLifeListClaims`** returns a `CursorPage` (`items`, `nextCursor`) instead of a plain array, and takes `cursor` (with `limit`) instead of `afterId`. The claims and their order (by ID) are unchanged.

### New: portal lists for members and brokers (BR-091, group A, portal launch)
Every list is scoped only by the caller's link (its party, or its intermediary with its sub-brokers for a firm principal), never by a request parameter; each takes `cursor` and `limit` and returns a `CursorPage` (`items`, `nextCursor`) of portal views. A record outside the link is never listed, and by ID is 404 as one that does not exist.
- **`GET /api/v1/portal/life/policies`** (`PortalLifeListPolicies`; member and broker): `PortalLifePolicyView`. A member's: as policyholder or insured life (beneficiaries shown to the policyholder only, as on the read). A broker's: under its codes.
- **`GET /api/v1/portal/health/memberships`** (`PortalHealthListMemberships`; member): `PortalMembershipView`, as principal or dependant.
- **`GET /api/v1/portal/general/claims`** (`PortalGeneralListClaims`; member and broker): `PortalClaimView`, the claims on the member's policies (as policyholder) or the broker's.
- **`GET /api/v1/portal/party/parties`** (`PortalPartyListClients`) and **`GET /api/v1/portal/party/parties/{id}`** (`PortalPartyGetClient`; broker): `PortalClientView` (`id`, `kind`, `displayName`, `status`; no identifiers, contacts, bank accounts or review status). A client is a party the broker (or a sub-broker) registered, or one that holds, or is quoted or proposed for, a General or Life policy under its codes. A registration staff linked to an existing party after review is shown as that party (its registration ID is then 404); anonymised parties are not shown. The registration's `Location` now resolves.
- **Already there:** `GET /portal/general/policies` (member and broker) and `GET /portal/health/claims` (member and provider).
- **Life claims** page with a cursor too (above).
- **Functions:** the operations join their portal screens (`CUSTOMER_PORTAL_LIFE_POLICIES`, `BROKER_PORTAL_LIFE_POLICIES`, `CUSTOMER_PORTAL_HEALTH_MEMBERSHIPS`, `CUSTOMER_PORTAL_GENERAL_CLAIMS`, `BROKER_PORTAL_GENERAL_CLAIMS`, `BROKER_PORTAL_PARTY_PARTIES`); no new functions.

## Wave 2 batch 4 (branch `wave2-4`, merged at `ef5a85c`)

### New: the default role templates, read only (BR-088)
- **`GET /api/v1/identity/role-templates`** (`IdentityListRoleTemplates`) lists the default role templates (OQ-111): `code`, `name`, `actorType` (`STAFF`, `PLATFORM`, `BROKER`, `CUSTOMER` or `PROVIDER`), `for`, and the number of `screens` and `actions`.
- **`GET /api/v1/identity/role-templates/{code}`** (`IdentityGetRoleTemplate`) gives one with its `subDomains`, `privilegedGrants`, `approves` and `functions`. Each function has its `code`, `kind` (`SCREEN` or `ACTION`), `subDomain`, `parent` (an action's screen), `audiences`, `privileged` and `feature`. An unknown code is 404.
- **`?tenantId=` reads it for a tenant:** each function then says whether that tenant can hold it now (`available`), and if not, why (`unavailableReason`: `FUNCTION_DISABLED` when the tenant switched it off, `FEATURE_NOT_ENTITLED` when the tenant is not entitled to its feature). The body's `tenantId` names the tenant. A tenant may ask for itself; the platform operator for any tenant (403 otherwise; 404 for an unknown tenant). Without `tenantId`, `available` and `unavailableReason` are null.
- **New screen function `IDENTITY_ROLE_TEMPLATES`** (sub-domain `IDENTITY_ACCESS`, staff and platform, permission `user.read`). Of the templates, `TENANT_ADMIN` and `PLATFORM_ADMIN` hold it (the read-only `AUDITOR` sees it too).
- **Existing tenants' administrator roles are left as they are** (decided, Ade). They do not gain `IDENTITY_ROLE_TEMPLATES` by themselves: a tenant's administrators add it to their role as a function grant (`iam.cfg_role_function_assignments`), a Tier 1 change approved by a second person. Until then the templates answer 403 `FUNCTION_NOT_GRANTED` for them. New tenants get it when their first-administrator request is prefilled from `TENANT_ADMIN`.
- The templates are starting points, never seeded. To prefill a new tenant's first-administrator request (`ProvisionTenantAdministrator`), read `TENANT_ADMIN` with that tenant's `tenantId` and take the functions marked `available`.

### Breaking: the first-administrator request refuses functions the tenant cannot hold (BR-088)
- **`POST /identity/tenant-administrators`** now also refuses a function the target tenant has switched off or whose feature it is not entitled to: 422 `VALIDATION_FAILED`, one entry per function under `errors.functions`, for example "`LIFE_ALTER_POLICY: FEATURE_NOT_ENTITLED, the tenant is not entitled to its feature.`" or "`INTEGRATION_DEAD_LETTERS: FUNCTION_DISABLED, the tenant has switched it off.`". Before, such a function was granted and then refused on every use. The template read with the tenant's `tenantId` marks exactly these as not available, by the same rule.

### Fixed: a newly created record's ETag is "1" (create-ETag defect)
- **A create now returns `ETag: "1"`, and `rowVersion: 1` in its body,** the version the record is saved with. Before, a create whose response was built before the save returned `"0"`, so changing the record with that ETag was 412 `PRECONDITION_FAILED`. This affected record legal holds (batch 4) and **GL exports**: a GL export batch acknowledged with the ETag from its create (`POST /finance/gl-exports`, then its If-Match decision) was refused. Please retest GL export acknowledgement. Every create that returns its record is covered, since a new record now starts at version 1.

### Breaking: static data inputs declare their required fields, and four more are required (BR-084, BR-085)
- **Every static data input schema now has a `required` list** (`<Module><Entity>Input`, in `openapi-static-data.json` and `openapi-platform-operator.json`, and the lines of its import). It lists `code`, `changeReason` and every field the table's catalogue entry marks mandatory, which are exactly the fields the API refuses a draft or an import line without (422 `VALIDATION_FAILED`, "`<field>` is required."). Before, no input declared any, so a console reading the contract took every field as optional. For example, `WorkflowCaseTypesInput` requires `caseTypeCode`, `family`, `processCode`, `linkTypes`, `requiresCategory` and `appealRoute` (BR-085). Those were already required; only the contract changed.
- **Four fields are now refused when missing on a draft's create or edit, and on an import line.** Before, the check ran later, or (for the Health plan's product) only at submit:

  | Input | Field | Before |
  |---|---|---|
  | `HealthPlansAndOptionsInput` | `productCode` (BR-084) | Refused only when the draft was submitted (`HEALTH_PLAN_PRODUCT_INVALID`). A draft without a product could be saved. |
  | `IntegrationChannelTransactionPermissionsInput` | `functionCode` | Refused on save, by the table's own rule, with "Required: the function the row allows". Only the message changes. |
  | `IdentitySegregationOfDutiesConflictRulesInput` | `functionA` and `functionB` | Refused on save, by the table's own rule. Only the message changes. |

  The submit check on a Health plan option stays, for drafts saved before this change.
- **Not changed:** requirements that depend on another field stay in each table's rule text, in the create and edit operation descriptions. Examples:
  - a notification template's subject for email and push;
  - a marketing notification type's consent purpose.

  They are not in `required`, because they are not always required.
- **A case type's process need not be in force when the case type is saved.** A case still opens if its type's process is not in force: the case opens without a process and a warning is logged, so a complaint is never lost (`addenda/cases.md` §3). No readiness check covers a case type's process yet, though the code's comment says one does. That gap is in the batch 4 report.
- **The catalogue matches** (`docs/specs/static-data-catalogue*.json`):
  - 35 fields that later migrations added are now listed;
  - `plans-and-options.product_code` is mandatory;
  - five retired fields are no longer mandatory: `channel-transaction-permissions.transaction_type`, `roles-and-permission-sets.permissions`, and `segregation-of-duties-conflict-rules.permission_a` and `permission_b`. They must be empty (ADR-026).

  The batch report lists every field.

### New: legal holds on a party, policy, membership, claim or case (D-34)
- **`POST /documents/legal-holds`** (`DocumentsPlaceRecordHold`, Idempotency-Key) `{scopeType, scopeId, reasonCode}` returns **201** with the hold and its ETag.
  - `scopeType` is one of `PARTY`, `LIFE_POLICY`, `GENERAL_POLICY`, `HEALTH_MEMBERSHIP`, `LIFE_CLAIM`, `GENERAL_CLAIM`, `HEALTH_CLAIM` or `CASE`. A payment is held through its policy or claim.
  - The record must exist (404) and be in the caller's scope (403 `OUT_OF_SCOPE`, F103), and the reason must be in force.
- **`GET /documents/legal-holds?scopeType=&scopeId=&inForce=&cursor=&limit=`** (`DocumentsListRecordHolds`) and **`GET /documents/legal-holds/{id}`** (`DocumentsGetRecordHold`, with ETag). Holds on records outside the caller's scope are left out.
- **`POST /documents/legal-holds/{id}/release`** (`DocumentsReleaseRecordHold`, **If-Match**) `{reason}` returns **202**. As for a document's hold, the release waits for a `LEGAL_HOLD_RELEASE` approval (new business reference type `RECORD_LEGAL_HOLD`), and the hold stays in force until it is approved.
- **What a hold does while in force:**
  - the record's documents are not disposed of (409 `DOCUMENT_ON_LEGAL_HOLD`);
  - a held case is not disposed of after its retention;
  - the party it is on, or whose policy, membership or claim it is on, is not anonymised (409 `PARTY_ANONYMISATION_BLOCKED`, a `LEGAL_HOLD` block).
- **Functions:** placing, listing and reading are `DOCUMENTS_PLACE_LEGAL_HOLD`; releasing is `DOCUMENTS_RELEASE_LEGAL_HOLD`. These are the existing privileged functions, so the approved privileged list is unchanged.

### Breaking: If-Match on General, claims, billing, distribution, Life and Health changes (BR-031, BR-057, BR-064, BR-071)
- **If-Match on every change to an existing record:** each read of a record that has a `rowVersion` now returns it as the **ETag**, and so does each change that returns the record. 88 operations now require **If-Match**, 428 without it and 412 when stale:
  - every General quote, proposal, policy, endorsement, claim, assessment and recovery action, and the risk object's changes;
  - the claim payment instruction's submit, release, retry and redirect;
  - `BillingAmendSchedule`, `BillingAgeAccount` and `BillingCloseAccount`;
  - `DistributionClawBack`;
  - every Life proposal, underwriting case, policy, claim and group scheme action;
  - every Health scheme, membership, preauthorisation, medical claim, utilisation case, fraud case and payment run action.

  The contract lists them as the operations with a required `If-Match` header.
- **New Idempotency-Key requirements:** the claims framework's creates (`ClaimsAllocateNumber`, communications, evidence requests, evidence, fraud evaluations, reserve movements, payment instruction drafts) and `DistributionPrepareStatement`. A repeat with the same key returns the first response.
- **`rowVersion` added** to `ClaimsFrameworkInstructionView` and `GeneralProposalView`.
- `HealthCheckEligibility` changes nothing, so it takes neither header.

### Fixed: a reason or code that is too long is a 422, not a 500 (BR-073)
- **Too long or empty:** any reason, code or other text longer than INSYS stores, or empty where one is required, now answers **422 `VALIDATION_FAILED`**, with `errors` naming the field (`{"reason": ["reason must be at most 30 characters."]}`). Before, it was a 500. This covers every such value, not only the six the front end found.
- **The limits are the stored lengths:**
  - a membership event's reason, and a preauthorisation's or medical claim's reject reason: 30 characters;
  - an approve `reasonCode`: 20;
  - a utilisation case's `ruleCode`: 15, and its outcome: 20;
  - a fraud case's outcome code: 15.

### Added: BR-008, BR-017 and BR-022 have tests of their own
No contract change:
- BR-008: the realm's MFA flows are held to the integration guide §5;
- BR-017: every dead letter's error comes from a type name, a status or a fixed sentence, and an architecture test refuses an exception's message there;
- BR-022: `/identity/me`'s `locale` has a test of its own.

### Breaking: a static data import is approved as one change (BR-016)
- **The import response** `StaticDataImportResult` gains `batchId`. Every imported draft carries `importBatchId` (a new field on every static data record), and is submitted only with the rest of its import: submitting one alone is 409 `INVALID_STATE_TRANSITION`.
- **New on every static data table:**
  - `GET …/imports/{batchId}` (`Get<Table>Import`): every line, in code order, for the maker and the checker.
  - `POST …/imports/{batchId}/submit` (`Change<Table>Import`): one change request (action `IMPORT`) and one approval of the table's tier. It returns `{batchId, changeRequestId, lines, hash}`.
  - `POST …/imports/{batchId}/activate`: once approved, every line is put in force together.
- **Approved or rejected as a whole, never partly.** A rejection, expiry or withdrawal returns every line to draft, and the corrected import is submitted again as a new change.
- **The approval is bound to the SHA-256 of the complete import.** If a line changed after submission, the approval is not applied: it parks (`APPROVAL_NOT_APPLIED`) and no line is approved.
- **Change requests:** `ChangeRequestView.action` can be `IMPORT`; its `after` names `batchId`, `lineCount` and `hash`, with the table's routes.

## BR-087 (branch `br-087`, merged at `24717de`)

### Fixed: static data and product lists no longer skip a row at a page boundary (BR-087)
- **Every static data list** (`GET /api/v1/{module}/static/{entity}`) and **the product list** (`GET /api/v1/product-factory/products`) now compare their cursor and order their codes bytewise (PostgreSQL `COLLATE "C"`). Before, the database ordered codes by its own collation (en_US, which ignores underscores) while the service re-sorted them ordinally, so a code that fell on a page boundary was never returned; in the 687-function catalogue at 200 a page, `SD_BENEFIT_MANAGEMENT_MAINTAIN` was one.
- **Static data and product lists now come back in byte order** (`PGB` before `PG_A`): digits and upper-case letters sort before `_`, as an ordinal comparison sorts them. Before, the order followed the server's collation (`PG_A` before `PGB`). A client that shows the list in the order returned will see codes with underscores move after their neighbours without one. Cursors issued before the change still decode and continue from the code they name.
- No other list was affected: every other cursor-paged list pages on its record's UUID, compared and ordered in SQL.

## Wave 2 batch 3 (branch `wave2-3`, merged at `1416683`)

### Breaking: a tenant's administrators (BR-013, BR-018)
- **`POST /identity/tenant-administrators`** body is now `{tenantId, administrators: [two people], functions}`, each person `{username, displayName, idpCode, externalSubject | email, mfaEnrolled}`. A new tenant gets exactly two administrators (422 `VALIDATION_FAILED` on `administrators` otherwise; the usernames must differ).
- **`TenantAdministratorRequestView`** loses the single person's fields (`username`, `displayName`, `idpCode`, `externalSubject`, `mfaEnrolled`, `userId`) and gains `kind` (`FirstAdministrators`, `AdditionalAdministrator`) and `administrators: [{username, displayName, idpCode, externalSubject, mfaEnrolled, userId, identityState, identityReason}]`.
- **`status`** is now `PendingApproval`, `AwaitingIdentity`, `IdentityParked`, `IdentityFailed`, `Provisioned` or `Rejected`. Provisioned means every administrator's identity is linked and usable (BR-018). `identityState` per person is `Pending`, `Parked`, `Failed` or `Linked`; `identityReason` gives the parking code and item for Parked or Failed (for example `KEYCLOAK_USER_CONFLICT username`).
- **New:** `POST /identity/tenant-administrators/additional` `{tenantId, administrator}` (Idempotency-Key): one more administrator for an existing Active or Onboarding tenant, approved by a second platform administrator. `POST /identity/tenant-administrators/{id}/retry-identity`: after the cause is fixed, retries the parked or failed identities; `{retried}`; 409 when none is parked or failed.
- **Readiness:** `ORGANISATION_AND_ACCESS` / `FIRST_ADMINISTRATOR` needs two active administrators (identity linked, signed in once); its `missing` reads `TENANT_ADMIN: n of 2 active`.
- **The administrators' approval authority:** once the first administrators exist, the tenant has `TENANT_ADMIN` rows in `workflow/static/delegation-and-approval-authority` for `PRIVILEGED_ROLE`, `EXTERNAL_SUBJECT_LINK`, `STATIC_DATA_T1` and `STATIC_DATA_T2`, so its administrators approve each other's changes from the start (OQ-119, for confirmation). They are the tenant's own rows, changed like any other.
- **New error code `IDENTITY_NOT_LINKED`** (403, with `Retry-After`): the token is for a user of the tenant whose identity is not linked yet; retry the same call. A console treats it as "still setting up", not as a refusal.

### Breaking: releasing a legal hold needs a second person (OQ-111)
- **`POST /documents/{id}/legal-holds/{holdId}/release`** and **`POST /party/search-log/holds/{id}/release`** now take a body `{reason}` (required, at most 200 characters) and return **202** with the hold: the release is a request, approved in Workflow as a `LEGAL_HOLD_RELEASE` approval (20 approval kinds now; business reference types `DOCUMENT_LEGAL_HOLD` and `SEARCH_LOG_HOLD`, 21). The hold stays in force until it is approved; a rejection, expiry or withdrawal leaves it in force and clears the request. The compliance officer template approves it.
- **Views:** the document legal hold and the search log hold gain `releaseRequestedBy`, `releaseRequestedAt`, `releaseReason` and `latestRejection` (the document hold also `releasedBy`); always present, null when they have no value. A second release request while one waits is 409.

### Breaking: the 20 approved privileged functions are flagged
- `SUSPEND_TENANT`, `CHANGE_ISOLATION_PROFILE`, `ASSIGN_ROLE`, `REVOKE_ROLE`, `SD_IDENTITY_AND_ACCESS_MANAGEMENT_MAINTAIN`, `SD_IDENTITY_AND_ACCESS_MANAGEMENT_APPROVE`, `PARTY_ANONYMISE`, `PARTY_MERGE_PARTIES`, `PARTY_PLACE_SEARCH_LOG_HOLD`, `PARTY_RELEASE_SEARCH_LOG_HOLD`, `DOCUMENTS_DISPOSE`, `DOCUMENTS_PLACE_LEGAL_HOLD`, `DOCUMENTS_RELEASE_LEGAL_HOLD`, `DISCARD_PARKED_DELIVERY`, `DISCARD_MESSAGE`, `PAYMENTS_ISSUE_CALLBACK_ENDPOINT`, `PAYMENTS_REVOKE_CALLBACK_ENDPOINT`, `COMMUNICATIONS_ISSUE_CALLBACK_ENDPOINT`, `COMMUNICATIONS_REVOKE_CALLBACK_ENDPOINT`, `PAYMENTS_REVERSE` (`privileged: true` in the catalogue, `addenda/role-templates.md`). Assigning a role that holds one is a `PRIVILEGED_ROLE` approval decided by another administrator of the tenant, and the user needs MFA. The tenant administrator role is now privileged; a tenant has two administrators from the start (BR-013).

### Fixed: dates are the tenant's business date (BR-040, GAP-038, ADR-025)
- Assigning, revoking and listing roles, a user's roles in effect (`/identity/me`, the user views), permissions, the menu, function availability, language settings, error texts, webhook subscriptions, Life and General claim notification checks, and Finance's posting dates now use the tenant's business date, not the UTC date. Between 00:00 and 02:00 in Johannesburg (00:00 and 03:00 for a UTC+3 tenant) a record effective "today" is now in force, where it was refused before. Permissions resolved before the tenant's midnight are not used after it, so a role ending that day stops at midnight in the tenant's time zone rather than up to 30 seconds later. Timestamps (`…At` fields) stay UTC instants. No field or endpoint changes.

### Breaking: anonymising a party checks its retention (D-34, OQ-120)
- `POST /party/parties/{id}/anonymise` (`PartyAnonymise`) now refuses with **409 `PARTY_ANONYMISATION_BLOCKED`** while anything still keeps the party's data. The problem carries a new member, **`blocks`** (documented on `ProblemDetails`). Each item has:
  - `reason`, one of:
    - `OPEN`: a policy, membership, claim or payment that has not reached a final status;
    - `IN_RETENTION`: inside the tenant's retention period, with `until`, the first business date it no longer blocks. The period runs from the business date the record reached its final status, from its status history. The tenant's record retention policies set it; the working default is 7 years (OQ-120);
    - `LEGAL_HOLD`: a hold on the record or one of its documents, with `holdId` and `documentId`;
    - `NO_RETENTION_POLICY`: the tenant has no period in force for the record's kind.
  - `recordType`: `LIFE_POLICY`, `LIFE_CLAIM`, `GENERAL_POLICY`, `GENERAL_CLAIM`, `HEALTH_MEMBERSHIP`, `HEALTH_CLAIM`, `PAYMENT`, `AUDIT_EVIDENCE`, or `PARTY` (a hold on one of the party's own documents). The retention kinds: the policy and membership types are `POLICY`, the claim types `CLAIM`, and `PAYMENT` is `PAYMENT`.
  - `recordId`: for `AUDIT_EVIDENCE` and `PARTY`, the party.
  - `retention`: `POLICY`, `CLAIM` or `PAYMENT` (the record retention policies), or `AUDIT` (the longest audit class, for `AUDIT_EVIDENCE`).
- Records of a party merged into this one count.
- **Added:** static data `party.cfg_record_retention_policies`, maintained through the generated static data endpoints (`/party/static/record-retention-policies`). It holds `recordKind` (`POLICY`, `CLAIM`, `PAYMENT`), `jurisdiction` (a country code; where several apply the longest period wins), `period` (an ISO 8601 duration such as `P7Y`) and `basis` (free text, the legal basis). The platform rows (`tenantId` null, read at `…/global`) are the 7-year working default. A tenant's own rows replace them for that kind.
- **Changed, every static data document:** every duration field (`period` and similar) now says it is an ISO 8601 duration.
- `until`, `holdId`, `documentId` and `retention` are null when they do not apply to the block's reason.
- A party that is not Deceased or Dormant is still refused with 409 `INVALID_STATE_TRANSITION`, now before its records are read.
- Erasure for a living party is not this operation (Wave 6 data-subject requests).

### Breaking: resolving an Unknown payment as succeeded needs a matched statement line or a second person (BR-042)
- `POST /payments/payments/{id}/resolve` (`PaymentsResolveUnknown`) with `succeeded: false` is unchanged: 200, one person's decision.
- With `succeeded: true`, it depends on whether a bank statement line is matched to the payment (by reference and amount, `POST /payments/bank-statements`):
  - **matched:** decided at once, 200; the evidence records the line;
  - **not matched:** **202**, and the payment stays `Unknown`, with a new `resolutionRequest` (`requestedBy`, `requestedAt`, `evidence`). The `UNKNOWN_PAYMENT_RESOLUTION` approval (business reference `PAYMENT`, which opens the payments screen) appears in the Workflow inbox.
    - The requester cannot approve it (`MAKER_CHECKER_SAME_PERSON`).
    - Granted: the payment becomes `Succeeded`.
    - Rejected, expired or withdrawn: the request is cleared, and the payment stays `Unknown`.
- **409 `PAYMENT_RESOLUTION_PENDING`** for any resolve while a request waits.
- `PaymentView.resolutionRequest` is null when nothing waits. The approval and reference type enums gain `UNKNOWN_PAYMENT_RESOLUTION` and `PAYMENT`.
- The finance manager template approves it.

### Fixed: a payout batch is released only as approved
- `POST /payments/payout-batches/{id}/release` (`PaymentsReleaseBatch`) is refused with **409 `PAYOUT_BATCH_CHANGED`** when the batch's payments are not exactly those its release approval covered. That means a payment added or removed, or one with another amount, currency, payee or bank account.
- The release approval is bound to the batch (its subject hash), and a grant for another subject is not applied.
- No API changes a built batch, so this is defence in depth; nothing a client does today changes.

### Added: front-end requests BR-011, BR-012, BR-014, BR-015, BR-019, BR-020, BR-022
- **Parked deliveries for the operator (BR-011):** a PLATFORM screen function `PLATFORM_PARKED_DELIVERIES` for `ListParkedDeliveriesByTenant`.
- **A tenant's own readiness (BR-012):** `GET /tenancy/readiness` (`GetOwnTenantReadiness`, `tenant.read`), the same view as the operator's `GetTenantReadiness`, for the signed-in user's tenant; STAFF screen `TENANT_ONBOARDING_READINESS`, route key `tenancy/readiness`.
- **Governance settings concurrency (BR-014):** the governance settings GET returns an ETag (the row version in double quotes) and the Tier 2 maker-checker PUT requires `If-Match` (428 without it, 412 when stale).
- **A change request links to its version (BR-015):** `ChangeRequestView` gains `versionNumber` and `entityRoute`; a pending code's versions are at `/api/v1/{entityRoute}/{record.code}/versions` (global routes end in `/global`).
- **Role function grants (BR-019):** creating or importing an `iam/static/role-function-assignments` row with a function code not in the catalogue, or a JOB function, is 422 `VALIDATION_FAILED` on the function field.
- **Labels (BR-020):** static data functions are labelled from their catalogue module ("Billing and premium receivables static data", "Maintain …", "Approve …"); the readiness views' steps gain `label` ("Organisation and access"). Operations that return labels: `GetMyMenu`, the readiness views, the communication preference views, and every static data record's `labels`.
- **Locale (BR-022):** `MeView` gains `locale {tag, country, baseCurrency}`, always present: the request's language with the tenant's country (`en-ZA`) and its base currency, from the locale defaults in force; null fields while the tenant has none.

### From the API review
- `IDENTITY_NOT_LINKED` is listed among the 403 codes any operation may return, and every 403 documents `Retry-After` (sent only with that code).
- The legal hold release `reason` is 1 to 200 characters; both release operations name their 409 (`INVALID_STATE_TRANSITION`: a release already waits, or the hold is released); the hold views say how a pending release reads.
- `administrators` is exactly two items and `functions` at least one in the schema; an administrator's `idpCode` points to the identity provider configurations; the person input no longer shows a `details` property; `identityState` and `userId` are null until approval, and `identityReason` is for display.
- `retry-identity` takes no body and names its 409 (`INVALID_STATE_TRANSITION`). `MeView.locale` is in `required`.

## Wave 2 batch 2 (branch `wave2-2`, merged)

### Breaking
- **`payments/static/payment-release-limits` is removed** (OQ-112, V0059): nothing read the table. Release limits are the approval authority in `workflow/static/delegation-and-approval-authority` (by approval type and currency).

### Added: front-end requests BR-001, BR-002, BR-004, BR-005, BR-008
- **Approve with a reason (BR-001):** `POST /workflow/approvals/{id}/approve` takes an optional body `{reason}` (at most 2000 characters), kept as the approval's `decisionReason` as a rejection's is. No body is still valid.
- **My authority (BR-002):** `GET /workflow/approvals/my-authority` (`approval.read`): `[{approvalType, roleCode, limit, currency}]` for the signed-in user's roles today; `currency` is the row's, or the tenant's base currency. Life claims' authority is per claim, so use the decidable filter for them.
- **Decidable approvals (BR-004):** `GET /workflow/approvals?decidable=true`: only approvals the signed-in user can decide now (waiting, someone else's, in scope, with any permission the requester named, within their authority for the type and amount), with the same cursor paging; `status` is ignored with it.
- **Approval kinds (BR-005):** `approvalType` (on `ApprovalView`, `latestRejection` and `my-authority`) and `businessRefType` (on `ApprovalView`) are enums of the declared values (19 with the cases' `CASE_REDRESS`). `ApprovalView.screen` names the SCREEN function (its code) that opens the record, for example `PAYMENT_PAYOUT_BATCHES`; the console maps it to its route. An approval of an undeclared kind is refused by Workflow.
- **Approval status `NotApplied`** (D-5): a granted approval its record would not take; `notAppliedBy` (the module) and `notAppliedAt` are set while it is. It returns to `Approved` if a retry applies it. The requester gets an `APPROVAL_FOLLOW_UP` task; the delivery parks as `APPROVAL_NOT_APPLIED`.
- **Always present:** `ApprovalView.screen`, `notAppliedBy` and `notAppliedAt`, and `UserView.externalSubjectLink` are in every response, null when they have no value; the approve body's `reason` has `maxLength` 2000.
- **External-subject link on the user view (BR-010):** `UserView.externalSubjectLink` `{id, kind, key, status, approvalId}`: the pending or active link, else the latest, or null. `key` is the party ID, intermediary code or provider ID by `kind`; `approvalId` is the Workflow approval while the link is `Pending`.
- **MFA (BR-008):** integration guide §5; no contract change.
- **If-Match for a listed version (BR-014):** every `…/{code}/versions` operation states the format: the version's `rowVersion` in double quotes (`If-Match: "3"`), the entity tag every ETag header uses. No behaviour change.
- **Dead letter error text (BR-017):** `GetDeadLetter` states that `error` is the exception's type, `HTTP <status>` or a fixed sentence, never the message body or personal data. No behaviour change.

### Added: cases (Wave 2, `addenda/cases.md`, signed off)
- **Staff, `/workflow/cases`:** list (filters `family`, `type`, `status`, `category`, `overdue`, `linkType`, `linkId`; cursor paging) and get by ID (ETag); `POST /` opens a case (Idempotency-Key); `POST /{id}/…` with If-Match: `acknowledge`, `messages`, `links`, `extend`, `escalate`, `start`, `await-complainant`, `resume`, `decide`, `close`, `withdraw`, `reopen`, `ombud-referral`, `ombud-outcome`, `share-with-broker` (OQ-108), `legal-hold`, `legal-hold/release`; `POST /{id}/redress` (Idempotency-Key) and `GET /{id}/redress/{redressId}`; `GET /summary` (dashboard figures `CASES_OPEN`, `CASES_OVERDUE`, `COMPLAINTS_RECEIVED`, `COMPLAINTS_UPHELD`, `COMPLAINTS_REJECTED`, `COMPLAINTS_REFERRED_TO_OMBUD`, `REDRESS_PAID`); `GET /complaints-report?from&to` (PPR 18.8.3 figures per category). The operator tenant is refused.
- **Portal, `/portal/cases`:** list and get (the member's own, the provider's own, the broker's own or shared), raise (Idempotency-Key), `messages`, `appeal` (Idempotency-Key).
- **Rules:** deciding an appeal needs the privileged function `CASE_DECIDE_APPEAL` (403 `FUNCTION_NOT_GRANTED`) and another person than the one who decided the case appealed (409 `CASE_DECIDER_SAME_PERSON`); `CASE_REASONS_REQUIRED`, `CASE_LINK_NOT_ALLOWED`, `CASE_CLOSED`, `CASE_CATEGORY_REQUIRED`, `CASE_TIMELINE_NOT_EXTENDABLE`. A case linked to Health records is read only with `medical_claim.read`.
- **Redress (OQ-104):** an approval of type `CASE_REDRESS` (19 approval kinds now); approved, Payments pays it with purpose `CASE_REDRESS`; a payee with no verified account in the currency parks the payout (`PAYEE_ACCOUNT_NOT_PAYABLE`).
- **Static data:** `workflow/static/case-types` (with `appealRoute` `PPR_NFO` or `CMS`, OQ-101), `case-timelines`, `case-categories` (the nine PPR categories as global rows), `case-reportability`, `ombud-schemes`, `case-retention` (5 years when none is in force, OQ-106).
- **Notices:** notification types `CASE_ACKNOWLEDGED`, `CASE_UPDATE`, `CASE_DECISION`, `CLAIM_DECISION_RIGHTS` (service notices; tenant configuration). Readiness `SHARED_OPERATIONAL_SETUP` gains the case checks (OQ-110).

- **From the API review:** the case view gains `currentStep` (`ACKNOWLEDGE`, `RESOLVE`, `REVIEW`; null once decided or closed) and `extensions` (how many times its timeline was extended); request fields `outcome`, `redressType`, `linkType` and the history's `eventType` are enums; the list's filters list their known values; every case change states `409 CASE_CLOSED` and the If-Match format; durations are `format: duration`; `POST /portal/cases/{id}/messages` requires `Idempotency-Key`; another user's case is 404 in the portal.

### Added: claim outcomes and reopening (OQ-102, OQ-103)
- **Views:** General claims gain `outcome` (`APPROVED`, `PARTLY_APPROVED`, `DECLINED`) and `settlement.claimedAmount` (in the settlement's currency); Life claims gain `outcome` and `claimedAmount` (in the claim's `currency`); Health medical claims gain `outcome` and `decisionReasonCode`. These fields are always present, null until decided. Health claim lines' `outcome` is an enum (`PAY`, `PEND`, `REJECT`). A settlement or approval below the amount claimed needs a decision reason of type PARTIAL (422 otherwise) and sends `CLAIM_DECISION_RIGHTS`. Health's approve takes an optional body `{reasonCode}`; a Health partial approval is an adjudicated claim with declined lines.
- **Reopening:** a dispute or appeal decided upheld or partly upheld reopens its linked claims: General (declined or closed), Life (declined, or closed and never paid) and Health (rejected) go back to assessment.

### Contract metadata (Wave 2 batch 1's API review)
- **`latestRejection.ending` is an enum:** `REJECTED`, `EXPIRED`, `WITHDRAWN`.
- **`latestRejection` is required** on every view that has it: always present, null when the record's approvals never ended without being granted.
- **`expiresAfter`** (`workflow/static/approval-expiry`) is `format: duration`, with its rule (at least one hour) and the 14-day default; **`currency`** on authority limits is described (empty: the tenant's base currency).
- **Descriptions:** the first-administrator request states the `displayName` rule (BR-006); the list of each tenant-named static table says a row naming a tenant is that tenant's only (BR-007).

## Wave 2 batch 1 (branch `wave2-1`, merged)

### Contract metadata
- **`DashboardFigure.code` is an enum** of every figure code the summaries return: `WRITTEN_PREMIUM`, `POLICIES_WRITTEN`, `CLAIMS_INCURRED`, `CLAIMS_REPORTED`, `OPEN_ARREARS`, `PAYMENTS_UNKNOWN`, `RETURNS_BY_STATUS`, `RETURNS_AWAITING_ACTION`, `RETURNS_OVERDUE`, `DELIVERIES_PARKED`, `DELIVERIES_FAILING`, `DEAD_LETTERS_OPEN`, `WEBHOOK_DELIVERIES_FAILING`, `OLDEST_PARKED_AT`, `JOB_FUNCTIONS_OFF`.
- **Months are `yyyy-MM`:** `fromMonth` and `toMonth` of `GET /analytics/claims-incurred`, and `monthKey` in its rows, have the pattern `^[0-9]{4}-(0[1-9]|1[0-2])$`.

### Approval endings (D-1 to D-5): every way an approval ends leaves its record in a defined state
- **New endpoint:** `POST /api/v1/workflow/approvals/{id}/withdraw` `{reason}` (If-Match), by the person who asked for the approval only (403 `APPROVAL_NOT_REQUESTER`). The record is put back as for a rejection.
- **Expiry:** an approval with no decision within its time limit expires; the time limit is per approval type in the new static table `workflow/static/approval-expiry` (`approvalType`, `expiresAfter` as an ISO 8601 duration; 14 days when none is in force). Expiry and withdrawal reach the record's module like a rejection, and the requester gets a follow-up task (process `APPROVAL_FOLLOW_UP`, in their `GET /workflow/tasks?assigneeId=`).
- **`ApprovalView`:** `status` can be `Expired` or `Withdrawn` with `decisionReason` set (why it expired, or the withdrawal reason) and `decidedAt`.
- **`latestRejection`** `{ending: REJECTED | EXPIRED | WITHDRAWN, approvalType, by, at, reason}` on every record an approval is about (OQ-113): refund request, commission statement, claim payment instruction, provider payment run, accounting periods (in the list), payout batch, General endorsement and claim, Life underwriting case and claim, product version, static data change request, bank accounts, role assignments that need approval, subject link, first-administrator request. `by` is the checker; null when it expired or was withdrawn.
- **Statuses:** `PayoutBatch.status` and `ProviderPaymentRun.status` gain `Rejected`. A rejected batch's payments return to the pool for a new batch; a rejected run's claims are released for a new run.
- **Bank accounts (D-3):** adding an account no longer supersedes the party's active account in that currency; the active one stays active until the new one is verified (then it is `Superseded`). A rejected, expired or withdrawn verification leaves the previous account active.
- **Commission statements:** a statement whose approval does not end in approval goes back to `Draft` and releases its entitlements and recoveries for the next statement.
- **Parking (D-5):** an ending a module cannot apply parks with the code `APPROVAL_ENDING_NOT_APPLIED` (item: approval type and ID), shown in the operations view and `/health`, escalated and retried.
- **Authority (OQ-112):** `workflow/static/delegation-and-approval-authority` gains `currency` (empty: the tenant's base currency). A limit counts only for approvals in its currency.
- **Error codes:** `APPROVAL_NOT_REQUESTER` (403) is new.

### Breaking: front-end requests BR-007 and BR-006
- **Tenant-named platform rows are the tenant's only (BR-007).** A static data row maintained by the platform that names a tenant (its `tenant` column) is read only by that tenant: list, by code and versions. Another tenant's rows are not listed, and reading one by code is 404. The platform operator still reads every tenant's rows through `…/global/{code}/versions`. The tables: `tenancy/static/feature-entitlements`, `tenancy/static/isolation-profile-assignment`, `tenancy/static/operational-limits`, and the tenant-named rows of `product/static/product-approval-workflow-settings`.
- **A display name Keycloak refuses is a 422 (BR-006).** `POST /identity/users` and the first-administrator request, for INSYS's own realm (`idpCode` `KEYCLOAK`), refuse a `displayName` with any of `< > & " $ % ! # ? § ; * ~ / \ | ^ = [ ] { } ( )` or a control character, with a field error on `displayName`. It used to be accepted and fail in Keycloak.
- **Validation error keys are the field as the request body spells it (breaking; merged in `77d377a`).** `errors` in a 422 is keyed by the field in the JSON serializer's own camel case, segment by segment: `displayName`, `lines[0].amount` for a nested field, and `vatNumber` for a property named `VATNumber` (leading capitals are lowered together, as the serializer does). Validators' keys were the C# property names (`DisplayName`, `VATNumber`); keys written by hand (`defaultLanguage`) were already camel case. A client mapping field errors to its form fields should use the keys exactly as they come back.
- **Parking:** Keycloak refusing a user for a reason retrying cannot fix parks as `KEYCLOAK_USER_REFUSED` with Keycloak's reason, in the operations view and `/health`, not as `KEYCLOAK_UNAVAILABLE`.

### Changed behaviour
- **Analytics' rebuild by business date runs by itself** once per tenant after the release (recorded in `analytics.release_task`); no manual step.

## Wave 1 batch 4 (merged at `9cc94c3`): function catalogue decisions; business dates

### Breaking
- **`POST /api/v1/identity/tenant-administrators` names functions** (OQ-099): `functions` replaces `permissions`, and the request view returns `functions`. Each must be a staff function in force in the catalogue, else 422 `VALIDATION_FAILED` on `functions` ("Not a staff function in force: …"). The tenant's TENANT_ADMIN role is granted exactly those functions.

- **Switching off a tenant's Billing or Payments daily job needs a privileged function:** submitting such a version is refused with 403 `FUNCTION_NOT_GRANTED` unless the maker holds `TENANT_SWITCH_OFF_MONEY_JOBS` (below).

### Changed behaviour
- **Switching off a tenant's Billing or Payments daily job is privileged** (OQ-098): submitting a `tenancy/static/tenant-function-availability` version with `enabled: false` for `BILLING_DAILY_JOBS` or `PAYMENTS_DAILY_JOBS` needs the privileged function `TENANT_SWITCH_OFF_MONEY_JOBS` (403 `FUNCTION_NOT_GRANTED` otherwise). Holding it makes a role privileged (approval and MFA to assign).
- **Analytics dates by the tenant's business date** (ADR-025): `GET /analytics/written-premium` and `/analytics/summary` count a policy on the tenant's date when it was issued, endorsed or cancelled, and `/analytics/claims-incurred` counts a registered claim in that month; before, an event between 00:00 and 02:00 Johannesburg time counted on the day before. Each tenant's projections are rebuilt once after the release (`POST /analytics/projections/rebuild`).
- **Switched-off JOB functions are shown** (OQ-098): `GET /integration/deliveries/summary` gains the figure `JOB_FUNCTIONS_OFF` (count, by function code); `/health` (operations, outside the documented API) gains the check `job-functions`, naming each tenant's switched-off JOB functions (Healthy: it is the tenant's choice). The rule is checked when the version is submitted; it is stated in the table's create and edit operations.

## Wave 1 batch 3 (merged at `5583285`): the function catalogue (ADR-026) and Communications follow-ups

### Breaking
- **`POST /api/v1/payments/callback-endpoints` needs `Idempotency-Key`**, as Communications' already does. Without it: 422 `IDEMPOTENCY_KEY_REQUIRED`.
- **A repeat of either callback-endpoint issue (Payments, Communications) with the same key is refused** with 409 `IDEMPOTENCY_RESPONSE_NOT_KEPT`: the key and signing secret are shown once and were never meant to be stored, so the response is not kept for replay. Before this batch Communications stored and replayed it.
- **Roles hold functions, not permissions.** On `…/identity/static/roles-and-permission-sets`, `permissions` is retired: a new version with permissions is refused (422, field `permissions`). A role is granted functions through the new `…/identity/static/role-function-assignments` (Tier 1, second person).
- **Segregation-of-duties rules pair functions:** `functionA` and `functionB` are required; `permissionA` and `permissionB` are retired and must be empty (422).
- **Channel transaction rows name a function:** `functionCode` is required (an ACTION function); `transactionType` is retired and must be empty (422). The channel check runs on the request's function.
- **A signed-in user refused for lack of access now gets `FUNCTION_NOT_GRANTED`** (403) instead of `FORBIDDEN`: the function is checked before the permission. `FORBIDDEN` remains for callers Identity does not know.
- **Onboarding readiness `CHANNELS`** names missing rows as `ROLE: FUNCTION_CODE` (was `ROLE: permission`).
- **Feature entitlements are enforced.** `tenant.cfg_feature_entitlements.feature` is one of `LIFE`, `GENERAL`, `HEALTH`, `REINSURANCE`, `BROKER_PORTAL`, `PROVIDER_PORTAL`, `CUSTOMER_PORTAL`. Tenants that exist at the release keep every feature; a tenant registered later must be entitled by the platform before its users can use Life, General, Health, Reinsurance or the portals.

### New endpoints

| Endpoint | Permission | Notes |
|---|---|---|
| `GET /api/v1/identity/me/menu` | signed-in user (staff and portals) | `{audience, domains: [{code, labelKey, label, icon, subDomains: [{code, labelKey, label, icon, screens: [{functionCode, labelKey, label, routeKey, actions: [{functionCode, labelKey, label}]}]}]}]}`. Only what the user may use now, for their audience (`CUSTOMER`, `BROKER`, `PROVIDER`, `PLATFORM` in the operator tenant, else `STAFF`), in the catalogue's order. While Onboarding, only functions open then. `labelKey` is the translation key; `label` is in the request's language |
| `GET /api/v1/claims/claims/{claimNumber}/communications` | `claim_register.read` | The claim's correspondence log, newest first: `[{id, direction: IN \| OUT, channel, documentId, sentAt}]`. The claim view keeps the count |

Static data (generated endpoints): `identity/static/business-domains`, `business-sub-domains` and `function-catalogue` (global, platform-maintained; shipped with each release), `identity/static/role-function-assignments` (tenant, Tier 1) and `tenancy/static/tenant-function-availability` (tenant, Tier 1: `{functionCode, enabled}`; a function with no row is enabled).

### Contract metadata
- **`x-function`** on every operation except those about the signed-in user (`/identity/me…`) and signed provider callbacks: an array of function codes; the caller needs any one of them (one per portal for an operation the portals share). Static data operations name their catalogue module's `SD_{MODULE}_VIEW` or `_MAINTAIN`; the shared approve and reject operations show `SD_{catalogue module}_APPROVE`: for a table, take its own operations' `x-function` and replace `_VIEW` or `_MAINTAIN` with `_APPROVE`.
- **Error responses:** `Problem403`, `Problem409` and `Problem422` list the codes any operation may return (the function refusals, idempotency and onboarding codes among them).

### Changed responses
- **`NotificationView`** gains `consentRecordId`: for marketing that was sent, the Party consent record that granted the purpose then (proof of consent).
- **`RoleView`** (`GET /identity/roles`) and **`MeView`**: `permissions` are now derived: the permissions the role's functions require.

### Error codes
- New, all 403: `FUNCTION_NOT_GRANTED` (the user's roles do not grant the operation's function, even when another function gives its permission), `FUNCTION_DISABLED` (not available on the platform, or disabled by the tenant), `FEATURE_NOT_ENTITLED`.
- While the tenant is Onboarding, a function not open then is refused with `TENANT_NOT_ACTIVE` (409), as before.
- Prerequisite `FUNCTION_DISABLED` (item: the JOB function): a consumer's delivery for a tenant that disabled the function parks, and is retried when it is enabled (ADR-022).

### Changed behaviour
- **Access by functions** (ADR-026 §2). Checks in order: tenant status, platform availability, tenant enablement, feature entitlement, the user's grant, then the permission, F103 scope and F005 channel as before. A user's permissions are those their functions require. Existing roles, rules and channel rows were converted at the release with no change in what anyone can do (one exception: OQ-096).
- **Privileged roles:** a role holding a privileged function is privileged: its assignment needs a second administrator and the user must be MFA-enrolled (`MFA_REQUIRED`, 409; OQ-097).
- **The first tenant administrator** request still names permissions; they are converted to functions, and one no function grants on its own is refused (422 `VALIDATION_FAILED`, field `permissions`; OQ-099).
- **Background work** a tenant disables (JOB functions, OQ-098) is held: consumers park, daily jobs skip the tenant and pick its due work up once enabled.

## Wave 1 batch 2 (merged at `2562160`): Communications (addenda/communications.md)

### New endpoints

| Endpoint | Permission | Notes |
|---|---|---|
| `GET /api/v1/communications/notifications?partyId&typeCode&status&sourceType&sourceId` (cursor) | `notification.read` | Summaries, oldest first, within the caller's F103 scope (the source record's product and node) |
| `GET /api/v1/communications/notifications/{id}` | `notification.read` | The text as sent and every attempt `{attemptNo, channel, providerCode, providerRef, maskedAddress, status, failureCode, attemptedAt, statusAt}`; out of scope is 404 |
| `GET /api/v1/communications/parties/{partyId}/preferences` | `notification_preference.read` | `[{typeCode, channels, optedOut, rowVersion}]`; a type with none uses its default order |
| `PUT /api/v1/communications/parties/{partyId}/preferences/{typeCode}` `{channels, optedOut}` | `notification_preference.update` | A whole replacement, idempotent, so no If-Match. A service type cannot be opted out of; channels must be the type's (`PREFERENCE_NOT_ALLOWED`, 422). Opting out of marketing withdraws the type's consent purpose in Party (eventual) |
| `GET /api/v1/communications/callback-endpoints` | `communication_callback_endpoint.read` | Without keys or secrets |
| `POST /api/v1/communications/callback-endpoints` `{providerCode, requireClientToken}` (Idempotency-Key) | `communication_callback_endpoint.update` | Key and signing secret shown once; an active endpoint for the provider is rotated (ADR-023). Allowed while Onboarding |
| `POST /api/v1/communications/callback-endpoints/{id}/revoke` | `communication_callback_endpoint.update` | At once |
| `POST /api/v1/communications/callbacks/{endpointKey}` `{providerRef, status: DELIVERED \| BOUNCED \| FAILED}` | signed provider callback | Headers `X-Callback-Timestamp`, `X-Callback-Id`, `X-Signature` as for payments. 204 when applied and for an unknown reference; 401 `CALLBACK_SIGNATURE_INVALID`; 409 `CALLBACK_REPLAYED`; 422 for a malformed body |
| `GET /api/v1/portal/communications/notifications` (cursor), `…/{id}` | `notification.read`, member portal | The member's own notices of types the tenant shows in the portal, newest first; another's is 404; never the address |
| `GET /api/v1/portal/communications/preferences`, `PUT …/preferences/{typeCode}` | `notification_preference.read` / `.update`, member portal | The member's own |
| `POST /api/v1/portal/communications/devices` `{platform: IOS \| ANDROID \| WEB, token}` (Idempotency-Key), `DELETE …/devices/{id}` | `notification_device.update`, member portal | Push devices; the token is stored encrypted and never returned |

Static data (generated endpoints under `/api/v1/communications/static/…`): `channels` (global), `notification-types`, `notification-templates`, `channel-providers`. A template may use only its type's merge fields; a type the platform does not trigger is marketing, needs a consent purpose, and its templates must contain `{sender_identity}` and `{opt_out_contact}` (422 with field errors otherwise). Email and push templates need a subject.

`general.cfg_renewal_rules` gains `reminderDays` (integer list, empty by default); `general.cfg_aggregation_zones_and_limits` gains `alertRole`.

### Statuses
- Notification: `Queued`, `Sent`, `Delivered`, `Failed`, `Undeliverable` (no address on any channel), `SuppressedNoConsent` (marketing without consent, or opted out).
- Attempt: `SENT`, `DELIVERED`, `FAILED`, `BOUNCED`, `UNDELIVERABLE`.

### Events
- New: `communication.NotificationRequested`, `NotificationSent`, `NotificationDelivered`, `NotificationFailed`, `MarketingOptedOut`; `general.GeneralClaimDeclined`, `general.GeneralRenewalReminderDue`; `health.MedicalClaimRejected`.
- Changed (additive): `billing.ArrearsDetected` gains `accountNumber`, `payerPartyId`, `nextAction`.

### Changed behaviour
- **Notices are sent from events** (§6): payment reminder and final notice from arrears buckets; renewal invitation and reminders; General and Life claim status; Health claim decisions and preauthorisations. A claim notice that is sent appears in the claim's communications.
- **Missing configuration parks** (ADR-022) with `NOTIFICATION_TYPE_NOT_CONFIGURED`, `CHANNEL_PROVIDER_NOT_CONFIGURED` or `NOTIFICATION_TEMPLATE_NOT_IN_FORCE`; a tenant that has not configured a type sees the trigger parked and escalated, not lost.
- **Error codes:** `PREFERENCE_NOT_ALLOWED` (422) is new. `CALLBACK_SIGNATURE_INVALID` and `CALLBACK_REPLAYED` are now shared with Communications; the codes are unchanged, their message keys become `error.callback.signature` and `error.callback.replayed`.

## Wave 1 batch 1 (merged at `8c007e7`): requests 48–50, completing Wave 0b

### New endpoints

| Endpoint | Permission | Notes |
|---|---|---|
| `GET /api/v1/general/aggregation-alerts?zone&peril&zoneReference&from&to` (cursor) | `general_aggregation_alert.read` (new) | GAP-051. Each zone and peril at or above its alert level, once a day: `{id, zone, peril, currency, exposure, maxExposure, alertAtPct, percentUsed, asAt, raisedAt, zoneReference}`. Tenant-wide: not narrowed by F103 scope |
| `GET /api/v1/general/aggregation-alerts/{id}` | `general_aggregation_alert.read` | GAP-051 |

### Changed responses
- **`HealthMembershipView`** (`GET /health/memberships/{id}` and the membership commands) gains `productCode` and `productVersion` (GAP-050): the product the membership is on, recorded at enrolment, on a plan change, and by the daily job once the plan names one. Both are null while the plan names none.
- **`WorkflowApprovalView`** gains `requiredPermission`: the permission the checker needs besides authority for the type (`bank_account.verify`, `party.update`), or null.

### Contract metadata
- **`x-additional-permissions`** (GAP-052): an operation whose handler checks a permission besides its `x-permission` lists it as `[{permission, when}]`. `x-permission` stays a single string.
  - `commission_payout.read` on:
    - `GET /payments/payments/{id}` and `GET /payments/payments`;
    - `GET /payments/summary`;
    - `POST /payments/payout-batches`, `GET /payments/payout-batches/{id}` and `…/{id}/payments`;
    - `POST /payments/payout-batches/{id}/release`.
  - `bank_account.verify` and `party.update` on `POST /workflow/approvals/{id}/approve` and `…/reject`, for the approval types that name them.
  - A test fails if a permission the code checks is declared on no operation.

### Changed behaviour
- **Aggregation alerts (GAP-051):**
  - `general.AggregationLimitApproached` gains `alertId`, `alertAtPct` and `targetRole`. It is now raised at most once a day per zone, peril and currency, however often the snapshot runs.
  - Workflow opens a task on the built-in process `AGGREGATION_REVIEW` (state `REVIEW_EXPOSURE`), in the queue of `targetRole` (`UNDERWRITING` by default, OQ-085). The task's business reference is `AGGREGATION_ZONE` and the alert's `zoneReference`.
  - There is one open task per zone and peril. Later alerts add none while it is open.
- **Static data reads** (`GET /{module}/{entity}` and `…/{code}`): without `asAt`, "today" is now the tenant's business date (ADR-025), not the UTC date. This matters only between 00:00 and 02:00 in Johannesburg.
- **`POST /billing/accounts/{id}/age`** ages on the business date.

## Wave 0b batch 6 (merged, `f97ed95`): external-user remainder; arrears past the last bucket; dashboard figures; onboarding readiness; Keycloak users

### New endpoints

| Endpoint | Permission | Notes |
|---|---|---|
| `GET /api/v1/portal/billing/invoices`, `…/invoices/{id}` | `invoice.read` | Member (accounts they pay), broker (its policies and sub-brokers') |
| `GET /api/v1/portal/documents?businessRefType&businessRefId`, `…/{id}/content` | `document.read` | Any portal. Only categories whose `allowed_roles` include one of the caller's roles; each read logged with purpose `PORTAL` |
| `GET /api/v1/portal/distribution/statements`, `…/statements/{id}` | `commission_statement.read` | Broker: approved and paid statements, its own and its sub-brokers' |
| `GET /api/v1/portal/life/claims`, `…/claims/{id}` | `life_claim.read` | Member (claimant, policyholder or nominated beneficiary), broker |
| `POST /api/v1/party/parties/{id}/match-resolution` `{resolution: LINK \| NEW, existingPartyId?}` (If-Match) | `party.update` | Staff resolve a matched portal registration; a second person approves `PARTY_MATCH_RESOLUTION` in Workflow |
| `GET /api/v1/analytics/summary?from&to` | `analytics.read` | Dashboard: `WRITTEN_PREMIUM`, `POLICIES_WRITTEN`, `CLAIMS_INCURRED`, `CLAIMS_REPORTED`; `notIncluded: [HEALTH]` |
| `GET /api/v1/compliance/returns/summary?fromPeriod&toPeriod` | `regulatory_return.read` | `RETURNS_BY_STATUS`, `RETURNS_AWAITING_ACTION`, `RETURNS_OVERDUE` |
| `GET /api/v1/integration/deliveries/summary` | `dead_letter.read` | `DELIVERIES_PARKED` (by prerequisite), `DELIVERIES_FAILING`, `DEAD_LETTERS_OPEN`, `WEBHOOK_DELIVERIES_FAILING`, `OLDEST_PARKED_AT` |
| `GET /api/v1/billing/summary` | `billing_account.read` | `OPEN_ARREARS` (whole accounts in scope only) |
| `GET /api/v1/payments/summary` | `payment.read` | `PAYMENTS_UNKNOWN` |
| `GET /api/v1/tenancy/tenants/{id}/readiness` (platform) | `tenant.read` | Every onboarding step's checks and results |
| `POST /api/v1/identity/users/{id}/resend-invitation` (If-Match) | `user.update` | Keycloak's invitation again, for a user of realm insys who has not signed in |
| `POST /api/v1/tenancy/tenants/{id}/smoke-run` `{runReference, runDate, outcome: PASSED \| FAILED, ranBy}` (If-Match, platform) | `tenant.update` | The end-to-end run, made in a non-production environment |
| `POST /api/v1/tenancy/tenants/{id}/smoke-run/approve` (If-Match, platform) | `tenant.update` | A second operator approves it (`MAKER_CHECKER_SAME_PERSON` for the recorder) |

Every summary has one shape, `DashboardSummary {asOf, refreshedAt?, notIncluded?, figures: [{code, count?, amounts?: [{currency, amount}], by?: [{key, count}], at?}]}`.

### Changed requests
- **`POST /identity/users` and `POST /identity/tenant-administrators`:** for INSYS's own realm (`idpCode` `KEYCLOAK`) give `email` and no `externalSubject`; INSYS creates the Keycloak user. `externalSubject` is still required for a federated identity provider.
- **`POST /tenancy/tenants/{id}/complete-onboarding-step`** takes `attestations: [{check, note}]`: a waiver for `TREATIES` or `WEBHOOKS_OR_NONE`, with the reason. `SMOKE_RUN` is met by a recorded, approved, passed run (`…/smoke-run`).

### Changed responses
- **`/portal/health/claims`** and **`/portal/health/claims/{id}`** are open to members too: their own claims, and as principal every claim of their membership.
- **`GET /party/parties/{id}`** gains `review {status, registeredBy, matchedPartyId, resolution}` for portal registrations.
- **`GET /analytics/claims-incurred`** rows gain `branchCode`; `/analytics/written-premium` and `/claims-incurred` now return only rows in the caller's scope (F103).
- **Users** gain `email`, and the status `PendingIdentity`.
- **A return definition** (static data) gains `submissionDays`.

### Changed behaviour
- **Onboarding readiness:** a step completes only when its checks pass; otherwise 422 `ONBOARDING_STEP_NOT_READY` with `errors` naming each check and what is missing. `ATTESTATION_NOT_ALLOWED` (422) for an attestation a check does not take.
- **While a tenant is Onboarding** its users may sign in and use identity, static data and configuration endpoints. Every business command (quotes, proposals, policies, endorsements, claims, billing, payments, finance postings, commission, reinsurance) is refused with 409 `TENANT_NOT_ACTIVE`; accounting periods and payment callback endpoints count as configuration. Reads are allowed. Suspended and Terminated tenants are refused as before.
- **Arrears:** an open case past every ageing bucket stays in its bucket with its true days overdue, and is parked once (`billing.ArrearsAgeingBlocked`, `ARREARS_AGEING_BUCKET_NOT_IN_FORCE`) until a bucket covers it.
- **Analytics** projects by the policy's organisation node (it was `ALL`).
- **Locking a user** now also disables its Keycloak user (realm insys), and unlocking enables it.
- **A broker quoting for a registration staff linked to an existing party** quotes for that party.

### New events
- `tenant.TenantActivated {tenantId}`; `billing.ArrearsAgeingBlocked {accountId, bucketCode, daysOverdue}`; `party.PartyMatchResolved {partyId, resolution, existingPartyId}`; `iam.UserIdentityRequested {userId}`, `iam.UserInvitationRequested {userId, actions}`, `iam.UserReenabled {userId}`.

### New error codes
- `ONBOARDING_STEP_NOT_READY`, `ATTESTATION_NOT_ALLOWED` (Tenancy); `TENANT_NOT_ACTIVE` (all business modules).

## Wave 0b batch 5 (merged, `0f66f05`): external-user access; Health product follow-ups

### Removed
- **`POST /api/v1/payments/providers/{providerCode}/callbacks`** (operation `PaymentsProviderCallback`) is gone, with its bearer-token path and the `Payments:Providers:<code>:CallbackSecret` setting. Every provider callback, the simulator's included, uses `POST /api/v1/payments/callbacks/{endpointKey}` with no token, signed as in ADR-023. The front end moved its simulator scripts first.
- **`payment_callback.create`** remains only as the optional client-credentials check on the keyed route (ADR-023 decision 4); no user needs it to send a callback.

### New endpoints

| Endpoint | Permission | Portal |
|---|---|---|
| `POST /api/v1/identity/users/{id}/subject-link` `{kind, partyId \| intermediaryCode \| providerId}` (If-Match) | `user.update` | Staff: link an external user; pending until a second person approves (`EXTERNAL_SUBJECT_LINK` in Workflow) |
| `POST /api/v1/identity/users/{id}/subject-link/end` (If-Match) | `user.update` | Staff: end it at once |
| `GET /api/v1/identity/users/{id}/subject-link` | `user.read` | Staff |
| `GET /api/v1/portal/general/policies`, `…/policies/{id}` | `general_policy.read` | Member (policyholder), broker (its codes and sub-brokers') |
| `GET /api/v1/portal/general/quotes`, `…/quotes/{id}`; `POST …/quotes` | `general_quote.read`, `.create` | Broker: quotes under its own code, for a client it services or registered |
| `GET /api/v1/portal/general/claims/{id}`; `POST …/claims` | `general_claim.read`, `.create` | Member (own policy; notify), broker (read) |
| `GET /api/v1/portal/life/policies/{id}` | `life_policy.read` | Member (policyholder or insured life; beneficiaries to the policyholder only), broker |
| `GET /api/v1/portal/health/memberships/{id}` | `health_membership.read` | Member (principal or dependant) |
| `GET /api/v1/portal/health/claims`, `…/claims/{id}`; `POST …/claims` `{memberNumber, dependantSuffix?, …}` | `medical_claim.read`, `.create` | Provider, as itself |
| `GET /api/v1/portal/health/preauthorisations/{id}`; `POST …/preauthorisations` | `health_preauthorisation.read`, `.create` | Provider, as itself |
| `GET /api/v1/portal/health/remittances` | `provider_payment_run.read` | Provider |
| `POST /api/v1/portal/party/parties` → 202 `{id, status: "Received"}` | `party.create` | Broker: register a client |
| `GET /api/v1/compliance/report-runs?reportCode&from&to&limit` → `ReportRunSummary[]`, newest first | `report.read` | Staff (GAP-047) |
| `GET /api/v1/compliance/report-runs/{id}/export` → `text/csv` file | `report.read` | Staff: a run made with export, while the catalogue lets your roles export it; else `REPORT_NOT_EXPORTED` (409) or `REPORT_NOT_ALLOWED` (403) (GAP-047) |

### Changed requests
- **Compliance (GAP-048):**
  - `POST /compliance/returns` and `POST /compliance/report-runs` need `Idempotency-Key`; a retry with the same key returns the first result;
  - the return actions (`…/review`, `/approve`, `/submit`, `/accept`, `/reject`, `/correct`) need `If-Match` with the return's ETag: 428 without it, 412 when stale.

### Changed responses
- **Compliance (GAP-048):** `GET /compliance/returns/{id}`, `POST /compliance/returns` and the return actions send an `ETag`. An action's response `rowVersion` is the new version (it was the one before the action).
- **`/identity/me`** gains `external` `{kind, partyId, intermediaryCode, providerId}` for an external user; null for staff. It is open to all three portals.

### Changed behaviour
- **External users** (a user with an approved link):
  - only their portal's endpoints: anything else is 403 `EXTERNAL_USER_NOT_ALLOWED`;
  - only through the channel of their audience (member: CUSTOMER, broker: BROKER, provider: PROVIDER): otherwise 403 `CHANNEL_NOT_ENABLED`;
  - brokers and providers must show MFA in the token (`amr` otp/mfa/hwk/swk, or `acr` ≥ 2); members too where the member portal's authentication policy says so (`client_ids`, `mfa_required`). Otherwise 403 `PORTAL_MFA_REQUIRED`.
- **Records outside the relationship** read as not found and are refused for actions. Portal views leave out internal fields.
- **A portal registration matching an existing party** (identity document, or name and birth date) gets the same 202 answer as a new one, and a staff review task (`PARTY_MATCH_REVIEW`, queue `PARTY_REVIEW`).
- **Arrears (request 46):**
  - a premium the daily run invoices after its due date is due on its own date, so the first run after that date finds it overdue and opens the arrears case (it used to take a second run). Back premiums of a policy billed for the first time (backdated issue, reinstatement, enrolment) are still due from the day they are invoiced;
  - an invoice passing its due date raises `billing.InvoiceOverdue`. With no ageing bucket for the days overdue, ageing no longer just refuses: the case opening is parked (`ARREARS_AGEING_BUCKET_NOT_IN_FORCE`, item the days overdue) and done once a bucket is published. `POST /billing/accounts/{id}/age` answers 200 in that case; the parked delivery shows in `/integration/parked-deliveries`.
- **Health:**
  - a membership given its plan's product later raises `health.MembershipProductAssigned`, and Billing gives its schedules the product;
  - `/health` `health-plan-products` lists every plan option whose product is missing, unknown, not a Health product, or differs from the plan's other options.

### New events
- **`billing.InvoiceOverdue`** `{invoiceId, billingAccountId, sourceDomain, policyRef, dueDate, outstanding, currency}`.

### Static data
- **`iam.cfg_authentication_and_session_policies`** gains `clientIds` and `mfaRequired`.

## Wave 0b batch 4 (merged, `537f4cb`): Health products; Health payment runs scoped; F062 follow-ups

### Changed requests
- **Health plan options** (`/api/v1/health/static/plans-and-options`) gain `productCode`: the Product Factory product (domain `HEALTH`) the plan is sold as.
  - A new version must name one, and every option of a plan names the same one; otherwise submitting it is refused with 422 `HEALTH_PLAN_PRODUCT_INVALID`.
- **General aggregation zone limits** (`/api/v1/general/static/aggregation-zones-and-limits`) gain an optional `alertAtPct`, the limit's own alert level.

### Changed responses
- **Health `PaymentRunView`** gains `partlyInScope`. When a run also pays claims outside the caller's scope, only the remittances whose claims are all in scope are listed, and `claimCount` and `totalAmount` are of those.
- **`PaymentRunSummary`** (the run list) gains `partlyInScope`.

### Changed behaviour
- **Health enrolment and plan changes:** the plan's product must be a Health product on sale on the date, else 422 `HEALTH_PRODUCT_NOT_AVAILABLE`. The membership records it.
- **Product scopes now reach Health:**
  - memberships, their claims, preauthorisations and cases, and their Billing schedules are scoped by the plan's product as well as the node;
  - a plan change to another product needs that product in the caller's scope;
  - memberships on plans that name no product yet stay outside product scopes until the plan's next version names one, when the daily job gives them the product;
  - `/health` lists such plans (`health-plan-products`).
- **Health payment runs follow their claims' branch and product (F103):**
  - a remittance is in scope when all the claims it pays are;
  - a run with none in scope is not found;
  - cutting off, calculating and releasing need all the run's claims in scope (403 `OUT_OF_SCOPE`);
  - the run's approval is scoped when its claims share one product and node;
  - payments of a remittance (`HEALTH_REMITTANCE`) follow it in `/payments` and payout batches.
- **The F062 alert** is on by default, at 80% of each limit, or the limit's own `alertAtPct`.

## Wave 0b batch 3 (merged, `cd2d78d`): claim payouts scoped; F062 aggregation limits

### Changed responses
- **Payments `BatchView`** gains `partlyInScope`. When a payout batch also pays records outside the caller's scope, `paymentCount` and `totalAmount` are of the payments in scope only.

### Changed behaviour
- **Claim payouts follow their claim's product and branch (F103).** Applies to claim payments in `/payments` (by ID and by business reference) and in payout batches (`/payout-batches/{id}` and its `/payments`). Outside the scope they are not found.
- **Partly-scoped batches:** a batch with some payments in scope shows only those. Building a batch, or releasing one, needs every payment in it to be in the caller's scope (403 `OUT_OF_SCOPE`).
- **A payout batch's approval** carries its payments' product and node when they all share one. Otherwise only a checker with unrestricted scope sees and decides it.
- **Commission payouts** are outside branch scope, but reading them (payments and batches) needs the new permission `commission_payout.read`. So does building or releasing a commission batch.
- **Zone and peril limits (F062):**
  - issuing a quote that would take a zone and peril past its limit (`general.cfg_aggregation_zones_and_limits`) refers it, with referral reason `AGG_LIMIT`, instead of issuing it; the response is the quote in status `Referred`;
  - once an underwriter approves that referral, issuing succeeds;
  - an endorsement over a limit goes to approval as before, and its approval's `reason` names the limit.
- **Aggregate claims limits (F062):** a General settlement over what remains of the coverage's aggregate in the period (`general.cfg_aggregate_limit_definitions`) is refused with 422 `AGGREGATE_LIMIT_EXCEEDED`, with the amount remaining.

### New events
- **`general.AggregationLimitApproached`** `{zone, peril, exposure, maxExposure, currency, asAt}`: raised once a day from the exposure snapshot, when a zone and peril reaches `General:Aggregation:AlertAt` of its limit. Not raised until that level is set (OQ-076).

## Wave 0b batch 2 (merged, `376ac3d`): F005 channel permissions

### Changed behaviour
- **Channels apply once a tenant has channel definitions** (F005, `addenda/channel-permissions.md`). A tenant without any is unchanged.
- **Which requests:** every command (POST, PUT, DELETE) of General, Life, Health and the Claims Framework, and building and releasing payout batches. Reads, static data and platform endpoints never are.
- **The channel** is the one whose `client_ids` hold the token's OAuth client (`azp`). The front end sends nothing new; its client (`insys-web`) must be on a channel.
- **Refusals (403):**
  - `CHANNEL_NOT_ENABLED`: the client is on no channel in force, or the channel does not enable the transaction (the command's permission code);
  - `CHANNEL_NOT_ALLOWED`: the caller's roles have no allowed row for the channel, transaction and product, or the product is not enabled on the channel;
  - `CHANNEL_APPROVAL_REQUIRED`: the row requires approval, and the command has no approval flow. Endorsement submission, settlement requests, claim payment instructions and payout batches have one.
- **Recorded:** the channel is stored on General quotes, policies and claims, Life claims and Health memberships. On a Life proposal, the request's own channel replaces the `channelCode` sent, where the tenant has channels.

### Static data
- **`channel.cfg_channel_definitions`** gains `clientIds` (a list).

## Wave 0b batch 1 (merged, `19a9bb2`): F103 access scopes

### Changed requests
- **Creation requests take an optional `orgNodeCode`**, the organisation node (branch, region or legal entity) the record belongs to:
  - `POST /api/v1/general/quotes`;
  - `POST /api/v1/life/proposals` and `POST /api/v1/life/group-schemes`;
  - `POST /api/v1/health/memberships`.
- **When `orgNodeCode` is omitted:** the user's one scoped node, else the tenant's root legal entity. With no organisation structure, records have no node.
- **Refusals:** a node not in the structure is refused with 422; a product or node outside the user's scope with 403 `OUT_OF_SCOPE`.
- **What follows a record:** policies, renewals, claims and Billing keep their quote's, proposal's or membership's node.
- **Role assignments with `scopeProducts` or `scopeBranches` are accepted** (`POST /identity/users/{id}/assign-role`); `SCOPES_NOT_ENFORCED` is gone.

### Changed responses
- **Billing `AccountView`** gains `partlyInScope`. When the account also bills policies outside the caller's scope, only the in-scope schedules are listed and `arrears` is empty (OQ-081).
- **`/identity/me`** `scopesEnforced` is now `true`.
- **Error code `OUT_OF_SCOPE`** (403): an action on a record outside the caller's scope for that permission.

### Changed behaviour
- **Access scopes are enforced (F103, `addenda/access-scopes.md`).** A role assignment scoped to products or organisation nodes (a node covers every node below it) limits what its permissions reach:
  - lists show only records in scope;
  - reading a single record outside the scope is 404, as if it did not exist;
  - an action on one is 403 `OUT_OF_SCOPE`.
- **Where it applies:**
  - General quotes, proposals, policies, endorsements and claims;
  - Life proposals, underwriting cases, policies, loans, claims and group schemes;
  - Health memberships, and their preauthorisations, medical claims, utilisation cases and fraud cases;
  - Claims Framework claims, reserves and payment instructions (as their domain claim);
  - Billing invoices and accounts. An account is in scope when any of its schedules is; a caller covering only part of it sees only that part.
  - **Whole account needed:** ageing, closing, issuing due invoices, refunds and new schedules (else 403 `OUT_OF_SCOPE`), and reading a refund.
  - Payments against an invoice, a billing account or a refund, shown only when the caller covers all of that record;
  - Workflow approvals about any of those (a checker sees and decides them only within their scope).
- **Health has no product** (OQ-080): a grant restricted to products reaches no Health record.
- **Not scoped:** parties, static data, Health schemes and provider payment runs, payouts, and approvals of other types.
- **Existing records** were placed at the tenant's root legal entity.

## Wave 0 batch 7 (merged, `7614b84`)

### Changed requests
- **`POST /api/v1/tenancy/tenants`** needs `timeZone`, an IANA name (for example `Africa/Johannesburg`); a Windows name or an unknown zone is refused with 422 (ADR-025).
- **`POST /api/v1/claims/claims/{claimNumber}/reserves`** needs `reason`: every reserve move is made with a reason (OQ-079).

### Changed responses
- **`/identity/me`** `tenant` gains `timeZone` (the IANA zone its business dates follow) and `businessDate` (today in it). Default date fields to `businessDate`.
- **Tenants** (`GET /tenancy/tenants`, `…/{id}`) gain `timeZone`; it is null for tenants registered before it was required.

### Changed behaviour
- **Free cover fails safe (GAP-043).** A scheme whose size matches no band, or whose band is not in force, has no free cover limit, and then **any** cover needs evidence. Before, "no limit" meant no evidence was ever required. Bands are also checked when a draft is submitted: from 1 member up, with no overlap or gap (422 `LIFE_FREE_COVER_BANDS_INVALID`).
- **A Life lump-sum claim starts with a reserve (OQ-079).** At registration, a death claim, or a disability claim whose benefit pays a fixed sum, gets a reserve equal to the claimed cover's sum assured, on reserve type `INDEMNITY` (configurable). Adjust it through the reserves endpoint, with a reason.
- **`/health`** has a `tenant-time-zones` check: Degraded while any tenant has no time zone, listing those tenants' codes in `data.tenantsWithoutTimeZone`.

## Wave 0 batch 6 (merged, `4b0e97c`)

### New endpoints

| Endpoint | Permission | Item |
|---|---|---|
| `GET /api/v1/life/proposals`, `/policies`, `/claims`, `/group-schemes` | the matching `.read` | GAP-040: by party (applicant, policyholder, claimant, employer), number, status (and policy for claims); cursor paging |
| `GET /api/v1/health/memberships`, `/preauthorisations`, `/claims`, `/utilisation-cases`, `/payment-runs` | the matching `.read` | GAP-040: by party, membership, number, scheme or status; cursor paging |
| `GET /api/v1/health/fraud-cases?claimNumber&status` | `health_case.read` | GAP-040: find a fraud case from the medical claim it is about |
| `POST /api/v1/testing/daily-jobs` `{asOf}` | `daily_job.run` | **Test only** (ADR-025), in Development and Testing when `Testing:DailyJobRunner:Enabled`; not in the documents. Runs the daily jobs for the caller's tenant as of a business date |

### Changed responses
- **Claims Framework `InstructionView`** gains `heldReason` (`RESERVE_BELOW_ZERO`, `PAYEE_ACCOUNT_CHANGED`), `heldDetail` and `heldAt`: why an approved instruction was not released automatically.

### Changed behaviour
- **Business dates are the tenant's (GAP-044, ADR-025).** Inception, cover start, event and loss dates, due dates, lapse, period close, static data in force and the daily jobs follow the tenant's time zone (`tenant.cfg_locale_defaults.time_zone`, else Africa/Johannesburg). Timestamps stay in UTC. Default date fields to the tenant's date, not the UTC date.
- **Free cover limits apply (GAP-043).** Bands are intervals (`[50,200)`, `[200,)`). Members get the band's limit, and cover above it needs evidence. Before, no band ever matched, so no member needed evidence.
- **A refused automatic claim payment release is held**, with its reason, a Workflow task for the claims role (`CLAIM_PAYMENT_HELD`) and an alert, instead of stopping silently.
- **Reinsurance on an endorsement** parks on missing configuration (`CESSION_RULE_NOT_IN_FORCE`, `FACULTATIVE_ARRANGEMENT_NOT_IN_FORCE`, `TREATY_NOT_APPLICABLE`) instead of dead-lettering.

## Wave 0 batch 5 (merged, `e8f9dc5`)

### New endpoints

| Endpoint | Permission | Item |
|---|---|---|
| `GET /api/v1/payments/payments?businessRefType&businessRefId` | `payment.read` | GAP-036: the payments for a record, from when each is created; cursor paging. Record types: refund `REFUND_REQUEST`, claim instruction `CLAIM_PAYMENT`, invoice `INVOICE`, `HEALTH_REMITTANCE`, `COMMISSION_STATEMENT`. `businessRefType` has `x-known-values` (GAP-037) |
| `GET /api/v1/payments/payout-batches/{id}/payments` | `payout_batch.read` | GAP-036: a batch's payments |
| `GET /api/v1/claims/claims/{claimNumber}/payment-instructions` | `claim_payment.read` | GAP-036: a claim's payment instructions, each with its `paymentId` once the payment exists |
| `POST /api/v1/payments/callbacks/{endpointKey}` | none: endpoint key plus signature | ADR-023: the provider callback address; headers `X-Callback-Timestamp`, `X-Callback-Id`, `X-Signature`. 401 `CALLBACK_SIGNATURE_INVALID`, 409 `CALLBACK_REPLAYED` |
| `GET /api/v1/payments/callback-endpoints` | `payment_callback_endpoint.read` | ADR-023: endpoints, without keys or secrets |
| `POST /api/v1/payments/callback-endpoints` `{providerCode, requireClientToken}` | `payment_callback_endpoint.update` | ADR-023: issue (or rotate); the response shows the path with its key, and the signing secret, once |
| `POST /api/v1/payments/callback-endpoints/{id}/revoke` | `payment_callback_endpoint.update` | ADR-023: refuse it at once |

### Changed behaviour
- **Business modules refuse the operator tenant** (ADR-024): 403 `OPERATOR_TENANT_NOT_BUSINESS`, for reads and changes. Still open to the operator tenant:
  - Tenancy, Identity, Static data, Audit, Integration and Workflow;
  - every module's static data endpoints;
  - platform endpoints.
- **`paymentId` is set when the payment is created** (GAP-036), before it completes:
  - on a refund, once its payout exists (at approval);
  - on a released claim payment instruction. A re-released instruction gets its new payment's ID.

  New event: `payment.PaymentInitiated`.
- **`GET /api/v1/finance/journals` `businessRefType`** has `x-known-values` (GAP-037): `INVOICE`, `PAYMENT`, `CLAIM_MOVEMENT`, `MEDICAL_CLAIM`, `COMMISSION`, `CESSION`, `RI_RECOVERY`.
- **Missing configuration now parks, rather than dead-letters,** in Billing, Reinsurance, Documents and Analytics too (ADR-022). The parked delivery names what is missing: `COLLECTION_METHOD_NOT_IN_FORCE`, `BILLING_FREQUENCY_NOT_IN_FORCE`, `ADJUSTMENT_REASON_NOT_IN_FORCE`, `DOCUMENT_TEMPLATE_NOT_IN_FORCE` or `TREATY_NOT_APPLICABLE`.
- **`/health`, check `parked-deliveries`:**
  - its data is now `parked` and `escalated` (numbers) and `tenants` (each tenant code with its kinds);
  - its description names the tenant and the missing configuration.

## Wave 0 batch 4 (merged, `5ae047e`)

### New endpoints

| Endpoint | Permission | Item |
|---|---|---|
| `GET /api/v1/integration/parked-deliveries` | `dead_letter.read` | ADR-022: deliveries parked for missing configuration, waiting behind them, or failing; by `status` (`Parked`, `Waiting`, `Failing`, `Retrying`, `DeadLettered`, `Resolved`, `Discarded`), `code`, `consumer`, `aggregateType`, `aggregateId`; cursor paging |
| `GET /api/v1/integration/parked-deliveries/{id}` | `dead_letter.read` | One delivery, with its ETag |
| `POST /api/v1/integration/parked-deliveries/{id}/retry` | `dead_letter.update` | 202 `{count}`: retried at the parking job's next pass (5 s) |
| `POST /api/v1/integration/parked-deliveries/retry` `{code}` | `dead_letter.update` | 202 `{count}`: every parked delivery of the tenant for one code |
| `POST /api/v1/integration/parked-deliveries/{id}/discard` `{reason}` | `dead_letter.update` | 204; If-Match; a reason is required; audited. 409 `PARKED_DELIVERY_NOT_OPEN` when it is resolved or already discarded |
| `GET /api/v1/integration/parked-deliveries/by-tenant` | platform `dead_letter.read` | Platform operator: counts per tenant and kind, with escalation level and oldest |
| `GET /api/v1/integration/parking-setting` | `dead_letter.read` | The tenant's escalation: configuration owner role, calendar, durations; `isDefault` until set |
| `PUT /api/v1/integration/parking-setting` | `parking_setting.update` | Changes it; audited; If-Match once the tenant has its own |
| `GET /api/v1/identity/role-assignments/scoped` | `user.read` | F103 interim: existing assignments with product or branch scopes, for review |
| `POST /api/v1/party/search-log/holds`, `GET …`, `POST …/{id}/release` | `party_search_log.hold` (POST), `audit_event.read` (GET) | OQ-073: legal holds on the party search log |

### Changed responses
- **Compliance (GAP-048):** `GET /compliance/returns/{id}`, `POST /compliance/returns` and the return actions send an `ETag`. An action's response `rowVersion` is the new version (it was the one before the action).
- **`/identity/me`** gains `scopesEnforced: false` (F103 interim): scopes are returned but have no effect.
- **Claims Framework `ClaimView`** gains `fraudEvaluations`: each screening, with `score`, `referred`, `indicatorsEvaluated` and `skipped` (`indicatorCode`, `reason` = `MISSING_FACTS` or `EVALUATION_FAILED`, `missingFacts`, `errorCode`) (F009).
- **Payments `PaymentView.purpose`, `BatchView.purpose`, `BuildBatchRequest.purpose`** are enums now: `PREMIUM`, `REFUND`, `CLAIM_PAYOUT`, `RECOVERY`, `COMMISSION` (a batch: `REFUND`, `CLAIM_PAYOUT`, `COMMISSION`) (GAP-037).

### Changed behaviour
- **Role assignments with product or branch scopes are refused** with 422 `SCOPES_NOT_ENFORCED` until the scopes are enforced (F103, Wave 0b).
- **A busy database answers 503 `DATABASE_BUSY`** with `Retry-After: 2`, instead of 500 (GAP-039). Retry it.
- **A payout that cannot start is parked, not skipped** (GAP-038): a released refund, claim payment, remittance or commission statement in a currency with no payout method or provider in force shows as a parked delivery (`PAYOUT_METHOD_NOT_IN_FORCE` or `PAYOUT_PROVIDER_NOT_IN_FORCE`, item = the currency or method). The payment is created when the configuration is published.
- **Order per record:** a later event for a record waits behind a parked or failing one for the same consumer, so, for example, an endorsement is never billed before its policy's issue.
- **A skipped fraud indicator** opens a review task for the tenant's configuration owner role (Workflow, process `CONFIG_REVIEW`, business reference type `FRAUD_INDICATOR`), one per indicator while it is open (F009).
- **Escalated parked deliveries** open Workflow tasks (process `PARKED_DELIVERY`), first to the configuration owner role, then to the tenant administrator (OQ-072).
- **`/health`** has a `parked-deliveries` check: Degraded while any delivery is parked, Unhealthy once any is escalated.

## Wave 0 batch 3 (merged, `8351b83`)

### New endpoints

| Endpoint | Permission | Item |
|---|---|---|
| `GET /api/v1/party/search-log` | `audit_event.read` | The party search access log, for audit roles only, newest first. An identifier is shown by document type, country and hash. Retention: OQ-073. |
| `POST /api/v1/finance/gl-exports` | `gl_export.create` | F055: export a date range's posted entries to the GL, summarised by date, account and currency (CSV). Idempotency-Key. |
| `GET /api/v1/finance/gl-exports[?periodCode=]`, `GET …/{id}` | `gl_export.read` | F055: batches and their status (`Sent`, `Acknowledged`, `Rejected`) |
| `GET /api/v1/finance/gl-exports/{id}/content` | `gl_export.read` | F055: the batch as sent, `text/csv` |
| `POST /api/v1/finance/gl-exports/{id}/acknowledgement` | `gl_export.update` | F055: the GL's answer. If-Match. |

### Changed behaviour
- **Fraud indicators (F009):** an indicator that cannot be evaluated on a claim's facts is skipped and logged, and the others still screen the claim. Before, the whole evaluation failed and the claim was registered unscreened. Indicator logic is now checked when drafted (422, `errors.logic`), for both the Claims Framework and Health.

## Wave 0 batch 2 (merged, `bcc8ebc`)

### GAP-031: every module schema is named with its module

- **Rule:** every type of a module now has a schema ID that starts with its module (`GeneralPolicyView`, `WorkflowApprovalView`, `IdentityMeView`, `StaticDataChangeStatus`), unless its name already does (`LifePolicyView`, `PartyView`). Generic types take their arguments' IDs (`CursorPageOfIdentityUserView`, `StaticDataImportOfBillingCollectionMethodsInput`). `ProblemDetails` and other types outside the modules keep their names.
- **Effect:** every generated type name for a module type changes. JSON does not change. The full map, old to new for each document, is [`schema-renames-gap031.md`](schema-renames-gap031.md):
  - `openapi-core.json`: 291 renamed;
  - `openapi-platform-operator.json`: 199 renamed;
  - `openapi-static-data.json`: 876 renamed;
  - none dropped.
- **Guards:**
  - generating a document fails if two types would share an ID;
  - an architecture test checks every type that can appear in a document;
  - tests compare real responses (General quote, proposal, policy, risk object and claim; `/identity/me`, roles, users, tenants, tenant-administrator requests, record history) with their documented schemas: the same properties, documented types and enums.

### New endpoints

| Endpoint | Permission | Item |
|---|---|---|
| `POST /api/v1/party/parties/search` | `party.read` | GAP-022: by name (3+ characters), exact identifier (type, country, number), role in effect, status; summaries with cursor paging. POST so that an identifier number is never in a URL. Every search is written to the party access log, with an identifier by hash only. |
| `GET /api/v1/product-factory/products` | `product.read` | GAP-023: one row per product: latest version, its status and dates, family and class codes, the version in force; by domain and status; cursor by product code |
| `GET /api/v1/general/quotes` | `general_quote.read` | GAP-032: by proposer or quote number |
| `GET /api/v1/general/policies` | `general_policy.read` | GAP-032: by policyholder or policy number |
| `GET /api/v1/general/claims` | `general_claim.read` | GAP-032: by policy or claim number |

### Changed behaviour and contract

- **Readiness (GAP-034):** `/health/ready` no longer includes outbox lag. One tenant's stuck event no longer takes the instance out of service. Outbox lag stays on `/health` for alerts. What each consumer of the General policy events needs is in `docs/runbooks/dead-letters-and-webhooks.md`. Parking such events (ADR-022) is proposed, not built.
- **Rating test cases (GAP-024):** a draft whose `inputs` lack a three-letter `currency` or a `frequency` is refused, with 422 and `errors.inputs`.
- **Asynchronous effects (GAP-027):** operations whose follow-ups complete through events carry `x-eventual-effects` and a sentence in their description: static data submit, Workflow approve and reject, product submit, General policy issue, role assignment, and first-administrator requests. Responses stay 200, 201 or 204, because the operation's own change is committed when it responds.
- **Audit target types (GAP-029):** `targetType` on `ListAuditEvents` and `GetRecordHistory` is an enum of every aggregate type, the static data entity codes, `BankAccount`, `ClaimFinancials` and `Endpoint`, as stored (30 characters).
- **Document reference types (GAP-033):** `businessRefType` on the documents list has `x-known-values`: `GENERAL_POLICY`, `GENERAL_ENDORSEMENT`, `LIFE_POLICY`, `HEALTH_REMITTANCE`. A stored document may carry another type.
- **General product line (request 24):** a General product's line is its class code, or its family code when it has no class. The quote and product list descriptions say so.

## Wave 0: front-end enablement (23 October 2026)

### New endpoints

| Endpoint | Permission | Item |
|---|---|---|
| `GET /api/v1/identity/me` | signed-in user | GAP-003: profile, tenant, languages, role assignments, effective permissions with scopes. ETag. |
| `PUT /api/v1/identity/me/preferred-language` | signed-in user | GAP-003: `{ "language": "zu" }` or null. If-Match. |
| `GET /api/v1/identity/roles` | `user.read` | GAP-021: the tenant's assignable roles with `privileged`, `requiresMfa`, `requiresApproval`, `permissions`, default scopes. |
| `POST /api/v1/identity/tenant-administrators` | platform: `user.create` | GAP-019 / OQ-067: request a new tenant's first administrator. Idempotency-Key. |
| `GET /api/v1/identity/tenant-administrators[?tenantId=]` | platform: `user.read` | GAP-019: the requests and their decisions. |
| `GET /api/v1/identity/tenant-administrators/{id}` | platform: `user.read` | GAP-019. ETag. |
| `GET /api/v1/audit/records/{targetType}/{targetId}` | `audit_event.read` | Per-record history: every change to the record, newest first, cursor paging. `targetType` is the aggregate type (`UserAccount`, `Tenant`, `Party`, `LifePolicy` ...). |

### Changed responses

- **Errors (GAP-005, GAP-006, GAP-007).**
  - Every error is `application/problem+json` with `correlationId`, equal to the `X-Correlation-Id` response header. This includes the warm-up gate's 503, authentication (401), routing (404) and unexpected errors (500).
  - Errors written by the framework now carry `code` too: `UNAUTHENTICATED`, `FORBIDDEN`, `NOT_FOUND`, `RATE_LIMITED`, `NOT_READY`, `INTERNAL_ERROR`.
  - 429 carries `Retry-After` in whole seconds.
  - OpenAPI declares `code`, `errors`, `correlationId`, `traceId` and `detailLanguage` on `ProblemDetails`. Each error status is one shared response, `#/components/responses/Problem{status}`, with its headers (`X-Correlation-Id`, `Content-Language`, and `Retry-After` on 429 and 503). Every operation declares 503.
- **State fields are enums (GAP-008).** The values are unchanged: domain states are PascalCase as before (`Active`, `InForce`, `PendingApproval`). The documents now declare them:
  - Domain states are named enum schemas (`ApprovalStatus`, `ChangeStatus`, `TenantStatus` ...).
  - Codes fixed by the database are inline enums, upper case as stored: bank account, benefit, dependant, estimate, evidence, journal, referral, report run, requirement, member evidence, onboarding status.
  - Static data `recordStatus` lists its table's lifecycle. Reference tables use `DRAFT, PENDING_APPROVAL, APPROVED, ACTIVE, DEPRECATED, RETIRED`; configuration tables use `DRAFT, VALIDATED, SUBMITTED, APPROVED, PUBLISHED, EFFECTIVE, WITHDRAWN, RETIRED`.
  - Static data columns with a CHECK list (a register's `status`, for example) are enums.
- **Schema names.** When two modules had a type with the same name, the documents kept one schema for both, and the other module's shape was lost (with the types only it referenced). Such types are now prefixed with their module. Field names and JSON are unchanged; generated client type names change.

  | Was | Now |
  |---|---|
  | `ClaimView` | `ClaimsFrameworkClaimView`, `GeneralClaimView` (Life's is `LifeClaimView`) |
  | `ClaimLineView` | `HealthClaimLineView`, `LifeClaimLineView` |
  | `EvidenceRequest`, `PayeeRequest` | `ClaimsFrameworkEvidenceRequest`, `LifeEvidenceRequest`, `ClaimsFrameworkPayeeRequest` |
  | `PolicyView`, `ProposalView`, `SchemeView`, `ScheduleView` | `GeneralPolicyView`, `LifePolicyView`; `GeneralProposalView`, `LifeProposalView`; `HealthSchemeView`, `LifeSchemeView`; `BillingScheduleView`, `LifeScheduleView` |
  | `RecoveryView`, `RecoveryStatus` | `GeneralRecoveryView`, `ReinsuranceRecoveryView`; `GeneralRecoveryStatus`, `ReinsuranceRecoveryStatus` |
  | `RoleView` (Party's) | `PartyRoleView`; Identity's new one is `IdentityRoleView` |
  | `PreferredLanguageRequest` | `IdentityPreferredLanguageRequest`, `PartyPreferredLanguageRequest` |
  | `StatusChangeView` | `PaymentsStatusChangeView`, `TenancyStatusChangeView` |
  | Static data: `AllocationRules`, `PolicyNumberingSchemes`, `UnderwritingDecisionCodes` (and `...Input`) | `BillingAllocationRules`, `LifeAllocationRules`; `GeneralPolicyNumberingSchemes`, `LifePolicyNumberingSchemes`; `GeneralUnderwritingDecisionCodes`, `LifeUnderwritingDecisionCodes` |

  Types that were missing before and now appear include `CancellationView`, `ClaimPartyView` and `SettlementView`.
- **`/identity/me` tenant status** is `Active` (was `ACTIVE`), as on the platform API.
- **Role assignments (GAP-020, GAP-021)** on `UserView.roleAssignments` and `/identity/me` roles:
  - new fields `privileged`, `requiresMfa`, `requiresApproval`;
  - new status `Revoked`, with `revokedAt` and `revokedBy` on `UserView`.
  - A revoked assignment is no longer `Active` with an end date of today. Its `validTo` is set to today only when it began before today.

### Behaviour

- **Development CORS (GAP-002):** in Development and Testing, origins in `Cors:AllowedOrigins` (by default `http://localhost:5173`) may call the API. Exposed headers: `ETag`, `Location`, `Content-Language`, `Retry-After`, `X-Correlation-Id`. Other environments refuse to start with it configured.
- **Record history:** every command or event policy that changes a record leaves an audit entry: the operation's own, or `RECORD_CHANGED` naming the command.
- **A new tenant's administrator** can act once the tenant is Active. Allow up to 30 seconds after activation: the permission cache keeps a refused sign-in for that long.
