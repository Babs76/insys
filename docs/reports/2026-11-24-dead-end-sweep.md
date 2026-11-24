# Dead-end sweep before beta (24 November 2026)

**Scope.** This is a read-only sweep of the back end at `main` `4211e3f`. Nothing in the code was changed. The Preview runs the release branch from `ca1bf2b`, which has the same `src` for everything swept here, so these findings apply to it.

**Question.** On the Preview, an approved static data version left its table's list until someone activated it, and no screen led to it. This sweep looks for that pattern everywhere:
- a state that no tenant-wide list shows;
- a transition that no role template can perform;
- an approval that no template can decide, or whose next step nobody is led to;
- a background state that waits for a person nobody tells.

**Counts.**
- **Stateful aggregates:** 110, each with more than one state.
- **States:** 467.
- **Transitions:** 542.
- **Approval types:** 38. That is 30 Workflow approval types (some split by what they approve) plus 8 approvals that use their own rule.
- **Background states:** 23 needing a person.
- **Gaps:** the five sections report 232 between them, before removing overlaps. The same gap can appear in both the approval-type table and a module table.

## What the sweep shows, by pattern

Each item cites the rows below, which give the file and line.

1. **Authority without an inbox (`NO-INBOX`).** Deciding needs `WORKFLOW_APPROVE`, and only 7 templates hold it: TENANT_ADMIN, CLAIMS_MANAGER, FINANCE_MANAGER, COMPLIANCE_OFFICER, LINE_CONFIGURATION_MANAGER, PLATFORM_ADMIN and PLATFORM_STATIC_DATA_STEWARD. The only templates with authority for these types hold neither `WORKFLOW_APPROVALS` nor `WORKFLOW_APPROVE`:
   - `LIFE_UNDERWRITING`, held by LIFE_UNDERWRITER;
   - `ENDORSEMENT`, held by GENERAL_UNDERWRITER;
   - `PRODUCT_PUBLICATION`, held by PRODUCT_MANAGER.

   OFTZA's seed falls back to TENANT_ADMIN, who can decide but lacks the screens to open those records. Static data is different: see item 3.
2. **No authority at all (`NO-AUTHORITY`).** These types are in no template's `Approves`:
   - `GENERAL_NON_RENEWAL`;
   - `PARTY_MATCH_RESOLUTION`;
   - `DUE_DILIGENCE_ENHANCED`;
   - `PROVIDER_CREDIT_NOTE`, `PROVIDER_RECOVERY_FINDING` and `PROVIDER_RECOVERY_WRITE_OFF`.

   Readiness passes `PARTY_MATCH_RESOLUTION` through the TENANT_ADMIN fallback, but that role lacks `party.update`, so nobody can decide it.
3. **Static data: decidable, but hard to find (`NO-LIST`, `NO-ROLE`).** A static data change is decided on the table's own route (`ApproveStaticDataChange`, `Workflow/Insys.Workflow.Api/WorkflowModule.cs:355-406`). That route needs the module's `SD_<M>_APPROVE` and `STATIC_DATA_T1`/`T2` authority, but not `WORKFLOW_APPROVE`. So PRODUCT_MANAGER, FINANCE_OFFICER, DISTRIBUTION_ADMINISTRATOR, REINSURANCE_OFFICER and INTEGRATION_ADMINISTRATOR can decide their modules' changes. What they lack is any way to find them:
   - a pending version isn't in the table's list;
   - they don't hold the approvals inbox;
   - the change-request list (`STATIC_DATA_CHANGE_REQUESTS`) is held only by TENANT_ADMIN, AUDITOR and the platform steward.

   Six modules have no tenant template that maintains or approves their tenant rows: Party, Communications, Documents, Claims Framework, Analytics and Audit. A SETUP change set needs every table's approve function.
4. **Whole areas no template holds (`NO-ROLE`):**
   - General renewals and retention (`GENERAL_DECIDE_RENEWAL`, `GENERAL_RECORD_RENEWAL_ANSWER`, `GENERAL_PROPOSE_NON_RENEWAL`, `GENERAL_WORK_RETENTION_CASE`). Their screens are only in AUDITOR, so referred and not-eligible renewals, and the retention cases opened automatically, can't be worked.
   - Health provider quality review.
   - The data protection officer holds no data-subject request function.
   - AI Assist run functions. This one is by design: a tenant grants them itself.
5. **Approved, then waiting for a step nobody is told about (`FOLLOW-UP`):**
   - **Static data activation:** the Preview case.
   - **Release of manually drafted claim payment instructions.** They are listed only under their claim.
   - **Release of payout batches and Health provider payment runs.**
   - **Building payout batches** for refunds, commission and redress.
   - **Issue after an accepted Life or General proposal.**

   Approval opens no task for the requester. The follow-up task is raised only on expiry, withdrawal or not-applied.
6. **Tasks sent where they can't be seen.** `WORKFLOW_TASKS` is held only by AUDITOR and OPERATIONS_ANALYST, and only OPERATIONS_ANALYST can complete tasks. There is also no "my tasks" operation; the list filters by queue or assignee. So these tasks wait unseen:
   - the requester's `APPROVAL_FOLLOW_UP`;
   - `CLAIM_PAYMENT_HELD`;
   - `PARKED_DELIVERY`;
   - quote referrals and renewal reviews.

   TENANT_ADMIN, the default target for escalations, can't list parked deliveries either.
7. **Failures nobody is told about (`NO-LIST`, `STUCK`):**
   - Dead letters raise no task or alert, and later events for the same record queue behind them. Integration roles see only a count (`DEAD_LETTERS_OPEN` on `GET /integration/deliveries/summary`).
   - `NotificationFailed` has no consumer.
   - Of the modules that own the paid records, only Claims Framework handles `PaymentFailed`; Payments uses it only to settle the batch. So a refund stays APPROVED, and a provider payment run stays RELEASED, after its payment fails.
   - `APPROVAL_ENDING_NOT_APPLIED` parks without telling the requester. Discarding that parked delivery leaves the approval `NotApplied` for good.
   - Uploads quarantined after five failed scans can't be listed or rescanned.
8. **Other states with no way out (`STUCK`, `NO-OP`):**
   - A static draft can't be discarded, only edited and submitted, and it blocks new drafts of its code.
   - An approved commission statement with a net amount of zero or less never reaches Paid.
   - An invoiced Health provider recovery leaves only when its invoice is settled.
   - Life surrender, paid-up and maturity always refuse (OQ-052).

   One spec gap that isn't a dead end: nothing moves a configuration version from Published to Effective. Published already counts as in force and can be retired.
9. **Lists that are missing or can't filter by status (`NO-LIST`, `NO-FILTER`):**
   - **Listed only under their parent record:** endorsements, recoveries and payment instructions.
   - **No status filter:** quotes, policies, claims (General), users, access reviews, data-subject requests, the intermediary register, processes and import batches.
   - **No list at all:** documents past their retention.
   - **Read by ID only:** product draft runs and tenant setups.
10. **Approvals whose record the checker can't open.** The approval stores an ID, but no GET takes that ID for:
    - bank accounts;
    - role assignments and subject links;
    - document holds;
    - product versions (fetched by code and number);
    - accounting periods (fetched by code);
    - case redress (needs the case ID too).

    Checkers also lack the screen for `COMMISSION_STATEMENT`, `CASE_REDRESS` and `LEGAL_HOLD_RELEASE` records.

**Limits of this sweep.**
- It reads the role templates. A tenant can build its own roles, and OFTZA's demo roles hold nearly every function. That is why these gaps didn't show on the Preview, except the list gap behind the activation dead end.
- It doesn't check the console's screens.
- Cells marked "unverified" say why.
- No open question was added. Which gaps become OQ entries or fixes is Ade's decision.

**How the role columns were made.** Each operation's guarding function comes from the OpenAPI `x-function`. The functions each template holds were computed from `db/migrations/R__function_catalogue.sql` and `RoleTemplates.cs`, with the rule in `RoleTemplates.Expand`. The result matches the screen and action counts in `docs/specs/addenda/role-templates.md` for all 25 templates. The tables call the derived files `ops.tsv`, `fn_roles.tsv` and `templates.tsv`; they are not committed. Paths are relative to `src/Modules/` unless they start with `src/`, `docs/`, `db/` or `tools/`.

**Gap tags.**

| Tag | Meaning |
| --- | --- |
| `NO-LIST` | Nothing tenant-wide lists the state |
| `NO-FILTER` | Listed, but the list can't filter by status |
| `NO-ROLE` | No template holds the function |
| `NO-OP` | The model's transition has no operation |
| `STUCK` | No way out of the state |
| `FOLLOW-UP` | A step must follow approval |
| `SPEC-DIFF` | `domain-model.json` and the code differ |
| `NO-INBOX` | The roles with authority can't decide |
| `NO-AUTHORITY` | No template may approve the type |


## Approval types and background states

Legend. Inbox = WORKFLOW_APPROVALS (screen) + WORKFLOW_APPROVE/WORKFLOW_REJECT (route `approval.approve`, WorkflowModule.cs:198-244); all templates holding WORKFLOW_APPROVE also hold WORKFLOW_APPROVALS. Inbox templates: TENANT_ADMIN, CLAIMS_MANAGER, LINE_CONFIGURATION_MANAGER, FINANCE_MANAGER, COMPLIANCE_OFFICER, PLATFORM_ADMIN, PLATFORM_STATIC_DATA_STEWARD (templates.tsv). "Approves" = RoleTemplates.cs:158-224 (the authority rows a template is expected to have, not rows created). Seed = tools/Insys.Seed; "TA fallback" = OftzaSeed.cs:172-175 gives TENANT_ADMIN 100 000 for every type onboarding readiness reports missing. Readiness (ApprovalAuthorityReadiness.cs:85-104) checks authority + `approval.approve` and a RequiredPermission only where a module states a condition (DD types, GENERAL_NON_RENEWAL); it does not check function sources (SD_*_APPROVE) nor bank_account.verify / party.update. Every approval stores businessRefType + businessRefId + a `screen` (ApprovalKinds.Declare, Approval.cs:88-90, Approvals.cs:84); "GET by id" says whether an operation reads the record by that id. Every ending other than approval goes back to the record module (ApprovalRejected); a requester gets an APPROVAL_FOLLOW_UP task only on expiry, withdrawal for them, or NotApplied (Approvals.cs:189-206, 527, 707, 727); on approval or rejection the requester gets nothing (no Communications consumer of ApprovalGranted/Rejected; grep). Checkers are never notified of a new request (only the badge `WorkflowCountDecidableApprovals`).

### TABLE 1: approval types

| Approval type | Requested by (operation, module) | Roles with authority | Those roles hold WORKFLOW_APPROVALS / WORKFLOW_APPROVE? | Any extra function the decision needs | Carries a link to its record? | Step that must follow approval, who does it, automatic? | Who is told on approval / rejection / expiry | Gap |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| LIFE_UNDERWRITING | LifeDecideCase POST /life/underwriting-cases/{id}/decision when override or above authority (LifeStores.cs:274), Life | LIFE_UNDERWRITER; seed: TA fallback | LIFE_UNDERWRITER: no / no. TA (seed): yes | none | LIFE_UNDERWRITING_CASE + case id; LifeGetUnderwritingCase. TA lacks LIFE_UNDERWRITING_CASES screen | Automatic: case Decided (LifeEventPolicies.cs:166); later issue not traced (unverified) | appr/rej: nothing; expiry: follow-up task to underwriter | `NO-INBOX` the only template with authority can't decide at all (no approval.approve); seed falls back to TA, who can't open the case |
| LIFE_CLAIM | Propose claim approval above own limit (LifeClaim.cs:390, LifeStores.cs:291), Life | Module source life.cfg_claims_authority_limits (ClaimsAuthority.cs:40); template CLAIMS_MANAGER; seed LCL-DEATH CLAIMS_MANAGER 500k (OftzaSeed.cs:182), LIFE_CLAIM CLAIMS_MANAGER (OftzaSeed.cs:932) | yes / yes | none | LIFE_CLAIM + claim id; LifeGetClaim | Automatic: claim Approved, then one instruction per payee released at once (Claims.cs:951-990); a refused release holds it and opens CLAIM_PAYMENT_HELD task (Processes.cs:502-516) | appr/rej: nothing; expiry: follow-up task | - |
| CLAIM_SETTLEMENT | General settlement request (GeneralStores.cs:625), General | CLAIMS_MANAGER; seed CLAIMS_MANAGER 250k (OftzaSeed.cs:932) | yes / yes | none | GENERAL_CLAIM + claim id; GeneralGetClaim | Automatic: GeneralClaimApproved → instruction created and released (Claims.cs:885-914) | appr/rej: nothing; expiry: follow-up task | - |
| ENDORSEMENT | GeneralSubmitEndorsement POST /general/endorsements/{id}/submit (GeneralStores.cs:619), General | GENERAL_UNDERWRITER; seed: TA fallback | GENERAL_UNDERWRITER: no / no; TA (seed): yes | none | GENERAL_ENDORSEMENT + id; GeneralGetEndorsement. TA lacks GENERAL_ENDORSEMENTS | Automatic: Approved then ApplyEndorsement (GeneralPolicies.cs:184-194) | appr/rej: nothing; expiry: follow-up task | `NO-INBOX` GENERAL_UNDERWRITER can't decide; TA fallback can't open the endorsement |
| GENERAL_NON_RENEWAL | GeneralProposeNonRenewal POST /general/policies/{id}/non-renewal (GeneralStores.cs:585), General | none in templates; seed OFTZA_APPROVER (OftzaSeed.cs:524-528) | OFTZA_APPROVER (seed role) yes / yes | RequiredPermission general_policy.non_renew = GENERAL_PROPOSE_NON_RENEWAL, held by no template (fn_roles) | GENERAL_RENEWAL + renewal id; GeneralGetRenewal (GENERAL_RENEWALS: AUDITOR only) | Automatic: NonRenewals.Decide, notice to policyholder (GeneralPolicies.cs:260-270, GeneralStores.cs:588) | appr/rej: nothing; expiry: follow-up task | `NO-AUTHORITY` no template; `NO-ROLE` both maker and checker function in no template; only the seed's own role works |
| CLAIM_PAYMENT (new instruction; payee change on retry) | ClaimsSubmitInstruction / retry (ClaimsStores.cs:240-243), ClaimsFramework | CLAIMS_MANAGER, FINANCE_MANAGER; seed FINANCE_OFFICER 100k (OftzaSeed.cs:932) | CM, FM yes / yes; FINANCE_OFFICER no / no | none | CLAIM_PAYMENT + instruction id; ClaimsGetInstruction (FM lacks CLAIM_PAYMENT_INSTRUCTIONS) | New instruction: Approved only (ClaimPaymentInstruction.cs:264-288); release is manual ClaimsReleaseInstruction (CLAIMS_RELEASE_INSTRUCTION: CLAIMS_MANAGER). Payee change: applied and failed payment re-released (Claims.cs:785-805) | appr: nothing; rej: nothing; expiry: follow-up task | `FOLLOW-UP` release by CLAIMS_MANAGER, no task or list: instructions list only per claim (ClaimsListInstructions); seed row for FINANCE_OFFICER useless (no approval.approve); FM can't open the instruction |
| REFUND | BillingRequestRefund POST /billing/accounts/{id}/refunds (BillingStores.cs:346), Billing | FINANCE_MANAGER; seed FINANCE_OFFICER 25k (OftzaSeed.cs:932), FINANCE_MANAGER 25k (OftzaSeedClaims.cs:57) | FM yes / yes; FO no / no | none | REFUND_REQUEST + id; BillingGetRefund | Automatic: RefundApproved → payout payment (Invoice.cs:472, Payments.cs:992); then batch build + PAYOUT_RELEASE (below) | appr/rej: nothing; expiry: follow-up task | `FOLLOW-UP` money only moves after a person builds a batch (PAYMENTS_BUILD_BATCH: FINANCE_OFFICER), nobody is told |
| COMMISSION_STATEMENT | Statement submit (DistributionStores.cs:336), Distribution | FINANCE_MANAGER; seed FM 5M (OftzaSeed.cs:932) | yes / yes | none | COMMISSION_STATEMENT + id; DistributionGetStatement (FM lacks COMMISSION_STATEMENTS) | Automatic: Approved, CommissionStatementApproved → payout payment (Commission.cs:382-400, Payments.cs:996); then batch | appr/rej: nothing; expiry: follow-up task | `FOLLOW-UP` as REFUND; checker can't open the statement (screen not held) |
| PERIOD_REOPEN | FinancePeriodRequestReopen POST /finance/periods/{periodCode}/reopen (FinanceStores.cs:216), Finance | FINANCE_MANAGER; seed: TA fallback only | FM yes / yes | none | ACCOUNTING_PERIOD + period **id**; FinanceGetPeriod takes periodCode, no GET by id | Automatic: period reopened (Policies.cs:243-255) | appr/rej: nothing; expiry: follow-up task | link by id only, no GET by id (minor) |
| PRODUCT_PUBLICATION | Product version submit (ProductStores.cs:215), ProductFactory | PRODUCT_MANAGER; seed TENANT_ADMIN limit 0 (OftzaSeed.cs:376) | PM no / no; TA yes | none | PRODUCT_VERSION + version **id**; ProductGetVersion takes productCode/versionNumber, no GET by id; TA lacks PRODUCTS | Automatic: ApproveVersion then PublishVersion (Products.cs:563-582) | appr/rej: nothing; expiry: follow-up task | `NO-INBOX` PRODUCT_MANAGER can't decide; seed TA decides blind |
| STATIC_DATA_T1, STATIC_DATA_T2 (change) | Submit of any tenant static data version (StaticDataStores.cs:125), StaticData | TENANT_ADMIN, PRODUCT_MANAGER, LINE_CONFIGURATION_MANAGER, FINANCE_OFFICER, FINANCE_MANAGER, DISTRIBUTION_ADMINISTRATOR, REINSURANCE_OFFICER, COMPLIANCE_OFFICER, INTEGRATION_ADMINISTRATOR, PLATFORM_STATIC_DATA_STEWARD; seed same 8 tenant roles + OFTZA_SEED_LOADER (OftzaSeedLines.cs:48-57, OftzaSeed.cs:530-533) | TA, LCM, FM, CO, PSS yes / yes; PM, FO, DA, RO, IA no / no (they decide via the table's own approve route, DecideStaticDataChange WorkflowModule.cs:390-406) | the table's SD_{MODULE}_APPROVE (StaticDataModule.cs:200-229). SD_PARTY_MANAGEMENT, SD_COMMUNICATIONS, SD_DOCUMENT_AND_CONTENT_MANAGEMENT, SD_SHARED_CLAIMS_FRAMEWORK, SD_ANALYTICS, SD_AUDIT _APPROVE are held only by PLATFORM_STATIC_DATA_STEWARD | STATIC_DATA_CHANGE + change request id; GetChangeRequest (STATIC_DATA_CHANGE_REQUESTS: TA, AUDITOR, PSS) | Manual: version APPROVED only (StaticDataService.cs:360-380); activate via Change<Table>Lifecycle {action}=activate, SD_{MODULE}_MAINTAIN; no auto-activation job (grep) | appr: nothing; rej: nothing; expiry: follow-up task | `FOLLOW-UP` activation by the maintainer, no task; `NO-ROLE` tenant tables of Party, Communications, Documents, Claims Framework, Analytics, Audit: no tenant template holds their approve function; `NO-INBOX` PM, FO, DA, RO, IA: no inbox, change-request list not held |
| STATIC_DATA_T1 (tier-2 switch, GOVERNANCE_SETTING) | ChangeTier2MakerChecker PUT /static-data/settings/tier2-maker-checker (StaticDataStores.cs:129), StaticData | as STATIC_DATA_T1 | as above | none (no function source covers GOVERNANCE_SETTING) | GOVERNANCE_SETTING + settings id; GetGovernanceSettings (singleton) | Automatic: setting applied (Changes.cs:318) | appr/rej: nothing; expiry: follow-up task | maker function STATIC_DATA_CHANGE_TIER2_MAKER_CHECKER privileged, granted individually |
| STATIC_DATA_T1 (SETUP change set) | AssistSubmitTenantSetup POST /assist/tenant-setups/{id}/submit (StaticDataStores.cs:135), Assist/StaticData | as STATIC_DATA_T1 | as above | every table's SD_*_APPROVE, each of them (StaticDataModule.cs:212-229, Approvals.cs:303-312) | STATIC_DATA_SETUP + set id; GetSetupChangeSet | Automatic: batches applied in order (Changes.cs:427) | appr: SetupChangeSetApplied event; expiry: follow-up task | `NO-ROLE` no tenant template holds all approve functions (TA holds 7 SD_*_APPROVE); maker ASSIST_TENANT_SETUP_RUN in no template |
| PRIVILEGED_ROLE | Assign a privileged role (IdentityStores.cs:193), Identity | TENANT_ADMIN; seed TA (OftzaSeed.cs:536) | yes / yes | none | ROLE_ASSIGNMENT + assignment **id**; no GET by assignment id (IdentityListScopedAssignments has no id param) | Automatic: assignment active (Users.cs:465) | appr/rej: nothing; expiry: follow-up task | link has no GET by id (minor) |
| EXTERNAL_SUBJECT_LINK | IdentityLinkSubject POST /identity/users/{id}/subject-link (IdentityStores.cs:199), Identity | TENANT_ADMIN; seed TA (OftzaSeedLines.cs:83) | yes / yes | none | SUBJECT_LINK + link **id**; IdentityGetSubjectLink is by user id | Automatic (Users.cs:465) | appr/rej: nothing; expiry: follow-up task | link has no GET by link id (minor) |
| TENANT_ADMINISTRATOR | ProvisionTenantAdministrator / RequestAdditionalAdministrator (IdentityStores.cs:186), Identity, operator tenant | PLATFORM_ADMIN | yes / yes | none | TENANT_ADMIN_REQUEST + id; GetTenantAdministratorRequest | Automatic provisioning (TenantAdministrators.cs:537); identity failure → RetryAdministratorIdentity (PLATFORM_ADMIN) | appr/rej: nothing; expiry: follow-up task | - |
| PARTY_MATCH_RESOLUTION | PartyResolveMatch POST /party/parties/{id}/match-resolution (PartyStores.cs:254), Party | none in templates; seed: TA fallback | TA yes / yes | RequiredPermission party.update (any PARTY_UPDATE_*/RESOLVE_MATCH…; CLAIMS_MANAGER holds, TENANT_ADMIN holds none) | PARTY_MATCH + party id; PartyGetParty | Automatic: match applied (PortalRegistration.cs:131) | appr/rej: nothing; expiry: follow-up task | `NO-AUTHORITY` no template; seed fallback TA lacks party.update and readiness doesn't check it (no condition), so readiness passes while nobody can decide |
| BANK_ACCOUNT_VERIFICATION | PartyAddBankAccount POST /party/parties/{id}/bank-accounts (PartyStores.cs:250), Party | FINANCE_MANAGER; seed FM (OftzaSeedLines.cs:47) | yes / yes | RequiredPermission bank_account.verify = PARTY_BANK_ACCOUNT_VERIFY (FM) | BANK_ACCOUNT + account **id**; only PartyListBankAccounts under the party, no GET by account id | Automatic: Active, previous Superseded (Parties.cs:249) | appr/rej: nothing; expiry: follow-up task | link has no GET by id: the inbox can't open the account without the party id (Approvals.cs:77 view has none) |
| DUE_DILIGENCE_ENHANCED | Enhanced DD clearance (DueDiligence.cs:451, PartyStores.cs:272), Party | none in templates; seed OFTZA_APPROVER (OftzaSeed.cs:524-528) | seed role yes / yes | RequiredPermission due_diligence.approve_enhanced = PARTY_APPROVE_ENHANCED_DD (privileged, no template); contributors excluded (EnhancedApprovalContributors.cs:40) | DUE_DILIGENCE + party id; PartyGetDueDiligence | Automatic (DueDiligence.cs:778) | appr/rej: nothing; expiry: follow-up task | `NO-AUTHORITY`; checker function privileged, granted individually |
| DUE_DILIGENCE_RISK_OVERRIDE | PartyOverrideRiskClass (PartyStores.cs:275), Party | COMPLIANCE_OFFICER; seed OFTZA_APPROVER | yes / yes | due_diligence.override_risk = PARTY_OVERRIDE_RISK_CLASS (CO) | DUE_DILIGENCE_RISK_OVERRIDE + party id; PartyGetDueDiligence | Automatic (DueDiligence.cs:778) | appr/rej: nothing; expiry: follow-up task | - |
| DUE_DILIGENCE_UNBLOCK | PartyRequestDueDiligenceUnblock (PartyStores.cs:278), Party | COMPLIANCE_OFFICER; seed OFTZA_APPROVER | yes / yes | due_diligence.unblock = PARTY_UNBLOCK_DUE_DILIGENCE (CO PrivilegedGrants) | DUE_DILIGENCE_UNBLOCK + party id; PartyGetDueDiligence | Automatic | appr/rej: nothing; expiry: follow-up task | - |
| LEGAL_HOLD_RELEASE (document / record hold) | DocumentsReleaseLegalHold, DocumentsReleaseRecordHold (DocumentStores.cs:274-277), Documents | COMPLIANCE_OFFICER; seed CO (OftzaSeed.cs:881) | yes / yes | none | DOCUMENT_LEGAL_HOLD + hold id: no GET by hold id; RECORD_LEGAL_HOLD + id: DocumentsGetRecordHold, needs DOCUMENTS_PLACE_LEGAL_HOLD (DPO only); screen DOCUMENTS not held by CO | Automatic: hold released (Documents.cs:513, RecordHolds.cs:268) | appr/rej: nothing; expiry: follow-up task to DPO | checker can't open the hold (screen and GET functions held only by DATA_PROTECTION_OFFICER) |
| LEGAL_HOLD_RELEASE (search log hold) | PartyReleaseSearchLogHold (PartyStores.cs:258), Party | COMPLIANCE_OFFICER; seed CO | yes / yes | none | SEARCH_LOG_HOLD + hold id; only PartyListSearchLogHolds (CO holds PARTY_SEARCH_LOG) | Automatic (SearchLogRetention.cs:190) | appr/rej: nothing; expiry: follow-up task | - |
| CASE_REDRESS | WorkflowRecordCaseRedress POST /workflow/cases/{id}/redress (CaseRedress.cs:158, CaseStores.cs:428), Workflow | FINANCE_MANAGER; seed: TA fallback | yes / yes | none | CASE_REDRESS + redress id; WorkflowGetCaseRedress needs the case id too; FM lacks SERVICE_COMPLAINTS | Automatic: Approved → CaseRedressApproved → payout payment (CaseRedressPolicies.cs:48-60, Payments.cs:993); then batch | appr/rej: nothing; expiry: follow-up task | `FOLLOW-UP` batch as REFUND; checker can't open the redress; maker CASE_RECORD_REDRESS privileged, granted individually |
| PROVIDER_PAYMENT_RUN | HealthCalculateRun (HealthStores.cs:750), Health | FINANCE_MANAGER; seed FM 5M (OftzaSeedClaims.cs:58) | yes / yes | none | HEALTH_PAYMENT_RUN + id; HealthGetRun | Manual: run Approved only (HealthEventPolicies.cs:97-110); HealthReleaseRun POST /health/payment-runs/{id}/release (HEALTH_RELEASE_RUN: FM, FO) | appr/rej: nothing; expiry: follow-up task | `FOLLOW-UP` release by FM/FO, no task; findable by HealthListRuns status filter |
| PROVIDER_RECOVERY_FINDING | HealthProposeRecoveryFinding (HealthStores.cs:746), Health | none in templates; seed: TA fallback | TA yes / yes | none | HEALTH_RECOVERY + id; HealthGetRecovery (TA lacks HEALTH_RECOVERIES) | Automatic: finding decided, terms set (ProviderRecoveries.cs:492-495); invoicing HealthInvoiceRecovery (FM, FO) unverified whether manual | appr/rej: nothing; expiry: follow-up task | `NO-AUTHORITY` |
| PROVIDER_RECOVERY_WRITE_OFF | HealthWriteOffRecovery (HealthStores.cs:744), Health | none in templates; seed: TA fallback | TA yes / yes | none | HEALTH_RECOVERY + id; HealthGetRecovery | Automatic (ProviderRecoveries.cs:499) | appr/rej: nothing; expiry: follow-up task | `NO-AUTHORITY`; maker HEALTH_WRITE_OFF_RECOVERY privileged, granted individually |
| PROVIDER_CREDIT_NOTE | HealthSubmitCreditNote / portal (HealthStores.cs:748), Health | none in templates; seed: TA fallback | TA yes / yes | none | HEALTH_CREDIT_NOTE + id; HealthGetCreditNote | Automatic: recovery raised from the note (ProviderRecoveries.cs:475-483) | appr/rej: nothing; expiry: follow-up task (none for a portal maker? unverified) | `NO-AUTHORITY` |
| PAYOUT_RELEASE | PaymentsBuildBatch POST /payments/payout-batches (PaymentsStores.cs:255), Payments | FINANCE_MANAGER; seed FM 5M (OftzaSeed.cs:932) | yes / yes | none | PAYOUT_BATCH + id; PaymentsGetBatch | Manual: batch Approved only (Payments.cs:1089-1108); PaymentsReleaseBatch POST …/release (PAYMENTS_RELEASE_BATCH: FINANCE_MANAGER) | appr/rej: nothing; expiry: follow-up task to builder | `FOLLOW-UP` release by FM, no task; findable by PaymentsListBatches status filter |
| UNKNOWN_PAYMENT_RESOLUTION | PaymentsResolveUnknown POST /payments/payments/{id}/resolve (PaymentsStores.cs:257), Payments | FINANCE_MANAGER | yes / yes | none | PAYMENT + id; PaymentsGet | Automatic: payment Succeeded (Payments.cs:1091-1095) | appr/rej: nothing; expiry: follow-up task | seed: TA fallback only (no explicit row) |
| (own rule) data-subject request approve / refuse | PartyApproveDataSubjectRequest, PartyRefuseDataSubjectRequest, Party | function PARTY_APPROVE/REFUSE_DATA_SUBJECT_REQUEST: CLAIMS_HANDLER, CLAIMS_MANAGER, CUSTOMER_SERVICE_AGENT, GENERAL_UNDERWRITER, HEALTH_CLAIMS_ASSESSOR, HEALTH_MEMBERSHIP_ADMINISTRATOR, LIFE_UNDERWRITER | n/a (not Workflow) | - | own record; PartyGetDataSubjectRequest | Manual: export / correction / deletion operations (same roles) | DATA_SUBJECT_REQUEST process task (ProcessInstance.cs:160) | `NO-FILTER` PartyListDataSubjectRequests filters partyId only; DATA_PROTECTION_OFFICER holds none of these functions |
| (own rule) party export approval | PartyApproveExport POST /party/bulk-jobs/{id}/approve, Party | same 7 roles | n/a | - | own record; PartyGetBulkJob | Download PartyDownloadExport | nothing (grep) | - |
| (own rule) Assist platform overlay | AssistApprovePlatformPack, Assist (operator) | ASSIST_OVERLAY_APPROVE privileged, no template | n/a | - | own; AssistPlatformPacksToApprove | - | - | privileged, granted individually |
| (own rule) Assist tenant pack | AssistApproveTenantPack, Assist | ASSIST_OVERLAY_APPROVE_TENANT privileged, no template | n/a | - | own; AssistTenantPacksToApprove | - | - | privileged, granted individually |
| (own rule) SETUP change set abandon | ApproveSetupAbandon / DeclineSetupAbandon, StaticData | STATIC_DATA_SETUP_ABANDON privileged, no template | n/a | - | GetSetupChangeSet | - | - | privileged, granted individually |
| (own rule) privileged access (break-glass) | ApproveAccess / RejectAccess, Identity | TENANT_ADMIN, PLATFORM_ADMIN | n/a | - | own; ListPrivilegedAccess status filter | - | - | - |
| (own rule) smoke run | ApproveSmokeRun, Tenancy | PLATFORM_ADMIN | n/a | - | own | ActivateTenant (PLATFORM_ADMIN) | - | - |
| (own rule) tax return | FinanceApproveTaxReturn / Reject | FINANCE_MANAGER, FINANCE_OFFICER | n/a | - | own | - | - | - |

Note: Compliance returns, Health preauthorisations, claims and chronic registrations are records' own transitions (frontend-integration-guide.md §1), left to the module agents.

### TABLE 2: background states a person must act on

| Background state | Where it's set (file:line) | Where it's listed (operationId, path; tenant-wide?) | Function and roles holding it | How a person learns of it | Gap |
| --- | --- | --- | --- | --- | --- |
| Parked delivery (ADR-022, any prerequisite code) | DeliveryLedger.cs:231 and :344 | ListParkedDeliveries GET /integration/parked-deliveries (status, code, consumer, aggregate filters; tenant-wide); operator: ListParkedDeliveriesByTenant | list INTEGRATION_PARKED_DELIVERIES: AUDITOR, INTEGRATION_ADMINISTRATOR, OPERATIONS_ANALYST; retry RETRY_PARKED_DELIVERY(_FOR_CODE): IA, OA; discard DISCARD_PARKED_DELIVERY privileged (IA) | PARKED_DELIVERY task to ConfigurationOwnerRole (default TENANT_ADMIN, DeliveryLedger.cs:63) then TENANT_ADMIN (ParkingJob.cs:319, Processes.cs:432-438); /health (ParkingJob.cs:468); log | `NO-LIST` the task goes to TENANT_ADMIN, who holds neither WORKFLOW_TASKS nor INTEGRATION_PARKED_DELIVERIES; `STUCK` the task is never closed when the delivery resolves (no ParkedDeliveryResolved handler in Workflow besides Approvals.cs:744) and only OPERATIONS_ANALYST holds WORKFLOW_COMPLETE_TASK; PLATFORM_PARKED_DELIVERIES in no template |
| Approval NotApplied (granted, record refused: APPROVAL_NOT_APPLIED) | Approvals.cs:717-727, Approval.cs:81 | WorkflowListApprovals GET /workflow/approvals?status=NotApplied (tenant-wide, scope-filtered); parked delivery list code=APPROVAL_NOT_APPLIED | WORKFLOW_APPROVALS: AUDITOR, CM, CO, FM, LCM, OA, PA, PSS, TA | follow-up task to requester (Approvals.cs:727); parked-delivery escalation | `NO-LIST` most requesters (claims handler, underwriter, finance officer) hold neither WORKFLOW_TASKS nor WORKFLOW_APPROVALS |
| Approval ending not applied (APPROVAL_ENDING_NOT_APPLIED) | e.g. Payments.cs:1134, CaseRedressPolicies.cs:70, ProviderRecoveries.cs:509 | parked delivery list code=APPROVAL_ENDING_NOT_APPLIED; the approval itself stays Rejected/Expired/Withdrawn | as parked delivery | parked-delivery escalation only; no follow-up, no approval mark (Approvals.cs:733-737 matches APPROVAL_NOT_APPLIED only) | `NO-LIST` requester not told and the record keeps its waiting state, visible only to integration roles |
| Approval Expired | WorkflowJobs.cs:52-54, Approval.cs:345 | WorkflowListApprovals status=Expired (tenant-wide) | WORKFLOW_APPROVALS (as above) | follow-up task to requester (Approvals.cs:527) | `NO-LIST` requester can't see the task (WORKFLOW_TASKS: AUDITOR, OA) |
| Approval Requested, waiting for a checker | RequestApprovalPolicy Approvals.cs:655-690 | WorkflowListApprovals status / decidable=true; WorkflowCountDecidableApprovals | WORKFLOW_APPROVALS | badge only; no notification | `NO-LIST` for LIFE_UNDERWRITING, ENDORSEMENT, PRODUCT_PUBLICATION, PARTY_MATCH_RESOLUTION and the six PSS-only static data modules: no template with authority can decide (Table 1) |
| APPROVAL_FOLLOW_UP task | Approvals.cs:189-206 | WorkflowListOpenTasks GET /workflow/tasks?assigneeId= (tenant-wide) | WORKFLOW_TASKS: AUDITOR, OPERATIONS_ANALYST; complete WORKFLOW_COMPLETE_TASK: OPERATIONS_ANALYST | it is the notice; no "my tasks" operation (/identity/me has none; ops.tsv) | `NO-LIST` the assignee can't list it; `STUCK` nothing closes it (ApprovalFollowUp used only at Approvals.cs:199) and the assignee can't complete it |
| Escalated task (SLA breach) | ProcessInstance.cs:488, EscalateOnSlaBreach Processes.cs:416 | WorkflowListOpenTasks queueCode= | WORKFLOW_TASKS: AUDITOR, OA | moves to escalation role's queue (Processes.cs:357) | `NO-LIST` escalation role sees it only if it holds WORKFLOW_TASKS |
| Claim payment held (reserve, arrears, payee changed, DD not cleared) | Claims.cs:944-949, ClaimPaymentInstruction.cs:338 | only per claim: ClaimsListInstructions GET /claims/claims/{claimNumber}/payment-instructions | CLAIM_PAYMENT_INSTRUCTIONS: AUDITOR, CH, CM; retry/release CLAIMS_RELEASE_INSTRUCTION: CM | CLAIM_PAYMENT_HELD task to HeldReleaseRole default CLAIMS_MANAGER (Claims.cs:206, Processes.cs:502-516); log alarm | `NO-LIST` no tenant-wide instruction list; CLAIMS_MANAGER lacks WORKFLOW_TASKS |
| Party match review (portal registration) | PartyMatchReviewRequested → Processes.cs:534-552 | WorkflowListOpenTasks queueCode=PARTY_REVIEW | WORKFLOW_TASKS: AUDITOR, OA; resolve PARTY_RESOLVE_MATCH: CH, CM, CSA, GU, HCA, HMA, LU | task in queue PARTY_REVIEW (auto-closed on resolve, Processes.cs:568) | `NO-LIST` resolvers can't see the queue |
| Due diligence referred / blocked / review due | Processes.cs:600-650 (queue COMPLIANCE) | PartyListDueDiligence GET /party/due-diligence (status, openHits, reviewDueBy filters; tenant-wide) | PARTY_DUE_DILIGENCE: AUDITOR, CO, GU, HMA, LU | task in queue COMPLIANCE (auto-closed) | - (list is filterable; task queue unseen by CO) |
| Other built-in task queues (CONFIG_REVIEW, AGGREGATION_REVIEW, GENERAL_REFERRAL, HEALTH_ARREARS_TERMINATION, renewal/retention/health reviews) | Processes.cs:456-476, 684-701, 743-763, 784-802 | WorkflowListOpenTasks queueCode= | WORKFLOW_TASKS: AUDITOR, OA | task only | `NO-LIST` the routed roles (underwriters, HEALTH_MEMBERSHIP, config owner) hold no WORKFLOW_TASKS |
| Dead letter | OutboxRelay.cs:160-168 (DeadLetterSink.cs:35), DeadLetter.cs:83 | ListDeadLetters GET /integration/dead-letters (status, source; tenant-wide) | INTEGRATION_DEAD_LETTERS: AUDITOR, IA, OA; replay REPLAY_MESSAGE: IA, OA; discard DISCARD_MESSAGE privileged (IA) | log only (OutboxRelay.cs:163); no task, no event (DeadLetter.cs:129), not in /health (OutboxLagHealthCheck.cs:46 excludes them) | `NO-LIST` nobody is told; later events for the record wait behind it (OutboxRelay.cs:168) |
| Webhook delivery Failed | WebhookDelivery.cs:70, :133 | ListWebhookDeliveries GET /integration/webhook-deliveries (status; tenant-wide) | INTEGRATION_WEBHOOK_DELIVERIES: AUDITOR, IA, OA; retry RETRY_WEBHOOK_DELIVERY: IA, OA | dead-lettered by the caller (WebhookDelivery.cs:64) → as dead letter | as dead letter |
| Notification Failed / Undeliverable | Notification.cs:316 | CommunicationsListNotifications GET /communications/notifications (status; tenant-wide) | COMMUNICATION_NOTIFICATIONS: AUDITOR, CUSTOMER_SERVICE_AGENT | NotificationFailed event (CommunicationsStores.cs:237) has no consumer (grep) | `NO-LIST` nobody told; `STUCK` no resend operation (ops.tsv) |
| Payment Unknown | Payment.cs:157, :527; reconciliation exception Payments.cs:1177 | PaymentsList GET /payments/payments?status (tenant-wide); PaymentsListExceptions; PaymentsSummary figure | PAYMENTS, PAYMENT_RECONCILIATION: AUDITOR, FM, FO; resolve PAYMENTS_RESOLVE_UNKNOWN: FM, FO → UNKNOWN_PAYMENT_RESOLUTION (FM) | dashboard figure PaymentsUnknown (PaymentsInfrastructure.cs:49); no task | - |
| Payment Failed (refund, commission, redress, provider remittance payouts) | Payment.cs:156, :551 | PaymentsList status=FAILED | PAYMENTS: AUDITOR, FM, FO | only ClaimsFramework consumes PaymentFailed (Claims.cs:743, 840); Billing, Distribution, Workflow, Health don't (grep) | `NO-LIST` nobody told and the refund/statement/redress record doesn't show it; `STUCK` no retry operation for non-claim payouts (unverified beyond ops.tsv: only /reverse, /allocate, /resolve) |
| Payout parked (no payout method, payee not payable) | Payments.cs:1188-1190, :1059 | parked delivery list | as parked delivery | parked-delivery escalation | as parked delivery |
| Upload quarantined after 5 failed scans | DocumentScanJob.cs:125-128, Documents.cs:123-126 | none: DocumentsList filters businessRefType/businessRefId only; pending list is internal (DocumentStores.cs:48) | DOCUMENTS (9 roles) | log warning only | `NO-LIST`; `STUCK` no rescan or release operation; comment says "listed as waiting" (Documents.cs:122) but no operation lists it |
| Static data version APPROVED, not active | StaticDataService.cs:378 | per-table version lists only (List<Table>Versions); ListChangeRequests (status, entityCode) shows the change, not activation | SD_{MODULE}_MAINTAIN | nothing | `FOLLOW-UP` (the Preview dead end): no task, no tenant-wide "approved, not yet active" list |
| SETUP change set partly applied / waiting | SetupChangeSet.cs:305-315 | GetSetupChangeSet by id only; no list | STATIC_DATA_CHANGE_REQUESTS: AUDITOR, PSS, TA; abandon STATIC_DATA_SETUP_ABANDON privileged | SetupChangeSetNotApproved / Abandoned events (StaticDataStores.cs:138-141), consumer Assist (unverified) | `NO-LIST` no list of sets |
| Tenant administrator request: identity not created | TenantAdministrators.cs (RetryAdministratorIdentity route) | ListTenantAdministratorRequests (tenantId filter) | IDENTITY_TENANT_ADMINISTRATORS: AUDITOR, PA; retry RETRY_ADMINISTRATOR_IDENTITY: PA | unverified | - |
| Product simulation Failed | SimulationRun.cs:37, :115 | only per version: ProductListSimulations | PRODUCTS: AUDITOR, CO, PM; run PRODUCT_RUN_SIMULATION: PM | synchronous result to the person who ran it | - |
| Fraud indicators not evaluated | Claims.cs:1057-1063 → Processes.cs:456-476 | WorkflowListOpenTasks | WORKFLOW_TASKS: AUDITOR, OA | CONFIG_REVIEW task to config owner role | covered in "Other built-in task queues" |

## Static data, Workflow, Integration, messaging and Assist

**Corrected after review.** The first draft said the roles without `WORKFLOW_APPROVE` couldn't approve static data. They can, on the table's own route (`ApproveStaticDataChange`). The rows below say so. Cells that still say "`NO-ROLE` as above" for approval mean only the six modules held by the steward alone.

Paths: `SDS` = src/BuildingBlocks/Insys.BuildingBlocks.Infrastructure/StaticData/StaticDataService.cs, `SDE` = src/BuildingBlocks/Insys.BuildingBlocks.Api/StaticData/StaticDataEndpoints.cs, `SDD` = …/StaticData/StaticDataDescriptor.cs, `SDC` = src/Modules/StaticData/Insys.StaticData.Domain/Changes/StaticDataChange.cs, `CHG` = src/Modules/StaticData/Insys.StaticData.Application/Changes/Changes.cs, `APR` = src/Modules/Workflow/Insys.Workflow.Application/Approvals.cs. `{M}{T}` = the table's module and entity in the operationId; `<M>` = its catalogue module code.

### StaticData · Static record, Reference lifecycle: 110 tables (`SDD:46`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| DRAFT | none: List{M}{T} (GET /{m}/static/{t}) is in-force only (SDS:108, SDE:94); ListChangeRequests starts at submission (SDC:31); only /{code}/versions shows it | `NO-LIST` an unsubmitted draft is found only by its code, and it blocks any new draft of that code (DRAFT_EXISTS, SDS:156-159) |
| PENDING_APPROVAL | ListChangeRequests GET /static-data/change-requests (status filter, entityCode); WorkflowListApprovals GET /workflow/approvals (status, businessRefType=STATIC_DATA_CHANGE, decidable) | |
| APPROVED | not List{M}{T}; ListChangeRequests?status=Approved (screen STATIC_DATA_CHANGE_REQUESTS: AUDITOR, PLATFORM_STATIC_DATA_STEWARD, TENANT_ADMIN only); WorkflowListApprovals?status=Approved | `NO-LIST` for every module maker except TENANT_ADMIN: LINE_CONFIGURATION_MANAGER, FINANCE_*, DISTRIBUTION_ADMINISTRATOR, PRODUCT_MANAGER, REINSURANCE_OFFICER, INTEGRATION_ADMINISTRATOR, COMPLIANCE_OFFICER hold no change-request list (fn_roles STATIC_DATA_CHANGE_REQUESTS) |
| ACTIVE | List{M}{T} (asAt, cursor; no status filter; in force on asAt only) | |
| DEPRECATED | List{M}{T} (still in force, SDD:47); ListChangeRequests?status=Deprecated | |
| RETIRED | none (only /{code}/versions) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → DRAFT | Create{M}{T}Draft POST /{m}/static/{t} (and /global for platform rows) | SD_<M>_MAINTAIN | per module, table below | no | |
| DRAFT → (deleted) | none: no discard action (SDE:66 allows submit, activate, deprecate, retire); the error text says "discard it" (SDS:159) | - | - | - | `NO-OP` a draft can't be discarded; only a SETUP batch's drafts are deleted (SDS:733-737) |
| DRAFT → PENDING_APPROVAL | Change{M}{T}Lifecycle POST /{m}/static/{t}/{code}/versions/{n}/submit (maker only, SDS:239) | SD_<M>_MAINTAIN | per module | yes: approval type STATIC_DATA_T1/T2 (requester ≠ checker, SDC:204-209); Tier 3, or Tier 2 with the switch off, approves on submission (SDC:144-148) | |
| PENDING_APPROVAL → APPROVED | WorkflowApprove POST /workflow/approvals/{id}/approve, applied by event (CHG:304, SDS:360-381) | SD_<M>_APPROVE + authority STATIC_DATA_T1/T2, on the table's route ApproveStaticDataChange (WorkflowModule.cs:355-406) or in the inbox with WORKFLOW_APPROVE (APR:256) | WORKFLOW_APPROVE: CLAIMS_MANAGER, COMPLIANCE_OFFICER, FINANCE_MANAGER, LINE_CONFIGURATION_MANAGER, PLATFORM_ADMIN, PLATFORM_STATIC_DATA_STEWARD, TENANT_ADMIN; effective deciders per module in the table below | yes: STATIC_DATA_T1/T2 | `NO-ROLE` tenant rows of the 6 steward-only modules (table below); `NO-LIST` PRODUCT_MANAGER, FINANCE_OFFICER, DISTRIBUTION_ADMINISTRATOR, REINSURANCE_OFFICER, INTEGRATION_ADMINISTRATOR can decide on the table's route but hold neither the inbox nor the change-request list, so nothing shows them what waits |
| PENDING_APPROVAL → DRAFT | WorkflowReject POST …/reject; WorkflowWithdraw POST …/withdraw (requester); expiry job WorkflowJobs.cs:54; applied by ApplyRejection (SDS:385-396) | WORKFLOW_REJECT + SD_<M>_APPROVE; withdraw: signed-in requester; expiry: job | WORKFLOW_REJECT: as WORKFLOW_APPROVE | reject: checker ≠ maker (SDC:236) | |
| APPROVED → ACTIVE | Change{M}{T}Lifecycle POST …/{n}/activate (SDS:334-340) | SD_<M>_MAINTAIN | per module | no | `FOLLOW-UP` approval doesn't activate; a SD_<M>_MAINTAIN holder must activate, and nothing tells them: no notification or task on approval (RequesterFollowUp opens tasks only for expiry, withdrawal, not-applied, APR:527/702/728) and the version is off the table's list |
| ACTIVE → DEPRECATED | Change{M}{T}Lifecycle POST …/deprecate (SDS:342-347) | SD_<M>_MAINTAIN | per module | no | |
| DEPRECATED → RETIRED | Change{M}{T}Lifecycle POST …/retire (SDS:349-354) | SD_<M>_MAINTAIN | per module | no | |
| (import) → DRAFT lines | Import{M}{T} POST /{m}/static/{t}/imports | SD_<M>_MAINTAIN | per module | no | |
| import DRAFT → PENDING_APPROVAL | Change{M}{T}Import POST …/imports/{batchId}/submit (SDS:600-660) | SD_<M>_MAINTAIN | per module | yes: one STATIC_DATA_T{tier} approval bound to the import hash | |
| import PENDING_APPROVAL → APPROVED | WorkflowApprove, applied by ApplyImportApproval (SDS:673-716) | WORKFLOW_APPROVE + SD_<M>_APPROVE | as above | yes: STATIC_DATA_T1/T2 | `NO-ROLE` as the single-record row |
| import PENDING_APPROVAL → DRAFT | WorkflowReject/Withdraw/expiry, ApplyImportRejection (SDS:718-742) | as reject | as reject | yes | |
| import APPROVED → ACTIVE | Change{M}{T}Import POST …/imports/{batchId}/activate (SDS:744-770) | SD_<M>_MAINTAIN | per module | no | `FOLLOW-UP` as single-record activation; the batch is found only by its batchId (GET …/imports/{batchId}) or the change request |
| SETUP batch PENDING_APPROVAL → ACTIVE | event: the SETUP change set's one approval, applied in order (SDS:679-704, CHG:408-470) | event | - | yes: STATIC_DATA_SETUP approval (STATIC_DATA_T1) | |
Note: global rows (/global routes) are platform-operator only (SDE: Secure(global)); in a business tenant PLATFORM_STATIC_DATA_STEWARD (operator tenant only, RoleTemplates.cs:223) cannot act.

### StaticData · Static record, Configuration lifecycle: 185 tables (`SDD:54`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| DRAFT | none (as Reference) | `NO-LIST` as Reference: found only by code, blocks new drafts (SDS:156-159) |
| VALIDATED | never stored: submit writes SUBMITTED directly (SDS:254, `PendingApproval => Submitted[^1]` SDD:57) | `SPEC-DIFF` §7.7 / SDD:50 comment "Submit records Validated then Submitted"; code skips it |
| SUBMITTED | ListChangeRequests?status=PendingApproval; WorkflowListApprovals | |
| APPROVED | ListChangeRequests?status=Approved (TENANT_ADMIN, AUDITOR, steward only); WorkflowListApprovals?status=Approved | `NO-LIST` as Reference |
| PUBLISHED | List{M}{T} (in force, no status filter) | |
| EFFECTIVE | List{M}{T} would show it (SDD:55), but nothing sets it | `NO-OP` "TODO(spec): who moves Published to Effective" (SDD:51) |
| WITHDRAWN | none in List{M}{T} (not in force); ListChangeRequests?status=Deprecated | |
| RETIRED | none (only /{code}/versions) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → DRAFT | Create{M}{T}Draft POST /{m}/static/{t} | SD_<M>_MAINTAIN | per module | no | |
| DRAFT → (deleted) | none (SDE:66) | - | - | - | `NO-OP` no discard, as Reference |
| DRAFT → SUBMITTED | Change{M}{T}Lifecycle POST …/submit | SD_<M>_MAINTAIN | per module | yes: STATIC_DATA_T1/T2 | |
| SUBMITTED → APPROVED | WorkflowApprove (event, SDS:360) | SD_<M>_APPROVE on the table's route (or the inbox) | effective deciders per module below | yes: STATIC_DATA_T1/T2 | `NO-ROLE` the 6 steward-only modules' tenant rows; `NO-LIST` pending changes for the other deciders (table below) |
| SUBMITTED → DRAFT | WorkflowReject / WorkflowWithdraw / expiry job (SDS:385) | WORKFLOW_REJECT + SD_<M>_APPROVE; requester; job | as reject | yes | |
| APPROVED → PUBLISHED | Change{M}{T}Lifecycle POST …/activate (SDS:334) | SD_<M>_MAINTAIN | per module | no | `FOLLOW-UP` as Reference: maintainer must activate, not told |
| PUBLISHED → EFFECTIVE | none | - | - | - | `SPEC-DIFF` SDD:51; not a dead end: PUBLISHED is in force and can be retired |
| PUBLISHED/EFFECTIVE → WITHDRAWN | Change{M}{T}Lifecycle POST …/deprecate (SDS:342) | SD_<M>_MAINTAIN | per module | no | |
| PUBLISHED/EFFECTIVE/WITHDRAWN → RETIRED | Change{M}{T}Lifecycle POST …/retire (SDS:349-354) | SD_<M>_MAINTAIN | per module | no | |
| import (new) → DRAFT → SUBMITTED | Import{M}{T}; Change{M}{T}Import …/submit | SD_<M>_MAINTAIN | per module | yes: one approval per import | |
| import SUBMITTED → APPROVED / DRAFT | WorkflowApprove / WorkflowReject (SDS:673, 718) | WORKFLOW_APPROVE/REJECT + SD_<M>_APPROVE | as above | yes | `NO-ROLE` as above |
| import APPROVED → PUBLISHED | Change{M}{T}Import …/activate (SDS:744) | SD_<M>_MAINTAIN | per module | no | `FOLLOW-UP` as Reference |
| SETUP batch SUBMITTED → PUBLISHED | event (SDS:679-704) | event | - | yes: STATIC_DATA_SETUP | |

Static data functions per catalogue module (generated descriptors: 295 tables; tenant-row = scope T or GT):
| Catalogue module (SD_<M>_*) | Tables (Ref/Cfg) | Tenant-row tables at tier 1-2 | SD_<M>_MAINTAIN (create, submit, activate, deprecate, retire) | SD_<M>_APPROVE | Tenant templates that can decide (on the table's own route, with STATIC_DATA_T1/T2 authority) | Gap |
| --- | --- | --- | --- | --- | --- | --- |
| ANALYTICS | 2 (1/1) | 2 | PLATFORM_STATIC_DATA_STEWARD | PLATFORM_STATIC_DATA_STEWARD | none | `NO-ROLE` tenant rows: no tenant template maintains or approves (StaticDataFunctions.cs:50, Approvals.cs:256) |
| AUDIT | 2 (1/1) | 1 | PLATFORM_STATIC_DATA_STEWARD | PLATFORM_STATIC_DATA_STEWARD | none | `NO-ROLE` tenant rows: no tenant template maintains or approves (StaticDataFunctions.cs:50, Approvals.cs:256) |
| BENEFICIARY_MANAGEMENT | 2 (1/1) | 1 | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER |  |
| BENEFITS_ADMINISTRATION | 4 (1/3) | 3 | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER |  |
| BENEFIT_MANAGEMENT | 4 (2/2) | 4 | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER |  |
| BILLING_AND_PREMIUM_RECEIVABLES | 6 (3/3) | 6 | FINANCE_MANAGER,FINANCE_OFFICER,PLATFORM_STATIC_DATA_STEWARD | FINANCE_MANAGER,FINANCE_OFFICER,PLATFORM_STATIC_DATA_STEWARD | FINANCE_MANAGER |  |
| CASH_VALUES_AND_POLICY_LOANS | 4 (0/4) | 4 | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER |  |
| CLOUD_DEPLOYMENT_AND_OPERATIONS | 2 (0/2) | 0 | PLATFORM_STATIC_DATA_STEWARD,TENANT_ADMIN | PLATFORM_STATIC_DATA_STEWARD,TENANT_ADMIN | TENANT_ADMIN |  |
| COMMERCIAL_INSURANCE | 3 (2/1) | 2 | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER |  |
| COMMISSION_AND_DISTRIBUTION | 6 (2/4) | 5 | DISTRIBUTION_ADMINISTRATOR,PLATFORM_STATIC_DATA_STEWARD | DISTRIBUTION_ADMINISTRATOR,PLATFORM_STATIC_DATA_STEWARD | DISTRIBUTION_ADMINISTRATOR | `NO-LIST` DISTRIBUTION_ADMINISTRATOR can decide on the table's route but has no list of pending changes (not in the table's list; no inbox; no change-request list) |
| COMMUNICATIONS | 4 (1/3) | 3 | PLATFORM_STATIC_DATA_STEWARD | PLATFORM_STATIC_DATA_STEWARD | none | `NO-ROLE` tenant rows: no tenant template maintains or approves (StaticDataFunctions.cs:50, Approvals.cs:256) |
| COMPLIANCE_AND_REPORTING | 4 (1/3) | 3 | COMPLIANCE_OFFICER,PLATFORM_STATIC_DATA_STEWARD | COMPLIANCE_OFFICER,PLATFORM_STATIC_DATA_STEWARD | COMPLIANCE_OFFICER |  |
| COVERAGE_MANAGEMENT | 3 (1/2) | 3 | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER |  |
| DISTRIBUTION | 1 (0/1) | 1 | DISTRIBUTION_ADMINISTRATOR,PLATFORM_STATIC_DATA_STEWARD | DISTRIBUTION_ADMINISTRATOR,PLATFORM_STATIC_DATA_STEWARD | DISTRIBUTION_ADMINISTRATOR | `NO-LIST` DISTRIBUTION_ADMINISTRATOR can decide on the table's route but has no list of pending changes (not in the table's list; no inbox; no change-request list) |
| DOCUMENT_AND_CONTENT_MANAGEMENT | 6 (3/3) | 5 | PLATFORM_STATIC_DATA_STEWARD | PLATFORM_STATIC_DATA_STEWARD | none | `NO-ROLE` tenant rows: no tenant template maintains or approves (StaticDataFunctions.cs:50, Approvals.cs:256) |
| ELIGIBILITY_MANAGEMENT | 1 (1/0) | 1 | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER |  |
| FINANCIAL_SUBLEDGER | 8 (3/5) | 8 | FINANCE_MANAGER,FINANCE_OFFICER,PLATFORM_STATIC_DATA_STEWARD | FINANCE_MANAGER,FINANCE_OFFICER,PLATFORM_STATIC_DATA_STEWARD | FINANCE_MANAGER |  |
| GENERAL_CLAIMS | 7 (5/2) | 6 | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER |  |
| GENERAL_POLICY_ADMINISTRATION | 4 (0/4) | 4 | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER |  |
| GENERAL_REINSURANCE_AND_ACTUARIAL_INTERFACES | 1 (0/1) | 1 | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER |  |
| GENERAL_UNDERWRITING | 3 (2/1) | 3 | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER |  |
| GROUP_LIFE | 3 (1/2) | 3 | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER |  |
| HEALTH_CLAIMS | 7 (0/7) | 7 | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER |  |
| HEALTH_FRAUD_AND_ABUSE_CONTROLS | 2 (1/1) | 2 | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER |  |
| HEALTH_PAYMENTS | 2 (0/2) | 2 | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER |  |
| HEALTH_PRODUCT_AND_PLAN | 5 (0/5) | 5 | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER |  |
| IDENTITY_AND_ACCESS_MANAGEMENT | 11 (4/7) | 7 | TENANT_ADMIN | TENANT_ADMIN (privileged, named grant) | TENANT_ADMIN |  |
| INTEGRATION_AND_API_MANAGEMENT | 9 (3/6) | 6 | INTEGRATION_ADMINISTRATOR,PLATFORM_STATIC_DATA_STEWARD | INTEGRATION_ADMINISTRATOR,PLATFORM_STATIC_DATA_STEWARD | INTEGRATION_ADMINISTRATOR | `NO-LIST` INTEGRATION_ADMINISTRATOR can decide on the table's route but has no list of pending changes (not in the table's list; no inbox; no change-request list) |
| LIFE_ACTUARIAL_INTERFACES | 2 (0/2) | 2 | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER |  |
| LIFE_CLAIMS | 4 (2/2) | 3 | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER |  |
| LIFE_POLICY_ADMINISTRATION | 4 (2/2) | 4 | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER |  |
| LIFE_PROPOSAL_MANAGEMENT | 3 (1/2) | 2 | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER |  |
| LIFE_UNDERWRITING | 5 (4/1) | 5 | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER |  |
| MARINE_INSURANCE | 4 (4/0) | 2 | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER |  |
| MATURITY_MANAGEMENT | 2 (1/1) | 2 | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER |  |
| MEDICAL_CLAIMS | 4 (2/2) | 3 | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER |  |
| MEMBERSHIP_AND_ELIGIBILITY | 2 (0/2) | 2 | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER |  |
| MEMBER_ADMINISTRATION | 4 (1/3) | 3 | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER |  |
| MOTOR_INSURANCE | 5 (5/0) | 2 | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER |  |
| OBSERVABILITY | 2 (0/2) | 0 | PLATFORM_STATIC_DATA_STEWARD,TENANT_ADMIN | PLATFORM_STATIC_DATA_STEWARD,TENANT_ADMIN | TENANT_ADMIN |  |
| PARTY_MANAGEMENT | 17 (8/9) | 10 | PLATFORM_STATIC_DATA_STEWARD | PLATFORM_STATIC_DATA_STEWARD | none | `NO-ROLE` tenant rows: no tenant template maintains or approves (StaticDataFunctions.cs:50, Approvals.cs:256) |
| PAYMENTS_AND_SETTLEMENTS | 7 (6/1) | 6 | FINANCE_MANAGER,FINANCE_OFFICER,PLATFORM_STATIC_DATA_STEWARD | FINANCE_MANAGER,FINANCE_OFFICER,PLATFORM_STATIC_DATA_STEWARD | FINANCE_MANAGER |  |
| PLATFORM_REFERENCE_DATA | 7 (7/0) | 1 | PLATFORM_STATIC_DATA_STEWARD,TENANT_ADMIN | PLATFORM_STATIC_DATA_STEWARD,TENANT_ADMIN | TENANT_ADMIN |  |
| POLICY_ADMINISTRATION | 7 (0/7) | 7 | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER |  |
| PORTALS_AND_PARTNER_CHANNELS | 5 (1/4) | 3 | INTEGRATION_ADMINISTRATOR,PLATFORM_STATIC_DATA_STEWARD | INTEGRATION_ADMINISTRATOR,PLATFORM_STATIC_DATA_STEWARD | INTEGRATION_ADMINISTRATOR | `NO-LIST` INTEGRATION_ADMINISTRATOR can decide on the table's route but has no list of pending changes (not in the table's list; no inbox; no change-request list) |
| PREAUTHORISATION_MANAGEMENT | 4 (1/3) | 3 | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER |  |
| PREMIUM_ADMINISTRATION | 3 (0/3) | 3 | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER |  |
| PRODUCT_FACTORY | 11 (1/10) | 11 | PLATFORM_STATIC_DATA_STEWARD,PRODUCT_MANAGER | PLATFORM_STATIC_DATA_STEWARD,PRODUCT_MANAGER | PRODUCT_MANAGER | `NO-LIST` PRODUCT_MANAGER can decide on the table's route but has no list of pending changes (not in the table's list; no inbox; no change-request list) |
| PROPERTY_INSURANCE | 4 (3/1) | 4 | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER |  |
| PROVIDER_NETWORK_MANAGEMENT | 7 (3/4) | 7 | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER |  |
| QUOTATION_AND_PROPOSAL | 3 (1/2) | 2 | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER |  |
| RATING_FRAMEWORK | 9 (1/8) | 9 | PLATFORM_STATIC_DATA_STEWARD,PRODUCT_MANAGER | PLATFORM_STATIC_DATA_STEWARD,PRODUCT_MANAGER | PRODUCT_MANAGER | `NO-LIST` PRODUCT_MANAGER can decide on the table's route but has no list of pending changes (not in the table's list; no inbox; no change-request list) |
| REINSURANCE | 5 (2/3) | 5 | PLATFORM_STATIC_DATA_STEWARD,REINSURANCE_OFFICER | PLATFORM_STATIC_DATA_STEWARD,REINSURANCE_OFFICER | REINSURANCE_OFFICER | `NO-LIST` REINSURANCE_OFFICER can decide on the table's route but has no list of pending changes (not in the table's list; no inbox; no change-request list) |
| RISK_OBJECT_MANAGEMENT | 4 (2/2) | 4 | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER |  |
| RULES_ENGINE | 7 (1/6) | 7 | PLATFORM_STATIC_DATA_STEWARD,PRODUCT_MANAGER | PLATFORM_STATIC_DATA_STEWARD,PRODUCT_MANAGER | PRODUCT_MANAGER | `NO-LIST` PRODUCT_MANAGER can decide on the table's route but has no list of pending changes (not in the table's list; no inbox; no change-request list) |
| SCHEME_AND_GROUP_ADMINISTRATION | 4 (1/3) | 4 | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER |  |
| SHARED_CLAIMS_FRAMEWORK | 6 (4/2) | 6 | PLATFORM_STATIC_DATA_STEWARD | PLATFORM_STATIC_DATA_STEWARD | none | `NO-ROLE` tenant rows: no tenant template maintains or approves (StaticDataFunctions.cs:50, Approvals.cs:256) |
| STATIC_DATA_MANAGEMENT | 3 (1/2) | 1 | PLATFORM_STATIC_DATA_STEWARD,TENANT_ADMIN | PLATFORM_STATIC_DATA_STEWARD,TENANT_ADMIN | TENANT_ADMIN |  |
| TENANT_MANAGEMENT | 7 (1/6) | 4 | PLATFORM_STATIC_DATA_STEWARD,TENANT_ADMIN | PLATFORM_STATIC_DATA_STEWARD,TENANT_ADMIN | TENANT_ADMIN |  |
| UTILISATION_MANAGEMENT | 3 (0/3) | 3 | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER,PLATFORM_STATIC_DATA_STEWARD | LINE_CONFIGURATION_MANAGER |  |
| WORKFLOW_AND_CASE_MANAGEMENT | 15 (5/10) | 14 | PLATFORM_STATIC_DATA_STEWARD,TENANT_ADMIN | PLATFORM_STATIC_DATA_STEWARD,TENANT_ADMIN | TENANT_ADMIN |  |
Note: a module where the only tenant decider is the maintaining template (LINE_CONFIGURATION_MANAGER, TENANT_ADMIN) needs two people holding it (checker ≠ maker, SDC:214).

### StaticData · StaticDataChange (change request) (`SDC:72`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| PendingApproval | ListChangeRequests GET /static-data/change-requests (status, entityCode filters) | |
| Approved | ListChangeRequests?status=Approved | `NO-LIST` the list's function STATIC_DATA_CHANGE_REQUESTS is only in AUDITOR, PLATFORM_STATIC_DATA_STEWARD, TENANT_ADMIN, not the module makers who must activate |
| Rejected | ListChangeRequests?status=Rejected | |
| Active | ListChangeRequests?status=Active | |
| Deprecated | ListChangeRequests?status=Deprecated | |
| Retired | ListChangeRequests?status=Retired would show it, but nothing sets it | `NO-OP` see transitions |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → PendingApproval | event StaticDataChangeSubmitted / StaticDataImportSubmitted (CHG:242-300) from the table's submit | event | - | yes: STATIC_DATA_T1/T2 | |
| (new) → Approved | same event, Tier 3 or Tier 2 switched off (SDC:144-148) | event | - | no (by rule) | |
| PendingApproval → Approved | event ApprovalGranted (CHG:304-325) | event | - | yes: STATIC_DATA_T1/T2 (SDC:214) | |
| PendingApproval → Rejected | event ApprovalRejected (rejected, expired, withdrawn) (CHG:326-350) | event | - | yes (SDC:236) | |
| Approved → Active | event StaticDataVersionActivated (CHG:509-515), from the table's activate | event | - | no | `SPEC-DIFF` domain-model.json says "Effective date reached" (automatic); code needs a manual activate (SDS:334) |
| Approved → Rejected | AbandonSetupBatch, from SetupChangeSetAbandoned (SDC:256-283, CHG:398) | event | - | yes: abandon is requester ≠ approver | `SPEC-DIFF` not in domain-model.json (OQ-183) |
| Active → Deprecated | event StaticDataCodeDeprecated (CHG:517-523) | event | - | no | |
| Deprecated → Retired | none: "No event reports retirement yet (OQ-028), so nothing calls it" (SDC:288); Retire publishes nothing (SDS:354) | - | - | - | `NO-OP` a retired version's change request stays Deprecated; a Configuration version retired from PUBLISHED stays Active (SDS:353) |

### StaticData · ImportBatch (`SDC:338`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| Committed | ListImportBatches GET /static-data/imports (cursor, limit; no status filter) | |
| Rejected | ListImportBatches (no status filter) | `NO-FILTER` rejected batches mixed with committed (CHG:191) |
| Submitted / Validating | never stored: the batch is recorded only after the module has committed or rejected (SDC:357-361) | `SPEC-DIFF` domain-model.json has Submitted → Validating → Rejected/Committed |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → Committed | event StaticDataImportCompleted (CHG:525-531), from Import{M}{T} | event | - | no (the lines' approval is the change request) | |
| (new) → Rejected | event StaticDataImportRejected (CHG:533-541) | event | - | no | |
Note: ImportBatch records the load only; the lines' approval and activation are the change request and the record lifecycle above.

### StaticData · SetupChangeSet (`src/Modules/StaticData/Insys.StaticData.Domain/Changes/SetupChangeSet.cs:100`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| SUBMITTED | none of sets: GetSetupChangeSet GET /static-data/setup-sets/{id} only; its approval in WorkflowListApprovals?businessRefType=STATIC_DATA_SETUP | |
| APPLYING | none: only GetSetupChangeSet by id, or the Assist setup's SetId (AssistGetTenantSetup) | `NO-LIST` a set stuck applying (a batch parked as APPROVAL_NOT_APPLIED, CHG:411-418) is found only through parked deliveries |
| APPLIED | none (by id) | |
| NOT_APPROVED | none (by id) | |
| ABANDONED | none (by id) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → SUBMITTED | event SetupChangeSetRequested from Assist submit (CHG:352-372) | event | - | yes: STATIC_DATA_SETUP approval, STATIC_DATA_T1 | |
| (new) → NOT_APPROVED | event SetupChangeSetWithdrawn before the set existed (SetupChangeSet.cs:181) | event | - | no | |
| SUBMITTED → APPLYING | WorkflowApprove (businessRefType STATIC_DATA_SETUP) → GrantSetup (CHG:424-450); tenant must be ConfigurationOnly | WORKFLOW_APPROVE | CLAIMS_MANAGER, COMPLIANCE_OFFICER, FINANCE_MANAGER, LINE_CONFIGURATION_MANAGER, PLATFORM_ADMIN, PLATFORM_STATIC_DATA_STEWARD, TENANT_ADMIN (authority STATIC_DATA_T1: TENANT_ADMIN, LINE_CONFIGURATION_MANAGER, FINANCE_MANAGER, COMPLIANCE_OFFICER) | yes: STATIC_DATA_T1 (requester ≠ checker) | |
| SUBMITTED → NOT_APPROVED | WorkflowReject / WorkflowWithdraw / expiry (SetupChangeSet.cs:347-366) | WORKFLOW_REJECT; requester; job | as WORKFLOW_APPROVE | yes | |
| APPLYING → APPLIED | event StaticDataSetupBatchApplied, last batch (SetupChangeSet.cs:262-266, CHG:408-421) | event | - | - | |
| APPLYING → (abandon requested) | RequestSetupAbandon POST /static-data/setup-sets/{id}/abandon | STATIC_DATA_SETUP_ABANDON | - (privileged, no template names it) | yes: own rule, requester ≠ approver (SetupChangeSet.cs:276-345) | `NO-ROLE` privileged, granted individually, and it is the only way out of APPLYING when a batch can't apply (`STUCK` otherwise) |
| APPLYING → ABANDONED | ApproveSetupAbandon POST …/abandon/approve | STATIC_DATA_SETUP_ABANDON | - (privileged) | yes: own rule | `NO-ROLE` as above; needs two individually granted people |
| (abandon requested) → APPLYING | DeclineSetupAbandon POST …/abandon/decline | STATIC_DATA_SETUP_ABANDON | - (privileged) | yes: own rule | |

### StaticData · TenantGovernanceSettings, Tier 2 maker-checker switch (`SDC:417`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| On (Tier2MakerChecker=true, nothing pending) | GetGovernanceSettings GET /static-data/settings (one record per tenant) | |
| Off pending approval (PendingTier2MakerChecker=false) | GetGovernanceSettings; its approval in WorkflowListApprovals?businessRefType=GOVERNANCE_SETTING | |
| Off | GetGovernanceSettings | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| On → Off pending | ChangeTier2MakerChecker PUT /static-data/settings/tier2-maker-checker {enabled:false} (CHG:117, SDC:449) | STATIC_DATA_CHANGE_TIER2_MAKER_CHECKER | - (privileged, no template names it) | yes: approval type STATIC_DATA_T1 (requester ≠ checker, SDC:473-482) | |
| Off pending → Off | WorkflowApprove (businessRefType GOVERNANCE_SETTING, CHG:497-506) | WORKFLOW_APPROVE | as WORKFLOW_APPROVE (authority STATIC_DATA_T1) | yes: STATIC_DATA_T1 | |
| Off pending → On | WorkflowReject / WorkflowWithdraw / expiry, or the same PUT {enabled:true} (SDC:435-446) | WORKFLOW_REJECT; requester; job; STATIC_DATA_CHANGE_TIER2_MAKER_CHECKER | as WORKFLOW_APPROVE; - (privileged) | yes / no for switching on | |
| Off → On | ChangeTier2MakerChecker PUT {enabled:true} (takes effect at once) | STATIC_DATA_CHANGE_TIER2_MAKER_CHECKER | - (privileged, no template names it) | no (tightens control) | |

### Workflow · Approval (`src/Modules/Workflow/Insys.Workflow.Domain/Approvals/Approval.cs:74`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| Requested | WorkflowListApprovals GET /workflow/approvals (status, businessRefType, businessRefId, decidable) | |
| Approved | WorkflowListApprovals?status=Approved | |
| NotApplied | WorkflowListApprovals?status=NotApplied | |
| Rejected | WorkflowListApprovals?status=Rejected | |
| Expired | WorkflowListApprovals?status=Expired | |
| Withdrawn | WorkflowListApprovals?status=Withdrawn | |
Note: the list's function WORKFLOW_APPROVALS: AUDITOR, CLAIMS_MANAGER, COMPLIANCE_OFFICER, FINANCE_MANAGER, LINE_CONFIGURATION_MANAGER, OPERATIONS_ANALYST, PLATFORM_ADMIN, PLATFORM_STATIC_DATA_STEWARD, TENANT_ADMIN.

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → Requested | event from the owning module (StaticDataApprovalNeeded, CaseRedress, …) | event | - | - | |
| Requested → Approved | WorkflowApprove POST /workflow/approvals/{id}/approve | WORKFLOW_APPROVE (+ the owning module's function, APR:256) | CLAIMS_MANAGER, COMPLIANCE_OFFICER, FINANCE_MANAGER, LINE_CONFIGURATION_MANAGER, PLATFORM_ADMIN, PLATFORM_STATIC_DATA_STEWARD, TENANT_ADMIN | yes: own rule (not the maker, within authority; Approval.cs:190) | |
| Requested → Rejected | WorkflowReject POST …/reject | WORKFLOW_REJECT | as WORKFLOW_APPROVE | yes: own rule | |
| Requested → Expired | job WorkflowJobs.cs:54 → ExpireApprovalHandler (APR:510-529) | job | - | - | |
| Requested → Withdrawn | WorkflowWithdraw POST …/withdraw (requester); or event ApprovalWithdrawn from the module (APR:695-704) | - (signed-in requester) / event | any signed-in requester | no | |
| Approved → NotApplied | event integration.DeliveryParked with APPROVAL_NOT_APPLIED (APR:713-730) | event | - | - | `FOLLOW-UP` a follow-up task goes to the requester in queue REQUESTER (APR:185-205), but WorkflowListOpenTasks (WORKFLOW_TASKS) is only AUDITOR, OPERATIONS_ANALYST |
| NotApplied → Approved | event integration.ParkedDeliveryResolved (APR:742-755), after RetryParkedDelivery | event | - | - | |
| NotApplied → (closed) | none: discarding the parked delivery (DiscardParkedDelivery) publishes no event (ParkedDeliveryStore.cs:154-160; only DeliveryParked and ParkedDeliveryResolved exist, Integration.Contracts/ParkedDeliveries.cs:53-57) | - | - | - | `STUCK` an approval whose parked delivery is discarded stays NotApplied for ever |
| Requested (approved) → follow-up | none on approval or rejection: RequesterFollowUp only on expiry, withdrawal, not-applied (APR:527, 702, 728) | - | - | - | `FOLLOW-UP` nobody is led to the step after an approval (static data activate, import activate) |
| (domain-model.json) | spec has Requested→Approved/Rejected/Expired/Withdrawn only | - | - | - | `SPEC-DIFF` code adds NotApplied and its two transitions (Approval.cs:81-82) |

### Workflow · ProcessInstance (`src/Modules/Workflow/Insys.Workflow.Domain/Processes/ProcessInstance.cs:300`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| Running | WorkflowListProcesses GET /workflow/processes (businessRefType, businessRefId only) | `NO-FILTER` no status filter (Processes.cs:143) |
| Suspended | WorkflowListProcesses (no status filter) | `NO-FILTER` a suspended process (someone must resume it) is found only by paging every process |
| Completed | WorkflowListProcesses (no status filter) | |
| Cancelled | WorkflowListProcesses (no status filter) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → Running | WorkflowStartProcess POST /workflow/processes; or events (RequesterFollowUp, CareActivityFollowUpTasks, …) | WORKFLOW_START_PROCESS / event | OPERATIONS_ANALYST | no | |
| Running → Suspended | WorkflowSuspendProcess POST /workflow/processes/{id}/suspend | WORKFLOW_SUSPEND_PROCESS | OPERATIONS_ANALYST | no | |
| Suspended → Running | WorkflowResumeProcess POST …/resume | WORKFLOW_RESUME_PROCESS | OPERATIONS_ANALYST | no | |
| Running → Completed | WorkflowCompleteTask POST …/tasks/{taskId}/complete into a final state | WORKFLOW_COMPLETE_TASK | OPERATIONS_ANALYST | no | `FOLLOW-UP` requester follow-up tasks (APPROVAL_FOLLOW_UP, assigned to the maker, APR:199-201) can be completed only by OPERATIONS_ANALYST |
| Running → Cancelled | WorkflowCancelProcess POST …/cancel | WORKFLOW_CANCEL_PROCESS | OPERATIONS_ANALYST | no | |

### Workflow · ProcessTask (`src/Modules/Workflow/Insys.Workflow.Domain/Processes/ProcessInstance.cs:174`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| Open | WorkflowListOpenTasks GET /workflow/tasks (queueCode, assigneeId) | `NO-LIST` for the assignee: WORKFLOW_TASKS is only AUDITOR, OPERATIONS_ANALYST, so a maker can't see the REQUESTER-queue task assigned to them (APR:187, 201) |
| Completed | only under its parent: WorkflowGetProcess | |
| Cancelled | only under its parent: WorkflowGetProcess | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → Open | process start / previous task completed (ProcessInstance.cs:416) | WORKFLOW_START_PROCESS / event | OPERATIONS_ANALYST | no | |
| Open → Open (assign, escalate) | WorkflowAssignTask / WorkflowEscalateTask POST …/tasks/{taskId}/assign, /escalate | WORKFLOW_ASSIGN_TASK / WORKFLOW_ESCALATE_TASK | OPERATIONS_ANALYST | no | |
| Open → Completed | WorkflowCompleteTask POST …/tasks/{taskId}/complete | WORKFLOW_COMPLETE_TASK | OPERATIONS_ANALYST | no | |
| Open → Cancelled | WorkflowCancelProcess (cancels the open task, ProcessInstance.cs:225) | WORKFLOW_CANCEL_PROCESS | OPERATIONS_ANALYST | no | |
| (domain-model.json) | Task has no stateTransitions in the spec | - | - | - | `SPEC-DIFF` Task statuses only in code |

### Workflow · ServiceCase (`src/Modules/Workflow/Insys.Workflow.Domain/Cases/ServiceCase.cs:266`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| Received | WorkflowListCases GET /workflow/cases (family, type, status, category, overdue, link); family lists (WorkflowListEnquiries, …Disputes, …Appeals, …ClaimDisputes, …ClaimAppeals, …HealthClaimAppeals, …ProviderDisputes), all with status | |
| Acknowledged | as Received | |
| InProgress | as Received | |
| AwaitingComplainant | as Received | |
| Decided | as Received | |
| Closed | as Received | |
| Withdrawn | as Received | |
| Reopened | as Received | |
Note: Health-linked cases are listed only to callers with medical_claim.read (CaseEndpoints.cs:126); whether CUSTOMER_SERVICE_AGENT or COMPLIANCE_OFFICER hold it is unverified (permission, not a function in fn_roles.tsv).

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → Received | WorkflowOpenCase POST /workflow/cases; PortalRaiseCase POST /portal/cases; events | CASE_REGISTER / portal functions | COMPLIANCE_OFFICER, CUSTOMER_SERVICE_AGENT | no | |
| Received → Acknowledged | WorkflowAcknowledgeCase POST /workflow/cases/{id}/acknowledge | CASE_ACKNOWLEDGE | COMPLIANCE_OFFICER, CUSTOMER_SERVICE_AGENT | no | |
| Acknowledged → InProgress | WorkflowStartCase POST …/start | CASE_UPDATE | COMPLIANCE_OFFICER, CUSTOMER_SERVICE_AGENT | no | |
| Reopened → InProgress | WorkflowStartCase POST …/start | CASE_UPDATE | COMPLIANCE_OFFICER, CUSTOMER_SERVICE_AGENT | no | |
| InProgress → AwaitingComplainant | WorkflowCaseAwaitComplainant POST …/await-complainant | CASE_UPDATE | COMPLIANCE_OFFICER, CUSTOMER_SERVICE_AGENT | no | |
| AwaitingComplainant → InProgress | WorkflowResumeCase POST …/resume | CASE_UPDATE | COMPLIANCE_OFFICER, CUSTOMER_SERVICE_AGENT | no | |
| InProgress → Decided | WorkflowDecideCase POST …/decide | CASE_DECIDE (appeals: CASE_DECIDE_APPEAL) | COMPLIANCE_OFFICER, CUSTOMER_SERVICE_AGENT (appeals: COMPLIANCE_OFFICER, privileged named grant) | no | |
| Decided → Closed | WorkflowCloseCase POST …/close | CASE_CLOSE | COMPLIANCE_OFFICER, CUSTOMER_SERVICE_AGENT | no | |
| Received → Closed | WorkflowCloseCase POST …/close (answered at once, ServiceCase.cs:665) | CASE_CLOSE | COMPLIANCE_OFFICER, CUSTOMER_SERVICE_AGENT | no | |
| Received/Acknowledged/InProgress/AwaitingComplainant/Reopened → Withdrawn | WorkflowWithdrawCase POST …/withdraw; portal withdraw (CasePortal.cs) | CASE_WITHDRAW | COMPLIANCE_OFFICER, CUSTOMER_SERVICE_AGENT | no | |
| Closed → Reopened | WorkflowReopenCase POST …/reopen | CASE_REOPEN | - (privileged, no template names it) | no | |

### Workflow · CaseRedress (`src/Modules/Workflow/Insys.Workflow.Domain/Cases/CaseRedress.cs:69`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| Requested | only under its parent: WorkflowGetCaseRedress GET /workflow/cases/{id}/redress/{redressId}; its approval in WorkflowListApprovals | |
| Approved | only under its parent | `NO-LIST` an approved redress waiting on its payment is not listed across the tenant |
| Rejected | only under its parent | |
| Paid | only under its parent (WorkflowCasesSummary gives totals only) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → Requested | WorkflowRecordCaseRedress POST /workflow/cases/{id}/redress | CASE_RECORD_REDRESS | - (privileged, no template names it) | yes: approval type CASE_REDRESS (requester ≠ checker) | |
| Requested → Approved | WorkflowApprove → CaseRedressPolicies (CaseRedressPolicies.cs:48-61) | WORKFLOW_APPROVE + authority CASE_REDRESS | FINANCE_MANAGER | yes: CASE_REDRESS | |
| Requested → Rejected | WorkflowReject / WorkflowWithdraw / expiry → CaseRedressPolicies.cs:63-75 | WORKFLOW_REJECT; requester; job | FINANCE_MANAGER (authority) | yes | |
| Approved → Paid | event payment.PaymentSucceeded (CaseRedressPolicies.cs:77-83), Payments pays it from CaseRedressApproved (Payments.cs:1054) | event | - | - | `STUCK` if the payment fails or is cancelled: CaseRedressPolicies handles only ApprovalGranted, ApprovalRejected, PaymentSucceeded (CaseRedressPolicies.cs:43-46) |

### Integration · DeadLetter (`src/Modules/Integration/Insys.Integration.Domain/DeadLetters/DeadLetter.cs:80`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| DeadLettered | ListDeadLetters GET /integration/dead-letters (status, source) | |
| Replayed | ListDeadLetters?status=Replayed | |
| Discarded | ListDeadLetters?status=Discarded | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → DeadLettered | OutboxRelay after max attempts (OutboxRelay.cs:161-169); webhook retries exhausted (Webhooks.cs:161-165); ParkingJob (ParkingJob.cs:208-215) | job | - | - | |
| DeadLettered → Replayed | ReplayMessage POST /integration/dead-letters/{id}/replay | REPLAY_MESSAGE | INTEGRATION_ADMINISTRATOR, OPERATIONS_ANALYST | no | `STUCK` a webhook dead letter is refused replay (DeadLetters/Commands.cs:86-90, OQ-066): discard is its only way out |
| DeadLettered → Discarded | DiscardMessage POST …/discard | DISCARD_MESSAGE | INTEGRATION_ADMINISTRATOR (privileged, named grant) | no | `FOLLOW-UP` discarding leaves the ledger row DEAD_LETTERED, still blocking later deliveries for the record (DeliveryLedger.cs:85); DiscardParkedDelivery must follow, nothing says so (Commands.cs DiscardMessageHandler) |

### Integration · WebhookDelivery (`src/Modules/Integration/Insys.Integration.Domain/Webhooks/WebhookDelivery.cs:66`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| Pending | ListWebhookDeliveries GET /integration/webhook-deliveries (status, subscriptionId) | |
| Delivered | ListWebhookDeliveries?status=Delivered | |
| Failed | ListWebhookDeliveries?status=Failed | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → Pending | event delivery to a subscription (WebhookDelivery.cs:97) | event | - | - | |
| Pending → Pending (retry now) | RetryWebhookDelivery POST /integration/webhook-deliveries/{id}/retry | RETRY_WEBHOOK_DELIVERY | INTEGRATION_ADMINISTRATOR, OPERATIONS_ANALYST | no | |
| Pending → Delivered | job WebhookJobsService (WebhookTransports.cs:148), 2xx | job | - | - | |
| Pending → Failed | job, retries exhausted (WebhookDelivery.cs:131-135) | job | - | - | |

### Integration · ParkedDelivery, LedgerStatus (BuildingBlocks `src/BuildingBlocks/Insys.BuildingBlocks.Application/Messaging/Delivery.cs:174`; table db/migrations/V0031__parked_delivery.sql:34)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| Parked | ListParkedDeliveries GET /integration/parked-deliveries (status, code, consumer, aggregateType, aggregateId); ListParkedDeliveriesByTenant (platform) | |
| Waiting | ListParkedDeliveries?status=Waiting | |
| Failing | ListParkedDeliveries?status=Failing | |
| Retrying | ListParkedDeliveries?status=Retrying | |
| DeadLettered | ListParkedDeliveries?status=DeadLettered | |
| Resolved | ListParkedDeliveries?status=Resolved | |
| Discarded | ListParkedDeliveries?status=Discarded | |
Note: ListParkedDeliveriesByTenant (PLATFORM_PARKED_DELIVERIES) is held by no template (`NO-ROLE` for the operator's cross-tenant view, fn_roles).

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → Parked | event bus: consumer throws PrerequisiteMissing (DeliveryLedger.cs:206) | event | - | - | |
| (new) → Waiting | event bus: an earlier delivery for the record blocks (DeliveryLedger.cs:249) | event | - | - | |
| (new)/Retrying → Failing | consumer failure (DeliveryLedger.cs:260); stale Retrying (ParkingJob.cs:141-148) | event / job | - | - | |
| Parked → Retrying | job ParkingJob periodic or after RetryParkedDelivery / RetryParkedDeliveriesForCode POST /integration/parked-deliveries/{id}/retry, /retry (ParkedDeliveryStore.cs:144-150, ParkingJob.cs:154-170); StaticDataVersionActivated marks retry (ParkingJob.cs:396) | RETRY_PARKED_DELIVERY / RETRY_PARKED_DELIVERIES_FOR_CODE / job | INTEGRATION_ADMINISTRATOR, OPERATIONS_ANALYST | no | |
| Failing → Retrying | job (ParkingJob.cs:163) | job | - | - | |
| Waiting → Retrying | job, once unblocked (DeliveryLedger.cs:314-324, ParkingJob.cs:185-190) | job | - | - | |
| Failing → DeadLettered | job / OutboxRelay at max failures (ParkingJob.cs:213, DeliveryLedger.cs:302) | job | - | - | |
| Parked/DeadLettered → Failing | record retry IRecordDeliveries.Retry (RecordDeliveries.cs:58-70), used by Identity TenantAdministrators | RETRY_ADMINISTRATOR_IDENTITY (Identity) | PLATFORM_ADMIN | no | |
| DeadLettered → Resolved | ReplayMessage of the dead letter, then success (DeliveryLedger.cs:278) | REPLAY_MESSAGE | INTEGRATION_ADMINISTRATOR, OPERATIONS_ANALYST | no | |
| any open → Resolved | delivery succeeds (DeliveryLedger.cs:276-280) | event / job | - | - | |
| Parked/Waiting/Failing/DeadLettered → Discarded | DiscardParkedDelivery POST /integration/parked-deliveries/{id}/discard (ParkedDeliveryStore.cs:154-160) | DISCARD_PARKED_DELIVERY | INTEGRATION_ADMINISTRATOR (privileged, named grant) | no | `FOLLOW-UP` no event is published on discard, so an APPROVAL_NOT_APPLIED approval stays NotApplied (see Workflow · Approval) |
| Parked → (escalated) | job ParkingJob.Escalate (ParkingJob.cs:240-350): ParkedDeliveryEscalated to the configured owner role | job | - | - | |

### BuildingBlocks · CallbackEndpoint (`src/BuildingBlocks/Insys.BuildingBlocks.Domain/ProviderCallbacks/CallbackEndpoint.cs:30`), used by payment.callback_endpoint and communication.callback_endpoint
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| Active | PaymentsListCallbackEndpoints GET /payments/callback-endpoints; CommunicationsListCallbackEndpoints GET /communications/callback-endpoints (no status filter; few rows) | |
| Retiring | as Active | |
| Revoked | as Active | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → Active | PaymentsIssueCallbackEndpoint / CommunicationsIssueCallbackEndpoint POST …/callback-endpoints | PAYMENTS_ISSUE_CALLBACK_ENDPOINT / COMMUNICATIONS_ISSUE_CALLBACK_ENDPOINT | INTEGRATION_ADMINISTRATOR (privileged, named grant) | no | |
| Active → Retiring | the next Issue for the same provider rotates it (ProviderCallbacks.cs:103-110) | as Issue | INTEGRATION_ADMINISTRATOR | no | |
| Active/Retiring → Revoked | PaymentsRevokeCallbackEndpoint / CommunicationsRevokeCallbackEndpoint POST …/{id}/revoke | PAYMENTS_REVOKE_CALLBACK_ENDPOINT / COMMUNICATIONS_REVOKE_CALLBACK_ENDPOINT | INTEGRATION_ADMINISTRATOR (privileged, named grant) | no | |
Note: after its cut-over a Retiring endpoint is refused (UsableAt) but its status stays Retiring; no job moves it (CallbackEndpoint.cs:68). Not a gap: it is final in effect.

### Assist · ProductDraftRun (`src/Modules/Assist/Insys.Assist.Domain/ProductDraft.cs:80`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| QUEUED | none: AssistGetProductDraft GET /assist/product-drafts/{id} only | |
| RUNNING | none (by id) | |
| DRAFTED | none (by id); the drafted product version shows in Product Factory | `NO-LIST` a drafted run's items wait for review (AssistDecideProductDraftItem), but no list shows runs to review; PRODUCT_MANAGER needs the run id |
| REFUSED | none (by id) | |
| FAILED | none (by id) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → QUEUED | AssistStartProductDraft POST /assist/product-drafts | ASSIST_PRODUCT_DRAFT_RUN | - | no | `NO-ROLE` not privileged, in no template (fn_roles) |
| QUEUED → RUNNING | job ProductDraftService (ProductDraftJob.cs:58; ProductDraft.cs:117-128) | job | - | - | |
| RUNNING → DRAFTED | job (ProductDrafts.cs:568) | job | - | - | `FOLLOW-UP` the draft's items are reviewed under ASSIST_PRODUCT_DRAFT_REVIEW (PRODUCT_MANAGER); nothing tells them a run finished |
| QUEUED/RUNNING → REFUSED/FAILED | job, incl. stalled after 30 min (ProductDrafts.cs:380-382) | job | - | - | |

### Assist · SetupRun, tenant setup (`src/Modules/Assist/Insys.Assist.Domain/SetupRun.cs:84`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| DRAFT | none: AssistGetTenantSetup GET /assist/tenant-setups/{id} only | `NO-LIST` a setup returned to draft (not approved, abandoned) is found only by its id |
| SUBMITTING | none (by id) | `NO-LIST` a submission that stopped part-way must be withdrawn by its submitter, found only by id |
| SUBMITTED | none (by id); its approval in WorkflowListApprovals?businessRefType=STATIC_DATA_SETUP | |
| APPLIED | none (by id) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → DRAFT | AssistStartTenantSetup POST /assist/tenant-setups | ASSIST_TENANT_SETUP_RUN | - | no | `NO-ROLE` not privileged, in no template (fn_roles) |
| DRAFT → SUBMITTING | AssistSubmitTenantSetup POST …/{id}/submit (SetupRun.cs:120-130) | ASSIST_TENANT_SETUP_RUN | - | yes: whoever ran it submits, another approves the set | `NO-ROLE` as above |
| SUBMITTING → SUBMITTED | all batches loaded (SetupRun.cs:138-148) | event (submission handler) | - | - | |
| SUBMITTING → DRAFT | AssistWithdrawTenantSetupSubmission POST …/submission/withdraw (SetupRun.cs:155-170) | ASSIST_TENANT_SETUP_RUN | - | no | `NO-ROLE` as above |
| SUBMITTED → APPLIED | event SetupChangeSetApplied (Submissions.cs:315-322) | event | - | yes: the set's STATIC_DATA_SETUP approval | |
| SUBMITTING/SUBMITTED → DRAFT | events SetupChangeSetNotApproved / SetupChangeSetAbandoned (Submissions.cs:304-334) | event | - | - | |
Note: domain-model.json has no Assist context.

### Assist · SetupAiRun, setup AI pass (`src/Modules/Assist/Insys.Assist.Domain/SetupAi.cs:56`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| QUEUED | only under its parent: AssistListSetupAiPasses GET /assist/tenant-setups/{id}/ai-passes | |
| RUNNING | only under its parent | |
| DRAFTED | only under its parent | |
| REFUSED | only under its parent | |
| FAILED | only under its parent | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → QUEUED | AssistQueueSetupAiPass POST /assist/tenant-setups/{id}/ai-passes | ASSIST_TENANT_SETUP_RUN | - | no | `NO-ROLE` in no template |
| QUEUED → RUNNING | job SetupAiService (ProductDraftJob.cs:106; SetupAi.cs:102-110) | job | - | - | |
| RUNNING → DRAFTED | job (SetupAiWorker.cs:200) | job | - | - | |
| QUEUED/RUNNING → REFUSED/FAILED | job, incl. stalled after 30 min (SetupAiWorker.cs:50, 197) | job | - | - | |

### Assist · StarterPack, tenant packs and platform overlays (`src/Modules/Assist/Insys.Assist.Domain/StarterPack.cs:104`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| DRAFT | tenant: AssistListTenantPacks GET /assist/packs (packCode only), AssistTenantPacksToApprove GET /assist/packs/awaiting-approval; platform: AssistListPlatformPacks GET /assist/overlays, AssistPlatformPacksToApprove | |
| REVIEWED | AssistListPlatformPacks; AssistPlatformPacksToApprove (ALL_TENANTS) | |
| APPROVED | AssistListTenantPacks / AssistListPlatformPacks (no status filter) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → DRAFT, tenant pack | AssistLoadTenantPack POST /assist/packs | ASSIST_TENANT_SETUP_RUN | - | no | `NO-ROLE` in no template |
| (new) → DRAFT, overlay | AssistLoadPlatformPack POST /assist/overlays | ASSIST_OVERLAY_MANAGE | - (privileged, no template names it) | no | |
| DRAFT → REVIEWED | AssistReviewPlatformPack POST /assist/overlays/{id}/review (StarterPack.cs:176-191) | ASSIST_OVERLAY_MANAGE | - (privileged) | no | |
| REVIEWED → APPROVED, ALL_TENANTS | AssistApprovePlatformPack POST /assist/overlays/{id}/approve (StarterPack.cs:198-226) | ASSIST_OVERLAY_APPROVE | - (privileged, no template names it) | yes: own rule, not the preparer or reviewer (StarterPack.cs:213) | |
| DRAFT → APPROVED, TENANT_ONLY | AssistApproveTenantPack POST /assist/packs/{id}/approve | ASSIST_OVERLAY_APPROVE_TENANT | - (privileged, no template names it) | yes: own rule (StarterPack.cs:213) | `FOLLOW-UP` a tenant pack's regulated values are never filled until approved (SetupRun.cs:43), and no template holds the approver function: each tenant must grant it individually |

### Assist · AssistRun (`src/Modules/Assist/Insys.Assist.Domain/AssistRun.cs:60`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| VERIFIED | none (metered only: AssistGetUsage) | |
| REJECTED | none | |
| UNAVAILABLE | none | |
| RESIDENCY_MISMATCH | none | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → final status | written once when the model call ends (AssistRun.cs:59, 98) | job / the calling feature | - | - | |

### Not counted (no persisted multi-value status)
| Item | Why | Gap |
| --- | --- | --- |
| DeliveryStatus (Delivery.cs:153) | per-call outcome of one consumer, not stored; its persisted form is LedgerStatus above | |
| outbox_event / inbox_message | no status column: published_at and publish_attempts only (V0001__core_schema.sql:155-173, 90-104) | |
| Disclosures (Integration Disclosures.cs, IntegrationAcknowledgeDisclosure) | no status; text is static data (disclosure-and-notice-texts), acknowledgements are rows | |

## Life and General

Paths are relative to `src/Modules/` unless they start with `docs/` or name the scratchpad TSVs. Roles come from fn_roles.tsv; approval authority and inbox access come from templates.tsv.

### Life · LifeProposal (`Life/Insys.Life.Domain/Proposals/LifeProposal.cs:33`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| Draft | LifeListProposals, GET /api/v1/life/proposals (status filter, all statuses; `Life/Insys.Life.Infrastructure/Persistence/LifeLists.cs:45`) | |
| Quoted | LifeListProposals (status filter) | |
| Submitted | LifeListProposals (status filter) | |
| InUnderwriting | LifeListProposals (status filter) | |
| Accepted | LifeListProposals (status filter) | |
| Declined | LifeListProposals (status filter) | |
| Postponed | LifeListProposals (status filter) | |
| Withdrawn | LifeListProposals (status filter) | |
| Converted | LifeListProposals (status filter) | |

Note: Draft has no withdraw (`LifeProposal.cs:218-219`) and Life does not handle PremiumNotCalculated (`Life/Insys.Life.Application/LifeEventPolicies.cs:49-63`). A proposal Rating cannot price stays in Draft unless it is re-rated through RatingCalculatePremium. Unverified as a dead end.

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| Draft → Quoted | PremiumCalculated handler (`LifeEventPolicies.cs:65`), after RatingCalculatePremium, POST /api/v1/rating/calculations | event (request: RATING_CALCULATE_PREMIUM) | GENERAL_UNDERWRITER, LIFE_UNDERWRITER, PRODUCT_MANAGER | no | |
| Quoted → Submitted | LifeSubmitProposal, POST /api/v1/life/proposals/{id}/submit | LIFE_SUBMIT_PROPOSAL | LIFE_UNDERWRITER | no | |
| Submitted → InUnderwriting | LifeUnderwritingCaseOpened handler (`LifeEventPolicies.cs:131`); the case opens on LifeProposalSubmitted (`:116`) | event | - | no | |
| InUnderwriting → Accepted | LifeUnderwritingDecided handler (`LifeEventPolicies.cs:141`) | event | - | no (the case decision may be maker-checked) | FOLLOW-UP: the policy is issued by hand (LifeIssuePolicy, LIFE_ISSUE_POLICY, LIFE_UNDERWRITER). The underwriter learns of it only from LifeListProposals?status=ACCEPTED; no task or event consumer starts it (`LifeEventPolicies.cs:141-147`) |
| InUnderwriting → Declined | LifeUnderwritingDecided handler (`LifeEventPolicies.cs:141`) | event | - | no | |
| InUnderwriting → Postponed | LifeUnderwritingDecided handler (`LifeEventPolicies.cs:141`) | event | - | no | |
| Quoted → Withdrawn | LifeWithdrawProposal, POST /api/v1/life/proposals/{id}/withdraw | LIFE_WITHDRAW_PROPOSAL | LIFE_UNDERWRITER | no | |
| Submitted → Withdrawn | LifeWithdrawProposal, POST /api/v1/life/proposals/{id}/withdraw | LIFE_WITHDRAW_PROPOSAL | LIFE_UNDERWRITER | no | |
| Accepted → Converted | LifePolicyIssued handler (`LifeEventPolicies.cs:150`), after LifeIssuePolicy, POST /api/v1/life/policies | event (issue: LIFE_ISSUE_POLICY) | LIFE_UNDERWRITER | no | |

### Life · LifeUnderwritingCase (`Life/Insys.Life.Domain/Underwriting/LifeUnderwritingCase.cs:33`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| Open | LifeListUnderwritingCases, GET /api/v1/life/underwriting-cases (status filter, all statuses; `LifeLists.cs:110`) | |
| AwaitingEvidence | LifeListUnderwritingCases (status filter) | |
| InAssessment | LifeListUnderwritingCases (status and decisionPending filters; `LifeLists.cs:113-116`) | |
| Referred | LifeListUnderwritingCases (status filter) | |
| Decided | LifeListUnderwritingCases (status filter) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| Open → AwaitingEvidence | LifeRequestEvidence, POST /api/v1/life/underwriting-cases/{id}/evidence | LIFE_REQUEST_EVIDENCE | LIFE_UNDERWRITER | no | |
| AwaitingEvidence → InAssessment | automatic inside LifeRecordEvidence or LifeWaiveEvidence once no mandatory requirement is outstanding (`LifeUnderwritingCase.cs:499`) | LIFE_RECORD_EVIDENCE / LIFE_WAIVE_EVIDENCE | LIFE_UNDERWRITER | no | |
| Open → InAssessment | LifeCaseStartAssessment, POST /api/v1/life/underwriting-cases/{id}/start-assessment | LIFE_CASE_START_ASSESSMENT | LIFE_UNDERWRITER | no | |
| InAssessment → Referred | LifeReferCase, POST /api/v1/life/underwriting-cases/{id}/refer | LIFE_REFER_CASE | LIFE_UNDERWRITER | no | |
| Referred → InAssessment | LifeCaseAnswerReferral, POST /api/v1/life/underwriting-cases/{id}/answer-referral | LIFE_CASE_ANSWER_REFERRAL | LIFE_UNDERWRITER | no | |
| InAssessment → Decided (one underwriter) | LifeDecideCase, POST /api/v1/life/underwriting-cases/{id}/decision, when the decision matches the rule outcome and is within authority (`Life/Insys.Life.Application/Underwriting.cs:236-246`) | LIFE_DECIDE_CASE | LIFE_UNDERWRITER | no: own rule (authority matrix plus rule outcome) | |
| InAssessment → Decided (second person) | LifeDecideCase raises UnderwritingDecisionRequested (`LifeUnderwritingCase.cs:355-368`); ApprovalGranted handler (`LifeEventPolicies.cs:166`, `LifeUnderwritingCase.cs:440-480`) | LIFE_DECIDE_CASE, then WORKFLOW_APPROVE | maker: LIFE_UNDERWRITER. Checker: no template holds both WORKFLOW_APPROVE and LIFE_UNDERWRITING authority | yes: approval type LIFE_UNDERWRITING (requester ≠ checker, `LifeUnderwritingCase.cs:450-453`) | NO-ROLE: LIFE_UNDERWRITER is the only template with LIFE_UNDERWRITING authority but holds neither WORKFLOW_APPROVALS nor WORKFLOW_APPROVE (templates.tsv); the 7 templates holding WORKFLOW_APPROVE lack the authority. FOLLOW-UP: the decision waits until the approval expires, which clears it |

### Life · UnderwritingRequirement (`Life/Insys.Life.Domain/Underwriting/LifeUnderwritingCase.cs:83`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| REQUESTED | only under its parent: LifeGetUnderwritingCase. Its cases can be found through LifeListUnderwritingCases?status=AWAITING_EVIDENCE | |
| RECEIVED | only under its parent: LifeGetUnderwritingCase | |
| WAIVED | only under its parent: LifeGetUnderwritingCase | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| REQUESTED → RECEIVED | LifeRecordEvidence, POST /api/v1/life/underwriting-cases/{id}/evidence/{requirementId}/received | LIFE_RECORD_EVIDENCE | LIFE_UNDERWRITER | no | |
| REQUESTED → WAIVED | LifeWaiveEvidence, POST /api/v1/life/underwriting-cases/{id}/evidence/{requirementId}/waive | LIFE_WAIVE_EVIDENCE | LIFE_UNDERWRITER | no (audited only, `Underwriting.cs:148`) | |

### Life · LifePolicy (`Life/Insys.Life.Domain/Policies/LifePolicy.cs:36`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| Issued | LifeListPolicies, GET /api/v1/life/policies (status filter, all statuses; `LifeLists.cs:57`) | |
| InForce | LifeListPolicies (status filter) | |
| Lapsed | LifeListPolicies (status filter) | |
| Terminated | LifeListPolicies (status filter) | |
| PaidUp | LifeListPolicies (status filter) | |
| Surrendered | LifeListPolicies (status filter) | |
| Claimed | LifeListPolicies (status filter) | |
| Matured | LifeListPolicies (status filter) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| Issued → InForce | LifeJobs StartCover (`Life/Insys.Life.Application/LifeEventPolicies.cs:338-341`, `Life/Insys.Life.Infrastructure/LifeInfrastructure.cs:156`); also at issue when inception ≤ today (`LifePolicy.cs:445`) | job (LIFE_DAILY_JOBS) | - | no | |
| InForce → Lapsed | LifeJobs Lapse (`LifeEventPolicies.cs:353-359`) | job | - | no | |
| Lapsed → InForce | LifeReinstatePolicy, POST /api/v1/life/policies/{id}/reinstate | LIFE_REINSTATE_POLICY | LIFE_UNDERWRITER | no | |
| Lapsed → Terminated | LifeJobs Terminate (`LifeEventPolicies.cs:348-351`) | job | - | no | |
| InForce → PaidUp | LifePolicyMakePaidUp, POST /api/v1/life/policies/{id}/paid-up, which always refuses LIFE_VALUE_NOT_CONFIGURED (`Life/Insys.Life.Application/Policies.cs:270-279`, `Life/Insys.Life.Api/LifeModule.cs:250-257`) | LIFE_POLICY_MAKE_PAID_UP | LIFE_UNDERWRITER | no | NO-OP: the operation exists but never fires MakePaidUp (OQ-052) |
| InForce → Surrendered | LifePolicySurrender, POST /api/v1/life/policies/{id}/surrender, which always refuses (`Policies.cs:270-279`) | LIFE_POLICY_SURRENDER | LIFE_UNDERWRITER | no | NO-OP: SurrenderPolicy is never fired (OQ-052) |
| InForce → Claimed | LifeClaimSettled handler → CloseOnClaim (`LifeEventPolicies.cs:225-231`, `LifePolicy.cs:698`) | event | - | no | |
| InForce → Matured | LifePolicyMature, POST /api/v1/life/policies/{id}/mature, which always refuses (`Policies.cs:270-279`) | LIFE_POLICY_MATURE | LIFE_UNDERWRITER | no | NO-OP: MaturePolicy is never fired (OQ-052) |

### Life · LifeBenefit (`Life/Insys.Life.Domain/Policies/LifePolicy.cs:134`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| ACTIVE | only under its parent: LifeGetPolicy | |
| CLAIMED | only under its parent: LifeGetPolicy | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| ACTIVE → CLAIMED | LifeClaimSettled handler → CloseOnClaim → MarkClaimed (`LifeEventPolicies.cs:225`, `LifePolicy.cs:181`, `:687`) | event | - | no | |

### Life · LifePolicyLoan (`Life/Insys.Life.Domain/Policies/LifePolicyLoan.cs:32`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| Active | LifeListLoans, GET /api/v1/life/loans (status filter; `LifeLists.cs:97`) | |
| Repaid | LifeListLoans (status filter) | |
| Offset | LifeListLoans (status filter) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| Active → Repaid | LifeRepayLoan, POST /api/v1/life/loans/{id}/repayments, when the balance reaches zero (`LifePolicyLoan.cs:173`) | LIFE_REPAY_LOAN | LIFE_UNDERWRITER | no | |
| Active → Offset | LifeClaimApproved handler → OffsetAgainstBenefit (`LifeEventPolicies.cs:211-222`, `LifePolicyLoan.cs:190`) | event | - | no | NO-OP: the model's "claim or surrender settles it"; the surrender half never happens because surrender always refuses (`Policies.cs:270-279`) |

### Life · LifeClaim (`Life/Insys.Life.Domain/Claims/LifeClaim.cs:33`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| Notified | LifeListClaims, GET /api/v1/life/claims (status filter, all statuses; `LifeLists.cs:72`) | |
| Registered | LifeListClaims (status filter) | |
| InAssessment | LifeListClaims (status filter); awaiting approval: WorkflowListApprovals (businessRefType=LIFE_CLAIM) | |
| Approved | LifeListClaims (status filter) | |
| Declined | LifeListClaims (status filter) | |
| Settled | LifeListClaims (status filter) | |
| Closed | LifeListClaims (status filter) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| Notified → Registered | LifeRegisterClaim, POST /api/v1/life/claims/{id}/register | LIFE_REGISTER_CLAIM | CLAIMS_HANDLER, CLAIMS_MANAGER | no | |
| Registered → InAssessment | LifeAssessClaim, POST /api/v1/life/claims/{id}/assessment (`LifeClaim.cs:323-325`) | LIFE_ASSESS_CLAIM | CLAIMS_HANDLER, CLAIMS_MANAGER | no | |
| InAssessment → Approved | LifeApproveClaim, POST /api/v1/life/claims/{id}/approval (maker, `LifeClaim.cs:349`), then the ApprovalGranted handler (`LifeEventPolicies.cs:166`) | LIFE_APPROVE_CLAIM, then WORKFLOW_APPROVE | maker: CLAIMS_HANDLER, CLAIMS_MANAGER. Checker: CLAIMS_MANAGER | yes: approval type LIFE_CLAIM (requester ≠ checker, `LifeClaim.cs:409`) | |
| InAssessment → Declined | LifeDeclineClaim, POST /api/v1/life/claims/{id}/decline | LIFE_DECLINE_CLAIM | CLAIMS_HANDLER, CLAIMS_MANAGER | no | |
| Approved → Settled | ClaimPaymentCompleted handler (`LifeEventPolicies.cs:201-208`) | event | - | no | FOLLOW-UP: ClaimsFramework creates the payment instructions automatically on LifeClaimApproved (`ClaimsFramework/Insys.ClaimsFramework.Application/Claims.cs:957`). Who releases them is outside this module and unverified here |
| Settled → Closed | LifeCloseClaim, POST /api/v1/life/claims/{id}/close | LIFE_CLOSE_CLAIM | CLAIMS_HANDLER, CLAIMS_MANAGER | no | |
| Declined → Closed | LifeCloseClaim, POST /api/v1/life/claims/{id}/close | LIFE_CLOSE_CLAIM | CLAIMS_HANDLER, CLAIMS_MANAGER | no | |
| Declined → InAssessment | CaseDecided (dispute or appeal upheld) handler (`Life/Insys.Life.Application/CaseReassessment.cs:42`, `LifeClaim.cs:512-520`) | event | - | no | SPEC-DIFF: ReopenClaim is not in domain-model.json (`LifeClaim.cs:166`) |
| Closed → InAssessment | CaseDecided handler (`CaseReassessment.cs:42`), only when nothing was paid | event | - | no | SPEC-DIFF: not in domain-model.json (`LifeClaim.cs:167`) |

### Life · MaturityCase (domain-model.json `life` MaturityCase; no code)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| Identified | none: no entity, table mapping or endpoint (grep `Maturity` in Life finds only static data) | SPEC-DIFF: aggregate in the model, absent from the code |
| Notified | none | |
| Option chosen | none | |
| Approved | none | |
| Paid | none | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| Identified → Notified | none (NotifyPolicyholder) | - | - | - | NO-OP |
| Notified → Option chosen | none (ChooseOption) | - | - | - | NO-OP |
| Option chosen → Approved | none (ApproveMaturity) | - | - | - | NO-OP |
| Approved → Paid | none (Payment completed) | - | - | - | NO-OP |

### Life · GroupScheme (`Life/Insys.Life.Domain/Groups/GroupScheme.cs:32`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| SetUp | LifeListGroupSchemes, GET /api/v1/life/group-schemes (status filter; `LifeLists.cs:84`) | |
| Active | LifeListGroupSchemes (status filter) | |
| Terminated | LifeListGroupSchemes (status filter) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| SetUp → Active | LifeJobs ActivateScheme (`LifeEventPolicies.cs:328-331`); at set-up when start ≤ today (`GroupScheme.cs:186`) | job (LIFE_DAILY_JOBS) | - | no | |
| Active → Active | LifeRenewScheme, POST /api/v1/life/group-schemes/{id}/renew | LIFE_RENEW_SCHEME | LIFE_UNDERWRITER | no | |
| Active → Terminated | LifeTerminateScheme, POST /api/v1/life/group-schemes/{id}/terminate | LIFE_TERMINATE_SCHEME | LIFE_UNDERWRITER | no | |

### Life · GroupMember evidence status (`Life/Insys.Life.Domain/Groups/GroupScheme.cs:46` constants, `:62` entity)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| NOT_REQUIRED | only under its parent: LifeGetGroupScheme | |
| REQUIRED | only under its parent: LifeGetGroupScheme. GroupMemberEnrolled (EvidenceRequired) has no consumer (grep) | NO-LIST: members waiting for an underwriter's evidence decision are found only by opening each scheme (`GroupScheme.cs:111-114`) |
| ACCEPTED | only under its parent: LifeGetGroupScheme | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| NOT_REQUIRED → REQUIRED | LifeEnrolMember, POST /api/v1/life/group-schemes/{id}/members; LifeChangeMember, PUT /api/v1/life/group-schemes/{id}/members/{memberId} (`GroupScheme.cs:99-115`) | LIFE_ENROL_MEMBER / LIFE_CHANGE_MEMBER | LIFE_UNDERWRITER | no | |
| REQUIRED → NOT_REQUIRED | LifeChangeMember, PUT /api/v1/life/group-schemes/{id}/members/{memberId} (cover ≤ free cover limit) | LIFE_CHANGE_MEMBER | LIFE_UNDERWRITER | no | |
| ACCEPTED → NOT_REQUIRED | LifeChangeMember, PUT /api/v1/life/group-schemes/{id}/members/{memberId} | LIFE_CHANGE_MEMBER | LIFE_UNDERWRITER | no | |
| REQUIRED → ACCEPTED | LifeAcceptMemberEvidence, POST /api/v1/life/group-schemes/{id}/members/{memberId}/evidence-accepted | LIFE_ACCEPT_MEMBER_EVIDENCE | LIFE_UNDERWRITER | no | |
| ACCEPTED → REQUIRED | LifeChangeMember, PUT /api/v1/life/group-schemes/{id}/members/{memberId} (cover rises above the limit) | LIFE_CHANGE_MEMBER | LIFE_UNDERWRITER | no | |

### General · GeneralRiskObject (`General/Insys.General.Domain/Underwriting/RiskObjects.cs:33`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| Active | GeneralListRiskObjects, GET /api/v1/general/risk-objects (all statuses, no status filter; `General/Insys.General.Infrastructure/Persistence/GeneralLists.cs:117-146`) | |
| Inactive | GeneralListRiskObjects (all statuses, no status filter) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| Active → Inactive | GeneralDeactivateRiskObject, POST /api/v1/general/risk-objects/{id}/deactivate (`RiskObjects.cs:270-277`) | GENERAL_DEACTIVATE_RISK_OBJECT | GENERAL_UNDERWRITER | no | |

### General · GeneralQuote (`General/Insys.General.Domain/Underwriting/Quotes.cs:32`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| Draft | GeneralListQuotes, GET /api/v1/general/quotes (all statuses, no status filter; `GeneralLists.cs:40-61`) | NO-FILTER: quotes Rating could not price (PremiumNotCalculated, `General/Insys.General.Application/GeneralPolicies.cs:118-132`) are found only by paging |
| Rated | GeneralListQuotes (no status filter) | NO-FILTER: quotes waiting to be issued |
| Referred | GeneralListQuotes (no status filter); task in WorkflowListOpenTasks (WORKFLOW_TASKS: AUDITOR, OPERATIONS_ANALYST only) | NO-FILTER: the referral work list. The task it opens (`Workflow/Insys.Workflow.Application/Processes.cs:784-805`) is routed to a role that cannot list tasks |
| Issued | GeneralListQuotes (no status filter) | NO-FILTER: quotes waiting for the customer's answer |
| Accepted | GeneralListQuotes (no status filter) | NO-FILTER: accepted quotes with no proposal yet (follow-up GeneralSubmitProposal) |
| Rejected | GeneralListQuotes (no status filter) | |
| Expired | GeneralListQuotes (no status filter) | |
| Declined | GeneralListQuotes (no status filter) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| Draft → Rated | PremiumCalculated handler (`GeneralPolicies.cs:74-90`, `Quotes.cs:415`), after GeneralCreateQuote or RatingCalculatePremium | event | - | no | |
| Rated → Referred (person) | GeneralReferQuote, POST /api/v1/general/quotes/{id}/refer (`Quotes.cs:485`) | GENERAL_REFER_QUOTE | GENERAL_UNDERWRITER | no | |
| Rated → Referred (rules) | GeneralQuoteIssue over a zone or peril limit (`General/Insys.General.Application/Underwriting.cs:449-462`); a quote priced on creation, once rated (`GeneralPolicies.cs:91-96`) | GENERAL_QUOTE_ISSUE / event | GENERAL_UNDERWRITER | no | |
| Referred → Rated | GeneralDecideReferral, POST /api/v1/general/quotes/{id}/referral-decision (approved) | GENERAL_DECIDE_REFERRAL | GENERAL_UNDERWRITER | own rule: underwriting authority covers the sum insured; no requester ≠ decider check (`Underwriting.cs:514-555`) | FOLLOW-UP: then GeneralQuoteIssue by hand; the underwriter learns of it only by opening the quote |
| Referred → Declined | GeneralDecideReferral, POST /api/v1/general/quotes/{id}/referral-decision (declined) | GENERAL_DECIDE_REFERRAL | GENERAL_UNDERWRITER | own rule (as above) | |
| Rated → Issued | GeneralQuoteIssue, POST /api/v1/general/quotes/{id}/issue | GENERAL_QUOTE_ISSUE | GENERAL_UNDERWRITER | no | |
| Issued → Accepted | GeneralQuoteAccept, POST /api/v1/general/quotes/{id}/accept | GENERAL_QUOTE_ACCEPT | GENERAL_UNDERWRITER | no | |
| Issued → Rejected | GeneralQuoteReject, POST /api/v1/general/quotes/{id}/reject | GENERAL_QUOTE_REJECT | GENERAL_UNDERWRITER | no | |
| Issued → Expired | GeneralJobs ExpireQuote (`GeneralPolicies.cs:453-459`, `General/Insys.General.Infrastructure/GeneralInfrastructure.cs:197`) | job (GENERAL_DAILY_JOBS) | - | no | |

### General · UnderwritingReferral (`General/Insys.General.Domain/Underwriting/Quotes.cs:179`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| OPEN | only under its parent: GeneralGetQuote; WorkflowListOpenTasks (WORKFLOW_TASKS: AUDITOR, OPERATIONS_ANALYST) | |
| APPROVED | only under its parent: GeneralGetQuote | |
| DECLINED | only under its parent: GeneralGetQuote | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| OPEN → APPROVED | GeneralDecideReferral, POST /api/v1/general/quotes/{id}/referral-decision (`Quotes.cs:214`) | GENERAL_DECIDE_REFERRAL | GENERAL_UNDERWRITER | own rule (authority) | |
| OPEN → DECLINED | GeneralDecideReferral, POST /api/v1/general/quotes/{id}/referral-decision | GENERAL_DECIDE_REFERRAL | GENERAL_UNDERWRITER | own rule (authority) | |

### General · GeneralProposal (`General/Insys.General.Domain/Underwriting/Quotes.cs:599`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| Submitted | GeneralListProposals, GET /api/v1/general/proposals (status filter; `GeneralLists.cs:158-161`) | |
| Accepted | GeneralListProposals (status filter) | |
| Converted | GeneralListProposals (status filter) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| Submitted → Accepted | GeneralAcceptProposal, POST /api/v1/general/proposals/{id}/accept | GENERAL_ACCEPT_PROPOSAL | GENERAL_UNDERWRITER | no | FOLLOW-UP: then GeneralIssuePolicy (GENERAL_ISSUE_POLICY, GENERAL_UNDERWRITER) by hand, found through GeneralListProposals?status=ACCEPTED; no task |
| Accepted → Converted | GeneralPolicyIssued handler (`GeneralPolicies.cs:213-223`) | event | - | no | |

### General · GeneralPolicy (`General/Insys.General.Domain/Policies/GeneralPolicy.cs:33`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| Issued | GeneralListPolicies, GET /api/v1/general/policies (all statuses, no status filter; `GeneralLists.cs:64-84`) | NO-FILTER: policies waiting for their first payment (CoverStartsOn FIRST_PAYMENT) are found only by paging |
| InForce | GeneralListPolicies (no status filter) | |
| Cancelled | GeneralListPolicies (no status filter) | NO-FILTER: candidates for reinstatement |
| Expired | GeneralListPolicies (no status filter) | |
| Renewed | GeneralListPolicies (no status filter) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| Issued → InForce | GeneralJobs StartPeriod (`GeneralPolicies.cs:519-523`); InvoiceSettled handler for FIRST_PAYMENT (`GeneralPolicies.cs:63-69`) | job / event | - | no | |
| InForce → Cancelled | GeneralCancelPolicy, POST /api/v1/general/policies/{id}/cancel | GENERAL_CANCEL_POLICY | GENERAL_UNDERWRITER | no | |
| InForce → Cancelled (non-payment) | GeneralJobs CancelForNonPayment (`GeneralPolicies.cs:468-482`) | job | - | no | |
| Cancelled → InForce | GeneralReinstatePolicy, POST /api/v1/general/policies/{id}/reinstate (`GeneralPolicy.cs:739`) | GENERAL_REINSTATE_POLICY | GENERAL_UNDERWRITER | no | |
| InForce → Expired | GeneralJobs ExpirePolicy (`GeneralPolicies.cs:525-536`) | job | - | no | |
| InForce → Renewed | GeneralPolicyIssued handler → MarkRenewed (`GeneralPolicies.cs:213-217`), after GeneralRenewPolicy (POST /api/v1/general/policies/{id}/renew) or the RenewPolicy job | event (issue: GENERAL_RENEW_POLICY or job) | GENERAL_UNDERWRITER | no | |
| Issued → Cancelled | GeneralCancelPolicy, POST /api/v1/general/policies/{id}/cancel (from inception, `GeneralPolicy.cs:630`) | GENERAL_CANCEL_POLICY | GENERAL_UNDERWRITER | no | |
| Cancelled → Issued | GeneralReinstatePolicy, POST /api/v1/general/policies/{id}/reinstate (before start, `GeneralPolicy.cs:739`) | GENERAL_REINSTATE_POLICY | GENERAL_UNDERWRITER | no | |

### General · GeneralEndorsement (`General/Insys.General.Domain/Policies/GeneralEndorsement.cs:33`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| Draft | only under its parent: GeneralListPolicyEndorsements, GET /api/v1/general/policies/{id}/endorsements (`GeneralLists.cs:171-180`) | |
| Rated | only under its parent: GeneralListPolicyEndorsements | NO-LIST: rated endorsements waiting to be submitted, including those drafted automatically from an accepted retention offer "for the case's owner to submit" (`General/Insys.General.Application/Retention.cs:241-246`, `:265`) |
| PendingApproval | only under its parent; approvals: WorkflowListApprovals (businessRefType=GENERAL_ENDORSEMENT) | |
| Approved | only under its parent | |
| Rejected | only under its parent | |
| Applied | only under its parent | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| Draft → Rated | PremiumCalculated handler (`GeneralPolicies.cs:98-104`); at draft for unrated types (`GeneralEndorsement.cs:175`, `:260`), after GeneralDraftEndorsement, POST /api/v1/general/policies/{id}/endorsements | event / GENERAL_DRAFT_ENDORSEMENT | GENERAL_UNDERWRITER | no | |
| Rated → PendingApproval | GeneralSubmitEndorsement, POST /api/v1/general/endorsements/{id}/submit | GENERAL_SUBMIT_ENDORSEMENT | GENERAL_UNDERWRITER | no | |
| PendingApproval → Approved | ApprovalGranted handler (`GeneralPolicies.cs:146`, `:247-259`) after WorkflowApprove, POST /api/v1/workflow/approvals/{id}/approve | WORKFLOW_APPROVE | No template holds both WORKFLOW_APPROVE and ENDORSEMENT authority | yes: approval type ENDORSEMENT (requester ≠ checker, `GeneralEndorsement.cs:373-376`) | NO-ROLE: GENERAL_UNDERWRITER has ENDORSEMENT authority but neither WORKFLOW_APPROVALS nor WORKFLOW_APPROVE (templates.tsv). FOLLOW-UP: no template can see or decide the approval |
| PendingApproval → Rejected | ApprovalRejected handler (reject, expiry or withdrawal; `GeneralPolicies.cs:154`) | WORKFLOW_REJECT / expiry | (as above) | yes: ENDORSEMENT | |
| Approved → Applied | GeneralEndorsementApproved → ApplyEndorsement, then GeneralPolicyEndorsed handler (`GeneralPolicies.cs:188-210`) | event | - | no | |

### General · GeneralClaim (`General/Insys.General.Domain/Claims/GeneralClaim.cs:34`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| FirstNotification | GeneralListClaims, GET /api/v1/general/claims (all statuses, no status filter; `GeneralLists.cs:87-114`) | NO-FILTER: losses waiting to be registered |
| Registered | GeneralListClaims (no status filter) | NO-FILTER: claims waiting for an assessor |
| UnderAssessment | GeneralListClaims (no status filter); settlement awaiting approval: WorkflowListApprovals (businessRefType=GENERAL_CLAIM) | NO-FILTER |
| Approved | GeneralListClaims (no status filter) | |
| Declined | GeneralListClaims (no status filter) | NO-FILTER: waiting to be closed |
| Settled | GeneralListClaims (no status filter) | NO-FILTER: waiting to be closed |
| Closed | GeneralListClaims (no status filter) | |
| Reopened | GeneralListClaims (no status filter) | NO-FILTER: reopened claims waiting for an assessor |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| FirstNotification → Registered | GeneralRegisterClaim, POST /api/v1/general/claims/{id}/register | GENERAL_REGISTER_CLAIM | CLAIMS_HANDLER, CLAIMS_MANAGER | no | |
| Registered → UnderAssessment | GeneralAppointAssessor, POST /api/v1/general/claims/{id}/assessor (`GeneralClaim.cs:386-388`) | GENERAL_APPOINT_ASSESSOR | CLAIMS_HANDLER, CLAIMS_MANAGER | no | |
| UnderAssessment → Approved | GeneralRequestSettlement, POST /api/v1/general/claims/{id}/settlement (maker), then the ApprovalGranted handler (`GeneralPolicies.cs:272-288`) | GENERAL_REQUEST_SETTLEMENT, then WORKFLOW_APPROVE | maker: CLAIMS_HANDLER, CLAIMS_MANAGER. Checker: CLAIMS_MANAGER | yes: approval type CLAIM_SETTLEMENT (requester ≠ checker, `GeneralClaim.cs:473-476`) | |
| UnderAssessment → Declined | GeneralDeclineClaim, POST /api/v1/general/claims/{id}/decline | GENERAL_DECLINE_CLAIM | CLAIMS_HANDLER, CLAIMS_MANAGER | no | |
| FirstNotification / Registered / UnderAssessment / Approved → Declined | GeneralPolicyLapsedUnpaid handler DeclineClaimsOnLapse (`General/Insys.General.Application/Grace.cs:81-102`) | event | - | no | SPEC-DIFF: DeclineForNonPayment is not in domain-model.json (`GeneralClaim.cs:201-204`) |
| Approved → Settled | ClaimPaymentCompleted handler (`GeneralPolicies.cs:157-164`) | event | - | no | FOLLOW-UP: ClaimsFramework creates the payment instruction on GeneralClaimApproved (`ClaimsFramework/Insys.ClaimsFramework.Application/Claims.cs:745`). Who releases it is outside this module and unverified here |
| Settled → Closed | GeneralClaimClose, POST /api/v1/general/claims/{id}/close | GENERAL_CLAIM_CLOSE | CLAIMS_HANDLER, CLAIMS_MANAGER | no | |
| Declined → Closed | GeneralClaimClose, POST /api/v1/general/claims/{id}/close | GENERAL_CLAIM_CLOSE | CLAIMS_HANDLER, CLAIMS_MANAGER | no | |
| Closed → Reopened | GeneralClaimReopen, POST /api/v1/general/claims/{id}/reopen; CaseDecided handler (`General/Insys.General.Application/CaseReassessment.cs:42`) | GENERAL_CLAIM_REOPEN / event | CLAIMS_HANDLER, CLAIMS_MANAGER | no | |
| Declined → Reopened | GeneralClaimReopen, POST /api/v1/general/claims/{id}/reopen; CaseDecided handler | GENERAL_CLAIM_REOPEN / event | CLAIMS_HANDLER, CLAIMS_MANAGER | no | SPEC-DIFF: the model has only Closed → Reopened (`GeneralClaim.cs:210`) |
| Reopened → UnderAssessment | GeneralAppointAssessor, POST /api/v1/general/claims/{id}/assessor (AssessmentResumes, `GeneralClaim.cs:388`) | GENERAL_APPOINT_ASSESSOR | CLAIMS_HANDLER, CLAIMS_MANAGER | no | |

### General · LossAssessment (`General/Insys.General.Domain/Claims/Assessments.cs:32`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| Assigned | GeneralListAllAssessments, GET /api/v1/general/assessments (status filter; `GeneralLists.cs:191-194`) | |
| InProgress | GeneralListAllAssessments (status filter) | |
| Reported | GeneralListAllAssessments (status filter) | |
| Accepted | GeneralListAllAssessments (status filter) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| Assigned → InProgress | GeneralAssessmentStart, POST /api/v1/general/assessments/{id}/start | GENERAL_ASSESSMENT_START | CLAIMS_HANDLER, CLAIMS_MANAGER | no | |
| InProgress → Reported | GeneralSubmitReport, POST /api/v1/general/assessments/{id}/report | GENERAL_SUBMIT_REPORT | CLAIMS_HANDLER, CLAIMS_MANAGER | no | |
| Reported → Accepted | GeneralAssessmentAccept, POST /api/v1/general/assessments/{id}/accept | GENERAL_ASSESSMENT_ACCEPT | CLAIMS_HANDLER, CLAIMS_MANAGER | no | |
| Reported → InProgress | GeneralAssessmentRequestFurtherWork, POST /api/v1/general/assessments/{id}/further-work | GENERAL_ASSESSMENT_REQUEST_FURTHER_WORK | CLAIMS_HANDLER, CLAIMS_MANAGER | no | |

### General · RecoveryCase (`General/Insys.General.Domain/Claims/Assessments.cs:200`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| Open | only under its parent: GeneralListClaimRecoveries, GET /api/v1/general/claims/{id}/recoveries (`GeneralLists.cs:209-218`) | NO-LIST: recoveries waiting for a demand. The GENERAL_RECOVERIES screen's only list is per claim (`General/Insys.General.Api/GeneralFunctions.cs:94`) |
| Pursuing | only under its parent: GeneralListClaimRecoveries | NO-LIST: demands outstanding, waiting to be recorded or written off |
| Recovered | only under its parent | NO-LIST: waiting to be closed |
| WrittenOff | only under its parent | NO-LIST: waiting to be closed |
| Closed | only under its parent | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| Open → Pursuing | GeneralRecoverySendDemand, POST /api/v1/general/recoveries/{id}/demand | GENERAL_RECOVERY_SEND_DEMAND | CLAIMS_HANDLER, CLAIMS_MANAGER | no | |
| Pursuing → Recovered | GeneralRecordRecovery, POST /api/v1/general/recoveries/{id}/recovered | GENERAL_RECORD_RECOVERY | CLAIMS_HANDLER, CLAIMS_MANAGER | no | |
| Pursuing → WrittenOff | GeneralRecoveryWriteOff, POST /api/v1/general/recoveries/{id}/write-off | GENERAL_RECOVERY_WRITE_OFF | CLAIMS_HANDLER, CLAIMS_MANAGER | no | |
| Recovered → Closed | GeneralRecoveryClose, POST /api/v1/general/recoveries/{id}/close | GENERAL_RECOVERY_CLOSE | CLAIMS_HANDLER, CLAIMS_MANAGER | no | |
| WrittenOff → Closed | GeneralRecoveryClose, POST /api/v1/general/recoveries/{id}/close | GENERAL_RECOVERY_CLOSE | CLAIMS_HANDLER, CLAIMS_MANAGER | no | |

### General · Renewal (`General/Insys.General.Domain/Renewals/Renewal.cs:32`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| Reviewing | GeneralListRenewals, GET /api/v1/general/renewals (status filter, all statuses; `General/Insys.General.Infrastructure/Persistence/GeneralStores.cs:344-351`). Its screen GENERAL_RENEWALS is held only by AUDITOR | SPEC-DIFF: Renewal is not an aggregate in domain-model.json (general has 8 aggregates) |
| Referred | GeneralListRenewals (status filter); Workflow review task (`Processes.cs:822-870`) in WorkflowListOpenTasks (AUDITOR, OPERATIONS_ANALYST) | |
| NotEligible | GeneralListRenewals (status filter); Workflow review task (as above) | |
| Offered | GeneralListRenewals (status filter); PortalGeneralListRenewals for MEMBER and BROKER once invited (`GeneralLists.cs:228-237`) | |
| Renewed | GeneralListRenewals (status filter) | |
| NotRenewed | GeneralListRenewals (status filter). Never assigned anywhere (grep `RenewalStatus.NotRenewed\b`) | |
| Accepted | GeneralListRenewals (status filter) | |
| DeclinedByCustomer | GeneralListRenewals (status filter) | |
| NotRenewedByInsurer | GeneralListRenewals (status filter) | |
| Lapsed | GeneralListRenewals (status filter) | |

Note: no staff template other than AUDITOR holds GENERAL_RENEWALS (`GeneralFunctions.cs:61`), so the staff who act on renewals have no list.

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → Reviewing | GeneralJobs ReviewRenewal (OpenReview, `Renewal.cs:501`); InviteRenewal for unreviewed products (OpenOffered → Offered at once, `Renewal.cs:322-335`; `GeneralPolicies.cs:487-505`) | job (GENERAL_DAILY_JOBS) | - | no | |
| Reviewing → Offered | Conclude, when the last check passes: RuleDecisionRecorded or PremiumCalculated handlers (`General/Insys.General.Application/RenewalReview.cs:164`, `Renewal.cs:715-750`) | event | - | no | |
| Reviewing → Referred | Conclude on a REFER check (`Renewal.cs:734-740`) | event | - | no | |
| Reviewing → NotEligible | Conclude on a FAIL check (`Renewal.cs:721-727`) | event | - | no | |
| Referred → Offered | GeneralDecideRenewal, POST /api/v1/general/renewals/{id}/decision (offer) | GENERAL_DECIDE_RENEWAL | - | own rule: underwriting authority (`RenewalReview.cs:235`) | NO-ROLE: no template holds GENERAL_DECIDE_RENEWAL (`GeneralFunctions.cs:62`); FOLLOW-UP: the Workflow review task is routed, but its holders can neither list tasks nor decide |
| Referred → NotEligible | GeneralDecideRenewal, POST /api/v1/general/renewals/{id}/decision (not offered, `Renewal.cs:680-684`) | GENERAL_DECIDE_RENEWAL | - | own rule | NO-ROLE: as above |
| NotEligible → Offered | GeneralDecideRenewal, POST /api/v1/general/renewals/{id}/decision (offer) | GENERAL_DECIDE_RENEWAL | - | own rule | NO-ROLE: as above |
| Offered → Accepted (staff) | GeneralAcceptRenewal, POST /api/v1/general/renewals/{id}/accept | GENERAL_RECORD_RENEWAL_ANSWER | - | no | NO-ROLE: no template holds GENERAL_RECORD_RENEWAL_ANSWER (`GeneralFunctions.cs:63`) |
| Offered → Accepted (portal) | PortalGeneralAcceptRenewal, POST /api/v1/portal/general/renewals/{id}/accept | CUSTOMER_PORTAL_GENERAL_ANSWER_RENEWAL / BROKER_PORTAL_GENERAL_ANSWER_RENEWAL | MEMBER, BROKER | no | |
| Offered / DeclinedByCustomer → Accepted (retention discount) | GeneralRetentionOfferAccepted handler → ApplyRetentionDiscount (`Retention.cs:211-235`, `Renewal.cs:383-401`) | event (from GENERAL_WORK_RETENTION_CASE) | - | no | NO-ROLE: the offer answer that triggers it is GENERAL_WORK_RETENTION_CASE, held by no template |
| Offered / Accepted → DeclinedByCustomer (staff) | GeneralDeclineRenewal, POST /api/v1/general/renewals/{id}/decline | GENERAL_RECORD_RENEWAL_ANSWER | - | no | NO-ROLE: as above |
| Offered / Accepted → DeclinedByCustomer (portal) | PortalGeneralDeclineRenewal, POST /api/v1/portal/general/renewals/{id}/decline | CUSTOMER_PORTAL_GENERAL_ANSWER_RENEWAL / BROKER_PORTAL_GENERAL_ANSWER_RENEWAL | MEMBER, BROKER | no | |
| Offered / Accepted → Renewed | GeneralRenewPolicy, POST /api/v1/general/policies/{id}/renew, or the RenewPolicy job (`General/Insys.General.Application/PolicyAdministration.cs:224`, `Renewal.cs:692-702`) | GENERAL_RENEW_POLICY / job | GENERAL_UNDERWRITER | no | |
| Offered (awaiting acceptance) → Lapsed | GeneralJobs ExpirePolicy → Renewal.Lapse (`GeneralPolicies.cs:526-528`, `Renewal.cs:405-415`) | job | - | no | |
| Renewed → Lapsed | GeneralJobs CancelForNonPayment → LapsedUnpaid (`GeneralPolicies.cs:477-481`, `Renewal.cs:430-437`) | job | - | no | |
| Reviewing / Referred / NotEligible / Offered / Accepted → NotRenewedByInsurer (no approval) | GeneralProposeNonRenewal, POST /api/v1/general/policies/{id}/non-renewal (`General/Insys.General.Application/RenewalResponses.cs:141-150`) | GENERAL_PROPOSE_NON_RENEWAL | - | no | NO-ROLE: no template holds GENERAL_PROPOSE_NON_RENEWAL (`GeneralFunctions.cs:64`) |
| (same) → NotRenewedByInsurer (approval) | ApprovalGranted handler (`GeneralPolicies.cs:260-271`) after GeneralProposeNonRenewal | GENERAL_PROPOSE_NON_RENEWAL, then WORKFLOW_APPROVE | - | yes: approval type GENERAL_NON_RENEWAL (checker needs general_policy.non_renew; `Workflow/Insys.Workflow.Api/WorkflowModule.cs:210`). Requester ≠ checker is not checked in General: unverified in Workflow | NO-ROLE: neither the maker function nor GENERAL_NON_RENEWAL authority is in any template (templates.tsv) |

### General · Renewal non-renewal status (`General/Insys.General.Domain/Renewals/Renewal.cs:69`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| REQUESTED | only under its parent: GeneralGetRenewal. The approval appears in WorkflowListApprovals (businessRefType=GENERAL_RENEWAL); GeneralListRenewals cannot filter by it | |
| DECIDED | only under its parent; GeneralListRenewals?status=NOT_RENEWED_BY_INSURER | |
| REJECTED | only under its parent | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (none) / REJECTED → REQUESTED | GeneralProposeNonRenewal, POST /api/v1/general/policies/{id}/non-renewal (`Renewal.cs:444-462`) | GENERAL_PROPOSE_NON_RENEWAL | - | no | NO-ROLE: no template holds it |
| REQUESTED → DECIDED (no approval) | same command (`RenewalResponses.cs:149`) | GENERAL_PROPOSE_NON_RENEWAL | - | no | |
| REQUESTED → DECIDED (approval) | ApprovalGranted handler (`GeneralPolicies.cs:260-271`, `Renewal.cs:469-497`) | WORKFLOW_APPROVE | no template has GENERAL_NON_RENEWAL authority | yes: GENERAL_NON_RENEWAL | FOLLOW-UP: no template can see or decide it, so it waits until the approval expires (→ REJECTED) |
| REQUESTED → REJECTED | ApprovalRejected handler (reject, expiry or withdrawal; `Renewal.cs:478-482`) | WORKFLOW_REJECT / expiry | - | yes: GENERAL_NON_RENEWAL | |

### General · RenewalCheck (`General/Insys.General.Domain/Renewals/Renewal.cs:108`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| PENDING | only under its parent: GeneralGetRenewal | |
| PASS | only under its parent | |
| REFER | only under its parent | |
| FAIL | only under its parent | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| PENDING → PASS / REFER / FAIL | RuleDecisionRecorded, PremiumCalculated and PremiumNotCalculated handlers (`RenewalReview.cs:164-165`, `GeneralPolicies.cs:85-89`, `:126-130`, `Renewal.cs:129-132`) | event | - | no | |

Note: if Rules never answers a PENDING eligibility or underwriting check, the renewal stays in Reviewing. Unverified: no timeout was found.

### General · RetentionCase (`General/Insys.General.Domain/Retention/RetentionCase.cs:119`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| OPEN | GeneralListRetentionCases, GET /api/v1/general/retention-cases (status filter OPEN, CLOSED; `General/Insys.General.Application/Retention.cs:55`). Its screen GENERAL_RETENTION_CASES is held only by AUDITOR. Workflow GeneralRetention task (`Processes.cs:908`) | SPEC-DIFF: RetentionCase is not an aggregate in domain-model.json |
| CLOSED | GeneralListRetentionCases (status filter) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| OPEN → CLOSED | GeneralCloseRetentionCase, POST /api/v1/general/retention-cases/{id}/outcome (`RetentionCase.cs:224`) | GENERAL_WORK_RETENTION_CASE | - | no | NO-ROLE: no template holds GENERAL_WORK_RETENTION_CASE (`GeneralFunctions.cs:66-67`). Cases opened automatically (renewal declined, `Retention.cs:213-223`; lapse risk job, `Retention.cs:174`) cannot be worked or closed by any template |

### General · RetentionOffer (`General/Insys.General.Domain/Retention/RetentionCase.cs:77`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| PENDING | only under its parent: GeneralGetRetentionCase (open cases listable through GeneralListRetentionCases?status=OPEN) | |
| ACCEPTED | only under its parent | |
| DECLINED | only under its parent | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| PENDING → ACCEPTED | GeneralAnswerRetentionOffer, POST /api/v1/general/retention-cases/{id}/offers/{offerId}/answer (`RetentionCase.cs:207-215`) | GENERAL_WORK_RETENTION_CASE | - | no | NO-ROLE: held by no template. FOLLOW-UP: a mid-term discount or payment-terms offer drafts an endorsement left Rated for the case owner to submit (`Retention.cs:241-246`, `:265`); nothing leads them to it |
| PENDING → DECLINED | GeneralAnswerRetentionOffer, POST /api/v1/general/retention-cases/{id}/offers/{offerId}/answer | GENERAL_WORK_RETENTION_CASE | - | no | NO-ROLE: held by no template |

## Health, Claims Framework, Payments, Billing and Finance

Paths are relative to `src/Modules/` unless they start with `db/` or `docs/`. "job HealthJobs" = `Health/Insys.Health.Infrastructure/HealthInfrastructure.cs:240`. Approval authority: a template "holds" an approval type when its `Approves` list names it (`Identity/Insys.Identity.Api/RoleTemplates.cs:155-224`); the starter pack's authority rows all name the role `APPROVER` (`docs/specs/starter-packs/base.json:545-592`).

### Health · HealthScheme (`Health/Insys.Health.Domain/Members/Membership.cs:33`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| REGISTERED | HealthListSchemes GET /health/schemes (status filter) | |
| ACTIVE | HealthListSchemes (status filter) | |
| TERMINATED | HealthListSchemes (status filter) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| Registered → Active | job HealthJobs ActivateScheme (HealthEventPolicies.cs:329); at registration if start ≤ today (Membership.cs:168) | job | - | no | |
| Active → Active | HealthSchemeRenew, POST /health/schemes/{id}/renew | HEALTH_SCHEME_RENEW | HEALTH_MEMBERSHIP_ADMINISTRATOR | no | |
| Active → Terminated | HealthSchemeTerminate, POST /health/schemes/{id}/terminate | HEALTH_SCHEME_TERMINATE | HEALTH_MEMBERSHIP_ADMINISTRATOR | no | |

### Health · HealthMembership (`Health/Insys.Health.Domain/Members/Membership.cs:48`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| PENDING | HealthListMemberships GET /health/memberships (status filter) | |
| ACTIVE | HealthListMemberships (status filter) | |
| SUSPENDED | HealthListMemberships (status filter) | FOLLOW-UP: paid-up but not auto-reinstated memberships can't be told apart from other suspended ones (see note) |
| TERMINATED | HealthListMemberships (status filter) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| Pending → Active | job HealthJobs ActivateMembership (HealthEventPolicies.cs:328); at enrolment if start ≤ today (Membership.cs:447) | job | - | no | |
| Active → Suspended | HealthMembershipSuspend, POST /health/memberships/{id}/suspend | HEALTH_MEMBERSHIP_SUSPEND | HEALTH_MEMBERSHIP_ADMINISTRATOR | no | |
| Active → Suspended (arrears) | job HealthJobs SuspendForArrears (HealthEventPolicies.cs:303, Membership.cs:621) | job | - | no | |
| Suspended → Active | HealthMembershipReinstate, POST /health/memberships/{id}/reinstate; or event: arrears paid (Grace.cs:77, Membership.cs:587) | HEALTH_MEMBERSHIP_REINSTATE | HEALTH_MEMBERSHIP_ADMINISTRATOR | no | FOLLOW-UP: with reinstate_on_payment off, arrears paid leaves it SUSPENDED and closes the task, with no new task or notice to reinstate (Membership.cs:578-585; Workflow/Insys.Workflow.Application/Processes.cs:711) |
| Active → Terminated | HealthMembershipTerminate, POST /health/memberships/{id}/terminate | HEALTH_MEMBERSHIP_TERMINATE | HEALTH_MEMBERSHIP_ADMINISTRATOR | no | |
| Suspended → Terminated | HealthMembershipTerminate | HEALTH_MEMBERSHIP_TERMINATE | HEALTH_MEMBERSHIP_ADMINISTRATOR | no | covered: arrears notice expiry opens a Workflow task in queue HEALTH_MEMBERSHIP (Membership.cs:640, Processes.cs:689) |

### Health · Dependant (`Health/Insys.Health.Domain/Members/Membership.cs:251`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| ACTIVE | only under its parent: HealthGetMembership | |
| REMOVED | only under its parent: HealthGetMembership | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| Active → Removed | HealthRemoveDependant, POST /health/memberships/{id}/dependants/{dependantId}/remove | HEALTH_REMOVE_DEPENDANT | HEALTH_MEMBERSHIP_ADMINISTRATOR | no | |

### Health · BenefitAccumulator (`Health/Insys.Health.Domain/Benefits/BenefitAccumulator.cs:44`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| Open (spec) | only under its parent: HealthGetAccumulators | SPEC-DIFF: the code has no status field (BenefitAccumulator.cs:44) |
| Exhausted (spec) | only under its parent: HealthGetAccumulators | |
| Closed (spec) | only under its parent: HealthGetAccumulators | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| Open → Exhausted | spec "Limit reached": none in code | - | - | no | SPEC-DIFF: spec lifecycle not modelled (BenefitAccumulator.cs:44) |
| Open → Closed | spec "Period ends": none in code | - | - | no | |
| Exhausted → Closed | spec "Period ends": none in code | - | - | no | |

### Health · Preauthorisation (`Health/Insys.Health.Domain/Care/Preauthorisation.cs:32`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| REQUESTED | HealthListAuthorisations GET /health/preauthorisations (status filter) | |
| IN_REVIEW | HealthListAuthorisations (status filter) | |
| APPROVED | HealthListAuthorisations (status filter) | |
| CONDITIONALLY_APPROVED | HealthListAuthorisations (status filter) | |
| REJECTED | HealthListAuthorisations (status filter) | |
| UTILISED | HealthListAuthorisations (status filter) | |
| EXPIRED | HealthListAuthorisations (status filter) | |
| CANCELLED | HealthListAuthorisations (status filter) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| Requested → In review | HealthReviewAuthorisation, POST /health/preauthorisations/{id}/review | HEALTH_REVIEW_AUTHORISATION | HEALTH_CLAIMS_ASSESSOR | no | |
| In review → Approved | HealthApproveAuthorisation, POST /health/preauthorisations/{id}/approve (no conditions) | HEALTH_APPROVE_AUTHORISATION | HEALTH_CLAIMS_ASSESSOR | no | |
| In review → Conditionally approved | HealthApproveAuthorisation (with conditions, Preauthorisation.cs:239) | HEALTH_APPROVE_AUTHORISATION | HEALTH_CLAIMS_ASSESSOR | no | |
| In review → Rejected | HealthRejectAuthorisation, POST /health/preauthorisations/{id}/reject | HEALTH_REJECT_AUTHORISATION | HEALTH_CLAIMS_ASSESSOR | no | |
| Approved → Utilised | event MedicalClaimAdjudicated (HealthEventPolicies.cs:78) | event | - | no | |
| Approved → Expired | job HealthJobs ExpireAuthorisation (HealthEventPolicies.cs:326) | job | - | no | |
| Approved → Cancelled | HealthCancelAuthorisation, POST /health/preauthorisations/{id}/cancel | HEALTH_CANCEL_AUTHORISATION | HEALTH_CLAIMS_ASSESSOR | no | |
| Conditionally approved → Approved | HealthConfirmConditionsMet, POST /health/preauthorisations/{id}/conditions-met | HEALTH_CONFIRM_CONDITIONS_MET | HEALTH_CLAIMS_ASSESSOR | no | |
| Conditionally approved → Utilised | event MedicalClaimAdjudicated (HealthEventPolicies.cs:78) | event | - | no | |
| Conditionally approved → Expired | job HealthJobs ExpireAuthorisation | job | - | no | |
| Conditionally approved → Cancelled | HealthCancelAuthorisation | HEALTH_CANCEL_AUTHORISATION | HEALTH_CLAIMS_ASSESSOR | no | |
| Utilised → Approved | HealthReverseClaim side effect (Application/Care.cs:768, Preauthorisation.cs:348) | HEALTH_REVERSE_CLAIM | HEALTH_CLAIMS_ASSESSOR | no | SPEC-DIFF: ClaimReversed transition not in domain-model.json (Preauthorisation.cs:152) |

### Health · MedicalClaim (`Health/Insys.Health.Domain/Care/MedicalClaim.cs:33`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| RECEIVED | HealthListClaims GET /health/claims (status filter) | |
| VALIDATED | HealthListClaims (status filter) | |
| ADJUDICATED | HealthListClaims (status filter) | |
| PENDED | HealthListClaims (status filter) | |
| APPROVED_FOR_PAYMENT | HealthListClaims (status filter) | FOLLOW-UP: paid only once finance opens and cuts off a run; HEALTH_CLAIMS (the list) is held by HEALTH_AUDITOR, HEALTH_CLAIMS_ASSESSOR only, not by the finance roles that run payment runs (fn_roles.tsv) |
| PAID | HealthListClaims (status filter) | |
| REJECTED | HealthListClaims (status filter) | |
| REVERSED | HealthListClaims (status filter) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| Received → Validated | HealthValidateClaim, POST /health/claims/{id}/validate | HEALTH_VALIDATE_CLAIM | HEALTH_CLAIMS_ASSESSOR | no | |
| Validated → Adjudicated | HealthAdjudicateClaim, POST /health/claims/{id}/adjudicate | HEALTH_ADJUDICATE_CLAIM | HEALTH_CLAIMS_ASSESSOR | no | |
| Adjudicated → Pended | HealthAdjudicateClaim (auto when lines need review, MedicalClaim.cs:389) | HEALTH_ADJUDICATE_CLAIM | HEALTH_CLAIMS_ASSESSOR | no | |
| Pended → Adjudicated | HealthReviewPendedClaim, POST /health/claims/{id}/review; job HealthJobs releases arrears-held claims (HealthInfrastructure.cs:448) | HEALTH_REVIEW_PENDED_CLAIM | HEALTH_CLAIMS_ASSESSOR | no | |
| Adjudicated → Approved for payment | HealthApproveClaim, POST /health/claims/{id}/approve | HEALTH_APPROVE_CLAIM | HEALTH_CLAIMS_ASSESSOR | no | |
| Approved for payment → Paid | event ProviderPaymentRunReleased (HealthEventPolicies.cs:148) | event | - | no | |
| Validated → Rejected | HealthRejectClaim, POST /health/claims/{id}/reject; HealthValidateClaim with a problem (MedicalClaim.cs:360) | HEALTH_REJECT_CLAIM | HEALTH_CLAIMS_ASSESSOR | no | |
| Pended → Rejected | HealthRejectClaim; job HealthJobs DeclineHeldClaim (HealthEventPolicies.cs:318) | HEALTH_REJECT_CLAIM | HEALTH_CLAIMS_ASSESSOR | no | SPEC-DIFF: not in domain-model.json (MedicalClaim.cs:250) |
| Paid → Reversed | HealthReverseClaim, POST /health/claims/{id}/reverse | HEALTH_REVERSE_CLAIM | HEALTH_CLAIMS_ASSESSOR | no | |
| Rejected → Validated | event Workflow CaseDecided upheld (Application/CaseReassessment.cs, MedicalClaim.cs:442) | event | - | no | SPEC-DIFF: ReopenClaim not in domain-model.json (MedicalClaim.cs:252) |

### Health · UtilisationCase (`Health/Insys.Health.Domain/Care/Cases.cs:32`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| OPEN | HealthListUtilisationCases GET /health/utilisation-cases (status filter) | |
| IN_REVIEW | HealthListUtilisationCases (status filter) | |
| CONCLUDED | HealthListUtilisationCases (status filter) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| Open → In review | HealthReviewUtilisationCase, POST /health/utilisation-cases/{id}/review | HEALTH_REVIEW_UTILISATION_CASE | HEALTH_CLAIMS_ASSESSOR | no | |
| In review → Concluded | HealthConcludeUtilisationCase, POST /health/utilisation-cases/{id}/conclude | HEALTH_CONCLUDE_UTILISATION_CASE | HEALTH_CLAIMS_ASSESSOR | no | |

### Health · HealthFraudCase (`Health/Insys.Health.Domain/Care/Cases.cs:46`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| OPENED | HealthListFraudCases GET /health/fraud-cases (status filter) | |
| INVESTIGATING | HealthListFraudCases (status filter) | |
| CONCLUDED | HealthListFraudCases (status filter) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| Opened → Investigating | HealthInvestigateFraudCase, POST /health/fraud-cases/{id}/investigate | HEALTH_INVESTIGATE_FRAUD_CASE | HEALTH_CLAIMS_ASSESSOR | no | |
| Investigating → Concluded | HealthConcludeFraudCase, POST /health/fraud-cases/{id}/conclude | HEALTH_CONCLUDE_FRAUD_CASE | HEALTH_CLAIMS_ASSESSOR | no | |

### Health · ProviderPaymentRun (`Health/Insys.Health.Domain/Payments/ProviderPaymentRun.cs:32`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| OPEN | HealthListRuns GET /health/payment-runs (status filter) | |
| CUT_OFF | HealthListRuns (status filter) | |
| CALCULATED | HealthListRuns (status filter) | |
| APPROVED | HealthListRuns (status filter) | FOLLOW-UP: must be released by a person; no task or notice on approval (HealthEventPolicies.cs:97-109), found only by listing status=APPROVED |
| RELEASED | HealthListRuns (status filter) | STUCK: if a remittance payment fails, nothing handles it (no PaymentFailed handler in Health; RecordPayment only on PaymentSucceeded, HealthEventPolicies.cs:165-170) |
| RECONCILED | HealthListRuns (status filter) | |
| REJECTED | HealthListRuns (status filter) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| Open → Cut off | HealthCutOffRun, POST /health/payment-runs/{id}/cut-off | HEALTH_CUT_OFF_RUN | FINANCE_MANAGER, FINANCE_OFFICER | no | |
| Cut off → Calculated | HealthCalculateRun, POST /health/payment-runs/{id}/calculate (raises approval) | HEALTH_CALCULATE_RUN | FINANCE_MANAGER, FINANCE_OFFICER | no | |
| Calculated → Approved | event ApprovalGranted (HealthEventPolicies.cs:97) | event | approver: FINANCE_MANAGER (Approves PROVIDER_PAYMENT_RUN) | yes: approval type PROVIDER_PAYMENT_RUN (requester ≠ checker) | |
| Calculated → Rejected | event ApprovalRejected (HealthEventPolicies.cs:116) | event | approver: FINANCE_MANAGER | yes: approval type PROVIDER_PAYMENT_RUN | |
| Approved → Released | HealthReleaseRun, POST /health/payment-runs/{id}/release (HealthModule.cs:542) | HEALTH_RELEASE_RUN | FINANCE_MANAGER, FINANCE_OFFICER | no | FOLLOW-UP: see APPROVED |
| Released → Reconciled | event PaymentSucceeded (HealthEventPolicies.cs:165; ProviderPaymentRun.cs:300) | event | - | no | |

### Health · ProviderRecovery (`Health/Insys.Health.Domain/Payments/ProviderRecovery.cs:46`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| PROPOSED | HealthListRecoveries GET /health/provider-recoveries (status filter) | |
| OPEN | HealthListRecoveries (status filter) | |
| INVOICED | HealthListRecoveries (status filter) | STUCK: only exit is InvoiceSettled (ProviderRecovery.cs:420); write-off needs OPEN (:440), reverse needs no invoice (:492), and a fully credited invoice raises no InvoiceSettled (Billing/Insys.Billing.Domain/Billing/Invoice.cs:276-279) |
| RECOVERED | HealthListRecoveries (status filter) | |
| WRITTEN_OFF | HealthListRecoveries (status filter) | |
| REVERSED | HealthListRecoveries (status filter) | |
| REJECTED | HealthListRecoveries (status filter) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → Proposed | HealthProposeRecoveryFinding, POST /health/provider-recoveries | HEALTH_PROPOSE_RECOVERY_FINDING | FINANCE_MANAGER, FINANCE_OFFICER | yes: raises PROVIDER_RECOVERY_FINDING | |
| (new) → Open | event MedicalClaimReversed / credit note approved (ProviderRecoveries.cs:440, :468) | event | - | no | |
| Proposed → Open | event ApprovalGranted (ProviderRecoveries.cs:459, :493) | event | approver: no template's Approves lists PROVIDER_RECOVERY_FINDING | yes: approval type PROVIDER_RECOVERY_FINDING (requester ≠ checker) | NO-ROLE: no template's Approves names PROVIDER_RECOVERY_FINDING (RoleTemplates.cs:188); only the generic APPROVER starter row (base.json:571) |
| Proposed → Rejected | event ApprovalRejected (ProviderRecoveries.cs:462) | event | as above | yes: PROVIDER_RECOVERY_FINDING | NO-ROLE: as above |
| Open → Invoiced | HealthInvoiceRecovery, POST /health/provider-recoveries/{id}/invoice | HEALTH_INVOICE_RECOVERY | FINANCE_MANAGER, FINANCE_OFFICER | no | covered: uncollected recoveries open a Workflow review task (job HealthJobs RecoveriesDue, HealthInfrastructure.cs:304; Processes.cs:1052) |
| Open → Recovered | event ProviderPaymentRunReleased, offsets taken (HealthEventPolicies.cs:159) | event | - | no | |
| Invoiced → Recovered | event InvoiceSettled (ProviderRecoveries.cs:553) | event | - | no | STUCK: see INVOICED |
| Open → Written off | HealthWriteOffRecovery, POST /health/provider-recoveries/{id}/write-off, then event ApprovalGranted | HEALTH_WRITE_OFF_RECOVERY | - (privileged, granted individually) | yes: approval type PROVIDER_RECOVERY_WRITE_OFF | NO-ROLE: no template's Approves names PROVIDER_RECOVERY_WRITE_OFF (RoleTemplates.cs:188) |
| Open → Reversed | HealthReverseRecovery, POST /health/provider-recoveries/{id}/reverse | HEALTH_REVERSE_RECOVERY | - (privileged, granted individually) | no | |
| Recovered → Reversed | HealthReverseRecovery | HEALTH_REVERSE_RECOVERY | - (privileged, granted individually) | no | |
Note: not in domain-model.json (provider recoveries addendum).

### Health · ProviderRecoveryOffset (`Health/Insys.Health.Domain/Payments/ProviderRecovery.cs:63`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| RESERVED | only under its parent: HealthGetRecovery | |
| TAKEN | only under its parent: HealthGetRecovery | |
| RELEASED | only under its parent: HealthGetRecovery | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → Reserved | HealthCalculateRun (Application/Runs.cs:187) | HEALTH_CALCULATE_RUN | FINANCE_MANAGER, FINANCE_OFFICER | no | |
| Reserved → Taken | event ProviderPaymentRunReleased (HealthEventPolicies.cs:159) | event | - | no | |
| Reserved → Released | event ApprovalRejected on the run (HealthEventPolicies.cs:136) | event | - | no | |

### Health · ProviderCreditNote (`Health/Insys.Health.Domain/Payments/ProviderRecovery.cs:70`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| SUBMITTED | HealthListCreditNotes GET /health/provider-credit-notes (status filter) | |
| APPROVED | HealthListCreditNotes (status filter) | |
| REJECTED | HealthListCreditNotes (status filter) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → Submitted | HealthSubmitCreditNote, POST /health/provider-credit-notes (also provider portal) | HEALTH_SUBMIT_CREDIT_NOTE | FINANCE_MANAGER, FINANCE_OFFICER | yes: raises PROVIDER_CREDIT_NOTE | |
| Submitted → Approved | event ApprovalGranted (ProviderRecoveries.cs:468) | event | approver: none in any template's Approves | yes: approval type PROVIDER_CREDIT_NOTE (requester ≠ checker) | NO-ROLE: no template's Approves names PROVIDER_CREDIT_NOTE (RoleTemplates.cs:188; base.json:547 names role APPROVER) |
| Submitted → Rejected | event ApprovalRejected (ProviderRecoveries.cs:468) | event | as above | yes: PROVIDER_CREDIT_NOTE | NO-ROLE: as above |
Note: not in domain-model.json.

### Health · ProviderStatement (`Health/Insys.Health.Domain/Providers/ProviderMonitoring.cs:152`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| EXCEPTIONS | HealthListProviderStatements GET /health/provider-statements (status filter) | |
| RECONCILED | HealthListProviderStatements (status filter) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → Exceptions/Reconciled | HealthSubmitProviderStatement, POST /health/provider-statements; HealthImportProviderStatement (ProviderMonitoring.cs:268) | HEALTH_SUBMIT_PROVIDER_STATEMENT | FINANCE_MANAGER, FINANCE_OFFICER | no | |
| Exceptions → Reconciled | HealthResolveStatementLine, POST /health/provider-statements/{id}/lines/{lineId}/resolve (last open line, :286) | HEALTH_RESOLVE_STATEMENT_LINE | FINANCE_MANAGER, FINANCE_OFFICER | no | |
Note: a provider measure breach opens a Workflow review task (Processes.cs:1027), but its action HEALTH_RECORD_PROVIDER_QUALITY_REVIEW is in no template and screen HEALTH_PROVIDER_PERFORMANCE only in AUDITOR (Health/Insys.Health.Api/HealthFunctions.cs:126-128): NO-ROLE.

### Health · ComplexCase (`Health/Insys.Health.Domain/Care/ComplexCase.cs:31`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| OPEN | HealthListComplexCases GET /health/complex-cases (status filter) | |
| ACTIVE | HealthListComplexCases (status filter) | |
| ON_HOLD | HealthListComplexCases (status filter) | |
| CLOSED | HealthListComplexCases (status filter) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| Open → Active | HealthAssignCaseManager, POST /health/complex-cases/{id}/case-manager | HEALTH_ASSIGN_CASE_MANAGER | HEALTH_CLAIMS_ASSESSOR | no | |
| Active → On hold | HealthHoldComplexCase, POST /health/complex-cases/{id}/hold | HEALTH_HOLD_COMPLEX_CASE | HEALTH_CLAIMS_ASSESSOR | no | |
| On hold → Active | HealthResumeComplexCase, POST /health/complex-cases/{id}/resume | HEALTH_RESUME_COMPLEX_CASE | HEALTH_CLAIMS_ASSESSOR | no | |
| Open → Closed | HealthCloseComplexCase, POST /health/complex-cases/{id}/close | HEALTH_CLOSE_COMPLEX_CASE | HEALTH_CLAIMS_ASSESSOR | no | |
| Active → Closed | HealthCloseComplexCase | HEALTH_CLOSE_COMPLEX_CASE | HEALTH_CLAIMS_ASSESSOR | no | |
| On hold → Closed | HealthCloseComplexCase | HEALTH_CLOSE_COMPLEX_CASE | HEALTH_CLAIMS_ASSESSOR | no | |
Note: not in domain-model.json (complex care addendum).

### Health · CarePlanItem (`Health/Insys.Health.Domain/Care/ComplexCase.cs:52`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| OPEN | only under its parent: HealthGetComplexCase | |
| DONE | only under its parent: HealthGetComplexCase | |
| MISSED | only under its parent: HealthGetComplexCase | |
| CHANGED | only under its parent: HealthGetComplexCase | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| Open → Done | HealthRecordCareProgress, POST /health/complex-cases/{id}/care-plan/items/{itemId}/progress | HEALTH_RECORD_CARE_PROGRESS | HEALTH_CLAIMS_ASSESSOR | no | |
| Open → Missed | HealthRecordCareProgress | HEALTH_RECORD_CARE_PROGRESS | HEALTH_CLAIMS_ASSESSOR | no | |
| Open → Changed | HealthRecordCareProgress | HEALTH_RECORD_CARE_PROGRESS | HEALTH_CLAIMS_ASSESSOR | no | |
Note: overdue OPEN activities raise one Workflow task each (ComplexCase.cs:368; Processes.cs:946), so no tenant-wide list is needed.

### Health · ClinicalReferral (`Health/Insys.Health.Domain/Care/ComplexCase.cs:388`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| REQUESTED | HealthListReferrals GET /health/referrals (status filter) | |
| APPROVED | HealthListReferrals (status filter) | |
| DECLINED | HealthListReferrals (status filter) | |
| USED | HealthListReferrals (status filter) | |
| EXPIRED | HealthListReferrals (status filter) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| Requested → Approved | HealthDecideReferral, POST /health/referrals/{id}/decide | HEALTH_DECIDE_REFERRAL | HEALTH_CLAIMS_ASSESSOR | no | |
| Requested → Declined | HealthDecideReferral | HEALTH_DECIDE_REFERRAL | HEALTH_CLAIMS_ASSESSOR | no | |
| Approved → Used | HealthRecordReferralVisit, POST /health/referrals/{id}/visits (last visit, ComplexCase.cs:533) | HEALTH_RECORD_REFERRAL_VISIT | HEALTH_CLAIMS_ASSESSOR | no | |
| Requested → Expired | job HealthJobs CareDue → ExpireClinicalReferral (HealthInfrastructure.cs:367) | job | - | no | |
| Approved → Expired | job HealthJobs CareDue → ExpireClinicalReferral | job | - | no | |

### Health · ProgrammeEnrolment (`Health/Insys.Health.Domain/Care/ProgrammeEnrolment.cs:33`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| ACTIVE | HealthListProgrammeEnrolments GET /health/programme-enrolments (status filter) | |
| ENDED | HealthListProgrammeEnrolments (status filter) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| Active → Ended | HealthEndProgrammeEnrolment, POST /health/programme-enrolments/{id}/end | HEALTH_END_PROGRAMME_ENROLMENT | HEALTH_CLAIMS_ASSESSOR | no | |

### Health · ChronicRegistration (`Health/Insys.Health.Domain/Care/ChronicRegistration.cs:34`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| REQUESTED | HealthListChronicRegistrations GET /health/chronic-registrations (status filter) | |
| APPROVED | HealthListChronicRegistrations (status filter) | |
| DECLINED | HealthListChronicRegistrations (status filter) | |
| ENDED | HealthListChronicRegistrations (status filter) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| Requested → Approved | HealthApproveChronicRegistration, POST /health/chronic-registrations/{id}/approve | HEALTH_APPROVE_CHRONIC_REGISTRATION | HEALTH_CLAIMS_ASSESSOR | yes: own rule (ChronicRegistration.cs:195) | |
| Requested → Declined | HealthDeclineChronicRegistration, POST /health/chronic-registrations/{id}/decline | HEALTH_DECLINE_CHRONIC_REGISTRATION | HEALTH_CLAIMS_ASSESSOR | yes: own rule (ChronicRegistration.cs:195) | |
| Approved → Ended | HealthEndChronicRegistration, POST /health/chronic-registrations/{id}/end | HEALTH_END_CHRONIC_REGISTRATION | HEALTH_CLAIMS_ASSESSOR | no | |
Note: an APPROVED registration past its valid_to stays APPROVED; no job ends it (unverified whether one is intended).

### ClaimsFramework · ClaimPaymentInstruction (`ClaimsFramework/Insys.ClaimsFramework.Domain/Claims/ClaimPaymentInstruction.cs:32`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| DRAFTED | only under its parent: ClaimsListInstructions GET /claims/claims/{claimNumber}/payment-instructions | NO-LIST: a draft waits for a person to submit it (ClaimsFrameworkModule.cs:219) |
| PENDING_APPROVAL | only under its parent: ClaimsListInstructions | (approval inbox covers it) |
| APPROVED | only under its parent: ClaimsListInstructions | NO-LIST, FOLLOW-UP: see Approved → Released |
| REJECTED | only under its parent: ClaimsListInstructions | |
| RELEASED | only under its parent: ClaimsListInstructions | |
| PAID | only under its parent: ClaimsListInstructions | |
| FAILED | only under its parent: ClaimsListInstructions | NO-LIST, FOLLOW-UP: a failed payment needs a person to retry; RecordOutcome raises no event on failure (ClaimPaymentInstruction.cs:371-393), so no task |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → Drafted | ClaimsDraftInstruction, POST /claims/claims/{claimNumber}/payment-instructions | CLAIMS_DRAFT_INSTRUCTION | CLAIMS_HANDLER | no | |
| Drafted → Pending approval | ClaimsSubmitInstruction, POST /claims/payment-instructions/{id}/submit; at creation from an approved settlement (ClaimPaymentInstruction.cs:229) | CLAIMS_SUBMIT_INSTRUCTION | CLAIMS_HANDLER, CLAIMS_MANAGER | yes: raises CLAIM_PAYMENT | |
| Pending approval → Approved | event ApprovalGranted (Application/Claims.cs:793) | event | approver: CLAIMS_MANAGER, FINANCE_MANAGER (Approves CLAIM_PAYMENT) | yes: approval type CLAIM_PAYMENT (requester ≠ checker) | |
| Pending approval → Rejected | event ApprovalRejected (Application/Claims.cs:813) | event | approver: CLAIMS_MANAGER, FINANCE_MANAGER | yes: CLAIM_PAYMENT | |
| Approved → Released | ClaimsReleaseInstruction, POST /claims/payment-instructions/{id}/release (Claims.cs:595); automatic for approved General/Life settlements (Claims.cs:934) | CLAIMS_RELEASE_INSTRUCTION | CLAIMS_MANAGER | no | FOLLOW-UP: a manually drafted instruction stays APPROVED after approval (Decide does not release, ClaimPaymentInstruction.cs:264-288); CLAIMS_MANAGER gets a task only when an automatic release is held (Processes.cs:503), otherwise no task and no tenant-wide list |
| Approved → Rejected | event GeneralClaimDeclined, held for arrears (WithdrawHeld, ClaimPaymentInstruction.cs:291) | event | - | no | SPEC-DIFF: WithdrawHeld not in domain-model.json (ClaimPaymentInstruction.cs:117) |
| Released → Paid | event PaymentSucceeded (Claims.cs:837) | event | - | no | |
| Released → Failed | event PaymentFailed (Claims.cs:840) | event | - | no | |
| Failed → Released | ClaimsRetryInstruction, POST /claims/payment-instructions/{id}/retry (new payee: approval first, ClaimPaymentInstruction.cs:396) | CLAIMS_RETRY_INSTRUCTION | CLAIMS_HANDLER, CLAIMS_MANAGER | yes: approval type CLAIM_PAYMENT for new payee details | FOLLOW-UP: see FAILED |

### ClaimsFramework · EvidenceItem (`ClaimsFramework/Insys.ClaimsFramework.Domain/Claims/ClaimRegistration.cs:94`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| REQUESTED | only under its parent: ClaimsGet GET /claims/claims/{claimNumber} | NO-LIST: outstanding evidence requests can only be found claim by claim (ClaimsFrameworkModule.cs:137) |
| RECEIVED | only under its parent: ClaimsGet | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → Requested | ClaimsRequestEvidence, POST /claims/claims/{claimNumber}/evidence-requests | CLAIMS_REQUEST_EVIDENCE | CLAIMS_HANDLER, CLAIMS_MANAGER | no | |
| Requested → Received | ClaimsRecordEvidence, POST /claims/claims/{claimNumber}/evidence | CLAIMS_RECORD_EVIDENCE | CLAIMS_HANDLER, CLAIMS_MANAGER | no | |

### ClaimsFramework · FraudCase (`ClaimsFramework/Insys.ClaimsFramework.Domain/Claims/FraudCase.cs:33`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| OPENED | ClaimsListFraudCases GET /claims/fraud-cases (all statuses, no filter) | NO-FILTER: cursor and limit only (ClaimsFrameworkModule.cs:97) |
| INVESTIGATING | ClaimsListFraudCases (no filter) | NO-FILTER |
| CONCLUDED | ClaimsListFraudCases (no filter) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| Opened → Investigating | ClaimsAssignFraudInvestigator, POST /claims/fraud-cases/{id}/assign | CLAIM_ASSIGN_FRAUD_INVESTIGATOR | CLAIMS_HANDLER, CLAIMS_MANAGER | no | |
| Investigating → Concluded | ClaimsConcludeFraudCase, POST /claims/fraud-cases/{id}/conclude | CLAIM_CONCLUDE_FRAUD_CASE | CLAIMS_HANDLER, CLAIMS_MANAGER | yes: own rule, the assigned investigator only (FraudCase.cs:110) | |
Note: not in domain-model.json (claims context lists only ClaimRegistration, ClaimFinancials, ClaimPaymentInstruction).

### Payments · Payment (`Payments/Insys.Payments.Domain/Payments/Payment.cs:32`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| INITIATED | PaymentsList GET /payments/payments (status filter) | FOLLOW-UP: payouts wait for FINANCE_OFFICER to build a batch; no task, found by listing status=INITIATED |
| PENDING | PaymentsList (status filter) | |
| SUCCEEDED | PaymentsList (status filter) | |
| FAILED | PaymentsList (status filter) | |
| UNKNOWN | PaymentsList (status filter); also as a reconciliation exception | |
| REVERSED | PaymentsList (status filter) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| Initiated → Pending | PaymentsInitiate, POST /payments/payments (inbound, Payments.cs:339); payouts on PaymentsReleaseBatch (Payments.cs:592) | PAYMENTS_INITIATE / PAYMENTS_RELEASE_BATCH | FINANCE_MANAGER, FINANCE_OFFICER / FINANCE_MANAGER | via batch approval | |
| Pending → Succeeded | provider answer at send (Payments.cs:382) or PaymentsSignedCallback, POST /payments/callbacks/{endpointKey} (Callbacks.cs:197) | - (provider, signed) | - | no | |
| Pending → Failed | as above | - (provider, signed) | - | no | |
| Pending → Unknown | job PaymentJobs (Payments/Insys.Payments.Infrastructure/PaymentsJobs.cs:70) | job | - | no | |
| Unknown → Succeeded | PaymentsResolveUnknown, POST /payments/payments/{id}/resolve with a matched statement line; otherwise approval then event (Payments.cs:451, :1157) | PAYMENTS_RESOLVE_UNKNOWN | FINANCE_MANAGER, FINANCE_OFFICER | yes: approval type UNKNOWN_PAYMENT_RESOLUTION without a matched line (approver FINANCE_MANAGER) | |
| Unknown → Failed | PaymentsResolveUnknown (Payment.cs:369) | PAYMENTS_RESOLVE_UNKNOWN | FINANCE_MANAGER, FINANCE_OFFICER | no | |
| Succeeded → Reversed | PaymentsReverse, POST /payments/payments/{id}/reverse (Payments.cs:480) | PAYMENTS_REVERSE | FINANCE_MANAGER (named privileged grant) | no | |

### Payments · PayoutBatch (`Payments/Insys.Payments.Domain/Payments/PayoutBatch.cs:31`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| BUILT | PaymentsListBatches GET /payments/payout-batches (status filter) | |
| APPROVED | PaymentsListBatches (status filter) | FOLLOW-UP: FINANCE_MANAGER must release; no task on approval (Payments.cs:1089-1110), found by listing status=APPROVED |
| RELEASED | PaymentsListBatches (status filter) | |
| SETTLED | PaymentsListBatches (status filter) | |
| REJECTED | PaymentsListBatches (status filter) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → Built | PaymentsBuildBatch, POST /payments/payout-batches | PAYMENTS_BUILD_BATCH | FINANCE_OFFICER | yes: raises PAYOUT_RELEASE | |
| Built → Approved | event ApprovalGranted (Payments.cs:1104) | event | approver: FINANCE_MANAGER (Approves PAYOUT_RELEASE) | yes: approval type PAYOUT_RELEASE (requester ≠ checker) | |
| Built → Rejected | event ApprovalRejected (Payments.cs:1131) | event | approver: FINANCE_MANAGER | yes: PAYOUT_RELEASE | SPEC-DIFF: Rejected not in domain-model.json (PayoutBatch.cs:62) |
| Approved → Released | PaymentsReleaseBatch, POST /payments/payout-batches/{id}/release (PaymentsModule.cs:219) | PAYMENTS_RELEASE_BATCH | FINANCE_MANAGER | yes: release after approval by another (PayoutBatch.cs:143) | FOLLOW-UP: see APPROVED |
| Released → Settled | event PaymentSucceeded / PaymentFailed, all payments final (Payments.cs:1171, :1220) | event | - | no | |

### Payments · ReconciliationException (`Payments/Insys.Payments.Domain/Payments/PayoutBatch.cs:221`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| OPEN | PaymentsListExceptions GET /payments/reconciliation-exceptions (status filter) | NO-FILTER (paging): limit with no cursor, so beyond one page they cannot be reached (PaymentsModule.cs:236) |
| INVESTIGATING | PaymentsListExceptions (status filter) | |
| RESOLVED | PaymentsListExceptions (status filter) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → Open | job PaymentJobs (Unknown) or PaymentsImportStatement unmatched line (Payments.cs:626) | job / PAYMENTS_IMPORT_STATEMENT | FINANCE_MANAGER, FINANCE_OFFICER | no | |
| Open → Investigating | PaymentsAssignException, POST /payments/reconciliation-exceptions/{id}/assign | PAYMENTS_ASSIGN_EXCEPTION | FINANCE_MANAGER, FINANCE_OFFICER | no | |
| Investigating → Resolved | PaymentsResolveException, POST /payments/reconciliation-exceptions/{id}/resolve | PAYMENTS_RESOLVE_EXCEPTION | FINANCE_MANAGER, FINANCE_OFFICER | no | |

### Payments · BankStatementLine (`Payments/Insys.Payments.Domain/Payments/PayoutBatch.cs:338`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| UNMATCHED | none (transient within the import) | |
| MATCHED | none | |
| EXCEPTION | none; surfaced as a reconciliation exception (PaymentsListExceptions) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| Unmatched → Matched | PaymentsImportStatement, POST /payments/bank-statements (Payments.cs:621) | PAYMENTS_IMPORT_STATEMENT | FINANCE_MANAGER, FINANCE_OFFICER | no | |
| Unmatched → Exception | PaymentsImportStatement (Payments.cs:626) | PAYMENTS_IMPORT_STATEMENT | FINANCE_MANAGER, FINANCE_OFFICER | no | |
Note: resolving its exception does not change the line's match_status, and there is no read of statement lines at all (ops.tsv has only the import).


### Billing · BillingAccount (`Billing/Insys.Billing.Domain/Billing/BillingAccount.cs:32`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| ACTIVE | BillingListAccounts GET /billing/accounts (status filter) | |
| IN_ARREARS | BillingListAccounts (status filter) | |
| CLOSED | BillingListAccounts (status filter) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| Active → In arrears | BillingAgeAccount, POST /billing/accounts/{id}/age; job BillingJobs (Billing/Insys.Billing.Infrastructure/BillingJobs.cs:66); event InvoiceOverdue (ArrearsOnOverdue.cs:41) | BILLING_AGE_ACCOUNT | FINANCE_MANAGER, FINANCE_OFFICER | no | |
| In arrears → Active | as above, nothing overdue (BillingAccount.cs:497) | BILLING_AGE_ACCOUNT | FINANCE_MANAGER, FINANCE_OFFICER | no | |
| Active → Closed | BillingCloseAccount, POST /billing/accounts/{id}/close | BILLING_CLOSE_ACCOUNT | FINANCE_MANAGER, FINANCE_OFFICER | no | |

### Billing · Instalment (`Billing/Insys.Billing.Domain/Billing/BillingAccount.cs:46`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| SCHEDULED | only under its parent: BillingGetAccount | |
| INVOICED | only under its parent: BillingGetAccount | |
| CANCELLED | only under its parent: BillingGetAccount | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| Scheduled → Invoiced | BillingIssueInvoices, POST /billing/accounts/{id}/invoices; job BillingJobs (BillingJobs.cs:61) | BILLING_ISSUE_INVOICES | FINANCE_MANAGER, FINANCE_OFFICER | no | |
| Scheduled → Cancelled | BillingAmendSchedule, PUT /billing/accounts/{id}/schedules/{scheduleId}; policy events ending the schedule (BillingAccount.cs:234, :269, :557) | BILLING_AMEND_SCHEDULE | FINANCE_MANAGER, FINANCE_OFFICER | no | |

### Billing · ArrearsCase (`Billing/Insys.Billing.Domain/Billing/BillingAccount.cs:280`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| OPEN | only under its parent: BillingGetAccount; accounts via BillingListAccounts status=IN_ARREARS | |
| CLEARED | only under its parent: BillingGetAccount | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → Open | BillingAgeAccount / job BillingJobs / event InvoiceOverdue (BillingAccount.cs:523) | BILLING_AGE_ACCOUNT | FINANCE_MANAGER, FINANCE_OFFICER | no | |
| Open → Cleared | same, nothing overdue (BillingAccount.cs:495) | BILLING_AGE_ACCOUNT | FINANCE_MANAGER, FINANCE_OFFICER | no | |

### Billing · Invoice (incl. credit notes) (`Billing/Insys.Billing.Domain/Billing/Invoice.cs:32`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| DRAFT | none (never persisted: set in New, Issued in the same call, Invoice.cs:211, :238, :349) | |
| ISSUED | only under its parent: BillingListInvoices GET /billing/accounts/{id}/invoices | |
| PART_PAID | only under its parent: BillingListInvoices | |
| PAID | only under its parent: BillingListInvoices | |
| OVERDUE | only under its parent: BillingListInvoices | NO-LIST: no tenant-wide invoice list (BillingModule.cs:126); overdue invoices found only through accounts IN_ARREARS |
| CREDITED | only under its parent: BillingListInvoices | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| Draft → Issued | BillingIssueInvoices, POST /billing/accounts/{id}/invoices; job BillingJobs; policy events | BILLING_ISSUE_INVOICES | FINANCE_MANAGER, FINANCE_OFFICER | no | |
| Issued → Part paid | event PaymentAllocated / PaymentsAllocate (Billing.cs:692; Invoice.cs:296) | event | - | no | |
| Part paid → Paid | event PaymentAllocated (Invoice.cs:296) | event | - | no | |
| Issued → Paid | event PaymentAllocated | event | - | no | |
| Issued → Overdue | BillingAgeAccount / job BillingJobs (Billing.cs:501; Invoice.cs:320) | BILLING_AGE_ACCOUNT | FINANCE_MANAGER, FINANCE_OFFICER | no | |
| Overdue → Paid | event PaymentAllocated | event | - | no | |
| Issued → Credited | BillingRaiseCreditNote, POST /billing/invoices/{id}/credit-notes (Billing.cs:473) | BILLING_RAISE_CREDIT_NOTE | FINANCE_MANAGER, FINANCE_OFFICER | no | NO-OP: a reason that requires approval is refused, the approval is not built (Billing.cs:465-469, OQ-058) |
| Paid/Part paid/Overdue → Issued/Part paid/Overdue | event PaymentReversed (Billing.cs:702; Invoice.cs:307) | event | - | no | SPEC-DIFF: allocation reversal transitions not in domain-model.json (Invoice.cs:312) |

### Billing · RefundRequest (`Billing/Insys.Billing.Domain/Billing/Invoice.cs:356`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| REQUESTED | BillingListRefunds GET /billing/refunds (status filter) | |
| APPROVED | BillingListRefunds (status filter) | STUCK: if the payout fails, nothing moves it: Billing handles PaymentSucceeded but not PaymentFailed (Billing.cs:680-685, :717) and there is no retry op |
| REJECTED | BillingListRefunds (status filter) | |
| PAID | BillingListRefunds (status filter) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → Requested | BillingRequestRefund, POST /billing/accounts/{id}/refunds | BILLING_REQUEST_REFUND | FINANCE_MANAGER, FINANCE_OFFICER | yes: raises REFUND | |
| Requested → Approved | event ApprovalGranted (Billing.cs:731) | event | approver: FINANCE_MANAGER (Approves REFUND) | yes: approval type REFUND (requester ≠ checker) | |
| Requested → Rejected | event ApprovalRejected (Billing.cs:750) | event | approver: FINANCE_MANAGER | yes: REFUND | |
| Approved → Paid | event PaymentSucceeded (Billing.cs:722) | event | - | no | STUCK: see APPROVED |

### Finance · AccountingPeriod (`Finance/Insys.Finance.Domain/Ledger/AccountingPeriod.cs:32`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| FUTURE | FinanceListPeriods GET /finance/periods (all statuses) | |
| OPEN | FinanceListPeriods (all statuses) | |
| SOFT_CLOSE | FinanceListPeriods (all statuses) | |
| CLOSED | FinanceListPeriods (all statuses) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| Future → Open | FinancePeriodOpen, POST /finance/periods/{periodCode}/open | FINANCE_PERIOD_OPEN | FINANCE_MANAGER, FINANCE_OFFICER | no | |
| Open → Soft close | FinancePeriodSoftClose, POST /finance/periods/{periodCode}/soft-close | FINANCE_PERIOD_SOFT_CLOSE | FINANCE_MANAGER, FINANCE_OFFICER | no | |
| Soft close → Open | FinancePeriodRequestReopen, POST /finance/periods/{periodCode}/reopen, then event ApprovalGranted (Finance/Insys.Finance.Application/Policies.cs:251) | FINANCE_PERIOD_REQUEST_REOPEN | FINANCE_MANAGER, FINANCE_OFFICER; approver FINANCE_MANAGER | yes: approval type PERIOD_REOPEN (requester ≠ checker, AccountingPeriod.cs:181) | |
| Soft close → Closed | FinancePeriodClose, POST /finance/periods/{periodCode}/close | FINANCE_PERIOD_CLOSE | FINANCE_MANAGER, FINANCE_OFFICER | no | |

### Finance · Journal (one state in the code; not counted) (`Finance/Insys.Finance.Domain/Ledger/Journal.cs:156`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| POSTED | FinanceListJournals GET /finance/journals (no status filter; only status) | SPEC-DIFF: spec has Posted and Reversed; code and db allow POSTED only (db/migrations/V0014__posted_financial_records.sql:40), reversal is a new journal (FIN-002, deliberate) |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| Posted → Reversed (spec) | FinanceReverseJournal, POST /finance/journals/{id}/reverse: new reversing journal, original unchanged (Journal.cs:219) | FINANCE_REVERSE_JOURNAL | FINANCE_MANAGER, FINANCE_OFFICER | no | SPEC-DIFF: as above |

### Finance · ReconciliationRun (`docs/specs/domain-model.json` finance › ReconciliationRun; db finance.reconciliation_run, V0001__core_schema.sql:2334)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| Running (spec) | none | SPEC-DIFF: no code references finance.reconciliation_run or reconciliation_break |
| Completed (spec) | none | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| Running → Completed | spec "Matching finished": none | - | - | no | NO-OP: aggregate not implemented (no Finance class or op) |

### Finance · GlExportBatch (`Finance/Insys.Finance.Domain/Ledger/GlExportBatch.cs:32`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| SENT | FinanceListGlExports GET /finance/gl-exports (periodCode filter only) | NO-FILTER: batches waiting for the GL's acknowledgement can't be filtered (FinanceModule.cs:210) |
| ACKNOWLEDGED | FinanceListGlExports (no status filter) | |
| REJECTED | FinanceListGlExports (no status filter) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → Sent | FinanceExportToGeneralLedger, POST /finance/gl-exports | FINANCE_EXPORT_TO_GENERAL_LEDGER | FINANCE_MANAGER, FINANCE_OFFICER | no | |
| Sent → Acknowledged | FinanceAcknowledgeGlExport, POST /finance/gl-exports/{id}/acknowledgement | FINANCE_ACKNOWLEDGE_GL_EXPORT | FINANCE_MANAGER, FINANCE_OFFICER | no | |
| Sent → Rejected | FinanceAcknowledgeGlExport (accepted=false) | FINANCE_ACKNOWLEDGE_GL_EXPORT | FINANCE_MANAGER, FINANCE_OFFICER | no | |
Note: not in domain-model.json.

### Finance · PostingException (`Finance/Insys.Finance.Domain/Ledger/PostingException.cs:50`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| OPEN | FinanceListPostingExceptions GET /finance/posting-exceptions (status filter) | |
| RESOLVED | FinanceListPostingExceptions (status filter) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → Open | event: a business event with no posting rule (Finance/Insys.Finance.Application/Policies.cs) | event | - | no | |
| Open → Resolved | FinanceRetryPosting, POST /finance/posting-exceptions/{id}/retry (Ledger.cs:406) | FINANCE_RETRY_POSTING | FINANCE_MANAGER, FINANCE_OFFICER | no | |
Note: not in domain-model.json.

### Finance · TaxReturn (`Finance/Insys.Finance.Domain/Ledger/TaxReturn.cs:64`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| PREPARED | FinanceListTaxReturns GET /finance/tax-returns (all statuses, no filter) | NO-FILTER: returns awaiting a decision can't be filtered (FinanceModule.cs:106) |
| APPROVED | FinanceListTaxReturns (no filter) | |
| REJECTED | FinanceListTaxReturns (no filter) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → Prepared | FinancePrepareTaxReturn, POST /finance/tax-returns | FINANCE_PREPARE_TAX_RETURN | FINANCE_MANAGER, FINANCE_OFFICER | no | |
| Prepared → Approved | FinanceApproveTaxReturn, POST /finance/tax-returns/{id}/approve | FINANCE_APPROVE_TAX_RETURN | FINANCE_MANAGER, FINANCE_OFFICER | yes: own rule (TaxReturn.cs:95) | |
| Prepared → Rejected | FinanceRejectTaxReturn, POST /finance/tax-returns/{id}/reject | FINANCE_REJECT_TAX_RETURN | FINANCE_MANAGER, FINANCE_OFFICER | yes: own rule (TaxReturn.cs:95) | |
Note: not in domain-model.json; the preparer is told of a decision by no task (they find it in the list).

## Identity, Tenancy, Party, Distribution, Reinsurance, Compliance, Documents, Communications, Product Factory, Rating, Rules, Analytics

Approval authority is noted from the `Approves` lists in `src/Modules/Identity/Insys.Identity.Api/RoleTemplates.cs:158-224` (templates.tsv); deciding in Workflow also needs WORKFLOW_APPROVE (`WorkflowApprove`, POST /api/v1/workflow/approvals/{id}/approve) and the permission the requester named (`Workflow.Application/Approvals.cs:239`).

### Identity · UserAccount (`src/Modules/Identity/Insys.Identity.Domain/Users/UserAccount.cs:32`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| PendingIdentity | ListUsers GET /api/v1/identity/users (all statuses, no status filter) | `NO-FILTER`: ListUsers takes cursor,limit only (Infrastructure/Persistence/IdentityStores.cs:60); `SPEC-DIFF`: state not in domain-model iam.UserAccount (UserAccount.cs:33) |
| Invited | ListUsers (all statuses, no filter) | |
| Active | ListUsers (all statuses, no filter) | |
| Locked | ListUsers (all statuses, no filter) | |
| Disabled | ListUsers (all statuses, no filter) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → PendingIdentity / Invited | ProvisionUser, POST /api/v1/identity/users; ProvisionServiceAccount (→ Active) | PROVISION_USER | PLATFORM_ADMIN,TENANT_ADMIN | no | |
| PendingIdentity → Invited | KeycloakProvisioning.Handle(UserIdentityRequested) (Infrastructure/Keycloak/KeycloakProvisioning.cs:84) | event | - | no | `SPEC-DIFF`: not in domain-model (UserAccount.cs:296) |
| PendingIdentity → Disabled | DisableUser, POST /api/v1/identity/users/{id}/disable | DISABLE_USER | PLATFORM_ADMIN,TENANT_ADMIN | no | `SPEC-DIFF`: not in domain-model (UserAccount.cs:297) |
| Invited → Active | first authenticated request, SQL in Infrastructure/Permissions.cs:235 | event | - | no | |
| Invited → Disabled | DisableUser | DISABLE_USER | PLATFORM_ADMIN,TENANT_ADMIN | no | `SPEC-DIFF`: not in domain-model (UserAccount.cs:300, register row 31) |
| Active → Locked | LockUser, POST /api/v1/identity/users/{id}/lock | LOCK_USER | PLATFORM_ADMIN,TENANT_ADMIN | no | |
| Locked → Active | UnlockUser, POST /api/v1/identity/users/{id}/unlock | UNLOCK_USER | PLATFORM_ADMIN,TENANT_ADMIN | no | |
| Active/Locked → Disabled | DisableUser | DISABLE_USER | PLATFORM_ADMIN,TENANT_ADMIN | no | |
A Keycloak refusal parks the UserIdentityRequested delivery (KeycloakProvisioning.cs:42-47); the user stays PendingIdentity until the parked delivery is replayed in Integration.

### Identity · RoleAssignment (`src/Modules/Identity/Insys.Identity.Domain/Users/UserAccount.cs:52`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| PendingApproval | inside each user of ListUsers (no filter); IdentityListScopedAssignments GET /api/v1/identity/role-assignments/scoped shows only scoped ones; the Workflow approvals inbox WorkflowListApprovals shows the PRIVILEGED_ROLE approval | `NO-FILTER`: no assignment list by status (IdentityStores.cs:60, :74) |
| Active | inside ListUsers (no filter) | |
| Rejected | inside ListUsers (no filter) | |
| Revoked | inside ListUsers (no filter) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → Active / PendingApproval (privileged role) | AssignRole, POST /api/v1/identity/users/{id}/assign-role | ASSIGN_ROLE (privileged, named) | PLATFORM_ADMIN,TENANT_ADMIN | privileged: yes, approval type PRIVILEGED_ROLE (requester ≠ checker) | |
| PendingApproval → Active | PrivilegedRoleApprovalPolicy on ApprovalGranted (Application/Users/Users.cs:461) | event | approver: TENANT_ADMIN (Approves PRIVILEGED_ROLE) | yes: PRIVILEGED_ROLE | |
| PendingApproval → Rejected | PrivilegedRoleApprovalPolicy on ApprovalRejected (Users.cs:468) | event | - | yes: PRIVILEGED_ROLE | |
| Active → Revoked | RevokeRole, POST /api/v1/identity/users/{id}/revoke-role | REVOKE_ROLE (privileged, named) | PLATFORM_ADMIN,TENANT_ADMIN | no | |
| Active → Revoked | access review completion (Application/Access/Access.cs:358) | event | - | no | |

### Identity · SubjectLink (`src/Modules/Identity/Insys.Identity.Domain/Users/UserAccount.cs:201`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| Pending | inside ListUsers (one link per user, no filter); EXTERNAL_SUBJECT_LINK approval in WorkflowListApprovals | `NO-FILTER`: no link list by status (IdentityStores.cs:86) |
| Active | inside ListUsers (no filter) | |
| Ended | inside ListUsers, only when it is the user's latest link (IdentityStores.cs:86) | |
| Rejected | inside ListUsers, latest link only | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → Pending | IdentityLinkSubject, POST /api/v1/identity/users/{id}/subject-link | IDENTITY_LINK_SUBJECT | PLATFORM_ADMIN,TENANT_ADMIN | yes: approval type EXTERNAL_SUBJECT_LINK (requester ≠ checker) | |
| Pending → Active | PrivilegedRoleApprovalPolicy on ApprovalGranted (Users.cs:482-487) | event | approver: TENANT_ADMIN | yes: EXTERNAL_SUBJECT_LINK | |
| Pending → Rejected | PrivilegedRoleApprovalPolicy on ApprovalRejected | event | - | yes: EXTERNAL_SUBJECT_LINK | |
| Active → Ended | IdentityEndSubjectLink, POST /api/v1/identity/users/{id}/subject-link/end | IDENTITY_END_SUBJECT_LINK | PLATFORM_ADMIN,TENANT_ADMIN | no | |
| Pending → Ended | IdentityEndSubjectLink (CurrentLink includes Pending, UserAccount.cs:341, :409) | IDENTITY_END_SUBJECT_LINK | PLATFORM_ADMIN,TENANT_ADMIN | no | `FOLLOW-UP`: no ApprovalWithdrawn is raised (UserAccount.cs:416), so the EXTERNAL_SUBJECT_LINK approval stays open in the inbox; a later grant finds no Pending link and parks (Users.cs:486, :495) |

### Identity · PrivilegedAccessGrant (`src/Modules/Identity/Insys.Identity.Domain/Access/PrivilegedAccessGrant.cs:33`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| Requested | ListPrivilegedAccess GET /api/v1/identity/privileged-access (status filter) | |
| Approved | ListPrivilegedAccess (status filter) | |
| Rejected | ListPrivilegedAccess (status filter) | |
| Expired | ListPrivilegedAccess (status filter) | |
| Revoked | ListPrivilegedAccess (status filter); never reached | `SPEC-DIFF`: in the enum and V0007, in no domain-model transition and no code path (PrivilegedAccessGrant.cs:40, OQ-041) |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → Requested | RequestAccess, POST /api/v1/identity/privileged-access | REQUEST_ACCESS | PLATFORM_ADMIN,TENANT_ADMIN | - | |
| Requested → Approved | ApproveAccess, POST /api/v1/identity/privileged-access/{id}/approve | APPROVE_ACCESS | PLATFORM_ADMIN,TENANT_ADMIN | yes: own rule (PrivilegedAccessGrant.cs:124) | |
| Requested → Rejected | RejectAccess, POST /api/v1/identity/privileged-access/{id}/reject | REJECT_ACCESS | PLATFORM_ADMIN,TENANT_ADMIN | yes: own rule (PrivilegedAccessGrant.cs:149) | |
| Approved → Expired | PrivilegedAccessExpiry job (Infrastructure/PrivilegedAccessExpiry.cs:63) | job | - | no | |

### Identity · AccessReview (`src/Modules/Identity/Insys.Identity.Domain/Access/AccessReview.cs:32`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| Open | ListAccessReviews GET /api/v1/identity/access-reviews (all statuses, no filter) | `NO-FILTER`: cursor,limit only (ops.tsv) |
| Completed | ListAccessReviews (no filter) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → Open | StartReview, POST /api/v1/identity/access-reviews | START_REVIEW | PLATFORM_ADMIN,TENANT_ADMIN | no | |
| Open → Completed | CompleteReview, POST /api/v1/identity/access-reviews/{id}/complete | COMPLETE_REVIEW | PLATFORM_ADMIN,TENANT_ADMIN | own rule: only the assigned reviewer (AccessReview.cs:157) | |

### Identity · ReviewItem (`src/Modules/Identity/Insys.Identity.Domain/Access/AccessReview.cs:51`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| Pending | only under its parent: GetAccessReview GET /api/v1/identity/access-reviews/{id} | |
| Confirmed | only under its parent: GetAccessReview | |
| Revoked | only under its parent: GetAccessReview | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| Pending → Confirmed | ConfirmAccess, POST /api/v1/identity/access-reviews/{id}/items/{itemId}/confirm | CONFIRM_ACCESS | PLATFORM_ADMIN,TENANT_ADMIN | no | |
| Pending → Revoked | RevokeAccess, POST …/items/{itemId}/revoke | REVOKE_ACCESS | PLATFORM_ADMIN,TENANT_ADMIN | no | |
| Confirmed ↔ Revoked (while Open) | ConfirmAccess / RevokeAccess (AccessReview.cs:181) | CONFIRM_ACCESS / REVOKE_ACCESS | PLATFORM_ADMIN,TENANT_ADMIN | no | |

### Identity · TenantAdministratorRequest (`src/Modules/Identity/Insys.Identity.Domain/Tenants/TenantAdministratorRequest.cs:32`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| PendingApproval | ListTenantAdministratorRequests GET /api/v1/identity/tenant-administrators (all statuses; filter tenantId only); TENANT_ADMINISTRATOR approval in WorkflowListApprovals | `NO-FILTER`: no status filter (Infrastructure/Persistence/TenantAdministratorStore.cs:47) |
| Approved | ListTenantAdministratorRequests (no status filter) | |
| Rejected | ListTenantAdministratorRequests (no status filter) | |
| NotApplied | ListTenantAdministratorRequests (no status filter) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → PendingApproval | ProvisionTenantAdministrator POST /api/v1/identity/tenant-administrators; RequestAdditionalAdministrator POST …/additional | PROVISION_TENANT_ADMINISTRATOR / REQUEST_ADDITIONAL_ADMINISTRATOR | PLATFORM_ADMIN | yes: approval type TENANT_ADMINISTRATOR (requester ≠ checker) | |
| PendingApproval → Approved | TenantAdministratorApprovalPolicy on ApprovalGranted (Application/Tenants/TenantAdministrators.cs:530) | event | approver: PLATFORM_ADMIN | yes: TENANT_ADMINISTRATOR | |
| PendingApproval → Rejected | TenantAdministratorApprovalPolicy on ApprovalRejected | event | - | yes: TENANT_ADMINISTRATOR | |
| PendingApproval → NotApplied | TenantAdministratorNotAppliedPolicy (TenantAdministrators.cs:586) | event | - | yes: TENANT_ADMINISTRATOR | |
The shown state (AwaitingIdentity, IdentityParked, IdentityFailed, Provisioned; TenantAdministrators.cs:116) is derived; IdentityFailed is retried with RetryAdministratorIdentity (RETRY_ADMINISTRATOR_IDENTITY: PLATFORM_ADMIN).

### Tenancy · Tenant (`src/Modules/Tenancy/Insys.Tenancy.Domain/Tenants/Tenant.cs:32`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| Onboarding | ListTenants GET /api/v1/tenancy/tenants (status filter) | |
| Active | ListTenants (status filter) | |
| Suspended | ListTenants (status filter) | |
| Terminated | ListTenants (status filter) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → Onboarding | RegisterTenant, POST /api/v1/tenancy/tenants | REGISTER_TENANT | PLATFORM_ADMIN | no | |
| Onboarding → Active | ActivateTenant, POST /api/v1/tenancy/tenants/{id}/activate (onboarding complete, Tenant.cs:375) | ACTIVATE_TENANT | PLATFORM_ADMIN | no | |
| Active → Suspended | SuspendTenant, POST …/{id}/suspend | SUSPEND_TENANT (privileged, named) | PLATFORM_ADMIN | no | |
| Suspended → Active | ReactivateTenant, POST …/{id}/reactivate | REACTIVATE_TENANT | PLATFORM_ADMIN | no | |
| Active → Terminated | TerminateTenant, POST …/{id}/terminate | TERMINATE_TENANT (privileged, named) | PLATFORM_ADMIN | no | |
| Suspended → Terminated | TerminateTenant | TERMINATE_TENANT (privileged, named) | PLATFORM_ADMIN | no | |

### Tenancy · OnboardingCase (`src/Modules/Tenancy/Insys.Tenancy.Domain/Tenants/Tenant.cs:106`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| IN_PROGRESS | ListTenants?status=ONBOARDING, then GetTenantReadiness GET /api/v1/tenancy/tenants/{id}/readiness per tenant | |
| COMPLETED | only under its parent: GetTenant / GetTenantReadiness | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| IN_PROGRESS → IN_PROGRESS (next step) | CompleteOnboardingStep, POST /api/v1/tenancy/tenants/{id}/complete-onboarding-step | COMPLETE_ONBOARDING_STEP | PLATFORM_ADMIN | no | |
| IN_PROGRESS (END_TO_END_TESTS run recorded → approved) | RecordSmokeRun POST …/smoke-run; ApproveSmokeRun POST …/smoke-run/approve | RECORD_SMOKE_RUN / APPROVE_SMOKE_RUN | PLATFORM_ADMIN | yes: own rule (Tenant.cs:148) | `FOLLOW-UP`: not a Workflow approval, so the second PLATFORM_ADMIN gets no inbox entry or task; seen only on the tenant's readiness (Tenant.cs:141) |
| IN_PROGRESS → COMPLETED | CompleteOnboardingStep on the last step (Tenant.cs:186) | COMPLETE_ONBOARDING_STEP | PLATFORM_ADMIN | no | `FOLLOW-UP`: ActivateTenant must follow, by PLATFORM_ADMIN, no prompt beyond readiness (Tenant.cs:375) |

### Party · Party (`src/Modules/Party/Insys.Party.Domain/Parties/Party.cs:38`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| Active | none as GET; PartySearchParties POST /api/v1/party/parties/search takes status | |
| Dormant | PartySearchParties (status) | |
| Deceased | PartySearchParties (status); retention review task from PartyComplianceTasks (Workflow Processes.cs:972) | |
| Merged | none: search excludes merged parties (Infrastructure/Persistence/PartySearchStore.cs:42) | |
| Anonymised | PartySearchParties (status) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → Active | PartyRegisterParty POST /api/v1/party/parties; PortalPartyRegister POST /api/v1/portal/party/parties | PARTY_REGISTER_PARTY / BROKER_PORTAL_PARTY_REGISTER | CLAIMS_HANDLER,CLAIMS_MANAGER,CUSTOMER_SERVICE_AGENT,GENERAL_UNDERWRITER,HEALTH_CLAIMS_ASSESSOR,HEALTH_MEMBERSHIP_ADMINISTRATOR,LIFE_UNDERWRITER / BROKER | no | |
| Active → Dormant | ReviewPartyDormancy job (Application/DataSubjectRequests.cs:299) | job | - | no | |
| Dormant → Active | ReviewPartyDormancy job (DataSubjectRequests.cs:310); RecordDeath passes through Active (Party.cs:991) | job | - | no | |
| Active/Dormant → Deceased | PartyRecordDeath POST /api/v1/party/parties/{id}/death | PARTY_RECORD_DEATH | CLAIMS_HANDLER,CLAIMS_MANAGER,CUSTOMER_SERVICE_AGENT,GENERAL_UNDERWRITER,HEALTH_CLAIMS_ASSESSOR,HEALTH_MEMBERSHIP_ADMINISTRATOR,LIFE_UNDERWRITER | no | |
| Active → Merged | PartyMergeParties POST /api/v1/party/merges | PARTY_MERGE_PARTIES (privileged, named) | DATA_PROTECTION_OFFICER | no | |
| Dormant → Merged | PartyMergeParties | PARTY_MERGE_PARTIES (privileged, named) | DATA_PROTECTION_OFFICER | no | |
| Active → Merged (portal registration linked) | PARTY_MATCH_RESOLUTION grant (Application/PortalRegistration.cs:162) | event | see match review | yes: PARTY_MATCH_RESOLUTION | |
| Deceased → Anonymised | PartyAnonymise POST /api/v1/party/parties/{id}/anonymise; PartyExecuteDataSubjectDeletion | PARTY_ANONYMISE (privileged, named) / PARTY_EXECUTE_DATA_SUBJECT_DELETION | DATA_PROTECTION_OFFICER / CLAIMS_HANDLER,CLAIMS_MANAGER,CUSTOMER_SERVICE_AGENT,GENERAL_UNDERWRITER,HEALTH_CLAIMS_ASSESSOR,HEALTH_MEMBERSHIP_ADMINISTRATOR,LIFE_UNDERWRITER | no | |
| Dormant → Anonymised | PartyAnonymise; PartyExecuteDataSubjectDeletion | as above | as above | no | |

### Party · Portal match review (`src/Modules/Party/Insys.Party.Domain/Parties/Party.cs:593`, ReviewStatus)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| PENDING_REVIEW | none in Party; Workflow task PARTY_MATCH_REVIEW in queue PARTY_REVIEW (Workflow Processes.cs:535) | |
| PROPOSED | none in Party; PARTY_MATCH_RESOLUTION approval in WorkflowListApprovals | |
| LINKED | none | |
| CONFIRMED_NEW | none | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → PENDING_REVIEW | PortalPartyRegister POST /api/v1/portal/party/parties, on a match (Party.cs:604) | BROKER_PORTAL_PARTY_REGISTER | BROKER | no | |
| PENDING_REVIEW → PROPOSED | PartyResolveMatch POST /api/v1/party/parties/{id}/match-resolution | PARTY_RESOLVE_MATCH | CLAIMS_HANDLER,CLAIMS_MANAGER,CUSTOMER_SERVICE_AGENT,GENERAL_UNDERWRITER,HEALTH_CLAIMS_ASSESSOR,HEALTH_MEMBERSHIP_ADMINISTRATOR,LIFE_UNDERWRITER | yes: approval type PARTY_MATCH_RESOLUTION (requester ≠ checker) | |
| PROPOSED → LINKED | ApprovalGranted (PortalRegistration.cs:151) | event | approver: no template | yes: PARTY_MATCH_RESOLUTION | `NO-ROLE`: PARTY_MATCH_RESOLUTION is in no template's Approves (RoleTemplates.cs:158-224); needs a tenant authority row |
| PROPOSED → CONFIRMED_NEW | ApprovalGranted (PortalRegistration.cs:151) | event | approver: no template | yes: PARTY_MATCH_RESOLUTION | (same as above) |
| PROPOSED → PENDING_REVIEW | ApprovalRejected/ending (Party.cs:666) | event | - | yes | |

### Party · BankAccount (`src/Modules/Party/Insys.Party.Domain/Parties/Party.cs:144`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| PendingVerification | only under its parent: PartyListBankAccounts GET /api/v1/party/parties/{id}/bank-accounts; BANK_ACCOUNT_VERIFICATION approval in WorkflowListApprovals | |
| Active | only under its parent: PartyListBankAccounts | |
| Superseded | only under its parent: PartyListBankAccounts | |
| Rejected | only under its parent: PartyListBankAccounts | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → PendingVerification | PartyAddBankAccount POST /api/v1/party/parties/{id}/bank-accounts | PARTY_ADD_BANK_ACCOUNT | CLAIMS_HANDLER,CLAIMS_MANAGER,CUSTOMER_SERVICE_AGENT,GENERAL_UNDERWRITER,HEALTH_CLAIMS_ASSESSOR,HEALTH_MEMBERSHIP_ADMINISTRATOR,LIFE_UNDERWRITER | yes: approval type BANK_ACCOUNT_VERIFICATION (requester ≠ checker) | |
| PendingVerification → Active | ApprovalGranted (Application/Parties.cs:268) | event | approver: FINANCE_MANAGER (Approves; holds PARTY_BANK_ACCOUNT_VERIFY) | yes: BANK_ACCOUNT_VERIFICATION + own rule (Party.cs:730) | |
| PendingVerification → Rejected | ApprovalRejected/ending (Parties.cs:268) | event | - | yes | |
| PendingVerification → Superseded | PartyAddBankAccount for the same currency (Party.cs:699-703; approval withdrawn) | PARTY_ADD_BANK_ACCOUNT | as above | no | |
| Active → Superseded | a newer account verified (Party.cs:743) | event | - | yes | |

### Party · DueDiligence (`src/Modules/Party/Insys.Party.Domain/Parties/DueDiligence.cs:32`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| NOT_STARTED | PartyListDueDiligence GET /api/v1/party/due-diligence (status filter) | |
| IN_PROGRESS | PartyListDueDiligence (status filter) | |
| CLEARED | PartyListDueDiligence (status filter) | |
| REFERRED | PartyListDueDiligence (status, openHits filters) | |
| BLOCKED | PartyListDueDiligence (status filter) | |
| EXPIRED | PartyListDueDiligence (status, reviewDueBy filters) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| NOT_STARTED → IN_PROGRESS / CLEARED | PartyRunDueDiligenceCheck POST /api/v1/party/parties/{id}/due-diligence/checks, then Assess (DueDiligence.cs:414) | PARTY_RUN_DUE_DILIGENCE_CHECK | COMPLIANCE_OFFICER,GENERAL_UNDERWRITER,HEALTH_MEMBERSHIP_ADMINISTRATOR,LIFE_UNDERWRITER | no | |
| any → REFERRED (open screening hit) | check or provider result, Assess (DueDiligence.cs:421) | PARTY_RUN_DUE_DILIGENCE_CHECK / event | as above | no | |
| REFERRED → re-assessed (CLEARED, IN_PROGRESS, BLOCKED) | PartyReviewScreeningHit POST …/due-diligence/hits/{hitId}/review | PARTY_REVIEW_SCREENING_HIT | COMPLIANCE_OFFICER | no | |
| any → BLOCKED (true sanctions match) | PartyReviewScreeningHit (DueDiligence.cs:368-370) | PARTY_REVIEW_SCREENING_HIT | COMPLIANCE_OFFICER | no | |
| BLOCKED → re-assessed | PartyRequestDueDiligenceUnblock POST …/due-diligence/unblock, then DUE_DILIGENCE_UNBLOCK grant (Application/DueDiligence.cs:802) | PARTY_UNBLOCK_DUE_DILIGENCE (privileged, named) | COMPLIANCE_OFFICER | yes: approval type DUE_DILIGENCE_UNBLOCK (requester ≠ checker), approver COMPLIANCE_OFFICER | |
| IN_PROGRESS (ENHANCED_APPROVAL outstanding) → CLEARED | DUE_DILIGENCE_ENHANCED grant (Application/DueDiligence.cs:800), requested by Assess (DueDiligence.cs:439) | PARTY_APPROVE_ENHANCED_DD (privileged) | - | yes: approval type DUE_DILIGENCE_ENHANCED + own rule (DueDiligence.cs:558) | `NO-ROLE`: PARTY_APPROVE_ENHANCED_DD is in no template's PrivilegedGrants and DUE_DILIGENCE_ENHANCED in no template's Approves (RoleTemplates.cs:158-224); it is the only way out for an ENHANCED party |
| CLEARED → EXPIRED | RunDueDiligenceJobsHandler (Application/DueDiligence.cs:833) | job | - | no | |
| EXPIRED → CLEARED | PartyCompleteDueDiligenceReview POST …/due-diligence/review, then fresh screening (DueDiligence.cs:526) | PARTY_COMPLETE_DUE_DILIGENCE_REVIEW | COMPLIANCE_OFFICER | no | |

### Party · DataSubjectRequest (`src/Modules/Party/Insys.Party.Domain/Parties/DataSubjectRequest.cs:38`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| RECEIVED | PartyListDataSubjectRequests GET /api/v1/party/data-subject-requests (all statuses; filter partyId only); Workflow task from PartyComplianceTasks (Processes.cs:978) | `NO-FILTER`: no status filter, for every state (Party.Api/PartyModule.cs:394) |
| IDENTITY_CONFIRMED | PartyListDataSubjectRequests (no status filter) | |
| APPROVED | PartyListDataSubjectRequests (no status filter) | |
| COMPLETED | PartyListDataSubjectRequests (no status filter) | |
| REFUSED | PartyListDataSubjectRequests (no status filter) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → RECEIVED | PartyReceiveDataSubjectRequest POST /api/v1/party/data-subject-requests; PortalPartyRaiseDataSubjectRequest | PARTY_RECEIVE_DATA_SUBJECT_REQUEST / CUSTOMER_PORTAL_RAISE_DATA_SUBJECT_REQUEST | CLAIMS_HANDLER,CLAIMS_MANAGER,CUSTOMER_SERVICE_AGENT,GENERAL_UNDERWRITER,HEALTH_CLAIMS_ASSESSOR,HEALTH_MEMBERSHIP_ADMINISTRATOR,LIFE_UNDERWRITER / MEMBER | no | |
| RECEIVED → IDENTITY_CONFIRMED | PartyConfirmDataSubjectIdentity POST …/{id}/confirm-identity | PARTY_CONFIRM_DATA_SUBJECT_IDENTITY | same 7 staff templates | no | |
| IDENTITY_CONFIRMED → APPROVED | PartyApproveDataSubjectRequest POST …/{id}/approve | PARTY_APPROVE_DATA_SUBJECT_REQUEST | same 7 staff templates | yes: own rule, approver ≠ identity confirmer (DataSubjectRequest.cs:115) | |
| APPROVED → COMPLETED | PartyExportDataSubjectRequest GET …/{id}/export; PartyExecuteDataSubjectDeletion POST …/{id}/execute-deletion; PartyCompleteDataSubjectCorrection POST …/{id}/complete-correction | PARTY_EXPORT_DATA_SUBJECT_REQUEST / PARTY_EXECUTE_DATA_SUBJECT_DELETION / PARTY_COMPLETE_DATA_SUBJECT_CORRECTION | same 7 staff templates | no | `FOLLOW-UP`: the step after approval has no signal of its own; the list can't show APPROVED alone (PartyModule.cs:394); unverified whether the receipt task is still open by then |
| APPROVED → REFUSED | PartyExecuteDataSubjectDeletion when the party is active or records are kept (Application/DataSubjectRequests.cs:250-270) | PARTY_EXECUTE_DATA_SUBJECT_DELETION | same 7 staff templates | no | |
| open → REFUSED | PartyRefuseDataSubjectRequest POST …/{id}/refuse | PARTY_REFUSE_DATA_SUBJECT_REQUEST | same 7 staff templates | no | |
DATA_PROTECTION_OFFICER holds none of the PARTY_*DATA_SUBJECT* functions (fn_roles.tsv).

### Party · BulkJob (`src/Modules/Party/Insys.Party.Domain/Parties/BulkJob.cs:31`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| DONE | PartyListBulkJobs GET /api/v1/integration/bulk-jobs (status filter) | |
| REJECTED | PartyListBulkJobs (status filter) | |
| TO_APPROVE | PartyListBulkJobs (status filter) | |
| APPROVED | PartyListBulkJobs (status filter) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → DONE / REJECTED | PartyImportParties POST /api/v1/party/bulk-jobs/imports | PARTY_IMPORT_PARTIES | same 7 staff templates | no | |
| (new) → TO_APPROVE | PartyRequestExport POST …/exports; PartyRequestPolicyExport POST …/policy-exports | PARTY_REQUEST_EXPORT / PARTY_REQUEST_POLICY_EXPORT | same 7 staff templates | yes: own rule | |
| TO_APPROVE → APPROVED | PartyApproveExport POST /api/v1/party/bulk-jobs/{id}/approve | PARTY_APPROVE_EXPORT | same 7 staff templates | yes: own rule, approver ≠ requester (BulkJob.cs:108) | |
No refusal or cancellation exists for TO_APPROVE (BulkJob.cs:101): an export nobody approves stays TO_APPROVE.

### Party · SearchLogHold (`src/Modules/Party/Insys.Party.Domain/Parties/SearchLogHold.cs:35`, derived from ReleasedAt/ReleaseRequestedBy)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| Active | PartyListSearchLogHolds GET /api/v1/party/search-log/holds (all, no filter) | |
| ReleasePending | PartyListSearchLogHolds; LEGAL_HOLD_RELEASE approval in WorkflowListApprovals | |
| Released | PartyListSearchLogHolds | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → Active | PartyPlaceSearchLogHold POST /api/v1/party/search-log/holds | PARTY_PLACE_SEARCH_LOG_HOLD (privileged, named) | DATA_PROTECTION_OFFICER | no | |
| Active → ReleasePending | PartyReleaseSearchLogHold POST …/holds/{id}/release | PARTY_RELEASE_SEARCH_LOG_HOLD (privileged, named) | DATA_PROTECTION_OFFICER | yes: approval type LEGAL_HOLD_RELEASE (requester ≠ checker) | |
| ReleasePending → Released | ApprovalGranted (SearchLogHold.cs:100) | event | approver: COMPLIANCE_OFFICER | yes: LEGAL_HOLD_RELEASE | |
| ReleasePending → Active | approval ending (SearchLogHold.cs:111) | event | - | yes | |

### Distribution · Intermediary (`src/Modules/Distribution/Insys.Distribution.Domain/Intermediaries/Intermediary.cs:31`, register static data)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| Appointed (ACTIVE) | ListDistributionIntermediaryRegister GET /api/v1/distribution/static/intermediary-register (all, no status filter) | `NO-FILTER`: asAt,cursor,limit only (ops.tsv) |
| Suspended | ListDistributionIntermediaryRegister (no status filter) | |
| Terminated | ListDistributionIntermediaryRegister (no status filter) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → Appointed | CreateDistributionIntermediaryRegisterDraft POST …/intermediary-register, then the static data lifecycle (checked by Intermediary.CheckChange, Infrastructure/Persistence/DistributionStores.cs:305) | SD_COMMISSION_AND_DISTRIBUTION_MAINTAIN | DISTRIBUTION_ADMINISTRATOR,PLATFORM_STATIC_DATA_STEWARD | yes: static data approval (covered by the static-data agent) | |
| Appointed → Suspended | new register version, ChangeDistributionIntermediaryRegisterLifecycle POST …/versions/{versionNumber}/{action} | SD_COMMISSION_AND_DISTRIBUTION_MAINTAIN | DISTRIBUTION_ADMINISTRATOR,PLATFORM_STATIC_DATA_STEWARD | yes: static data approval | |
| Suspended → Appointed | new register version | SD_COMMISSION_AND_DISTRIBUTION_MAINTAIN | as above | yes: static data approval | |
| Appointed → Terminated | new register version | SD_COMMISSION_AND_DISTRIBUTION_MAINTAIN | as above | yes: static data approval | |
| Suspended → Terminated | new register version | SD_COMMISSION_AND_DISTRIBUTION_MAINTAIN | as above | yes: static data approval | |

### Distribution · CommissionEntitlement (`src/Modules/Distribution/Insys.Distribution.Domain/Commission/CommissionEntitlement.cs:31`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| Accrued | DistributionListEntitlements GET /api/v1/distribution/entitlements (status filter) | |
| Payable | DistributionListEntitlements (status filter) | |
| Paid | DistributionListEntitlements (status filter) | |
| ClawedBack | DistributionListEntitlements (status filter) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → Accrued | CommissionPolicies on InvoiceIssued (Application/Commission.cs:319) | event | - | no | |
| Accrued → Payable | CommissionPolicies on InvoiceSettled (Commission.cs:366) | event | - | no | |
| Payable → Paid | CommissionPolicies on PaymentSucceeded, via the statement (Commission.cs:437, CommissionStatement.cs:240) | event | - | no | |
| Accrued → ClawedBack | DistributionClawBack POST /api/v1/distribution/entitlements/{id}/claw-back; GeneralPolicyCancelled / LifePolicyLapsed handlers (Commission.cs:464) | DISTRIBUTION_CLAW_BACK / event | DISTRIBUTION_ADMINISTRATOR | no | |
| Payable → ClawedBack | DistributionClawBack; same handlers | DISTRIBUTION_CLAW_BACK / event | DISTRIBUTION_ADMINISTRATOR | no | |

### Distribution · CommissionStatement (`src/Modules/Distribution/Insys.Distribution.Domain/Commission/CommissionStatement.cs:32`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| Draft (incl. awaiting approval) | DistributionListStatements GET /api/v1/distribution/statements (status filter); COMMISSION_STATEMENT approval in WorkflowListApprovals | |
| Approved | DistributionListStatements (status filter) | `STUCK`: approved with net amount ≤ 0 raises no CommissionStatementApproved, so no payment and no PaymentSucceeded ever moves it to Paid (CommissionStatement.cs:186, :213, TODO(spec) OQ-062) |
| Paid | DistributionListStatements (status filter) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → Draft, submitted for approval | DistributionPrepareStatement POST /api/v1/distribution/statements | DISTRIBUTION_PREPARE_STATEMENT | DISTRIBUTION_ADMINISTRATOR | yes: approval type COMMISSION_STATEMENT (requester ≠ checker) | |
| Draft → Approved | CommissionPolicies on ApprovalGranted (Commission.cs:390) | event | approver: FINANCE_MANAGER | yes: COMMISSION_STATEMENT | |
| Draft (awaiting) → Draft | approval ending (Commission.cs:420) | event | - | yes | |
| Approved → Paid | CommissionPolicies on PaymentSucceeded (Commission.cs:437) | event | - | no | |
Distribution handles no PaymentFailed or PaymentReversed for a statement (Commission.cs:319-327); unverified whether Payments re-issues a failed commission payout.

### Reinsurance · ReinsuranceRecovery (`src/Modules/Reinsurance/Insys.Reinsurance.Domain/Recoveries.cs:33`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| Identified | ReinsuranceListRecoveries GET /api/v1/reinsurance/recoveries (status filter) | |
| Billed | ReinsuranceListRecoveries (status filter) | |
| Settled | ReinsuranceListRecoveries (status filter) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → Identified | claim-settled event handler (Recoveries.cs:104-108) | event | - | no | |
| Identified → Billed | ReinsuranceBillRecovery POST /api/v1/reinsurance/recoveries/{id}/bill | REINSURANCE_BILL_RECOVERY | REINSURANCE_OFFICER | no | |
| Billed → Settled | ReinsuranceSettleRecovery POST …/{id}/settle | REINSURANCE_SETTLE_RECOVERY | REINSURANCE_OFFICER | no | |

### Reinsurance · Bordereau (`src/Modules/Reinsurance/Insys.Reinsurance.Domain/Recoveries.cs:179`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| Prepared | ReinsuranceListBordereaux GET /api/v1/reinsurance/bordereaux (status filter) | |
| Sent | ReinsuranceListBordereaux (status filter) | |
| Agreed | ReinsuranceListBordereaux (status filter) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → Prepared | ReinsurancePrepareBordereau POST /api/v1/reinsurance/bordereaux | REINSURANCE_PREPARE_BORDEREAU | REINSURANCE_OFFICER | no | |
| Prepared → Sent | ReinsuranceSendBordereau POST …/{id}/send | REINSURANCE_SEND_BORDEREAU | REINSURANCE_OFFICER | no | |
| Sent → Agreed | ReinsuranceAgreeBordereau POST …/{id}/agree | REINSURANCE_AGREE_BORDEREAU | REINSURANCE_OFFICER | no | |

### Compliance · RegulatoryReturn (`src/Modules/Compliance/Insys.Compliance.Domain/RegulatoryReturn.cs:32`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| Prepared | ComplianceListReturns GET /api/v1/compliance/returns (status filter) | |
| Reviewed | ComplianceListReturns (status filter) | |
| Approved | ComplianceListReturns (status filter) | |
| Submitted | ComplianceListReturns (status filter) | |
| Accepted | ComplianceListReturns (status filter) | |
| Rejected | ComplianceListReturns (status filter) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → Prepared | CompliancePrepareReturn POST /api/v1/compliance/returns | COMPLIANCE_PREPARE_RETURN | COMPLIANCE_OFFICER | - | |
| Prepared → Reviewed | ComplianceReviewReturn POST …/{id}/review | COMPLIANCE_REVIEW_RETURN | COMPLIANCE_OFFICER | yes: own rule, reviewer ≠ preparer (RegulatoryReturn.cs:154) | |
| Reviewed → Approved | ComplianceApproveReturn POST …/{id}/approve | COMPLIANCE_APPROVE_RETURN | COMPLIANCE_OFFICER | yes: own rule, ≠ preparer and reviewer (RegulatoryReturn.cs:171) | |
| Approved → Submitted | ComplianceSubmitReturn POST …/{id}/submit | COMPLIANCE_SUBMIT_RETURN | COMPLIANCE_OFFICER | no | |
| Submitted → Accepted | ComplianceAcceptReturn POST …/{id}/accept | COMPLIANCE_ACCEPT_RETURN | COMPLIANCE_OFFICER | no | |
| Submitted → Rejected | ComplianceRejectReturn POST …/{id}/reject | COMPLIANCE_REJECT_RETURN | COMPLIANCE_OFFICER | no | |
| Rejected → Prepared | ComplianceCorrectReturn POST …/{id}/correct | COMPLIANCE_CORRECT_RETURN | COMPLIANCE_OFFICER | no | |
Risk limits: no aggregate, status or operation found in src/Modules/Compliance (no RiskLimit or risk_limit in any .cs file). ReportRun has the single status COMPLETED (RegulatoryReturn.cs:269).

### Documents · Document (`src/Modules/Documents/Insys.Documents.Domain/Documents/Document.cs:32`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| Generated | only under its parent: DocumentsList GET /api/v1/documents/documents (businessRefType and businessRefId required) | `NO-LIST`: nothing lists documents whose retention ended, for DocumentsDispose (Documents.Api/DocumentsModule.cs:115) |
| Delivered | only under its parent: DocumentsList | (same as Generated) |
| Disposed | only under its parent: DocumentsList | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → Generated | DocumentsGenerate POST …/documents/generate; DocumentsStore POST /api/v1/documents/documents; event handlers (e.g. Application/GeneralDocuments.cs:49) | DOCUMENTS_GENERATE / DOCUMENTS_STORE / event | CLAIMS_HANDLER,CLAIMS_MANAGER,CUSTOMER_SERVICE_AGENT,GENERAL_UNDERWRITER,HEALTH_CLAIMS_ASSESSOR,HEALTH_MEMBERSHIP_ADMINISTRATOR,LIFE_UNDERWRITER | no | `SPEC-DIFF`: domain-model has Requested → Generated; the code has no Requested state (GenerationRequest only GENERATED, Document.cs:244) |
| Generated → Delivered | DocumentsDeliver POST …/documents/{id}/deliveries | DOCUMENTS_DELIVER | same 7 staff templates | no | |
| Delivered → Delivered | DocumentsDeliver (Document.cs:270) | DOCUMENTS_DELIVER | same 7 staff templates | no | |
| Generated → Disposed | DocumentsDispose POST …/documents/{id}/dispose (manual) | DOCUMENTS_DISPOSE (privileged, named) | DATA_PROTECTION_OFFICER | no | `SPEC-DIFF`: domain-model trigger "Retention ended, no hold" is automatic; the code has only the manual command and no job (grep DisposeDocument: Documents.cs:566 only) |
| Delivered → Disposed | DocumentsDispose | DOCUMENTS_DISPOSE (privileged, named) | DATA_PROTECTION_OFFICER | no | |

### Documents · Document scan (`src/Modules/Documents/Insys.Documents.Domain/Documents/Document.cs:258`, ScanStatus)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| NOT_REQUIRED | only under its parent: DocumentsList | |
| PENDING | only under its parent: DocumentsList | `NO-LIST`: uploads in quarantine (scanner down, retries in ScanFailed) can't be found tenant-wide (Document.cs:358, DocumentsModule.cs:115) |
| CLEAN | only under its parent: DocumentsList | |
| INFECTED | only under its parent: DocumentsList | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → NOT_REQUIRED | DocumentsGenerate / generated by event | DOCUMENTS_GENERATE / event | same 7 staff templates | no | |
| (new) → PENDING | DocumentsStore (upload) | DOCUMENTS_STORE | same 7 staff templates | no | |
| PENDING → CLEAN | DocumentScanJob (Infrastructure/Scanning/DocumentScanJob.cs:43) | job | - | no | |
| PENDING → INFECTED | DocumentScanJob | job | - | no | |

### Documents · Delivery (`src/Modules/Documents/Insys.Documents.Domain/Documents/Document.cs:76`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| SENT | only under its parent: DocumentsGet GET /api/v1/documents/documents/{id} | |
| FAILED | only under its parent: DocumentsGet | `NO-LIST`: no list of failed deliveries to resend (Document.cs:105) |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → SENT / FAILED | DocumentsDeliver POST …/documents/{id}/deliveries | DOCUMENTS_DELIVER | same 7 staff templates | no | |

### Documents · LegalHold on a document (`src/Modules/Documents/Insys.Documents.Domain/Documents/Document.cs:112`, derived)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| Active | only under its parent: DocumentsGet; DocumentsListRecordHolds reads record_hold only, not legal_hold | `NO-LIST`: no list of document holds in force (Infrastructure/Persistence/DocumentsDbContext.cs:83 vs :95) |
| ReleasePending | only under its parent; LEGAL_HOLD_RELEASE approval in WorkflowListApprovals | |
| Released | only under its parent | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → Active | DocumentsPlaceLegalHold POST …/documents/{id}/legal-holds | DOCUMENTS_PLACE_LEGAL_HOLD (privileged, named) | DATA_PROTECTION_OFFICER | no | |
| Active → ReleasePending | DocumentsReleaseLegalHold POST …/legal-holds/{holdId}/release | DOCUMENTS_RELEASE_LEGAL_HOLD (privileged, named) | DATA_PROTECTION_OFFICER | yes: approval type LEGAL_HOLD_RELEASE (requester ≠ checker) | |
| ReleasePending → Released | ApprovalGranted (Document.cs:489) | event | approver: COMPLIANCE_OFFICER | yes: LEGAL_HOLD_RELEASE | |
| ReleasePending → Active | approval ending (Document.cs:489) | event | - | yes | |

### Documents · RecordHold (`src/Modules/Documents/Insys.Documents.Domain/Documents/RecordHold.cs:56`, derived)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| Active | DocumentsListRecordHolds GET /api/v1/documents/legal-holds (inForce, scopeType filters) | |
| ReleasePending | DocumentsListRecordHolds (inForce=true; no filter for pending); LEGAL_HOLD_RELEASE approval in WorkflowListApprovals | |
| Released | DocumentsListRecordHolds (inForce=false) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → Active | DocumentsPlaceRecordHold POST /api/v1/documents/legal-holds | DOCUMENTS_PLACE_LEGAL_HOLD (privileged, named) | DATA_PROTECTION_OFFICER | no | |
| Active → ReleasePending | DocumentsReleaseRecordHold POST …/legal-holds/{id}/release | DOCUMENTS_RELEASE_LEGAL_HOLD (privileged, named) | DATA_PROTECTION_OFFICER | yes: approval type LEGAL_HOLD_RELEASE (requester ≠ checker) | |
| ReleasePending → Released | ApprovalGranted (RecordHold.cs:139) | event | approver: COMPLIANCE_OFFICER | yes: LEGAL_HOLD_RELEASE | |
| ReleasePending → Active | approval ending (RecordHold.cs:139) | event | - | yes | |

### Communications · Notification (`src/Modules/Communications/Insys.Communications.Domain/Notification.cs:31`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| Queued | CommunicationsListNotifications GET /api/v1/communications/notifications (status filter) | |
| Sent | CommunicationsListNotifications (status filter) | unverified: nothing ends a Sent notification whose provider never calls back (no timeout found in Notification.cs or Sending.cs) |
| Delivered | CommunicationsListNotifications (status filter) | |
| Failed | CommunicationsListNotifications (status filter) | |
| Undeliverable | CommunicationsListNotifications (status filter) | |
| SuppressedNoConsent | CommunicationsListNotifications (status filter) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → Queued | trigger handlers (e.g. Application/Triggers/RenewalTriggers.cs:84) | event | - | no | |
| Queued → SuppressedNoConsent | NotificationSender (Application/Sending.cs:83; Queueing.cs:74) | job | - | no | |
| Queued → Sent | NotificationSenderService (Sending.cs:142) | job | - | no | |
| Sent → Delivered | CommunicationsSignedDeliveryCallback POST /api/v1/communications/callbacks/{endpointKey} (Requests.cs:467) | - (signed callback) | - | no | |
| Sent → Queued (bounce or failure, next channel) | CommunicationsSignedDeliveryCallback (Notification.cs:270) | - (signed callback) | - | no | |
| Queued → Failed | NotificationSender, every channel used up (Notification.cs:321) | job | - | no | |
| Queued → Undeliverable | NotificationSender, no address on every channel (Notification.cs:321) | job | - | no | |

### Communications · NotificationAttempt (`src/Modules/Communications/Insys.Communications.Domain/Notification.cs:41`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| Sent | only under its parent: CommunicationsGetNotification GET /api/v1/communications/notifications/{id} | |
| Delivered | only under its parent | |
| Failed | only under its parent | |
| Bounced | only under its parent | |
| Undeliverable | only under its parent | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → Sent | NotificationSender (Sending.cs:142) | job | - | no | |
| (new) → Failed | NotificationSender (Sending.cs:108, :147) | job | - | no | |
| (new) → Undeliverable | NotificationSender, no address (Sending.cs:114) | job | - | no | |
| Sent → Delivered | CommunicationsSignedDeliveryCallback | - (signed callback) | - | no | |
| Sent → Bounced | CommunicationsSignedDeliveryCallback | - (signed callback) | - | no | |
| Sent → Failed | CommunicationsSignedDeliveryCallback | - (signed callback) | - | no | |

### ProductFactory · ProductVersion (`src/Modules/ProductFactory/Insys.ProductFactory.Domain/Products/ProductVersion.cs:32`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| Draft | ProductListProducts GET /api/v1/product-factory/products (status filter on each product's latest version only) | `NO-FILTER`: an older version (e.g. Effective under a newer Draft) can't be found by status (Application/Products.cs:167) |
| Validated | ProductListProducts (latest version only) | |
| Submitted | ProductListProducts (latest version only); PRODUCT_PUBLICATION approval in WorkflowListApprovals | |
| Approved | ProductListProducts (latest version only); transient | |
| Published | ProductListProducts (latest version only) | |
| Effective | ProductListProducts (latest version only) | |
| Withdrawn | ProductListProducts (latest version only) | |
| Retired | ProductListProducts (latest version only) | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → Draft | ProductCreateDraftVersion POST /api/v1/product-factory/products/{productCode}/versions | PRODUCT_CREATE_DRAFT_VERSION | PRODUCT_MANAGER | no | |
| Draft → Validated | ProductValidateVersion POST …/versions/{versionNumber}/validate | PRODUCT_VALIDATE_VERSION | PRODUCT_MANAGER | no | |
| Validated → Draft | ProductEditConfiguration PUT …/versions/{versionNumber}; static data change (Products.cs:614) | PRODUCT_EDIT_CONFIGURATION / event | PRODUCT_MANAGER | no | |
| Validated → Submitted | ProductSubmitForApproval POST …/versions/{versionNumber}/submit | PRODUCT_SUBMIT_FOR_APPROVAL | PRODUCT_MANAGER | yes: approval type PRODUCT_PUBLICATION (requester ≠ checker) | |
| Submitted → Draft | ProductPolicies on ApprovalRejected (Products.cs:594) | event | - | yes | |
| Submitted → Approved | WorkflowApprove, then ProductPolicies on ApprovalGranted (Products.cs:572) | WORKFLOW_APPROVE | CLAIMS_MANAGER,COMPLIANCE_OFFICER,FINANCE_MANAGER,LINE_CONFIGURATION_MANAGER,PLATFORM_ADMIN,PLATFORM_STATIC_DATA_STEWARD,TENANT_ADMIN | yes: PRODUCT_PUBLICATION | `NO-ROLE`: only PRODUCT_MANAGER has PRODUCT_PUBLICATION in Approves (RoleTemplates.cs:161), and it holds neither WORKFLOW_APPROVE nor WORKFLOW_APPROVALS (templates.tsv); no template both sees and can decide it |
| Approved → Published | ProductPolicies, same handler (Products.cs:582) | event | - | no | |
| Published → Effective | ProductJobs, MakeVersionEffective (Infrastructure/ProductJobs.cs:66) | job | - | no | |
| Effective → Withdrawn | ProductWithdrawVersion POST …/versions/{versionNumber}/withdraw | PRODUCT_WITHDRAW_VERSION | PRODUCT_MANAGER | no | |
| Published → Withdrawn | ProductWithdrawVersion | PRODUCT_WITHDRAW_VERSION | PRODUCT_MANAGER | no | |
| Effective → Retired | ProductRetireVersion POST …/versions/{versionNumber}/retire | PRODUCT_RETIRE_VERSION | PRODUCT_MANAGER | no | |

### ProductFactory · SimulationRun (`src/Modules/ProductFactory/Insys.ProductFactory.Domain/Products/SimulationRun.cs:31`)
States:
| State | Tenant-wide list showing records in this state | Gap |
| --- | --- | --- |
| Passed | only under its parent: ProductListSimulations GET …/versions/{versionNumber}/simulations | |
| Failed | only under its parent: ProductListSimulations | |
| NoCases | only under its parent: ProductListSimulations | |
| Stale | only under its parent: ProductListSimulations | |

Transitions:
| From → To | Operation (operationId, METHOD path) or trigger | Function | Roles holding it | Maker-checker | Gap |
| --- | --- | --- | --- | --- | --- |
| (new) → Passed / Failed / NoCases | ProductRunSimulation POST …/versions/{versionNumber}/simulations | PRODUCT_RUN_SIMULATION | PRODUCT_MANAGER | no | |
| Passed / Failed / NoCases → Stale | ProductEditConfiguration; ApprovalRejected; static data change (Products.cs:293, :599, :614) | PRODUCT_EDIT_CONFIGURATION / event | PRODUCT_MANAGER | no | |

### Rating, Rules, Analytics
No aggregate or persisted child with a status of more than one value: Rating has none (rating.rating_request has no status column, V0001:835); Rules' decision_log.outcome is a decision result, not a lifecycle (V0001:867); Analytics has facts, event_log and release_task (completed_at only, V0055__release_tasks.sql:33).
