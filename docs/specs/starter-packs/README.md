# Starter packs

**Status: draft.** A pack is a list of static data rows a new tenant starts from. Each row goes through the governed static data APIs and a second person, as a tenant's own rows do today. Batch 7.6 loads a pack file as a version in Assist's own tables (D-18), for review and approval (§5.3); its rows reach a tenant only through the `SETUP` change set (7.7).

## Structure
A tenant's starting data is layers, in this order (Ade, 16 November, OQ-181): the base, then each line, then the country (after the regional overlay it builds on), each the platform's version. Then come the tenant's own approved overlays.

- **Merging:** a later layer's row merges into the earlier one with the same table and code, field by field. A null means "no change", never "set to null".
- **The tenant's own overlays:** they only fill values still empty, and never replace a regulated value from a platform-approved version.
- **Unapproved versions:** a regulated value from a version not approved for the tenant is never used.

1. **`base.json`, country-neutral:** operational defaults, with no currency, country, regulator or legal text.
2. **Country overlay, `overlays/<country>.json`:** currency, calendar, identity documents, regulated values and texts.
3. **Line overlay, `overlays/line-<line>.json`:** taxonomy, coverages and rating structure of a line of business.

Every row has a `class`: `OPERATIONAL`, `FINANCE` or `COMPLIANCE`. Review sheets list only FINANCE and COMPLIANCE rows.

**Row format (corrected 16 November, [the list](format-corrections-2026-11-16.md)):**
- One row holds one record, and `code` is that record's code.
- `values` holds its fields under the table's own column names, every field the table requires included. A field with no value yet is `null`.
- A note for the reviewer goes in the row's `note`, never in `values`.

Every row is to carry two marks (D-20, Ade 15 November): `regulated` (true when only an approved overlay may supply it, AIA-082) and `tailorable` (true when AI may tailor it, 7.8). **No row in these drafts has them yet.** Until it does, INSYS reads a missing `tailorable` as false and a missing `regulated` from the class (FINANCE or COMPLIANCE is regulated), and records that the mark is the interim reading; the overlay review sheet shows each row's marks and their source for the reviewer and the approver to confirm. AI never tailors a row whose mark is the interim reading.

**What tailoring may change (OQ-187 (a), Ade, 19 November 2026; batch 7.8):** a row marked `"tailorable": true` also names what may change, and from which questionnaire answers, in `tailor`:
```json
{ "table": "workflow.ref_business_calendars", "code": "CAL", "class": "OPERATIONAL", "regulated": false, "tailorable": true,
  "tailor": { "columns": ["time_zone"], "from": ["timeZone"] }, "values": { "calendar_code": "CAL", "time_zone": null } }
```
- `columns` are the row's own value names; `from` are answers by their API names: `country`, `lines`, `currencies`, `timeZone`, `products`, `channels`, `paymentMethods`, `teamRoles`, `languages`.
- AI may tailor the row only with both marks explicit, `regulated` false, and `tailor` given. It sets one named column to exactly one of a named answer's values, and INSYS's check refuses anything else (AIA-087).
- `tailor` is part of the version's content hash; a row without it hashes as before, so approvals already given still bind.
- A tailored value is a proposal: undecided until a person accepts it, and then in force only through the SETUP change set's second-person approval.

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
