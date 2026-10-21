# ADR-003: Static data across modules, governed by the Static Data module

- **Status:** Accepted (items 6 and 7 of the Phase 0 review)
- **Date:** 21 October 2026
- **Requirements:** SDM-002 to SDM-015, SRS-002, DBA-004

## Context
Static data tables live in their owning module's schema (PROMPT §7). Change requests live in `sdm.change_request`. With one role per module (ADR-001), no module can write both in one transaction.

## Decision
1. **A generator writes the code.** `tools/Insys.StaticData.Generator` reads V0002 and the catalogue, and writes an entity, an input, an EF mapping and the endpoints for every table, into its owning module. The output is deterministic and committed.
2. **The owning module runs the lifecycle in its own schema:**
   - draft, edit (maker only), submit;
   - activate (Published for configuration items), deprecate, retire;
   - all-or-nothing import.

   Each step publishes an `sdm.*` event through the module's outbox.
3. **The Static Data module records change requests and decides approvals:**
   - It creates the change request from `StaticDataChangeSubmitted`.
   - Approve and reject are mapped once, on the same paths as the table's own actions.
   - It checks that maker and checker differ for Tier 1, and by default for Tier 2 (SDM-003). The database also refuses a checker equal to the maker.
   - It publishes `StaticDataChangeApproved` or `StaticDataChangeRejected`, and the owning module applies the outcome.
4. **Tier 3 needs one maintainer.** The change request is approved on submission with no checker.
5. **Global rows are for platform administrators only:**
   - Global rows (`tenant_id IS NULL`, scopes G and GT) are written only on the platform administrator connection, and only from `/global` endpoints.
   - Those endpoints require the platform operator tenant and the permission.
   - Tenant requests can never write them. The policy refuses them, and the database also refuses them: only `insys_platform_admin` has write policies on global rows.
6. **Where the events live:** the governance events are the published language of the `sdm` context. They live in BuildingBlocks.Contracts, because the shared static data framework raises them for every owning module.

## Consequences
- Approval is eventually consistent: a submitted change appears in Static Data within one relay pass. Approving earlier returns 404 `CHANGE_REQUEST_NOT_FOUND`, and the client can retry.
- Phase 2 moves the approval decision itself to Workflow Approval (item 7). The events stay the same.
