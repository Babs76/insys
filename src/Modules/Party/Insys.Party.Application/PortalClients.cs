/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Party
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
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Insys.Party.Domain.Parties;

namespace Insys.Party.Application;

// BR-091: a broker's clients in its portal (external-user access §2, §3), by its link only. A client is a party the broker
// registered (its code or a sub-broker's), or one that holds, or is quoted or proposed for, a policy under those codes in
// any domain (IBrokeredClients). A registration staff linked to an existing party after review (§6) is shown as that
// party, as the broker's quotes for it are made; anonymised parties are not shown. The view is the name and kind only:
// no identifiers, contact points, bank accounts or review status (§4, §6).

public sealed record PortalClientView(Guid Id, PartyKind Kind, string DisplayName, PartyStatus Status)
{
    public static PortalClientView From(Domain.Parties.Party p) => new(p.Id, p.Kind, p.DisplayName, p.Status);
}

public sealed record ListPortalClients(Guid? AfterId, int Limit) : IQuery<IReadOnlyList<PortalClientView>>;

public sealed record GetPortalClient(Guid PartyId) : IQuery<PortalClientView>;

public interface IPartyPortalQueries
{
    /// <summary>
    /// The parties among <paramref name="partyIds"/> or registered by one of <paramref name="registeredBy"/>, a merged one as
    /// its survivor, leaving out the anonymised; by ID after <paramref name="afterId"/>, or only <paramref name="only"/>.
    /// </summary>
    Task<IReadOnlyList<Domain.Parties.Party>> Clients(IReadOnlyCollection<Guid> partyIds, IReadOnlyCollection<string> registeredBy, Guid? only, Guid? afterId, int limit,
        CancellationToken cancellationToken);
}

internal sealed class PortalClientHandlers(IPartyPortalQueries queries, IEnumerable<IBrokeredClients> domains, IExecutionContext execution, ITenantContext tenant) :
    IQueryHandler<ListPortalClients, IReadOnlyList<PortalClientView>>,
    IQueryHandler<GetPortalClient, PortalClientView>
{
    public async Task<Result<IReadOnlyList<PortalClientView>>> Handle(ListPortalClients query, CancellationToken cancellationToken) =>
        Result.Success<IReadOnlyList<PortalClientView>>((await Clients(null, query.AfterId, query.Limit, cancellationToken)).Select(PortalClientView.From).ToList());

    public async Task<Result<PortalClientView>> Handle(GetPortalClient query, CancellationToken cancellationToken) =>
        (await Clients(query.PartyId, null, 1, cancellationToken)).SingleOrDefault() is { } client
            ? PortalClientView.From(client)
            : Error.NotFound(ErrorCodes.NotFound, "The client was not found.");

    private async Task<IReadOnlyList<Domain.Parties.Party>> Clients(Guid? only, Guid? afterId, int limit, CancellationToken cancellationToken)
    {
        var broker = execution.External ?? throw new InvalidOperationException("A portal endpoint is used by external users only.");
        if (broker.Kind != ExternalSubject.Intermediary || broker.IntermediaryCodes.Count == 0)
        {
            return [];
        }

        var ids = new HashSet<Guid>();
        foreach (var domain in domains)
        {
            ids.UnionWith(await domain.PartyIds(tenant.RequireTenantId(), broker.IntermediaryCodes, cancellationToken));
        }

        var registeredBy = broker.IntermediaryCodes.Select(code => $"{ExternalSubject.Intermediary}:{code}").ToList();
        return await queries.Clients(ids, registeredBy, only, afterId, limit, cancellationToken);
    }
}
