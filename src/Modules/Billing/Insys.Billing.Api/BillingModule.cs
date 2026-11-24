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
using Insys.Billing.Domain.Billing;
using Insys.Billing.Infrastructure;
using Insys.BuildingBlocks.Api;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.Billing.Api;

public sealed record OpenAccountRequest(Guid PayerPartyId, string Currency, [property: BuildingBlocks.Domain.CodeList(BillingCodeLists.CollectionMethods, "method_code")] string CollectionMethod,
    string? MandateRef);

public sealed record CreateScheduleRequest([property: BuildingBlocks.Domain.OneOf("LIFE", "GENERAL", "HEALTH")] string SourceDomain, Guid PolicyRef, string ProductVersionRef,
    [property: BuildingBlocks.Domain.CodeList(BillingCodeLists.Frequencies, "code")] string Frequency, DateOnly StartDate, DateOnly? EndDate, decimal InstalmentAmount);

public sealed record AmendScheduleRequest(decimal InstalmentAmount, DateOnly EffectiveFrom);

public sealed record IssueInvoicesRequest(DateOnly AsOf);

public sealed record CreditNoteRequest(decimal Amount, [property: BuildingBlocks.Domain.CodeList(BillingCodeLists.AdjustmentReasons, "code")] string ReasonCode);

/// <summary>DE-008: <paramref name="PaymentRef"/> is the failed payout's reference, typed; <paramref name="BankAccountId"/>, when given, another of the payer's accounts.</summary>
public sealed record RetryRefundPayoutRequest(string PaymentRef, string Reason, Guid? BankAccountId);

/// <summary>DE-008: <paramref name="PaymentRef"/> is the failed payout's reference, typed.</summary>
public sealed record CancelRefundRequest(string PaymentRef, string Reason);

public sealed record RefundRequestBody(decimal Amount, [property: BuildingBlocks.Domain.CodeList(BillingCodeLists.AdjustmentReasons, "code")] string ReasonCode, Guid BankAccountId);

/// <summary>The Billing module: accounts, premium schedules, invoices and credit notes, arrears and refunds (BIL-001 to BIL-004).</summary>
public sealed class BillingModule : IModule
{
    public string Name => "Billing";

    public IReadOnlyList<BuildingBlocks.Api.Functions.FunctionDeclaration> Functions => BillingFunctions.All;

    /// <summary>Business commands wait for the tenant to be activated (onboarding readiness).</summary>
    public bool TransactsBusiness => true;

    public void Register(IServiceCollection services, IConfiguration configuration) => services.AddBillingInfrastructure();

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        BillingStaticDataEndpoints.MapStaticData(api);
        var accounts = api.MapGroup("/billing/accounts").WithTags(Name).ETagsFromRowVersion();

        accounts.MapPost("/", async (OpenAccountRequest r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new OpenAccount(r.PayerPartyId, r.Currency, r.CollectionMethod, r.MandateRef), ct))
                .ToHttp(view => Results.Created($"/api/v1/billing/accounts/{view.Id}", view)))
            .Produces<AccountView>(StatusCodes.Status201Created)
            .WithName("BillingOpenAccount")
            .WithSummary("Open a billing account for a payer")
            .RequirePermission(BillingPermissions.AccountCreate)
            .RequireIdempotencyKey();

        accounts.MapGet("/", async (Guid? payerPartyId, Guid? policyRef, AccountStatus? status, string? cursor, int? limit, IDispatcher dispatcher, CancellationToken ct) =>
                await Page(cursor, limit, (after, size) => dispatcher.Query(new ListAccounts(payerPartyId, policyRef, status, after, size), ct), a => a.Id))
            .Produces<CursorPage<AccountView>>(StatusCodes.Status200OK)
            .WithName("BillingListAccounts")
            .WithSummary("Billing accounts by payer, policy and status, oldest first (BR-054)")
            .WithDescription("An account is listed when one of its schedules is in your scope, and shows only that part (PartlyInScope), as its read does.")
            .RequirePermission(BillingPermissions.AccountRead);

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
            .TakesIfMatch()
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

        accounts.MapPost("/{id:guid}/age", async (Guid id, IDispatcher dispatcher, IBusinessDate businessDate, CancellationToken ct) =>
                (await dispatcher.Send(new AgeAccount(id, businessDate.Today), ct)).ToHttp(Results.Ok))
            .Produces<AccountView>(StatusCodes.Status200OK)
            .TakesIfMatch()
            .WithName("BillingAgeAccount")
            .WithSummary("Mark overdue invoices and detect arrears by the ageing buckets (the billing job does this daily)")
            .RequirePermission(BillingPermissions.AccountUpdate);

        accounts.MapPost("/{id:guid}/close", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new CloseAccount(id), ct)).ToHttp(Results.Ok))
            .Produces<AccountView>(StatusCodes.Status200OK)
            .TakesIfMatch()
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

        var billing = api.MapGroup("/billing").WithTags(Name).ETagsFromRowVersion();
        billing.MapGet("/invoices/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) => (await dispatcher.Query(new GetInvoice(id), ct)).ToHttp(Results.Ok))
            .Produces<InvoiceView>(StatusCodes.Status200OK)
            .WithName("BillingGetInvoice")
            .WithSummary("Get an invoice or credit note")
            .RequirePermission(BillingPermissions.InvoiceRead);

        billing.MapPost("/migration/outstanding-premiums", async (MigrateOutstandingPremiums r, IDispatcher dispatcher, CancellationToken ct) => (await dispatcher.Send(r, ct)).ToHttp(Results.Ok))
            .Produces<PremiumMigrationResult>(StatusCodes.Status200OK).ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("BillingMigrateOutstandingPremiums").WithSummary("Bring premiums outstanding in a source system, all or nothing, against the file's control totals (B-013)")
            .RequirePermission(BillingPermissions.PremiumMigrate).RequireIdempotencyKey();
        billing.MapPost("/invoices/{id:guid}/credit-notes", async (Guid id, CreditNoteRequest r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new RaiseCreditNote(id, r.Amount, r.ReasonCode), ct)).ToHttp(outcome => outcome.CreditNote is { } credit
                    ? Results.Created($"/api/v1/billing/invoices/{credit.Id}", credit)
                    : Results.Accepted($"/api/v1/billing/credit-note-requests/{outcome.Request!.Id}", outcome.Request)))
            .Produces<InvoiceView>(StatusCodes.Status201Created)
            .Produces<CreditNoteRequestView>(StatusCodes.Status202Accepted)
            .WithName("BillingRaiseCreditNote")
            .WithSummary("Correct an issued invoice with a credit note; the invoice itself never changes (BIL-004)")
            .WithDescription("A reason that doesn't require approval issues the credit note at once (201). One that does (DE-010, OQ-058) asks for a CREDIT_NOTE approval "
                + "instead (202, the credit note request), for at most what the invoice still asks for; the invoice goes on taking payments while it waits.")
            .WithEventualEffects("for a 202, the CREDIT_NOTE approval appears in Workflow (businessRefType CREDIT_NOTE_REQUEST, the request's id). Once a second person "
                + "grants it, the request reads Issued and names its credit note, for what the invoice then still asks for, never more than was asked; if nothing is "
                + "outstanding by then it reads Lapsed and nothing is credited. Rejected, expired or withdrawn, it reads Rejected and nothing is credited.")
            .RequirePermission(BillingPermissions.InvoiceCreate)
            .RequireIdempotencyKey();

        billing.MapGet("/credit-note-requests/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new GetCreditNoteRequest(id), ct)).ToHttp(Results.Ok))
            .Produces<CreditNoteRequestView>(StatusCodes.Status200OK)
            .WithName("BillingGetCreditNoteRequest")
            .WithSummary("Get a credit note request: a credit note waiting for, or decided by, its CREDIT_NOTE approval (DE-010)")
            .WithDescription("The record a CREDIT_NOTE approval names (businessRefType CREDIT_NOTE_REQUEST). Its invoice lists it too, under creditNoteRequests.")
            .RequirePermission(BillingPermissions.InvoiceRead);

        billing.MapGet("/refunds", async (Guid? accountId, RefundStatus? status, string? cursor, int? limit, IDispatcher dispatcher, CancellationToken ct) =>
                await Page(cursor, limit, (after, size) => dispatcher.Query(new ListRefunds(accountId, status, after, size), ct), r => r.Id))
            .Produces<CursorPage<RefundView>>(StatusCodes.Status200OK)
            .WithName("BillingListRefunds")
            .WithSummary("Refunds by account and status, oldest first (BR-055); PaymentFailed and Cancelled included (DE-008)")
            .WithDescription("Only refunds of accounts wholly in your scope, as a refund's read (OQ-081).")
            .RequirePermission(BillingPermissions.RefundRead);

        billing.MapPost("/refunds/{id:guid}/retry-payout", async (Guid id, RetryRefundPayoutRequest r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new RetryRefundPayout(id, r.PaymentRef, r.Reason, r.BankAccountId), ct)).ToHttp(Results.Ok))
            .Produces<RefundView>(StatusCodes.Status200OK)
            .TakesIfMatch()
            .WithName("BillingRetryRefundPayout")
            .WithSummary("Ask for a refund whose payout failed to be paid again; a second person approves it (DE-008, PAYOUT_RETRY)")
            .WithDescription("Only a PaymentFailed refund with no retry waiting. paymentRef is the failed payout's reference (failedPaymentRef), typed: another is refused "
                + "with TYPED_REFERENCE_MISMATCH and nothing changes. bankAccountId, when given, is another of the payer's verified, current accounts in the refund's "
                + "currency (else REFUND_ACCOUNT_NOT_PAYABLE); without it the refund's own account is paid, and must still be one.")
            .WithEventualEffects("the PAYOUT_RETRY approval appears in Workflow (businessRefType REFUND_REQUEST) and the refund reads retryPending. Once a second person "
                + "grants it, the refund reads Approved on its next payoutAttempt, and Payments creates a new payout (payment.PayoutRequested) that waits for a "
                + "released payout batch; a succeeded payout then makes it Paid. Rejected, expired or withdrawn, it stays PaymentFailed with retryPending false.")
            .RequirePermission(BillingPermissions.RefundRetryPayout);

        billing.MapPost("/refunds/{id:guid}/cancel", async (Guid id, CancelRefundRequest r, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Send(new CancelRefund(id, r.PaymentRef, r.Reason), ct)).ToHttp(Results.Ok))
            .Produces<RefundView>(StatusCodes.Status200OK)
            .TakesIfMatch()
            .WithName("BillingCancelRefund")
            .WithSummary("Cancel a refund whose payout failed; its money stays with the account as credit (DE-008)")
            .WithDescription("Only a PaymentFailed refund with no retry waiting; paymentRef is the failed payout's reference, typed (else TYPED_REFERENCE_MISMATCH). "
                + "No approval, since no money moves: a refund reaches the ledger only when its payout succeeds.")
            .WithEventualEffects("Payments resolves the failed payout's reconciliation exception (payment.PayoutAbandoned).")
            .RequirePermission(BillingPermissions.RefundCancel);

        billing.MapGet("/refunds/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) => (await dispatcher.Query(new GetRefund(id), ct)).ToHttp(Results.Ok))
            .Produces<RefundView>(StatusCodes.Status200OK)
            .WithName("BillingGetRefund")
            .WithSummary("Get a refund request")
            .RequirePermission(BillingPermissions.RefundRead);

        api.MapGroup("/billing").WithTags(Name).MapGet("/summary", async (IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new BillingSummaryQuery(), ct)).ToHttp(Results.Ok))
            .Produces<BuildingBlocks.Application.DashboardSummary>(StatusCodes.Status200OK)
            .WithName("BillingSummary")
            .WithSummary("Dashboard figure OPEN_ARREARS (GAP-046): open arrears cases and their amount overdue per currency")
            .WithDescription("Only accounts wholly in your scope: an account that also bills policies outside it is left out (OQ-081).")
            .RequirePermission(BillingPermissions.AccountRead);

        // The member and broker portals (external-user access §2): the invoices a member pays, or of a broker's policies.
        const string member = BuildingBlocks.Application.Context.ExternalSubject.Party, broker = BuildingBlocks.Application.Context.ExternalSubject.Intermediary;
        var portal = api.MapGroup("/portal/billing").WithTags(Name);
        portal.MapGet("/invoices", async (Guid? afterId, int? limit, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new ListPortalInvoices(afterId, Math.Clamp(limit ?? 50, 1, 200)), ct)).ToHttp(Results.Ok))
            .Produces<IReadOnlyList<PortalInvoiceView>>(StatusCodes.Status200OK)
            .WithName("PortalBillingListInvoices")
            .WithSummary("The member's invoices (accounts they pay) or the broker's (its policies and its sub-brokers'), by ID after afterId")
            .RequirePermission(BillingPermissions.InvoiceRead).ForPortal(member, broker);
        portal.MapGet("/invoices/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
                (await dispatcher.Query(new GetPortalInvoice(id), ct)).ToHttp(Results.Ok))
            .Produces<PortalInvoiceView>(StatusCodes.Status200OK)
            .WithName("PortalBillingGetInvoice")
            .WithSummary("An invoice of the member or broker, without the account's collection details or allocations")
            .RequirePermission(BillingPermissions.InvoiceRead).ForPortal(member, broker);
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
