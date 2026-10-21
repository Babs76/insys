using System.Text.Json;
using Insys.BuildingBlocks.Domain;
using Insys.Integration.Domain.DeadLetters;
using Insys.Testing;

namespace Insys.Integration.Tests;

public sealed class DeadLetterTests
{
    private static readonly Guid Operator = Guid.CreateVersion7();

    private static DeadLetter NewDeadLetter() => DeadLetter.DeadLetterMessage(Guid.CreateVersion7(), new Source("outbox:party"),
        new MessageRef(Guid.NewGuid().ToString()), "{}", new DeliveryError("System.TimeoutException"), DateTimeOffset.UtcNow, 10);

    [Fact]
    public void DeadLetter_DeadLetterMessage_StartsDeadLettered()
    {
        var deadLetter = NewDeadLetter();

        deadLetter.Status.ShouldBe(DeadLetterStatus.DeadLettered);
        deadLetter.ResolvedBy.ShouldBeNull();
        deadLetter.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void DeadLetter_ReplayMessage_FromDeadLettered_MovesToReplayed_INT006()
    {
        var deadLetter = NewDeadLetter();

        deadLetter.ReplayMessage(Operator).IsSuccess.ShouldBeTrue();
        deadLetter.Status.ShouldBe(DeadLetterStatus.Replayed);
        deadLetter.ResolvedBy.ShouldBe(Operator);
        deadLetter.HasChanges.ShouldBeTrue();
    }

    [Fact]
    public void DeadLetter_DiscardMessage_FromDeadLettered_MovesToDiscarded_INT006()
    {
        var deadLetter = NewDeadLetter();

        deadLetter.DiscardMessage(Operator).IsSuccess.ShouldBeTrue();
        deadLetter.Status.ShouldBe(DeadLetterStatus.Discarded);
    }

    [Theory]
    [InlineData(DeadLetterTrigger.ReplayMessage, DeadLetterTrigger.ReplayMessage)]
    [InlineData(DeadLetterTrigger.ReplayMessage, DeadLetterTrigger.DiscardMessage)]
    [InlineData(DeadLetterTrigger.DiscardMessage, DeadLetterTrigger.ReplayMessage)]
    [InlineData(DeadLetterTrigger.DiscardMessage, DeadLetterTrigger.DiscardMessage)]
    public void DeadLetter_ResolvedMessage_CannotBeResolvedAgain_IsInvalidStateTransition(DeadLetterTrigger first, DeadLetterTrigger second)
    {
        var deadLetter = NewDeadLetter();
        Fire(deadLetter, first).IsSuccess.ShouldBeTrue();

        var result = Fire(deadLetter, second);

        result.Error!.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
        result.Error.Kind.ShouldBe(ErrorKind.Conflict);
    }

    [Fact]
    public void DeadLetter_ReplayedOnlyByAnAuthorisedOperator_INT006()
    {
        var deadLetter = NewDeadLetter();

        deadLetter.ReplayMessage(Guid.Empty).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        deadLetter.Status.ShouldBe(DeadLetterStatus.DeadLettered);
    }

    [Fact]
    public void DeadLetter_Lifecycle_MatchesDomainModelStateTransitions()
    {
        using var model = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryPaths.Specs, "domain-model.json")));
        var specified = model.RootElement.GetProperty("contexts").EnumerateArray()
            .Single(c => c.GetProperty("id").GetString() == "integration")
            .GetProperty("aggregates").EnumerateArray().Single(a => a.GetProperty("name").GetString() == "DeadLetter")
            .GetProperty("stateTransitions").EnumerateArray()
            .Select(t => (Normalise(t.GetProperty("from").GetString()!), t.GetProperty("trigger").GetString()!, Normalise(t.GetProperty("to").GetString()!)))
            .OrderBy(t => t.ToString())
            .ToList();

        var implemented = DeadLetter.Lifecycle.Transitions.Select(t => (t.From.ToString(), t.Trigger.ToString(), t.To.ToString())).OrderBy(t => t.ToString()).ToList();

        implemented.ShouldBe(specified);
    }

    private static string Normalise(string state) => string.Concat(state.Split('-', ' ').Select(p => char.ToUpperInvariant(p[0]) + p[1..]));

    private static Result Fire(DeadLetter deadLetter, DeadLetterTrigger trigger) =>
        trigger == DeadLetterTrigger.ReplayMessage ? deadLetter.ReplayMessage(Operator) : deadLetter.DiscardMessage(Operator);
}
