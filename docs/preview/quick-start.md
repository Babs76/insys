# INSYS Preview: quick-start

**For OpenFactor's functional team.** The INSYS Preview is a test system with invented data. It isn't production, and nothing in it reaches a real person, provider or bank, except the sign-in emails below. A banner on every page says so, and the sign-in page is titled "INSYS Preview (sample data only)".

## Signing in
- **Address:** `https://insys.project-demo.app`. Sign-in goes through `https://id.insys.project-demo.app`.
- **Accounts:** Ade gives each person the accounts they need, with their passwords, privately. Don't share them in chat or email.
- **Broker and provider portal accounts** ask for a second factor. Ade gives you its setup code with the password; if he can't, the first person to sign in sets it up and shares it.
- **Usernames** ignore case.
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
