# ADR-008: The configuration expression language

- **Status:** Accepted (Phase 2)
- **Date:** 21 October 2026
- **Requirements:** SRS §7.4, §7.8; WFL-001; LIF-002

## Context
The static data catalogue stores conditions as text: eligibility rule sets, acceptance and referral rules, decline conditions, and workflow transition guards. Its samples include `age_next >= 18 AND age_next <= 65 AND resident_country IN (ZA)`. SRS §7.8 asks for a governed rules language but does not define one.

## Decision
A small, typed condition language in `Insys.BuildingBlocks.Domain.Expressions`. It has no functions, assignments or loops, and cannot reach anything outside the variables it is given.

**Grammar:**
- `AND`, `OR`, `NOT` and brackets.
- Comparisons `= != ≠ <> < <= ≤ > >= ≥`.
- `IN (…)` and `NOT IN (…)`; list items may be bare words, such as `ZA`.
- `BETWEEN … AND …`.
- Numbers (decimal), `'text'`, `true`, `false` and `null`.
- Identifiers with dots.

**Behaviour:**
- **Keywords** are case-insensitive.
- **A missing variable is an error** (`EXPRESSION_EVALUATION_FAILED`), never a silent false. A rule that cannot be evaluated fails the evaluation, and nothing is recorded.
- **Parse errors** are `EXPRESSION_INVALID`. Rule sets and workflow definitions are parsed when they are read, so an invalid condition is refused.

## Consequences
- Anything beyond conditions, such as arithmetic or date functions, needs a decision first.
- Expressions are evaluated in memory against the inputs. The inputs are recorded with each rule decision (LIF-002).
