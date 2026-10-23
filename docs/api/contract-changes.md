# API contract changes

What changed in the API contract, newest first, so the front end can sync. The OpenAPI documents in this folder are the contract; this list says what moved.

## Wave 0 batch 2 (branch `wave0-frontend-2`, not merged)

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
