using System.Text.Json;
using FluentValidation;
using Insys.BuildingBlocks.Application.Auditing;
using Insys.BuildingBlocks.Application.Context;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Domain;
using Insys.BuildingBlocks.Domain.Expressions;
using Insys.Life.Domain;
using Insys.Life.Domain.Groups;
using Insys.ProductFactory.Contracts;

namespace Insys.Life.Application;

public sealed record SetUpGroupScheme(Guid EmployerPartyId, string ProductCode, int? ProductVersion, string SchemeType, DateOnly StartDate, DateOnly RenewalDate,
    string Currency) : ICommand<SchemeView>;

/// <param name="Facts">The member's facts the category's eligibility rule reads (for example their grade), as a JSON object.</param>
public sealed record EnrolGroupMember(Guid SchemeId, Guid PartyId, string CategoryCode, decimal? AnnualSalary, DateOnly JoinedOn, JsonElement? Facts)
    : ICommand<SchemeView>;

public sealed record ChangeGroupMember(Guid SchemeId, Guid MemberId, string CategoryCode, decimal? AnnualSalary, JsonElement? Facts) : ICommand<SchemeView>;

public sealed record AcceptMemberEvidence(Guid SchemeId, Guid MemberId) : ICommand<SchemeView>;

public sealed record RemoveGroupMember(Guid SchemeId, Guid MemberId, DateOnly LeftOn) : ICommand<SchemeView>;

public sealed record RenewGroupScheme(Guid SchemeId) : ICommand<SchemeView>;

public sealed record TerminateGroupScheme(Guid SchemeId, DateOnly On) : ICommand<SchemeView>;

public sealed record GetGroupScheme(Guid SchemeId) : IQuery<SchemeView>;

public sealed class SetUpGroupSchemeValidator : AbstractValidator<SetUpGroupScheme>
{
    public SetUpGroupSchemeValidator()
    {
        RuleFor(c => c.EmployerPartyId).NotEmpty();
        RuleFor(c => c.ProductCode).NotEmpty().MaximumLength(15);
        RuleFor(c => c.SchemeType).NotEmpty().MaximumLength(15);
        RuleFor(c => c.Currency).Length(3);
    }
}

public sealed class EnrolGroupMemberValidator : AbstractValidator<EnrolGroupMember>
{
    public EnrolGroupMemberValidator()
    {
        RuleFor(c => c.PartyId).NotEmpty();
        RuleFor(c => c.CategoryCode).NotEmpty().MaximumLength(10);
        RuleFor(c => c.AnnualSalary).GreaterThan(0).PrecisionScale(18, 2, true);
    }
}

/// <summary>A member's category, checked against its eligibility rule (an expression over the member's facts), and the free cover limit for the scheme's size.</summary>
internal static class Categories
{
    public static async Task<Result<(MemberCategory Category, decimal? FreeCoverLimit)>> Resolve(ILifeCodes codes, GroupScheme scheme, string categoryCode, JsonElement? facts,
        int members, DateOnly day, CancellationToken cancellationToken)
    {
        if (await codes.MemberCategory(scheme.SchemeNumber, categoryCode, day, cancellationToken) is not { } definition)
        {
            return Error.Validation(LifeErrorCodes.CodeNotInForce, $"{categoryCode} is not a member category of scheme {scheme.SchemeNumber} in force.");
        }

        var rule = Expression.Parse(definition.EligibilityRule);
        if (rule.IsFailure)
        {
            return Error.Validation(LifeErrorCodes.RuleNotEvaluable, $"The eligibility rule of {categoryCode} cannot be evaluated: {rule.Error.Message}");
        }

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (facts is { ValueKind: JsonValueKind.Object } json)
        {
            foreach (var property in json.EnumerateObject())
            {
                values[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number => property.Value.GetDecimal(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => null,
                };
            }
        }

        var eligible = rule.Value.Evaluate(values, today: day);
        if (eligible.IsFailure || !eligible.Value)
        {
            return Error.Validation(LifeErrorCodes.MemberNotEligible,
                eligible.IsFailure ? $"The eligibility rule of {categoryCode} could not be evaluated: {eligible.Error.Message}" : $"The member is not eligible for {categoryCode}.");
        }

        return (new MemberCategory(definition.CategoryCode, definition.BenefitMultiple), await codes.FreeCoverLimit(members, day, cancellationToken));
    }
}

internal sealed class SetUpGroupSchemeHandler(ILifeRepository life, ILifeCodes codes, ISellableProducts products, ITenantContext tenant, IClock clock, IAuditTrail audit)
    : ICommandHandler<SetUpGroupScheme, SchemeView>
{
    public async Task<Result<SchemeView>> Handle(SetUpGroupScheme command, CancellationToken cancellationToken)
    {
        var product = await Products.Sellable(products, tenant.RequireTenantId(), command.ProductCode, command.ProductVersion, clock.UtcToday, cancellationToken);
        if (product.IsFailure)
        {
            return product.Error;
        }

        if (!await codes.Exists(LifeCode.GroupSchemeType, command.SchemeType, clock.UtcToday, cancellationToken))
        {
            return Error.Validation(LifeErrorCodes.CodeNotInForce, $"{command.SchemeType} is not a group scheme type in force.");
        }

        var id = Guid.CreateVersion7();
        var scheme = GroupScheme.SetUpScheme(id, Numbers.Of("GS", id, 12), command.EmployerPartyId, product.Value.ProductCode, product.Value.Version, command.SchemeType,
            command.StartDate, command.RenewalDate, command.Currency, clock.UtcToday);
        if (scheme.IsFailure)
        {
            return scheme.Error;
        }

        life.Add(scheme.Value);
        audit.Record(new AuditEntry("LIFE", "SetUpScheme", nameof(GroupScheme), id.ToString()));
        return SchemeView.From(scheme.Value);
    }
}

internal sealed class EnrolGroupMemberHandler(ILifeRepository life, ILifeCodes codes, IClock clock, IAuditTrail audit) : ICommandHandler<EnrolGroupMember, SchemeView>
{
    public async Task<Result<SchemeView>> Handle(EnrolGroupMember command, CancellationToken cancellationToken)
    {
        if (await life.Scheme(command.SchemeId, cancellationToken) is not { } scheme)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The scheme was not found.");
        }

        var category = await Categories.Resolve(codes, scheme, command.CategoryCode, command.Facts, scheme.CurrentMembers + 1, clock.UtcToday, cancellationToken);
        if (category.IsFailure)
        {
            return category.Error;
        }

        audit.Record(new AuditEntry("LIFE", "EnrolMember", nameof(GroupScheme), scheme.Id.ToString()));
        var result = scheme.EnrolMember(command.PartyId, category.Value.Category, command.AnnualSalary, command.JoinedOn, category.Value.FreeCoverLimit, clock.UtcNow);
        return result.IsFailure ? result.Error : SchemeView.From(scheme);
    }
}

internal sealed class ChangeGroupMemberHandler(ILifeRepository life, ILifeCodes codes, IClock clock) : ICommandHandler<ChangeGroupMember, SchemeView>
{
    public async Task<Result<SchemeView>> Handle(ChangeGroupMember command, CancellationToken cancellationToken)
    {
        if (await life.Scheme(command.SchemeId, cancellationToken) is not { } scheme)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The scheme was not found.");
        }

        var category = await Categories.Resolve(codes, scheme, command.CategoryCode, command.Facts, scheme.CurrentMembers, clock.UtcToday, cancellationToken);
        if (category.IsFailure)
        {
            return category.Error;
        }

        var result = scheme.ChangeMember(command.MemberId, category.Value.Category, command.AnnualSalary, category.Value.FreeCoverLimit);
        return result.IsFailure ? result.Error : SchemeView.From(scheme);
    }
}

internal sealed class GroupSchemeChangeHandler(ILifeRepository life, IClock clock) :
    ICommandHandler<AcceptMemberEvidence, SchemeView>,
    ICommandHandler<RemoveGroupMember, SchemeView>,
    ICommandHandler<RenewGroupScheme, SchemeView>,
    ICommandHandler<TerminateGroupScheme, SchemeView>
{
    public Task<Result<SchemeView>> Handle(AcceptMemberEvidence command, CancellationToken cancellationToken) =>
        Apply(command.SchemeId, s => s.AcceptEvidence(command.MemberId), cancellationToken);

    public Task<Result<SchemeView>> Handle(RemoveGroupMember command, CancellationToken cancellationToken) =>
        Apply(command.SchemeId, s => s.RemoveMember(command.MemberId, command.LeftOn), cancellationToken);

    public Task<Result<SchemeView>> Handle(RenewGroupScheme command, CancellationToken cancellationToken) =>
        Apply(command.SchemeId, s => s.RenewScheme(clock.UtcNow), cancellationToken);

    public Task<Result<SchemeView>> Handle(TerminateGroupScheme command, CancellationToken cancellationToken) =>
        Apply(command.SchemeId, s => s.TerminateScheme(command.On), cancellationToken);

    private async Task<Result<SchemeView>> Apply(Guid schemeId, Func<GroupScheme, Result> change, CancellationToken cancellationToken)
    {
        if (await life.Scheme(schemeId, cancellationToken) is not { } scheme)
        {
            return Error.NotFound(ErrorCodes.NotFound, "The scheme was not found.");
        }

        var result = change(scheme);
        return result.IsFailure ? result.Error : SchemeView.From(scheme);
    }
}

internal sealed class GetGroupSchemeHandler(ILifeRepository life) : IQueryHandler<GetGroupScheme, SchemeView>
{
    public async Task<Result<SchemeView>> Handle(GetGroupScheme query, CancellationToken cancellationToken) =>
        await life.Scheme(query.SchemeId, cancellationToken) is { } scheme ? SchemeView.From(scheme) : Error.NotFound(ErrorCodes.NotFound, "The scheme was not found.");
}
