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

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddFinanceInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        FinanceStaticDataEndpoints.MapStaticData(api);
        var finance = api.MapGroup("/finance").WithTags(Name);

        finance.MapGet("/journals/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new GetJournal(id), ct)).ToHttp(Results.Ok))
            .Produces<JournalView>(StatusCodes.Status200OK)
            .WithName("FinanceGetJournal")
            .WithSummary("Get a journal with its subledger entries and the posting rule versions used")
            .RequirePermission(FinancePermissions.JournalRead);

        finance.MapGet("/journals", async (string businessRefType, Guid businessRefId, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new ListJournals(businessRefType, businessRefId), ct)).ToHttp(Results.Ok))
            .Produces<IReadOnlyList<JournalView>>(StatusCodes.Status200OK)
            .WithName("FinanceListJournals")
            .WithSummary("List the journals of a business record (FIN-001)")
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

        finance.MapGet("/periods", async (IDispatcher dispatcher, CancellationToken ct) => (await dispatcher.Query(new ListPeriods(), ct)).ToHttp(Results.Ok))
            .Produces<IReadOnlyList<PeriodView>>(StatusCodes.Status200OK)
            .WithName("FinanceListPeriods")
            .WithSummary("List accounting periods and their states")
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
                .RequirePermission(FinancePermissions.PeriodUpdate);
        }

        finance.MapGet("/periods/{periodCode}/trial-balance", async (string periodCode, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new GetTrialBalance(periodCode), ct)).ToHttp(Results.Ok))
            .Produces<IReadOnlyList<TrialBalanceLine>>(StatusCodes.Status200OK)
            .WithName("FinanceTrialBalance")
            .WithSummary("Debits, credits and balance per account and currency for a period")
            .RequirePermission(FinancePermissions.JournalRead);

        finance.MapGet("/posting-exceptions", async (string? status, int? limit, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new ListPostingExceptions(status, Cursor.ClampLimit(limit)), ct)).ToHttp(Results.Ok))
            .Produces<IReadOnlyList<PostingExceptionView>>(StatusCodes.Status200OK)
            .WithName("FinanceListPostingExceptions")
            .WithSummary("Business events that could not be posted (FIN-007)")
            .RequirePermission(FinancePermissions.PostingExceptionRead);

        finance.MapPost("/posting-exceptions/{id:guid}/retry", async (Guid id, RetryPostingRequest r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new RetryPosting(id, r.AccountingDate), ct)).ToHttp(Results.Ok))
            .Produces<PostingExceptionView>(StatusCodes.Status200OK)
            .WithName("FinanceRetryPosting")
            .WithSummary("Post a failed business event again, optionally on another accounting date")
            .RequirePermission(FinancePermissions.JournalReverse)
            .RequireIdempotencyKey();
    }
}
