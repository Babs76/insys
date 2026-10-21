# INSYS Core: Claude Code build kit

This kit gives Claude Code everything it needs to build the INSYS Core back-end API services in C#: the build prompt, persistent repository instructions, the specifications as machine-readable files, and the tested database schema.

## Contents

| Path | Purpose |
|---|---|
| `PROMPT.md` | The build prompt: architecture, technology, cross-cutting rules, domain rules, API conventions, testing, and eight phases with exit criteria |
| `CLAUDE.md` | Persistent instructions Claude Code reads in every session |
| `docs/specs/requirements.json` | 117 SRS requirements with text, plus the proposed SDM and DBA requirements |
| `docs/specs/domain-model.json` | 23 bounded contexts, 60 aggregates, events, state machines, relationships, 6 processes, glossary |
| `docs/specs/database-tables.json` | 135 core tables with columns, keys and references |
| `docs/specs/static-data-catalogue.json` | 253 static data entities with fields, scope, tier and owner |
| `docs/specs/html/` | The five companion documents: presentation, domain architecture, static data spec, database spec, domain design |
| `docs/srs/` | The SRS v1.0 PDF |
| `db/migrations/` | `V0001` core schema and `V0002` static data schema, tested together on PostgreSQL 16.15 |

## How to use it

1. Create an empty Git repository and copy the contents of this kit into its root.
2. Edit the settings block at the top of `PROMPT.md`. In particular, set `FIRST_DOMAIN` (Life, General or Health) if it has been decided; otherwise Claude Code will ask before Phase 4.
3. Make sure the machine running Claude Code has the .NET 10 SDK, Docker (for PostgreSQL and Testcontainers), `jq` and Git.
4. Open Claude Code in the repository and say:
   `Read PROMPT.md and CLAUDE.md, then start Phase 0.`
5. Review each phase report, the code and the test results. Then say `Continue with Phase <n>`.

## What to expect

- The build is long. Expect each phase to take one or more Claude Code sessions. Review between phases rather than letting it run unchecked.
- Claude Code will log specification gaps and conflicts in `docs/decisions/open-questions.md`. Many relate to the open SRS §30 decisions: first domain, message broker, identity provider, currencies, coding systems and accounting rules.
- External systems (banks, payment providers, clearing houses, reinsurers, the GL and actuarial engines) are built as interfaces with stubs. Real integrations need partner specifications.

## Assumptions behind the prompt

- .NET 10 LTS (supported to 14 November 2028) and PostgreSQL 18. .NET 11 and PostgreSQL 19 were both at release candidate on 21 October 2026.
- A modular monolith, as the SRS risk register recommends against premature microservices (§29).
- No MediatR, AutoMapper, MassTransit v9 or FluentAssertions v8, because they have moved to commercial licences.
- Everything beyond the SRS (the domain model, tables, static data catalogue, and SDM and DBA requirements) is a proposal from the companion specifications and should be reviewed alongside the code.
