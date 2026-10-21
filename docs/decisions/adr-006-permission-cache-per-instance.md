# ADR-006: The permission cache is per instance

- **Status:** Accepted (Phase 1 review, item 3; resolves OQ-032)
- **Date:** 21 October 2026
- **Requirements:** IAM-005, TEN-004

## Context
Permissions are resolved per request from IAM role assignments and cached in memory for 30 seconds per tenant and subject (OQ-014). Evictions happen in the process that made the change:
- after the command commits;
- when `RoleAssigned`, `RoleRevoked`, `UserDisabled`, `AccessReviewCompleted` or `TenantSuspended` is consumed.

With the in-process event bus, each event is consumed by whichever instance's relay publishes it. So other instances do not evict.

## Decision
- **Revocation timing:** a revocation is immediate on the instance that made it. On any other instance it takes effect within the cache duration (`Identity:PermissionCache:Duration`, 30 seconds by default).
- **Single instance until evictions are distributed:** an API instance holds a PostgreSQL advisory lock for its lifetime (`SingleInstanceGuard`). A second instance on the same database refuses to start.
- **The escape hatch:** `Identity:PermissionCache:AllowMultipleInstances = true` turns the guard off, for an operator who accepts the delay above. The instance logs a warning when it starts.

## When to revisit
When a message broker is chosen (MESSAGE_BROKER, SRS §30), broadcast evictions to every instance, or move the cache behind `IPermissionCache` to a shared store. Then the guard can be removed.
