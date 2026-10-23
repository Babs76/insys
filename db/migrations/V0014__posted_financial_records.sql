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
-- INSYS Core · Posted financial records (Phase 3; FIN-002, FIN-003, DBA-009)
-- 1. A journal balances: its lines' debits equal their credits, in one currency,
--    with at least two lines. Checked at commit by deferred constraint triggers, so
--    a journal and its lines are written in one transaction.
-- 2. Posted records are never changed or deleted. The module roles hold no UPDATE or
--    DELETE on them (db/roles/module_grants.sql), and a trigger refuses both even
--    on a partition, whose grants a new partition takes from the schema defaults.
-- 3. A reversal journal posts on its own accounting date (the original's period may
--    be closed), so it no longer has to share the original's date (OQ-057).
-- =====================================================================

ALTER TABLE finance.journal DROP CONSTRAINT fk_journal_reverses_journal_id;
ALTER TABLE finance.journal ADD CONSTRAINT ck_journal_status CHECK (status = 'POSTED');
ALTER TABLE finance.subledger_entry
  ADD CONSTRAINT ck_subledger_entry_amounts CHECK (debit >= 0 AND credit >= 0 AND debit + credit > 0);

CREATE OR REPLACE FUNCTION finance.check_journal_balanced() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
  -- A line names its journal; a journal is its own. (NEW.journal_id cannot be read on a journal row.)
  journal uuid := coalesce((to_jsonb(NEW) ->> 'journal_id')::uuid, NEW.id);
  lines integer;
  currencies integer;
  difference numeric;
BEGIN
  SELECT count(*), count(DISTINCT currency), coalesce(sum(debit), 0) - coalesce(sum(credit), 0)
    INTO lines, currencies, difference
    FROM finance.subledger_entry WHERE tenant_id = NEW.tenant_id AND journal_id = journal;
  IF lines < 2 THEN
    RAISE EXCEPTION 'Journal % has % line(s); a journal has at least two', journal, lines USING ERRCODE = '23514';
  END IF;
  IF currencies <> 1 THEN
    RAISE EXCEPTION 'Journal % mixes currencies', journal USING ERRCODE = '23514';
  END IF;
  IF difference <> 0 THEN
    RAISE EXCEPTION 'Journal % does not balance: debits minus credits is %', journal, difference USING ERRCODE = '23514';
  END IF;
  RETURN NULL;
END $$;

CREATE CONSTRAINT TRIGGER journal_balanced AFTER INSERT ON finance.journal
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION finance.check_journal_balanced();
CREATE CONSTRAINT TRIGGER subledger_entry_balanced AFTER INSERT ON finance.subledger_entry
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION finance.check_journal_balanced();

-- A reversal references a journal of the same tenant, and a journal is reversed at most once.
CREATE OR REPLACE FUNCTION finance.check_reversal() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.reverses_journal_id IS NULL THEN
    RETURN NEW;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM finance.journal WHERE tenant_id = NEW.tenant_id AND id = NEW.reverses_journal_id) THEN
    RAISE EXCEPTION 'Journal % reverses an unknown journal %', NEW.id, NEW.reverses_journal_id USING ERRCODE = '23503';
  END IF;
  IF EXISTS (SELECT 1 FROM finance.journal WHERE tenant_id = NEW.tenant_id AND reverses_journal_id = NEW.reverses_journal_id) THEN
    RAISE EXCEPTION 'Journal % is already reversed', NEW.reverses_journal_id USING ERRCODE = '23505';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER journal_reversal BEFORE INSERT ON finance.journal FOR EACH ROW EXECUTE FUNCTION finance.check_reversal();

-- Posted records are never changed (FIN-002). One function per owning schema.
CREATE OR REPLACE FUNCTION finance.refuse_change() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  RAISE EXCEPTION '%.% is posted: records are never changed or deleted; post a reversal (FIN-002, FIN-003)', TG_TABLE_SCHEMA, TG_TABLE_NAME
    USING ERRCODE = '42501';
END $$;
CREATE TRIGGER journal_immutable BEFORE UPDATE OR DELETE ON finance.journal FOR EACH ROW EXECUTE FUNCTION finance.refuse_change();
CREATE TRIGGER subledger_entry_immutable BEFORE UPDATE OR DELETE ON finance.subledger_entry FOR EACH ROW EXECUTE FUNCTION finance.refuse_change();

CREATE OR REPLACE FUNCTION claims.refuse_change() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  RAISE EXCEPTION '%.% is append-only: a correction is a new movement (CLM-004)', TG_TABLE_SCHEMA, TG_TABLE_NAME USING ERRCODE = '42501';
END $$;
CREATE TRIGGER claim_financial_movement_immutable BEFORE UPDATE OR DELETE ON claims.claim_financial_movement
  FOR EACH ROW EXECUTE FUNCTION claims.refuse_change();

CREATE OR REPLACE FUNCTION payment.refuse_change() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  RAISE EXCEPTION '%.% is a history: records are never changed (PAY-002)', TG_TABLE_SCHEMA, TG_TABLE_NAME USING ERRCODE = '42501';
END $$;
CREATE TRIGGER payment_status_history_immutable BEFORE UPDATE OR DELETE ON payment.payment_status_history
  FOR EACH ROW EXECUTE FUNCTION payment.refuse_change();

-- Reopening a soft-closed period waits for a second person's approval in Workflow (ADR-009).
ALTER TABLE finance.accounting_period
  ADD COLUMN reopen_requested_by uuid,
  ADD COLUMN reopen_requested_at timestamptz,
  ADD CONSTRAINT ck_accounting_period_reopen CHECK (
    (reopen_requested_by IS NULL) = (reopen_requested_at IS NULL) AND (reopen_requested_by IS NULL OR status = 'SOFT_CLOSE'));
