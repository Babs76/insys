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
-- V0133: Wave 7, batch 7.6. The starter packs (ai-assist.md §5.2, D-18): each version of a base pack or an overlay, with its rows, in Assist's own tables,
-- and the overlay lifecycle of §5.3 on the version (DRAFT, REVIEWED, APPROVED). A platform version (ALL_TENANTS) is held by the operator tenant and read by
-- every tenant; a fallback version (TENANT_ONLY) is held by the one tenant that may use it, and read by it alone. A version's rows never change: new content
-- is a new version. The content hash covers every row, its marks and their source, so an approval binds to exactly what was reviewed (§5.3).
CREATE TABLE assist.starter_pack (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  pack_code                  varchar(40) NOT NULL,
  kind                       varchar(10) NOT NULL,
  version                    integer NOT NULL,
  builds_on                  varchar(40),
  country                    char(2),
  line                       varchar(10),
  scope                      varchar(12) NOT NULL,
  status                     varchar(10) NOT NULL,
  content_hash               char(64) NOT NULL,
  row_count                  integer NOT NULL,
  prepared_by                varchar(80) NOT NULL,
  reviewed_by                varchar(80),
  review_partner             varchar(200),
  reviewer_name              varchar(200),
  reviewed_on                date,
  review_document_id         uuid,
  approved_by                varchar(80),
  approved_at                timestamptz,
  CONSTRAINT pk_starter_pack PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_starter_pack_version UNIQUE (tenant_id, pack_code, version),
  CONSTRAINT ck_starter_pack_kind CHECK (kind IN ('BASE', 'COUNTRY', 'REGION', 'LINE')),
  CONSTRAINT ck_starter_pack_scope CHECK (scope IN ('ALL_TENANTS', 'TENANT_ONLY')),
  CONSTRAINT ck_starter_pack_status CHECK (status IN ('DRAFT', 'REVIEWED', 'APPROVED')),
  CONSTRAINT ck_starter_pack_reviewed CHECK (status = 'DRAFT' OR scope = 'TENANT_ONLY' OR (reviewed_by IS NOT NULL AND review_partner IS NOT NULL AND reviewer_name IS NOT NULL AND reviewed_on IS NOT NULL AND review_document_id IS NOT NULL)),
  CONSTRAINT ck_starter_pack_approved CHECK ((status = 'APPROVED') = (approved_by IS NOT NULL AND approved_at IS NOT NULL)),
  CONSTRAINT ck_starter_pack_separate CHECK (approved_by IS NULL OR (approved_by <> prepared_by AND approved_by IS DISTINCT FROM reviewed_by))
);
ALTER TABLE assist.starter_pack ENABLE ROW LEVEL SECURITY;
ALTER TABLE assist.starter_pack FORCE ROW LEVEL SECURITY;
CREATE POLICY read_platform_and_own ON assist.starter_pack FOR SELECT
  USING (scope = 'ALL_TENANTS' OR tenant_id = current_setting('app.tenant_id')::uuid);
CREATE POLICY own_insert ON assist.starter_pack FOR INSERT WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
CREATE POLICY own_update ON assist.starter_pack FOR UPDATE USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);

CREATE TABLE assist.starter_pack_value (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  pack_id                    uuid NOT NULL,
  scope                      varchar(12) NOT NULL,
  line_no                    integer NOT NULL,
  table_name                 varchar(80) NOT NULL,
  row_code                   varchar(120) NOT NULL,
  class                      varchar(12) NOT NULL,
  regulated                  boolean NOT NULL,
  regulated_source           varchar(10) NOT NULL,
  tailorable                 boolean NOT NULL,
  tailorable_source          varchar(10) NOT NULL,
  values                     jsonb NOT NULL,
  row_status                 varchar(40),
  note                       text,
  basis_to_check             text,
  CONSTRAINT pk_starter_pack_value PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_starter_pack_value_line UNIQUE (tenant_id, pack_id, line_no),
  CONSTRAINT fk_starter_pack_value_pack FOREIGN KEY (tenant_id, pack_id) REFERENCES assist.starter_pack (tenant_id, id),
  CONSTRAINT ck_starter_pack_value_class CHECK (class IN ('OPERATIONAL', 'FINANCE', 'COMPLIANCE')),
  CONSTRAINT ck_starter_pack_value_regulated_source CHECK (regulated_source IN ('EXPLICIT', 'INTERIM')),
  CONSTRAINT ck_starter_pack_value_tailorable_source CHECK (tailorable_source IN ('EXPLICIT', 'DEFAULT')),
  CONSTRAINT ck_starter_pack_value_scope CHECK (scope IN ('ALL_TENANTS', 'TENANT_ONLY'))
);
ALTER TABLE assist.starter_pack_value ENABLE ROW LEVEL SECURITY;
ALTER TABLE assist.starter_pack_value FORCE ROW LEVEL SECURITY;
CREATE POLICY read_platform_and_own ON assist.starter_pack_value FOR SELECT
  USING (scope = 'ALL_TENANTS' OR tenant_id = current_setting('app.tenant_id')::uuid);
CREATE POLICY own_insert ON assist.starter_pack_value FOR INSERT WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
COMMENT ON COLUMN assist.starter_pack_value.regulated_source IS 'EXPLICIT: the row says. INTERIM: read from the class, FINANCE or COMPLIANCE (D-20); never tailored.';
COMMENT ON COLUMN assist.starter_pack_value.tailorable_source IS 'EXPLICIT: the row says. DEFAULT: no mark, so not tailorable (D-20).';

-- After loading, a version's identity and content never change, and its status only moves forward (§5.3); its rows are added only with it, while it is a
-- draft that hasn't all its rows. So an approval binds to what was reviewed, whatever a holder's UPDATE could otherwise reach.
CREATE FUNCTION assist.guard_starter_pack() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF (NEW.tenant_id, NEW.pack_code, NEW.kind, NEW.version, NEW.builds_on, NEW.country, NEW.line, NEW.scope, NEW.content_hash, NEW.row_count, NEW.prepared_by)
     IS DISTINCT FROM (OLD.tenant_id, OLD.pack_code, OLD.kind, OLD.version, OLD.builds_on, OLD.country, OLD.line, OLD.scope, OLD.content_hash, OLD.row_count, OLD.prepared_by) THEN
    RAISE EXCEPTION 'A starter pack version''s identity and content never change: load a new version.';
  END IF;
  IF OLD.status = 'APPROVED' OR (OLD.status = 'REVIEWED' AND NEW.status = 'DRAFT') THEN
    RAISE EXCEPTION 'A starter pack version''s status only moves forward.';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER guard_starter_pack BEFORE UPDATE ON assist.starter_pack FOR EACH ROW EXECUTE FUNCTION assist.guard_starter_pack();

CREATE FUNCTION assist.guard_starter_pack_value() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE parent assist.starter_pack%ROWTYPE;
BEGIN
  SELECT * INTO parent FROM assist.starter_pack WHERE tenant_id = NEW.tenant_id AND id = NEW.pack_id;
  IF parent.status <> 'DRAFT' OR parent.reviewed_by IS NOT NULL
     OR (SELECT count(*) FROM assist.starter_pack_value WHERE tenant_id = NEW.tenant_id AND pack_id = NEW.pack_id) >= parent.row_count
     OR NEW.scope <> parent.scope THEN
    RAISE EXCEPTION 'A starter pack version''s rows are added only with it.';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER guard_starter_pack_value BEFORE INSERT ON assist.starter_pack_value FOR EACH ROW EXECUTE FUNCTION assist.guard_starter_pack_value();

