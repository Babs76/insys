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
