# Addendum: access for external users (broker, provider and member portals)

**Signed off 24 October 2026** (Ade), with the two additions in §6 and the open points decided as proposed. Planned in Wave 0b after Health products.

- **Basis:**
  - SRS-003 (access by product, organisation and relationship);
  - IAM-001 (identity lifecycle) and SRS-002 (maker-checker);
  - POPIA (minimality, section 10; security safeguards, section 19);
  - `addenda/access-scopes.md` (F103, the record-scope check this reuses);
  - `addenda/channel-permissions.md` (F005, how a request's channel is known).
- **Today:**
  - An IAM user is linked to nothing but a tenant and an IdP subject. Nothing says that a portal user *is* party P, intermediary BRK-1 or provider PR-1.
  - So a read permission given to a portal user (`general_policy.read`, say) reaches that record type across the whole tenant. F103 scopes narrow by product and branch, not by relationship, and F005 narrows by channel and transaction, not by record.
  - Portal users must therefore not be given business permissions until this is built.

## The proposal

### 1. Each external user is linked to exactly one party, intermediary or provider
- **The link:** a new `iam.user_subject_link`, one row per external user:
  - `kind`: `PARTY`, `INTERMEDIARY` or `PROVIDER`;
  - the key: `party_id`, `intermediary_code` (Distribution's register) or `provider_id` (Health's provider register);
  - `valid_from` and `valid_to`, and the usual audit columns.

  At most one link in force per user.
- **Set by the tenant's staff, under maker-checker:**
  - `POST /identity/users/{id}/subject-link` creates a pending link and a Workflow approval of type `EXTERNAL_SUBJECT_LINK`. A second person approves it (SRS-002), and the link takes effect only then.
  - Ending a link (`…/subject-link/end`) is immediate. It is audited, and the user's next request is already without it (IAM-005).
- **The user is external** when it has a link. An external user is refused staff-only endpoints whatever its roles (§5).
- **Checked when set:** the party, intermediary or provider exists and is active in the tenant; for an intermediary, it holds an active licence (Distribution).

### 2. A relationship scope, enforced like F103
For an external user, every scoped record is also checked against the link, in the same repository loads and list queries that apply the F103 scope. There are no per-endpoint checks. Reads out of scope are 404; actions out of scope are 403 `OUT_OF_SCOPE`.

| Portal | The record is in scope when |
|---|---|
| **Member (party)** | General: the party is the policyholder or proposer, or a claimant or party on the claim. Life: the policyholder, an insured life, a nominated beneficiary for that policy's claims, or the claimant. Health: the principal member or a dependant of the membership, or the patient on a preauthorisation or medical claim. Billing: the payer of the account (its invoices only for policies in scope). Documents: their business record is in scope. |
| **Broker (intermediary)** | Quotes, proposals and policies whose `intermediary_code` is the broker's, and the claims, endorsements, invoices and documents of those policies. Commission entitlements and statements of that intermediary. |
| **Provider** | Health preauthorisations and medical claims whose `provider_id` is the provider's, and remittance advices of that provider. |

- **Servicing intermediary:** the policy's current `intermediary_code` serves as both selling and servicing intermediary. A change of broker (Life alteration `INTERMEDIARY`, or a General endorsement) moves the policy to the new broker from its effective date, and the old broker loses it. A separate servicing intermediary is not modelled anywhere; adding one would be a schema change, proposed only if the business needs both.
- **Lists** are filtered in the query, as for F103, so paging stays correct.
- **Guessing an ID** gives 404, exactly as for a record that does not exist.

### 3. Creation inside the relationship only
- **A broker** quotes or submits a proposal only with its own `intermediary_code`. The body's intermediary is ignored and set from the link, and the proposer or applicant must be a client the broker already services, or a new party the broker registers in that request.
- **A provider** submits medical claims and preauthorisation requests only as itself. The `provider_id` is set from the link.
- **A member** notifies a claim only on a policy or membership in their scope, as claimant or on behalf of a dependant in it. Payee bank accounts must be the member's own verified ones (as today).
- **Anything else is refused** with 403 `OUT_OF_SCOPE`.

### 4. Portal views: internal fields left out (POPIA minimisation)
External users get portal views of the records they can read, not the staff views. Each module defines its portal view next to its staff view, and the endpoint chooses by the caller being external. Left out:
- **Reserves and claim financials,** fraud indicators and hits, referral and investigation status.
- **Underwriting notes,** rule outcomes, loadings rationale and medical evidence. A member sees their own disclosures; a broker sees none.
- **Other parties' personal information:**
  - on a claim, only the caller's own details and the claim's status;
  - on a policy, beneficiaries are shown to the policyholder only;
  - identity numbers and bank details of others are never shown.
- **Internal workflow:** assignees, queues, approvals and audit details.
- **The response schemas** are separate OpenAPI components (for example `PolicyPortalView`), so the front end cannot read a field that is not there. A sweep test fails if a portal endpoint returns a staff view.

### 5. How it combines with F005 channels
- **Each portal has its own Keycloak client** (`broker-portal`, `provider-portal`, `member-portal`), in realm `insys` or a federated realm. Each is on its own channel: audience `BROKER`, `PROVIDER` or `CUSTOMER`, whose `client_ids` list that client.
- **An external user acts only through its portal channel:** a request from an external user on any other client (including `insys-web`) is refused with `CHANNEL_NOT_ENABLED`. The channel's permission rows decide which transactions and products each portal role may use (F005).
- **The order of checks** on each request: permission (IAM), then channel (F005), then product and branch scope (F103, which a tenant may also give portal roles), then relationship scope (§2), then the portal view (§4).
- **Staff endpoints** (static data, administration, approvals, Workflow queues) are refused to external users with 403, whatever permissions their roles hold.

### 6. Added at sign-off (24 October 2026)
- **No duplicate answers to a portal (POPIA).**
  - **The check:** a party a portal user registers, for example a broker's new client, is checked for duplicates as staff registrations are: the identifier hash, or a close name match with the same date of birth.
  - **On a match:** the portal user never gets a duplicate error. The registration is accepted as pending and goes to staff review (a Workflow task, `PARTY_MATCH_REVIEW`), where staff link it to the existing party or confirm a new one.
  - **The response** is the same either way ("received, pending review"), so a portal user cannot learn that someone is already a client of the tenant.
- **MFA:**
  - **Broker and provider portal users** must use MFA. Each portal client has its own authentication policy (`iam.cfg_authentication_and_session_policies`, by client) with MFA required, and a token from those clients without MFA (`amr`/`acr` showing no second factor) is refused.
  - **For member portal users,** MFA is a tenant setting on the member portal's policy.

### 7. Tests (when built)
- A broker cannot see another broker's client's quote, policy, claim or invoice: 404 by ID, absent from lists.
- A provider cannot read another provider's medical claim or remittance, or submit a claim as another provider.
- A member cannot open another member's policy or membership, including by guessing its ID (404), and cannot notify a claim on it (403).
- Portal views carry none of the fields in §4 (a schema sweep, and a check of each portal response).
- A link pending approval gives nothing; an ended link stops access on the next request.
- A portal registration matching an existing party gets the same answer as a new one, and a staff review task; no duplicate error.
- A broker or provider portal token without MFA is refused.
- An external user on `insys-web` is refused; a staff user is unaffected.

## Where it belongs
- **Wave 0b,** straight after the Health products work (`health-plan-products.md`) and before Wave 1, as you suggested.
- **It builds on F103 and F005,** which are merged, and on Health products, because a member's Health records need the same product scoping.
- **Wave 1's communications and cases** will need it: a portal user's messages and cases are scoped by the same link.

## Consequences
- **Identity:** the link table and its commands (a migration), the external flag on the resolved principal, the `EXTERNAL_SUBJECT_LINK` approval type.
- **Building blocks:**
  - a relationship scope next to the access scope;
  - each module's repositories and list queries take it, as they take the F103 scope.
- **Every portal-facing module:** relationship predicates, portal views, and creation rules.
- **Keycloak:** one client per portal, and the portal channels in each tenant's onboarding (onboarding readiness §5).
- **Front end:** the portal apps use their own clients and the portal views.

## Decided at sign-off
- **Servicing intermediary:** the policy's current intermediary is both the selling and the servicing intermediary (§2).
- **Beneficiaries:** a Life beneficiary who is not the policyholder sees nothing before a claim.
- **Broker hierarchies:** a broker firm's principal sees its sub-brokers' clients through the intermediary hierarchy, as a branch covers its descendants.
