# Defect: a reversed Health claim was not posted to the ledger

Branch `fix-health-reversal`, on `main` at `9d1cc89`. Found while writing the Wave 5a addendum (gap 1). Ade asked for it first, ahead of 5a, on 30 October 2026.

**The defect.** Finance posted `MedicalClaimSettled` but ignored `MedicalClaimReversed`, so a reversed claim stayed in the claims-paid balance.

**The fix.** A new consumer reverses the claim's claim-paid journal on the reversal's business date (FIN-003), with the event as the journal's source. It swaps the original's lines, so it needs no posting rule and nets to exactly zero.
- **Cannot post yet:** with no journal (the original is waiting on a posting exception), or no open period on the date, the delivery parks with `CLAIM_REVERSAL_NOT_POSTABLE` and is retried from the parked deliveries. It is never dropped.
- **Redelivery:** a journal already reversed changes nothing.

**For finance to note.** The reversal debits the account the claim-paid rule credited, so until 5a.3 collects the money that account shows the amount as owed back by the provider. 5a.3's recovery postings build on it.

**Claims reversed before this fix** have no reversal journal. No tenant is live, so none is back-filled; a test tenant's earlier reversals stay as they are.

**Tests.** The end-to-end Health flow, on every dated clock: a paid claim reversed leaves account 2100 net zero with one reversal journal. An integration test covers the parked path. Results are in the next report, so this branch's head does not move after CI.
