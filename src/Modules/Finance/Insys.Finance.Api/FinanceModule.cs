/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Finance
 * CONFIDENTIALITY AND INTELLECTUAL PROPERTY NOTICE
 * This source code and its associated documentation contain confidential and
 * proprietary information belonging to OpenFactor Technology Group or its
 * applicable intellectual property rights holder.
 * This material is provided solely for authorised development, maintenance,
 * operation or evaluation purposes, as applicable. No person may reproduce,
 * modify, distribute, disclose, publish or otherwise use this material except
 * as expressly permitted by an applicable written agreement or licence.
 * No ownership rights or licence to any intellectual property are granted
 * by the inclusion of this notice, except as expressly provided in an
 * applicable written agreement or licence.
 * Third-party software, open-source components and customer-owned materials
 * remain subject to their respective ownership rights and applicable licences
 * or agreements.
 * UNAUTHORISED ACCESS, USE, COPYING OR DISCLOSURE IS PROHIBITED.
 * ============================================================================
 */
using Insys.BuildingBlocks.Api;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.Finance.Api.StaticData;
using Insys.Finance.Application;
using Insys.Finance.Contracts;
using Insys.Finance.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Finance.Api;

public sealed record ReverseJournalRequest(DateOnly AccountingDate, string Reason);

public sealed record RetryPostingRequest(DateOnly? AccountingDate);

/// <summary>
/// The Finance module: balanced, traceable journals from business events (FIN-001 to FIN-005), accounting periods,
/// and posting exceptions for operators (FIN-007). Journals are posted by Finance's own policies, never by a client.
/// </summary>
public sealed class FinanceModule : IModule
{
    public string Name => "Finance";

    public IReadOnlyList<BuildingBlocks.Api.Functions.FunctionDeclaration> Functions => FinanceFunctions.All;

    /// <summary>Business commands wait for the tenant to be activated (onboarding readiness).</summary>
    public bool TransactsBusiness => true;

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddFinanceInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        FinanceStaticDataEndpoints.MapStaticData(api);
        var finance = api.MapGroup("/finance").WithTags(Name);
        MapGlExports(finance.MapGroup("/gl-exports"));

        finance.MapGet("/journals/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new GetJournal(id), ct)).ToHttp(Results.Ok))
            .Produces<JournalView>(StatusCodes.Status200OK)
            .WithName("FinanceGetJournal")
            .WithSummary("Get a journal with its subledger entries and the posting rule versions used")
            .RequirePermission(FinancePermissions.JournalRead);

        finance.MapGet("/journals", async (string? businessRefType, Guid? businessRefId, string? journalNumber, string? periodCode, DateOnly? accountingFrom,
                    DateOnly? accountingTo, string? cursor, int? limit, IDispatcher dispatcher, CancellationToken ct) =>
                await Page(cursor, limit, (after, size) => dispatcher.Query(new ListJournals(new JournalSearch(businessRefType, businessRefId, journalNumber, periodCode,
                    accountingFrom, accountingTo), after, size), ct), j => j.Id))
            .Produces<CursorPage<JournalView>>(StatusCodes.Status200OK)
            .WithName("FinanceListJournals")
            .WithSummary("Journals, oldest first: a business record's (FIN-001), or found by journal number, period and accounting dates (BR-043)")
            .WithDescription("businessRefType and businessRefId together, or neither; accountingFrom and accountingTo are inclusive. A cursor page (BR-043; was an array).")
            .WithKnownValues("businessRefType", JournalReferenceTypes.Known, "The business record types Finance posts journals for (GAP-037).")
            .RequirePermission(FinancePermissions.JournalRead);

        finance.MapPost("/journals/{id:guid}/reverse", async (Guid id, ReverseJournalRequest r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new ReverseJournal(id, r.AccountingDate, r.Reason), ct))
                .ToHttp(view => Results.Created($"/api/v1/finance/journals/{view.Id}", view)))
            .Produces<JournalView>(StatusCodes.Status201Created)
            .WithName("FinanceReverseJournal")
            .WithSummary("Reverse a journal: a new journal with debits and credits swapped (FIN-003)")
            .WithDescription("The original journal is never changed (FIN-002). The reversal is posted on its own accounting date, which must be in an open period.")
            .RequirePermission(FinancePermissions.JournalReverse)
            .RequireIdempotencyKey();

        finance.MapPost("/tax-returns", async (PrepareTaxReturnRequest r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new PrepareTaxReturn(r.TaxCode, r.Currency, r.PeriodStart, r.PeriodEnd), ct)).ToHttp(v => Results.Created($"/api/v1/finance/tax-returns/{v.Id}", v)))
            .Produces<TaxReturnView>(StatusCodes.Status201Created).ProducesProblem(StatusCodes.Status409Conflict).ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("FinancePrepareTaxReturn").WithSummary("Total a period's tax for a tax code from what was posted to its account (F094)").RequirePermission(FinancePermissions.TaxReturnCreate).RequireIdempotencyKey();
        finance.MapGet("/tax-returns", async (string? cursor, int? limit, IDispatcher dispatcher, CancellationToken ct) =>
                await Page(cursor, limit, (after, size) => dispatcher.Query(new ListTaxReturns(after, size), ct), r => r.Id))
            .Produces<CursorPage<TaxReturnView>>(StatusCodes.Status200OK)
            .WithName("FinanceListTaxReturns").WithSummary("Tax returns").RequirePermission(FinancePermissions.TaxReturnRead);
        finance.MapGet("/tax-returns/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) => (await dispatcher.Query(new GetTaxReturn(id), ct)).ToHttp(Results.Ok))
            .Produces<TaxReturnView>(StatusCodes.Status200OK).WithName("FinanceGetTaxReturn").WithSummary("A tax return").RequirePermission(FinancePermissions.TaxReturnRead);
        finance.MapPost("/tax-returns/{id:guid}/approve", async (Guid id, IDispatcher dispatcher, CancellationToken ct) => (await dispatcher.Send(new ApproveTaxReturn(id), ct)).ToHttp(Results.Ok))
            .Produces<TaxReturnView>(StatusCodes.Status200OK).ProducesProblem(StatusCodes.Status409Conflict).TakesIfMatch()
            .WithName("FinanceApproveTaxReturn").WithSummary("Approve a return; not for who prepared it (SRS-002)").RequirePermission(FinancePermissions.TaxReturnApprove);
        finance.MapPost("/tax-returns/{id:guid}/reject", async (Guid id, RejectTaxReturnRequest r, IDispatcher dispatcher, CancellationToken ct) => (await dispatcher.Send(new RejectTaxReturn(id, r.Reason), ct)).ToHttp(Results.Ok))
            .Produces<TaxReturnView>(StatusCodes.Status200OK).ProducesProblem(StatusCodes.Status409Conflict).ProducesProblem(StatusCodes.Status422UnprocessableEntity).TakesIfMatch()
            .WithName("FinanceRejectTaxReturn").WithSummary("Reject a return with a reason; another can then be prepared").RequirePermission(FinancePermissions.TaxReturnApprove);
        finance.MapGet("/tax-returns/{id:guid}/content", async (Guid id, IDispatcher dispatcher, CancellationToken ct) => (await dispatcher.Send(new ExportTaxReturn(id), ct)).ToHttp(csv => Results.Text(csv, "text/csv")))
            .Produces<string>(StatusCodes.Status200OK, "text/csv").ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("FinanceExportTaxReturn").WithSummary("An approved return as CSV; INSYS files nothing").RequirePermission(FinancePermissions.TaxReturnRead);
        finance.MapGet("/periods", async (IDispatcher dispatcher, CancellationToken ct) => (await dispatcher.Query(new ListPeriods(), ct)).ToHttp(Results.Ok))
            .Produces<IReadOnlyList<PeriodView>>(StatusCodes.Status200OK)
            .WithName("FinanceListPeriods")
            .WithSummary("List accounting periods and their states, with the calendar's periods not yet opened as Future (BR-045)")
            .RequirePermission(FinancePermissions.PeriodRead);

        finance.MapGet("/periods/{periodCode}", async (string periodCode, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new GetPeriod(periodCode), ct)).ToHttp(Results.Ok))
            .Produces<PeriodView>(StatusCodes.Status200OK)
            .WithName("FinanceGetPeriod")
            .WithSummary("An accounting period (yyyy-MM), or Future when only the calendar has it (BR-045)")
            .RequirePermission(FinancePermissions.PeriodRead);

        foreach (var (action, route) in new[] { (PeriodAction.Open, "open"), (PeriodAction.SoftClose, "soft-close"), (PeriodAction.Close, "close"), (PeriodAction.RequestReopen, "reopen") })
        {
            finance.MapPost($"/periods/{{periodCode}}/{route}", async (string periodCode, IDispatcher dispatcher, CancellationToken ct) =>
                    (await dispatcher.Send(new ChangePeriod(periodCode, action), ct)).ToHttp(Results.Ok))
                .Produces<PeriodView>(StatusCodes.Status200OK)
                .WithName($"FinancePeriod{action}")
                .WithSummary(action switch
                {
                    PeriodAction.Open => "Open a period defined in the accounting calendar",
                    PeriodAction.SoftClose => "Soft-close a period: it accepts no more postings",
                    PeriodAction.Close => "Close a soft-closed period for good",
                    _ => "Ask to reopen a soft-closed period; a second person approves it in Workflow",
                })
                .RequirePermission(FinancePermissions.PeriodUpdate)
                .OnboardingConfiguration();
        }

        finance.MapGet("/periods/{periodCode}/trial-balance", async (string periodCode, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new GetTrialBalance(periodCode), ct)).ToHttp(Results.Ok))
            .Produces<IReadOnlyList<TrialBalanceLine>>(StatusCodes.Status200OK)
            .WithName("FinanceTrialBalance")
            .WithSummary("Debits, credits and balance per account and currency for a period")
            .RequirePermission(FinancePermissions.JournalRead);

        finance.MapGet("/posting-exceptions", async (string? status, string? cursor, int? limit, IDispatcher dispatcher, CancellationToken ct) =>
                await Page(cursor, limit, (after, size) => dispatcher.Query(new ListPostingExceptions(status, size, after), ct), e => e.Id))
            .Produces<CursorPage<PostingExceptionView>>(StatusCodes.Status200OK)
            .WithName("FinanceListPostingExceptions")
            .WithSummary("Business events that could not be posted (FIN-007), oldest first, a cursor page (BR-044; was an array)")
            .WithKnownValues("status", ["OPEN", "RESOLVED"], "OPEN or RESOLVED.")
            .RequirePermission(FinancePermissions.PostingExceptionRead);

        finance.MapGet("/posting-exceptions/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new GetPostingException(id), ct)).ToHttp(Results.Ok))
            .Produces<PostingExceptionView>(StatusCodes.Status200OK)
            .WithName("FinanceGetPostingException")
            .WithSummary("A posting exception, with the failed event's amount, accounting date and business record (BR-044)")
            .RequirePermission(FinancePermissions.PostingExceptionRead);

        finance.MapPost("/posting-exceptions/{id:guid}/retry", async (Guid id, RetryPostingRequest r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new RetryPosting(id, r.AccountingDate), ct)).ToHttp(Results.Ok))
            .Produces<PostingExceptionView>(StatusCodes.Status200OK)
            .WithName("FinanceRetryPosting")
            .WithSummary("Post a failed business event again, optionally on another accounting date")
            .RequirePermission(FinancePermissions.JournalReverse)
            .RequireIdempotencyKey();
    }

    /// <summary>The general ledger export (F055; SRS §15.1).</summary>
    private static void MapGlExports(RouteGroupBuilder exports)
    {
        exports.MapPost("/", async (GlExportRequest r, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                (await dispatcher.Send(new ExportToGeneralLedger(r.LayoutCode, r.PeriodCode, r.FromDate, r.ToDate), ct)).ToHttp(view =>
                {
                    Concurrency.SetETag(http, view.RowVersion);
                    return Results.Created($"/api/v1/finance/gl-exports/{view.Id}", view);
                }))
            .Produces<GlExportView>(StatusCodes.Status201Created)
            .ReturnsETag()
            .WithName("FinanceExportToGeneralLedger")
            .WithSummary("Export a date range's posted entries to the general ledger")
            .WithDescription("The posted subledger entries with an accounting date in the range (within one period), summarised by accounting date, account and "
                + "currency, are sent in the layout (CSV) through the GL adapter; the batch is Sent until the GL acknowledges or rejects it. A range that "
                + "overlaps a batch of the layout that was not rejected is refused, so no entry is exported twice.")
            .RequirePermission(FinancePermissions.GlExportCreate)
            .RequireIdempotencyKey();

        exports.MapGet("/", async (string? periodCode, string? cursor, int? limit, IDispatcher dispatcher, CancellationToken ct) =>
            {
                if (!Cursor.TryDecode(cursor, out var afterId))
                {
                    return Problems.For(BuildingBlocks.Domain.ErrorCodes.ValidationFailed, "The cursor is not valid.", StatusCodes.Status422UnprocessableEntity);
                }

                var size = Cursor.ClampLimit(limit);
                return (await dispatcher.Query(new ListGlExports(periodCode, afterId, size), ct))
                    .ToHttp(items => Results.Ok(new CursorPage<GlExportView>(items, items.Count == size ? Cursor.Encode(items[^1].Id) : null)));
            })
            .Produces<CursorPage<GlExportView>>(StatusCodes.Status200OK)
            .WithName("FinanceListGlExports")
            .WithSummary("List GL export batches, optionally for one period")
            .RequirePermission(FinancePermissions.GlExportRead);

        exports.MapGet("/{id:guid}", async (Guid id, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                (await dispatcher.Query(new GetGlExport(id), ct)).ToHttp(view =>
                {
                    Concurrency.SetETag(http, view.RowVersion);
                    return Results.Ok(view);
                }))
            .Produces<GlExportView>(StatusCodes.Status200OK)
            .ReturnsETag()
            .WithName("FinanceGetGlExport")
            .WithSummary("Get a GL export batch")
            .RequirePermission(FinancePermissions.GlExportRead);

        exports.MapGet("/{id:guid}/content", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new GetGlExportContent(id), ct)).ToHttp(csv => Results.Text(csv, "text/csv")))
            .Produces<string>(StatusCodes.Status200OK, "text/csv")
            .WithName("FinanceGetGlExportContent")
            .WithSummary("The batch as sent: CSV, rebuilt from the posted entries, which never change (FIN-002)")
            .RequirePermission(FinancePermissions.GlExportRead);

        exports.MapPost("/{id:guid}/acknowledgement", (Guid id, GlAcknowledgementRequest r, IDispatcher dispatcher, HttpContext http, CancellationToken ct) =>
                Versioned(http, version => dispatcher.Send(new RecordGlAcknowledgement(id, r.Accepted, r.Reason, version), ct)))
            .Produces(StatusCodes.Status204NoContent)
            .RequireIfMatch()
            .WithName("FinanceAcknowledgeGlExport")
            .WithSummary("Record the GL's answer: acknowledged, or rejected with a reason (the range may then be exported again)")
            .RequirePermission(FinancePermissions.GlExportUpdate);
    }

    private static async Task<IResult> Versioned(HttpContext http, Func<long, Task<BuildingBlocks.Domain.Result<long>>> send)
    {
        var expected = Concurrency.ExpectedVersion(http.Request);
        if (expected.IsFailure)
        {
            return Problems.For(expected.Error);
        }

        return (await send(expected.Value)).ToHttp(version =>
        {
            Concurrency.SetETag(http, version);
            return Results.NoContent();
        });
    }

    /// <summary>A cursor page of a list query (GAP-032).</summary>
    private static async Task<IResult> Page<T>(string? cursor, int? limit, Func<Guid?, int, Task<BuildingBlocks.Domain.Result<IReadOnlyList<T>>>> query, Func<T, Guid> id)
    {
        if (!Cursor.TryDecode(cursor, out var after))
        {
            return Problems.For(BuildingBlocks.Domain.ErrorCodes.ValidationFailed, "The cursor is not valid.", StatusCodes.Status422UnprocessableEntity);
        }

        var size = Cursor.ClampLimit(limit);
        return (await query(after, size)).ToHttp(items => Results.Ok(new CursorPage<T>(items, items.Count == size ? Cursor.Encode(id(items[^1])) : null)));
    }
}

public sealed record GlExportRequest(string LayoutCode, string PeriodCode, DateOnly FromDate, DateOnly ToDate);

public sealed record GlAcknowledgementRequest(bool Accepted, string? Reason);

public sealed record PrepareTaxReturnRequest(string TaxCode, string Currency, DateOnly PeriodStart, DateOnly PeriodEnd);

public sealed record RejectTaxReturnRequest(string Reason);
