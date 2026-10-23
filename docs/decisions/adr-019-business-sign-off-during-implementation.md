# ADR-019: Business sign-off of proposals during implementation

- **Status:** Accepted (Phase 8 brief, decisions table, 23 October 2026)
- **Date:** 23 October 2026

## Decision
The business owner has confirmed that proposals are signed off while implementation goes on, not before it starts. Where the SRS does not specify something that Phase 8 builds, the work follows this order:

1. **Write the addendum first.** A short specification addendum goes in `docs/specs/addenda/<area>.md`. It gives the proposed states, rules, data and events, marked **"Proposed, pending business sign-off"**, following ADR-012, ADR-014 and ADR-015.
2. **Record the proposals.** Each proposal is recorded in `docs/decisions/open-questions.md` under an OQ number, with status *Resolved (proposal)*, so that it can be signed off item by item.
3. **Build to the addendum.** Make every default configurable where practical, and mark the code `TODO(spec)` with the OQ number.
4. **Keep the marker.** Every default stays marked "pending business sign-off" until it is signed off. Signing off is a change of status in the OQ and the ADR, with the date. A rejected proposal becomes a change request.

## Consequences
- Sources of truth are unchanged: SRS, then migrations, then `domain-model.json`, then the HTML specs (CLAUDE.md). An addendum ranks below all of them. Where an addendum adds states or events to the domain model, the addendum is the record of that until the model is updated.
- ADR-012, ADR-014 and ADR-015 and the open questions listed at the top of `open-questions.md` are covered by the same process.
