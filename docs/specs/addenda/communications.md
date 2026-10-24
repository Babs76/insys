# Addendum: notifications and communications (Wave 1: F021, F031, F078, F095–F102)

**Signed off 25 October 2026** (Ade), with these decisions:
- **OQ-086:** the claim statuses of §6.3 notify, with amounts only on settlement (Health as a total only). For compliance to confirm: whether a decline notice must give the reasons and the policyholder's rights under the Policyholder Protection Rules.
- **OQ-087:** as proposed.
  - Service notices keep at least one channel.
  - The section 69(3) customer exception is not used.
  - Form 4, CPA section 11 and ECTA section 45 go to compliance to confirm.
- **OQ-088:** stubs only. Providers are chosen later, and push stays stubbed until a portal app exists.
- **OQ-089:**
  - `general.cfg_renewal_rules` gains `reminder_days`, a list that is empty by default, so reminders are sent only when a tenant configures them.
  - No pre-due premium reminders in Wave 1.
  - Health and Life scheme renewals stay out until specified.
- **OQ-090:** `communication.ref_channels` is the shared channel list. Other modules are aligned when they are next changed.
- **§5.1:** the ADR-023 callback signature check and endpoint lifecycle move into the building blocks.
- **Access:** permissions as today. The function catalogue (proposed next) maps Communications' operations with the rest.

- **Basis:**
  - SRS §20.2: "Communications Email, SMS, approved messaging providers".
  - SRS §7.6 (versioned templates for renewal notices, claims correspondence and premium notices).
  - SRS §6.1 items 4 and 8 (contact information; consent and communication preferences).
  - SRS §12.1 (claims communications), §13.1 (arrears management), §18.1–18.2 (document delivery; remittance advice).
  - PTY-005 (personal information by authorised purpose), HLT-008 (medical information by role and minimum necessary access).
  - INT-005 to INT-007 (duplicate-tolerant consumers; controlled retry; identifiers only in events).
  - ADR-022 (park on missing configuration), ADR-023 (provider callbacks authenticate the provider).
  - `addenda/localisation.md` §5–§7 (the language chain), `addenda/external-user-access.md` (portal relationship rules), `addenda/access-scopes.md` (F103).
  - POPIA section 69 and its definitions in section 1 (§7 below, **for compliance to confirm**).
- **Built today:**
  - Documents delivers a generated document through `IDocumentDelivery`, which is a stub. Each attempt is recorded in `document.delivery`, with `SENT` or `FAILED` and the provider reference.
  - Party keeps contact points (a primary per type), an append-only consent ledger per purpose (`party.cfg_consent_purposes`), and an individual's preferred language.
  - Claims Framework records claim communications (`claims.claim_communication`). Nothing sends one.
  - The renewal job raises `GeneralRenewalInvited`, and Documents generates the invitation. Nothing delivers it.
  - Billing ages arrears and stores the bucket's action (`REMINDER`, `FINAL_NOTICE` …) on the arrears case. Nothing acts on it.
  - The only real outbound email is Keycloak's invitation.
- **What the specifications do not say:**
  - The SRS lists "Notifications" as a Phase 2 module (§27) with no module section, submodules or requirement IDs. `presentation.html` records this.
  - F097 (push) and F098 (WhatsApp) have no basis beyond "approved messaging providers".
  - F101 (claim status) is "Not specified".
  - What follows is therefore a proposal. The open points are OQ-086 to OQ-090.

## 1. One notifications capability, in a new Communications module
- **The module:** `Insys.Communications`, schema `communication`. It owns:
  - notification types and their templates;
  - channels and their providers;
  - per-party preferences;
  - every notification and every delivery attempt;
  - provider delivery callbacks.
- **Other modules ask for a notification; they never send one.** Two ways:
  - Communications consumes their integration events (§6).
  - A module publishes `communication.NotificationRequested {notificationType, recipientPartyId, sourceType, sourceId, productCode?, orgNodeCode?, mergeValues}`.
- **Events carry identifiers only (INT-007).** Communications resolves the recipient's contact details and language itself, when it sends, through Party's public queries. Merge values are business references (a claim number, a due date, an amount), never contact details, ID numbers or clinical data.
- **Documents:** later, `IDocumentDelivery` is implemented by Communications' email channel, so a document is sent as an attachment or a portal link through the same tracking. Until then Documents keeps its stub. This is not in Wave 1's first build.

## 2. Notification types and templates
- **Notification types** (new tenant static data, `communication.cfg_notification_types`):
  - `type_code`, label;
  - `category`: `SERVICE` or `MARKETING`;
  - `consent_purpose`: required for `MARKETING`, a `party.cfg_consent_purposes` code; none for `SERVICE`;
  - `channels`: the channels allowed, in default order (`EMAIL`, `SMS`, `PUSH`, `WHATSAPP`);
  - `portal_visible`: whether a portal user sees it in their notification list (§8).
- **Proposed service types,** with the trigger each one has (§6):

  | Type | Trigger |
  |---|---|
  | `PAYMENT_REMINDER` | Arrears reach a bucket with action `REMINDER` |
  | `PAYMENT_FINAL_NOTICE` | Arrears reach a bucket with action `FINAL_NOTICE` |
  | `RENEWAL_INVITATION` | `GeneralRenewalInvited` |
  | `RENEWAL_REMINDER` | A renewal reminder falls due, if OQ-089 gives a schedule |
  | `CLAIM_STATUS` | A claim's status changes (§6.3) |
  | `HEALTH_CLAIM_DECISION` | A medical claim is decided (F031, the member's notice) |
  | `PREAUTH_DECISION` | A preauthorisation is approved or rejected |

  Marketing types are the tenant's own. None is seeded.
- **Templates** (new tenant static data, `communication.cfg_notification_templates`):
  - `type_code`, `channel`, `language`, version;
  - `subject` (email and push);
  - `body`, with named merge fields from the type's allowed list.
  - Versioned and approved like other static data (SDM-002).
- **Language:** the chain of `localisation.md` §5–§6 picks the version: the party's preferred language, then the tenant default, then English. An organisation uses the tenant default.
- **What a template may contain:**
  - A template may use only its type's allowed merge fields. Unknown fields are refused when the template is saved.
  - No type offers clinical data, ID numbers, bank details or another party's details.
  - Health types offer the claim or authorisation number, status, date and a "see the portal" link, never diagnosis codes, procedures or amounts per line (HLT-008, POPIA section 26).

## 3. Channels behind adapters
- **The interface:** each channel is an adapter behind one interface, defined in the module's application layer before any adapter is built.
  - `INotificationChannel { Channel; Task<ChannelReceipt> Send(OutboundMessage, CancellationToken) }`
  - `ChannelReceipt(Accepted, ProviderRef?, FailureCode?, Retryable)`
  - `OutboundMessage` carries the address, rendered subject and body, sender identity and correlation ID, and nothing else.
- **Channels:** `EMAIL`, `SMS`, `PUSH` and `WHATSAPP`, each with a stub in Wave 1. A stub records the message and answers `Accepted` with a `STUB-…` reference, plus a test switch that fails it. Real providers are chosen per tenant later (OQ-088).
- **Providers** (new tenant static data, `communication.cfg_channel_providers`): `channel`, `provider_code` (a Partner), `sender_identity`, `enabled`.
  - A channel with no enabled provider is not configured (§9).
  - This replaces nothing yet. `document.cfg_delivery_channel_settings` stays for Documents' own delivery.
- **Addresses:**
  - Email and SMS go to the party's primary contact point of type `EMAIL` and `MOBILE`.
  - WhatsApp goes to the primary `MOBILE`, only where the party has opted into WhatsApp (§4).
  - Push goes to a device registered by a portal app for a portal user linked to the party (`communication.device_registration`). No portal app exists yet (OQ-088), so push is built and stubbed, with no device in practice.
- **Contact data:** Party gains a public query `IPartyContacts.Primary(partyId, channelType)`. The value is read when sending and is not copied into the notification. The attempt records a masked address (`t***@example.com`, `+27*****123`).

## 4. Preferences and consent
- **Per party, per notification type** (`communication.notification_preference`):
  - the channels the party wants, in order; or
  - `OPTED_OUT`, for a marketing type only.
- **Who sets them:** staff with `notification_preference.update`, or the party through the portal.
- **With no preference,** the type's default order applies.
- **Service notices:**
  - They are sent without marketing consent: they are needed to perform the contract.
  - A party may choose the channels, but not opt out of every channel. At least one stays, the first of the type's default order (OQ-087).
- **Marketing is never sent without consent:**
  - When it sends, Communications checks that the party's current consent record for the type's `consent_purpose` is granted (Party's `IPartyConsent.Current`, a new public query). The check runs at sending, not only at queueing.
  - No consent, or consent withdrawn, means the notification ends as `SUPPRESSED_NO_CONSENT`. Nothing is sent, and it is not parked.
  - `party.ConsentWithdrawn` suppresses any queued marketing for that purpose at once.
  - The customer exception of section 69(3) is **not** used: consent is required even from customers, until compliance decides otherwise (OQ-087).
- **What every marketing message carries** (POPIA s69(4)): the sender's identity and an address where the recipient can ask for such messages to stop. Templates of a marketing type are refused without both fields, `{sender_identity}` and `{opt_out_contact}`.
- **Opting out** is free, and is recorded both as the consent ledger's withdrawal and as `OPTED_OUT`.

## 5. Notifications and delivery tracking
- **A notification** (`communication.notification`) is one message for one recipient. It records:
  - the type;
  - the recipient party;
  - the source record (`source_type`, `source_id`), its product and organisation node (for F103);
  - the merge values;
  - the language and template version used;
  - the status.
- **Idempotent:** the key is the source event ID, the type and the recipient. A redelivered event creates nothing (INT-005).
- **Each attempt** (`communication.delivery_attempt`) records:
  - the channel, provider and provider reference;
  - the masked address;
  - the status, one of `QUEUED`, `SENT`, `DELIVERED`, `FAILED`, `BOUNCED`, `UNDELIVERABLE`;
  - the failure code and the timestamps.
- **Statuses:**
  - A notification is `QUEUED`, then `SENT` when a channel accepts it.
  - It is `DELIVERED` when the provider confirms.
  - It is `FAILED` when every allowed channel failed after retries.
  - It is `UNDELIVERABLE` when the party has no address for any allowed channel. This is a data gap on the party, not configuration, so it is listed but not parked.
  - Marketing can also end as `SUPPRESSED_NO_CONSENT`.
- **Fallback:** a channel failure that is not retryable, or retryable but exhausted (three attempts with back-off), moves to the next channel in the party's order.
- **Events:** `communication.NotificationSent`, `communication.NotificationDelivered`, `communication.NotificationFailed`, with identifiers and status only.
- **Claims:** for a claim notification, Claims Framework records a `claims.claim_communication` row (direction `OUT`) from `NotificationSent`, so the claim's correspondence log includes it.

### 5.1 Provider delivery callbacks (ADR-023)
- **The address:** each provider that reports delivery calls `POST /api/v1/communications/callbacks/{endpointKey}`.
  - The endpoint key in the path finds the tenant and provider.
  - An HMAC-SHA256 signature over `{timestamp}.{callbackId}.{body}` authenticates it, with a replay window.
  - This is ADR-023's pattern, as payment callbacks use it.
- **The endpoints** (`communication.callback_endpoint`) are issued, rotated and revoked as payment endpoints are.
  - The key is shown once and stored only as a hash; the secret is encrypted.
  - Proposal: move the signature check and endpoint lifecycle that Payments has into the building blocks, so both modules use one implementation.
- **A callback** matches an attempt by provider reference and moves it to `DELIVERED`, `BOUNCED` or `FAILED`. An unknown reference is logged and acknowledged, so the provider does not retry forever. A repeated callback is a no-op.

## 6. What sends a notification
These consumers are event-driven, one per trigger, and idempotent.

### 6.1 Payment reminders (F099)
- **Trigger:** arrears reach a bucket whose action is `REMINDER` or `FINAL_NOTICE`.
- **Recipient:** the billing account's payer.
- **Merge fields:** invoice and policy references, amount overdue, currency, days overdue.
- **Event change (Billing):** `ArrearsDetected` carries neither the payer nor the action. It gains `PayerPartyId` and `Action` (additive), plus `ProductCode` and `OrgNodeCode` where the account has them.
- **Not proposed:** a reminder before the due date. No configuration specifies one (OQ-089).

### 6.2 Renewal notices and reminders (F021, F100)
- **The invitation:** `GeneralRenewalInvited` already carries `PolicyholderPartyId` and the template. It sends `RENEWAL_INVITATION` to the policyholder, with a link to the invitation document.
- **Reminders (OQ-089, decided):** `general.cfg_renewal_rules.reminder_days` lists the days before the period ends on which `RENEWAL_REMINDER` is sent, while the policy has not renewed. It is empty by default, so none is sent until the tenant sets it.
- **Health and Life schemes:** these renewals have no template or recipient rule. They are left out until specified.

### 6.3 Claim status (F101) and Health decisions (F031)
- **Claim statuses that notify** (proposed; OQ-086 confirms):

  | Line | Statuses |
  |---|---|
  | Life | Registered, Approved, Declined, Settled |
  | General | Registered, Approved, Declined, Settled, Closed |
  | Health (the member) | Adjudicated (paid, part-paid or pended), Reversed, preauthorisation approved or rejected |

- **Recipient:** the claimant, or the policyholder where there is no claimant.
  - Health: the patient. For a dependant who is a minor, the principal (OQ-084's rule).
- **Gaps this needs:**
  - General has no `GeneralClaimDeclined` event, though the domain model has the transition.
  - Health has no event for a rejected claim.
  - Claim events carry no claimant party, so each line gains a public query from claim to claimant and policyholder.
  - These are additive changes in General, Life and Health.
- **Content:** a claim notice says the claim number, its new status and where to see more. Amounts appear only for settlement, and Health notices only as a total.

## 7. POPIA: direct marketing by electronic communication (**for compliance to confirm**)
Checked on 25 October 2026 against the Act's text (popia.co.za, sections 1 and 69). This summary is not legal advice; compliance confirms it before the build relies on it.

- **Definitions (section 1):**
  - "Direct marketing" is approaching a data subject in person, by mail or by electronic communication, to promote or offer goods or services, or to ask for a donation.
  - An "electronic communication" is a text, voice, sound or image message sent over an electronic communications network and stored until the recipient collects it. On that reading, email, SMS, WhatsApp and push messages are all electronic communications.
- **Section 69(1):** processing personal information for direct marketing by unsolicited electronic communication is prohibited unless the data subject has consented, or is the responsible party's customer under 69(3).
- **Section 69(2):** a responsible party may ask a data subject for that consent **only once**, and only if they have not already withheld it. The request must be in the prescribed manner and form, which we understand to be Form 4 of the POPIA Regulations, 2018 (to confirm).
- **Section 69(3), the customer exception,** has three conditions:
  - the contact details were obtained in the sale of a product or service;
  - the marketing is of the responsible party's own similar products or services;
  - the customer was given a free and simple chance to object, when the details were collected and with every message.
- **Section 69(4):** every direct marketing message must contain the sender's identity, and an address or other contact details for a request that such messages stop.
- **How this design applies it:**
  - Marketing types need a granted consent purpose at sending. The customer exception is not used (§4).
  - Marketing templates must carry the sender identity and the opt-out contact.
  - Opting out is free and takes effect at once.
  - Service notices are not direct marketing: they do not promote or offer anything. Their templates must not include promotional content, and compliance reviews them as static data under maker-checker.
- **The one-time request:** the system does not send consent requests itself. A tenant that asks for consent by electronic message records the request against the party (`party.consent_record` with `granted = false`), so it is not asked twice.
- **Other law to confirm:** the Consumer Protection Act section 11 (the right to refuse direct marketing, and the opt-out registry) and ECTA section 45 (unsolicited commercial communications). Neither was checked for this addendum.

## 8. Portal users
- **The list:** a portal user lists the notifications addressed to the party, intermediary or provider they are linked to (`iam.user_subject_link`, external-user access §1).
  - Only the types with `portal_visible`, newest first.
  - Read with `GET /api/v1/portal/communications/notifications` and `…/{id}`.
  - Another party's notification is not found (404), as other portal reads are.
- **Broker:** a broker sees notifications addressed to the intermediary itself (for example a commission statement notice), not their clients' notifications. A client's notice is the client's.
- **What it shows:** the rendered subject and body as sent, the channel and the status. It does not show the address.
- **Preferences:** a portal user may read and change their own preferences (§4) at `/portal/communications/preferences`.
- **Push devices:** a portal app registers a device for the signed-in user at `/portal/communications/devices`.

## 9. Missing configuration parks (ADR-022)
- **These park** under prerequisite codes:
  - `NOTIFICATION_TYPE_NOT_CONFIGURED`: a trigger fires for a type the tenant has not configured;
  - `NOTIFICATION_TEMPLATE_NOT_IN_FORCE`: no template in force for the chosen channel in any language of the chain;
  - `CHANNEL_PROVIDER_NOT_CONFIGURED`: no enabled provider for any channel the party's order allows.
- **Retry:** parked notifications are retried when the static data is activated (`StaticDataVersionActivated`) or on request. They escalate to the configuration owner role, as ADR-022 decision 6 says.
- **Not configuration:** a party with no address and a marketing type without consent end as `UNDELIVERABLE` and `SUPPRESSED_NO_CONSENT`. They are not parked.

## 10. Access, scope and personal data
- **Permissions:**
  - `notification.read`;
  - `notification_preference.read` and `notification_preference.update`;
  - `notification_device.update` (portal);
  - `communication_callback_endpoint.read` and `communication_callback_endpoint.update`.
- **Staff reads** are narrowed by F103: a notification is in scope when its source record's product and organisation node are, as recorded on the notification.
- **Tenant isolation:** every table has row-level security by `tenant_id`. The callback resolves the tenant only from the endpoint key, as ADR-023 does.
- **Personal data:**
  - Events and logs carry identifiers only. Logs mask addresses and never contain a body.
  - The rendered body is stored on the notification as evidence of what was sent, and kept for the audit retention period (`audit.cfg_audit_retention_policies`). Anonymising a party (`PartyAnonymised`) blanks the bodies and masked addresses of its notifications.

## 11. API (summary)

| Endpoint | Permission |
|---|---|
| `GET /communications/notifications?partyId&type&status&sourceType&sourceId` (cursor), `GET …/{id}` with its attempts | `notification.read` |
| `GET` and `PUT /communications/parties/{partyId}/preferences` (If-Match) | `notification_preference.read`, `.update` |
| `GET` and `POST /communications/callback-endpoints`, `…/{id}/revoke` | `communication_callback_endpoint.*` |
| `POST /communications/callbacks/{endpointKey}` | signed provider callback |
| `GET /portal/communications/notifications`, `…/{id}`; `GET` and `PUT /portal/communications/preferences`; `POST` and `DELETE /portal/communications/devices` | portal, relationship-scoped |

Every create takes `Idempotency-Key`, and every update takes `If-Match`.

## 12. How it is built
- **One migration per batch,** allocated by the batch owner. It creates the static data tables of §2–§3, and `notification`, `delivery_attempt`, `notification_preference`, `device_registration` and `callback_endpoint`.
- **Order:**
  1. The batch owner builds the module, its contracts (`INotificationChannel`, the events, `NotificationRequested`), the migrations, the Party queries and the parking codes.
  2. One agent, in its own worktree, builds the four channel adapters and stubs against the interface.
  3. A second agent builds the event consumers of §6 once the contracts exist.
  4. The event changes in Billing, General, Life and Health are made by the batch owner.
- **Tests:**
  - preferences, consent and suppression;
  - the language chain and parking;
  - fallback between channels;
  - callbacks: signature, replay and an unknown reference;
  - portal scope: another party's notification is 404;
  - each trigger end to end through the stubs.

## 13. Open points
- **OQ-086** Which statuses of which claims notify, and whether an amount appears (§6.3).
- **OQ-087:**
  - whether service notices may be fully opted out;
  - whether the section 69(3) customer exception is used;
  - Form 4 for consent requests;
  - CPA section 11 and ECTA section 45 (§4, §7).
- **OQ-088** The SMS, WhatsApp, email and push providers, and whether a portal app will register devices (§3).
- **OQ-089** Reminder schedules: before the due date for premiums, and between invitation and renewal for General policies; Health and Life scheme renewals (§6.1–6.2).
- **OQ-090** Channel codes:
  - Documents (`POST`, `PORTAL`), Life lead times and remittance formats each use their own channel lists, and claim communications use free text.
  - Proposal: Communications' four codes become a shared reference list (`communication.ref_channels`), and the others are aligned when their modules are next changed.
