# Addendum: channel transaction permissions (F005)

**Proposed, pending business sign-off** (ADR-019). Recorded as OQ-075. Nothing in it is built yet.

- **Basis:** static data
  - `channel.cfg_channel_definitions`: `channel_code`, `audience` (Staff, Broker, Customer, Provider, Partner), `enabled_products`, `enabled_transactions`;
  - `channel.cfg_channel_transaction_permissions`: channel, role, `transaction_type`, product (empty = all), `allowed`, `requires_approval`.

  Domain model: a Life proposal's product must be available on the proposal's channel (§7.1, PRD-007).
- **What the code does today:** neither table is read. A Life proposal stores the `channelCode` the caller sends, unchecked. Nothing else has a channel.
- **What is undefined:**
  - how the platform knows a request's channel;
  - what a `transaction_type` is;
  - what `requires_approval` means for a transaction that has no approval flow.

## 1. A request's channel comes from the authenticated client, never from the request body
A channel claimed in the body is the caller's own say-so: a back-office user could claim a channel with wider permissions. The channel is taken from the token's OAuth client (`azp`, for example `insys-web` or a partner's client).

**Schema change:** this needs a mapping from client to channel, which no static data holds. The proposal adds `client_ids` (a list) to `channel.cfg_channel_definitions`, by migration, with the static data catalogue updated to match.

A request whose client maps to no channel is refused for channel-scoped transactions (403 `CHANNEL_NOT_ENABLED`). A tenant with no channel definitions has no channel scoping, so behaviour is unchanged until it configures them.

## 2. Transaction types
A transaction type is the permission code of the command (`general_quote.create`, `life_proposal.create`, `general_claim.create`, and so on), so no second vocabulary is needed. `enabled_transactions` and `transaction_type` hold permission codes. The channel-scoped transactions are the domain commands that create or change a customer's business: quotes, proposals, policies and endorsements, claims, and payments out. Administration and static data are not channel-scoped.

## 3. Enforcement
For a channel-scoped command, after the permission check:
1. The channel must be in force and enabled. The product must be in `enabled_products` (empty = all), and the transaction in `enabled_transactions` (empty = all).
2. The caller's roles × the channel × the transaction × the product must have a permission row with `allowed = true`. No row means not allowed.
3. `requires_approval = true` sends the transaction to Workflow maker-checker where the command has an approval flow (endorsements, claim payments). For a command with none, the transaction is refused with `CHANNEL_APPROVAL_REQUIRED` until one is defined.

The channel is recorded on the record the command creates: Life proposals already have `channel_code`, and other domains gain one by migration.

## Consequences
- **Migrations:** `client_ids` on channel definitions; `channel_code` on General quotes and policies, claims, and Health memberships.
- **A building-block check:** `IChannelPolicy`, read from the Integration module's channel tables through its contract. It runs as an endpoint filter on channel-scoped commands.
- **The front end** sends nothing new: its client is its channel.
