# Wave 0 front-end enablement, batch 1: merged

**Merged:** `main` fast-forwarded to `844b668` on 23 October 2026, from branch `wave0-frontend` (base `769db1d`).

**CI on that exact head:** run 39984737124, all three jobs green.

| Job | Duration | Test runs | Total | Passed | Failed | Skipped |
|---|---|---|---|---|---|---|
| unit-and-architecture | 3 min 52 s | 22 projects | 699 | 689 | 0 | 10 |
| integration | 9 min 13 s | 1 | 210 | 210 | 0 | 0 |
| sweeps-and-end-to-end | 5 min 56 s | 2 (sweeps 7, end-to-end 18) | 25 | 25 | 0 | 0 |
| **All** | | | **934** | **924** | **0** | **10** |

The last green `main` run (`769db1d`) had 909 tests: 899 passed and 10 skipped. This branch adds 25, which accounts for the whole difference, so nothing was dropped:

| Tests | Added |
|---|---|
| `MeTests` | 3 |
| `CorsTests` | 2 |
| Error response tests | 5 |
| OpenAPI contract tests (GAP-005 theory ×3, GAP-008) | 4 |
| `MakerCheckerTests` (GAP-021) | 1 |
| Identity unit tests (GAP-020) | 2 |
| `StateEnumTests` | 2 |
| `TenantAdministratorTests` | 4 |
| `OutboxAndInboxTests` (record history) | 1 |
| `RecordHistoryTests` | 1 |

The 10 skipped are unchanged: 8 Life tests (OQ-052) and 2 Huawei real-cloud tests (no credentials).

## What is in it

| Item | Commit | Summary |
|---|---|---|
| Production readiness; optional real-cloud workflow | first on branch | `docs/production-readiness.md`; `.github/workflows/cloud-tests.yml` runs `HuaweiCloudTests` when the `INSYS_HUAWEI_*` secrets exist, else skips |
| GAP-003 | `/identity/me` | `GET /api/v1/identity/me`, `PUT /api/v1/identity/me/preferred-language` (V0024) |
| GAP-002 | CORS | Development CORS for `http://localhost:5173`; refuses to start elsewhere when configured |
| GAP-006, GAP-007 | `c012730` | `Retry-After` on 429; `correlationId` on every error, equal to `X-Correlation-Id` |
| GAP-005, GAP-008 | `689f8c4` | ProblemDetails extensions and shared error responses in OpenAPI; state fields as enums; schemas of same-named types no longer merged |
| GAP-021 | `e2a72ee` | `privileged`, `requiresMfa`, `requiresApproval` on role assignments; `GET /api/v1/identity/roles` |
| GAP-020 | `356d11e` | `Revoked` assignments with `revokedAt` and `revokedBy` (V0025) |
| GAP-019, OQ-067 | `4d6cad6` | `POST /api/v1/identity/tenant-administrators`, approved by a second platform administrator (V0026); runbook covers the Keycloak user |
| Per-record history | `f9988f8` | Every change leaves an audit entry; `GET /api/v1/audit/records/{targetType}/{targetId}` |
| GAP-025, GAP-030 | with GAP-008 and GAP-005 | All CHECK-constrained static data columns are enums; the 503, `Retry-After`, `Content-Language` and extensions are declared |

The contract changes, with the schema renames, are in [`docs/api/contract-changes.md`](../api/contract-changes.md).

## Notes for the front end

- **Schema names (GAP-031).** This batch already stops merging same-named types: the colliding names now carry their module (`GeneralProposalView`, `LifeProposalView` …), and General's responses are documented with General's own shapes. Batch 2 applies module-specific IDs to every schema, as asked, so every generated type name will change again. I will say so before that merges.
- **The first administrator** of a new tenant acts once the tenant is Active. The permission cache keeps a refused sign-in for up to 30 seconds (ADR-006).

## GAP-034: readiness is instance-wide (confirmed)

`/health/ready` includes `integration.outbox_lag`: the age of the oldest unpublished event, across every module outbox and every tenant. Past 5 minutes it reports Unhealthy, and the instance is taken out of service.

A consumer that fails because one tenant lacks billing or document configuration keeps its event unpublished. The event is retried up to 10 times with exponential back-off, which is longer than 5 minutes. So one tenant's missing configuration takes the instance out of service for every tenant. The fix, and an ADR proposal if it changes the outbox and inbox design, come in batch 2.

## Next (branch `wave0-frontend-2`)

In this order:

1. GAP-031: module schema IDs for every schema, a CI test for duplicate IDs, and a test of real responses against their schemas.
2. GAP-034.
3. GAP-022, 023, 024, 027 and 029, with the party-search access log.
4. Front-end requests 19 to 24.
