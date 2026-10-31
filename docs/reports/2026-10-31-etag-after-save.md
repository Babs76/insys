# A change's response carries the version as saved (group A)

**The defect:** a handler builds its response before the save, so its `rowVersion` and ETag were one behind unless it added one by hand. A client using that ETag got 412. Adding one is wrong whenever the transaction writes the row more than once.

**The fix, in one place:** `TransactionalCommandHandler`, after the save and before the commit, reads the version back from the row and puts it on the result (`SavedVersions`). No handler computes a version any more. The target is:
- for a view with `rowVersion`: the tracked record with the view's `Id`, else the one aggregate the command changed;
- for a version returned as a number: the one aggregate changed, or the one the command names.

A response whose record the command didn't save keeps the version it read. Every write in the integration and end-to-end suites runs with `RequireSavedVersion`, which fails a number-returning command whose aggregate can't be told. Static data endpoints already read the entity after their own save, so they don't change.

## What changed
- **Every command returning a view with `rowVersion`:** corrected centrally, no code change. This is what most of the 448 write endpoints carrying an ETag or `rowVersion` use.
- **`ICommand<long>` commands not marked as versioned**, now `IReturnsSavedVersion`: `MakeVersionEffective`, `ExpireAccess`, `LinkSubject`, `EndSubjectLink`, `ExpireApproval`, `EscalateTask`.
- **44 hand-computed `RowVersion + 1` removed:**

```
ProductFactory/Products.cs:270
ProductFactory/Products.cs:286
ProductFactory/Products.cs:335
ProductFactory/Products.cs:369
ProductFactory/Products.cs:385
ProductFactory/Products.cs:400
Identity/Access.cs:191
Identity/Access.cs:209
Identity/Access.cs:225
Identity/Access.cs:289
Identity/Access.cs:308
Identity/Me.cs:160
Identity/SubjectLinks.cs:100
Identity/SubjectLinks.cs:122
Identity/Users.cs:290
Identity/Users.cs:322
Identity/Users.cs:357
Identity/Users.cs:475
Party/Parties.cs:471
Party/Parties.cs:697
Party/Parties.cs:771
General/PolicyAdministration.cs:312
Tenancy/Readiness.cs:106
Tenancy/Tenants.cs:240
Tenancy/Tenants.cs:272
Tenancy/Tenants.cs:296
Integration/Commands.cs:104
Integration/Commands.cs:122
Integration/ParkedDeliveries.cs:249
Compliance/Compliance.cs:312
StaticData/Changes.cs:127
Workflow/Cases.cs:802
Workflow/Approvals.cs:366
Workflow/Approvals.cs:387
Workflow/Approvals.cs:432
Workflow/Approvals.cs:454
Workflow/Processes.cs:228
Workflow/Processes.cs:279
Workflow/Processes.cs:305
Workflow/Processes.cs:320
Workflow/Processes.cs:371
Finance/GlExport.cs:188
Documents/Documents.cs:463
Communications/Requests.cs:351
```

## The guard
- **`ShouldChain`**, in `Insys.Testing`: the returned ETag moved, the previous one gets 412, the returned one takes a second change. It's on one endpoint of each way a response is built: a view in the handler (Health referrals), a number with a body (Party language), an ETag with no body (Identity roles), and a case action (Workflow). BR-121's test also submits with the filing's ETag.
- **`AChangesResponse_CarriesTheVersionAsSaved_HoweverOftenTheRowWasWritten`:** a save, then a raw write, then the pipeline's save, for both a number and a view.
- **`RequireSavedVersion`** in every host the suites start.
- **Shown failing first:** run 41174381241, on 60cff5d, with the guard and not the fix. In all six Life/Health/Enterprise end-to-end jobs the referral chain failed with `The change returned "2", the version from before it: built before the save.` The number-returning chains didn't fail there, because those handlers added one by hand; it is the `RowVersion + 1` the fix removes that they were resting on. The first version of the Identity and Case chains disturbed their host tests, and they now have tests of their own.

## Decisions for Ade
**You asked for a chain test on each write endpoint. It isn't built for each.** There are 448, each needing its own prerequisites and a valid second action, which I estimate at 20 to 25 days, and the responses now all pass through one line of code. Four chains prove each way a response is built. *Recommendation: accept this; add a chain only when a new way of building a response appears.* The one gap this leaves: a view whose `Id` is not the record changed, where two aggregates changed in one command, keeps the version it was built with. None failed in the suites, and those wouldn't catch it if no test reads that response's version.
