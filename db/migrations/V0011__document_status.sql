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
