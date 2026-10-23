/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Billing
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
using Insys.Billing.Api.StaticData;
using Insys.Billing.Application;
using Insys.Billing.Contracts;
using Insys.Billing.Infrastructure;
using Insys.BuildingBlocks.Api;
using Insys.BuildingBlocks.Application.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Billing.Api;

public sealed record OpenAccountRequest(Guid PayerPartyId, string Currency, string CollectionMethod, string? MandateRef);

public sealed record CreateScheduleRequest(string SourceDomain, Guid PolicyRef, string ProductVersionRef, string Frequency, DateOnly StartDate, DateOnly? EndDate,
    decimal InstalmentAmount);

public sealed record AmendScheduleRequest(decimal InstalmentAmount, DateOnly EffectiveFrom);

public sealed record IssueInvoicesRequest(DateOnly AsOf);

public sealed record CreditNoteRequest(decimal Amount, string ReasonCode);

public sealed record RefundRequestBody(decimal Amount, string ReasonCode, Guid BankAccountId);

/// <summary>The Billing module: accounts, premium schedules, invoices and credit notes, arrears and refunds (BIL-001 to BIL-004).</summary>
public sealed class BillingModule : IModule
{
    public string Name => "Billing";

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddBillingInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        BillingStaticDataEndpoints.MapStaticData(api);
        var accounts = api.MapGroup("/billing/accounts").WithTags(Name);

        accounts.MapPost("/", async (OpenAccountRequest r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new OpenAccount(r.PayerPartyId, r.Currency, r.CollectionMethod, r.MandateRef), ct))
                .ToHttp(view => Results.Created($"/api/v1/billing/accounts/{view.Id}", view)))
            .Produces<AccountView>(StatusCodes.Status201Created)
            .WithName("BillingOpenAccount")
            .WithSummary("Open a billing account for a payer")
            .RequirePermission(BillingPermissions.AccountCreate)
            .RequireIdempotencyKey();

        accounts.MapGet("/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) => (await dispatcher.Query(new GetAccount(id), ct)).ToHttp(Results.Ok))
            .Produces<AccountView>(StatusCodes.Status200OK)
            .WithName("BillingGetAccount")
            .WithSummary("Get a billing account with its schedules, instalments and arrears")
            .RequirePermission(BillingPermissions.AccountRead);

        accounts.MapPost("/{id:guid}/schedules", async (Guid id, CreateScheduleRequest r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new CreateSchedule(id, r.SourceDomain, r.PolicyRef, r.ProductVersionRef, r.Frequency, r.StartDate, r.EndDate, r.InstalmentAmount), ct))
                .ToHttp(Results.Ok))
            .Produces<AccountView>(StatusCodes.Status200OK)
            .WithName("BillingCreateSchedule")
            .WithSummary("Create a policy's premium schedule (BIL-001)")
            .WithDescription("Normally created from the domains' policy issued events; this endpoint is for authorised premium transactions.")
            .RequirePermission(BillingPermissions.AccountUpdate)
            .RequireIdempotencyKey();

        accounts.MapPut("/{id:guid}/schedules/{scheduleId:guid}", async (Guid id, Guid scheduleId, AmendScheduleRequest r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new AmendSchedule(id, scheduleId, r.InstalmentAmount, r.EffectiveFrom), ct)).ToHttp(Results.Ok))
            .Produces<AccountView>(StatusCodes.Status200OK)
            .WithName("BillingAmendSchedule")
            .WithSummary("Amend a schedule's instalment amount from a date; instalments already invoiced are not changed")
            .RequirePermission(BillingPermissions.AccountUpdate);

        accounts.MapPost("/{id:guid}/invoices", async (Guid id, IssueInvoicesRequest r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new IssueDueInvoices(id, r.AsOf), ct)).ToHttp(Results.Ok))
            .Produces<IReadOnlyList<InvoiceView>>(StatusCodes.Status200OK)
            .WithName("BillingIssueInvoices")
            .WithSummary("Issue invoices for instalments due by a date (the billing job does this daily)")
            .RequirePermission(BillingPermissions.InvoiceCreate)
            .RequireIdempotencyKey();

        accounts.MapGet("/{id:guid}/invoices", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new ListInvoices(id), ct)).ToHttp(Results.Ok))
            .Produces<IReadOnlyList<InvoiceView>>(StatusCodes.Status200OK)
            .WithName("BillingListInvoices")
            .WithSummary("List an account's invoices and credit notes")
            .RequirePermission(BillingPermissions.InvoiceRead);

        accounts.MapPost("/{id:guid}/age", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new AgeAccount(id, DateOnly.FromDateTime(DateTime.UtcNow)), ct)).ToHttp(Results.Ok))
            .Produces<AccountView>(StatusCodes.Status200OK)
            .WithName("BillingAgeAccount")
            .WithSummary("Mark overdue invoices and detect arrears by the ageing buckets (the billing job does this daily)")
            .RequirePermission(BillingPermissions.AccountUpdate);

        accounts.MapPost("/{id:guid}/close", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new CloseAccount(id), ct)).ToHttp(Results.Ok))
            .Produces<AccountView>(StatusCodes.Status200OK)
            .WithName("BillingCloseAccount")
            .WithSummary("Close an account with nothing outstanding")
            .RequirePermission(BillingPermissions.AccountUpdate);

        accounts.MapPost("/{id:guid}/refunds", async (Guid id, RefundRequestBody r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new RequestRefund(id, r.Amount, r.ReasonCode, r.BankAccountId), ct)).ToHttp(view => Results.Created($"/api/v1/billing/refunds/{view.Id}", view)))
            .Produces<RefundView>(StatusCodes.Status201Created)
            .WithName("BillingRequestRefund")
            .WithSummary("Request a refund; a second person approves it in Workflow before any payment (SRS-002)")
            .RequirePermission(BillingPermissions.RefundCreate)
            .RequireIdempotencyKey();

        var billing = api.MapGroup("/billing").WithTags(Name);
        billing.MapGet("/invoices/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) => (await dispatcher.Query(new GetInvoice(id), ct)).ToHttp(Results.Ok))
            .Produces<InvoiceView>(StatusCodes.Status200OK)
            .WithName("BillingGetInvoice")
            .WithSummary("Get an invoice or credit note")
            .RequirePermission(BillingPermissions.InvoiceRead);

        billing.MapPost("/invoices/{id:guid}/credit-notes", async (Guid id, CreditNoteRequest r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new RaiseCreditNote(id, r.Amount, r.ReasonCode), ct)).ToHttp(view => Results.Created($"/api/v1/billing/invoices/{view.Id}", view)))
            .Produces<InvoiceView>(StatusCodes.Status201Created)
            .WithName("BillingRaiseCreditNote")
            .WithSummary("Correct an issued invoice with a credit note; the invoice itself never changes (BIL-004)")
            .RequirePermission(BillingPermissions.InvoiceCreate)
            .RequireIdempotencyKey();

        billing.MapGet("/refunds/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) => (await dispatcher.Query(new GetRefund(id), ct)).ToHttp(Results.Ok))
            .Produces<RefundView>(StatusCodes.Status200OK)
            .WithName("BillingGetRefund")
            .WithSummary("Get a refund request")
            .RequirePermission(BillingPermissions.RefundRead);
    }
}
