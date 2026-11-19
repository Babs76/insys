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
-- V0144: an approved static data row's status moves only forward (OQ-185, Ade, 19 November 2026, option (b)). Before, the guard kept an
-- approved row's fields immutable (SDM-002) but let record_status change to any value, so a direct update could set a published product
-- version back to DRAFT. The same function guards every static data table, so this one change covers them all.
--
-- The moves allowed from a locked status: the two lifecycles of the static data specification §4, together, as one trigger serves both.
--   Reference:     APPROVED -> ACTIVE -> DEPRECATED -> RETIRED, and ACTIVE -> RETIRED.
--   Configuration: APPROVED -> PUBLISHED -> EFFECTIVE; PUBLISHED or EFFECTIVE -> WITHDRAWN; PUBLISHED, EFFECTIVE or WITHDRAWN -> RETIRED.
-- An unchanged status stays allowed (effective_to and row_version still change on a locked row). INSYS makes only these moves:
-- StaticDataService's activation, deprecation and retirement, imports, and Product Factory's ProductVersion.Lifecycle. A rejection
-- returns only a pending row to draft, and a pending row is not locked.
-- A deliberate correction stays possible as the owner, with the trigger off for that change, which is visible.
-- =====================================================================
CREATE OR REPLACE FUNCTION sdm.guard_static_version() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
  locked text[] := ARRAY['APPROVED','ACTIVE','DEPRECATED','PUBLISHED','EFFECTIVE','WITHDRAWN','RETIRED'];
  mutable text[] := ARRAY['record_status','effective_to','row_version'];
  forward text[];
BEGIN
  IF TG_OP = 'DELETE' THEN
    IF OLD.record_status <> 'DRAFT' THEN
      RAISE EXCEPTION '% %: only draft records can be deleted; deprecate or retire instead (SDM-007)', TG_TABLE_NAME, OLD.code;
    END IF;
    RETURN OLD;
  END IF;
  IF OLD.record_status = ANY (locked) THEN
    IF (to_jsonb(NEW) - mutable) IS DISTINCT FROM (to_jsonb(OLD) - mutable) THEN
      RAISE EXCEPTION '% % v%: approved versions are immutable; create a new version (SDM-002)', TG_TABLE_NAME, OLD.code, OLD.version;
    END IF;
    forward := CASE OLD.record_status
      WHEN 'APPROVED'   THEN ARRAY['ACTIVE','PUBLISHED']
      WHEN 'ACTIVE'     THEN ARRAY['DEPRECATED','RETIRED']
      WHEN 'DEPRECATED' THEN ARRAY['RETIRED']
      WHEN 'PUBLISHED'  THEN ARRAY['EFFECTIVE','WITHDRAWN','RETIRED']
      WHEN 'EFFECTIVE'  THEN ARRAY['WITHDRAWN','RETIRED']
      WHEN 'WITHDRAWN'  THEN ARRAY['RETIRED']
      ELSE ARRAY[]::text[]
    END;
    IF NEW.record_status IS DISTINCT FROM OLD.record_status AND NOT (NEW.record_status = ANY (forward)) THEN
      RAISE EXCEPTION '% % v%: an approved version''s status moves only forward, not from % to % (OQ-185)',
        TG_TABLE_NAME, OLD.code, OLD.version, OLD.record_status, NEW.record_status;
    END IF;
  END IF;
  RETURN NEW;
END $$;
