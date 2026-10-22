-- =====================================================================
-- INSYS Core · Phase 5: Life and Health
-- 1. Health adjudication defaults (ADR-012, proposed, pending business sign-off; OQ-060): two new static
--    tables, and columns linking tariffs, contracts, networks, plans, providers, rules, limits, exclusions
--    and waiting periods. Static tables keep V0002's form so the generator reads them.
-- 2. Health transactional gaps: the accumulator's reservation (OQ-060 item 12), waiting periods fixed at
--    enrolment, adjudication results per claim line, and payment run and remittance details.
-- 3. Life transactional gaps: eligibility and beneficiary nominations on the proposal, evidence waivers,
--    billing on the policy, the claim decision and its payees.
-- =====================================================================

-- ---------------------------------------------------------------------
-- 1. Health static data (ADR-012)
-- ---------------------------------------------------------------------

CREATE TABLE health.cfg_benefit_category_mappings (
  id                             uuid NOT NULL DEFAULT gen_random_uuid(),
  tenant_id                      uuid NOT NULL,
  code                           varchar(120) NOT NULL,
  version                        integer NOT NULL DEFAULT 1,
  effective_from                 date NOT NULL DEFAULT CURRENT_DATE,
  effective_to                   date,
  record_status                  varchar(20) NOT NULL DEFAULT 'DRAFT',
  supersedes_id                  uuid,
  labels                         jsonb,
  description                    text,
  record_source                  varchar(60),
  record_source_version          varchar(30),
  external_mappings              jsonb,
  record_class                   varchar(20) NOT NULL DEFAULT 'INTERNAL',
  sort_order                     integer,
  display_flags                  jsonb,
  change_request_id              uuid,
  created_at                     timestamptz NOT NULL DEFAULT now(),
  created_by                     varchar(80) NOT NULL,
  approved_at                    timestamptz,
  approved_by                    varchar(80),
  change_reason                  text,
  row_version                    bigint NOT NULL DEFAULT 1,
  code_system                    varchar(80),
  code_from                      varchar(15),
  code_to                        varchar(15),
  provider_type                  varchar(10),
  benefit_category               varchar(15) NOT NULL,
  CONSTRAINT cfg_benefit_category_mappings_pk PRIMARY KEY (id),
  CONSTRAINT cfg_benefit_category_mappings_bkey UNIQUE NULLS NOT DISTINCT (tenant_id, code, version),
  CONSTRAINT cfg_benefit_category_mappings_stat CHECK (record_status IN ('DRAFT', 'VALIDATED', 'SUBMITTED', 'APPROVED', 'PUBLISHED', 'EFFECTIVE', 'WITHDRAWN', 'RETIRED')),
  CONSTRAINT cfg_benefit_category_mappings_dates CHECK (effective_to IS NULL OR effective_to > effective_from),
  CONSTRAINT cfg_benefit_category_mappings_rcls CHECK (record_class IN ('PUBLIC','INTERNAL','CONFIDENTIAL','PERSONAL','HIGHLY_SENSITIVE')),
  CONSTRAINT cfg_benefit_category_mappings_kind CHECK ((code_system IS NOT NULL AND code_from IS NOT NULL AND code_to IS NOT NULL AND code_from <= code_to AND provider_type IS NULL)
                                                       OR (provider_type IS NOT NULL AND code_system IS NULL AND code_from IS NULL AND code_to IS NULL)),
  CONSTRAINT cfg_benefit_category_mappings_nolap EXCLUDE USING gist (
    (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =, code WITH =,
    daterange(effective_from, effective_to, '[)') WITH &&)
    WHERE (record_status IN ('APPROVED', 'PUBLISHED', 'EFFECTIVE'))
);
CREATE INDEX cfg_benefit_category_mappings_lookup ON health.cfg_benefit_category_mappings (tenant_id, code, effective_from);
ALTER TABLE health.cfg_benefit_category_mappings ENABLE ROW LEVEL SECURITY;
ALTER TABLE health.cfg_benefit_category_mappings FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON health.cfg_benefit_category_mappings
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
CREATE TRIGGER guard_versions BEFORE UPDATE OR DELETE ON health.cfg_benefit_category_mappings
  FOR EACH ROW EXECUTE FUNCTION sdm.guard_static_version();
COMMENT ON TABLE health.cfg_benefit_category_mappings IS 'Benefit category mappings. CFG, scope T, Tier 1. A code or code range of a code system to a benefit category, or a fallback per provider type. Added in Phase 5 (ADR-012, proposed, pending business sign-off).';
COMMENT ON COLUMN health.cfg_benefit_category_mappings.code_from IS 'First code of the range, inclusive; equal to code_to for a single code. Codes compare as text.';
COMMENT ON COLUMN health.cfg_benefit_category_mappings.provider_type IS 'Fallback row: the category for a line whose code no mapping covers, by the provider''s type.';

CREATE TABLE health.cfg_tariff_modifiers (
  id                             uuid NOT NULL DEFAULT gen_random_uuid(),
  tenant_id                      uuid NOT NULL,
  code                           varchar(120) NOT NULL,
  version                        integer NOT NULL DEFAULT 1,
  effective_from                 date NOT NULL DEFAULT CURRENT_DATE,
  effective_to                   date,
  record_status                  varchar(20) NOT NULL DEFAULT 'DRAFT',
  supersedes_id                  uuid,
  labels                         jsonb,
  description                    text,
  record_source                  varchar(60),
  record_source_version          varchar(30),
  external_mappings              jsonb,
  record_class                   varchar(20) NOT NULL DEFAULT 'INTERNAL',
  sort_order                     integer,
  display_flags                  jsonb,
  change_request_id              uuid,
  created_at                     timestamptz NOT NULL DEFAULT now(),
  created_by                     varchar(80) NOT NULL,
  approved_at                    timestamptz,
  approved_by                    varchar(80),
  change_reason                  text,
  row_version                    bigint NOT NULL DEFAULT 1,
  modifier_code                  varchar(15) NOT NULL,
  type                           varchar(20) NOT NULL,
  value                          numeric(12,4) NOT NULL,
  applies_to                     text[],
  CONSTRAINT cfg_tariff_modifiers_pk PRIMARY KEY (id),
  CONSTRAINT cfg_tariff_modifiers_bkey UNIQUE NULLS NOT DISTINCT (tenant_id, code, version),
  CONSTRAINT cfg_tariff_modifiers_stat CHECK (record_status IN ('DRAFT', 'VALIDATED', 'SUBMITTED', 'APPROVED', 'PUBLISHED', 'EFFECTIVE', 'WITHDRAWN', 'RETIRED')),
  CONSTRAINT cfg_tariff_modifiers_dates CHECK (effective_to IS NULL OR effective_to > effective_from),
  CONSTRAINT cfg_tariff_modifiers_rcls CHECK (record_class IN ('PUBLIC','INTERNAL','CONFIDENTIAL','PERSONAL','HIGHLY_SENSITIVE')),
  CONSTRAINT cfg_tariff_modifiers_ck1 CHECK (type IN ('PERCENT_UPLIFT', 'FIXED_ADD', 'MULTIPLIER')),
  CONSTRAINT cfg_tariff_modifiers_nolap EXCLUDE USING gist (
    (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =, code WITH =,
    daterange(effective_from, effective_to, '[)') WITH &&)
    WHERE (record_status IN ('APPROVED', 'PUBLISHED', 'EFFECTIVE'))
);
CREATE INDEX cfg_tariff_modifiers_lookup ON health.cfg_tariff_modifiers (tenant_id, code, effective_from);
ALTER TABLE health.cfg_tariff_modifiers ENABLE ROW LEVEL SECURITY;
ALTER TABLE health.cfg_tariff_modifiers FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON health.cfg_tariff_modifiers
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
CREATE TRIGGER guard_versions BEFORE UPDATE OR DELETE ON health.cfg_tariff_modifiers
  FOR EACH ROW EXECUTE FUNCTION sdm.guard_static_version();
COMMENT ON TABLE health.cfg_tariff_modifiers IS 'Tariff modifiers. CFG, scope T, Tier 1. What a modifier does to a tariff price. Added in Phase 5 (ADR-012, proposed, pending business sign-off).';
COMMENT ON COLUMN health.cfg_tariff_modifiers.applies_to IS 'Tariff codes or ranges (T-100-T-199) the modifier applies to; empty or null means every tariff code.';

-- Tariff resolution: contract, then network, then the plan's reference schedule (ADR-012).
ALTER TABLE health.cfg_tariff_schedules ADD COLUMN schedule_code varchar(15);
ALTER TABLE health.cfg_provider_contracts ADD COLUMN tariff_schedule_code varchar(15);
ALTER TABLE health.cfg_network_definitions ADD COLUMN tariff_schedule_code varchar(15);
ALTER TABLE health.cfg_plans_and_options ADD COLUMN reference_tariff_schedule_code varchar(15);
ALTER TABLE health.cfg_plans_and_options ADD COLUMN benefit_year varchar(15) NOT NULL DEFAULT 'CALENDAR';
ALTER TABLE health.cfg_plans_and_options ADD CONSTRAINT cfg_plans_and_options_ck1 CHECK (benefit_year IN ('CALENDAR', 'MEMBERSHIP_YEAR'));
ALTER TABLE health.mst_provider_register ADD COLUMN provider_type varchar(10);
ALTER TABLE health.mst_provider_register ADD COLUMN party_id uuid;
ALTER TABLE health.cfg_co_payment_and_deductible_rules ADD COLUMN rule_kind varchar(15) NOT NULL DEFAULT 'CO_PAYMENT';
ALTER TABLE health.cfg_co_payment_and_deductible_rules ADD CONSTRAINT cfg_co_payment_and_deductible_rules_ck3 CHECK (rule_kind IN ('CO_PAYMENT', 'DEDUCTIBLE'));
ALTER TABLE health.cfg_co_payment_and_deductible_rules ADD COLUMN cap_basis varchar(15) NOT NULL DEFAULT 'PER_LINE';
ALTER TABLE health.cfg_co_payment_and_deductible_rules ADD CONSTRAINT cfg_co_payment_and_deductible_rules_ck4 CHECK (cap_basis IN ('PER_LINE', 'PER_CLAIM', 'PER_YEAR'));
ALTER TABLE health.cfg_co_payment_and_deductible_rules ADD COLUMN plan_code varchar(15);
ALTER TABLE health.cfg_limit_definitions ADD COLUMN plan_code varchar(15);
ALTER TABLE health.cfg_health_waiting_period_rules ADD COLUMN diagnosis_codes text[];
ALTER TABLE health.cfg_health_waiting_period_rules ADD COLUMN procedure_codes text[];
ALTER TABLE product.cfg_exclusion_library ADD COLUMN diagnosis_codes text[];
ALTER TABLE product.cfg_exclusion_library ADD COLUMN procedure_codes text[];
COMMENT ON COLUMN health.cfg_tariff_schedules.schedule_code IS 'The tariff schedule this price belongs to; contracts, networks and plans reference it (ADR-012).';
COMMENT ON COLUMN health.cfg_plans_and_options.benefit_year IS 'CALENDAR (1 January) or MEMBERSHIP_YEAR (anniversary of the membership start), for annual limits and deductibles (ADR-012).';
COMMENT ON COLUMN health.mst_provider_register.party_id IS 'The provider as a party, whose verified bank account is paid (ADR-013). mst_provider_payment_details is not used for payment.';
COMMENT ON COLUMN health.cfg_co_payment_and_deductible_rules.cap_basis IS 'What the cap limits: the co-payment per line, per claim, or per member and benefit year (ADR-012).';
COMMENT ON COLUMN product.cfg_exclusion_library.diagnosis_codes IS 'Diagnosis codes or ranges (A00-A09) a claim line is excluded for (ADR-012).';

-- ---------------------------------------------------------------------
-- 2. Health transactional tables
-- ---------------------------------------------------------------------

-- Used plus reserved never exceeds the limit (HLT-007). A family pool is on the principal's membership with the
-- principal as beneficiary. A deductible is tracked without a limit (ADR-012).
ALTER TABLE health.benefit_accumulator
  ALTER COLUMN limit_amount DROP NOT NULL,
  ADD COLUMN reserved_amount numeric(18,2) NOT NULL DEFAULT 0,
  ADD COLUMN scope varchar(10) NOT NULL DEFAULT 'MEMBER',
  ADD COLUMN deductible_met numeric(18,2) NOT NULL DEFAULT 0,
  ADD COLUMN copay_taken numeric(18,2) NOT NULL DEFAULT 0,
  ADD CONSTRAINT ck_benefit_accumulator_scope CHECK (scope IN ('MEMBER', 'FAMILY')),
  ADD CONSTRAINT ck_benefit_accumulator_amounts CHECK (used_amount >= 0 AND reserved_amount >= 0 AND deductible_met >= 0 AND copay_taken >= 0
                                                       AND (limit_amount IS NULL OR used_amount + reserved_amount <= limit_amount)),
  ADD CONSTRAINT ck_benefit_accumulator_period CHECK (period_end >= period_start);
DROP INDEX health.uq_benefit_accumulator_1;
CREATE UNIQUE INDEX uq_benefit_accumulator_1 ON health.benefit_accumulator (tenant_id, membership_id, beneficiary_party_id, benefit_category, scope, period_start, period_end);

-- Waiting periods are fixed at enrolment; contributions are billed to the payer (HealthMemberEnrolled).
ALTER TABLE health.membership
  ADD COLUMN waiting_periods jsonb NOT NULL DEFAULT '[]',
  ADD COLUMN payer_party_id uuid,
  ADD COLUMN billing_account_ref uuid,
  ADD COLUMN member_category varchar(10),
  ADD COLUMN currency char(3),
  ADD CONSTRAINT ck_membership_status CHECK (status IN ('PENDING', 'ACTIVE', 'SUSPENDED', 'TERMINATED'));
ALTER TABLE health.member_dependant ADD CONSTRAINT ck_member_dependant_status CHECK (status IN ('ACTIVE', 'REMOVED'));
ALTER TABLE health.scheme
  ADD COLUMN currency char(3),
  ADD CONSTRAINT ck_scheme_status CHECK (status IN ('REGISTERED', 'ACTIVE', 'TERMINATED'));

ALTER TABLE health.preauthorisation
  ADD COLUMN service_type varchar(15),
  ADD COLUMN conditions text,
  ADD COLUMN decided_by uuid,
  ADD COLUMN decision_reason varchar(30),
  ADD CONSTRAINT ck_preauthorisation_validity CHECK (valid_to IS NULL OR valid_to >= valid_from);
-- What each authorised line holds on the accumulator, and what a claim has used of it.
ALTER TABLE health.preauth_line
  ADD COLUMN benefit_category varchar(15),
  ADD COLUMN reserved_amount numeric(18,2) NOT NULL DEFAULT 0,
  ADD COLUMN consumed_amount numeric(18,2) NOT NULL DEFAULT 0,
  ADD CONSTRAINT ck_preauth_line_amounts CHECK (authorised_amount >= 0 AND reserved_amount >= 0 AND consumed_amount >= 0);

ALTER TABLE health.medical_claim
  ADD COLUMN currency char(3) NOT NULL,
  ADD COLUMN pend_reason varchar(30),
  ADD COLUMN adjudicated_at timestamptz,
  ADD COLUMN approved_by uuid,
  ADD COLUMN approved_at timestamptz,
  ADD COLUMN payment_run_id uuid,
  ADD COLUMN reversed_at timestamptz,
  ADD CONSTRAINT ck_medical_claim_source CHECK (source IN ('PROVIDER', 'MEMBER'));
-- The adjudication of each line, in ADR-012's order.
ALTER TABLE health.medical_claim_line
  ALTER COLUMN adjustment_reason TYPE varchar(30),
  ADD COLUMN modifiers text[],
  ADD COLUMN benefit_category varchar(15),
  ADD COLUMN network_status varchar(15),
  ADD COLUMN tariff_amount numeric(18,2),
  ADD COLUMN deductible_amount numeric(18,2) NOT NULL DEFAULT 0,
  ADD COLUMN member_liability numeric(18,2) NOT NULL DEFAULT 0,
  ADD COLUMN outcome varchar(10),
  ADD COLUMN preauth_line_id uuid,
  ADD CONSTRAINT ck_medical_claim_line_outcome CHECK (outcome IS NULL OR outcome IN ('PAY', 'PEND', 'REJECT')),
  ADD CONSTRAINT ck_medical_claim_line_amounts CHECK (paid_amount >= 0 AND copay_amount >= 0 AND deductible_amount >= 0 AND paid_amount <= allowed_amount);

ALTER TABLE health.utilisation_case
  ADD COLUMN claim_id uuid,
  ADD COLUMN concluded_at timestamptz,
  ADD CONSTRAINT ck_utilisation_case_status CHECK (status IN ('OPEN', 'IN_REVIEW', 'CONCLUDED'));
ALTER TABLE health.fraud_case
  ADD COLUMN investigator_id uuid,
  ADD COLUMN concluded_at timestamptz;

-- A run is approved in Workflow; each remittance pays one provider's party into its verified bank account (ADR-013).
ALTER TABLE health.provider_payment_run
  ADD COLUMN currency char(3) NOT NULL,
  ADD COLUMN approval_id uuid,
  ADD COLUMN approved_by uuid,
  ADD COLUMN released_at timestamptz,
  ADD CONSTRAINT ck_provider_payment_run_status CHECK (status IN ('OPEN', 'CUT_OFF', 'CALCULATED', 'APPROVED', 'RELEASED', 'RECONCILED'));
ALTER TABLE health.remittance_advice
  ALTER COLUMN document_id DROP NOT NULL,
  ADD COLUMN provider_party_id uuid,
  ADD COLUMN bank_account_id uuid,
  ADD COLUMN claim_count integer NOT NULL DEFAULT 0,
  ADD COLUMN payment_id uuid,
  ADD COLUMN paid_on date,
  ADD CONSTRAINT ck_remittance_advice_amount CHECK (amount > 0);

-- ---------------------------------------------------------------------
-- 3. Life transactional tables
-- ---------------------------------------------------------------------

-- The eligibility decision the proposal was submitted with (LIF-001, LIF-002), the requested start, how it will be
-- billed, and the beneficiary nominations (BeneficiaryNomination has no table in V0001).
ALTER TABLE life.proposal
  ADD COLUMN inception_date date,
  ADD COLUMN payer_party_id uuid,
  ADD COLUMN collection_method varchar(15),
  ADD COLUMN rating_version integer,
  ADD COLUMN eligibility_decision_id uuid,
  ADD COLUMN eligibility_outcome varchar(15),
  ADD COLUMN ruleset_code varchar(30),
  ADD COLUMN ruleset_version integer,
  ADD COLUMN beneficiary_nominations jsonb NOT NULL DEFAULT '[]',
  ADD CONSTRAINT ck_proposal_status CHECK (status IN ('DRAFT', 'QUOTED', 'SUBMITTED', 'IN_UNDERWRITING', 'ACCEPTED', 'DECLINED', 'POSTPONED', 'WITHDRAWN', 'CONVERTED')),
  ADD CONSTRAINT ck_proposal_quoted CHECK (status IN ('DRAFT', 'WITHDRAWN') OR (quoted_premium IS NOT NULL AND rating_request_id IS NOT NULL));

ALTER TABLE life.underwriting_case
  ALTER COLUMN ruleset_version DROP NOT NULL,
  ADD COLUMN ruleset_code varchar(30),
  ADD COLUMN decided_by uuid,
  ADD COLUMN referred_to varchar(30),
  ADD COLUMN outcome varchar(10),
  ADD COLUMN requested_by uuid,
  ADD COLUMN approval_id uuid,
  ADD CONSTRAINT ck_underwriting_case_outcome CHECK (outcome IS NULL OR outcome IN ('ACCEPT', 'DECLINE', 'POSTPONE')),
  ADD CONSTRAINT ck_underwriting_case_decided CHECK (status <> 'DECIDED' OR (outcome IS NOT NULL AND decision_code IS NOT NULL AND decided_by IS NOT NULL));
ALTER TABLE life.underwriting_requirement
  ADD COLUMN mandatory boolean NOT NULL DEFAULT true,
  ADD COLUMN waived_by uuid,
  ADD COLUMN waiver_reason varchar(200),
  ADD CONSTRAINT ck_underwriting_requirement_status CHECK (status IN ('REQUESTED', 'RECEIVED', 'WAIVED'));

ALTER TABLE life.policy
  ADD COLUMN payer_party_id uuid,
  ADD COLUMN collection_method varchar(15),
  ADD COLUMN ruleset_version integer,
  ADD CONSTRAINT ck_policy_status CHECK (status IN ('ISSUED', 'IN_FORCE', 'LAPSED', 'TERMINATED', 'PAID_UP', 'SURRENDERED', 'CLAIMED', 'MATURED'));
CREATE UNIQUE INDEX uq_policy_proposal ON life.policy (tenant_id, proposal_id);
-- A benefit insures one of the policy's lives (claims check the insured life, LIF-008).
ALTER TABLE life.policy_benefit
  ADD COLUMN insured_life_id uuid,
  ADD CONSTRAINT ck_policy_benefit_status CHECK (status IN ('ACTIVE', 'CLAIMED', 'ENDED'));
ALTER TABLE life.beneficiary ADD CONSTRAINT ck_beneficiary_share CHECK (share_pct > 0 AND share_pct <= 100);
ALTER TABLE life.premium_schedule
  ADD COLUMN arrears_since date,
  ADD CONSTRAINT ck_premium_schedule_missed CHECK (missed_count >= 0);
ALTER TABLE life.lapse_reinstatement ADD CONSTRAINT ck_lapse_reinstatement_type CHECK (event_type IN ('LAPSE', 'REINSTATE', 'TERMINATE'));

-- A notified claim has no number yet; the Claims Framework allocates it at registration, as for General.
ALTER TABLE life.claim
  ALTER COLUMN claim_number DROP NOT NULL,
  ADD COLUMN insured_life_id uuid,
  ADD COLUMN currency char(3),
  ADD COLUMN notified_at timestamptz,
  ADD COLUMN decision_rationale text,
  ADD COLUMN decided_by uuid,
  ADD COLUMN decided_at timestamptz,
  ADD COLUMN approval_id uuid,
  ADD COLUMN requested_by uuid,
  ADD COLUMN paid_amount numeric(18,2) NOT NULL DEFAULT 0,
  ADD COLUMN reserve_type varchar(15),
  ADD CONSTRAINT ck_claim_status CHECK (status IN ('NOTIFIED', 'REGISTERED', 'IN_ASSESSMENT', 'APPROVED', 'DECLINED', 'SETTLED', 'CLOSED')),
  ADD CONSTRAINT ck_claim_number CHECK (status = 'NOTIFIED' OR claim_number IS NOT NULL),
  ADD CONSTRAINT ck_claim_decided CHECK (status NOT IN ('APPROVED', 'DECLINED', 'SETTLED') OR (decision_reason IS NOT NULL AND decision_rationale IS NOT NULL));

-- Who is paid, into which verified account (ADR-013), and how much: beneficiaries by share, or the claimant.
CREATE TABLE life.claim_payee (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  claim_id                   uuid NOT NULL,
  party_id                   uuid NOT NULL,
  bank_account_id            uuid NOT NULL,
  bank_account_hash          varchar(64) NOT NULL,
  share_pct                  numeric(5,2) NOT NULL,
  amount                     numeric(18,2) NOT NULL,
  CONSTRAINT pk_claim_payee PRIMARY KEY (tenant_id, id),
  CONSTRAINT fk_claim_payee_claim_id FOREIGN KEY (tenant_id, claim_id) REFERENCES life.claim (tenant_id, id),
  CONSTRAINT uq_claim_payee UNIQUE (tenant_id, claim_id, party_id),
  CONSTRAINT ck_claim_payee_amounts CHECK (share_pct > 0 AND share_pct <= 100 AND amount > 0)
);
CREATE INDEX ix_claim_payee_1 ON life.claim_payee (tenant_id, claim_id);
ALTER TABLE life.claim_payee ENABLE ROW LEVEL SECURITY;
ALTER TABLE life.claim_payee FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON life.claim_payee
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
-- Grants: db/roles/module_grants.sql gives insys_life its schema's tables.

ALTER TABLE life.policy_loan ADD CONSTRAINT ck_policy_loan CHECK (status IN ('ACTIVE', 'REPAID', 'OFFSET') AND principal > 0 AND balance >= 0);
ALTER TABLE life.policy_loan_transaction ADD CONSTRAINT ck_policy_loan_transaction_type CHECK (txn_type IN ('ADVANCE', 'INTEREST', 'REPAYMENT', 'OFFSET'));

ALTER TABLE life.group_scheme
  ADD COLUMN start_date date,
  ADD COLUMN scheme_type varchar(15),
  ADD COLUMN currency char(3),
  ADD CONSTRAINT ck_group_scheme_status CHECK (status IN ('SET_UP', 'ACTIVE', 'TERMINATED'));
ALTER TABLE life.group_member
  ADD COLUMN evidence_status varchar(15) NOT NULL DEFAULT 'NOT_REQUIRED',
  ADD COLUMN free_cover_limit numeric(18,2),
  ADD CONSTRAINT ck_group_member_evidence CHECK (evidence_status IN ('NOT_REQUIRED', 'REQUIRED', 'ACCEPTED')),
  ADD CONSTRAINT ck_group_member_dates CHECK (left_on IS NULL OR left_on >= joined_on);
CREATE UNIQUE INDEX uq_group_member_current ON life.group_member (tenant_id, scheme_id, party_id) WHERE left_on IS NULL;
