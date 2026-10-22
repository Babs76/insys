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
  - the status is PENDING_VERIFICATION, ACTIVE, SUPERSEDED or REJECTED. ACTIVE needs the approval (as its verification reference), the checker and the time.
- **A change is a new row.** Adding an account supersedes the party's current account in the same currency (`superseded_by_id`), in one transaction. A deferred exclusion constraint keeps one current account per party and currency. Adding and verifying are audited (`AddBankAccount`, `VerifyBankAccount`).
- **References by id only.** Settlements (`general.claim.settlement_bank_account_id`), payment instructions (`bank_account_id`, `pending_bank_account_id`), refunds (`billing.refund_request.bank_account_id`) and payments (`payment.payment.bank_account_id`) hold the account id. Approval subjects hash the id and the account number's keyed hash. No request, event, approval or log carries an account number; `BankAccountView` and `PayeeAccountDetails.ToString()` show the last four digits only.
- **Verification through Workflow (Phase 5 review, item 1; V0020).** Adding or changing an account raises a BANK_ACCOUNT_VERIFICATION approval. The maker is the user who added it; the checker is a different user with authority for the type (`workflow.cfg_delegation_and_approval_authority`) and the `bank_account.verify` permission, which the approval names (`required_permission`). The subject is the party id, account id and keyed hash only. ApprovalGranted activates the account; ApprovalRejected leaves it REJECTED, never active. A change while approval is pending supersedes the pending account and withdraws its approval (`workflow.ApprovalWithdrawn`, state Withdrawn). There is no other way to verify an account.
- **Checks when a payout is requested.** The requesting module reads `IBankAccounts` (Party contracts): the account must be ACTIVE, belong to the payee and be in the payout's currency. Otherwise `BANK_ACCOUNT_NOT_PAYABLE` or `REFUND_ACCOUNT_NOT_PAYABLE`.
- **Decryption only at send time.** `PaymentStarter` reads `IBankAccountDetails.ForPayment` immediately before calling the gateway, audits the read (`ReadBankAccountForPayment`) and passes the details to `IPaymentGateway.Send`. It returns null for an account that is no longer ACTIVE.
- **A change after approval needs a fresh approval.** A payment whose account has been superseded since approval fails without reaching the provider ("the payee's bank account changed after approval"). The instruction returns to Failed. A retry to the same account is refused (`PAYEE_BANK_ACCOUNT_CHANGED`). A retry or redirect to the new account raises a new Workflow approval (`CLAIM_PAYMENT`), and the payment is released only when the approval's subject hash matches.

## Consequences
- Provider adapters receive the decrypted details through `Send`; the simulator ignores them.
- Verification is a person's decision in Workflow: what they check (a bank account verification service, a document) is a tenant process. `TODO(spec)`: no verification provider is specified.
- Audit entries recorded by event handlers (the verification decision, for example) are published in the handler's transaction, as commands' are.
- Payouts of the Life and Health modules (claims, maturities, provider payment runs) use the same references.
