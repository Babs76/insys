# Defect: a reversed Health claim gave back benefit use wrongly

Branch `fix-accumulator-giveback`, on `wave5-b010`. Found while checking reverse-and-reprocess, Ade's approach to correcting part of a paid claim (decided 30 October 2026: partial reversal is not built).

## The defects
1. **Deductible and co-payment were not given back.** A reprocessed claim saw the deductible as already met, so it took too little from the member and the insurer paid too much.
2. **Another dependant's use was reduced.** The reversal gave back on every accumulator of the category on the membership, whoever it belonged to, so another dependant had more limit left than they should.
3. **A line the deductible absorbed whole was skipped,** because only lines with a paid amount were given back. Its deductible would have stayed met.

## The fix
- **Own and family:** a reversal gives back on the patient's own `MEMBER` accumulators and the shared `FAMILY` ones, never another dependant's.
- **Deductible and co-payment:** they go back on the patient's benefit-year accumulator, where the engine tracks them. Lifetime and family accumulators give back use only.

## Sweep: does consumption have the same fault?
**No.** Adjudication, consumption and reservations all read and write accumulators by exact key: scope, the patient (or the principal for a family limit), category and period. A claim cannot consume another dependant's individual limit. `AccumulatorsCovering` was used only by the reversal.

## Still open, in 5a.3
Reversal does not restore a preauthorisation (its status, reservation or `ConsumedAmount`), so a reprocessed claim for a service needing one pends with `PREAUTH_REQUIRED`. The link between the two claims is also 5a.3.

## Tests
- **End-to-end, on the dated clocks:** two people on one family pool, each with a claim of 500 (deductible 100, co-payment 40, paid 360). Reversing the spouse's restores all three of its own figures and leaves the principal's unchanged, and the family limit goes from 720 to 360. A new claim for the same service is not a duplicate, takes all three again, and the family limit returns to 720. The test marks the claim paid directly; the run is covered by the main Health flow.
- **Unit:** the give-back, which never goes below zero.

Results are in the next report, so this branch's head does not move after CI.
