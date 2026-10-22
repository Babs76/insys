-- =====================================================================
-- INSYS Core · Schema gaps found by auditing V0001 against domain-model.json
-- (Phase 2 review item 3).
-- =====================================================================

-- (a) Status columns too short for their states, and no CHECK on the states.
--     States are domain-model.json's, upper-case with underscores.
ALTER TABLE claims.claim_payment_instruction ALTER COLUMN status TYPE varchar(30),
  ADD CONSTRAINT ck_claim_payment_instruction_status
  CHECK (status IN ('DRAFTED', 'PENDING_APPROVAL', 'APPROVED', 'REJECTED', 'RELEASED', 'PAID', 'FAILED'));
ALTER TABLE payment.reconciliation_exception ALTER COLUMN status TYPE varchar(30),
  ADD CONSTRAINT ck_reconciliation_exception_status CHECK (status IN ('OPEN', 'INVESTIGATING', 'RESOLVED'));
ALTER TABLE life.underwriting_case ALTER COLUMN status TYPE varchar(30),
  ADD CONSTRAINT ck_underwriting_case_status CHECK (status IN ('OPEN', 'AWAITING_EVIDENCE', 'IN_ASSESSMENT', 'REFERRED', 'DECIDED'));
ALTER TABLE life.maturity_case ALTER COLUMN status TYPE varchar(30),
  ADD CONSTRAINT ck_maturity_case_status CHECK (status IN ('IDENTIFIED', 'NOTIFIED', 'OPTION_CHOSEN', 'APPROVED', 'PAID'));
ALTER TABLE general.endorsement ALTER COLUMN status TYPE varchar(30),
  ADD CONSTRAINT ck_endorsement_status CHECK (status IN ('DRAFT', 'RATED', 'PENDING_APPROVAL', 'APPROVED', 'REJECTED', 'APPLIED'));
ALTER TABLE general.claim ALTER COLUMN status TYPE varchar(30),
  ADD CONSTRAINT ck_claim_status
  CHECK (status IN ('FIRST_NOTIFICATION', 'REGISTERED', 'UNDER_ASSESSMENT', 'APPROVED', 'DECLINED', 'SETTLED', 'CLOSED', 'REOPENED'));
ALTER TABLE general.recovery_case ALTER COLUMN status TYPE varchar(30),
  ADD CONSTRAINT ck_recovery_case_status CHECK (status IN ('OPEN', 'PURSUING', 'RECOVERED', 'WRITTEN_OFF', 'CLOSED'));
ALTER TABLE health.preauthorisation ALTER COLUMN status TYPE varchar(30),
  ADD CONSTRAINT ck_preauthorisation_status
  CHECK (status IN ('REQUESTED', 'IN_REVIEW', 'APPROVED', 'CONDITIONALLY_APPROVED', 'REJECTED', 'UTILISED', 'EXPIRED', 'CANCELLED'));
ALTER TABLE health.medical_claim ALTER COLUMN status TYPE varchar(30),
  ADD CONSTRAINT ck_medical_claim_status
  CHECK (status IN ('RECEIVED', 'VALIDATED', 'PENDED', 'ADJUDICATED', 'APPROVED_FOR_PAYMENT', 'PAID', 'REJECTED', 'REVERSED'));
ALTER TABLE health.fraud_case ALTER COLUMN status TYPE varchar(30),
  ADD CONSTRAINT ck_fraud_case_status CHECK (status IN ('OPENED', 'INVESTIGATING', 'CONCLUDED'));

-- (b) A loss assessment exists before its figures: Assigned → In progress → Reported → Accepted.
ALTER TABLE general.loss_assessment
  ADD COLUMN status varchar(30) NOT NULL DEFAULT 'REPORTED',
  ALTER COLUMN assessed_amount DROP NOT NULL,
  ALTER COLUMN excess_applied DROP NOT NULL,
  ALTER COLUMN assessed_at DROP NOT NULL,
  ADD CONSTRAINT ck_loss_assessment_status CHECK (status IN ('ASSIGNED', 'IN_PROGRESS', 'REPORTED', 'ACCEPTED')),
  ADD CONSTRAINT ck_loss_assessment_reported CHECK (
    status NOT IN ('REPORTED', 'ACCEPTED') OR (assessed_amount IS NOT NULL AND excess_applied IS NOT NULL AND assessed_at IS NOT NULL));
ALTER TABLE general.loss_assessment ALTER COLUMN status DROP DEFAULT;

-- (c) The state of accounting periods. finance.cfg_accounting_calendar_and_periods (static data) defines the
--     periods; the AccountingPeriod aggregate owns their state here. At most one OPEN period covers a date.
CREATE TABLE finance.accounting_period (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  period_code                varchar(7) NOT NULL,
  start_date                 date NOT NULL,
  end_date                   date NOT NULL,
  status                     varchar(12) NOT NULL,
  closed_by                  uuid,
  closed_at                  timestamptz,
  CONSTRAINT pk_accounting_period PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_accounting_period_code UNIQUE (tenant_id, period_code),
  CONSTRAINT ck_accounting_period_status CHECK (status IN ('FUTURE', 'OPEN', 'SOFT_CLOSE', 'CLOSED')),
  CONSTRAINT ck_accounting_period_dates CHECK (end_date >= start_date),
  CONSTRAINT ck_accounting_period_closed CHECK ((status IN ('SOFT_CLOSE', 'CLOSED')) = (closed_by IS NOT NULL AND closed_at IS NOT NULL)),
  CONSTRAINT ex_accounting_period_one_open EXCLUDE USING gist (
    tenant_id WITH =, daterange(start_date, end_date, '[]') WITH &&) WHERE (status = 'OPEN')
);
ALTER TABLE finance.accounting_period ENABLE ROW LEVEL SECURITY;
ALTER TABLE finance.accounting_period FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON finance.accounting_period
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
-- Grants: db/roles/module_grants.sql gives insys_finance its schema's tables.

-- (d) record_status is the single lifecycle of these static tables (ADR-010, OQ-053). The duplicate status
--     columns are no longer written; they stay, nullable, until a later release drops them.
ALTER TABLE product.cfg_product_definitions_and_versions ALTER COLUMN status DROP NOT NULL;
ALTER TABLE integration.cfg_api_catalogue_entries ALTER COLUMN status DROP NOT NULL;
ALTER TABLE integration.cfg_event_schema_versions ALTER COLUMN status DROP NOT NULL;
ALTER TABLE finance.cfg_accounting_calendar_and_periods ALTER COLUMN status DROP NOT NULL;
COMMENT ON COLUMN product.cfg_product_definitions_and_versions.status IS 'Deprecated (ADR-010): record_status is the lifecycle. Not written.';
COMMENT ON COLUMN integration.cfg_api_catalogue_entries.status IS 'Deprecated (ADR-010): record_status is the lifecycle. Not written.';
COMMENT ON COLUMN integration.cfg_event_schema_versions.status IS 'Deprecated (ADR-010): record_status is the lifecycle. Not written.';
COMMENT ON COLUMN finance.cfg_accounting_calendar_and_periods.status IS
  'Deprecated (ADR-010): finance.accounting_period holds the state of a period. Not written.';
