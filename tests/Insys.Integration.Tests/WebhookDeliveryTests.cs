using Insys.Integration.Domain.Webhooks;
using Insys.Testing;

namespace Insys.Integration.Tests;

public sealed class WebhookDeliveryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Lifecycle_MatchesDomainModel()
    {
        var spec = DomainModelSpec.Transitions("integration", "WebhookDelivery")
            .Select(t => t.Replace("2xx response", "Response2xx", StringComparison.Ordinal).Replace("Retries exhausted", "RetriesExhausted", StringComparison.Ordinal))
            .OrderBy(t => t, StringComparer.Ordinal);
        WebhookDelivery.Lifecycle.Transitions.Select(t => $"{t.From}-{t.Trigger}->{t.To}").OrderBy(t => t, StringComparer.Ordinal).ShouldBe(spec);
    }

    [Fact]
    public void FailedAttempts_AreRetriedWithBackOff_UpToThePolicyLimit_ThenTheDeliveryFails_INT006()
    {
        var policy = new RetryPolicy("RP", 3, "EXPONENTIAL", TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(3));
        var delivery = WebhookDelivery.DeliverEvent(Guid.CreateVersion7(), "WH-1", Guid.CreateVersion7(), "general.GeneralPolicyIssued", "{}", Now);
        delivery.IsDue(Now).ShouldBeTrue();

        delivery.RecordAttempt(503, null, policy, Now).IsSuccess.ShouldBeTrue();
        (delivery.Status, delivery.NextAttemptAt).ShouldBe((WebhookDeliveryStatus.Pending, Now.AddMinutes(1)));
        delivery.RecordAttempt(null, "TaskCanceledException", policy, Now).IsSuccess.ShouldBeTrue();
        delivery.NextAttemptAt.ShouldBe(Now.AddMinutes(2));
        delivery.RecordAttempt(500, null, policy, Now).IsSuccess.ShouldBeTrue();
        (delivery.Status, delivery.Attempt, delivery.NextAttemptAt).ShouldBe((WebhookDeliveryStatus.Failed, 3, (DateTimeOffset?)null));
        delivery.RetryNow(Now).IsFailure.ShouldBeTrue();

        policy.DelayAfter(5).ShouldBe(TimeSpan.FromMinutes(3));
        var ok = WebhookDelivery.DeliverEvent(Guid.CreateVersion7(), "WH-1", Guid.CreateVersion7(), "general.GeneralPolicyIssued", "{}", Now);
        ok.RecordAttempt(204, null, policy, Now).IsSuccess.ShouldBeTrue();
        ok.Status.ShouldBe(WebhookDeliveryStatus.Delivered);
    }
}
