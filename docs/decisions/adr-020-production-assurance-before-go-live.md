# ADR-020: Production assurance before final go-live

- **Status:** Accepted (Phase 8 brief, decisions table, 23 October 2026)
- **Date:** 23 October 2026
- **Requirements:** NFR-005, NFR-006, NFR-014, NFR-015; SRS §27.4

## Decision
Load testing, penetration testing, and backup and DR testing are done **before final go-live, outside Phase 8**.

What Phase 8 keeps doing:
- the automated §27.4 security suite;
- the performance smoke tests of the hot paths (Phase 7);
- the backup and recovery runbook, which is a proposal.

## Consequences
- None of the Phase 8 reports claims load, penetration or DR assurance.
- **Before go-live,** these must exist and be signed off:
  - a load test against NFR-015, on the target RDS and CCE sizing in af-south-1 (ADR-016);
  - an external penetration test;
  - a restore test against the RPO and RTO targets (NFR-005 and NFR-006).
