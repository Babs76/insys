# Starter packs: format corrections (16 November 2026)

**A format correction, not a requirement change** (OQ-179, Ade, 16 November). The drafts held some rows the import path (BR-016) can't take. Some rows held several records, some lacked fields their table requires, and some named fields their table doesn't have. Batch 7.7's completeness check found them. Each row now holds one record, under its table's own column names. No value was changed, and no regulated value was filled in.

**Where a value came from:**
- **Already in the pack row:** kept as it was, moved under its own record.
- **The row code:** used where the table has a code field the row lacked (`layout_code`, `table_code`, `bucket_code`, and so on).
- **The seed** (`tests/Insys.Testing/OnboardingSeed.cs`), named there explicitly: `link_types` and `requires_category` of COMPLAINT, and the posting rules' order. They are numbered PR-1 to PR-13, because the base's own reserve type already names PR-4. The seed fills columns it doesn't name with placeholders, which aren't taken.
- **A label written from the code,** as the catalogue's own samples do (MONTHLY, "Monthly"): billing frequencies and document categories only.
- **No source: added as null.** The field is required, but neither the pack nor the seed gives it. In a FINANCE or COMPLIANCE row, a null keeps the row Missing until an approved overlay or the tenant gives it (AIA-082). In an OPERATIONAL row, the import refuses it until an overlay gives it or the reviewer rejects it.

**Left as it was:** `rules.cfg_authority_matrices` `AM` in the motor line. It is regulated and empty, so it stays Missing, for compliance and the customer.

## Every change

| File | Table | Was | Now | What |
|---|---|---|---|---|
| `base.json` | `finance.cfg_posting_rules` | `PR-*` | `PR-1`, `PR-2`, `PR-3`, `PR-4`, `PR-5`, `PR-6`, `PR-7`, `PR-8`, `PR-9`, `PR-10`, `PR-11`, `PR-12`, `PR-13` | split one row of 13 transaction types into one rule per type; `transaction_types` (not a field) becomes each rule's `transaction_type`; `rule_code` added, numbered PR-1 to PR-13 in the seed's order (the reserve type INDEMNITY already names PR-4) |
| `base.json` | `finance.cfg_gl_export_layouts` | `GL` | `GL` | `layout_code` added (the row code); `target_system` and `schedule` added as null: required, with no source (the seed fills them with placeholders). The row is FINANCE, so it stays Missing |
| `base.json` | `workflow.ref_business_calendars` | `CAL` | `CAL` | `holiday_calendar` and `time_zone` added as null: required, and the row's note says they come from the country overlay. Until one gives them, the import refuses the row |
| `base.json` | `workflow.cfg_case_types` | `COMPLAINT, COMPLAINT_REVIEW` | `COMPLAINT`, `COMPLAINT_REVIEW` | split into one row per case type; `case_type_code` added; COMPLAINT takes `link_types` [POLICY, CLAIM] and `requires_category` false from the seed; `appeal_route` added as null on both (the note: from the country overlay), and COMPLAINT_REVIEW's `link_types` and `requires_category` as null (no source). COMPLIANCE rows, so both stay Missing |
| `base.json` | `workflow.cfg_delegation_and_approval_authority` | `APPROVER_*` | `AUTH-{type} (28 rows)` | "every type the tenant can raise" (`types`, not a field) becomes one row per approval type, the 28 declared at this commit, as the seed seeds them; `transaction_type` added; `limit_value` added as null (the note: from the country overlay). FINANCE rows, so they stay Missing |
| `base.json` | `iam.cfg_roles_and_permission_sets` | `APPROVER` | `APPROVER` | `functions` (not a field) removed from the values, and its list kept in the row's `note`. The list holds permission codes, and `permissions` is retired (ADR-026: a role's grants are role-function assignments, which the pack doesn't hold). Which functions to grant is a permissions decision, not format, so the role loads with no grants |
| `base.json` | `communication.cfg_notification_types` | `CASE_*` | `CASE_ACKNOWLEDGED`, `CASE_UPDATE`, `CASE_DECISION` | split into one row per notification type; `types` (not a field) becomes each row's `type_code` |
| `base.json` | `communication.cfg_notification_templates` | `CASE_*_EN` | `CASE_ACKNOWLEDGED_EN`, `CASE_UPDATE_EN`, `CASE_DECISION_EN` | split into one template per notification type; `subjects` (not a field) becomes each row's `subject`; `type_code` added |
| `base.json` | `document.ref_document_categories` | `POLICY_SCHEDULE, REMITTANCE_ADVICE` | `POLICY_SCHEDULE`, `REMITTANCE_ADVICE` | split into one row per record, each record's values from under its code (not a field); added: `allowed_roles` as null (required, no source: who may see a category is the tenant's), `category_code`, `label` written from the code |
| `base.json` | `document.cfg_document_templates` | `TPL-1, TPL-RA` | `TPL-1`, `TPL-RA` | split into one row per record, each record's values from under its code (not a field); added: `merge_fields` as null (required, no source), `template_code` |
| `base.json` | `billing.ref_billing_frequencies` | `MONTHLY, ANNUAL` | `MONTHLY`, `ANNUAL` | split into one row per record, each record's values from under its code (not a field); added: `label` written from the code (the catalogue's sample: MONTHLY, "Monthly") |
| `base.json` | `billing.cfg_arrears_ageing_buckets` | `B1, B2` | `B1`, `B2` | split into one row per record, each record's values from under its code (not a field); added: `bucket_code` |
| `base.json` | `payment.ref_payment_status_mappings` | `(per provider)` | (removed) | removed: the row holds no record, only a note ("one row per real provider status"); the note moves to `excluded` |
| `base.json` | `claims.cfg_claim_numbering_schemes` | `CLM, LIF` | `CLM`, `LIF` | split into one row per record, each record's values from under its code (not a field) |
| `base.json` | `claims.ref_claim_decision_reason_codes` | `SETTLE, PART-EXCL` | `SETTLE`, `PART-EXCL` | split into one row per record, each record's values from under its code (not a field) |
| `overlays/line-general-motor.json` | `rating.cfg_rating_tables` | `BASE` | `BASE` | `table_code` added (the row code) |

## Not corrected: content, for Ade and compliance
- **`communication.cfg_notification_templates` `CASE_ACKNOWLEDGED_EN`, `CASE_UPDATE_EN`, `CASE_DECISION_EN`:** the body `{subject}. Sign in to see it.` uses `{subject}`, which these notification types don't offer. They offer sender_identity, portal_link, case_number, case_type, expected_date, decision_reasons, review_options, ombud_details and representations_deadline, so the import refuses the body. It is compliance wording, a value, so it isn't changed here.

## Not corrected: the country overlays, for Ade

`overlays/ng.json` and `overlays/za.json` are written to be merged into the base. Their rows give only the fields that differ: the `CAL` calendar row gives `holiday_calendar` and `time_zone`, and the `APPROVER_*` authority row gives `limit_value` and `currency`. The assembly (7.6) and this README say a later layer's row *replaces* the earlier one with the same table and code. So such a row would drop the base row's other fields, and the import would refuse it. NG also holds placeholder rows (`(rows)`, `candidates`, `retention`) that aren't records yet.

Correcting them needs a decision first:
- **(a)** overlays hold complete rows (a format correction like this one); or
- **(b)** the assembly merges an overlay row's fields into the earlier row (a change to 7.6's behaviour).

Until then, only the base and line packs load as they stand.
