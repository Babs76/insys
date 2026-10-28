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
-- V0071: the party number (BR-029, decided by Ade on 28 October 2026). INSYS gives every party a number on registration:
-- "P", 8 digits from a per-tenant counter, and a Luhn (mod 10) check digit over those 8. It holds no personal data, is
-- never reused (the counter only goes up; a registration that rolls back rolls its increment back with it, so no two
-- parties ever share a number), and is searchable through the party search. A trigger assigns it, so every way a party
-- is registered (staff, portal, imports) gets one in the same transaction. Existing parties are numbered in
-- registration order, per tenant.
-- =====================================================================

-- The Luhn check digit of a string of digits: the digit that makes the string followed by it pass the Luhn check.
CREATE FUNCTION party.luhn_check_digit(p_digits text)
RETURNS integer
LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
SET search_path = pg_catalog AS $$
  SELECT (10 - (sum(CASE WHEN (length(p_digits) - i) % 2 = 0
                         THEN CASE WHEN substr(p_digits, i, 1)::int * 2 > 9 THEN substr(p_digits, i, 1)::int * 2 - 9 ELSE substr(p_digits, i, 1)::int * 2 END
                         ELSE substr(p_digits, i, 1)::int END) % 10)) % 10
  FROM generate_series(1, length(p_digits)) AS i
$$;

-- The party number for a counter value: P, the value in 8 digits, and its check digit.
CREATE FUNCTION party.party_number(p_value bigint)
RETURNS varchar(10)
LANGUAGE plpgsql IMMUTABLE STRICT
SET search_path = pg_catalog AS $$
BEGIN
  IF p_value < 1 OR p_value > 99999999 THEN
    RAISE EXCEPTION 'Party number % is out of range (1 to 99999999).', p_value USING ERRCODE = 'numeric_value_out_of_range';
  END IF;
  RETURN 'P' || lpad(p_value::text, 8, '0') || party.luhn_check_digit(lpad(p_value::text, 8, '0'));
END
$$;

CREATE TABLE party.party_number_counter (
  tenant_id                  uuid NOT NULL,
  last_value                 bigint NOT NULL,
  CONSTRAINT pk_party_number_counter PRIMARY KEY (tenant_id),
  CONSTRAINT ck_party_number_counter_range CHECK (last_value BETWEEN 0 AND 99999999)
);
COMMENT ON TABLE party.party_number_counter IS 'BR-029: the last party number value given in the tenant. Only ever increases; written by party.assign_party_number.';
ALTER TABLE party.party_number_counter ENABLE ROW LEVEL SECURITY;
ALTER TABLE party.party_number_counter FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON party.party_number_counter
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);

ALTER TABLE party.party ADD COLUMN party_number varchar(10);
COMMENT ON COLUMN party.party.party_number IS
  'BR-029: P, 8 digits from the tenant''s counter and a Luhn check digit (for example P000000018). Assigned on insert, never changed or reused; no personal data.';

-- Existing parties, in registration order, per tenant (as the owner: row-level security does not apply to it).
WITH numbered AS (
  SELECT tenant_id, id, row_number() OVER (PARTITION BY tenant_id ORDER BY created_at, id) AS n FROM party.party
)
UPDATE party.party p SET party_number = party.party_number(numbered.n)
FROM numbered WHERE p.tenant_id = numbered.tenant_id AND p.id = numbered.id;
INSERT INTO party.party_number_counter (tenant_id, last_value)
SELECT tenant_id, count(*) FROM party.party GROUP BY tenant_id;

ALTER TABLE party.party ALTER COLUMN party_number SET NOT NULL;
CREATE UNIQUE INDEX uq_party_number ON party.party (tenant_id, party_number);

CREATE FUNCTION party.assign_party_number()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog AS $$
DECLARE
  next_value bigint;
BEGIN
  INSERT INTO party.party_number_counter AS c (tenant_id, last_value) VALUES (NEW.tenant_id, 1)
  ON CONFLICT (tenant_id) DO UPDATE SET last_value = c.last_value + 1
  RETURNING last_value INTO next_value;
  NEW.party_number := party.party_number(next_value);
  RETURN NEW;
END
$$;

CREATE TRIGGER party_number_on_insert BEFORE INSERT ON party.party
  FOR EACH ROW WHEN (NEW.party_number IS NULL) EXECUTE FUNCTION party.assign_party_number();

-- Once given, a number does not change.
CREATE FUNCTION party.keep_party_number()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog AS $$
BEGIN
  IF NEW.party_number IS DISTINCT FROM OLD.party_number THEN
    RAISE EXCEPTION 'A party number is never changed (BR-029).' USING ERRCODE = 'check_violation';
  END IF;
  RETURN NEW;
END
$$;

CREATE TRIGGER party_number_kept BEFORE UPDATE OF party_number ON party.party
  FOR EACH ROW EXECUTE FUNCTION party.keep_party_number();

-- A search by party number is logged like the other filters (GAP-022). The number holds no personal data, so retention
-- keeps it, as it keeps the identifier's hash (only the name filter is minimised, OQ-073).
ALTER TABLE party.party_search_log ADD COLUMN party_number_filter varchar(10);
