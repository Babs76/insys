-- =====================================================================
-- INSYS Core · Phase 5 follow-ups
-- 1. Bank account verification through Workflow (ADR-013): an account is activated or rejected by a second person's
--    approval; a change while approval is pending withdraws that approval. Workflow approvals can name the permission
--    the checker must hold, and can be withdrawn by the requester.
-- 2. Health concurrency (ADR-012): a claim belongs to one payment run, and never moves to another.
-- 3. OQ-061 decisions as configuration: the product's eligibility rule set and its rule on arrears at reinstatement;
--    the plan's basis for member reimbursement claims; eligibility outcomes mapped to response codes; the history
--    window of adjudication edit rules.
-- =====================================================================

-- 1. Bank accounts: REJECTED is never activated; one account per party and currency is pending or active.
ALTER TABLE party.bank_account
  DROP CONSTRAINT ck_bank_account_status,
  DROP CONSTRAINT ex_bank_account_current,
  ADD COLUMN requested_by uuid,
  ADD COLUMN approval_id uuid,
  ADD CONSTRAINT ck_bank_account_status CHECK (status IN ('PENDING_VERIFICATION', 'ACTIVE', 'SUPERSEDED', 'REJECTED'));
ALTER TABLE party.bank_account ADD CONSTRAINT ex_bank_account_current
  EXCLUDE USING btree (tenant_id WITH =, party_id WITH =, currency WITH =) WHERE (status IN ('PENDING_VERIFICATION', 'ACTIVE')) DEFERRABLE INITIALLY DEFERRED;
COMMENT ON COLUMN party.bank_account.approval_id IS 'The Workflow approval (BANK_ACCOUNT_VERIFICATION) that activated or rejected the account.';

ALTER TABLE workflow.approval
  DROP CONSTRAINT ck_approval_status,
  ADD COLUMN required_permission varchar(80),
  ADD CONSTRAINT ck_approval_status CHECK (status IN ('REQUESTED', 'APPROVED', 'REJECTED', 'EXPIRED', 'WITHDRAWN'));
COMMENT ON COLUMN workflow.approval.required_permission IS 'A permission the checker must hold, besides authority for the type (for example bank_account.verify).';

-- Life underwriting: the underwriting rule set's outcome for the case, so a decision that matches it within the
-- underwriter's authority needs no second person (Phase 5 review, item 3).
ALTER TABLE life.underwriting_case
  ADD COLUMN rule_outcome varchar(10),
  ADD COLUMN rule_decision_id uuid,
  ADD CONSTRAINT ck_underwriting_case_rule_outcome CHECK (rule_outcome IS NULL OR rule_outcome IN ('ACCEPT', 'REFER', 'DECLINE'));

-- 2. A claim is paid by one run: the run exists, and once set the run does not change.
ALTER TABLE health.medical_claim
  ADD CONSTRAINT fk_medical_claim_payment_run_id FOREIGN KEY (tenant_id, payment_run_id) REFERENCES health.provider_payment_run (tenant_id, id);
CREATE FUNCTION health.keep_payment_run() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF OLD.payment_run_id IS NOT NULL AND NEW.payment_run_id IS DISTINCT FROM OLD.payment_run_id THEN
    RAISE EXCEPTION 'Claim % already belongs to payment run %', OLD.claim_number, OLD.payment_run_id USING ERRCODE = 'check_violation';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER keep_payment_run BEFORE UPDATE OF payment_run_id ON health.medical_claim FOR EACH ROW EXECUTE FUNCTION health.keep_payment_run();
CREATE INDEX ix_medical_claim_payment_run ON health.medical_claim (tenant_id, payment_run_id) WHERE payment_run_id IS NOT NULL;

-- 3. Configuration for OQ-061.
ALTER TABLE product.cfg_product_definitions_and_versions ADD COLUMN eligibility_ruleset_code varchar(30);
ALTER TABLE product.cfg_product_definitions_and_versions ADD COLUMN reinstatement_arrears varchar(15) NOT NULL DEFAULT 'NONE';
ALTER TABLE product.cfg_product_definitions_and_versions ADD CONSTRAINT cfg_product_definitions_and_versions_ck3 CHECK (reinstatement_arrears IN ('NONE', 'REQUIRED'));
ALTER TABLE health.cfg_plans_and_options ADD COLUMN member_claim_basis varchar(20) NOT NULL DEFAULT 'REFERENCE_TARIFF';
ALTER TABLE health.cfg_plans_and_options ADD CONSTRAINT cfg_plans_and_options_ck2 CHECK (member_claim_basis IN ('REFERENCE_TARIFF', 'PEND'));
ALTER TABLE health.ref_eligibility_response_codes ADD COLUMN outcome varchar(15);
ALTER TABLE health.ref_eligibility_response_codes ADD CONSTRAINT ref_eligibility_response_codes_ck1 CHECK (outcome IN ('ELIGIBLE', 'NOT_ELIGIBLE', 'NOT_FOUND', 'WAITING_PERIOD'));
ALTER TABLE health.cfg_adjudication_edit_rules ADD COLUMN window_days integer;
COMMENT ON COLUMN product.cfg_product_definitions_and_versions.eligibility_ruleset_code IS 'The eligibility rule set a proposal on this version must pass (rules.cfg_eligibility_rule_sets); empty means any.';
COMMENT ON COLUMN product.cfg_product_definitions_and_versions.reinstatement_arrears IS 'NONE: a lapsed policy is reinstated without paying arrears; REQUIRED: overdue instalments are paid first. A business decision (OQ-061).';
COMMENT ON COLUMN health.cfg_plans_and_options.member_claim_basis IS 'Member reimbursement claims: REFERENCE_TARIFF (priced at the reference tariff, paid to the member''s verified account) or PEND (adjudicated by a person). A business decision (OQ-061).';
COMMENT ON COLUMN health.ref_eligibility_response_codes.outcome IS 'The internal eligibility outcome this response code answers.';
COMMENT ON COLUMN health.cfg_adjudication_edit_rules.window_days IS 'How many days of the patient''s claim history before the service date the rule''s condition may read (ADR-012); empty reads none.';
