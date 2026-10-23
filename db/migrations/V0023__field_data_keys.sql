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
-- V0023: the register of field protection data keys for envelope encryption (ADR-018, Phase 8 Workstream D).
-- Platform-wide (no tenant): each row is a data key encrypted under the KMS master key; its plaintext is never stored.
-- Encrypted fields carry their data key's ID, so rotating keys never rewrites rows. The API role reads and adds keys.
-- =====================================================================
CREATE TABLE integration.field_data_key (
  key_id                     varchar(40) NOT NULL,
  master_key_ref             varchar(200) NOT NULL,
  ciphertext                 bytea NOT NULL,
  created_at                 timestamptz NOT NULL,
  CONSTRAINT pk_field_data_key PRIMARY KEY (key_id)
);
CREATE INDEX ix_field_data_key_created ON integration.field_data_key (created_at);
