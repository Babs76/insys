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
-- V0051: Communications (addenda/communications.md, signed off 25 October 2026; Wave 1).
-- The communication schema, owned by the Communications module on the insys_communication role:
-- - static data: the shared channel list (OQ-090), notification types, templates per channel and language, and the
--   tenant's channel providers. A marketing type names its consent purpose, and a service type none: Communications
--   validates that, since the static data generator reads only IN, array, range and pattern checks;
-- - one notification per recipient and trigger, idempotent by request_key, with its delivery attempts;
-- - per-party preferences per notification type; push devices of portal users;
-- - provider delivery callback endpoints and receipts (ADR-023, as payment's);
-- - the module's outbox and inbox.
-- Also: general.cfg_renewal_rules.reminder_days (OQ-089) and general.cfg_aggregation_zones_and_limits.alert_role
-- (OQ-085: the role reviewing a limit's alerts; null uses General:Aggregation:AlertRole).
-- =====================================================================
CREATE SCHEMA IF NOT EXISTS communication;

CREATE TABLE communication.outbox_event (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  aggregate_type             varchar(60) NOT NULL,
  aggregate_id               uuid NOT NULL,
  event_type                 varchar(80) NOT NULL,
  event_version              smallint NOT NULL,
  payload                    jsonb NOT NULL,
  occurred_at                timestamptz NOT NULL,
  correlation_id             uuid NOT NULL,
  causation_id               uuid,
  published_at               timestamptz,
  publish_attempts           smallint NOT NULL,
  CONSTRAINT pk_outbox_event PRIMARY KEY (tenant_id, id)
);
ALTER TABLE communication.outbox_event ENABLE ROW LEVEL SECURITY;
ALTER TABLE communication.outbox_event FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON communication.outbox_event
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);

CREATE TABLE communication.inbox_message (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  consumer                   varchar(60) NOT NULL,
  event_id                   uuid NOT NULL,
  event_type                 varchar(80) NOT NULL,
  received_at                timestamptz NOT NULL,
  processed_at               timestamptz,
  status                     varchar(12) NOT NULL,
  CONSTRAINT pk_inbox_message PRIMARY KEY (tenant_id, id)
);
CREATE UNIQUE INDEX uq_inbox_message_1 ON communication.inbox_message (tenant_id, consumer, event_id);
ALTER TABLE communication.inbox_message ENABLE ROW LEVEL SECURITY;
ALTER TABLE communication.inbox_message FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON communication.inbox_message
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);

-- Communications · Channels (OQ-090: the shared channel list)
CREATE TABLE communication.ref_channels (
  id                             uuid NOT NULL DEFAULT gen_random_uuid(),
  tenant_id                      uuid,
  code                           varchar(120) NOT NULL,
  version                        integer NOT NULL DEFAULT 1,
  effective_from                 date NOT NULL DEFAULT CURRENT_DATE,
  effective_to                   date,
  record_status                  varchar(20) NOT NULL DEFAULT 'DRAFT',
  supersedes_id                  uuid,
  labels                         jsonb,
  description                    text,
  record_source                  varchar(60),
  record_source_version          varchar(30),
  external_mappings              jsonb,
  record_class                   varchar(20) NOT NULL DEFAULT 'INTERNAL',
  sort_order                     integer,
  display_flags                  jsonb,
  change_request_id              uuid,
  created_at                     timestamptz NOT NULL DEFAULT now(),
  created_by                     varchar(80) NOT NULL,
  approved_at                    timestamptz,
  approved_by                    varchar(80),
  change_reason                  text,
  row_version                    bigint NOT NULL DEFAULT 1,
  channel_code                   varchar(10) NOT NULL,
  CONSTRAINT ref_channels_pk PRIMARY KEY (id),
  CONSTRAINT ref_channels_bkey UNIQUE NULLS NOT DISTINCT (tenant_id, code, version),
  CONSTRAINT ref_channels_stat CHECK (record_status IN ('DRAFT', 'PENDING_APPROVAL', 'APPROVED', 'ACTIVE', 'DEPRECATED', 'RETIRED')),
  CONSTRAINT ref_channels_dates CHECK (effective_to IS NULL OR effective_to > effective_from),
  CONSTRAINT ref_channels_rcls CHECK (record_class IN ('PUBLIC','INTERNAL','CONFIDENTIAL','PERSONAL','HIGHLY_SENSITIVE')),
  CONSTRAINT ref_channels_glob CHECK (tenant_id IS NULL),
  CONSTRAINT ref_channels_ck1 CHECK (channel_code IN ('EMAIL', 'SMS', 'PUSH', 'WHATSAPP')),
  CONSTRAINT ref_channels_nolap EXCLUDE USING gist (
    (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =, code WITH =,
    daterange(effective_from, effective_to, '[)') WITH &&)
    WHERE (record_status IN ('APPROVED', 'ACTIVE', 'DEPRECATED'))
);
CREATE INDEX ref_channels_lookup ON communication.ref_channels (tenant_id, code, effective_from);
ALTER TABLE communication.ref_channels ENABLE ROW LEVEL SECURITY;
ALTER TABLE communication.ref_channels FORCE ROW LEVEL SECURITY;
CREATE POLICY read_all ON communication.ref_channels FOR SELECT USING (true);
CREATE POLICY platform_maintain ON communication.ref_channels TO insys_platform_admin
  USING (tenant_id IS NULL) WITH CHECK (tenant_id IS NULL);
CREATE TRIGGER guard_versions BEFORE UPDATE OR DELETE ON communication.ref_channels
  FOR EACH ROW EXECUTE FUNCTION sdm.guard_static_version();
COMMENT ON TABLE communication.ref_channels IS 'Channels. REF, scope G, Tier 2. The channels notifications are sent on (OQ-090). Added in Wave 1 (addenda/communications.md).';

-- Communications · Notification types
CREATE TABLE communication.cfg_notification_types (
  id                             uuid NOT NULL DEFAULT gen_random_uuid(),
  tenant_id                      uuid NOT NULL,
  code                           varchar(120) NOT NULL,
  version                        integer NOT NULL DEFAULT 1,
  effective_from                 date NOT NULL DEFAULT CURRENT_DATE,
  effective_to                   date,
  record_status                  varchar(20) NOT NULL DEFAULT 'DRAFT',
  supersedes_id                  uuid,
  labels                         jsonb,
  description                    text,
  record_source                  varchar(60),
  record_source_version          varchar(30),
  external_mappings              jsonb,
  record_class                   varchar(20) NOT NULL DEFAULT 'INTERNAL',
  sort_order                     integer,
  display_flags                  jsonb,
  change_request_id              uuid,
  created_at                     timestamptz NOT NULL DEFAULT now(),
  created_by                     varchar(80) NOT NULL,
  approved_at                    timestamptz,
  approved_by                    varchar(80),
  change_reason                  text,
  row_version                    bigint NOT NULL DEFAULT 1,
  type_code                      varchar(30) NOT NULL,
  category                       varchar(10) NOT NULL,
  consent_purpose                varchar(20),
  channels                       text[] NOT NULL,
  portal_visible                 boolean NOT NULL DEFAULT false,
  CONSTRAINT cfg_notification_types_pk PRIMARY KEY (id),
  CONSTRAINT cfg_notification_types_bkey UNIQUE NULLS NOT DISTINCT (tenant_id, code, version),
  CONSTRAINT cfg_notification_types_stat CHECK (record_status IN ('DRAFT', 'VALIDATED', 'SUBMITTED', 'APPROVED', 'PUBLISHED', 'EFFECTIVE', 'WITHDRAWN', 'RETIRED')),
  CONSTRAINT cfg_notification_types_dates CHECK (effective_to IS NULL OR effective_to > effective_from),
  CONSTRAINT cfg_notification_types_rcls CHECK (record_class IN ('PUBLIC','INTERNAL','CONFIDENTIAL','PERSONAL','HIGHLY_SENSITIVE')),
  CONSTRAINT cfg_notification_types_ck1 CHECK (category IN ('SERVICE', 'MARKETING')),
  CONSTRAINT cfg_notification_types_ck2 CHECK (channels <@ ARRAY['EMAIL', 'SMS', 'PUSH', 'WHATSAPP']::text[]),
  CONSTRAINT cfg_notification_types_nolap EXCLUDE USING gist (
    (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =, code WITH =,
    daterange(effective_from, effective_to, '[)') WITH &&)
    WHERE (record_status IN ('APPROVED', 'PUBLISHED', 'EFFECTIVE'))
);
CREATE INDEX cfg_notification_types_lookup ON communication.cfg_notification_types (tenant_id, code, effective_from);
ALTER TABLE communication.cfg_notification_types ENABLE ROW LEVEL SECURITY;
ALTER TABLE communication.cfg_notification_types FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON communication.cfg_notification_types
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
CREATE TRIGGER guard_versions BEFORE UPDATE OR DELETE ON communication.cfg_notification_types
  FOR EACH ROW EXECUTE FUNCTION sdm.guard_static_version();
COMMENT ON TABLE communication.cfg_notification_types IS 'Notification types. CFG, scope T, Tier 1. What a notification is, whether it is a service notice or marketing (which needs consent), and its channels in default order. Added in Wave 1 (addenda/communications.md).';

-- Communications · Notification templates
CREATE TABLE communication.cfg_notification_templates (
  id                             uuid NOT NULL DEFAULT gen_random_uuid(),
  tenant_id                      uuid NOT NULL,
  code                           varchar(120) NOT NULL,
  version                        integer NOT NULL DEFAULT 1,
  effective_from                 date NOT NULL DEFAULT CURRENT_DATE,
  effective_to                   date,
  record_status                  varchar(20) NOT NULL DEFAULT 'DRAFT',
  supersedes_id                  uuid,
  labels                         jsonb,
  description                    text,
  record_source                  varchar(60),
  record_source_version          varchar(30),
  external_mappings              jsonb,
  record_class                   varchar(20) NOT NULL DEFAULT 'INTERNAL',
  sort_order                     integer,
  display_flags                  jsonb,
  change_request_id              uuid,
  created_at                     timestamptz NOT NULL DEFAULT now(),
  created_by                     varchar(80) NOT NULL,
  approved_at                    timestamptz,
  approved_by                    varchar(80),
  change_reason                  text,
  row_version                    bigint NOT NULL DEFAULT 1,
  type_code                      varchar(30) NOT NULL,
  channel                        varchar(10) NOT NULL,
  language                       varchar(2) NOT NULL,
  subject                        varchar(200),
  body                           text NOT NULL,
  CONSTRAINT cfg_notification_templates_pk PRIMARY KEY (id),
  CONSTRAINT cfg_notification_templates_bkey UNIQUE NULLS NOT DISTINCT (tenant_id, code, version),
  CONSTRAINT cfg_notification_templates_stat CHECK (record_status IN ('DRAFT', 'VALIDATED', 'SUBMITTED', 'APPROVED', 'PUBLISHED', 'EFFECTIVE', 'WITHDRAWN', 'RETIRED')),
  CONSTRAINT cfg_notification_templates_dates CHECK (effective_to IS NULL OR effective_to > effective_from),
  CONSTRAINT cfg_notification_templates_rcls CHECK (record_class IN ('PUBLIC','INTERNAL','CONFIDENTIAL','PERSONAL','HIGHLY_SENSITIVE')),
  CONSTRAINT cfg_notification_templates_ck1 CHECK (channel IN ('EMAIL', 'SMS', 'PUSH', 'WHATSAPP')),
  CONSTRAINT cfg_notification_templates_ck2 CHECK (language ~ '^[a-z]{2}$'),
  CONSTRAINT cfg_notification_templates_nolap EXCLUDE USING gist (
    (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =, code WITH =,
    daterange(effective_from, effective_to, '[)') WITH &&)
    WHERE (record_status IN ('APPROVED', 'PUBLISHED', 'EFFECTIVE'))
);
CREATE INDEX cfg_notification_templates_lookup ON communication.cfg_notification_templates (tenant_id, code, effective_from);
ALTER TABLE communication.cfg_notification_templates ENABLE ROW LEVEL SECURITY;
ALTER TABLE communication.cfg_notification_templates FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON communication.cfg_notification_templates
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
CREATE TRIGGER guard_versions BEFORE UPDATE OR DELETE ON communication.cfg_notification_templates
  FOR EACH ROW EXECUTE FUNCTION sdm.guard_static_version();
COMMENT ON TABLE communication.cfg_notification_templates IS 'Notification templates. CFG, scope T, Tier 1. A notification type''s text on one channel in one language, with named merge fields. Added in Wave 1 (addenda/communications.md).';

-- Communications · Channel providers
CREATE TABLE communication.cfg_channel_providers (
  id                             uuid NOT NULL DEFAULT gen_random_uuid(),
  tenant_id                      uuid NOT NULL,
  code                           varchar(120) NOT NULL,
  version                        integer NOT NULL DEFAULT 1,
  effective_from                 date NOT NULL DEFAULT CURRENT_DATE,
  effective_to                   date,
  record_status                  varchar(20) NOT NULL DEFAULT 'DRAFT',
  supersedes_id                  uuid,
  labels                         jsonb,
  description                    text,
  record_source                  varchar(60),
  record_source_version          varchar(30),
  external_mappings              jsonb,
  record_class                   varchar(20) NOT NULL DEFAULT 'INTERNAL',
  sort_order                     integer,
  display_flags                  jsonb,
  change_request_id              uuid,
  created_at                     timestamptz NOT NULL DEFAULT now(),
  created_by                     varchar(80) NOT NULL,
  approved_at                    timestamptz,
  approved_by                    varchar(80),
  change_reason                  text,
  row_version                    bigint NOT NULL DEFAULT 1,
  channel                        varchar(10) NOT NULL,
  provider_code                  varchar(20) NOT NULL,
  sender_identity                varchar(120) NOT NULL,
  enabled                        boolean NOT NULL DEFAULT true,
  CONSTRAINT cfg_channel_providers_pk PRIMARY KEY (id),
  CONSTRAINT cfg_channel_providers_bkey UNIQUE NULLS NOT DISTINCT (tenant_id, code, version),
  CONSTRAINT cfg_channel_providers_stat CHECK (record_status IN ('DRAFT', 'VALIDATED', 'SUBMITTED', 'APPROVED', 'PUBLISHED', 'EFFECTIVE', 'WITHDRAWN', 'RETIRED')),
  CONSTRAINT cfg_channel_providers_dates CHECK (effective_to IS NULL OR effective_to > effective_from),
  CONSTRAINT cfg_channel_providers_rcls CHECK (record_class IN ('PUBLIC','INTERNAL','CONFIDENTIAL','PERSONAL','HIGHLY_SENSITIVE')),
  CONSTRAINT cfg_channel_providers_ck1 CHECK (channel IN ('EMAIL', 'SMS', 'PUSH', 'WHATSAPP')),
  CONSTRAINT cfg_channel_providers_nolap EXCLUDE USING gist (
    (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =, code WITH =,
    daterange(effective_from, effective_to, '[)') WITH &&)
    WHERE (record_status IN ('APPROVED', 'PUBLISHED', 'EFFECTIVE'))
);
CREATE INDEX cfg_channel_providers_lookup ON communication.cfg_channel_providers (tenant_id, code, effective_from);
ALTER TABLE communication.cfg_channel_providers ENABLE ROW LEVEL SECURITY;
ALTER TABLE communication.cfg_channel_providers FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON communication.cfg_channel_providers
  USING (tenant_id = current_setting('app.tenant_id')::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
CREATE TRIGGER guard_versions BEFORE UPDATE OR DELETE ON communication.cfg_channel_providers
  FOR EACH ROW EXECUTE FUNCTION sdm.guard_static_version();
COMMENT ON TABLE communication.cfg_channel_providers IS 'Channel providers. CFG, scope T, Tier 1. The provider a tenant sends a channel through, and the sender identity shown. Added in Wave 1 (addenda/communications.md).';

CREATE TABLE communication.notification (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  request_key                varchar(200) NOT NULL,
  type_code                  varchar(30) NOT NULL,
  category                   varchar(10) NOT NULL,
  consent_purpose            varchar(20),
  recipient_party_id         uuid NOT NULL,
  source_type                varchar(30) NOT NULL,
  source_id                  uuid NOT NULL,
  product_code               varchar(15),
  org_node_code              varchar(30),
  merge_values               jsonb NOT NULL,
  channels                   text[] NOT NULL,
  language                   varchar(2) NOT NULL,
  subject                    varchar(200),
  body                       text,
  status                     varchar(25) NOT NULL,
  channel                    varchar(10),
  attempts                   smallint NOT NULL DEFAULT 0,
  next_attempt_at            timestamptz,
  sent_at                    timestamptz,
  delivered_at               timestamptz,
  ended_at                   timestamptz,
  CONSTRAINT pk_notification PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_notification_request UNIQUE (tenant_id, request_key),
  CONSTRAINT ck_notification_category CHECK (category IN ('SERVICE', 'MARKETING')),
  CONSTRAINT ck_notification_status CHECK (status IN ('QUEUED', 'SENT', 'DELIVERED', 'FAILED', 'UNDELIVERABLE', 'SUPPRESSED_NO_CONSENT')),
  CONSTRAINT ck_notification_channel CHECK (channel IS NULL OR channel IN ('EMAIL', 'SMS', 'PUSH', 'WHATSAPP'))
);
CREATE INDEX ix_notification_party ON communication.notification (tenant_id, recipient_party_id, id);
CREATE INDEX ix_notification_source ON communication.notification (tenant_id, source_type, source_id);
CREATE INDEX ix_notification_due ON communication.notification (tenant_id, next_attempt_at) WHERE status = 'QUEUED';
ALTER TABLE communication.notification ENABLE ROW LEVEL SECURITY;
ALTER TABLE communication.notification FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON communication.notification
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
COMMENT ON TABLE communication.notification IS 'One message to one party, from one trigger (addenda/communications.md §5).';
COMMENT ON COLUMN communication.notification.request_key IS 'Source event ID, type and recipient: a redelivered trigger creates nothing (INT-005).';
COMMENT ON COLUMN communication.notification.body IS 'The rendered text as sent: evidence of what was sent. Blanked when the party is anonymised.';

CREATE TABLE communication.delivery_attempt (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  notification_id            uuid NOT NULL,
  attempt_no                 smallint NOT NULL,
  channel                    varchar(10) NOT NULL,
  provider_code              varchar(20),
  provider_ref               varchar(120),
  masked_address             varchar(120),
  status                     varchar(15) NOT NULL,
  failure_code               varchar(40),
  retryable                  boolean NOT NULL DEFAULT false,
  attempted_at               timestamptz NOT NULL,
  status_at                  timestamptz NOT NULL,
  CONSTRAINT pk_delivery_attempt PRIMARY KEY (tenant_id, id),
  CONSTRAINT fk_delivery_attempt_notification FOREIGN KEY (tenant_id, notification_id) REFERENCES communication.notification (tenant_id, id),
  CONSTRAINT ck_delivery_attempt_channel CHECK (channel IN ('EMAIL', 'SMS', 'PUSH', 'WHATSAPP')),
  CONSTRAINT ck_delivery_attempt_status CHECK (status IN ('SENT', 'DELIVERED', 'FAILED', 'BOUNCED', 'UNDELIVERABLE'))
);
CREATE INDEX ix_delivery_attempt_notification ON communication.delivery_attempt (tenant_id, notification_id);
CREATE INDEX ix_delivery_attempt_provider_ref ON communication.delivery_attempt (tenant_id, provider_code, provider_ref);
ALTER TABLE communication.delivery_attempt ENABLE ROW LEVEL SECURITY;
ALTER TABLE communication.delivery_attempt FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON communication.delivery_attempt
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
COMMENT ON COLUMN communication.delivery_attempt.masked_address IS 'The address, masked (t***@example.com): the address itself is read from Party when sending and not kept.';

CREATE TABLE communication.notification_preference (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  party_id                   uuid NOT NULL,
  type_code                  varchar(30) NOT NULL,
  channels                   text[] NOT NULL,
  opted_out                  boolean NOT NULL,
  CONSTRAINT pk_notification_preference PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_notification_preference UNIQUE (tenant_id, party_id, type_code),
  CONSTRAINT ck_notification_preference_channels CHECK (channels <@ ARRAY['EMAIL', 'SMS', 'PUSH', 'WHATSAPP']::text[]),
  CONSTRAINT ck_notification_preference_choice CHECK (opted_out OR cardinality(channels) > 0)
);
ALTER TABLE communication.notification_preference ENABLE ROW LEVEL SECURITY;
ALTER TABLE communication.notification_preference FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON communication.notification_preference
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
COMMENT ON TABLE communication.notification_preference IS 'A party''s channels for a notification type, in order, or OPTED_OUT (marketing only) (§4).';

CREATE TABLE communication.device_registration (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  user_id                    uuid NOT NULL,
  party_id                   uuid NOT NULL,
  platform                   varchar(10) NOT NULL,
  token_hash                 char(64) NOT NULL,
  token_enc                  bytea NOT NULL,
  registered_at              timestamptz NOT NULL,
  revoked_at                 timestamptz,
  CONSTRAINT pk_device_registration PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_device_registration_token UNIQUE (tenant_id, token_hash),
  CONSTRAINT ck_device_registration_platform CHECK (platform IN ('IOS', 'ANDROID', 'WEB'))
);
CREATE INDEX ix_device_registration_party ON communication.device_registration (tenant_id, party_id) WHERE revoked_at IS NULL;
ALTER TABLE communication.device_registration ENABLE ROW LEVEL SECURITY;
ALTER TABLE communication.device_registration FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON communication.device_registration
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
COMMENT ON COLUMN communication.device_registration.token_enc IS 'The push token, encrypted by field protection (ADR-018). Never logged.';

CREATE TABLE communication.callback_endpoint (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  row_version                bigint NOT NULL,
  created_at                 timestamptz NOT NULL,
  created_by                 varchar(80) NOT NULL,
  updated_at                 timestamptz NOT NULL,
  updated_by                 varchar(80) NOT NULL,
  provider_code              varchar(20) NOT NULL,
  key_hash                   char(64) NOT NULL,
  secret_enc                 bytea NOT NULL,
  status                     varchar(10) NOT NULL,
  require_client_token       boolean NOT NULL,
  issued_at                  timestamptz NOT NULL,
  issued_by                  varchar(80) NOT NULL,
  retires_at                 timestamptz,
  revoked_at                 timestamptz,
  revoked_by                 varchar(80),
  CONSTRAINT pk_callback_endpoint PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_callback_endpoint_key UNIQUE (key_hash),
  CONSTRAINT ck_callback_endpoint_status CHECK (status IN ('ACTIVE', 'RETIRING', 'REVOKED')),
  CONSTRAINT ck_callback_endpoint_retiring CHECK (status <> 'RETIRING' OR retires_at IS NOT NULL),
  CONSTRAINT ck_callback_endpoint_revoked CHECK ((status = 'REVOKED') = (revoked_at IS NOT NULL))
);
COMMENT ON COLUMN communication.callback_endpoint.key_hash IS 'SHA-256 of the endpoint key, hex. The key itself is never stored.';
COMMENT ON COLUMN communication.callback_endpoint.secret_enc IS 'The HMAC signing secret, encrypted by field protection (ADR-018). Never logged.';
CREATE INDEX ix_callback_endpoint_provider ON communication.callback_endpoint (tenant_id, provider_code, status);
ALTER TABLE communication.callback_endpoint ENABLE ROW LEVEL SECURITY;
ALTER TABLE communication.callback_endpoint FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON communication.callback_endpoint
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);

CREATE TABLE communication.callback_receipt (
  tenant_id                  uuid NOT NULL,
  id                         uuid NOT NULL,
  endpoint_id                uuid NOT NULL,
  callback_id                varchar(100) NOT NULL,
  received_at                timestamptz NOT NULL,
  CONSTRAINT pk_callback_receipt PRIMARY KEY (tenant_id, id),
  CONSTRAINT uq_callback_receipt UNIQUE (tenant_id, endpoint_id, callback_id)
);
CREATE INDEX ix_callback_receipt_received ON communication.callback_receipt (tenant_id, endpoint_id, received_at);
ALTER TABLE communication.callback_receipt ENABLE ROW LEVEL SECURITY;
ALTER TABLE communication.callback_receipt FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON communication.callback_receipt
  USING (tenant_id = current_setting('app.tenant_id')::uuid) WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);

-- The tenant and endpoint for a key hash, as payment.resolve_callback_endpoint (V0033).
CREATE OR REPLACE FUNCTION communication.resolve_callback_endpoint(p_key_hash char(64))
RETURNS TABLE (tenant_id uuid, endpoint_id uuid)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER
SET search_path = pg_catalog, pg_temp AS $$
DECLARE
  t uuid;
  previous text := current_setting('app.tenant_id', true);
BEGIN
  FOR t IN SELECT id FROM tenant.tenant LOOP
    PERFORM set_config('app.tenant_id', t::text, true);
    RETURN QUERY
      SELECT e.tenant_id, e.id FROM communication.callback_endpoint e
      WHERE e.key_hash = p_key_hash
        AND (e.status = 'ACTIVE' OR (e.status = 'RETIRING' AND e.retires_at > now()));
    EXIT WHEN FOUND;
  END LOOP;
  PERFORM set_config('app.tenant_id', coalesce(previous, ''), true);
END $$;
REVOKE ALL ON FUNCTION communication.resolve_callback_endpoint(char) FROM PUBLIC;
-- Grants: db/roles/module_grants.sql gives insys_communication EXECUTE on it, and DML on the schema.

ALTER TABLE general.cfg_renewal_rules ADD COLUMN reminder_days integer[];
COMMENT ON COLUMN general.cfg_renewal_rules.reminder_days IS 'Days before the period ends on which a renewal reminder is sent while the policy has not renewed (OQ-089). Empty: none.';
ALTER TABLE general.cfg_aggregation_zones_and_limits ADD COLUMN alert_role varchar(30);
COMMENT ON COLUMN general.cfg_aggregation_zones_and_limits.alert_role IS 'The role whose queue reviews this limit''s alerts (OQ-085); null uses General:Aggregation:AlertRole.';
