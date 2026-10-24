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
-- V0048: onboarding readiness (docs/specs/addenda/onboarding-readiness.md, signed off 24 October 2026; TEN-008).
-- What the operator records when completing a step instead of configuration: "no reinsurance" or "no webhooks" with a
-- reason (a waiver), or the reference of the end-to-end run (evidence). Each is {step, check, kind, note, by, at}.
-- =====================================================================
ALTER TABLE tenant.onboarding_case ADD COLUMN attestations jsonb NOT NULL DEFAULT '[]'::jsonb;
COMMENT ON COLUMN tenant.onboarding_case.attestations IS 'Waivers (TREATIES, WEBHOOKS_OR_NONE) and evidence (SMOKE_RUN) recorded by the operator when completing a step.';
