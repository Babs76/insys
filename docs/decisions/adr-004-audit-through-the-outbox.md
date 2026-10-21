# ADR-004: Audit entries travel through each module's outbox

- **Status:** Accepted
- **Date:** 21 October 2026
- **Requirements:** IAM-006, DBA-016, TEN-005

## Decision
- **Commands:** a command's audit entries are published as `audit.SensitiveOperationRecorded` in the module's own outbox:
  - **on success:** in the command's transaction, so an audit record exists if and only if the change committed;
  - **on failure:** in a separate transaction, with outcome FAILURE.
- **Recording:** the Audit module consumes the events and writes `audit.audit_event`, using the event ID as the row ID, so a duplicate delivery changes nothing. This matches `domain-model.json` ("Audit consumes all sensitive operations").
- **Denied requests** have no business transaction. They are written directly through `IAuditSink`, on the Audit module's role.

## Consequences
- Audit rows appear one relay pass after the change, typically within 2 seconds. The outbox guarantees they arrive.
- No module role needs access to the audit schema.
