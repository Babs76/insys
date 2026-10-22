# ADR-013: Payee bank accounts are Party records, referenced by id

- **Status:** Proposed (Phase 5, item 1 of the Phase 4 follow-ups)
- **Date:** 22 October 2026
- **Requirements:** PAY-001, PAY-003, CLM-004, SRS-002, IAM-006; a gap in the original specification (no table held bank details)

## Context
Settlements, refunds and payouts pay a party into a bank account, but no table held bank details. Up to Phase 4:
- raw details (bank code, account number, holder) travelled in the HTTP bodies of the General settlement request and the Claims draft and retry requests;
- the handlers hashed them for the approval subject (ADR-009) and stored or published only the hash;
- Payments had no account at all: the SIMULATOR provider received no bank details, so a real provider adapter had nothing to send.

So the details reached no provider, and an operator had to retype them on every request.

## Decision
- **`party.bank_account` (V0018)** belongs to the Party module and its role:
  - the account number is encrypted by the field protector (AES-256-GCM, `account_number_enc`), with a keyed hash for matching and the last four digits for display;
  - the bank and branch code, holder name, account type and currency are kept in clear;
  - the status is PENDING_VERIFICATION, ACTIVE or SUPERSEDED. ACTIVE needs a verification reference, the verifier and the time.
- **A change is a new row.** Adding an account supersedes the party's current account in the same currency (`superseded_by_id`), in one transaction. A deferred exclusion constraint keeps one current account per party and currency. Adding and verifying are audited (`AddBankAccount`, `VerifyBankAccount`).
- **References by id only.** Settlements (`general.claim.settlement_bank_account_id`), payment instructions (`bank_account_id`, `pending_bank_account_id`), refunds (`billing.refund_request.bank_account_id`) and payments (`payment.payment.bank_account_id`) hold the account id. Approval subjects hash the id and the account number's keyed hash. No request, event, approval or log carries an account number; `BankAccountView` and `PayeeAccountDetails.ToString()` show the last four digits only.
- **Checks when a payout is requested.** The requesting module reads `IBankAccounts` (Party contracts): the account must be ACTIVE, belong to the payee and be in the payout's currency. Otherwise `BANK_ACCOUNT_NOT_PAYABLE` or `REFUND_ACCOUNT_NOT_PAYABLE`.
- **Decryption only at send time.** `PaymentStarter` reads `IBankAccountDetails.ForPayment` immediately before calling the gateway, audits the read (`ReadBankAccountForPayment`) and passes the details to `IPaymentGateway.Send`. It returns null for an account that is no longer ACTIVE.
- **A change after approval needs a fresh approval.** A payment whose account has been superseded since approval fails without reaching the provider ("the payee's bank account changed after approval"). The instruction returns to Failed. A retry to the same account is refused (`PAYEE_BANK_ACCOUNT_CHANGED`). A retry or redirect to the new account raises a new Workflow approval (`CLAIM_PAYMENT`), and the payment is released only when the approval's subject hash matches.

## Consequences
- Provider adapters receive the decrypted details through `Send`; the simulator ignores them.
- Verification is recorded, not performed: how an account is verified (a bank account verification service, a document) is a tenant process. `TODO(spec)`: no verification provider is specified.
- Payouts of the Life and Health modules (claims, maturities, provider payment runs) use the same references.
