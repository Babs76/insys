/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Integration
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
using FluentValidation;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Insys.Integration.Domain.DeadLetters;

namespace Insys.Integration.Application.DeadLetters;

public sealed record ListDeadLetters(DeadLetterStatus? Status, string? Source, Guid? AfterId, int Limit) : IQuery<IReadOnlyList<DeadLetterSummary>>;

public sealed record GetDeadLetter(Guid DeadLetterId) : IQuery<DeadLetterDetail>;

public sealed class ListDeadLettersValidator : AbstractValidator<ListDeadLetters>
{
    public ListDeadLettersValidator()
    {
        RuleFor(q => q.Limit).InclusiveBetween(1, 200);
        RuleFor(q => q.Source).MaximumLength(60);
    }
}

internal sealed class ListDeadLettersHandler(IDeadLetterQueries queries) : IQueryHandler<ListDeadLetters, IReadOnlyList<DeadLetterSummary>>
{
    public async Task<Result<IReadOnlyList<DeadLetterSummary>>> Handle(ListDeadLetters query, CancellationToken cancellationToken) =>
        Result.Success(await queries.List(query.Status, query.Source, query.AfterId, query.Limit, cancellationToken));
}

internal sealed class GetDeadLetterHandler(IDeadLetterQueries queries) : IQueryHandler<GetDeadLetter, DeadLetterDetail>
{
    public async Task<Result<DeadLetterDetail>> Handle(GetDeadLetter query, CancellationToken cancellationToken) =>
        await queries.Get(query.DeadLetterId, cancellationToken) is { } detail
            ? detail
            : Error.NotFound(ErrorCodes.NotFound, "The dead letter was not found.");
}
