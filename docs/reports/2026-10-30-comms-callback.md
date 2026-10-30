# Communications callbacks, 30 October 2026

**Defect.** At an endpoint requiring a client token, `CommunicationsModule` accepted any authenticated token of the tenant, with no permission check, so any staff user's token satisfied it. The signature was always required.

**Fix.** As Payments: the token must hold `communication_callback.create`, granted only by the privileged function `COMMUNICATIONS_CALLBACK_CLIENT`, in no role template.

**Readiness.** `COMMUNICATION_CALLBACK_CLIENT` on `SHARED_OPERATIONAL_SETUP`, blocking, because that is where Communications' other onboarding check sits. It lists the providers affected.

**Tests.** No token, a staff user's token and a holder's token (`AnEndpointRequiringAClientToken_TakesAHoldersToken_AndNoStaffUsers_ADR023`), and the readiness cases. No test or demo setup required a client token, so none changed.

**Also.** The two literal grants in `FunctionMap.Permission` use their constants. B-011 is in the backlog.
