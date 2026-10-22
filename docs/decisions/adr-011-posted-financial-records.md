# ADR-011: Posted financial records are insert-only, and journals balance in the database

- **Status:** Proposed (Phase 3)
- **Date:** 22 October 2026
- **Requirements:** FIN-001 to FIN-004, PAY-002, CLM-004, DBA-009

## Context
A journal must balance, and a posted record must never be overwritten. A correction is a reversal (FIN-002, FIN-003). Checking this in code alone is not enough: a bug, a script or a future module could write unbalanced or changed rows. `finance.journal` and `finance.subledger_entry` are partitioned by accounting date. A partition created later takes its grants from the schema defaults, not from its parent.

## Decision
- **Balance at commit (V0014):** deferred constraint triggers on `finance.journal` and `finance.subledger_entry` check each journal when the transaction commits. A journal has at least two lines, in one currency, and debits equal credits. `Journal` checks the same in code first.
- **No UPDATE or DELETE (DBA-009):** `db/roles/module_grants.sql` revokes UPDATE, DELETE and TRUNCATE from the module roles on these tables:
  - `finance.journal` and `finance.subledger_entry`, partitions included;
  - `claims.claim_financial_movement`;
  - `payment.payment_status_history`.
- **A trigger as well:** a `BEFORE UPDATE OR DELETE` trigger refuses both, even for the owner. It also covers a partition that received default grants.
- **Reversals:**
  - **No status change:** a reversal is a new journal that references the original. The original keeps status POSTED. "Reversed" is derived (a later journal reverses it), not stored. domain-model.json shows `Posted → Reversed`; that transition is represented by the reversing journal (OQ-057).
  - **Its own accounting date:** a reversal posts on its own accounting date, in an open period, because the original's period may be closed. V0014 drops the foreign key that required the original's date. A trigger checks that the original exists and is reversed at most once.
- **Posting rule versions (FIN-004):**
  - **Resolved on the accounting date:** the rule is the version in force on the accounting date, not the latest version.
  - **Stored on every line:** each line stores `posting_rule_code` and `posting_rule_version`.
  - **Reversal lines:** a reversal line keeps the original's rule and version.
- **Mappings:** EF maps these tables without `row_version` or `updated_*` columns. An integration test checks that every column each DbContext maps exists in the migrated schema.

## Consequences
- **Corrections:** fixing a posting means posting a reversal, then posting again.
- **Mixed currencies:** a multi-currency journal is not possible. Each journal is in one currency, with a base amount and rate per line.
- **Restores and data fixes:** these need the owner role with the triggers disabled, a deliberate and audited act.
