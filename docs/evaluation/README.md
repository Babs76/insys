# AI-2 evaluation set

`ai2-evaluation-set.json` is the AI-2 evaluation set (ai-assist.md §6 and §7). It is generated, never edited by hand: `OftzaSeedTests` builds it from the error catalogue's worded codes and from the sample tenant's premiums. Run that test class with `INSYS_UPDATE_SNAPSHOTS=1` to accept a change. It holds sample data only, and the rates are invented.

## Case ids (Ade, 18 November 2026)
A case's id is built from the case's identity, never from its text:
- **A refusal is `R-<error code>`.** The kind is `R`, for the refusal template, and the code is the case. For example, `R-ASSIST_PACK_CHANGED`.
- **A premium is `C-<product>-<factor value>-<sum insured>-<adjustments>`.** The kind is `C`, for the premium template, and the scenario is the case. The adjustments are the schedules applied, joined by `+`, or `NONE`. For example, `C-OFT-HOME-BRICK-1000000-OFT-HOM-NCD+OFT-HOM-FLOOD`.

**Consequences:**
- A change of wording keeps a case's id: the catalogue's meaning or next step, or a premium's lines or amount.
- So does a new version of a prompt template, which isn't part of the id.
- Only a new case adds an id, and only a removed one retires it: a code gains or loses its wording, or the grid gains or loses a scenario.

The ids are stable from the expert's run on. Ids from earlier runs, such as `R-005` and `C-001`, were positions in the list and don't carry over.
