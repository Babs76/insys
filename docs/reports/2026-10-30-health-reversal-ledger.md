# Defect: a reversed Health claim was not posted to the ledger

Branch `fix-health-reversal`, on `main` at `9d1cc89`. Found while writing the Wave 5a addendum (gap 1). Ade asked for it first, ahead of 5a, on 30 October 2026.

**The defect.** Finance posted `MedicalClaimSettled` but ignored `MedicalClaimReversed`, so a reversed claim stayed in the claims-paid balance.

## Which account CLAIM_PAID credits (asked before merging)
**The bank side.** Posting rules are tenant configuration, and the platform seeds none. The rule the end-to-end tests use, as the tenant's accounts:

| Rule | Debit | Credit | Meaning |
|---|---|---|---|
| `CLAIM_PAID` | `2100` claims | `1200` bank | The claim is paid |
| `PAYMENT_RECEIVED` | `1200` bank | `1100` premium receivable | Cash comes in |

`1200` is cash, not a payable:
- OQ-137 calls it "the bank side", and `PAYMENT_RECEIVED` debits the same account.
- Finance posts nothing else when a provider is paid: `PaymentSucceeded` posts only refunds, commission and redress. `MedicalClaimSettled` is raised when the payment succeeds, so `CLAIM_PAID` is the only cash leg.

**So the first version of this fix was wrong**, and its first report with it. It reversed the journal, which would have debited `1200` and shown cash received that never arrived. It was caught before merging. An account a tenant names differently does not change this: the first version would have debited whatever `CLAIM_PAID` credits.

## The fix
A new transaction type, **`CLAIM_PAID_REVERSED`**, posted on the reversal's business date through its own posting rule:

| Rule | Debit | Credit |
|---|---|---|
| `CLAIM_PAID_REVERSED` | `1300` provider overpayment receivable | `2100` claims |

After a paid claim is reversed: `2100` nets to zero, `1300` shows what is owed, and `1200` keeps its credit, because no cash came back. Collecting the money (5a.3) credits `1300`.

- **`recover_from`:** the posting carries `PROVIDER` or `MEMBER`, so a tenant can route a member's overpayment to another account with a rule condition.
- **No rule, or no open period:** the posting waits as a posting exception, as any posting does. It is never dropped.
- **Readiness:** `POSTING_RULES` lists `CLAIM_PAID_REVERSED` for a tenant entitled to HEALTH, and blocks `FINANCIAL_FOUNDATIONS` until it has the rule. **This is new setup for Health tenants, the front end's included.**
- **A limit:** nothing checks that this rule credits the same account `CLAIM_PAID` debits. A tenant that configures another account will not net to zero there. Finance owns that configuration.

**Claims reversed before this fix** have no posting. No tenant is live, so none is back-filled.

**Tests.** The end-to-end Health flow, on every dated clock: after a paid claim is reversed, `2100` nets to zero, `1300` holds the amount, and `1200` still shows the original credit. An integration test covers the posting exception with no rule. Results are in the next report, so this branch's head does not move after CI.
