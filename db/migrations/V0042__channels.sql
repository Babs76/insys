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
-- V0042: channel transaction permissions (F005, docs/specs/addenda/channel-permissions.md, signed off 23 October 2026).
-- A request's channel is the one whose client_ids hold the token's OAuth client (azp), never one the request names (§1).
-- The channel a command came through is recorded on what it creates: General quotes, policies and claims, Life claims
-- and Health memberships (Life proposals already have channel_code). Existing records keep no channel.
-- =====================================================================
ALTER TABLE channel.cfg_channel_definitions ADD COLUMN client_ids text[];
ALTER TABLE general.quote ADD COLUMN channel_code varchar(12);
ALTER TABLE general.policy ADD COLUMN channel_code varchar(12);
ALTER TABLE general.claim ADD COLUMN channel_code varchar(12);
ALTER TABLE life.claim ADD COLUMN channel_code varchar(12);
ALTER TABLE health.membership ADD COLUMN channel_code varchar(12);
