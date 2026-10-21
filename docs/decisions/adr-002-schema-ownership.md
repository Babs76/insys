# ADR-002: Owners of schemas without a module

- **Status:** Proposed
- **Date:** 21 October 2026

## Context
Three V0002 schemas have no module in PROMPT §2:
- **`channel`:** "Portals and Channels has no module".
- **`refdata`:** shared reference data, such as countries, currencies and languages.
- **`ops`:** platform operations configuration.

Every table needs exactly one owning role (DBA-004).

## Decision
- **`channel` belongs to the Integration module**, because Integration exposes the API gateway and the partner channels.
- **`refdata` and `ops` belong to the Static Data module**, which stewards platform reference data.

A module may own several schemas. `ModuleDbContext.OwnedSchemas` lists them, and an architecture test checks that they match the role list.
