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

**Limits (Phase 2 review, item 6):**
- an expression is at most 4 000 characters;
- brackets and NOTs nest at most 32 deep;
- one evaluation visits at most 10 000 nodes and list comparisons. A caller can set a lower budget.

Malformed input of any kind fails with `EXPRESSION_INVALID` or `EXPRESSION_EVALUATION_FAILED`, never with another exception. A seeded fuzz test checks this.

## Consequences
- **Arithmetic (Phase 4 review):** bounded arithmetic was added within the same limits:
  - `+ - * /` on numbers, and unary minus;
  - a date plus or minus a whole number of days is a date, and a date minus a date is a number of days;
  - `year(d)` and `today()`. `today()` is the evaluation date the caller supplies; without one it fails.
  - **Date variables** are dates. Text such as `'2027-01-01'` is read as a date where a date is expected, so existing comparisons of dates with text still hold.
  - **Words in lists:** `-` and `+` stay part of a word only inside `IN (…)` lists (such as `25-34`); elsewhere they are arithmetic.
  - **Failures:** division by zero, overflow, fractional days and dates out of range fail the evaluation, never with another exception.
  - The catalogue's sample `year <= current_year + 1` now works.
- Anything beyond this (other functions, assignment, loops) needs a decision first.
- Expressions are evaluated in memory against the inputs. The inputs are recorded with each rule decision (LIF-002).
