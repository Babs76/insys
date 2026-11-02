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
-- V0130: Workstream F. A sample tenant's notifications and payments go only to stubs. The mark is set when the tenant is created and stays for its life:
-- no one can change it, the operator and the module roles included, and a real customer always gets a new tenant (Ade, 2 November 2026).
-- =====================================================================
ALTER TABLE tenant.tenant ADD COLUMN sample boolean NOT NULL DEFAULT false;

CREATE FUNCTION tenant.sample_mark_is_fixed() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.sample IS DISTINCT FROM OLD.sample THEN
    RAISE EXCEPTION 'A tenant''s sample mark is fixed when it is created.' USING ERRCODE = 'check_violation';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER sample_mark_is_fixed BEFORE UPDATE ON tenant.tenant FOR EACH ROW EXECUTE FUNCTION tenant.sample_mark_is_fixed();
