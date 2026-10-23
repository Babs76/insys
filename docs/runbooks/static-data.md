# Static data changes

Reference data and configuration (254 tables) change only through versions and approvals (ADR-003, SDM-002 to SDM-015). Nobody edits these tables in the database.

## Where each table lives

Every table has the same endpoints, under its owning module: `/api/v1/<module>/static/<table>`. For example, `/api/v1/rating/static/rounding-and-currency-precision-rules`.

- Tenant rows use the plain path.
- Platform-wide rows (`tenant_id` NULL) use `…/global`. Only the platform operator tenant can call those, on the platform administrator connection.
- The OpenAPI document `docs/api/openapi-static-data.json` lists every table.

| Step | Call | Permission |
|---|---|---|
| See what is in force | `GET …/<table>`, `GET …/<table>/{code}` | static_data.read |
| See every version | `GET …/<table>/{code}/versions` | static_data.read |
| Draft | `POST …/<table>` | static_data.create |
| Edit a draft (maker only) | `PUT …/<table>/{code}/versions/{n}` with If-Match | static_data.update |
| Submit for approval | `POST …/{code}/versions/{n}/submit` with If-Match | static_data.update |
| Approve or reject | `POST …/{code}/versions/{n}/approve` or `…/reject` | static_data.approve |
| Put in force | `POST …/{code}/versions/{n}/activate` | static_data.update |
| Stop new use | `POST …/{code}/versions/{n}/deprecate` | static_data.update |
| Hide | `POST …/{code}/versions/{n}/retire` | static_data.update |
| Bulk load | `POST …/<table>/imports`: all or nothing; every failing line is reported (SDM-012) | static_data.create |

## Approvals by tier

| Tier | Who approves |
|---|---|
| 1 | Always a second person (SDM-003). The checker needs `static_data.approve` and, once the tenant has configured it, `STATIC_DATA_T1` authority (OQ-050). |
| 2 | A second person by default. A tenant can switch this off (`PUT /api/v1/static-data/settings/tier2-maker-checker` [governance_setting.update], If-Match). Switching it off itself needs approval in Workflow, and is audited. |
| 3 | Approved on submission. |

- Approved versions are immutable. To correct one, draft a new version.
- A submitted change appears as a change request (`GET /api/v1/static-data/change-requests` [change_request.read]) within one relay pass. Approving it before then answers 404 `CHANGE_REQUEST_NOT_FOUND`; try again a few seconds later.

## Before changing something important

- Tier 1 activation has no impact analysis or simulation yet (OQ-025). Before activating a rating, rules or product change, test it in a non-production tenant.
- A change to a table that other records point to by code takes effect for new work only. Records already made keep the version they were made with. For example, policies keep their product version, and journals keep their posting rule version.
- For a new tenant, load the tables in the order of the onboarding steps (OQ-029; see [tenants-and-access.md](tenants-and-access.md)).

## Error code catalogue

The error code catalogue is global static data. It is generated from the code (`db/migrations/R__error_code_catalogue.sql`) and loaded by the migration runner.

- Missing codes are added.
- Changing an existing code's status or message needs a new version, made through the global endpoints above.
