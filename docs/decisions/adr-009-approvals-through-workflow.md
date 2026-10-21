# ADR-009: Approvals go through Workflow

- **Status:** Accepted (Phase 0 review item 7; Phase 2)
- **Date:** 21 October 2026
- **Requirements:** SRS-002, WFL-004, SDM-003, PRD-006

## Context
Maker-checker is needed in several modules: static data changes, product publication, and later claims payments and others. domain-model.json gives Workflow the Approval aggregate, with "Checker's authority covers the amount and type", and has modules consume `ApprovalGranted`.

## Decision
- **Requesting:** a module publishes `workflow.ApprovalRequested` (BuildingBlocks.Contracts) through its outbox, in the transaction that needs the approval. It carries the business reference type and ID, the approval type, the maker, and an optional amount and currency. Workflow's `RequestApprovalPolicy` creates the Approval. It expires after 14 days by default (`Workflow:Approvals:DefaultExpiry`).
- **Deciding:**
  - The checker decides through `/api/v1/workflow/approvals/{id}/approve` or `/reject`.
  - For static data, the decision uses the existing paths `/{module}/static/{entity}/{code}/versions/{n}/approve` and `/reject`. Workflow maps them and finds the change request through Static Data's public `IStaticDataChangeLookup`.
  - The checker is never the maker. Their roles today (`IUserRoleQuery`, Identity.Contracts) must hold authority for the approval type in `workflow.cfg_delegation_and_approval_authority`. With an amount, the limit must cover it. If authority is missing, the approval is refused.
- **Outcome:** `workflow.ApprovalGranted` or `workflow.ApprovalRejected` goes to every module. Each acts only on its own business reference type:
  - **Static Data** (`STATIC_DATA_CHANGE`, `STATIC_DATA_T{tier}`): ApproveChange or RejectChange. The owning module then marks the version approved or returns it to draft.
  - **Product Factory** (`PRODUCT_VERSION`, `PRODUCT_PUBLICATION`): ApproveVersion then PublishVersion, or back to Draft.
- **Bootstrap exception (OQ-050):** the authority table is itself Tier 1 static data. So static data approvals are governed by the `static_data.approve` permission, with maker and checker different, until the tenant configures authority for `STATIC_DATA_T{n}`. From then on that authority applies.

## Consequences
- The decision is asynchronous. A just-submitted change can be approved once the request has been relayed, within seconds.
- Approval types are strings agreed between the requesting module and the authority configuration.
