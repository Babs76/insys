# Front-end requests

Requests from the front end reach the back end through Ade. The front end is now `insys-console` (`Frontend/insys-console`), building one wave behind the back end; the back end does not read or write its repository. Requests 1 to 50 came from the first front end (`insys-web`) and were tracked in its `docs/backend-requests.md`; they are closed or recorded in `docs/traceability/functional-coverage.md` (GAP-001 to GAP-052).

Each wave's batch report has the front-end readiness checklist and the independent API review.

| # | Request | Received | Status | Where |
|---|---|---|---|---|
| 51 | Make the Analytics projection rebuild automatic, once per tenant | Wave 2 batch 1 | Done (`wave2-1`) | `analytics.release_task`; `deployment.md` release steps |
| 52 | `yyyy-MM` pattern on Analytics month parameters; enum of the dashboard figure codes | Wave 2 batch 1 | Done (`wave2-1`) | `contract-changes.md`, Wave 2 batch 1 |
| 53 | Integration guide: what rejecting does where the domain model has no reject transition | Wave 2 batch 1 | Answered | [`frontend-integration-guide.md`](frontend-integration-guide.md) §1 |
| 54 | Integration guide: who maintains each static data tier in a tenant | Wave 2 batch 1 | Answered; default role templates open (OQ-111) | guide §2 |
| 55 | Integration guide: authority bands per role, where configured, OFT-ZA sample values | Wave 2 batch 1 | Answered; samples for the business to confirm (OQ-112) | guide §3 |
| 56 | Integration guide: how a Life underwriting decision above authority is held | Wave 2 batch 1 | Answered | guide §4 |
| 57 | Keycloak redirect and post-logout URIs per environment for `insys-web`, `broker-portal`, `member-portal`, `provider-portal` | Wave 2 batch 1 | Done (`wave2-1`) | `runbooks/deployment.md`, Front-end addresses per environment |
