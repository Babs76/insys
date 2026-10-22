-- =====================================================================
-- V0021: Phase 6 (Distribution and Commission, Reinsurance, Compliance and Reporting, Analytics, Integration).
-- The defaults proposed in OQ-062 to OQ-066 (accepted to build, pending business sign-off), as configuration.
-- =====================================================================

-- ---------------------------------------------------------------------
-- Commission (OQ-062)
-- ---------------------------------------------------------------------

-- Which plan applies: intermediary × product → plan (OQ-062 item 1).
ALTER TABLE commission.cfg_intermediary_product_permissions ADD COLUMN plan_code varchar(15);
COMMENT ON COLUMN commission.cfg_intermediary_product_permissions.plan_code IS
  'The commission plan (cfg_commission_plans.plan_code) the intermediary earns on the product; none when empty (OQ-062).';

-- The payee: statements are paid to the party's verified bank account (OQ-062 item 7, ADR-013).
ALTER TABLE commission.mst_intermediary_register ADD COLUMN party_id uuid;
COMMENT ON COLUMN commission.mst_intermediary_register.party_id IS 'The intermediary''s party (party.party), paid through its verified bank account (OQ-062).';

-- The clawback rule as a code (OQ-062 item 5).
ALTER TABLE commission.cfg_commission_plans ADD CONSTRAINT cfg_commission_plans_ck2 CHECK (clawback_rule IN ('NONE', 'UNEARNED'));
COMMENT ON COLUMN commission.cfg_commission_plans.clawback_rule IS
  'NONE, or UNEARNED (default when empty): entitlements on invoices due on or after the cancellation or lapse date are clawed back in full (OQ-062).';

-- An entitlement keeps what the clawback rule needs: the product and the invoice's due date.
ALTER TABLE commission.commission_entitlement
  ADD COLUMN product_code varchar(15),
  ADD COLUMN source_domain varchar(10),
  ADD COLUMN due_date date,
  ADD CONSTRAINT ck_commission_entitlement_status CHECK (status IN ('ACCRUED', 'PAYABLE', 'PAID', 'CLAWED_BACK')),
  ADD CONSTRAINT ck_commission_entitlement_amounts CHECK (amount >= 0 AND rate >= 0);
CREATE UNIQUE INDEX uq_commission_entitlement_source ON commission.commission_entitlement (tenant_id, source_type, source_id, intermediary_code);

-- A clawback is recovered on a statement.
ALTER TABLE commission.clawback
  ADD COLUMN statement_id uuid,
  ADD CONSTRAINT fk_clawback_statement_id FOREIGN KEY (tenant_id, statement_id) REFERENCES commission.commission_statement (tenant_id, id),
  ADD CONSTRAINT uq_clawback_entitlement UNIQUE (tenant_id, entitlement_id),
  ADD CONSTRAINT ck_clawback_amount CHECK (amount > 0);

ALTER TABLE commission.commission_statement
  ADD COLUMN currency char(3),
  ADD COLUMN submitted_at timestamptz,
  ADD COLUMN approval_id uuid,
  ADD COLUMN approved_by uuid,
  ADD COLUMN payee_party_id uuid,
  ADD COLUMN bank_account_id uuid,
  ADD COLUMN bank_account_hash varchar(64),
  ADD COLUMN paid_on date,
  ADD CONSTRAINT ck_commission_statement_status CHECK (status IN ('DRAFT', 'APPROVED', 'PAID')),
  ADD CONSTRAINT ck_commission_statement_amounts CHECK (net_amount = gross_amount - tax_amount);

-- ---------------------------------------------------------------------
-- Reinsurance (OQ-063)
-- ---------------------------------------------------------------------

-- A cession is reproducible from what it was calculated on (REI-002) and linked to its policy (REI-001).
ALTER TABLE reinsurance.cession
  ADD COLUMN product_code varchar(15),
  ADD COLUMN treaty_version integer,
  ADD COLUMN arrangement_code varchar(15),
  ADD COLUMN gross_sum_insured numeric(20,2),
  ADD COLUMN gross_premium numeric(18,2),
  ADD COLUMN ceded_share numeric(9,6),
  ADD COLUMN supersedes_id uuid,
  ADD CONSTRAINT ck_cession_amounts CHECK (ceded_sum_insured >= 0 AND ceded_premium >= 0);

ALTER TABLE reinsurance.recovery
  ADD COLUMN source_domain varchar(10),
  ADD COLUMN claim_id uuid,
  ADD COLUMN policy_ref uuid,
  ADD COLUMN cession_id uuid,
  ADD COLUMN paid_amount numeric(18,2),
  ADD COLUMN billed_on date,
  ADD COLUMN settled_on date,
  ADD CONSTRAINT ck_recovery_status CHECK (status IN ('IDENTIFIED', 'BILLED', 'SETTLED')),
  ADD CONSTRAINT ck_recovery_amounts CHECK (recoverable > 0 AND (settled_amount IS NULL OR settled_amount >= 0));
CREATE UNIQUE INDEX uq_recovery_claim_treaty ON reinsurance.recovery (tenant_id, claim_number, treaty_code);

-- The bordereau's rows, as CSV in the layout's field order (OQ-063 item 5).
ALTER TABLE reinsurance.bordereau
  ADD COLUMN content text,
  ADD COLUMN partner_ref varchar(80),
  ADD CONSTRAINT ck_bordereau_status CHECK (status IN ('PREPARED', 'SENT', 'AGREED'));

-- ---------------------------------------------------------------------
-- Compliance and reporting (OQ-064)
-- ---------------------------------------------------------------------
ALTER TABLE reporting.regulatory_return_run
  ADD COLUMN field_values jsonb,
  ADD COLUMN prepared_by uuid,
  ADD COLUMN reviewed_by uuid,
  ADD COLUMN regulator_ref varchar(80),
  ADD COLUMN rejection_reason text,
  ADD CONSTRAINT ck_regulatory_return_run_status CHECK (status IN ('PREPARED', 'REVIEWED', 'APPROVED', 'SUBMITTED', 'ACCEPTED', 'REJECTED'));

ALTER TABLE reporting.report_run
  ADD COLUMN result jsonb,
  ADD CONSTRAINT ck_report_run_status CHECK (status IN ('COMPLETED', 'FAILED'));

-- ---------------------------------------------------------------------
-- Analytics (OQ-065): the events projections are rebuilt from.
-- ---------------------------------------------------------------------
CREATE TABLE analytics.event_log (
  tenant_id                  uuid NOT NULL,
  event_id                   uuid NOT NULL,
  event_type                 varchar(80) NOT NULL,
  occurred_at                timestamptz NOT NULL,
  received_at                timestamptz NOT NULL,
  payload                    jsonb NOT NULL,
  CONSTRAINT pk_event_log PRIMARY KEY (tenant_id, event_id)
);
CREATE INDEX ix_event_log_1 ON analytics.event_log (tenant_id, occurred_at, event_id);
ALTER TABLE analytics.event_log ENABLE ROW LEVEL SECURITY;
ALTER TABLE analytics.event_log FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON analytics.event_log
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
COMMENT ON TABLE analytics.event_log IS 'The events Analytics consumed, minimal payloads only (INT-007); projections are rebuilt by replaying them (OQ-065).';
-- Grants: db/roles/module_grants.sql gives insys_analytics its schema's tables.

-- ---------------------------------------------------------------------
-- Integration (OQ-066): a webhook delivery keeps what it sends, so a retry sends the same.
-- ---------------------------------------------------------------------
ALTER TABLE integration.webhook_delivery
  ADD COLUMN event_type varchar(80),
  ADD COLUMN payload jsonb,
  ADD CONSTRAINT ck_webhook_delivery_status CHECK (status IN ('PENDING', 'DELIVERED', 'FAILED')),
  ADD CONSTRAINT uq_webhook_delivery UNIQUE (tenant_id, subscription_id, event_id);

-- ---------------------------------------------------------------------
-- General renewal invitations (GEN-004, OQ-066 item 3)
-- ---------------------------------------------------------------------
ALTER TABLE general.policy ADD COLUMN renewal_invited_on date;
COMMENT ON COLUMN general.policy.renewal_invited_on IS 'When the renewal invitation was sent (general.cfg_renewal_rules.notice_days before the period end).';
