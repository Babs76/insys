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

## The country overlays (Ade, 16 November, OQ-181)
**Decided:**
- Overlays merge into the earlier row field by field, and a null in an overlay means "no change", never "set to null".
- The layers are the base, then the lines, then the country.
- After them comes the tenant's own approved overlay. It fills only values still empty, and never replaces a regulated value from a platform-approved version.

`ng.json` and `za.json` rows that give only the fields they change are therefore correct as written (the calendar, the complaint type, the limits' currency). What changed is below.

Rows that name what to review but hold no record yet went to a new `pending` list in the file, which isn't loaded. These are candidate document types, retention, register entries, taxes and treaties. Which records exist is compliance's call.

`nigeria-review-sheet.csv` was written before this and still lists the old codes.

| File | Table | Was | Now | What |
|---|---|---|---|---|
| `overlays/ng.json` | `tenant.cfg_locale_defaults` | `LOCALE (non-regulated part), LOCALE (currency)` | `LOCALE` | two rows for one record joined as `LOCALE`, FINANCE (the stricter class, as the currency is regulated); `tenant` added as null (the tenant's own code, not a pack's) |
| `overlays/ng.json` | `refdata.ref_public_holiday_calendars` | `NG` | (removed) | moved to `pending`, not loaded: a platform-wide table (one row per holiday date), and no dates are given yet |
| `overlays/ng.json` | `workflow.cfg_case_timelines` | `COMPLAINT_ACKNOWLEDGE, COMPLAINT_RESOLVE` | `COMPLAINT_ACKNOWLEDGE`, `COMPLAINT_RESOLVE` | split into one row per step; `case_type_code`, `step` added; `duration` from the step's value where it is one duration; null otherwise (NG gives none; ZA's resolve time has two sources, kept in the note) |
| `overlays/ng.json` | `channel.cfg_disclosure_and_notice_texts` | `DISC-QUOTE, -ISSUE, -RENEWAL, -CLAIM` | `DISC-QUOTE`, `DISC-ISSUE`, `DISC-RENEWAL`, `DISC-CLAIM` | split into one row per display point; `display_point` from the code; `text_version` added as null |
| `overlays/ng.json` | `communication.cfg_notification_templates` | `CLAIM_DECISION_RIGHTS` | `CLAIM_DECISION_RIGHTS` | `type_code` and `channel` added as null: required, not given |
| `overlays/ng.json` | `party.ref_identity_document_types` | `(rows)` | (removed) | moved to `pending`, not loaded: candidate document types for review, not records yet (which ones exist is compliance's) |
| `overlays/ng.json` | `party.cfg_consent_purposes` | `(rows)` | (removed) | moved to `pending`, not loaded: no record yet: the row names what to review (purposes) |
| `overlays/ng.json` | `party.cfg_record_retention_policies` | `(rows)` | (removed) | moved to `pending`, not loaded: no record yet: the row names what to review (retention) |
| `overlays/ng.json` | `audit.cfg_audit_retention_policies` | `(rows)` | (removed) | moved to `pending`, not loaded: no record yet: the row names what to review (retention) |
| `overlays/ng.json` | `document.cfg_retention_schedules` | `(rows)` | (removed) | moved to `pending`, not loaded: no record yet: the row names what to review (retention) |
| `overlays/ng.json` | `reporting.cfg_compliance_register_entries` | `(rows)` | (removed) | moved to `pending`, not loaded: no record yet: the row names what to review (entries) |
| `overlays/ng.json` | `rating.cfg_premium_tax_and_levy_rates` | `(rows)` | (removed) | moved to `pending`, not loaded: no record yet: the row names what to review (tax_code, rate, base, rounding) |
| `overlays/ng.json` | `finance.cfg_tax_codes_on_financial_transactions` | `(rows)` | (removed) | moved to `pending`, not loaded: no record yet: the row names what to review (tax_code, rate, account) |
| `overlays/ng.json` | `rating.cfg_rounding_and_currency_precision_rules` | `NGN-2DP` | `NGN-2DP` | `rule_code` added (the row code) |
| `overlays/ng.json` | `workflow.cfg_delegation_and_approval_authority` | `APPROVER_*` | `AUTH-{type} (28 rows)` | split into one row per approval type, each merging into the base's `AUTH-{type}`; `currency` on each; `limit_value` left out (null: no change) |
| `overlays/ng.json` | `payment.ref_payment_methods` | `COLLECT, PAY` | `COLLECT`, `PAY` | split into one row per method; `method_code` and `direction` from the code, as the seed has them |
| `overlays/ng.json` | `billing.cfg_arrears_ageing_buckets` | `B1, B2` | (removed) | removed from the rows: its values held only a note ("Replaces the base pack if premium must be paid before cover."), kept in `pending` |
| `overlays/ng.json` | `billing.ref_collection_methods` | `(rows)` | (removed) | moved to `pending`, not loaded: no record yet: the row names what to review (candidates, lead_time) |
| `overlays/ng.json` | `reinsurance.cfg_treaty_definitions` | `(rows)` | (removed) | moved to `pending`, not loaded: no record yet: the row names what to review (capacity, currency) |
| `overlays/ng.json` | `general.cfg_settlement_authority_limits` | `(rows)` | (removed) | moved to `pending`, not loaded: no record yet: the row names what to review (limit_value) |
| `overlays/za.json` | `tenant.cfg_locale_defaults` | `LOCALE` | `LOCALE` | `tenant` added as null: the tenant's own code, not a pack's |
| `overlays/za.json` | `refdata.ref_public_holiday_calendars` | `ZA` | (removed) | moved to `pending`, not loaded: a platform-wide table (one row per holiday date), and no dates are given yet |
| `overlays/za.json` | `workflow.cfg_case_timelines` | `COMPLAINT_ACKNOWLEDGE, COMPLAINT_RESOLVE` | `COMPLAINT_ACKNOWLEDGE`, `COMPLAINT_RESOLVE` | split into one row per step; `case_type_code`, `step` added; `duration` from the step's value where it is one duration, with `basis` CALENDAR and `extension_allowed` false as the seed has them; null otherwise (NG gives none; ZA's resolve time has two sources, kept in the note) |
| `overlays/za.json` | `workflow.cfg_delegation_and_approval_authority` | `APPROVER_*` | `AUTH-{type} (28 rows)` | split into one row per approval type, each merging into the base's `AUTH-{type}`; `currency` on each; `limit_value` from the text "1000000 (5000000 for LIFE_UNDERWRITING)": 1000000, and 5000000 for LIFE_UNDERWRITING |
| `overlays/za.json` | `payment.ref_payment_methods` | `COLLECT, PAY` | `COLLECT`, `PAY` | split into one row per method; `method_code` and `direction` from the code, as the seed has them |
| `overlays/za.json` | `billing.ref_collection_methods` | `DEBIT_ORDER` | `DEBIT_ORDER` | `method_code` added (the row code) |
| `overlays/za.json` | `payment.mst_tenant_bank_accounts` | `BANK` | `BANK` | `account_ref`, `bank`, `account_number`, `gl_account` added as null: the tenant's own account |
| `overlays/za.json` | `rating.cfg_rounding_and_currency_precision_rules` | `ZAR-2DP` | `ZAR-2DP` | `rule_code` added (the row code) |
| `overlays/za.json` | `party.ref_identity_document_types` | `ZA-PASSPORT` | `ZA-PASSPORT` | `label` written from `doc_type` |
| `overlays/za.json` | `channel.cfg_disclosure_and_notice_texts` | `DISC-QUOTE, -ISSUE, -RENEWAL, -CLAIM` | `DISC-QUOTE`, `DISC-ISSUE`, `DISC-RENEWAL`, `DISC-CLAIM` | split into one row per display point; `display_point` from the code; `text_version` added as null |
| `overlays/za.json` | `party.cfg_record_retention_policies` | `(platform default)` | (removed) | moved to `pending`, not loaded: no record yet: the row names what to review (retention) |

## The case notification templates (Ade, 16 November, OQ-182)
The three base templates' bodies are null, so the templates are Missing until compliance reviews the proposed wording. The proposal is in each row's `note`, marked FOR COMPLIANCE REVIEW, and uses only the merge fields the types offer: sender_identity, portal_link, case_number, case_type, expected_date, decision_reasons, review_options, ombud_details and representations_deadline.

| Template | Proposed body |
|---|---|
| `CASE_ACKNOWLEDGED_EN` | We have received your case {case_number} ({case_type}). We expect to reply by {expected_date}. You can follow it at {portal_link}. {sender_identity} |
| `CASE_UPDATE_EN` | There is an update on your case {case_number}. Sign in to see it: {portal_link}. {sender_identity} |
| `CASE_DECISION_EN` | We have decided your case {case_number}. Our reasons: {decision_reasons}. {review_options} {ombud_details} Sign in to see the decision: {portal_link}. {sender_identity} |

## The APPROVER role (Ade, 16 November)
The pack keeps no grants for `APPROVER`. The tenant administrator grants its functions during onboarding, through role-function assignments (ADR-026). The seed's permission list stays in the row's note, for reference only.
