# Starter packs

**Status: draft, not loaded by anything.** A pack is a list of static data rows a new tenant starts from. Each row goes through the governed static data APIs and a second person, as a tenant's own rows do today. No loader exists yet.

## Structure
A tenant's starting data is three layers. A later layer replaces a row with the same table and code.

1. **`base.json`, country-neutral:** operational defaults, with no currency, country, regulator or legal text.
2. **Country overlay, `overlays/<country>.json`:** currency, calendar, identity documents, regulated values and texts.
3. **Line overlay, `overlays/line-<line>.json`:** taxonomy, coverages and rating structure of a line of business.

Every row has a `class`: `OPERATIONAL`, `FINANCE` or `COMPLIANCE`. Review sheets list only FINANCE and COMPLIANCE rows.

Target markets in order, with the overlays each needs:
| Market | Overlays | Regulator | Currency | Language | State |
|---|---|---|---|---|---|
| Nigeria | `ng` | NAICOM | NGN | en | Skeleton: `overlays/ng.json` |
| Rwanda | `rw` | BNR | RWF (no minor unit) | en, fr, rw | Not started |
| Ghana | `gh` | NIC | GHS | en | Not started |
| Senegal | `cima` + `sn` | CIMA, with the national directorate | XOF (no minor unit) | fr | Not started |
| Kenya | `ke` | IRA | KES | en, sw | Not started |
| DRC (not CIMA) | `cd` | ARCA | CDF and USD | fr | Not started |
| Côte d'Ivoire | `cima` + `ci` | CIMA, with the national directorate | XOF (no minor unit) | fr | Not started |

`cima` holds what the CIMA Code sets for all its members, and `sn` and `ci` hold only what differs nationally, so a country overlay may itself build on a regional one. South Africa stays as `overlays/za.json`, for reference only. Regulators outside Nigeria, and the languages, are from general knowledge and need confirming.

## Where the values came from
- **`base.json` and `za.json`:** the ready-tenant seed (`tests/Insys.Testing/OnboardingSeed.cs`) and the front end's live run (`insys-console`, `e2e/live/readiness.ts`). Together they are what onboarding readiness needs.
- **These are readiness minimums, not a working setup.** The chart of accounts is one account, and all posting rules post to it. The notes on those rows say `PLACEHOLDER` or `TEST VALUE`. Real finance setup needs a real chart.
- **One default isn't extracted:** MFA is on in the base pack, though both sources turn it off for test sign-in.
- **Where the sources differ:** a complaint's resolve time is 42 calendar days in the seed and 30 business days in the live run. `za.json` records both, for compliance to settle.
- **Left out:** the simulator payment provider, test products and test people.

## Nigeria
`overlays/ng.json` is a skeleton. Every regulated value is `DRAFT-FOR-REVIEW`, most of them empty. `basis_to_check` says where to look; it comes from general knowledge and nobody has checked it against Nigerian law. [`nigeria-review-sheet.csv`](nigeria-review-sheet.csv) has its FINANCE and COMPLIANCE rows for reviewers.

Code gaps affect some rows: see [the survey](../../reports/2026-11-01-country-readiness-survey.md).
