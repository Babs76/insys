using FluentValidation;
using Insys.BuildingBlocks.Domain;

namespace Insys.BuildingBlocks.Application.Messaging;

/// <summary>Runs the request's FluentValidation validators before the handler. Failures return 422.</summary>
public sealed class ValidatingCommandHandler<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner,
    IEnumerable<IValidator<TCommand>> validators) : ICommandHandler<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    public async Task<Result<TResult>> Handle(TCommand command, CancellationToken cancellationToken)
    {
        var error = await RequestValidation.Validate(command, validators, cancellationToken);
        return error is null ? await inner.Handle(command, cancellationToken) : error;
    }
}

public sealed class ValidatingQueryHandler<TQuery, TResult>(
    IQueryHandler<TQuery, TResult> inner,
    IEnumerable<IValidator<TQuery>> validators) : IQueryHandler<TQuery, TResult>
    where TQuery : IQuery<TResult>
{
    public async Task<Result<TResult>> Handle(TQuery query, CancellationToken cancellationToken)
    {
        var error = await RequestValidation.Validate(query, validators, cancellationToken);
        return error is null ? await inner.Handle(query, cancellationToken) : error;
    }
}

public static class RequestValidation
{
    public static async Task<Error?> Validate<T>(T request, IEnumerable<IValidator<T>> validators, CancellationToken cancellationToken)
    {
        var failures = new List<FluentValidation.Results.ValidationFailure>();
        foreach (var validator in validators)
        {
            var result = await validator.ValidateAsync(request, cancellationToken);
            failures.AddRange(result.Errors);
        }

        if (failures.Count == 0)
        {
            return null;
        }

        var details = failures
            .GroupBy(f => f.PropertyName)
            .ToDictionary(g => g.Key, g => g.Select(f => f.ErrorMessage).ToArray());
        return Error.Validation(ErrorCodes.ValidationFailed, "The request is not valid.", details);
    }
}
