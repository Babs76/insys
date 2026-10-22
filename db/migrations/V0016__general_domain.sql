-- =====================================================================
-- INSYS Core · General domain (Phase 4). Gaps between V0001 and domain-model.json
-- (general context), recorded in OQ-059.
-- =====================================================================

-- Quotes are versioned: "an issued quote version is immutable; changes create a new version". V0001 has both
-- uq_quote_1 (quote_number, quote_version) and uq_quote_quote_number (quote_number alone), which allows only one
-- version; the second is dropped. A draft quote is not yet rated, so its premium and rating request come later.
ALTER TABLE general.quote DROP CONSTRAINT uq_quote_quote_number;
ALTER TABLE general.quote
  ALTER COLUMN premium DROP NOT NULL,
  ALTER COLUMN rating_request_id DROP NOT NULL,
  ADD COLUMN rating_version integer,
  ADD COLUMN period_start date,
  ADD COLUMN period_end date,
  ADD CONSTRAINT ck_quote_status CHECK (status IN ('DRAFT', 'RATED', 'REFERRED', 'ISSUED', 'ACCEPTED', 'REJECTED', 'EXPIRED')),
  ADD CONSTRAINT ck_quote_rated CHECK (status = 'DRAFT' OR (premium IS NOT NULL AND rating_request_id IS NOT NULL AND rating_version IS NOT NULL)),
  ADD CONSTRAINT ck_quote_period CHECK (period_end IS NULL OR period_end > period_start);

-- QuotedRisk and QuotedCoverage (domain-model.json) have no tables in V0001. Insert-only: a quote's risks and
-- coverages never change; a change is a new quote version.
CREATE TABLE general.quote_risk (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  quote_id                   uuid NOT NULL,
  risk_object_id             uuid NOT NULL,
  sum_insured                numeric(18,2) NOT NULL,
  CONSTRAINT pk_quote_risk PRIMARY KEY (tenant_id, id),
  CONSTRAINT fk_quote_risk_quote_id FOREIGN KEY (tenant_id, quote_id) REFERENCES general.quote (tenant_id, id),
  CONSTRAINT fk_quote_risk_risk_object_id FOREIGN KEY (tenant_id, risk_object_id) REFERENCES general.risk_object (tenant_id, id),
  CONSTRAINT uq_quote_risk_object UNIQUE (tenant_id, quote_id, risk_object_id),
  CONSTRAINT ck_quote_risk_sum_insured CHECK (sum_insured > 0)
);
CREATE TABLE general.quote_coverage (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  quote_risk_id              uuid NOT NULL,
  coverage_code              varchar(20) NOT NULL,
  sum_insured                numeric(18,2) NOT NULL,
  excess_schedule_code       varchar(20),
  CONSTRAINT pk_quote_coverage PRIMARY KEY (tenant_id, id),
  CONSTRAINT fk_quote_coverage_quote_risk_id FOREIGN KEY (tenant_id, quote_risk_id) REFERENCES general.quote_risk (tenant_id, id),
  CONSTRAINT uq_quote_coverage UNIQUE (tenant_id, quote_risk_id, coverage_code),
  CONSTRAINT ck_quote_coverage_sum_insured CHECK (sum_insured > 0)
);
CREATE INDEX ix_quote_risk_1 ON general.quote_risk (tenant_id, quote_id);
CREATE INDEX ix_quote_coverage_1 ON general.quote_coverage (tenant_id, quote_risk_id);
ALTER TABLE general.quote_risk ENABLE ROW LEVEL SECURITY;
ALTER TABLE general.quote_risk FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON general.quote_risk
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE general.quote_coverage ENABLE ROW LEVEL SECURITY;
ALTER TABLE general.quote_coverage FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON general.quote_coverage
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
-- Grants: db/roles/module_grants.sql gives insys_general its schema's tables.

-- A proposal carries the proposer's declarations (the Declaration value object).
ALTER TABLE general.proposal
  ADD COLUMN declarations jsonb NOT NULL DEFAULT '{}',
  ADD CONSTRAINT ck_proposal_status CHECK (status IN ('SUBMITTED', 'ACCEPTED', 'CONVERTED'));

ALTER TABLE general.underwriting_referral
  ADD COLUMN referred_by uuid,
  ADD COLUMN decided_at timestamptz,
  ADD CONSTRAINT ck_underwriting_referral_status CHECK (status IN ('OPEN', 'APPROVED', 'DECLINED')),
  ADD CONSTRAINT ck_underwriting_referral_decided CHECK ((status = 'OPEN') = (decided_by IS NULL AND decided_at IS NULL));

-- "Has a valuation basis whenever a value is set".
ALTER TABLE general.risk_object
  ADD CONSTRAINT ck_risk_object_status CHECK (status IN ('ACTIVE', 'INACTIVE')),
  ADD CONSTRAINT ck_risk_object_valuation CHECK (valuation IS NULL OR (valuation >= 0 AND valuation_basis IS NOT NULL));

-- How the policy is billed, so its renewal is billed the same way.
ALTER TABLE general.policy
  ADD COLUMN billing_frequency varchar(15),
  ADD COLUMN collection_method varchar(15),
  ADD COLUMN payer_party_id uuid,
  ADD COLUMN quote_id uuid,
  ADD CONSTRAINT ck_policy_status CHECK (status IN ('ISSUED', 'IN_FORCE', 'CANCELLED', 'EXPIRED', 'RENEWED')),
  ADD CONSTRAINT ck_policy_period CHECK (period_end > period_start),
  ADD CONSTRAINT ck_policy_premium CHECK (annual_premium >= 0);
ALTER TABLE general.coverage ADD CONSTRAINT ck_coverage_amounts CHECK (sum_insured > 0 AND premium >= 0);

-- A reinstated policy keeps its cancellation as history; only one cancellation is in effect at a time.
DROP INDEX general.uq_cancellation_1;
ALTER TABLE general.cancellation
  ADD COLUMN cancelled_by uuid,
  ADD COLUMN cancelled_at timestamptz,
  ADD COLUMN fee numeric(18,2) NOT NULL DEFAULT 0,
  ADD COLUMN reinstated_by uuid,
  ADD COLUMN reinstated_at timestamptz,
  ADD CONSTRAINT ck_cancellation_refund_basis CHECK (refund_basis IN ('PRO_RATA', 'SHORT_PERIOD', 'NONE')),
  ADD CONSTRAINT ck_cancellation_amounts CHECK (refund_amount >= 0 AND fee >= 0),
  ADD CONSTRAINT ck_cancellation_reinstated CHECK ((reinstated_by IS NULL) = (reinstated_at IS NULL));
CREATE UNIQUE INDEX uq_cancellation_in_effect ON general.cancellation (tenant_id, policy_id) WHERE reinstated_at IS NULL;

-- An endorsement's premium comes from a rating request, and it is applied only after a second person's approval.
ALTER TABLE general.endorsement
  ADD COLUMN rating_request_id uuid,
  ADD COLUMN new_annual_premium numeric(18,2),
  ADD COLUMN requested_by uuid,
  ADD COLUMN approval_id uuid,
  ADD COLUMN decided_by uuid,
  ADD CONSTRAINT ck_endorsement_rated CHECK (status = 'DRAFT' OR (rating_request_id IS NOT NULL AND new_annual_premium IS NOT NULL));

-- A first notification of loss has no claim number yet; the Claims Framework allocates it at registration
-- (process "General claim to settlement"). The settlement and the decision, with its rationale (CLM-003).
ALTER TABLE general.claim
  ALTER COLUMN claim_number DROP NOT NULL,
  ADD COLUMN coverage_code varchar(20),
  ADD COLUMN settlement_amount numeric(18,2),
  ADD COLUMN settlement_currency char(3),
  ADD COLUMN settlement_payee_party_id uuid,
  ADD COLUMN settlement_reserve_type varchar(15),
  ADD COLUMN settlement_requested_by uuid,
  ADD COLUMN settlement_approval_id uuid,
  ADD COLUMN decision_code varchar(15),
  ADD COLUMN decision_rationale text,
  ADD COLUMN decided_by uuid,
  ADD COLUMN decided_at timestamptz,
  ADD CONSTRAINT ck_claim_number CHECK (status = 'FIRST_NOTIFICATION' OR claim_number IS NOT NULL),
  ADD CONSTRAINT ck_claim_settlement CHECK (settlement_amount IS NULL OR settlement_amount > 0),
  ADD CONSTRAINT ck_claim_decided CHECK (status NOT IN ('APPROVED', 'SETTLED', 'DECLINED') OR (decision_code IS NOT NULL AND decision_rationale IS NOT NULL));

-- A repair estimate belongs to the loss assessment that records it.
ALTER TABLE general.repair_estimate
  ADD COLUMN loss_assessment_id uuid,
  ADD CONSTRAINT fk_repair_estimate_loss_assessment_id FOREIGN KEY (tenant_id, loss_assessment_id) REFERENCES general.loss_assessment (tenant_id, id),
  ADD CONSTRAINT ck_repair_estimate_status CHECK (status IN ('SUBMITTED', 'ACCEPTED', 'REJECTED')),
  ADD CONSTRAINT ck_repair_estimate_amount CHECK (amount >= 0);
ALTER TABLE general.loss_assessment ADD COLUMN accepted_by uuid;

ALTER TABLE general.recovery_case
  ADD CONSTRAINT ck_recovery_case_amounts CHECK (expected_amount >= 0 AND recovered_amount >= 0);
ALTER TABLE general.salvage_item ADD CONSTRAINT ck_salvage_item_proceeds CHECK (proceeds IS NULL OR proceeds >= 0);
