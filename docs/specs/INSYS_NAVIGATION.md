# INSYS Core: navigation taxonomy for the static UI prototype

This is the source for the prototype's sidebar and for each page's process areas. It is taken from what the INSYS back end provides on `main` (`0f66f05`, 24 October 2026), plus the Wave 0b batch 6 items marked *(batch 6)*. Status and wave come from the back end's build plan.

## How it maps to the UI

The UI spec allows only a flat sidebar: labelled groups of single-level items, with no accordions and no nested trees. So:

| Taxonomy level | UI element |
|---|---|
| **Business domain** | Sidebar group header (uppercase micro-label) |
| **Sub-domain** | Sidebar item (lucide icon + label), with one route per sub-domain |
| **Process area** | Shown on the sub-domain's page. Status tabs (or a segmented control) switch between its process areas, and each one opens a Record Listing View (template A) of its main record. Rows open a Record Detail View (template B). |

**Status values, shown as pills on the process-area tab:**
- **Built:** live in the back end.
- **Partial:** some of the process exists.
- **Config only:** the static data exists, but no process uses it.
- **Planned:** with its wave number.

A Partial process area shows a dashed `demo-note`: "Partially available in INSYS today". A Planned or Config only one shows the dashed empty state: "Planned: Wave N".

## Global (top of sidebar, no group header)
- **Home** (`layout-dashboard`): the dashboard
- **Inbox** (`inbox`): approvals and tasks (maker-checker)
- **Search** (`search`): global search across mock records

---

## LIFE INSURANCE
| Sub-domain (sidebar item) | Icon | Process areas | Main record | Status |
|---|---|---|---|---|
| New Business | `file-plus` | Proposal capture · Rating · Eligibility · Submission & withdrawal | LifeProposal | Built |
| Underwriting | `stethoscope` | Underwriting cases · Evidence · Assessment · Referral · Decision (authority / second person) | LifeUnderwritingCase | Built |
| Policy Administration | `shield-check` | Issue & in force · Beneficiaries · Alterations · Reinstatement · Policy documents · Grace & lapse | LifePolicy | Built |
| Policy Values | `piggy-bank` | Surrender · Paid-up · Maturity · Policy loans | MaturityCase, LifePolicyLoan | Planned (blocked by OQ-052) |
| Life Claims | `heart-pulse` | Notification · Registration & automatic reserve · Assessment · Approval (second person) · Decline & close · Payment | LifeClaim | Built |
| Group Schemes | `users` | Scheme setup · Member enrolment by category · Free cover limits · Renewal · Termination | GroupScheme | Built |

## GENERAL INSURANCE
| Sub-domain | Icon | Process areas | Main record | Status |
|---|---|---|---|---|
| Risk Objects | `car` | Register · Revalue · Deactivate | GeneralRiskObject | Built |
| Quotation | `calculator` | Quote & rate · Referral & decision · Aggregation-limit referral · Issue, accept, reject | GeneralQuote | Built |
| Policy Administration | `shield` | Proposal · Policy issue · Endorsements (rated, approved) · Cancellation · Reinstatement | GeneralPolicy, GeneralEndorsement | Built |
| Renewals | `refresh-cw` | Policies due · Renewal offer · Renewed policy issue · Renewal billing: **Built**. Eligibility · Risk & claims review · Renewal underwriting · Acceptance · Retention · Reporting: **Planned (Wave 4)** | GeneralPolicy | Partial |
| General Claims | `clipboard-list` | Notification · Registration · Assessor appointment · Loss assessment & excess · Reserves · Settlement approval · Aggregate limit: **Built**. Reopen · Partial approval: **Partial** | GeneralClaim, LossAssessment | Built |
| Recoveries | `undo-2` | Subrogation · Third-party recovery · Salvage | RecoveryCase | Built |

## HEALTH INSURANCE
| Sub-domain | Icon | Process areas | Main record | Status |
|---|---|---|---|---|
| Schemes & Plans | `layers` | Schemes · Plan options linked to products | HealthScheme | Built |
| Membership | `id-card` | Enrolment · Dependants · Plan changes · Suspension & termination · Benefit totals | HealthMembership, BenefitAccumulator | Built |
| Access to Care | `badge-check` | Eligibility checks · Preauthorisations | EligibilityDecision, Preauthorisation | Built |
| Medical Claims | `file-heart` | Validation · Line adjudication · Review · Approve / reject / reverse | MedicalClaim | Built |
| Fraud & Utilisation | `scan-search` | Fraud cases · Utilisation cases: **Built**. Complex case management · Case manager assignment: **Built** | HealthFraudCase, UtilisationCase | Built |
| Provider Payments | `banknote` | Payment runs (second-person release) · Remittances · Provider reconciliation · Overpayment recovery · Credit notes: **Built** | ProviderPaymentRun | Built |
| Provider Management | `hospital` | Performance & quality monitoring: **Built**. Credit notes · Provider disputes: **Planned (Wave 5a)** | ProviderMeasureResult, ProviderQualityReview | Partial |
| Pharmacy & Medication | `pill` | Pharmacy claims · Prescription & quantity validation · Formulary · Medication limits · Duplicate detection | (none yet) | Planned (Wave 5b) |
| Care Programmes | `activity` | Chronic conditions · Disease management · Preventive & vaccination · Wellness · Treatment monitoring | (none yet) | Planned (Wave 5b) |

## PRODUCTS & PRICING
| Sub-domain | Icon | Process areas | Main record | Status |
|---|---|---|---|---|
| Product Factory | `package` | Definitions & versions · Publication (second person) · Availability per tenant | ProductDefinition, ProductVersion | Built |
| Rating | `sigma` | Rating tables · Premium calculation · Test cases & simulation | PremiumCalculation | Built |
| Rules | `git-branch` | Eligibility, underwriting and referral rule decisions | RuleDecision | Built |

## CUSTOMERS
| Sub-domain | Icon | Process areas | Main record | Status |
|---|---|---|---|---|
| Parties | `contact` | Register people & organisations · Identifiers (masked) · Search with access log · Merges · Bank accounts (second-person verification) · Portal registration review *(batch 6)* | Party | Built |
| Consent & Preferences | `lock-keyhole` | Consent ledger (grant, withdraw, history): **Built**. Communication preferences: **Partial (Wave 1)** | ConsentLedger | Built |
| Due Diligence | `user-search` | Identity verification · KYC · Sanctions & PEP screening · Risk classification · Duplicate check | (none yet) | Planned (Wave 3) |

## DISTRIBUTION
| Sub-domain | Icon | Process areas | Main record | Status |
|---|---|---|---|---|
| Intermediaries | `briefcase` | Brokers · Licences · Hierarchy | Intermediary | Built |
| Commission | `percent` | Entitlements · Statements (second person) · Payment · Claw-back | CommissionStatement | Built |
| Channels | `waypoints` | Channel definitions · Channel-transaction permissions | (static data) | Built |

## CLAIMS FRAMEWORK
| Sub-domain | Icon | Process areas | Main record | Status |
|---|---|---|---|---|
| Claim Register | `list-ordered` | Numbering · Registration from all domains | ClaimRegistration | Built |
| Claim Financials | `scale` | Reserves (reasons, authority) · Payment instructions · Held releases | ClaimFinancials, ClaimPaymentInstruction | Built |
| Fraud Screening | `shield-alert` | Indicators at registration · Skipped indicators & review | ClaimRegistration | Built |
| Disputes & Appeals | `gavel` | Claim disputes · Claim appeals | (none yet) | Planned (Wave 2) |

## MONEY
| Sub-domain | Icon | Process areas | Main record | Status |
|---|---|---|---|---|
| Billing | `receipt` | Accounts · Premium schedules · Invoices & credit notes · Arrears ageing & cases · Refunds (second person): **Built**. Premium financing: **Planned (Wave 6)** | BillingAccount, Invoice, RefundRequest | Built |
| Payments | `credit-card` | Collections (debit order) · Allocation · Payout batches (release approval) · Provider callbacks · Bank statement reconciliation | Payment, PayoutBatch, ReconciliationException | Built |
| Finance | `landmark` | Chart of accounts · Posting rules · Journals · Periods & close · Posting exceptions · GL export: **Built**. Insurance tax · Accounts payable view: **Partial (Wave 6)** | Journal, AccountingPeriod | Built |
| Reinsurance | `network` | Cessions · Recoveries · Bordereaux: **Built**. Exposure monitoring: **Partial (Wave 6)** | Cession, ReinsuranceRecovery, Bordereau | Built |

## COMPLIANCE & RISK
| Sub-domain | Icon | Process areas | Main record | Status |
|---|---|---|---|---|
| Regulatory Reporting | `file-check` | Returns (multi-person) · Report runs · Export | RegulatoryReturn | Built |
| Risk Limits | `gauge` | Aggregation limits by zone & peril · Aggregate claims limits · Daily alerts | (exposure snapshot) | Built |
| Data Protection | `fingerprint` | Retention jobs · Legal holds: **Partial**. POPIA data-subject requests: **Planned (Wave 6)** | (none yet) | Partial |
| Disclosures & Fraud Investigation | `search-check` | Required disclosures · Fraud investigation (Life, General) | (none yet) | Partial (Wave 6) |
| Solvency | `bar-chart-3` | Solvency & capital reporting | (none yet) | Planned (Wave 6) |

## SERVICE & COMMUNICATIONS
| Sub-domain | Icon | Process areas | Main record | Status |
|---|---|---|---|---|
| Customer Service | `headset` | Policy information lookup: **Built**. Enquiries · Complaints · Disputes · Internal appeals · Service reporting: **Planned (Wave 2)** | (none yet) | Partial |
| Notifications | `bell-ring` | Email · SMS · Push · WhatsApp · Payment, renewal and claim reminders · Delivery tracking | (none yet) | Planned (Wave 1) |

## OPERATIONS
| Sub-domain | Icon | Process areas | Main record | Status |
|---|---|---|---|---|
| Documents | `folder-open` | Storage · Templates · Generation from events · Categories & access · Retention | Document | Built |
| Workflow | `workflow` | Approvals & authority limits · Processes · Tasks & queues · SLAs & calendars · Escalations | Approval, ProcessInstance | Built |
| Analytics | `line-chart` | Written premium · Claims incurred · Dashboard figures *(batch 6)* | (projections) | Partial |
| Integration Operations | `plug` | Dead letters · Parked deliveries (retry, discard with reason) · Webhook deliveries | DeadLetter, WebhookDelivery | Built |

## PLATFORM (super user / tenant administrator)
| Sub-domain | Icon | Process areas | Main record | Status |
|---|---|---|---|---|
| Tenants | `building-2` | Tenant lifecycle · Onboarding steps & readiness *(batch 6)* · First administrator · Time zone & business date | Tenant | Built |
| Identity & Access | `key-round` | Users · Roles · Privileged roles (approval) · MFA · Product & branch scopes · Privileged access · Access reviews · External user links | UserAccount, PrivilegedAccessGrant, AccessReview | Built |
| Static Data | `database` | 252+ reference & configuration tables · Three maintenance tiers · Maker-checker · Imports | StaticDataChange, ImportBatch | Built |
| Audit | `history` | Audit trail · Record history | (audit events) | Built |
| Localisation | `languages` | Languages · Preferred language per user and party | (static data) | Built |

---

## End-to-end processes (for the Home page "Process map" panel)
1. General quote to cash
2. Life proposal to policy
3. General claim to settlement
4. Health care to provider payment
5. Product publication
6. Missed premium to lapse

## Personas (persona switcher in the top bar)
Each persona sees only its groups. The super user sees everything.

| Persona | Badge | Sees |
|---|---|---|
| Super user (platform operator) | violet "Super user" | All groups |
| Tenant administrator | (none) | Platform (without Tenants), Operations, Compliance & Risk |
| Underwriter | (none) | Life, General, Health, Products & Pricing, Customers |
| Claims handler | (none) | Life Claims, General Claims, Medical Claims, Claims Framework, Recoveries |
| Finance officer | (none) | Money, Compliance & Risk |
| Broker (portal) | (none) | Broker portal: clients, quotes, policies, claims, commission statements |
| Provider (portal) | (none) | Provider portal: eligibility, preauthorisations, claims, remittances |
| Member (portal) | (none) | Member portal: my policies, my claims, my documents, notify a claim |
