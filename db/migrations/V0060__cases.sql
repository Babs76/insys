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
-- V0060: cases: enquiries, complaints, disputes and appeals (Wave 2, addenda/cases.md, signed off 25 October 2026).
-- A case (workflow.service_case) holds what it is about; the Workflow process its case type names holds where it is.
-- Links to the records it is about are identifiers only (ADR-001). Every timeline is tenant configuration; the platform
-- seeds none. The nine PPR complaint categories (Rule 18.5.1) ship as global reference rows; ombud schemes do not
-- (their contact details are the tenant's or the platform's to enter).
-- Decisions: OQ-101 (appeal route per case type), OQ-104 (redress paid through Payments with maker-checker), OQ-106
-- (kept 5 years after closure, configurable; a legal hold overrides), OQ-108 (a broker sees the cases it raised, or
-- those staff share with it).
-- =====================================================================

-- Case numbers: one counter per tenant.
CREATE TABLE workflow.case_number_counter (
  tenant_id                  uuid NOT NULL,
  last_number                bigint NOT NULL,
  CONSTRAINT pk_case_number_counter PRIMARY KEY (tenant_id)
);

CREATE TABLE workflow.service_case (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  case_number                varchar(25) NOT NULL,
  case_type_code             varchar(30) NOT NULL,
  family                     varchar(12) NOT NULL,
  status                     varchar(22) NOT NULL,
  outcome                    varchar(14),
  received_at                timestamptz NOT NULL,
  received_channel           varchar(20) NOT NULL,
  complainant_party_id       uuid,
  complainant_name           varchar(200),
  complainant_contact        varchar(200),
  on_behalf_of_party_id      uuid,
  raised_by_user_id          uuid,
  raised_by_audience         varchar(10) NOT NULL,
  raised_by_intermediary_code varchar(30),
  raised_by_provider_id      varchar(40),
  shared_with_broker         boolean NOT NULL DEFAULT false,
  category_code              varchar(30),
  summary                    text NOT NULL,
  upheld_at_receipt          boolean NOT NULL DEFAULT false,
  reportable                 boolean,
  decision_reasons           text,
  decided_by                 uuid,
  decided_at                 timestamptz,
  parent_case_id             uuid,
  process_instance_id        uuid,
  product_code               varchar(15),
  org_node_code              varchar(15),
  acknowledged_at            timestamptz,
  expected_by                timestamptz,
  closed_at                  timestamptz,
  retain_until               date,
  legal_hold                 boolean NOT NULL DEFAULT false,
  ombud_scheme_code          varchar(20),
  ombud_reference            varchar(60),
  ombud_referred_at          timestamptz,
  ombud_outcome              varchar(14),
  ombud_outcome_at           timestamptz,
  CONSTRAINT pk_service_case PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_service_case_number UNIQUE (tenant_id, case_number),
  CONSTRAINT ck_service_case_family CHECK (family IN ('ENQUIRY', 'COMPLAINT', 'DISPUTE', 'APPEAL')),
  CONSTRAINT ck_service_case_status CHECK (status IN ('RECEIVED', 'ACKNOWLEDGED', 'IN_PROGRESS', 'AWAITING_COMPLAINANT', 'DECIDED', 'CLOSED', 'WITHDRAWN', 'REOPENED')),
  CONSTRAINT ck_service_case_outcome CHECK (outcome IS NULL OR outcome IN ('UPHELD', 'PARTLY_UPHELD', 'REJECTED', 'WITHDRAWN', 'ANSWERED')),
  CONSTRAINT ck_service_case_audience CHECK (raised_by_audience IN ('STAFF', 'CUSTOMER', 'BROKER', 'PROVIDER')),
  CONSTRAINT ck_service_case_complainant CHECK (complainant_party_id IS NOT NULL OR complainant_name IS NOT NULL),
  CONSTRAINT ck_service_case_decided CHECK ((outcome IS NULL) OR (decided_at IS NOT NULL)),
  CONSTRAINT ck_service_case_ombud_outcome CHECK (ombud_outcome IS NULL OR ombud_outcome IN ('UPHELD', 'PARTLY_UPHELD', 'REJECTED', 'SETTLED', 'DISMISSED'))
);
CREATE INDEX ix_service_case_1 ON workflow.service_case (tenant_id, family, status);
CREATE INDEX ix_service_case_2 ON workflow.service_case (tenant_id, complainant_party_id);
CREATE INDEX ix_service_case_3 ON workflow.service_case (tenant_id, raised_by_intermediary_code);
CREATE INDEX ix_service_case_4 ON workflow.service_case (tenant_id, retain_until) WHERE retain_until IS NOT NULL;
COMMENT ON COLUMN workflow.service_case.summary IS 'Complaint text: may hold health or identity information (POPIA s26-s33). Never logged or put in events.';
COMMENT ON COLUMN workflow.service_case.shared_with_broker IS 'OQ-108: staff marked the case visible to the broker of its linked records, which did not raise it.';
COMMENT ON COLUMN workflow.service_case.retain_until IS 'OQ-106: set at closure from workflow.cfg_case_retention (5 years when none is in force); the case is disposed of after it unless legal_hold.';

CREATE TABLE workflow.case_link (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  case_id                    uuid NOT NULL,
  link_type                  varchar(20) NOT NULL,
  record_id                  uuid NOT NULL,
  reference                  varchar(60) NOT NULL,
  added_at                   timestamptz NOT NULL,
  added_by                   varchar(80) NOT NULL,
  CONSTRAINT pk_case_link PRIMARY KEY (tenant_id, id),
  CONSTRAINT fk_case_link_case FOREIGN KEY (tenant_id, case_id) REFERENCES workflow.service_case (tenant_id, id) ON DELETE CASCADE,
  CONSTRAINT uq_case_link UNIQUE (tenant_id, case_id, link_type, record_id),
  CONSTRAINT ck_case_link_type CHECK (link_type IN ('POLICY', 'CLAIM', 'MEMBERSHIP', 'PAYMENT', 'INVOICE', 'BILLING_ACCOUNT', 'PARTY', 'PROVIDER', 'REMITTANCE', 'PAYMENT_RUN', 'INTERMEDIARY'))
);
CREATE INDEX ix_case_link_1 ON workflow.case_link (tenant_id, link_type, record_id);

CREATE TABLE workflow.case_event (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  case_id                    uuid NOT NULL,
  event_type                 varchar(30) NOT NULL,
  occurred_at                timestamptz NOT NULL,
  actor                      varchar(80) NOT NULL,
  detail                     varchar(2000),
  CONSTRAINT pk_case_event PRIMARY KEY (tenant_id, id),
  CONSTRAINT fk_case_event_case FOREIGN KEY (tenant_id, case_id) REFERENCES workflow.service_case (tenant_id, id) ON DELETE CASCADE,
  CONSTRAINT ck_case_event_type CHECK (event_type IN ('RECEIVED', 'ACKNOWLEDGED', 'ASSIGNED', 'UPDATED', 'LINKED', 'MESSAGE_SENT', 'MESSAGE_RECEIVED',
    'EXTENDED', 'ESCALATED', 'AWAITING_COMPLAINANT', 'RESUMED', 'DECIDED', 'REDRESS_REQUESTED', 'REDRESS_APPROVED', 'REDRESS_REJECTED', 'REDRESS_PAID',
    'REFERRED_TO_OMBUD', 'OMBUD_OUTCOME', 'SHARED_WITH_BROKER', 'REOPENED', 'WITHDRAWN', 'CLOSED', 'LEGAL_HOLD_PLACED', 'LEGAL_HOLD_RELEASED'))
);
CREATE INDEX ix_case_event_1 ON workflow.case_event (tenant_id, case_id, occurred_at);

CREATE TABLE workflow.case_message (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  case_id                    uuid NOT NULL,
  direction                  varchar(3) NOT NULL,
  author_audience            varchar(10) NOT NULL,
  author_id                  uuid,
  body                       text NOT NULL,
  sent_at                    timestamptz NOT NULL,
  CONSTRAINT pk_case_message PRIMARY KEY (tenant_id, id),
  CONSTRAINT fk_case_message_case FOREIGN KEY (tenant_id, case_id) REFERENCES workflow.service_case (tenant_id, id) ON DELETE CASCADE,
  CONSTRAINT ck_case_message_direction CHECK (direction IN ('IN', 'OUT')),
  CONSTRAINT ck_case_message_audience CHECK (author_audience IN ('STAFF', 'CUSTOMER', 'BROKER', 'PROVIDER'))
);
CREATE INDEX ix_case_message_1 ON workflow.case_message (tenant_id, case_id, sent_at);
COMMENT ON TABLE workflow.case_message IS 'Messages between the complainant (or broker, provider) and staff on a case: what the portal shows. Internal notes stay in workflow.case_note.';

CREATE TABLE workflow.case_redress (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  case_id                    uuid NOT NULL,
  redress_type               varchar(14) NOT NULL,
  amount                     numeric(18,2) NOT NULL,
  currency                   char(3) NOT NULL,
  payee_party_id             uuid NOT NULL,
  bank_account_id            uuid,
  status                     varchar(12) NOT NULL,
  requested_by               uuid NOT NULL,
  requested_at               timestamptz NOT NULL,
  decided_by                 uuid,
  decided_at                 timestamptz,
  paid_at                    timestamptz,
  payment_id                 uuid,
  CONSTRAINT pk_case_redress PRIMARY KEY (tenant_id, id),
  CONSTRAINT fk_case_redress_case FOREIGN KEY (tenant_id, case_id) REFERENCES workflow.service_case (tenant_id, id) ON DELETE CASCADE,
  CONSTRAINT ck_case_redress_type CHECK (redress_type IN ('COMPENSATION', 'GOODWILL')),
  CONSTRAINT ck_case_redress_status CHECK (status IN ('REQUESTED', 'APPROVED', 'REJECTED', 'PAID')),
  CONSTRAINT ck_case_redress_amount CHECK (amount > 0)
);
CREATE INDEX ix_case_redress_1 ON workflow.case_redress (tenant_id, case_id);
COMMENT ON TABLE workflow.case_redress IS 'OQ-104: compensation and goodwill on a case (PPR 18.8.3), approved by a second person (approval type CASE_REDRESS) and paid through Payments.';

ALTER TABLE workflow.case_number_counter ENABLE ROW LEVEL SECURITY;
ALTER TABLE workflow.case_number_counter FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON workflow.case_number_counter
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE workflow.service_case ENABLE ROW LEVEL SECURITY;
ALTER TABLE workflow.service_case FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON workflow.service_case
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE workflow.case_link ENABLE ROW LEVEL SECURITY;
ALTER TABLE workflow.case_link FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON workflow.case_link
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE workflow.case_event ENABLE ROW LEVEL SECURITY;
ALTER TABLE workflow.case_event FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON workflow.case_event
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE workflow.case_message ENABLE ROW LEVEL SECURITY;
ALTER TABLE workflow.case_message FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON workflow.case_message
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
ALTER TABLE workflow.case_redress ENABLE ROW LEVEL SECURITY;
ALTER TABLE workflow.case_redress FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON workflow.case_redress
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);

-- Workflow and Case Management · Cases · Case types
CREATE TABLE workflow.cfg_case_types (
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
  case_type_code                 varchar(30) NOT NULL,
  family                         varchar(12) NOT NULL,
  process_code                   varchar(30) NOT NULL,
  default_queue_code             varchar(30),
  link_types                     text[] NOT NULL,
  portal_audiences               text[],
  notification_types             text[],
  requires_category              boolean NOT NULL DEFAULT false,
  review_case_type_code          varchar(30),
  appeal_route                   varchar(10) NOT NULL,
  ombud_scheme_codes             text[],
  CONSTRAINT cfg_case_types_pk PRIMARY KEY (id),
  CONSTRAINT cfg_case_types_bkey UNIQUE NULLS NOT DISTINCT (tenant_id, code, version),
  CONSTRAINT cfg_case_types_stat CHECK (record_status IN ('DRAFT', 'VALIDATED', 'SUBMITTED', 'APPROVED', 'PUBLISHED', 'EFFECTIVE', 'WITHDRAWN', 'RETIRED')),
  CONSTRAINT cfg_case_types_dates CHECK (effective_to IS NULL OR effective_to > effective_from),
  CONSTRAINT cfg_case_types_rcls CHECK (record_class IN ('PUBLIC','INTERNAL','CONFIDENTIAL','PERSONAL','HIGHLY_SENSITIVE')),
  CONSTRAINT cfg_case_types_ck1 CHECK (family IN ('ENQUIRY', 'COMPLAINT', 'DISPUTE', 'APPEAL')),
  CONSTRAINT cfg_case_types_ck2 CHECK (link_types <@ ARRAY['POLICY', 'CLAIM', 'MEMBERSHIP', 'PAYMENT', 'INVOICE', 'BILLING_ACCOUNT', 'PARTY', 'PROVIDER', 'REMITTANCE', 'PAYMENT_RUN', 'INTERMEDIARY']::text[]),
  CONSTRAINT cfg_case_types_ck3 CHECK (portal_audiences <@ ARRAY['CUSTOMER', 'BROKER', 'PROVIDER']::text[]),
  CONSTRAINT cfg_case_types_ck4 CHECK (appeal_route IN ('PPR_NFO', 'CMS')),
  CONSTRAINT cfg_case_types_nolap EXCLUDE USING gist (
    (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =, code WITH =,
    daterange(effective_from, effective_to, '[)') WITH &&)
    WHERE (record_status IN ('APPROVED', 'PUBLISHED', 'EFFECTIVE'))
);
CREATE INDEX cfg_case_types_lookup ON workflow.cfg_case_types (tenant_id, code, effective_from);
ALTER TABLE workflow.cfg_case_types ENABLE ROW LEVEL SECURITY;
ALTER TABLE workflow.cfg_case_types FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON workflow.cfg_case_types
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
CREATE TRIGGER guard_versions BEFORE UPDATE OR DELETE ON workflow.cfg_case_types
  FOR EACH ROW EXECUTE FUNCTION sdm.guard_static_version();
COMMENT ON TABLE workflow.cfg_case_types IS 'Case types. CFG, scope T, Tier 1. A kind of case: its family, the Workflow process that runs it, the records it may link, who may raise it in a portal, its review (appeal) type and its appeal route (OQ-101). A complaint type names a review type (PPR 18.6).';

-- Workflow and Case Management · Cases · Case timelines
CREATE TABLE workflow.cfg_case_timelines (
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
  case_type_code                 varchar(30) NOT NULL,
  step                           varchar(30) NOT NULL,
  duration                       varchar(20) NOT NULL,
  basis                          varchar(10) NOT NULL,
  calendar_code                  varchar(30),
  extension_allowed              boolean NOT NULL DEFAULT false,
  max_extension                  varchar(20),
  CONSTRAINT cfg_case_timelines_pk PRIMARY KEY (id),
  CONSTRAINT cfg_case_timelines_bkey UNIQUE NULLS NOT DISTINCT (tenant_id, code, version),
  CONSTRAINT cfg_case_timelines_stat CHECK (record_status IN ('DRAFT', 'VALIDATED', 'SUBMITTED', 'APPROVED', 'PUBLISHED', 'EFFECTIVE', 'WITHDRAWN', 'RETIRED')),
  CONSTRAINT cfg_case_timelines_dates CHECK (effective_to IS NULL OR effective_to > effective_from),
  CONSTRAINT cfg_case_timelines_rcls CHECK (record_class IN ('PUBLIC','INTERNAL','CONFIDENTIAL','PERSONAL','HIGHLY_SENSITIVE')),
  CONSTRAINT cfg_case_timelines_ck1 CHECK (step IN ('ACKNOWLEDGE', 'RESOLVE', 'DECIDE_REPRESENTATIONS', 'NOTIFY_DECISION', 'REVIEW', 'REDRESS')),
  CONSTRAINT cfg_case_timelines_ck2 CHECK (basis IN ('BUSINESS', 'CALENDAR')),
  CONSTRAINT cfg_case_timelines_nolap EXCLUDE USING gist (
    (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =, code WITH =,
    daterange(effective_from, effective_to, '[)') WITH &&)
    WHERE (record_status IN ('APPROVED', 'PUBLISHED', 'EFFECTIVE'))
);
CREATE INDEX cfg_case_timelines_lookup ON workflow.cfg_case_timelines (tenant_id, code, effective_from);
ALTER TABLE workflow.cfg_case_timelines ENABLE ROW LEVEL SECURITY;
ALTER TABLE workflow.cfg_case_timelines FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON workflow.cfg_case_timelines
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
CREATE TRIGGER guard_versions BEFORE UPDATE OR DELETE ON workflow.cfg_case_timelines
  FOR EACH ROW EXECUTE FUNCTION sdm.guard_static_version();
COMMENT ON TABLE workflow.cfg_case_timelines IS 'Case timelines. CFG, scope T, Tier 1. A step of a case type and how long it may take (ISO 8601), on business or calendar days; set by compliance, none seeded.';

-- Workflow and Case Management · Cases · Case categories
CREATE TABLE workflow.ref_case_categories (
  id                             uuid NOT NULL DEFAULT gen_random_uuid(),
  tenant_id                      uuid,
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
  category_code                  varchar(30) NOT NULL,
  family                         varchar(12) NOT NULL,
  ppr_category                   varchar(30) NOT NULL,
  name                           varchar(200) NOT NULL,
  CONSTRAINT ref_case_categories_pk PRIMARY KEY (id),
  CONSTRAINT ref_case_categories_bkey UNIQUE NULLS NOT DISTINCT (tenant_id, code, version),
  CONSTRAINT ref_case_categories_stat CHECK (record_status IN ('DRAFT', 'PENDING_APPROVAL', 'APPROVED', 'ACTIVE', 'DEPRECATED', 'RETIRED')),
  CONSTRAINT ref_case_categories_dates CHECK (effective_to IS NULL OR effective_to > effective_from),
  CONSTRAINT ref_case_categories_rcls CHECK (record_class IN ('PUBLIC','INTERNAL','CONFIDENTIAL','PERSONAL','HIGHLY_SENSITIVE')),
  CONSTRAINT ref_case_categories_ck1 CHECK (family IN ('ENQUIRY', 'COMPLAINT', 'DISPUTE', 'APPEAL')),
  CONSTRAINT ref_case_categories_ck2 CHECK (ppr_category IN ('POLICY_DESIGN', 'INFORMATION_PROVIDED', 'ADVICE', 'POLICY_PERFORMANCE', 'SERVICE', 'ACCESSIBILITY_CHANGES', 'COMPLAINTS_HANDLING', 'CLAIMS', 'OTHER')),
  CONSTRAINT ref_case_categories_nolap EXCLUDE USING gist (
    (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =, code WITH =,
    daterange(effective_from, effective_to, '[)') WITH &&)
    WHERE (record_status IN ('APPROVED', 'ACTIVE'))
);
CREATE INDEX ref_case_categories_lookup ON workflow.ref_case_categories (tenant_id, code, effective_from);
ALTER TABLE workflow.ref_case_categories ENABLE ROW LEVEL SECURITY;
ALTER TABLE workflow.ref_case_categories FORCE ROW LEVEL SECURITY;
CREATE POLICY read_global_and_own ON workflow.ref_case_categories FOR SELECT
  USING (tenant_id IS NULL OR tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
CREATE POLICY tenant_insert ON workflow.ref_case_categories FOR INSERT WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
CREATE POLICY tenant_update ON workflow.ref_case_categories FOR UPDATE USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
CREATE POLICY tenant_delete ON workflow.ref_case_categories FOR DELETE USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
CREATE POLICY platform_baseline ON workflow.ref_case_categories TO insys_platform_admin
  USING (tenant_id IS NULL) WITH CHECK (tenant_id IS NULL);
CREATE TRIGGER guard_versions BEFORE UPDATE OR DELETE ON workflow.ref_case_categories
  FOR EACH ROW EXECUTE FUNCTION sdm.guard_static_version();
COMMENT ON TABLE workflow.ref_case_categories IS 'Case categories. REF, scope GT, Tier 2. Complaint and case categories, each under one of the nine PPR categories (Rule 18.5); the nine ship as global rows.';

-- Workflow and Case Management · Cases · Case reportability
CREATE TABLE workflow.cfg_case_reportability (
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
  case_type_code                 varchar(30) NOT NULL,
  query_window                   varchar(20) NOT NULL,
  CONSTRAINT cfg_case_reportability_pk PRIMARY KEY (id),
  CONSTRAINT cfg_case_reportability_bkey UNIQUE NULLS NOT DISTINCT (tenant_id, code, version),
  CONSTRAINT cfg_case_reportability_stat CHECK (record_status IN ('DRAFT', 'VALIDATED', 'SUBMITTED', 'APPROVED', 'PUBLISHED', 'EFFECTIVE', 'WITHDRAWN', 'RETIRED')),
  CONSTRAINT cfg_case_reportability_dates CHECK (effective_to IS NULL OR effective_to > effective_from),
  CONSTRAINT cfg_case_reportability_rcls CHECK (record_class IN ('PUBLIC','INTERNAL','CONFIDENTIAL','PERSONAL','HIGHLY_SENSITIVE')),
  CONSTRAINT cfg_case_reportability_nolap EXCLUDE USING gist (
    (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =, code WITH =,
    daterange(effective_from, effective_to, '[)') WITH &&)
    WHERE (record_status IN ('APPROVED', 'PUBLISHED', 'EFFECTIVE'))
);
CREATE INDEX cfg_case_reportability_lookup ON workflow.cfg_case_reportability (tenant_id, code, effective_from);
ALTER TABLE workflow.cfg_case_reportability ENABLE ROW LEVEL SECURITY;
ALTER TABLE workflow.cfg_case_reportability FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON workflow.cfg_case_reportability
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
CREATE TRIGGER guard_versions BEFORE UPDATE OR DELETE ON workflow.cfg_case_reportability
  FOR EACH ROW EXECUTE FUNCTION sdm.guard_static_version();
COMMENT ON TABLE workflow.cfg_case_reportability IS 'Case reportability. CFG, scope T, Tier 1. The window (ISO 8601, business days) within which a complaint upheld through the ordinary query process is not reportable (PPR 18.1).';

-- Workflow and Case Management · Cases · Ombud schemes
CREATE TABLE workflow.ref_ombud_schemes (
  id                             uuid NOT NULL DEFAULT gen_random_uuid(),
  tenant_id                      uuid,
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
  scheme_code                    varchar(20) NOT NULL,
  name                           varchar(200) NOT NULL,
  contact                        text,
  referral_information           text NOT NULL,
  families                       text[] NOT NULL,
  CONSTRAINT ref_ombud_schemes_pk PRIMARY KEY (id),
  CONSTRAINT ref_ombud_schemes_bkey UNIQUE NULLS NOT DISTINCT (tenant_id, code, version),
  CONSTRAINT ref_ombud_schemes_stat CHECK (record_status IN ('DRAFT', 'PENDING_APPROVAL', 'APPROVED', 'ACTIVE', 'DEPRECATED', 'RETIRED')),
  CONSTRAINT ref_ombud_schemes_dates CHECK (effective_to IS NULL OR effective_to > effective_from),
  CONSTRAINT ref_ombud_schemes_rcls CHECK (record_class IN ('PUBLIC','INTERNAL','CONFIDENTIAL','PERSONAL','HIGHLY_SENSITIVE')),
  CONSTRAINT ref_ombud_schemes_ck1 CHECK (families <@ ARRAY['ENQUIRY', 'COMPLAINT', 'DISPUTE', 'APPEAL']::text[]),
  CONSTRAINT ref_ombud_schemes_nolap EXCLUDE USING gist (
    (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =, code WITH =,
    daterange(effective_from, effective_to, '[)') WITH &&)
    WHERE (record_status IN ('APPROVED', 'ACTIVE'))
);
CREATE INDEX ref_ombud_schemes_lookup ON workflow.ref_ombud_schemes (tenant_id, code, effective_from);
ALTER TABLE workflow.ref_ombud_schemes ENABLE ROW LEVEL SECURITY;
ALTER TABLE workflow.ref_ombud_schemes FORCE ROW LEVEL SECURITY;
CREATE POLICY read_global_and_own ON workflow.ref_ombud_schemes FOR SELECT
  USING (tenant_id IS NULL OR tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
CREATE POLICY tenant_insert ON workflow.ref_ombud_schemes FOR INSERT WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
CREATE POLICY tenant_update ON workflow.ref_ombud_schemes FOR UPDATE USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
CREATE POLICY tenant_delete ON workflow.ref_ombud_schemes FOR DELETE USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
CREATE POLICY platform_baseline ON workflow.ref_ombud_schemes TO insys_platform_admin
  USING (tenant_id IS NULL) WITH CHECK (tenant_id IS NULL);
CREATE TRIGGER guard_versions BEFORE UPDATE OR DELETE ON workflow.ref_ombud_schemes
  FOR EACH ROW EXECUTE FUNCTION sdm.guard_static_version();
COMMENT ON TABLE workflow.ref_ombud_schemes IS 'Ombud schemes. REF, scope GT, Tier 2. Ombud schemes and what a rejection notice and portal decision show about them (PPR 18.9-18.10); none seeded.';

-- Workflow and Case Management · Cases · Case retention
CREATE TABLE workflow.cfg_case_retention (
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
  family                         varchar(12) NOT NULL,
  retain_for                     varchar(20) NOT NULL,
  CONSTRAINT cfg_case_retention_pk PRIMARY KEY (id),
  CONSTRAINT cfg_case_retention_bkey UNIQUE NULLS NOT DISTINCT (tenant_id, code, version),
  CONSTRAINT cfg_case_retention_stat CHECK (record_status IN ('DRAFT', 'VALIDATED', 'SUBMITTED', 'APPROVED', 'PUBLISHED', 'EFFECTIVE', 'WITHDRAWN', 'RETIRED')),
  CONSTRAINT cfg_case_retention_dates CHECK (effective_to IS NULL OR effective_to > effective_from),
  CONSTRAINT cfg_case_retention_rcls CHECK (record_class IN ('PUBLIC','INTERNAL','CONFIDENTIAL','PERSONAL','HIGHLY_SENSITIVE')),
  CONSTRAINT cfg_case_retention_ck1 CHECK (family IN ('ENQUIRY', 'COMPLAINT', 'DISPUTE', 'APPEAL')),
  CONSTRAINT cfg_case_retention_nolap EXCLUDE USING gist (
    (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =, code WITH =,
    daterange(effective_from, effective_to, '[)') WITH &&)
    WHERE (record_status IN ('APPROVED', 'PUBLISHED', 'EFFECTIVE'))
);
CREATE INDEX cfg_case_retention_lookup ON workflow.cfg_case_retention (tenant_id, code, effective_from);
ALTER TABLE workflow.cfg_case_retention ENABLE ROW LEVEL SECURITY;
ALTER TABLE workflow.cfg_case_retention FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON workflow.cfg_case_retention
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
CREATE TRIGGER guard_versions BEFORE UPDATE OR DELETE ON workflow.cfg_case_retention
  FOR EACH ROW EXECUTE FUNCTION sdm.guard_static_version();
COMMENT ON TABLE workflow.cfg_case_retention IS 'Case retention. CFG, scope T, Tier 1. How long a closed case is kept (ISO 8601, from closure); 5 years when none is in force; a legal hold overrides (OQ-106).';

-- The nine PPR complaint categories (Rule 18.5.1), global and active.
INSERT INTO workflow.ref_case_categories (tenant_id, code, version, effective_from, record_status, created_by, change_reason, category_code, family, ppr_category, name) VALUES
  (NULL, 'POLICY_DESIGN', 1, DATE '2026-01-01', 'ACTIVE', 'V0060', 'PPR Rule 18.5.1', 'POLICY_DESIGN', 'COMPLAINT', 'POLICY_DESIGN', 'Policy or service design, including premiums, fees and charges'),
  (NULL, 'INFORMATION_PROVIDED', 1, DATE '2026-01-01', 'ACTIVE', 'V0060', 'PPR Rule 18.5.1', 'INFORMATION_PROVIDED', 'COMPLAINT', 'INFORMATION_PROVIDED', 'Information provided'),
  (NULL, 'ADVICE', 1, DATE '2026-01-01', 'ACTIVE', 'V0060', 'PPR Rule 18.5.1', 'ADVICE', 'COMPLAINT', 'ADVICE', 'Advice'),
  (NULL, 'POLICY_PERFORMANCE', 1, DATE '2026-01-01', 'ACTIVE', 'V0060', 'PPR Rule 18.5.1', 'POLICY_PERFORMANCE', 'COMPLAINT', 'POLICY_PERFORMANCE', 'Policy performance'),
  (NULL, 'SERVICE', 1, DATE '2026-01-01', 'ACTIVE', 'V0060', 'PPR Rule 18.5.1', 'SERVICE', 'COMPLAINT', 'SERVICE', 'Service, including premium collection and lapses'),
  (NULL, 'ACCESSIBILITY_CHANGES', 1, DATE '2026-01-01', 'ACTIVE', 'V0060', 'PPR Rule 18.5.1', 'ACCESSIBILITY_CHANGES', 'COMPLAINT', 'ACCESSIBILITY_CHANGES', 'Policy accessibility, changes or switches'),
  (NULL, 'COMPLAINTS_HANDLING', 1, DATE '2026-01-01', 'ACTIVE', 'V0060', 'PPR Rule 18.5.1', 'COMPLAINTS_HANDLING', 'COMPLAINT', 'COMPLAINTS_HANDLING', 'Complaints handling'),
  (NULL, 'CLAIMS', 1, DATE '2026-01-01', 'ACTIVE', 'V0060', 'PPR Rule 18.5.1', 'CLAIMS', 'COMPLAINT', 'CLAIMS', 'Insurance risk claims, including non-payment'),
  (NULL, 'OTHER', 1, DATE '2026-01-01', 'ACTIVE', 'V0060', 'PPR Rule 18.5.1', 'OTHER', 'COMPLAINT', 'OTHER', 'Other');
