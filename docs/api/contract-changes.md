# API contract changes

What changed in the API contract, newest first, so the front end can sync. The OpenAPI documents in this folder are the contract; this list says what moved.

## Wave 0b batch 5 (branch `wave0b-5`, not merged): external-user access; Health product follow-ups

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
