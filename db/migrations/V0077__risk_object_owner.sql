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
-- V0077: a risk object belongs to a client (BR-092, decided by Ade on 28 October 2026), so a broker can register one for
-- a client it services and list a client's. The owner is a party; staff may set or change it, audited.
--
-- Backfill, not guessed: an item gets an owner only where every policy it is on has the same policyholder, and no quote
-- for it names anyone else. An item on no policy, or on policies of two policyholders, stays without an owner.
-- =====================================================================
ALTER TABLE general.risk_object ADD COLUMN owner_party_id uuid;

COMMENT ON COLUMN general.risk_object.owner_party_id IS 'BR-092: the client the item belongs to (party.party); null when not known. A broker sees the item while it services the owner.';

UPDATE general.risk_object r
SET owner_party_id = o.holder
FROM (SELECT pr.tenant_id, pr.risk_object_id, min(p.policyholder_party_id::text)::uuid AS holder
      FROM general.policy_risk pr
      JOIN general.policy p ON p.tenant_id = pr.tenant_id AND p.id = pr.policy_id
      GROUP BY pr.tenant_id, pr.risk_object_id
      HAVING count(DISTINCT p.policyholder_party_id) = 1) o
WHERE r.tenant_id = o.tenant_id AND r.id = o.risk_object_id
  AND NOT EXISTS (SELECT 1
                  FROM general.quote_risk qr
                  JOIN general.quote q ON q.tenant_id = qr.tenant_id AND q.id = qr.quote_id
                  WHERE qr.tenant_id = r.tenant_id AND qr.risk_object_id = r.id AND q.proposer_party_id <> o.holder);

CREATE INDEX ix_risk_object_owner ON general.risk_object (tenant_id, owner_party_id) WHERE owner_party_id IS NOT NULL;
