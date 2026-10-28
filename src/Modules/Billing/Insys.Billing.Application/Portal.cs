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
using Insys.Billing.Domain.Billing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;

namespace Insys.Billing.Application;

// External-user access (docs/specs/addenda/external-user-access.md §2): invoices in the member and broker portals. A
// member sees the invoices of accounts they pay; a broker, the invoices of the policies it sells and services (its codes
// and its sub-brokers', from General and Life). Both within the caller's F103 scope. The view leaves out the account's
// collection details, allocations and adjustments (§4).

public sealed record PortalInvoiceView(Guid Id, string InvoiceNumber, [property: BuildingBlocks.Domain.OneOf("LIFE", "GENERAL", "HEALTH")] string SourceDomain, Guid PolicyRef, DateOnly IssueDate, DateOnly DueDate, string Currency,
    decimal TotalAmount, decimal Outstanding, InvoiceStatus Status)
{
    public static PortalInvoiceView From(Invoice i) => new(i.Id, i.InvoiceNumber, i.SourceDomain, i.PolicyRef, i.IssueDate, i.DueDate, i.Currency, i.TotalAmount,
        i.Outstanding, i.Status);
}

public sealed record ListPortalInvoices(Guid? AfterId, int Limit) : IQuery<IReadOnlyList<PortalInvoiceView>>;

public sealed record GetPortalInvoice(Guid InvoiceId) : IQuery<PortalInvoiceView>;

/// <summary>The portal's invoices, in the query so paging stays correct, within the caller's F103 scope.</summary>
public interface IBillingPortalQueries
{
    /// <summary>Invoices of accounts the party pays, or of the policies given; by ID after <paramref name="afterId"/>.</summary>
    Task<IReadOnlyList<Invoice>> Invoices(Guid? payerPartyId, IReadOnlyCollection<Guid> policyRefs, Guid? afterId, int limit, CancellationToken cancellationToken);

    Task<Invoice?> Invoice(Guid id, CancellationToken cancellationToken);

    Task<Guid?> PayerOf(Guid accountId, CancellationToken cancellationToken);
}

internal sealed class PortalInvoiceHandlers(IBillingPortalQueries queries, IEnumerable<IBrokeredPolicies> brokered, IExecutionContext execution, ITenantContext tenant) :
    IQueryHandler<ListPortalInvoices, IReadOnlyList<PortalInvoiceView>>,
    IQueryHandler<GetPortalInvoice, PortalInvoiceView>
{
    private ExternalSubject Who => execution.External ?? throw new InvalidOperationException("A portal endpoint is used by external users only.");

    public async Task<Result<IReadOnlyList<PortalInvoiceView>>> Handle(ListPortalInvoices query, CancellationToken cancellationToken)
    {
        var who = Who;
        var policies = new List<Guid>();
        if (who.Kind == ExternalSubject.Intermediary)
        {
            foreach (var domain in brokered)
            {
                policies.AddRange(await domain.PolicyIds(tenant.RequireTenantId(), who.IntermediaryCodes, cancellationToken));
            }
        }

        var payer = who.Kind == ExternalSubject.Party ? who.PartyId : null;
        var invoices = payer is null && policies.Count == 0 ? [] : await queries.Invoices(payer, policies, query.AfterId, query.Limit, cancellationToken);
        return Result.Success<IReadOnlyList<PortalInvoiceView>>(invoices.Select(PortalInvoiceView.From).ToList());
    }

    public async Task<Result<PortalInvoiceView>> Handle(GetPortalInvoice query, CancellationToken cancellationToken) =>
        await queries.Invoice(query.InvoiceId, cancellationToken) is { } invoice && await PortalInvoices.Related(Who, invoice.BillingAccountId, invoice.PolicyRef, queries,
            brokered, tenant.RequireTenantId(), cancellationToken)
            ? PortalInvoiceView.From(invoice)
            : Error.NotFound(ErrorCodes.NotFound, "The invoice was not found.");
}

internal static class PortalInvoices
{
    /// <summary>The member pays the invoice's account, or the broker sells or services its policy.</summary>
    public static async Task<bool> Related(ExternalSubject who, Guid accountId, Guid policyRef, IBillingPortalQueries queries, IEnumerable<IBrokeredPolicies> brokered,
        Guid tenantId, CancellationToken cancellationToken)
    {
        if (who.Kind == ExternalSubject.Party)
        {
            return who.PartyId is { } party && await queries.PayerOf(accountId, cancellationToken) == party;
        }

        if (who.Kind == ExternalSubject.Intermediary)
        {
            foreach (var domain in brokered)
            {
                if (await domain.Brokers(tenantId, who.IntermediaryCodes, policyRef, cancellationToken))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
