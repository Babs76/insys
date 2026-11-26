# API contract changes

What changed in the API contract, newest first, so the front end can sync. The OpenAPI documents in this folder are the contract; this list says what moved.

## Approvals open their records by the ID they carry; exports refused or cancelled; imports filtered and discarded (DE-017, DE-019, DE-020, DE-022, DE-059, OQ-203; Ade, 26 November 2026; branch `group-dfc`)
- **`WorkflowApprovalView`** has `subjectOperation` and `subjectPath` (always present): the GET operation (operationId) that opens the record the approval is about, and its path with the `businessRefId` filled in, for example `/api/v1/party/bank-accounts/{businessRefId}`. Both are null only for a reference type whose read depends on the record (`STATIC_DATA_VERSION` and `STATIC_DATA_IMPORT`, a discarded draft or import), which no approval carries: their task carries the link (below). **Front end: open an approval's record through `subjectPath`**, under the screen the view already names (`screen`).
- **New reads by ID**, each under exactly its record's existing read's screen function, permission, tenant and scope (OQ-203: product scope where that read has it, as a document's hold has its document's; 404 for a record outside the caller's tenant or scope; 403 `FUNCTION_NOT_GRANTED` without the function, as the existing read gives):
  - `GET /party/bank-accounts/{id}` (`PartyGetBankAccount`, without the number) and `GET /party/bank-accounts/pending-verification` (`PartyListBankAccountsPendingVerification`, a cursor page, oldest first, each naming its `partyId`), screen `PARTY_BANK_ACCOUNTS`.
  - `GET /party/search-log/holds/{id}` (`PartyGetSearchLogHold`, `PARTY_SEARCH_LOG`).
  - `GET /product-factory/product-versions/{id}` (`ProductGetVersionById`, `PRODUCTS`).
  - `GET /identity/role-assignments/{id}` (`IdentityGetRoleAssignment`, `IDENTITY_USERS`, as `GET /identity/users/{id}`, which shows the same assignment and name; OQ-203 moved it from `IDENTITY_ROLE_ASSIGNMENTS`; the `PRIVILEGED_ROLE` approval's screen is `IDENTITY_USERS` too, Ade 26 November 2026) and `GET /identity/subject-links/{id}` (`IdentityGetSubjectLinkById`, `IDENTITY_USERS`; the link the user's own reads show, pending or in effect, else the latest: an earlier link is `404 SUBJECT_LINK_NOT_FOUND`); `IdentitySubjectLinkView` has `userId`.
  - `GET /documents/document-legal-holds/{id}` (`DocumentsGetDocumentHold`, `DOCUMENTS`): `DocumentsDocumentHoldView`, the hold with the document it is on.
  - `GET /workflow/case-redress/{redressId}` (`WorkflowGetRedress`, `SERVICE_COMPLAINTS`): a redress by its ID alone, its case's access rules applying (DE-022).
  - `GET /finance/accounting-periods/{id}` (`FinanceGetPeriodById`, `FINANCE_PERIODS`).
- **Exports waiting for approval (DE-059):** `POST /party/bulk-jobs/{id}/refuse` (`PartyRefuseExport`, function `PARTY_APPROVE_EXPORT`, body `{reason}`, at most 500 characters; If-Match taken when sent, as for approve) by a second person, not the requester (SRS-002); `POST /party/bulk-jobs/{id}/cancel` (`PartyCancelExport`, function `PARTY_REQUEST_EXPORT`, If-Match taken when sent) by the requester only, whichever export function they asked with (OQ-203: the operation belongs to both `PARTY_REQUEST_EXPORT` and `PARTY_REQUEST_POLICY_EXPORT`, `x-function` lists both, the caller needs either; anyone but the requester is `403 FORBIDDEN`). Both audited; nothing leaves. The job's status can now be `REFUSED` or `CANCELLED`; `PartyBulkJobView` has `refusedBy`, `refusalReason` and `runBy`. V0162.
- **`GET /static-data/imports`** (`ListImportBatches`) takes `status` (`COMMITTED`, `REJECTED` or `DISCARDED`; anything else is `422 VALIDATION_FAILED`); `StaticDataImportStatus` gains `Discarded`. `StaticDataImportBatchView` has `batchId`, the same as its `id` and the `{batchId}` of the table's import routes (DE-017).
- **Discarding an import never submitted (OQ-203, Ade, 26 November 2026):** `POST …/{table}/imports/{batchId}/discard` and `…/global/imports/{batchId}/discard` (`Discard{Table}Import`, `Discard{Table}ImportGlobal`; the table's maintain function, `static_data.update`; optional body `{reason}`; no If-Match, as submit). Under a draft's rules (OQ-204): every line still a draft with no change request; its maker at any time; anyone else once no line has been touched for `staleDraftDiscardDays`, with a reason (`422 VALIDATION_FAILED` without one), else `409 STATIC_DRAFT_NOT_STALE` with `discardableFrom`; `409 INVALID_STATE_TRANSITION` once submitted or returned by a rejection; `404` for an import with no lines. `204`; every line deleted and audited (`DiscardImport`, `DiscardStaleImport`); the import then reads `DISCARDED`. A line's own discard now names this route in its `409`.
- **A discarded draft's or import's task (OQ-203):** `WorkflowMyTaskView` has `subjectOperation` and `subjectPath` (always present), as an approval's: the declared read by `businessRefId`, or, for a `STATIC_DRAFT_DISCARDED` task, its table's page of versions not in force (`List{Table}Pending`, `/api/v1/{module}/static/{entity}[/global]/pending`). `WorkflowStaticDraftDiscardNotice` gains `table` (schema.table), `recordKey` (`{code}@{version}`, `G:` first for a global row; an import's `batchNumber`), `tableOperation`, `tablePath`, `importBatchId` and `lines` (an import's). Someone else discarding an import tells its maker by one such task, `businessRefType` `STATIC_DATA_IMPORT` (new reference type) with the import's `batchId`.
- No error code or permission changed, and no function was added. `docs/api/openapi-core.json` and `openapi-static-data.json` regenerated.

## Disclosures: merged parties, `isDefault` and a request for the value in force (DE-039, OQ-201; Ade, 26 November 2026; branch `disclosures-fixes`)
- **`POST /portal/broker/disclosure-acknowledgements`:** a record naming a party that was merged into the client now counts as the client's (along a chain of merges), and is accepted. One naming a party merged into another party is still `404 NOT_FOUND`. No field changed.
- **`GET /integration/disclosure-setting`: `isDefault` changes only when a change is granted, never at the request.** It stays true while the tenant's first request waits, and after one that is rejected, expires or is withdrawn; the view is then as before, except that `rowVersion` (and the ETag) is the setting's row version, which a request moves. **Front end:** don't read `isDefault` false as "a request was made"; show `pendingChange` for that. If-Match stays optional while `isDefault` is true (send the ETag anyway) and required once it is false (412 without it).
- **`PUT /integration/disclosure-setting`: a request for the value already in force is refused with `422 VALIDATION_FAILED`** (the existing code; no new code), and changes nothing: no pending change, no approval, no audit, and the row version doesn't move. **Front end:** offer only the other value, and show the refusal if it comes.
- V0161 (`channel.disclosure_setting.is_default`). `docs/api/openapi-core.json` regenerated (the two operations' descriptions). No error code, function or permission changed.

## Disclosures: a broker records its client's acknowledgement; the switch goes through a second person (DE-039 part 2, OQ-201; Ade, 25 and 26 November 2026; branch `disclosures-broker`)
- **Broker portal** (functions `BROKER_PORTAL_DISCLOSURES`, `BROKER_PORTAL_ACKNOWLEDGE_DISCLOSURE`):
  - **`GET /portal/broker/disclosures`** (`PortalBrokerGetDisclosureText`, query `displayPoint`, `noticeType`, `jurisdiction`, `language`, as `DISCLOSURE_REQUIRED` names them): the text, with `brokerAcknowledgementAllowed` (false: show the notice read-only).
  - **`POST /portal/broker/disclosure-acknowledgements`** (`PortalBrokerAcknowledgeDisclosure`, Idempotency-Key, 201): `{partyId, displayPoint, noticeType, jurisdiction, language, businessRefType, businessRefId, presentationMethod, presentationDetail}`. `presentationMethod` is IN_PERSON, PHONE, EMAIL or OTHER; `presentationDetail` is required with OTHER, at most 200 characters. Answers as the staff acknowledgement, with `outstanding`. The language is the one the broker opened; none is picked for it.
  - **The customer will see `presentationDetail`** (Ade, 26 November 2026). **Front end: where the broker types it, tell them so** (for example "The customer will see this."). The field's OpenAPI description says the same.
  - **Refusals:** a party that isn't the broker's client, `404 NOT_FOUND`. **New (Ade, 26 November 2026): the record named must exist and belong to that client:** another party's record, or one that doesn't exist, is `404 NOT_FOUND` (the same answer for both, BR-091). `businessRefType` may be `GENERAL_QUOTE`, `GENERAL_RENEWAL` or `LIFE_PROPOSAL` (the records `DISCLOSURE_REQUIRED` names), or none with no ID (a Health enrolment); any other type, or a type without its ID or the reverse, is `422 VALIDATION_FAILED`. Switched off for the tenant, `403 DISCLOSURE_BROKER_NOT_ALLOWED` (new code).
- **Member portal:** **`GET /portal/disclosure-acknowledgements`** (`PortalListDisclosureAcknowledgements`, on `CUSTOMER_PORTAL_DISCLOSURES`): the member's acknowledgements, newest first, with `recordedBy` (SELF, BROKER or STAFF) and, for BROKER, `intermediaryCode`, `presentationMethod` and `presentationDetail`.
- **Staff: the switch** (screen `INTEGRATION_DISCLOSURE_SETTING`, action `CHANGE_DISCLOSURE_SETTING`, permission `disclosure_setting.update`):
  - **`GET /integration/disclosure-setting`** (`IntegrationGetDisclosureSetting`, ETag): `{brokerAcknowledgementAllowed, isDefault, rowVersion, pendingChange}`. `pendingChange` is null, or `{brokerAcknowledgementAllowed, requestedBy, requestedAt}` for a change waiting for approval; `brokerAcknowledgementAllowed` stays the value in effect until it is granted. `isDefault` is true until the tenant first asks for a change (superseded above: until a change is granted).
  - **`PUT /integration/disclosure-setting`** (`IntegrationChangeDisclosureSetting`, body `{brokerAcknowledgementAllowed}`) now **answers 202** (was 204) with the same view, its `pendingChange` set, and the new ETag. If-Match is required once the tenant has its own setting (412 otherwise). The change waits for a Workflow approval: **approval type `DISCLOSURE_SETTING_CHANGE`, business reference type `DISCLOSURE_SETTING`** (new enum values of `approvalType` and `businessRefType`; the reference opens `INTEGRATION_DISCLOSURE_SETTING`). The requester can't approve it (`MAKER_CHECKER_SAME_PERSON`). Granted, it applies; rejected, expired or withdrawn, nothing changes. While one waits, another PUT is `409 INVALID_STATE_TRANSITION`. Show the pending change and who asked, and that it waits for a second person.
  - **Role templates:** `CHANGE_DISCLOSURE_SETTING` is held by `COMPLIANCE_OFFICER` only, which also approves `DISCLOSURE_SETTING_CHANGE`. The screen stays with `OPERATIONS_ANALYST`, `INTEGRATION_ADMINISTRATOR` and `AUDITOR` (read only), and comes to `COMPLIANCE_OFFICER` with the action. The menu follows from `/identity/me/menu` as ever.
- **`DISCLOSURE_REQUIRED`**'s remedy now names the broker; its code and meaning are unchanged.
- `docs/api/openapi-core.json` (and the operator and static data documents' shared enums), `error-codes.json`, `R__error_code_catalogue.sql`, `R__function_catalogue.sql` (741 functions) and the role-templates addendum regenerated. V0160.

## Static data: a draft left untouched is discarded by another maintainer, and its maker is told (DE-016, OQ-204; Ade, 25 November 2026; branch `stale-draft-discard`)
- **`POST …/static/{table}/{code}/versions/{versionNumber}/discard`** (`Discard{Table}Draft`, `…Global`) takes an optional body `{ "reason": string }` (`StaticDraftDiscard`). Its maker discards as before, with no body (audited `DiscardDraft`). Anyone else holding the table's maintain function (`SD_<module>_MAINTAIN`, the route's function) may discard it once it has been untouched for the tenant's `staleDraftDiscardDays` (24-hour periods since the later of `createdAt` and `editedAt`): `reason` is then required, at most 500 characters (422 `VALIDATION_FAILED`, `errors.reason`), and it is audited `DiscardStaleDraft` with the reason, `makerId`, `daysUntouched` and `staleDraftDiscardDays`. Sooner: **409 `STATIC_DRAFT_NOT_STALE`** (new code) with `discardableFrom` and `staleDraftDiscardDays` in the problem. **Changed, for the front end (Ade, 25 November 2026):** a non-maker on a fresh draft got 403 `STATIC_MAKER_ONLY`; it now gets 409 `STATIC_DRAFT_NOT_STALE`. Show its `discardableFrom`, the time from which they may discard it, and don't treat it as a permission refusal. Without the maintain function: 403 `FUNCTION_NOT_GRANTED`, as before.
- **Every static data record** (GET …/{code}/versions, the list, creates and edits) has `editedAt` (when its maker last edited the draft, null until then; V0159 column `edited_at` on every static table) and `discardableByOthersFrom` (versions only: when a draft never submitted and not of an import becomes discardable by others; null otherwise and on the list). **`StaticVersionNotInForce`** (the pending list) has both too.
- **`GovernanceSettingsView`** (`GET /api/v1/static-data/settings`) has `staleDraftDiscardDays` (7 until changed). **`PUT /api/v1/static-data/settings/stale-draft-discard-days`** (`ChangeStaleDraftDiscardDays`, body `{ "staleDraftDiscardDays": int }`, at least 1, If-Match, 204 with the new ETag, audited, at once, no approval), permission `governance_setting.update`, new function **`STATIC_DATA_CHANGE_STALE_DRAFT_DAYS`** (ACTION under `STATIC_DATA_GOVERNANCE`, not privileged, in TENANT_ADMIN and PLATFORM_STATIC_DATA_STEWARD; Ade, 25 November 2026). The platform operator's setting applies to the global tables.
- **The maker is told** (Ade, 25 November 2026): event `sdm.StaticDataDraftDiscarded` (record reference, makerId, discardedBy, discardedAt, reason), only when someone else discards; Workflow opens an informational **`STATIC_DRAFT_DISCARDED`** task assigned to the maker (queue `REQUESTER`, business reference type **`STATIC_DATA_VERSION`**, the discarded version's ID; screen `STATIC_DATA_CHANGE_REQUESTS`). **`WorkflowMyTaskView.draftDiscarded`** (always present, null for other tasks): `entityCode`, `entityRoute`, `code`, `versionNumber`, `global`, `discardedBy`, `discardedAt`, `reason`; `informational` is true. V0159 adds `workflow.process_instance.notice`.
- `docs/api/openapi-static-data.json`, `openapi-platform-operator.json`, `openapi-core.json`, `error-codes.json`, `R__error_code_catalogue.sql`, `R__function_catalogue.sql` and the role-templates addendum regenerated.

## Disclosures: the refusal names each notice to open; an acknowledgement says what is left (DE-039 part 1; Ade, 25 November 2026; branch `disclosures-gate`)
- **`DISCLOSURE_REQUIRED`** (409; wording unchanged) adds extensions `notices`, `partyId`, `businessRefType` and `businessRefId` (null for a Health enrolment, which has no record yet). `displayPoint` and `noticeTypes` stay.
  - Each notice is `{noticeType, jurisdiction, language, textVersion, availableLanguages}`: the text to open. `language` is the party's preferred language if a text is in force in it, else the tenant's default if one is, else the only language the notice is in force in. With several and none of those (Ade, 25 November 2026), `language` and `textVersion` are null and the person chooses one of `availableLanguages` (always present, by code); there is no "first by code".
- **Acknowledging** (`POST /integration/disclosure-acknowledgements`, `POST /portal/disclosure-acknowledgements`) answers with `outstanding`: the same notice list, what is still to acknowledge at that point for that party and record; empty means the refused step can be retried.
- **A notice counts as acknowledged** (Ade, 25 November 2026) once its text in force in any one of its languages is acknowledged, in that text's version; a new version in force needs a new acknowledgement. Before, each language's text was checked separately by version.

## Static data: versions not in force listed, drafts discarded (DE-015, DE-016; branch `static-pending-discard`)
- **`GET …/static/{table}/pending`** on every static data table (`List{Table}Pending`; `…/global/pending`, `List{Table}PendingGlobal`, in the platform operator document): the versions the table's list leaves out, oldest first, as a cursor page (`items`, `nextCursor`; `cursor`, `limit`). Filter `status`: `DRAFT`, `SUBMITTED` (waiting for approval) or `APPROVED` (waiting to be activated); all three when omitted, 422 `VALIDATION_FAILED` for any other value. Each item (`StaticVersionNotInForce`): `id`, `code`, `versionNumber`, `status`, `makerId` (null for a seeded row), `effectiveFrom`, `effectiveTo`, `changeRequestId` (null until submitted), `importBatchId`, `createdAt`, `rowVersion`. Read permission as the table's list. 295 tenant and 84 platform operations.
- **`POST …/static/{table}/{code}/versions/{versionNumber}/discard`** on every table a caller may write (`Discard{Table}Draft`; `…Global` for the platform tables): needs `If-Match` (428 missing, 412 stale; the version's `rowVersion` from `GET …/{code}/versions` or the pending list), answers 204. Only a draft never submitted is discarded, by its maker only (403 `STATIC_MAKER_ONLY`); 409 `INVALID_STATE_TRANSITION` for any other status, for a draft a rejection returned and for a line of an import. The row is deleted and audited (`DiscardDraft`); the code is free for a new draft. Update permission as the table's other lifecycle actions. 254 tenant and 84 platform operations.
- **`STATIC_DRAFT_EXISTS`** (409 on create): the code and its catalogue wording are unchanged; the `detail` now names the version in progress, its status and its way out (submit it or discard it, activate it, or wait for its approval).
- No function, permission or error code added; `docs/api/openapi-static-data.json` and `docs/api/openapi-platform-operator.json` regenerated.

## Role templates: the dead ends closed (DE-001 to DE-005, DE-014, DE-015, DE-019, DE-021, DE-028, DE-044, DE-058; Ade, 24 November 2026; branch `role-templates-de`)
- **Two new functions, two new permissions (Ade, 25 November 2026), and V0155;** the catalogue has 731 functions. No path, body, field, enum or error code changed. `docs/api/openapi-core.json` regenerated:
  - **`PARTY_DECIDE_MATCH`** (ACTION under `PARTIES`, no operation of its own) grants **`party.resolve_match`**, which a `PARTY_MATCH_RESOLUTION` approval now names as its `requiredPermission` (was `party.update`): `WorkflowApprove` and `WorkflowReject` list it in their additional permissions. Proposing a resolution (`PartyResolveMatch`, `PARTY_RESOLVE_MATCH`) still needs `party.update`. Onboarding readiness's `APPROVAL_AUTHORITY` lists `PARTY_MATCH_RESOLUTION` while no role with authority for it holds `party.resolve_match`. An approval requested before this keeps `party.update` as its required permission.
  - **`DOCUMENTS_RECORD_HOLDS`** (SCREEN, route `documents/legal-holds`, not privileged) covers `DocumentsListRecordHolds` and `DocumentsGetRecordHold`, which now need **`legal_hold.read`** (was `legal_hold.manage`); `DOCUMENTS_PLACE_LEGAL_HOLD` no longer covers them. V0155 gives `DOCUMENTS_RECORD_HOLDS` to every tenant role that grants `DOCUMENTS_PLACE_LEGAL_HOLD`, so whoever read record holds still does.
- **`GET /identity/role-templates` and `/{code}`** return different contents (`docs/specs/addenda/role-templates.md` lists each template's functions):
  - the Workflow inbox (`WORKFLOW_APPROVALS`, `WORKFLOW_APPROVE`, `WORKFLOW_REJECT`) in `LIFE_UNDERWRITER`, `GENERAL_UNDERWRITER`, `PRODUCT_MANAGER`, `FINANCE_OFFICER`, `DISTRIBUTION_ADMINISTRATOR`, `REINSURANCE_OFFICER`, `INTEGRATION_ADMINISTRATOR`, `HEALTH_CLAIMS_ASSESSOR`, `DATA_PROTECTION_OFFICER`;
  - `STATIC_DATA_CHANGE_REQUESTS` in every template that maintains or approves static data;
  - `GENERAL_UNDERWRITER`: the `GENERAL_RENEWALS` sub-domain (renewals and retention cases), approves `GENERAL_NON_RENEWAL`;
  - `HEALTH_CLAIMS_ASSESSOR`: `HEALTH_PROVIDER_PERFORMANCE`, `HEALTH_RECORD_PROVIDER_QUALITY_REVIEW`, `HEALTH_RECOVERIES` (screen); approves `PROVIDER_RECOVERY_FINDING`;
  - `FINANCE_MANAGER`: `COMMISSION_STATEMENTS`, `CLAIM_PAYMENT_INSTRUCTIONS`, `SERVICE_COMPLAINTS` (screens only); approves `PROVIDER_CREDIT_NOTE`, `PROVIDER_RECOVERY_WRITE_OFF`;
  - `COMPLIANCE_OFFICER`: `DOCUMENTS` and `DOCUMENTS_RECORD_HOLDS` (screens only) and the Party and Audit static data (`SD_PARTY_MANAGEMENT_*`, `SD_AUDIT_*`);
  - `CLAIMS_MANAGER`: the Claims Framework's static data (`SD_SHARED_CLAIMS_FRAMEWORK_*`), approves `STATIC_DATA_T1`, `STATIC_DATA_T2`;
  - `TENANT_ADMIN`: the Communications, Documents and Analytics static data (`SD_COMMUNICATIONS_*`, `SD_DOCUMENT_AND_CONTENT_MANAGEMENT_*`, `SD_ANALYTICS_*`), and `INTEGRATION_PARKED_DELIVERIES` (screen only: no retry or discard), as the second escalation's role (OQ-072);
  - `AUDITOR`: `DOCUMENTS_RECORD_HOLDS`, as every staff screen;
  - `DATA_PROTECTION_OFFICER`: the eight data-subject request functions (`PARTY_DATA_SUBJECT_REQUESTS` and its actions), `PARTY_DECIDE_MATCH` (not `PARTY_RESOLVE_MATCH`: no party editing) and `DOCUMENTS_RECORD_HOLDS`; approves `PARTY_MATCH_RESOLUTION`. The underwriters, claims handler, claims manager, Health claims assessor, Health membership administrator and customer service agent no longer hold the data-subject request functions.
- **Menus** (`GET /identity/me/menu`) change only for users whose roles a tenant builds from the new templates; a tenant's existing roles are never changed.
- **`GET /integration/parking-setting`:** with no setting of the tenant's own (`isDefault: true`), `configurationOwnerRole` is now `INTEGRATION_ADMINISTRATOR` (was `TENANT_ADMIN`), so the first escalation's tasks go to that queue. F009's configuration review tasks still default to `TENANT_ADMIN`; a tenant's stored setting is unchanged and names the role for both; the second escalation still goes to `TENANT_ADMIN` (OQ-072).

## Money group: failed payouts, nil and negative commission statements, credit notes needing approval, discarding dead letters (DE-007, DE-008, DE-009, DE-010, DE-011, DE-031; Ade, 24 November 2026; branch `money-group`, V0150 to V0154)
- **Typed reference:** every new step that moves money, or discards what would have, takes the reference of what it acts on in its body; a mismatch is 422 `TYPED_REFERENCE_MISMATCH` (new code) and changes nothing. A failed payout's reference is its payment's `paymentRef`.
- **Payments:** `GET /payments/reconciliation-exceptions` is now a cursor page (`items`, `nextCursor`; pass `cursor`), not an array (DE-031). Exception type `PAYMENT_FAILED` is new: a failed outbound payment, with `paymentRef`, `businessRefType`, `businessRefId`; INSYS resolves it itself (`resolution`) when a later attempt pays the record or its module gives the payout up.
- **Billing:** refund states `PaymentFailed` and `Cancelled`; `POST /billing/refunds/{id}/retry-payout` `{paymentRef, reason, bankAccountId?}` (a PAYOUT_RETRY approval) and `POST /billing/refunds/{id}/cancel` `{paymentRef, reason}`. `POST /billing/invoices/{id}/credit-notes` answers 202 with a credit note request when the reason needs approval (CREDIT_NOTE), 201 as before otherwise; `GET /billing/credit-note-requests/{id}`; the invoice view lists `creditNoteRequests`.
- **Health:** remittance `paymentStatus` (NONE, PENDING, PAID, FAILED), `failedPaymentRef`, `attempt`, `reissuePending`; `POST /health/payment-runs/{id}/remittances/{remittanceId}/reissue` `{paymentRef, reason, bankAccountId?}`; a run is Reconciled only when every remittance with an amount is paid. A fully credited recovery invoice returns the recovery to `Open` for review; the recovery view lists its `credits`.
- **Workflow:** redress state `PaymentFailed`; `POST /workflow/cases/{id}/redress/{redressId}/retry-payout`; four case history event types for it.
- **Distribution:** statement states `ClosedNil`, `CarriedForward`, `SettledByCarryForward`, `Invoiced`, `PaymentFailed`; `POST /distribution/statements/{id}/retry-payout`, `POST /distribution/statements/{id}/invoice-clawback` `{reference, reason}` (reference `{intermediary}/{period}`); list filters `status` and `invoiceDue=true`; new view fields (`reference`, `openingAmount`, `carriedOn`, `carriedAmount`, `carriedOwed`, `carriedDays` and others). Tenant setting `clawback_invoice_after_days` (default 90) on broker change settings.
- **Integration:** `POST /integration/dead-letters/{id}/discard` now needs a body `{messageRef, reason}`; 204 when discarded (and the deliveries behind it released), 202 when a money message's discard waits for a second person (`DEAD_LETTER_DISCARD`); status `DiscardRequested`, with `discardRequestedBy`, `discardRequestedAt`, `discardReason`.
- **Approval types:** `PAYOUT_RETRY`, `CREDIT_NOTE`, `COMMISSION_CLAWBACK_INVOICE`, `DEAD_LETTER_DISCARD`; business ref types `HEALTH_REMITTANCE`, `CREDIT_NOTE_REQUEST`, `DEAD_LETTER`. The finance manager template approves all four. Endings: `docs/frontend-integration-guide.md` §1.

## Keycloak login theme (BR-168, 22 November 2026; branch `keycloak-theme`)
- **No API change:** the chart and the Preview's scripts only (the console's Keycloak login theme `insys`); no OpenAPI document, error code or field changed.

## Tenant administrator requests: a grant not applied ends `NotApplied` (OQ-099 follow-up; decided by Ade, 21 November 2026; branch `first-admin-not-applied`, V0149)
- **`TenantAdministratorRequestView`** (`GET /identity/tenant-administrators/{id}`, `GetTenantAdministratorRequest`, and each item of `GET /identity/tenant-administrators`, `ListTenantAdministratorRequests`):
  - `status` gains the value **`NotApplied`**: the request was granted in Workflow but the grant couldn't be applied (the new tenant has had users since the request, the tenant's `TENANT_ADMIN` role is no longer in force, a username is now taken, or the functions hold both sides of a platform baseline segregation-of-duties rule). A final state: nothing is created in the tenant. Such a request used to stay `PendingApproval` for ever.
  - New field **`notAppliedReason`** (string or null, always present): why, in plain text with no personal data, for example "The tenant already has users." or the `ROLE_SEGREGATION_OF_DUTIES` message naming the pair and the rule; null unless `NotApplied`. For `NotApplied`, `decidedBy` is the approval's checker and `decidedAt` when the request ended.
- **Unchanged:** the Workflow approval still reads `NotApplied` (`GET /workflow/approvals/{id}`) and the requester still gets the follow-up task. A `NotApplied` request no longer blocks a new request for the tenant (409 `DUPLICATE_RECORD` counts only `PendingApproval`).
- `docs/api/openapi-platform-operator.json` regenerated (the enum, the field, and the four operations' descriptions). No path or body changes, no new error code.
- **For the front end's coverage list:** a new status value (`NotApplied`) and a new field (`notAppliedReason`) on `GetTenantAdministratorRequest` and `ListTenantAdministratorRequests`; no new operation.

## Opening a case the caller can't read: a receipt, not 404 (OQ-196, approved by Ade 21 November 2026; branch `oq196-receipt`)
- **`POST /workflow/cases`** (`WorkflowOpenCase`) answered 404 after creating a case the caller can't read: a Health-linked case (a `CLAIM` link to a Health claim or one whose line isn't known, or a `MEMBERSHIP`, `PROVIDER`, `REMITTANCE` or `PAYMENT_RUN` link) opened by staff without `medical_claim.read` (`cases.md` §9), or a case outside their scope. The case was created all the same, and a retry with a new `Idempotency-Key` opened a second one. It now answers **`201 Created`** with `Location: /api/v1/workflow/cases/{id}` and a **`CaseOpenedReceipt`** (schema `WorkflowCaseOpenedReceipt`), with no ETag:
  ```json
  { "id": "<uuid>", "caseNumber": "<string>", "status": "<WorkflowCaseStatus, e.g. Received>", "readable": false }
  ```
  Nothing else of the case is in it. `GET /workflow/cases/{id}` is still 404 to that caller.
- **A caller who can read the case** gets the full `CaseView` with its ETag, unchanged; `CaseView` has no `readable` field. Tell the two apart by `readable` (present and false only on the receipt).
- **Retries:** a retry with the same `Idempotency-Key` returns the same 201 receipt (`Idempotent-Replayed: true`).
- The 201 response is now `oneOf` `WorkflowCaseView` or `WorkflowCaseOpenedReceipt`; the operation's description says when each comes back. `docs/api/openapi-core.json` regenerated.
- **For the front end's coverage list:** a new response schema, `WorkflowCaseOpenedReceipt`, on `WorkflowOpenCase`'s 201; no new operation, no new error code.

## First administrators: no role with both sides of a baseline rule (SRS-002, OQ-099, OQ-197; approved by Ade, 21 November 2026, merged `0bd03b5`)
- **`POST /identity/tenant-administrators`** (`ProvisionTenantAdministrator`): `functions` that hold both sides of a platform baseline segregation-of-duties rule (`CLAIMS_DRAFT_INSTRUCTION` / `CLAIMS_RELEASE_INSTRUCTION`, `PAYMENTS_BUILD_BATCH` / `PAYMENTS_RELEASE_BATCH`, `PARTY_ADD_BANK_ACCOUNT` / `PARTY_BANK_ACCOUNT_VERIFY`) are refused with 422 `ROLE_SEGREGATION_OF_DUTIES` on `functions`, one message per rule naming the pair and the rule. Checked before the catalogue checks.
- **The back end's rule** for the request is unchanged (each function a staff function in force that the tenant can hold) and now also refuses both sides of a baseline rule in one role.
- **Approving** a first-administrator request made with both sides (before this check) is not applied: the approval is marked not applied and nothing is created in the tenant. (Since the follow-up above, the request also ends `NotApplied`.)
- **For the front end:** its D-44 ("every staff function", its live-run choice) must leave one side of each baseline pair out, or give the two sides to separate roles later; INSYS's `TENANT_ADMIN` template already holds neither side. No path, body or response shape changes; the existing code `ROLE_SEGREGATION_OF_DUTIES`, no new one. `docs/api/openapi-platform-operator.json` regenerated (the operation's description).

## Segregation of duties: the platform baseline and the action-time check (SRS-002, 20 November 2026; Ade's decisions, OQ-197; branch `sod-baseline`)
- **The baseline is in force for every tenant:** `SOD_CLAIM_PAYMENT` (`CLAIMS_DRAFT_INSTRUCTION` / `CLAIMS_RELEASE_INSTRUCTION`), `SOD_PAYOUT_BATCH` (`PAYMENTS_BUILD_BATCH` / `PAYMENTS_RELEASE_BATCH`) and `SOD_PAYEE_ACCOUNT` (`PARTY_ADD_BANK_ACCOUNT` / `PARTY_BANK_ACCOUNT_VERIFY`), as BLOCK, whatever the tenant's rows in `identity/static/segregation-of-duties-conflict-rules`. The tenant's own rules on other pairs still apply. So `SEGREGATION_OF_DUTIES` (409, assigning a role) and `ROLE_SEGREGATION_OF_DUTIES` (422, a role's grant) now apply in every tenant.
- **New refusal `SEGREGATION_OF_DUTIES_BLOCKED` (403):** a user whose role assignments in effect give both sides of a BLOCK rule in force is refused the rule's release or verify side (function B) at every operation carrying it: `POST /claims/payment-instructions/{id}/release`, `POST /payments/payout-batches/{id}/release`, and deciding a `BANK_ACCOUNT` verification approval (`POST /workflow/approvals/{id}/approve` or `/reject`, which needs `bank_account.verify`). The draft, build and add sides still work. The refusal is audited (`ACCESS` / `SegregationOfDutiesRefused`, target the function). The menu (`GET /identity/me/menu`) leaves the refused function out. For a tenant's own rule, function B of its row is the side refused.
- **New refusal `SEGREGATION_BASELINE_PROTECTED` (422):** submitting a rule row on a baseline pair (in either order) as WARN, with `effectiveTo`, or a new version moving a baseline row to another pair; and `deprecate` or `retire` of a baseline row.
- **`ROLE_SEGREGATION_OF_DUTIES` widened:** a grant giving role X one side is refused too when a user holding X (active or waiting for approval) already holds the other side through another role. Its meaning in the catalogue says so.
- **New read `GET /identity/access-reviews/segregation-conflicts`** (`IdentityListSegregationConflicts`, permission `access_review.read`, function `IDENTITY_ACCESS_REVIEWS`): an array of `{ userId, rule, baseline, functionA, functionB, refusedFunction, rolesGivingA, rolesGivingB }`, one per user and BLOCK rule in force today whose two sides the user's assignments give. Names come from the display-name lookup.
- **Access review items gain `segregationConflicts`** (`GET /identity/access-reviews`, `GET /identity/access-reviews/{id}`): the rule codes in force today whose two sides the item's user holds, with the item's role giving one of them; empty when none.
- No other path, body or response shape changes. `docs/api/error-codes.json` and the OpenAPI documents regenerated.
- **For the front end's coverage list:** one new operation, `IdentityListSegregationConflicts` (`GET /identity/access-reviews/segregation-conflicts`); one new response field, `segregationConflicts` on access review items; two new error codes, `SEGREGATION_OF_DUTIES_BLOCKED` (403) and `SEGREGATION_BASELINE_PROTECTED` (422), with AI-2 cases `R-SEGREGATION_OF_DUTIES_BLOCKED` and `R-SEGREGATION_BASELINE_PROTECTED`. `ROLE_SEGREGATION_OF_DUTIES`'s meaning is widened (case `R-ROLE_SEGREGATION_OF_DUTIES` changed).

## BR-100: each case screen's own reads (Ade, 20 November 2026, OQ-196)
All added, none breaking. ADR-026 is unchanged: each new operation has one `x-function`, the case screen's own. The complaints screen's reads (`GET /workflow/cases`, `/{id}`, `/{id}/redress/{redressId}`, `/summary`) stay under `SERVICE_COMPLAINTS`.
- **New operations,** 14 in all: a list (the filters of `GET /workflow/cases` without `family`; cursor paged) and a case (`/{id}`, with ETag), limited to the screen's cases. A case the screen doesn't read is 404 on its `/{id}`. Scope (F103) and the Health rule (`medical_claim.read`) apply as on the complaints reads.

  | Function | List | Case | Its cases |
  |---|---|---|---|
  | `SERVICE_ENQUIRIES` | `GET /workflow/cases/enquiries` (`WorkflowListEnquiries`) | `GET /workflow/cases/enquiries/{id}` (`WorkflowGetEnquiry`) | family `ENQUIRY` |
  | `SERVICE_DISPUTES` | `GET /workflow/cases/disputes` (`WorkflowListDisputes`) | `…/disputes/{id}` (`WorkflowGetDispute`) | family `DISPUTE` |
  | `SERVICE_APPEALS` | `GET /workflow/cases/appeals` (`WorkflowListAppeals`) | `…/appeals/{id}` (`WorkflowGetAppeal`) | family `APPEAL` |
  | `CLAIM_DISPUTES` | `GET /workflow/cases/claim-disputes` (`WorkflowListClaimDisputes`) | `…/claim-disputes/{id}` (`WorkflowGetClaimDispute`) | disputes with a `CLAIM` link to a General or Life claim |
  | `CLAIM_APPEALS` | `GET /workflow/cases/claim-appeals` (`WorkflowListClaimAppeals`) | `…/claim-appeals/{id}` (`WorkflowGetClaimAppeal`) | appeals with a `CLAIM` link to a General or Life claim |
  | `HEALTH_CLAIM_APPEALS` | `GET /workflow/cases/health-claim-appeals` (`WorkflowListHealthClaimAppeals`) | `…/health-claim-appeals/{id}` (`WorkflowGetHealthClaimAppeal`) | appeals with a `CLAIM` link to a Health claim or one whose line isn't known, or a `MEMBERSHIP`, `PROVIDER`, `REMITTANCE` or `PAYMENT_RUN` link, or raised by a provider |
  | `PROVIDER_DISPUTES` | `GET /workflow/cases/provider-disputes` (`WorkflowListProviderDisputes`) | `…/provider-disputes/{id}` (`WorkflowGetProviderDispute`) | disputes with a `PROVIDER`, `REMITTANCE` or `PAYMENT_RUN` link, or raised by a provider |
- **The claim and Health screens by the case's line (OQ-196, option B, Ade 20 November 2026):** a `CLAIM` link now records its claim's line (GENERAL, LIFE or HEALTH) from the claim modules, so a Health claim appeal linking only its medical claim is under `HEALTH_CLAIM_APPEALS`, not `CLAIM_APPEALS`. A case with a `CLAIM` link to a Health claim, or to a claim whose line isn't known (fail closed), is Health-linked: every staff case read (the complaints screen's and each screen's) leaves it out, or answers 404, without `medical_claim.read` (`cases.md` §9). That includes `POST /workflow/cases`, which answers with the case as its reader sees it: 404 for such a case opened by staff without `medical_claim.read`, as already for the other Health links. Links made before this change have no line until the `CASE_TIMELINES` job fills it in. No path, body or response shape changes; only the summaries and descriptions of `WorkflowListCases`, the claim and Health screens' operations.
- **For the front end:** each case screen reads through its own operations, so a role holding only that screen (a disputes clerk, say) can open it. The case's actions are unchanged, still under `SERVICE_COMPLAINTS` (BR-100 asked only for the reads).
- **For the front end's coverage list:** no operation is added or removed by OQ-196 (B); `WorkflowListClaimDisputes`, `WorkflowGetClaimDispute`, `WorkflowListClaimAppeals`, `WorkflowGetClaimAppeal`, `WorkflowListHealthClaimAppeals` and `WorkflowGetHealthClaimAppeal` now read by the claim's line, and a case linking a Health claim needs `medical_claim.read` on every case read.

## Privileged access window refusal wording (20 November 2026, Ade)
- `ACCESS_WINDOW_TOO_LONG` (422, `POST /identity/privileged-access`): the detail no longer cites TEN-007, which sets no maximum, and gives the limit in words with the setting it comes from: "Privileged access can be asked for at most 8 hours at a time (the Identity:PrivilegedAccess:MaxWindow setting)." It used to read "at most 08:00:00 (TEN-007)". Code, status, message key and the catalogue's meaning are unchanged.

## Segregation of duties: the seed's rules and a role grant's check (SRS-002, 20 November 2026; approved by Ade, OQ-197)
- **New refusal `ROLE_SEGREGATION_OF_DUTIES` (422, on `functionCode`):** submitting a role's function grant (`POST /identity/static/role-function-assignments/{code}/versions/{n}/submit`, or the import's submit) when the role's grants in force or on their way, the import's other lines and this one would hold both functions of a BLOCK rule in force (`identity/static/segregation-of-duties-conflict-rules`). The message names both functions and the rule. Without a rule in force, nothing changes.
- **The OFTZA sample:** three BLOCK rules in force, `SOD_CLAIM_PAYMENT` (`CLAIMS_DRAFT_INSTRUCTION` / `CLAIMS_RELEASE_INSTRUCTION`), `SOD_PAYOUT_BATCH` (`PAYMENTS_BUILD_BATCH` / `PAYMENTS_RELEASE_BATCH`) and `SOD_PAYEE_ACCOUNT` (`PARTY_ADD_BANK_ACCOUNT` / `PARTY_BANK_ACCOUNT_VERIFY`). So the assignment refusal `SEGREGATION_OF_DUTIES` (409) now applies there too.
- No path, body or response shape changes.

## Access review refusal wording (20 November 2026, front end's security capture)
- `REVIEW_INCOMPLETE` (409, `POST /identity/access-reviews/{id}/complete`): the title (the catalogue's meaning) and the detail no longer repeat each other. The meaning is now "The review can't be completed while some of its items are neither confirmed nor revoked." and the remedy "Confirm or revoke each remaining item, then complete the review."; the detail says how much is left, "1 of 2 items is still undecided." The code, status and message key are unchanged. `docs/api/error-codes.json` regenerated.

## Batch 1: revoking an invitation, portal readiness, abandoned SETUP batches (19 November 2026)
All additive; no path, body or response shape changes.
- **`POST /identity/users/{id}/disable`** now accepts an `Invited` user (register row 31): it revokes the invitation. Before, it answered 409 `INVALID_STATE_TRANSITION`. The user's status becomes `Disabled`; its token is refused from the next request.
- **Readiness `CHANNELS`** (`GET /tenancy/tenants/{id}/readiness`, free-text `missing`) gains, for each portal feature the tenant is entitled to: `a BROKER channel (audience BROKER), as the tenant has BROKER_PORTAL`, and `ROLE: FUNCTION (AUDIENCE channel)` lines (OQ-165). A role whose every function is a portal function is no longer listed as a staff role.
- **Static data change requests:** a SETUP set's batch that was approved but not in force now reads `REJECTED` once its set is abandoned (OQ-183); before, it stayed `APPROVED`.

## Review-gap batch: an AI-1 review can always be finished (Ade, 19 November 2026, OQ-190)
- **New function `ASSIST_PRODUCT_DRAFT_REVIEW`:** an action under Product Factory's `PRODUCTS` screen, staff, not privileged, no feature, in the product manager's template. It holds three operations that were `ASSIST_PRODUCT_DRAFT_RUN`'s:
  - `GET /assist/product-drafts/{id}`;
  - `POST /assist/product-drafts/{id}/items/{itemId}/decision`;
  - `GET /assist/product-drafts/{id}/simulation-cases`.
  Their `x-function` changes accordingly; paths, bodies and responses are unchanged. Only the run's configurator decides, as before (404 for anyone else).
- **`ASSIST_PRODUCT_DRAFT_RUN`** now holds only `POST /assist/product-drafts`, still under `AI_ASSIST`. A configurator needs both functions: the run's to start, the review's to read and decide.
- **Submission's hold** (`PRODUCT_AI_REVIEW_INCOMPLETE`, 422) now applies to every version AI-1 drafted, whether or not the tenant has AI Assist today. Before, a tenant without the feature wasn't held.
- **The compliance officer** holds `PRODUCTS` for viewing only, with the provenance report: no product maintain or approve action (tested).
- **New refusal `ASSIST_RUN_NEEDS_REVIEW` (422):** a role granted `ASSIST_PRODUCT_DRAFT_RUN` must also hold `ASSIST_PRODUCT_DRAFT_REVIEW`.
  - Refused: submitting the run's grant (`POST /identity/static/role-function-assignments/{code}/versions/{n}/submit`, or an import line) without the review in force or on its way; ending the review (a version with `effectiveTo`, or `deprecate`/`retire`) while the run stays; and `POST /identity/tenant-administrators` naming the run without the review.
  - Grant the review first, then the run; end the run first, then the review.
- **For the front end:** show the run's review and the decision actions under `ASSIST_PRODUCT_DRAFT_REVIEW` from the menu, not under `ASSIST_PRODUCT_DRAFT_RUN`; with AI Assist off, the review stays available and starting a run doesn't.

## Wave 7 batch 7.8: AI-3's tailoring and document mapping (Ade, 19 November 2026: OQ-186 to OQ-188)
All added, none breaking. Nothing AI-3 proposes takes effect until a person accepts it and a second person approves the SETUP change set.
- **Documents on a setup** (D-16, as AI-1's): `POST /assist/tenant-setups/{id}/documents` (If-Match, Idempotency-Key) with `{ documents: [{ documentId, confirmsNoPersonalData }] }`; `GET …/documents`; `DELETE …/documents/{documentId}` (If-Match).
  - Each is your own upload, cleared by the scan, at most 20 MB and 10 a setup, filed under a category that holds no personal data. Refusals: `ASSIST_CONFIRMATION_REQUIRED`, `ASSIST_NOT_UPLOADER`, `ASSIST_DOCUMENT_NOT_ALLOWED`, `DOCUMENT_QUARANTINED`.
  - The view's ETag is the setup's: attaching or removing moves it.
- **AI passes:** `POST /assist/tenant-setups/{id}/ai-passes` (If-Match, Idempotency-Key) answers **202** with the pass; a job does it as you. `GET …/ai-passes` lists them, newest first, each with `status` (`QUEUED`, `RUNNING`, `DRAFTED`, `REFUSED`, `FAILED`), `outcomeCode`, `tailored`, `mapped` and `unmapped`.
  - Needs the tenant's `AI_ASSIST` entitlement (`FEATURE_NOT_ENTITLED` otherwise), AI Assist's settings and cap. The pack-only draft needs neither (AIA-090).
  - New codes: `ASSIST_SETUP_PASS_OPEN` (409, one pass at a time) and `ASSIST_SETUP_CHANGED` (409, the answers changed or the setup was submitted while it ran, so it recorded nothing).
  - A refused pass names the document's refusal (`ASSIST_DOCUMENT_IDENTIFIERS` with each identifier's kind and page, never its value, on `GET …/documents`).
- **Each value** (`GET /assist/tenant-setups/{id}`) gains `aiPassId`, `citationDocumentId`, `citationPage`, `citationQuote` (a `MAPPED` value's source) and `citedAnswer`, `citedAnswerValue` (a `TAILORED` value's).
  - `TAILORED`: a pack row's value, one named column set to a named answer's value. `MAPPED`: a new row beside the pack's, on `finance.mst_chart_of_accounts` (`ACC-<account code>`), `workflow.cfg_delegation_and_approval_authority` (`AUTH-<transaction type>-<role>`) or `iam.cfg_roles_and_permission_sets` (a role template's code, the title as its English label, never permissions).
  - Both are undecided: the completeness check lists them `UNDECIDED` until accepted or rejected.
  - A new pass replaces only undecided values (Ade, 19 November 2026). An accepted or rejected value keeps its decision, its `aiPassId` and its audit.
  - New answers keep a tailored value only while its answer still holds it; mapped rows stay.
- **`unmapped`:** each line the model couldn't map (`NOT_MAPPED`) and each proposal INSYS dropped, with why: `NOT_TAILORABLE`, `COLUMN_NOT_TAILORABLE`, `ANSWER_NOT_NAMED`, `NOT_IN_ANSWER`, `UNKNOWN_TABLE`, `UNKNOWN_COLUMN`, `MISSING_COLUMN`, `NOT_ALLOWED`, `NOT_IN_QUOTE`, `CURRENCY_MISSING`, `CODE_IN_PACK`, `DUPLICATE`, `MALFORMED`, `NO_CITATION`, `CITATION_NOT_FOUND`, `CITATION_TOO_SHORT`.
- **Packs:** a row may carry `tailor: { columns, from }` (docs/specs/starter-packs/README.md). The review sheet's rows gain `tailorColumns` and `tailorFrom`.
- **Mapped text is limited to its column's length** (`account_code` 20, `gl_account` 30, `name` 100, a role's title 100): a longer value is dropped `MALFORMED`.
- **Stand-in model:** with `Assist:Provider=development`, the published test document `docs/frontend/ai3-sample-tenant-documents.pdf` gets fixed mappings, one dropped for a wrong figure, and one title left unmapped.

## AI-1's submission check, and the provenance report's function (19 November 2026)
- **`POST /product-factory/products/{productCode}/versions/{n}/submit`** gains a refusal: 422 `PRODUCT_AI_REVIEW_INCOMPLETE` for a version AI-1 drafted while an Interpreted value isn't reviewed, or a version field differs from its decision (OQ-190 (b)). The detail gives the counts. Any other version is unaffected. New in `docs/api/error-codes.json`.
- **`ASSIST_PRODUCT_DRAFT_PROVENANCE`** in the menu is now an action under the `PRODUCTS` screen (Product Factory), not under AI Assist's settings, and needs no feature. It is in the product manager's, compliance officer's and auditor's role templates. The endpoint is unchanged.

## Wave 7 batch 7.5: AI-1's review, provenance and simulation cases (19 November 2026)
All added, none breaking.
- **`POST /assist/product-drafts/{id}/items/{itemId}/decision`** (`ASSIST_PRODUCT_DRAFT_RUN`, If-Match the item's ETag), body `{ decision, value, note }` (AIA-019).
  - `decision` is `ACCEPTED`, `EDITED` (with `value` and `note`) or `REJECTED`. The AI's proposal (`value` on the item) is never changed; an edit is recorded as `editedValue`.
  - A version's own field (`version.name`, `version.coverages`, `version.effectiveFrom`, `version.coverStartsOn`, `version.reinstatementArrears`) is written to the Draft as the user, through Product Factory's edit. A rejected one goes back to the version the Draft was copied from. An effective date is edited, not rejected.
  - Everything else (table rows, rate tables, rules, simulation cases) is recorded only.
  - Refused: a dropped proposal (409 `INVALID_STATE_TRANSITION`); a Missing value except by an edit (422); a value the field doesn't allow (422); a stale If-Match (412); a version Product Factory no longer lets you edit, with its own refusal.
  - Only the run's configurator decides; anyone else gets 404, as for the run.
- **Items** in `GET /assist/product-drafts/{id}` gain `decision`, `editedValue`, `decisionNote`, `decidedBy`, `decidedAt` and `rowVersion`.
- **`GET /assist/product-drafts/{id}/simulation-cases`** (`ASSIST_PRODUCT_DRAFT_RUN`): the accepted and edited simulation cases as lines for Rating's test case library (`rating.cfg_rating_test_case_library`), to import through its own import for a second person's approval (AIA-018). A case without a stated expected premium, or without `currency` and `frequency` in its inputs, is listed under `notExported` with its reason.
- **`GET /assist/product-drafts/provenance/{productCode}/{productVersion}`** (new function `ASSIST_PRODUCT_DRAFT_PROVENANCE`, permission `assist_product_draft.read_provenance`, off by default, in no role template, and not tied to the AI_ASSIST feature): the provenance report for the approver (AIA-020).
  - It lists every proposed value with its flag, citation (file, page, quote), INSYS's check, and outcome (`ACCEPTED`, `EDITED`, `REJECTED`, `UNDECIDED`, `DROPPED`), by whom and when.
  - Each version field gives `draftValue` and `matchesDraft`.
  - A `summary` gives the counts and `reviewComplete`.
  - It answers 404 for a version AI-1 didn't draft or one outside the caller's scope.
  - It isn't attached to the approval in Workflow, and submission doesn't wait on it (OQ-190).
- **Database:** V0146 adds the decision columns to `assist.draft_item`.

## Status guard: an approved row's status moves only forward (V0144, OQ-185, 19 November 2026)
- **No API change.** INSYS's own moves are unchanged. In the database, a direct update that moves an approved static data row's status backwards, such as a published product version back to `DRAFT`, is refused.

## Usability batch: the front end's four requests (18 November 2026)
All added, none breaking.
- **Role labels:** `label` on each role in `GET /identity/me` (`roles[]`) and `GET /identity/roles`. It is the role's own label in the caller's languages (its static row's `labels`), else its role template's name, else null, in which case the client shows `roleCode`.
- **A party filter on `GET /general/claims`:**
  - `partyId` gives the claims where that party is recorded on the claim, in any role (`general.claim_party`).
  - With `partyRole` (a `general.cfg_claim_party_roles` code), only that role. `partyRole` without `partyId` is 422 `VALIDATION_FAILED`.
  - A policyholder's claims are their policies' claims: list the policies by `policyholderPartyId`, then the claims by `policyId`. A General claim records its parties by role, not a single claimant, so the filter is by party (Life's is `claimantPartyId`, Health's `patientPartyId`).
- **Coverage codes' label table:** every `coverageCode` in the core document now has `x-code-list: product.cfg_coverage_and_benefit_library` and `x-code-list-column: code`. The label is the row's `name` (`GET /product-factory/static/coverage-and-benefit-library`). The fields are on `GeneralClaimSummary`, `GeneralClaimView`, `GeneralCoverageRequest`, `GeneralCoverageView`, `GeneralMigratedClaim`, `GeneralNotifyLoss`, `GeneralPortalClaimRequest`, `GeneralPortalClaimView` and `GeneralPortalCoverageView`.
- **`GET /workflow/approvals/decidable-count`** (`workflow_approval.read`) returns `{ count, more }`.
  - These are the approvals `?decidable=true` gives (BR-004), counted up to 99; `more` is true beyond that.
  - It is cached for 30 seconds for each user, so a badge can call it on every page; a new approval or a decision can take up to 30 seconds to show.
- **Database:** V0143 adds an index on `general.claim_party (tenant_id, party_id)` for the party filter. No API change.

## Directory batch: row-level security on tenant.tenant (V0142, Ade, 18 November 2026)
- **No API change.** In the database, a session for a tenant now sees only that tenant's row of `tenant.tenant`. The platform's directory reads see every row through an explicit role (`insys_tenant_directory`), and so do the platform operator's own requests (`platform_operator`, set only by the database owner). A session with no tenant set sees none. The API already allowed only the platform to read tenants.
- **Deployments:** the role scripts (`db/roles/module_grants.sql`) must run after this migration, as the migrate job does with `migrations.createRoles`. A deployment whose roles are made otherwise grants `insys_tenant_directory` to `insys_tenant` with `INHERIT FALSE, SET TRUE`.

## Release batch: a platform-wide row releases only what it can resolve (Ade, 18 November 2026)
- **Event changed, not breaking:** `sdm.StaticDataVersionActivated` gains `entitlementTenant` (the tenant code) and `entitlementFeature`. Both are set only for a feature entitlement (`tenant.cfg_feature_entitlements`) and are null for every other row. A consumer acts for that tenant and feature without reading the global row. Existing consumers ignore them.
- **No API change.** Publishing a platform-wide row retries only the parked deliveries it can resolve, marked 500 at a time. The parking job still retries at most its batch size per tenant per pass. Before (availability batch), it retried every tenant's parks.
  - A feature entitlement releases, in its own tenant only, the `FEATURE_NOT_ENTITLED` and `FUNCTION_DISABLED` parks of the JOB functions that need the feature.
  - A function catalogue row releases those two codes for that function, in every tenant.
  - Any other global code (for example a global billing frequency) releases, in every tenant, the parks whose item is that code.
  - Whatever else a row resolves is retried by the 15-minute retry, as before.

## Availability batch: entitlements and parks in a tenant being onboarded (LQNEWA, 18 November 2026)
- **No API change.** Two behaviour fixes, found from the front end's live run LQNEWA:
  - A delivery no longer parks as `FUNCTION_DISABLED` or `FEATURE_NOT_ENTITLED` because of the 30-second cache. A refusal is read again from the database before it parks.
  - Publishing a platform-wide row (a feature entitlement, the function catalogue, a global code) now retries the parked deliveries of every tenant at once. Before, its event carried the operator's tenant, so a business tenant's parks waited for the 15-minute retry.

## Scope batch 5: full pages, and the portal's claim list (F103, read sweep, 18 November 2026)
- **Breaking, for callers with a product or branch scope:**
  - `GET /general/renewals`, `GET /health/payment-runs` and `GET /documents/legal-holds` now drop the records outside the caller's scope before the page is cut. A page is full whenever enough records in scope exist, and its `nextCursor` reaches the later ones. Before, a page could come back short, and with no cursor, so in-scope records after it couldn't be reached. Rows and fields are unchanged.
  - `GET /portal/health/claims` lists only the claims whose membership is in the caller's scope, as `GET /portal/health/claims/{id}` already decided. Before, the list applied only the relationship.
  - `GET /billing/refunds/{id}` on a billing account with no premium schedules now answers **404** to a scoped caller, as the refund list and Payments already did (OQ-081). Before, a scoped caller could read it.
- An unrestricted caller sees no change.

## Seed batch: the sample tenant's missing configuration, and FEATURE_NOT_ENTITLED (18 November 2026)
- **Changed, for anyone reading parked deliveries** (`GET /integration/parked-deliveries`, the operations view, `/health`): a consumer whose JOB function needs a feature the tenant isn't entitled to now parks with prerequisite code **`FEATURE_NOT_ENTITLED`** (item: the JOB function, for example `LIFE_EVENT_PROCESSING`). Before, it parked as `FUNCTION_DISABLED`, which wrongly said the tenant had switched the function off. `FUNCTION_DISABLED` now means only that: switched off by the tenant, or not in force in the catalogue. Retrying is unchanged: entitling the tenant publishes configuration, which retries its parked deliveries. No endpoint, field or schema changed.
- **New prerequisite codes, where a delivery used to fail, be retried and be dead-lettered** (Ade, 18 November 2026). Each now parks, and is retried when the configuration is published, as ADR-022 intends. They show in the parked-deliveries list and its filters like any other code. The item says what to configure.
  - `DOCUMENT_CATEGORY_NOT_IN_FORCE` <category>: a generated document's category, which is its template's document type.
  - `DOCUMENT_TEMPLATE_FIELD_UNAVAILABLE` <template>:<language>: the template names a merge field the event doesn't give.
  - `GRACE_RULE_NOT_IN_FORCE` <premium frequency>: Life arrears.
  - `COMMISSION_PLAN_NOT_IN_FORCE` <plan code>: commission on an invoice.
  - `RETURN_MAPPING_INVALID` <return code>: a regulatory return on the final close.
  - `PLAN_OPTION_NOT_IN_FORCE` <plan>/<option>: a Health authorisation's reservation.
  - `DOCUMENT_TEMPLATE_NOT_IN_FORCE` is now also used when the template version itself isn't in force, with item <template>:<language>.
- **More prerequisite codes, where missing configuration was skipped or dropped, never retried.** Each now parks.
  - `COLLECTION_PAYMENT_METHOD_NOT_IN_FORCE` <method>:<currency>: Payments, collecting an invoice.
  - `GRACE_RULE_NOT_IN_FORCE` <product>:<frequency>: General arrears. Life's item stays the frequency.
  - `CONTRIBUTION_GRACE_RULE_NOT_IN_FORCE` <scheme, or DEFAULT>: Health arrears.
  - `COMMISSION_PLAN_NOT_ASSIGNED` <intermediary>:<product>: commission, where the permission names no plan. A broker who earns nothing has a plan at rate 0.
  - `BILLING_FREQUENCY_NOT_IN_FORCE` and `COMMISSION_PLAN_NOT_IN_FORCE` on a broker of record change, which guessed or skipped before.
- **Added, not breaking:** `waitingFor` (`CHANNEL_PROVIDER_NOT_CONFIGURED` or `NOTIFICATION_TEMPLATE_NOT_IN_FORCE`, or null) and `waitingItem` (type and channel) on a notification in `GET /communications/notifications` and `GET /communications/notifications/{id}`. A notification whose provider or template is withdrawn after it was queued now stays `Queued` and says what it waits for. Before, it was refused and ended `Failed`.
- **Changed:** a webhook delivery whose signing key can't be resolved stays `Pending`, with an `error` starting `SIGNING_KEY_NOT_AVAILABLE`, and no attempt spent. Before, it failed and was dead-lettered.
- **The sample tenant (OFTZA) only:** the seed adds what its own business needed. No API change.
  - Every platform notice as a service notice by email, with a sample English template: payment reminder and final notice, renewal invitation and reminder, claim status, Health claim and pre-authorisation decisions, claim decision rights, grace, non-payment cancellation, contribution suspension, scheme renewal, non-renewal, broker change and `PROVIDER_RECOVERY_NOTICE`.
  - A channel provider row for email (`OFT-EMAIL`, provider `STUB`): the platform's stub channel, which records the message and sends nothing.
  - A `CERTIFICATE` template assigned to every General product, and the document categories `CERTIFICATE` and `RENEWAL_NOTICE`.
  - `MONTHLY` in force from 1 January 2020, before the oldest sample policy.
  - A General grace rule for each General product, monthly, with the `NON_PAYMENT` cancellation, adjustment and claim decline reasons it names. A missing rule now parks.

## Scope batch 4: processes, tasks and documents follow their record (F103, OQ-169, 18 November 2026)
**Breaking, for callers with a product or branch scope; an unrestricted caller sees no change.** Each list is filtered in the query, so paging holds.
- **`GET /workflow/processes`, `GET /workflow/processes/{id}` and `GET /workflow/tasks`:** a process, its tasks and its case notes follow the record the process is about.
  - Outside the caller's scope, a process isn't listed, and its id answers 404.
  - A process about a party, a data subject request, a party's retention review, a fraud indicator definition or the parked-delivery queue stays tenant-wide.
  - A process about a record with no scope found, or about tenant-wide totals and provider-level reviews (aggregation zones, Health programme reviews, provider measures, recovery reviews), is seen only by an unrestricted caller (OQ-172, OQ-176).
- **`GET /documents/documents?businessRefType&businessRefId`, `GET /documents/documents/{id}` and its content:** a document filed against a policy, claim, membership, Health claim or case follows that record's scope. Outside it, the document isn't listed and its id answers 404.
  - A document filed against a party, or against no scoped record, keeps its classification only, as before.
  - A document filed against a scoped record that isn't found fails closed: an unrestricted reader's only.
- **Existing rows** are backfilled by V0140 from their records. A process with no record found, of a type that has a scope, fails closed.
## Contract batch (18 November 2026): error wording, refusal titles, portal scan status, the stand-in model
1. **Not breaking: `docs/api/error-codes.json`, generated.** Every code with `httpStatus`, `messageKey`, `retryable`, and the catalogue's `means` and `remedy` (null where a code has no wording yet). It is generated from the `[ErrorCode]` constants with `R__error_code_catalogue.sql`, and `ErrorCodeCatalogueTests` fails when it is stale. This is the file the front end's `contracts:sync` reads for the wording.
2. **Not breaking, but visible: a worded code's English title is the catalogue's meaning.** For example, `DOCUMENT_QUARANTINED`'s `title` is now "The uploaded file is held until a malware scan clears it, or it failed the scan.", not "The request conflicts with the current state.".
   - A localised title from the message catalogue still wins.
   - A code with no wording keeps its kind's title.
   - `code`, `status` and `detail` are unchanged, so a client that matches on `code`, as it should, sees no change.
   - The generic title had been by design (titles come from the message catalogue, and no code had catalogue text); now the error catalogue's wording fills that gap.
3. **Not breaking: `scanStatus` on the portal's document view** (`GET /portal/documents…`): `NOT_REQUIRED`, `PENDING`, `CLEAN` or `INFECTED`. So a member or broker sees a held upload as held before its content read answers 409 `DOCUMENT_QUARANTINED`.
4. **Not breaking: the stand-in model,** `Assist:Provider=development`, in Development and Testing only (BR-147, widened to AI-1). See `docs/frontend-integration-guide.md` §7, and the test document `docs/frontend/ai1-sample-product.pdf`.

## AI Assist: the 7.4 error codes are catalogued (18 November 2026)
- **Not breaking:** each code now has a meaning and a next step in the catalogue's source (`[ErrorCode(..., Means, Remedy)]` in `src/Modules/Assist/Insys.Assist.Domain/AssistTenantSetting.cs`, read through the back-end mirror). The API's catalogue (`GET /api/v1/integration/static/error-code-catalogue`) serves the code, status, message key and retry flag, as before, not this wording. The codes are `ASSIST_NOT_UPLOADER`, `ASSIST_DOCUMENT_NOT_ALLOWED`, `ASSIST_CONFIRMATION_REQUIRED`, `ASSIST_CONFIRMATION_STALE`, `ASSIST_DOCUMENT_IDENTIFIERS`, `ASSIST_SOURCE_UNREADABLE`, `ASSIST_USER_CANNOT_ACT`, `ASSIST_RUN_STALLED`, `ASSIST_RESIDENCY_MISMATCH`, `ASSIST_SOURCE_TOO_LARGE` and `ASSIST_RUN_EXPIRED`. No code, status, message key or retry flag changed: none is retryable.
- The AI-2 evaluation set gains them as refusals (88 refusals and 66 premiums). **Its case ids are now stable:** a refusal is `R-<code>` and a premium `C-<product>-<factor value>-<sum>-<adjustments or NONE>`, so adding a code or a premium no longer renumbers the others. They change once, now, before the expert's run. The run waiting for Ade (44062786569) is on `073391f` and doesn't include them.

## Scope batch 3: a policy export holds only the reader's policies (F103, read sweep, Party, 18 November 2026)
- **Breaking, for callers with a product or branch scope:** `GET /party/bulk-jobs/{id}/content` for a policy export (`POST /party/bulk-jobs/policy-exports`) now lists only the General and Life policies and Health memberships whose product and node the reader's scope allows. Before, it listed every one in the tenant. Each module filters in its own query.
- **The job's count** (`POST …/policy-exports`) is the requester's, in their scope; the download is the reader's, in theirs. So two readers with different scopes can download different rows from one approved job, and neither gets more than their own scope. An unrestricted reader sees no change.

## Scope batch 2: a membership's accumulators follow its scope (F103, read-scope sweep row 1, 18 November 2026)
- **Breaking, for callers with a product or branch scope:** `GET /health/memberships/{id}/accumulators` now answers **404** for a membership outside the caller's scope, as `GET /health/memberships/{id}` already did. Before, it returned the accumulators of any membership in the tenant. An unrestricted caller sees no change.

## Wave 7 batch 7.4: the malware scan of uploads, and AI-1's product drafts (17 November 2026)
Every change, marked **breaking** where a client built on the previous contract can see a different answer.

**Documents (AIA-010):**
1. **Breaking: an upload's content is held until the malware scan clears it.**
   - `GET /documents/documents/{id}/content`, the portal's `GET /portal/documents/{id}/content` and `POST /documents/documents/{id}/deliveries` answer **409 `DOCUMENT_QUARANTINED`** while the upload's `scanStatus` is `PENDING`. They do so for good when it is `INFECTED`: never released, its content deleted, so upload a clean copy.
   - The scan usually takes under a minute; the front end polls the document.
   - Generated documents (`NOT_REQUIRED`) are unaffected.
   - Uploads stored before this deployment wait for one scan too.
   - For a stack with no ClamAV, see `docs/frontend-integration-guide.md` §6.
2. **Not breaking:** the new field **`scanStatus`** on a document's view: `NOT_REQUIRED`, `PENDING`, `CLEAN` or `INFECTED`. The metadata reads as before while the content is held.
3. **Not breaking:** the new error code **`DOCUMENT_QUARANTINED`** (409), with catalogue wording.

**AI Assist, AI-1 (AI Assist SRS §4):**
4. **Not breaking (new routes):**
   - **`POST /assist/product-drafts`**, with `Idempotency-Key`, under function `ASSIST_PRODUCT_DRAFT_RUN` (feature `AI_ASSIST`). The caller also needs Product Factory's create and edit functions and Documents' read.
     - The body is `{ productCode, documents: [{ documentId, confirmsNoPersonalData }] }`: 1 to 10 uploads, each `CLEAN`, at most 20 MB, uploaded by the caller, of a category that isn't PERSONAL or HIGHLY_SENSITIVE, and confirmed to hold no customer personal data.
     - It returns **202** with the run, status `QUEUED`.
   - **`GET /assist/product-drafts/{id}`** returns the run:
     - `status` (`QUEUED`, `RUNNING`, `DRAFTED`, `REFUSED`, `FAILED`), `outcomeCode` and `draftVersion`;
     - each document with its confirmation, `kind`, `unreadablePages` and any `identifiers` found (kind and page, never the value);
     - every item with `kind`, `field`, `value` (JSON text), `flag` (`FOUND`, `INTERPRETED`, `MISSING`), citation, `checkResult`, `checkReason` and `inDraft`.
     - Poll it until the status is no longer `QUEUED` or `RUNNING`.
5. **Not breaking: a run is visible only to its requester.** Another user, even with the same function, gets **404** for it, as for a run of another tenant. There is no list of runs.
6. **Not breaking: the Draft.** It is an ordinary Product Factory version, created by the caller. Only the version's own fields that were Found (quoted from the source) are set; everything else, Interpreted and Missing values included, is a cited proposal on the run (OQ-184, Ade, 17 November).
7. **Not breaking: new error codes**, with no catalogue wording yet:
   - `ASSIST_NOT_UPLOADER` (403), `ASSIST_DOCUMENT_NOT_ALLOWED` (422), `ASSIST_CONFIRMATION_REQUIRED` (422), `ASSIST_CONFIRMATION_STALE` (409);
   - `ASSIST_DOCUMENT_IDENTIFIERS` (422), `ASSIST_SOURCE_UNREADABLE` (422), `ASSIST_USER_CANNOT_ACT` (403), `ASSIST_RUN_STALLED` (409), `ASSIST_RESIDENCY_MISMATCH` (422);
   - and the two added by the security review: **`ASSIST_SOURCE_TOO_LARGE`** (422: a file or the run's text over AI-1's limits) and **`ASSIST_RUN_EXPIRED`** (409: a run still queued after an hour).
   - The start answers the first four directly. The others are a run's `outcomeCode`.
8. Review and decisions on items (AIA-019) come in 7.5.

## AI Assist: templates v3, and IDENTITY_NOT_LINKED is retryable (17 November 2026)
- **Prompt templates `refusal-v3` and `premium-v3`** (Ade, from a pre-screen of run 43904917124):
  - They don't say what the assistant can or can't do. v2's "Never say that you can change, approve or retry anything" brought the answer "I can't approve, change or resubmit…" in 64 of Sonnet's 107.
  - They give only the next step the facts give, with no other contacts or advice, and explain no causes the facts don't state.
  - A premium's exposure line is described as what the rating is based on, not as a starting amount.
  - Explanations cached under v2 aren't reused; each is made again when next asked. No field changed.
- **`IDENTITY_NOT_LINKED`** is now retryable (`retryable: true` in the catalogue). The response already carried `Retry-After: 2` and said to try again in a moment; only the flag was wrong.
  - Of the 75 worded entries, it was the only one whose flag disagreed with its next step.
  - Six others say "correct …, then try again". They stay not retryable, because the flag means the same request may succeed unchanged.

## Static data: abandoning a SETUP change set (OQ-180, Ade, 17 November 2026)
New routes, function, permission and error code; no existing field changes.
- **`GET /static-data/setup-sets/{id}`** (`STATIC_DATA_CHANGE_REQUESTS`, `change_request.read`): the set, its batches in order with `appliedAt` for those in force, its approval, and any abandonment (`abandonRequestedBy`, `abandonReason`, `abandonRequestedAt`, `abandonedBy`, `abandonedAt`), with an ETag.
- **`POST /static-data/setup-sets/{id}/abandon`** (`{ "reason": … }`), **`…/abandon/approve`**, **`…/abandon/decline`**, all If-Match:
  - Function `STATIC_DATA_SETUP_ABANDON` (privileged, in no role template), permission `setup_change_set.abandon`.
  - Only an approved set still `APPLYING`; otherwise 409 `INVALID_STATE_TRANSITION`.
  - 403 `MAKER_CHECKER_SAME_PERSON` when whoever asked approves or declines.
  - 409 `SETUP_TENANT_LIVE` (new) once the tenant is live.
- **Once abandoned:** the set's `status` is `ABANDONED`. Batches in force stay, and the others' drafts are discarded. A pending batch's `IMPORT` change request is `REJECTED`. The one whose module refused it stays `APPROVED`, as the domain model has no other way (OQ-183). The Assist setup is `DRAFT` again.
- **Go-live:** activation is 409 `ONBOARDING_INCOMPLETE` until a new set is applied in full. Readiness `SETUP_CHANGE_SET` lists the abandoned set until then.
- **The AI-2 evaluation set** gains the refusal `SETUP_TENANT_LIVE`. The refusal cases after it in code order are renumbered.

## AI Assist: how the wizard layers the packs (OQ-181, Ade, 16 November 2026)
No field or route is new. What `POST /assist/tenant-setups` and `PUT …/answers` draft changes:
- **`packIds`** lists the layers in their new order: the base, then each line, then the country, then the tenant's own versions of those packs (approved, else its newest draft).
- **Merging:** a later layer's row merges into the earlier one field by field, and a null means "no change". So a value's `values` can hold fields from several packs, and `sourcePackId` names the last that gave one.
- **The tenant's own overlays only fill empty values.** A value a platform-approved version gives is never replaced by the tenant's. A regulated value a platform line leaves empty can now be given by the tenant's approved overlay.
- **`missingReason`:** NOT_APPROVED when an unapproved version's regulated value was left out, and NO_VALUE once an approved version names the field empty too.
- **Starter packs:** NG and ZA are corrected for merging, with their placeholder rows moved to an unloaded `pending` list. The three base case templates have a null body, so they are Missing until compliance reviews the proposed wording in their notes. The `APPROVER` role has no grants.

## AI Assist: the SETUP change set (Wave 7 batch 7.7, 16 November 2026)
New routes and one error code; no existing field changes.
- **`GET /assist/tenant-setups/{id}/completeness`** (`ASSIST_TENANT_SETUP_RUN`): the check before submission (AIA-085). It returns `ready`, the number of `tables` and `values` the set would hold, and `blockers` and `warnings`. Each finding has a `code` (`UNDECIDED`, `MISSING`, `NOT_APPROVED`, `UNKNOWN_TABLE`, `GLOBAL_TABLE`, `LINE_REFUSED`, `NOTHING_ACCEPTED`; warning `REJECTED`), the `table`, `rowCode` and `field`, and a message. A finding never carries a value. The check saves nothing: each table's lines are tried and rolled back.
- **`POST /assist/tenant-setups/{id}/submit`** (`ASSIST_TENANT_SETUP_RUN`, If-Match on the setup, Idempotency-Key): submits the accepted values as one SETUP change set, one import batch per table, in dependency order (AIA-086). It returns `status`, `attempt`, `setId` and the `batches` (`seq`, `table`, `batchId`, `changeRequestId`, `lines`, `hash`).
  - 422 `ASSIST_SETUP_NOT_READY` (new) while the check has blockers, listed by table.
  - 403 `FORBIDDEN` for anyone but whoever ran the wizard.
  - 409 `ASSIST_TENANT_LIVE` once the tenant is live.
  - A submission that stops part-way leaves the setup `SUBMITTING` (its values locked); submitting again resumes it.
- **`POST /assist/tenant-setups/{id}/submission/withdraw`** (`ASSIST_TENANT_SETUP_RUN`, If-Match): the submitter withdraws a submission that stopped part-way (`SUBMITTING`). The batches it loaded are discarded and the setup is `DRAFT` again. 403 `FORBIDDEN` for anyone else; 409 `INVALID_STATE_TRANSITION` once the set is requested (Workflow ends a requested set).
- **Deciding a value** (`POST /assist/tenant-setups/{id}/items/{itemId}/decision`) now moves the setup's ETag too, so a submission names the setup as reviewed.
- **The completeness check** also runs each table's own submission rules, so a submission stops part-way less often.
- **A setup's `status`** gains `SUBMITTING`, `SUBMITTED` and `APPLIED`. A set not approved returns it to `DRAFT`, with its drafts discarded, to be submitted again as a new attempt.
- **The approval (`/workflow/approvals`):** one approval for the whole set, `approvalType` `STATIC_DATA_T1`, new `businessRefType` `STATIC_DATA_SETUP`, opening on the change request screen. Whoever ran the wizard can't decide it (403 `MAKER_CHECKER_SAME_PERSON`). The approver holds the approve function (`SD_{module}_APPROVE`) of every catalogue module whose tables the set loads, each of them, as approving each batch alone would need; missing one is 403 `FUNCTION_NOT_GRANTED` naming it, on `approve` and `reject`, and the set isn't offered under `decidable=true`. Once it is granted, the batches go in force one after another.
- **Change requests (`/static-data/change-requests`):** each batch is an `IMPORT` change request. It is `PENDING_APPROVAL` with no approval of its own until the set is decided, then `APPROVED` and `ACTIVE` in its turn, or `REJECTED` with the set.
- **Tenant activation (`POST /tenancy/tenants/{id}/activate`):**
  - 409 `ONBOARDING_INCOMPLETE` while a SETUP change set waits for its approval or is partly applied, or while a submission's batches are loaded and its set isn't recorded yet. The set's approval isn't applied once the tenant is live.
  - Once one is applied, every onboarding step's readiness runs again, and a check not ready is 422 `ONBOARDING_STEP_NOT_READY`, named `STEP.CHECK`.
  - Tenants with no SETUP change set see no change.
- **Readiness:** `END_TO_END_TESTS` gains `SETUP_CHANGE_SET`, a blocker while a set is open (a submission still loading shows as `LOADING`).
- **Batch order:** each table after the tables of the set it refers to, from the static data catalogue's references (OQ-178).
- **Starter packs (`docs/specs/starter-packs`):** the base and motor line hold one record per row now (format only, OQ-179). A setup from them lists far fewer blockers. What remains Missing or refused is listed in `format-corrections-2026-11-16.md`.
- **The AI-2 evaluation set** gains the refusal `ASSIST_SETUP_NOT_READY`, worded in the catalogue. The refusal cases after it are renumbered, from `R-010` onwards. Runs made before this commit (43862166993, 43904917124) use the old numbers.

## AI Assist: list numbers no longer reject an explanation, and templates v2 (16 November 2026)
- **`POST /assist/explanations/refusals` and `/premiums`:** an explanation that numbers its points as a list ("1. Open the version", "2) Ask again") now passes the number check (AIA-033a). In the first evaluation run every rejection was such a list marker or a number word, so these came back `REJECTED` with the plain facts. A figure on a listed line is still checked. A number written in words is still rejected.
- **Prompt templates `refusal-v2` and `premium-v2`:** they also ask the model not to say how many lines or steps there are, and not to write numbers in words. Explanations stored under v1 aren't reused for v2; each is made again when next asked. No field changed.

## Scope batch 1: approvals and writes follow the record's scope (F103, OQ-170, OQ-173, OQ-176; 16 November 2026)
No field, route or error code is new. What changes is who gets 403 `OUT_OF_SCOPE`, 404, or an empty list. Unscoped users see no change.
- **Approvals (`/workflow/approvals`):** approvals about refunds, non-renewals, case redress, legal holds and record holds, payments' unknown outcomes, product versions, and Health recoveries and credit notes now carry their record's scope. A checker sees one, and `decidable=true` offers it, only when their scope covers that record. `GET /{id}` is 404 and `POST /{id}/approve` and `/reject` are 403 `OUT_OF_SCOPE` outside it.
  - **A product version's approval** is judged by product only, so a branch-limited checker who holds the product decides it (OQ-173).
  - **Fail closed (OQ-176):** when the record's scope can't be settled (a refund on an account whose schedules span scopes, a provider's recovery or credit note, a record not found), only a checker with unrestricted scope sees and decides it. Approvals about parties, users, static data, Finance periods and commission statements stay unscoped.
  - **`decidable=true`** is now judged by the scope of `approval.approve`, not `approval.read`. A checker who reads everything but approves within one product is offered only that product's approvals.
  - **Approvals already waiting** of these types are scoped by V0135: a product version's by its product, the rest to unrestricted checkers only. Decided approvals are unchanged.
- **Payments (`/payments/payments`):** `POST /` (create), `POST /{id}/resolve`, `/reverse` and `/allocate` are 403 `OUT_OF_SCOPE` unless the payment's whole record (an invoice, billing account, refund, claim payment instruction or Health remittance) is in the caller's scope. A payment with no record, or against a record no module scopes, is changed only by an unrestricted caller (OQ-176). A commission payout against a commission statement isn't scoped (OQ-174) and needs `commission_payout.read`, as reading it does; the purpose alone doesn't lift the check. Reads of payments are unchanged in this batch.
- **Reconciliation exceptions (`/payments/reconciliation-exceptions/{id}/assign` and `/resolve`):** 403 `OUT_OF_SCOPE` unless the payment the exception references is in scope. One with no payment (an unmatched statement line) is for unrestricted callers only (OQ-176). The exception list isn't filtered yet; it comes in a later batch.
- **General assessments and recoveries** (`/general/assessments/{id}/start`, `/accept`, `/further-work`, `/estimates`, `/report`; `/general/recoveries/{id}/demand`, `/write-off`, `/close`, `/salvage`, `/salvage/{itemId}/dispose`, `/recovered`): 403 `OUT_OF_SCOPE` outside the claim's scope. Their reads were already 404 outside it.
- **`POST /claims/claims`:** 403 `OUT_OF_SCOPE` when the `productCode` and `orgNodeCode` aren't in the caller's scope. A repeat for an existing claim outside the scope is also 403.
- **`POST /health/claims` (staff):** 403 `OUT_OF_SCOPE` when the membership is outside the caller's scope. The provider portal's route is unchanged; it is limited by relationship (OQ-175).
- **`POST /rating/calculations`:** 403 `OUT_OF_SCOPE` for a product outside the caller's product scope (by product only, as its reads are, OQ-168).

## AI Assist: starter packs and overlay approval (Wave 7 batch 7.6, 15 November 2026)
- **New, platform operator API, `/assist/overlays`:** `POST /` (a pack file as JSON; Idempotency-Key; 201 with `StarterPackView` and its ETag) and `GET /`, `GET /{id}` (`StarterPackSheet`: the version and every row with `class`, `regulated`, `regulatedSource`, `tailorable`, `tailorableSource`, `mayBeTailored`, `values`), `POST /{id}/review` (`partner`, `reviewerName`, `reviewedOn`, `reviewDocumentId`; If-Match), all `ASSIST_OVERLAY_MANAGE`; `GET /awaiting-approval`, `GET /{id}/approval` and `POST /{id}/approve` (`contentHash`; If-Match), `ASSIST_OVERLAY_APPROVE`.
- **New, tenant API, `/assist/packs`:** `POST /` (the tenant's own version, `TENANT_ONLY`; only while onboarding), `GET /`, `GET /{id}`, all `ASSIST_TENANT_SETUP_RUN`; `GET /awaiting-approval`, `GET /{id}/approval`, `POST /{id}/approve`, `ASSIST_OVERLAY_APPROVE_TENANT`.
- **New functions:** `ASSIST_OVERLAY_MANAGE` (platform) and `ASSIST_OVERLAY_APPROVE_TENANT` (staff), both privileged, in no role template. `ASSIST_OVERLAY_APPROVE` is unchanged.
- **New, tenant API, `/assist/tenant-setups`** (AI-3's pack-only path, `ASSIST_TENANT_SETUP_RUN`, onboarding only): `POST /` (the questionnaire: `country`, `lines`, and optionally `currencies`, `timeZone`, `products`, `channels`, `paymentMethods`, `teamRoles`, `languages`; Idempotency-Key; 201 `SetupRunView` with every value's `flag` PACK or MISSING, `missingReason` NOT_APPROVED or NO_VALUE, its source pack and line), `GET /{id}`, `PUT /{id}/answers` (If-Match; only changed values are re-drafted), `POST /{id}/items/{itemId}/decision` (`decision` ACCEPTED or REJECTED, `note`; the value's If-Match).
- **New error codes:** `ASSIST_PACK_NOT_APPROVED` (422; also accepting a value Missing because its overlay isn't approved; one the pack leaves empty is 422 `VALIDATION_FAILED`), `ASSIST_PACK_CHANGED` (409, the approval names other content, or the stored rows aren't what was loaded), `ASSIST_PACK_NOT_NEWEST` (409, only a pack's newest version is approved), `ASSIST_TENANT_LIVE` (409; also the wizard and its decisions on a live tenant). Approving is 403 `MAKER_CHECKER_SAME_PERSON` for whoever loaded or reviewed any version of the pack, or ran a setup with it.
- **`SetupItemView.inDraft`:** false once the answers no longer bring the value; such a value is rejected and can't be decided again (409). The run's counts are of the values in the draft. Whoever loaded or reviewed a version gets 403 `MAKER_CHECKER_SAME_PERSON` on approving it.

## AI Assist: the residency check, and explanations that name a step (Wave 7 batch 7.3, 15 November 2026)
- **`PUT /assist/settings`: `inferenceRegion` is now one of the provider's regions,** `US` or `GLOBAL` (Ade, 15 November); a country code is 422 `VALIDATION_FAILED`, naming the list. **This narrows what was accepted.**
- **`GET /assist/settings` (and the PUT's answer) gain `inferenceRegionSupported`** (false for a region set earlier as a country code) **and `supportedInferenceRegions`** (the choices, for the settings page to say why and offer them). `residencyAllowsInference` is false while the region isn't supported.
- **New error code `ASSIST_REGION_NOT_SUPPORTED` (422):** any Assist request from a tenant whose region isn't supported, refused before any model call.
- **`POST /assist/explanations/refusals` and `/premiums`:** each call asks for the tenant's region; when the provider reports inference somewhere the setting doesn't allow (`GLOBAL` allows any; `US` only `us`), or doesn't say where, the outcome is `UNAVAILABLE` with the plain facts, as when the model can't be reached (AIA-050).
- **`POST /assist/explanations/premiums`:** an explanation that names a calculation's line by its number ("line 2 applies the table") now passes the number check (AIA-033a), as does a code followed by punctuation ("group G3, then"). Before, such an explanation came back `REJECTED` with the plain facts. No field changed.

## Premium calculations: a branch scope no longer hides them (15 November 2026)
- **`GET /rating/calculations/{id}`, `GET /rating/calculations?subjectType&subjectId` and `POST /assist/explanations/premiums`** apply the reader's **product** scope only. A calculation has no branch yet, so a branch scope doesn't apply to it (OQ-168). Since 7.3 below, a reader whose grants were all branch-limited was refused every calculation (404, an empty list); they see their products' calculations again. Product-limited readers see no change.
- **The list is filtered in the query,** so a page is full and its cursor is right when some calculations of a subject are out of scope.

## Premium calculations follow the reader's product scope (Wave 7 batch 7.3, 15 November 2026)
- **`GET /rating/calculations/{id}` and `GET /rating/calculations?subjectType&subjectId`** now apply the product scope of `premium_calculation.read` (F103, `addenda/access-scopes.md` §1): a calculation of a product outside the reader's scope isn't found (404), and isn't listed. Before, any holder of the permission read every product's. Unscoped readers see no change.
- **`POST /assist/explanations/premiums`** follows it: refused in the same way (404), with no stored explanation returned and no model call (AIA-003).

## AI Assist: AI-2's explanations (Wave 7 batch 7.2, 15 November 2026)
All three routes are off unless the tenant is entitled to `AI_ASSIST` and has switched explanations on (`PUT /assist/settings`); staff only (D-3).
- **`POST /assist/explanations/refusals` (new, `ASSIST_EXPLAIN_REFUSAL`, Idempotency-Key):** `code` (an error catalogue code), `fields` (the names of the fields in error, at most 30; only how many there are is sent to a model), `recordType`, `recordId` and `correlationId`, kept for `basedOn` and the audit and never sent (AIA-039, D-15). An unknown code is 422 `VALIDATION_FAILED`.
- **`POST /assist/explanations/premiums` (new, `ASSIST_EXPLAIN_PREMIUM`, Idempotency-Key):** `calculationId`, `correlationId`. The caller must also be able to read premium calculations (`premium_calculation.read`, function `RATING_CALCULATIONS`), or 403 `FUNCTION_NOT_GRANTED`; an unknown calculation, or another tenant's, is 404. Explained from the calculation's product and lines only (step, configured factor, value used, amount with its currency), not its inputs; for Health, without the factor values (AIA-038).
- **Both return `ExplanationView`:** `id`, `kind` (`REFUSAL` or `PREMIUM`), `outcome`, `text`, `label` (always "AI-generated explanation of a recorded INSYS decision", AIA-034), `basedOn` (`kind`, `reference`, `href` for a calculation, `correlationId`, and for a refusal the `recordType` and `recordId` it was sent with), `facts` (the plain structured facts, `name` and `value`), `reused` and `rejectedNumbers`.
  - `outcome` `EXPLAINED`: show `text` with the label and the `basedOn` link.
  - `REJECTED` (the verifier found a number not in the facts, AIA-033), `UNAVAILABLE` (no model answered, P8) or `NO_WORDING` (the code has no catalogue wording yet, so no call was made): `text` is null; show `facts` as the plain refusal or breakdown.
  - Errors: `ASSIST_NOT_ENABLED`, `ASSIST_RESIDENCY_NOT_SET`, `ASSIST_CAP_REACHED` (only when a new call is needed: a reused explanation still shows).
- **`POST /assist/explanations/{id}/feedback` (new, signed-in user, Idempotency-Key):** `helpful`, `reason` (`WRONG`, `UNCLEAR`, `INCOMPLETE` or `OTHER`, needed when not helpful), `comment` (at most 500 characters, never sent to a model). 201. Only the user an explanation was shown to marks it; anyone else gets 404 (AIA-037).


## The environment and its stubs, for the INSYS Preview (3 November 2026)
- **`GET /platform/environment` (new):** any signed-in user, staff or portal. `environment` (`ASPNETCORE_ENVIRONMENT`, such as `Preview`), `nonProduction` (true everywhere but Production) and `stubs`: each integration a stub answers, with its `area` (`EMAIL`, `SMS`, `PUSH`, `WHATSAPP`, `PAYMENTS`, `SCREENING`, `IDENTITY_VERIFICATION`, `REINSURER`, `REGULATOR`, `GENERAL_LEDGER`) and a sentence saying what it does instead. For the console's permanent banner and its list of stubs (`docs/plans/insys-preview.md`); the front end's part is a request.

## The front end's reconciled requests (2 November 2026)
- **`GeneralPolicyView.expiryReason` (BR-116):** now returned and declared: `NOT_RENEWED_BY_INSURER`, `DECLINED_BY_CUSTOMER` or `LAPSED`, on an expired policy. The earlier note promised it before the view carried it.
- **`CessionView.periodCode` (BR-125):** the `yyyy-MM` value `GET /reinsurance/cessions?periodCode=` finds the cession under.
- **`PartyDueDiligenceView.partyKind` (BR-108):** `INDIVIDUAL` or `ORGANISATION`.
- **Now in the error catalogue (BR-119):** `NOT_A_CASE_MANAGER` (422), `NOT_THE_CASE_MANAGER` (403), `CARE_CONSENT_REQUIRED` (422 on a note; 403 where a referral lacks it).
- **Descriptions and known values only:** `receivedChannel`, `outstanding`, `pendReason`, `PartyRecordFicReport`, notification `sourceType`, and `PARTY` among the documents' `businessRefType`.
- **Corrected note (BR-111):** the renewal decision's authority error is `GENERAL_UW_AUTHORITY_EXCEEDED`.

## Activating a tenant with the simulator (2 November 2026)
- **`POST /tenancy/tenants/{id}/activate`:** refused for an onboarding tenant, 422 `PAYMENT_PROVIDER_NOT_ALLOWED`, on the same condition as reactivation.

## BR-126 and BR-127: case categories and Health decline reasons (2 November 2026)
- **BR-126, `POST /workflow/cases` and `POST /portal/cases`:** a category from another family than the case type's is refused, 422 `CASE_CATEGORY_NOT_ALLOWED`. A category not in force stays `VALIDATION_FAILED`.
- **BR-126, appeals:** an appeal keeps its parent's category, including one no longer in force. On the staff route, an appeal with no `categoryCode` now takes the parent's; another category must be of the appeal type's family.
- **BR-126, `linkTypes`:** no change. Staff opening, staff appeal, the links route and portal raise refuse with `CASE_LINK_NOT_ALLOWED`; a portal appeal leaves behind a parent's link its type doesn't allow. A record that isn't the portal caller's own still answers 404 or 403 first.
- **BR-127, `POST /health/claims/{id}/reject`:** `reason` is a code, `x-code-list` `claims.ref_claim_decision_reason_codes`, filter `decision_type=DECLINE`, at most 30 characters. An unknown code or one of another decision type answers 422 `CLAIM_DECLINE_REASON_INVALID`, and the claim doesn't change.
- **BR-127, `POST /health/claims/{id}/validate`:** when validation would decline the claim and that reason isn't a `DECLINE` reason in force, it answers 422 `CLAIM_DECLINE_REASON_INVALID` and the claim stays `Received`. The arrears job leaves such a claim `Pended` and tries again on its next run.
- **BR-127, readiness:** new check `HEALTH_DECLINE_REASONS` (step `DOMAIN_REFERENCE_DATA`), for a tenant with health plans: `NOT_COVERED`, `UNKNOWN_FORMAT`, `PHARMACY_LINE_NOT_IN_FORMAT`, `PHARMACY_FIELDS_MISSING`, `PROVIDER_NOT_ACTIVE`, `UNKNOWN_TARIFF_CODE`, `UNKNOWN_DIAGNOSIS`, `CONTRIBUTIONS_ARREARS`.
- **BR-127, static data:** a claim decision reason's `code` may be up to 30 characters (V0127).

## BR-129: labels (2 November 2026)
- A reconciliation line has `kindLabel`, and a bulk job, in the list and alone, has `jobTypeLabel`. English only for now (D-4); a kind with no label shows its code.

## BR-128: currency on migration amounts (2 November 2026)
**One batch may hold several currencies; one load file holds one.** A batch reconciles per currency.
- **`POST /integration/migration-batches/{code}/reconciliation`:** a source total with an `amount` needs its `currency`, or 422 `VALIDATION_FAILED`, and the same kind and currency twice is refused. Each line now has `currency`, one line per kind and currency. A kind with no amount has none. A source total that names no currency doesn't agree with a loaded amount that has one.
- **Every load** (`general/migration/policies` and `claims`, `life/migration/policies` and `claims`, `health/migration/memberships` and `accumulators`, `billing/migration/outstanding-premiums`, `finance/migration/opening-balances`): the request needs `currency`, the currency of its control total and of every row. A row in another currency is a rejected line, and the file loads all or nothing. The result returns `currency` beside its amount.
- **Opening balances** are in the tenant's base currency; another is a rejected line.

## Appeal routes as static data, and reactivation with the simulator (2 November 2026)
- **New static data** `workflow/static/appeal-routes`: `routeCode`, `name`, `preferredLines` (`LIFE`, `GENERAL`, `HEALTH`). `PPR_NFO` and `CMS` are global rows. A tenant's own row wins over a global one with the same code.
- **Case types:** `appealRoute` is no longer limited to `PPR_NFO` or `CMS`. It takes capitals, digits and underscores, starting with a capital, up to 20 characters. Existing values are unchanged. Whether the route is in force is checked by readiness (`CASE_APPEAL_ROUTES`), not when the case type is written.
- **Claim rights notices:** `appeal_route` comes from the tenant's dispute case type whose route prefers the claim's line, and otherwise its first such type.
- **`POST /tenancy/tenants/{id}/reactivate`:** refused, 422 `PAYMENT_PROVIDER_NOT_ALLOWED`, while the tenant's provider register or callback endpoints name `SIMULATOR` and this environment refuses it.

## Simulator guard (branch `simulator-guard`, 1 November 2026)
- **New error code** `PAYMENT_PROVIDER_NOT_ALLOWED` (422): on the provider register's static data, `POST /payments/callback-endpoints` and a provider callback, for `SIMULATOR` outside Development and Testing or a deployment declared non-production.

## BR-124: a claim dated after the business date is refused

### Breaking
- **`POST /health/claims` and the portal's claim route:** a `serviceDate` after the tenant's business date is refused, 422 `CLAIM_EVENT_DATE_IN_FUTURE`. It was accepted and could be paid. Pre-authorisations for future treatment are unaffected.
- **`POST /general/claims` and `POST /life/claims`:** a future `lossDate` or `eventDate` was already refused; the code changes from `VALIDATION_FAILED` to `CLAIM_EVENT_DATE_IN_FUTURE`.

## Dormant parties (branch `dormant-parties`, OQ-054)

- **`party.status` can now become `DORMANT` by itself** (daily), and return to `ACTIVE`. Its `rowVersion` moves when it does.
- **`blocks` on `PARTY_ANONYMISATION_BLOCKED`** can name `recordType` `CASE`, with `retention` `CASE`; and `PARTY` with `reason` `IN_RETENTION`.
- **`party.cfg_record_retention_policies.recordKind`** gains `CASE`.

## Small fixes (branch `small-fixes`, Ade's decisions of 31 October 2026)

- **New `GET /integration/bulk-jobs` (BR-122, GAP-126):** `type` (`PARTY_IMPORT`, `PARTY_EXPORT`), `direction` (`IMPORT`, `EXPORT`), `status`, `from`, `to` (UTC dates, inclusive), `cursor`, `limit`. Newest first, as `{items, nextCursor}`. Each row: `id`, `jobType`, `direction`, `status`, `totalCount`, `acceptedCount`, `runBy`, `createdAt`. Rejected lines and the filter stay on `GET /party/bulk-jobs/{id}`. Permission `party_bulk_job.read`. Added to the `PARTY_BULK_JOBS` screen's functions.
- **Readiness blocker `DISCLOSURE_MANDATORY_TEXTS`** in `INTEGRATION_AND_REPORTING`. `DISCLOSURE_TEXTS` now warns of `PROPOSAL` only.

## Endorsement actions (branch `endorsement-actions`, B-007, B-008)

- **`POST /general/policies/{id}/endorsements`** takes `adjustmentPct` for a `PREMIUM_ADJUST` type (negative for a discount) and `billingFrequency` for a `BILLING_FREQ` type. Each has no `changes`, and is `Rated` at once, ready to submit. A tenant adds the two types in `general.cfg_endorsement_types` to use them.
- **`EndorsementView`** gains `adjustmentPct`, `newBillingFrequency`.
- **An accepted mid-term discount offer** drafts a `PREMIUM_ADJUST` endorsement for the case's owner to submit.

## Retention offers (branch `retention-payment-terms`, Ade's decision of 1 November 2026)

- **`general.cfg_retention_offers`** gains `billingFrequency`, required for a `PAYMENT_TERMS` offer. Accepting one mid-term drafts a `BILLING_FREQ` endorsement for the case's owner.

## Back-end requests (branch `be-requests`)

- **BR-099, new `GET /portal/cases/options`:** the case types this portal may raise (`code`, `family`, `label`, `linkTypes`, `requiresCategory`) and the categories in force.
- **BR-107:** `partyName` and `partyNumber` on the due diligence view and work list.
- **BR-118:** `CR-`, `IB-`, `PAY-`, `PB-`, `SIM-` and `JNL-` numbers keep their length, and now end with the ID's random digits instead of starting with its clock. Don't sort by them.
- **BR-120:** invoicing or reversing a recovery with a write-off waiting answers 409 `INVALID_STATE_TRANSITION`.
- **BR-123:** approving an authorisation for a medicine on the formulary takes the formulary's category. A benefit category mapping's code may hold a hyphen.

## Health migration (branch `health-migration-accumulators`, Ade's decisions of 1 November 2026)

- **New `POST /health/migration/accumulators`:** a member's benefit use to date. Permission `health_membership.migrate`.
- **`POST /health/claims`** takes optional `migrationBatch` and `sourceReference`, for a claim open in a source system.

## Commission recovery (branch `commission-recovery`, B-009)

- **New `GET /distribution/commission-recoveries`:** `intermediaryCode`, `owedOnly` (default true), `cursor`, `limit`. What brokers owe of commission already paid. Permission `commission.read`.
- **New static data `commission.cfg_broker_change_settings`:** `paidCommission` `RECOVER` (the default, with no row) or `KEEP`.
- **A statement's net** can be lowered by what the broker owes, as far as it has commission.

## Service accounts (branch `service-callback-tokens`, B-011)

- **New `POST /identity/users/service-accounts`:** registers a client-credentials identity. Permission as for provisioning a user.
- **A callback endpoint requiring a client token** now takes it only from a service account. **Readiness** `PAYMENT_CALLBACK_CLIENT` and `COMMUNICATION_CALLBACK_CLIENT` need a service account holding the function.

## Migration batch (branch `migration-batch`, OQ-158, B-013)

- **New, each idempotent and all or nothing against the file's control totals:** `POST /general/migration/policies`, `/life/migration/policies`, `/health/migration/memberships`, `/billing/migration/outstanding-premiums`, `/finance/migration/opening-balances`. Permissions `general_policy.migrate`, `life_policy.migrate`, `health_membership.migrate`, `invoice.migrate`, `journal.migrate`.
- **`POST /general/migration/claims`, `/life/migration/claims`:** claims open in a source system. Their numbers start `MIG-`. Permissions as for each domain's policies.
- **`POST /integration/migration-batches/{code}/reconciliation`:** the source's totals in, every difference out. Permission `migration_batch.reconcile`.
- **`POST /party/bulk-jobs/policy-exports`:** a second person approves, as for parties. `jobType` gains `POLICY_EXPORT`.
- **`POST /party/bulk-jobs/imports`** also takes `organisations` and `controlCount`; `rows` is optional. A rejected line 0 means the whole file.
- **Life `proposalId` can now be null,** on a migrated policy.

## Wave 6 batch 6.3 (branch `wave6-3`): fraud cases and bulk jobs (addendum §4.8, §4.9; F058, F076)

### New
- **`/claims/fraud-cases`:** `POST`, `GET`, `GET /{id}`, `POST /{id}/assign`, `/conclude`. Permissions `fraud_case.read`, `.create`, `.update`. No portal route.
- **`/party/bulk-jobs`:** `POST /imports` (individuals, all or nothing), `POST /exports`, `GET /{id}`, `POST /{id}/approve`, `GET /{id}/content`. Permissions `party_bulk_job.read`, `.create`, `.approve`.

## Wave 6 batch 6.2 (branch `wave6-2`): register, data-subject requests, disclosures, retention (addendum §4.4 to §4.7; F057, F059, F060, F064)

### New
- **`/party/data-subject-requests`:** `POST`, `GET`, `GET /{id}`, and `POST /{id}/confirm-identity`, `/approve`, `/refuse`, `/complete-correction`, `/execute-deletion`, `GET /{id}/export`. Portal: `POST`, `GET /portal/party/data-subject-requests`. Permissions `data_subject_request.read`, `.create`, `.update`, `.approve`. `party.cfg_data_subject_settings` holds the days.
- **`GET /integration/disclosures`, `POST /integration/disclosure-acknowledgements`,** and on the portal `GET /portal/disclosures`, `POST /portal/disclosure-acknowledgements`. Permissions `disclosure.read`, `.create`.
- **Error `DISCLOSURE_REQUIRED` (409)**, with `displayPoint` and `noticeTypes`: from accepting a General quote, issuing a General or Life policy, enrolling a Health member, accepting a General renewal, once a text is in force at that point: display points `QUOTE`, `ISSUE`, `RENEWAL`, `CLAIM`. Acknowledge with the record named: `GENERAL_QUOTE`, `LIFE_PROPOSAL`, `GENERAL_RENEWAL`; Health names none.
- **Business references** `DATA_SUBJECT_REQUEST`, `PARTY_RETENTION`. **Readiness warnings** `DISCLOSURE_TEXTS` (non-mandatory points), `REGISTER_ENFORCEMENT`. **Readiness blocker** `DISCLOSURE_MANDATORY_TEXTS` (OQ-157, each sale country; entries read `NA QUOTE: P-READY`).

### Changed
- **`reporting.cfg_compliance_register_entries`** gains `enforcedBy`.

## Wave 6 batch 6.1 (branch `wave6-1`): tax on invoices and returns (addendum §4.2; F094)

### New
- **`POST`, `GET /finance/tax-returns`, `GET /{id}`, `POST /{id}/approve`, `/reject`, `GET /{id}/content`.** Permissions `tax_return.read`, `.create`, `.approve`.
- **Transaction types** `PREMIUM_TAX`, `PREMIUM_TAX_CREDITED`: a tenant with a tax code in force needs posting rules for them (readiness `TAX_POSTING_RULES`).

### Changed
- **`rating.cfg_premium_tax_and_levy_rates`** gains `productCode`. A rate naming a product applies to its invoices.
- **Invoices:** `taxAmount` and `netAmount` are now filled for such products, and `totalAmount` doesn't change: the tax is taken out of the premium.

## Wave 5b batch 5b.4 (branch `wave5b4`): disease programmes and preventive schedules (addendum §4.4, §4.5; F086 to F088)

### New
- **`health.cfg_disease_programmes`, `health.cfg_preventive_schedules`** (static data, tenant, Tier 2).
- **`POST`, `GET /health/programme-enrolments`, `GET /{id}`, `GET /{id}/clinical?purpose=`, `POST /{id}/review`, `/end`.** The ordinary views name no programme. Permissions `health_programme.read`, `.create`, `.update`. Business reference `HEALTH_PROGRAMME_REVIEW`.
- **Readiness warnings** `PREVENTIVE_SCHEDULES`, `CHRONIC_REVIEWER`.

## Wave 5b batch 5b.3 (branch `wave5b3`): chronic registration (addendum §4.3; F083 to F085)

### New
- **`POST`, `GET /health/chronic-registrations`, `GET /{id}`, `GET /{id}/clinical?purpose=`, `POST /{id}/approve`, `/decline`, `/end`.** The ordinary views show no condition. Approve and decline need `health_chronic.decide` and the clinical function, and aren't for the requester. They take If-Match.
- **Portal:** `POST /portal/health/chronic-registrations` (provider), `GET /portal/health/chronic-registrations` (member: their own and minors', with `remainingThisMonth`; an adult dependant's isn't listed to the principal, OQ-161).
- **Permissions** `health_chronic.read`, `.create`, `.decide`, `.update`.

### Changed
- **`health.cfg_chronic_condition_list`** gains `benefitCategory`.

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
## A change's response carries the version as saved (branch `etag-after-save`)

### Changed
- **Every write's `rowVersion` and ETag are now the record's version after the save.** Before, many were one behind, so a client needed a fresh GET. A client may now use the returned ETag for its next change.

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
- **`POST /general/renewals/{id}/decision`** (`GeneralDecideRenewal`, `general_quote.underwrite`): `{ offer, decisionCode, loadingPct? }` on a referred or ineligible renewal. It needs underwriting authority covering the product and the sum insured; otherwise 403 `GENERAL_UW_AUTHORITY_EXCEEDED`. A referred or ineligible renewal opens a Workflow task on `GENERAL_RENEWAL_REVIEW` (reference `GENERAL_RENEWAL`), routed by authority, and the decision closes it.
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
