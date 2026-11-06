# INSYS Preview: quick-start

**For OpenFactor's functional team.** The INSYS Preview is a test system with invented data. It isn't production, and nothing in it reaches a real person, provider or bank, except the sign-in emails below. A banner on every page says so, and the sign-in page is titled "INSYS Preview (sample data only)".

## Signing in
- **Address:** `https://insys.project-demo.app`. Sign-in goes through `https://id.insys.project-demo.app`.
- **Accounts:** Ade gives each person the accounts they need, with their passwords, privately. Don't share them in chat or email.
- **Broker and provider portal accounts** ask for a second factor. Ade gives you its setup code with the password; if he can't, the first person to sign in sets it up and shares it.
- **Sign-in names** carry the tenant's code: the tenant's accounts are `oftza.` and the account (`oftza.admin01`), the platform's `operator.` and the account (`operator.operator01`). The full list is under "Every account's sign-in name" below. They ignore case.
- **Emails:** Invitations and password resets are real emails from insys-preview@openfactorgroup.com; everything else is a stand-in.

## Who does what
Each account holds one role template, with nothing added.

| Accounts | Role | Use it for |
|---|---|---|
| ADMIN01/02 | Tenant administrator | Users and roles, static data approvals, **and approving a product's publication** (see below) |
| PRODUCT01/02 | Product manager | Products, rating tables and rules |
| UNDWRIT01/02 | General underwriter | Motor and household quotes (rating them too), policies, endorsements |
| LIFEUW01/02 | Life underwriter | Funeral and term proposals (rating them and running the rules too), underwriting, policies |
| CLAIMS01/02 | Claims handler | Registering, reserving and assessing General and Life claims |
| CLAIMSMGR01/02 | Claims manager | Approving settlements and Life claims |
| HEALTHCLM01/02 | Health claims assessor | Medical claims and preauthorisations |
| HEALTHMEM01/02 | Health membership administrator | The scheme, members and dependants |
| LINECFG01/02 | Line configuration manager | Life, General and Health configuration, such as a scheme's contribution rule: one changes it, the other approves |
| FIN01/02, PAY01 | Finance officer | Billing, payment runs, building payout batches |
| FINMGR01/02, PAY02 | Finance manager | Releasing payout batches, verifying bank accounts, approving refunds and runs |
| DIST01/02 | Distribution administrator | Brokers and commission |
| REINS01/02 | Reinsurance officer | The treaty, cessions and bordereaux |
| COMPL01/02 | Compliance officer | Due diligence reviews, complaints' appeals, reporting |
| CS01/02 | Customer service agent | Looking things up, complaints and enquiries |
| Others | As named | Data protection, operations, integration, audit, the approver role, the portals, and the platform |

Where a step needs a second person, use the other account of the pair (01 and 02), or the manager's.

## Every account's sign-in name
Type the sign-in name exactly as here (case doesn't matter). INSYS shows both accounts of a pair by the same name (the second column), so note which sign-in name you used. Ade's encrypted file lists the same `signIn` beside each password.

| Sign in as | Shown in INSYS as | Role |
|---|---|---|
| `oftza.admin01` | Sample tenant admin | `TENANT_ADMIN` |
| `oftza.admin02` | Sample tenant admin | `TENANT_ADMIN` |
| `oftza.product01` | Sample product manager | `PRODUCT_MANAGER` |
| `oftza.product02` | Sample product manager | `PRODUCT_MANAGER` |
| `oftza.undwrit01` | Sample general underwriter | `GENERAL_UNDERWRITER` |
| `oftza.undwrit02` | Sample general underwriter | `GENERAL_UNDERWRITER` |
| `oftza.lifeuw01` | Sample life underwriter | `LIFE_UNDERWRITER` |
| `oftza.lifeuw02` | Sample life underwriter | `LIFE_UNDERWRITER` |
| `oftza.claims01` | Sample claims handler | `CLAIMS_HANDLER` |
| `oftza.claims02` | Sample claims handler | `CLAIMS_HANDLER` |
| `oftza.claimsmgr01` | Sample claims manager | `CLAIMS_MANAGER` |
| `oftza.claimsmgr02` | Sample claims manager | `CLAIMS_MANAGER` |
| `oftza.healthclm01` | Sample health claims assessor | `HEALTH_CLAIMS_ASSESSOR` |
| `oftza.healthclm02` | Sample health claims assessor | `HEALTH_CLAIMS_ASSESSOR` |
| `oftza.healthmem01` | Sample health membership administrato | `HEALTH_MEMBERSHIP_ADMINISTRATO` |
| `oftza.healthmem02` | Sample health membership administrato | `HEALTH_MEMBERSHIP_ADMINISTRATO` |
| `oftza.fin01` | Sample finance officer | `FINANCE_OFFICER` |
| `oftza.fin02` | Sample finance officer | `FINANCE_OFFICER` |
| `oftza.finmgr01` | Sample finance manager | `FINANCE_MANAGER` |
| `oftza.finmgr02` | Sample finance manager | `FINANCE_MANAGER` |
| `oftza.pay01` | Sample finance officer | `FINANCE_OFFICER` |
| `oftza.pay02` | Sample finance manager | `FINANCE_MANAGER` |
| `oftza.dist01` | Sample distribution administrator | `DISTRIBUTION_ADMINISTRATOR` |
| `oftza.dist02` | Sample distribution administrator | `DISTRIBUTION_ADMINISTRATOR` |
| `oftza.reins01` | Sample reinsurance officer | `REINSURANCE_OFFICER` |
| `oftza.reins02` | Sample reinsurance officer | `REINSURANCE_OFFICER` |
| `oftza.compl01` | Sample compliance officer | `COMPLIANCE_OFFICER` |
| `oftza.compl02` | Sample compliance officer | `COMPLIANCE_OFFICER` |
| `oftza.dpo01` | Sample data protection officer | `DATA_PROTECTION_OFFICER` |
| `oftza.dpo02` | Sample data protection officer | `DATA_PROTECTION_OFFICER` |
| `oftza.cs01` | Sample customer service agent | `CUSTOMER_SERVICE_AGENT` |
| `oftza.cs02` | Sample customer service agent | `CUSTOMER_SERVICE_AGENT` |
| `oftza.ops01` | Sample operations analyst | `OPERATIONS_ANALYST` |
| `oftza.ops02` | Sample operations analyst | `OPERATIONS_ANALYST` |
| `oftza.integ01` | Sample integration administrator | `INTEGRATION_ADMINISTRATOR` |
| `oftza.integ02` | Sample integration administrator | `INTEGRATION_ADMINISTRATOR` |
| `oftza.audit01` | Sample auditor | `AUDITOR` |
| `oftza.audit02` | Sample auditor | `AUDITOR` |
| `oftza.healthaud01` | Sample health auditor | `HEALTH_AUDITOR` |
| `oftza.healthaud02` | Sample health auditor | `HEALTH_AUDITOR` |
| `oftza.approver01` | Sample oftza approver | `OFTZA_APPROVER` |
| `oftza.approver02` | Sample oftza approver | `OFTZA_APPROVER` |
| `oftza.linecfg01` | Sample line configuration manager | `LINE_CONFIGURATION_MANAGER` |
| `oftza.linecfg02` | Sample line configuration manager | `LINE_CONFIGURATION_MANAGER` |
| `oftza.broker01` | Sample broker | `BROKER` |
| `oftza.member01` | Sample member | `MEMBER` |
| `oftza.provider01` | Sample provider | `PROVIDER` |
| `operator.operator01` | Sample platform admin | `PLATFORM_ADMIN` (operator tenant) |
| `operator.operator02` | Sample platform admin | `PLATFORM_ADMIN` (operator tenant) |
| `operator.steward01` | Sample platform static data steward | `PLATFORM_STATIC_DATA_STEWARD` (operator tenant) |
| `operator.steward02` | Sample platform static data steward | `PLATFORM_STATIC_DATA_STEWARD` (operator tenant) |

## What's in it
OFT Insurance South Africa, invented: about 30 people and companies, 3 providers and 2 brokers; motor, household, funeral, term and a medical scheme; about 12 policies per line in different states; claims at each stage; collections, payouts, a refund and a closed month; a treaty, a broker's commission statement, and cases of each kind. Names carry "Sample", phone numbers can't be dialled, and email addresses end in `example.test`.

## What's stubbed
Nothing is sent or paid for real. The console lists the stubs; they are email, SMS, push and WhatsApp, payments (the simulator), sanctions screening and identity checks, reinsurers, the regulator and the general ledger. AI Assist is off.

## Where a journey stops or goes round
These are findings for the business review, not things to work around quietly.
- **A product manager can't approve a publication:** the administrator does.
- **A Life policy can't be cancelled:** there's no such step.
- **Grace, lapse and renewal invitations** come from jobs that run every hour, so a change shows within the hour, not at once.

## If something is wrong
Note the account, the time and what you did, and tell Ade. The Preview can be reset to its starting data, which removes everything entered since.
