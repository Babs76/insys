/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Migrations
 * CONFIDENTIALITY AND INTELLECTUAL PROPERTY NOTICE
 * This source code and its associated documentation contain confidential and
 * proprietary information belonging to OpenFactor Technology Group or its
 * applicable intellectual property rights holder.
 * This material is provided solely for authorised development, maintenance,
 * operation or evaluation purposes, as applicable. No person may reproduce,
 * modify, distribute, disclose, publish or otherwise use this material except
 * as expressly permitted by an applicable written agreement or licence.
 * No ownership rights or licence to any intellectual property are granted
 * by the inclusion of this notice, except as expressly provided in an
 * applicable written agreement or licence.
 * Third-party software, open-source components and customer-owned materials
 * remain subject to their respective ownership rights and applicable licences
 * or agreements.
 * UNAUTHORISED ACCESS, USE, COPYING OR DISCLOSURE IS PROHIBITED.
 * ============================================================================
 */
-- =====================================================================
-- V0111: Wave 6 batch 6.1 (addenda/finance-and-compliance.md §3, §4.2; F094; OQ-058, OQ-156). A rate naming a product applies to its invoices. The tax is
-- taken out of the premium, which already includes it, so what a customer pays doesn't change. A return totals what was posted to the tax code's account.
-- =====================================================================

ALTER TABLE rating.cfg_premium_tax_and_levy_rates ADD COLUMN product_code varchar(15);
COMMENT ON COLUMN rating.cfg_premium_tax_and_levy_rates.product_code IS 'The product whose invoices carry this tax (OQ-156). With none, the rate is for rating only.';

ALTER TABLE billing.invoice_line ADD COLUMN tax_amount numeric(18,2) NOT NULL DEFAULT 0;
ALTER TABLE billing.invoice ADD COLUMN credited_tax numeric(18,2) NOT NULL DEFAULT 0;

CREATE TABLE finance.tax_return (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  return_number              varchar(25) NOT NULL,
  tax_code                   varchar(15) NOT NULL,
  account_code               varchar(80) NOT NULL,
  currency                   char(3) NOT NULL,
  period_start               date NOT NULL,
  period_end                 date NOT NULL,
  output_total               numeric(18,2) NOT NULL,
  input_total                numeric(18,2) NOT NULL,
  status                     varchar(10) NOT NULL,
  prepared_by                uuid NOT NULL,
  decided_by                 uuid,
  decided_at                 timestamptz,
  reject_reason              varchar(500),
  CONSTRAINT pk_tax_return PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_tax_return_number UNIQUE (tenant_id, return_number),
  CONSTRAINT ck_tax_return_status CHECK (status IN ('PREPARED', 'APPROVED', 'REJECTED')),
  CONSTRAINT ck_tax_return_period CHECK (period_end >= period_start)
);
CREATE UNIQUE INDEX uq_tax_return_period ON finance.tax_return (tenant_id, tax_code, currency, period_start, period_end) WHERE status <> 'REJECTED';
ALTER TABLE finance.tax_return ENABLE ROW LEVEL SECURITY;
ALTER TABLE finance.tax_return FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON finance.tax_return USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
