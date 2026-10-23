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
-- INSYS Core · document.document records its lifecycle state (OQ-056)
-- domain-model.json gives Document the states Requested, Generated, Delivered
-- and Disposed, and the rule that a document is not disposed of before its
-- retention date or while on legal hold. V0001 has no column for the state:
-- Requested is document.generation_request.status, but nothing records that a
-- document was delivered or disposed of. Same approach as V0007 and V0010.
-- Existing rows are generated documents.
-- =====================================================================
ALTER TABLE document.document
  ADD COLUMN status      varchar(12) NOT NULL DEFAULT 'GENERATED',
  ADD COLUMN disposed_at timestamptz,
  ADD CONSTRAINT ck_document_status CHECK (status IN ('GENERATED', 'DELIVERED', 'DISPOSED')),
  ADD CONSTRAINT ck_document_disposed CHECK ((status = 'DISPOSED') = (disposed_at IS NOT NULL)),
  ADD CONSTRAINT ck_document_hold_disposal CHECK (NOT (status = 'DISPOSED' AND legal_hold));
ALTER TABLE document.document ALTER COLUMN status DROP DEFAULT;
