-- =====================================================================
-- INSYS Core · Concurrency and change columns on the tenant register
-- tenant.tenant changes status (TEN-008) but V0001 gives it no row_version,
-- which DBA-011 requires for mutable rows, and no updated_at/updated_by.
-- See OQ-022.
-- =====================================================================
ALTER TABLE tenant.tenant
  ADD COLUMN row_version bigint NOT NULL DEFAULT 1,
  ADD COLUMN updated_at  timestamptz NOT NULL DEFAULT now(),
  ADD COLUMN updated_by  varchar(80) NOT NULL DEFAULT 'migration';
