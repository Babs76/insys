# Wave 2 batch 6: plan, for Ade's approval

Batch 6 opens with the broker portal's quoting (group A, added by Ade on 28 October 2026), then the member's loss reporting (group B), then phase 4: money (payments, billing, distribution, finance and reinsurance). It is built on `wave2-6`, branched from main at `99ac395` (batch 5 merged). The request texts are in the console's `docs/backend-requests.md`.

## Scope, in build order

| # | Item | What gets built | Proof (tests) | Size |
|---|---|---|---|---|
| 0 | **BR-095** a broker's portal quote is priced (group A) | A portal quote is rated as it is made, so the broker sees a premium instead of a Draft with no price. **Decision 1** chooses how. | A broker's portal quote reads Rated with its premium; it is rated under the broker's own scope only; a quote the rules refer stays Referred, with no price shown; the endpoint sweeps cover any new operation. | Medium |
| 1 | **BR-092** a broker registers and lists the insured item it quotes for (group A) | `POST /portal/general/risk-objects` registers a risk object (type, attributes, valuation with its currency, location) for a client the broker services, and `GET /portal/general/risk-objects?clientPartyId=` lists a client's. **Decision 2** chooses how a risk object is tied to a client. | A broker registers an item and quotes on it; it lists its own clients' items only; another broker's items never appear in the list or by ID, nor do another tenant's; `OperatorTenantTests`, `FunctionAccessTests` and the sweeps cover the new operations. | Medium |
| 2 | **BR-093** the causes and perils a member can report a loss under (group B) | `GET /portal/general/policies/{id}/loss-codes`: the loss causes and perils in force for the policy's product, on the portal channel, each with its code and label. It is scoped as the member's policy read (404 outside it). | The member's policy lists the codes in force; a code retired from the product is gone; another member's policy is 404. | Small |
| 3 | **BR-050** finding payments | `GET /payments/payments` by reference, status (including Unknown) and date range, with a cursor, without a business record. | Each filter; paging returns each payment once; scope. | Small |
| 4 | **BR-051** payout batches | `GET /payments/payout-batches` (status, purpose, currency, cursor); the batch's approval ID and build date on its view. | The list and filters; the fields after a batch is built and approved. | Small |
| 5 | **BR-052** a reconciliation exception's read | `GET /payments/reconciliation-exceptions/{id}`; currency, the statement line, raised and resolved dates; `exceptionType`'s values declared. | The read; the contract declaration. | Small |
| 6 | **BR-053** the payment's missing details | `initiatedAt`, counterparty, bank account ID and failure reason on the payment view; the allocation's rule; the status history's `source` values; a read of one callback endpoint and If-Match on revoking it; ETags on the payment reads. | Each field on the money flow test; a stale revoke is 412. | Medium |
| 7 | **BR-054** billing accounts | `GET /billing/accounts` (payer, policy, status, cursor). | The list and filters, in scope. | Small |
| 8 | **BR-055** refunds | `GET /billing/refunds` (account, status); the account names its refunds; the refund's approval ID. | The list; the fields after a refund is approved. | Small |
| 9 | **BR-056** billing's codes and arrears currency | Billing's code fields declare their values or table (as BR-036 did for General); `BillingArrearsView.currency`. | A contract test like BR-036's over billing's schemas. | Small |
| 10 | **BR-058** commission statements | `GET /distribution/statements` (intermediary, period, status, cursor); the statement's approval ID. | The list and filters; the field. | Small |
| 11 | **BR-059** a statement's entitlements; the rate's basis | `GET /distribution/entitlements?statementId=`; the entitlement says whether `rate` is a percentage or an amount. | The filter; the field on a percentage and on an amount. | Small |
| 12 | **BR-043** journals | `GET /finance/journals` by journal number, period and accounting date, with a cursor, without a business record; the reversing journal's ID on a reversed journal; the base amount's currency. | Each filter; a reversed journal names its reversal. | Small |
| 13 | **BR-044** posting exceptions | A cursor on the list; a read of one; the failed event's amount, accounting date and business record. | Paging; the read and fields. | Small |
| 14 | **BR-045** accounting periods | `GET /finance/periods/{periodCode}`; the calendar's periods not acted on listed as Future; `reopenRequestedAt` and the reopen approval's ID. | The read; a future period listed; the reopen fields. | Small |
| 15 | **BR-046** GL export sent | `sentAt` and `sentBy` on the GL export view. | The fields after sending. | Small |
| 16 | **BR-047** bordereaux | `GET /reinsurance/bordereaux` (treaty, period, status, cursor); a file name for the CSV. | The list; the download's file name. | Small |
| 17 | **BR-048** cessions | `GET /reinsurance/cessions` by treaty or period; `sourceDomain`'s values declared. | The filters; the declaration. | Small |
| 18 | **BR-049** reinsurance recoveries | A cursor on the list; who billed and who settled. | Paging; the fields. | Small |
| 19 | **Riders (group C):** BR-041, BR-060 | BR-041: the GL export read declares its ETag in the contract. BR-060: an answer; see decision 3. | The contract declaration. | Small |

## Decisions (Ade, 28 October 2026)

1. **BR-095: option (a), priced as the quote is made.** Every new quote and every new quote version is rated through an event Rating consumes. A quote missing rating inputs stays Draft with the reason; a quote the rules refer stays Referred with no price; the broker can never change the price. `PortalGeneralCreateQuote` declares the eventual price in `x-eventual-effects`.
2. **BR-092: option (a), an owner party.** A migration adds `owner_party_id` to `general.risk_object`, backfilled only where an item sits solely on policies of one policyholder; the rest stay empty, not guessed. A broker sees items owned by clients it currently services, and loses them when the client's intermediary changes. A staff change of owner is audited. Tests cover the broker's visibility and the loss of access on an intermediary change.
3. **BR-060: no new reads.** The static data screens are the place for the intermediary register.

The options set out before the decisions:

1. **BR-095: how a portal quote is rated.**
   - **(a) Recommended: rated as it is made.** General asks Rating to calculate when a portal quote is created, through an event Rating consumes (as other cross-module work runs), so the quote reads Rated shortly after. `PortalGeneralCreateQuote` declares the eventual effect, as `GeneralCreateQuote` does. The broker needs no new permission.
   - **(b) A broker rating operation:** `POST /portal/general/quotes/{id}/rate`, scoped to the broker's own quotes. The broker controls when; one more operation and function.
   - Either way, the staff flow is unchanged: staff still request the calculation themselves.
2. **BR-092: how a risk object belongs to a client.** A risk object has no owner today (`general.risk_object` holds no party).
   - **(a) Recommended: an owner party.** A migration adds `owner_party_id` (nullable for existing items). A portal registration sets it to the client, and a broker lists items owned by clients it services. Staff may set it too.
   - **(b) No owner:** a broker sees only the items on its own quotes and policies, plus those it registered. Simpler, but a client's items registered by the insurer stay invisible to the broker until quoted.
   - **(c) Inline:** the quote takes the item's details and registers it. No list. This is the front end's fallback; it doesn't let the broker reuse an item.
3. **BR-060: the intermediary register.** Recommended: the static data screens are the place for staff (they already read intermediaries, appointments and product permissions), so no new reads; confirm, or say which reads staff need.

## How the batch runs

- **Branch:** `wave2-6` from main at `99ac395`.
- **Order:** group A first (items 0 and 1), then BR-093, then phase 4 by module.
- **Shipping:** if the portal launch needs group A before the rest, items 0 and 1 can ship to main alone, as BR-091 and BR-094 did.
- **Gate:** CI green on the exact head is the gate. The full local suite runs only on a quiet machine, otherwise targeted tests (always `OperatorTenantTests` and `FunctionAccessTests` for new operations). Any local failure CI doesn't reproduce is investigated and noted.
- **Report:** the report gives the scope table with every job's counts and shard times.
