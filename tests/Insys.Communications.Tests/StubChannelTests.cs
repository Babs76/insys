/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Communications.Tests
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
using Insys.Communications.Application.Channels;
using Insys.Communications.Contracts;
using Insys.Communications.Infrastructure.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Insys.Communications.Tests;

/// <summary>The Wave 1 stub channels (addenda/communications.md §3, §10; OQ-088), resolved as the host resolves them.</summary>
public sealed class StubChannelTests : IDisposable
{
    private const string ValidEmail = "thandi.nkosi@example.co.za";
    private const string ValidMobile = "+27821234567";
    private const string ValidPushToken = "device-token-7f3a9c";

    private readonly ServiceProvider _services;
    private readonly INotificationChannels _channels;
    private readonly StubChannelOutbox _outbox;
    private readonly StubChannelSwitches _switches;
    private readonly LogSink _logs = new();

    public StubChannelTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_logs);
        services.AddSingleton(typeof(ILogger<>), typeof(CapturingLogger<>));
        services.AddStubNotificationChannels();
        _services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        _channels = _services.GetRequiredService<INotificationChannels>();
        _outbox = _services.GetRequiredService<StubChannelOutbox>();
        _switches = _services.GetRequiredService<StubChannelSwitches>();
    }

    public void Dispose() => _services.Dispose();

    private static OutboundMessage Message(string channel, string? address = null, string? subject = null, string? body = null)
    {
        var defaultAddress = channel switch
        {
            Channels.Email => ValidEmail,
            Channels.Push => ValidPushToken,
            _ => ValidMobile,
        };
        var defaultSubject = channel is Channels.Email or Channels.Push ? "Your policy schedule" : null;
        return new OutboundMessage(Guid.NewGuid(), Guid.NewGuid(), "STUB", "INSYS Life", address ?? defaultAddress,
            subject ?? defaultSubject, body ?? "Dear Thandi, your premium of R 450.00 is due on 1 November.");
    }

    private Task<ChannelReceipt> Send(OutboundMessage message, string channel) =>
        _channels.For(channel)!.Send(message, TestContext.Current.CancellationToken);

    public static TheoryData<string> AllChannels => [.. Channels.All];

    [Theory]
    [MemberData(nameof(AllChannels))]
    public async Task ValidMessage_IsAccepted_WithAStubReference_AndRecordedInTheOutbox(string channel)
    {
        var message = Message(channel);

        var receipt = await Send(message, channel);

        receipt.Accepted.ShouldBeTrue();
        receipt.FailureCode.ShouldBeNull();
        receipt.Retryable.ShouldBeFalse();
        receipt.ProviderRef.ShouldNotBeNull();
        receipt.ProviderRef.ShouldStartWith("STUB-");
        Guid.TryParseExact(receipt.ProviderRef["STUB-".Length..], "N", out _).ShouldBeTrue();

        var sent = _outbox.Sent(channel).ShouldHaveSingleItem();
        sent.Channel.ShouldBe(channel);
        sent.ProviderRef.ShouldBe(receipt.ProviderRef);
        sent.Message.ShouldBe(message);
    }

    [Theory]
    [MemberData(nameof(AllChannels))]
    public async Task EachSend_HasItsOwnReference_AndOnlyItsOwnChannelRecordsIt(string channel)
    {
        var first = await Send(Message(channel), channel);
        var second = await Send(Message(channel), channel);

        first.ProviderRef.ShouldNotBe(second.ProviderRef);
        _outbox.Sent(channel).Count.ShouldBe(2);
        foreach (var other in Channels.All.Where(c => c != channel))
            _outbox.Sent(other).ShouldBeEmpty();
    }

    [Fact]
    public async Task Outbox_KeepsOnlyTheLastThousandMessages_PerChannel()
    {
        var messages = Enumerable.Range(0, StubChannelOutbox.Capacity + 5).Select(_ => Message(Channels.Sms)).ToList();
        foreach (var message in messages)
            await Send(message, Channels.Sms);

        var sent = _outbox.Sent(Channels.Sms);
        sent.Count.ShouldBe(StubChannelOutbox.Capacity);
        sent[0].Message.ShouldBe(messages[5]);
        sent[^1].Message.ShouldBe(messages[^1]);
    }

    [Fact]
    public async Task Outbox_RecordsConcurrentSends_WithoutLosingAny()
    {
        await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(() => Send(Message(Channels.Email), Channels.Email))));

        _outbox.Sent(Channels.Email).Count.ShouldBe(200);
        _outbox.Sent(Channels.Email).Select(s => s.ProviderRef).Distinct().Count().ShouldBe(200);
    }

    [Theory]
    [InlineData("")]
    [InlineData("thandi")]
    [InlineData("thandi@")]
    [InlineData("@example.com")]
    [InlineData("thandi@example")]
    [InlineData("thandi@@example.com")]
    [InlineData("thandi nkosi@example.com")]
    [InlineData("thandi@example.com.")]
    [InlineData("+27821234567")]
    public async Task Email_ToAnImplausibleAddress_IsAddressRejected_NotRetryable(string address)
    {
        var receipt = await Send(Message(Channels.Email, address: address), Channels.Email);

        receipt.ShouldBe(ChannelReceipt.Failed(ChannelFailures.AddressRejected, retryable: false));
        _outbox.Sent(Channels.Email).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("a@b.co")]
    [InlineData("thandi.nkosi+policies@mail.example.co.za")]
    public async Task Email_ToAPlausibleAddress_IsAccepted(string address)
    {
        var receipt = await Send(Message(Channels.Email, address: address), Channels.Email);

        receipt.Accepted.ShouldBeTrue();
    }

    [Theory]
    [InlineData(Channels.Sms, "")]
    [InlineData(Channels.Sms, "0821234567")]
    [InlineData(Channels.Sms, "27821234567")]
    [InlineData(Channels.Sms, "+0821234567")]
    [InlineData(Channels.Sms, "+27 82 123 4567")]
    [InlineData(Channels.Sms, "+1234567")]
    [InlineData(Channels.Sms, "+1234567890123456")]
    [InlineData(Channels.Sms, "thandi@example.com")]
    [InlineData(Channels.WhatsApp, "")]
    [InlineData(Channels.WhatsApp, "0821234567")]
    [InlineData(Channels.WhatsApp, "+27-82-123-4567")]
    [InlineData(Channels.WhatsApp, "+1234567890123456")]
    public async Task SmsAndWhatsApp_ToANumberThatIsNotE164_AreAddressRejected_NotRetryable(string channel, string address)
    {
        var receipt = await Send(Message(channel, address: address), channel);

        receipt.ShouldBe(ChannelReceipt.Failed(ChannelFailures.AddressRejected, retryable: false));
        _outbox.Sent(channel).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(Channels.Sms, "+12345678")]
    [InlineData(Channels.Sms, "+123456789012345")]
    [InlineData(Channels.WhatsApp, "+27821234567")]
    [InlineData(Channels.WhatsApp, "+447911123456")]
    public async Task SmsAndWhatsApp_ToAnE164Number_AreAccepted(string channel, string address)
    {
        var receipt = await Send(Message(channel, address: address), channel);

        receipt.Accepted.ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Push_ToAnEmptyToken_IsAddressRejected_NotRetryable(string token)
    {
        var receipt = await Send(Message(Channels.Push, address: token), Channels.Push);

        receipt.ShouldBe(ChannelReceipt.Failed(ChannelFailures.AddressRejected, retryable: false));
        _outbox.Sent(Channels.Push).ShouldBeEmpty();
    }

    [Fact]
    public async Task Sms_BodyOfSixSegments_IsAccepted_AndOneCharacterMoreIsMessageRejected()
    {
        (await Send(Message(Channels.Sms, body: new string('a', 918)), Channels.Sms)).Accepted.ShouldBeTrue();

        var receipt = await Send(Message(Channels.Sms, body: new string('a', 919)), Channels.Sms);

        receipt.ShouldBe(ChannelReceipt.Failed(ChannelFailures.MessageRejected, retryable: false));
        _outbox.Sent(Channels.Sms).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Push_BodyOfAThousandCharacters_IsAccepted_AndOneCharacterMoreIsMessageRejected()
    {
        (await Send(Message(Channels.Push, body: new string('a', 1_000)), Channels.Push)).Accepted.ShouldBeTrue();

        var receipt = await Send(Message(Channels.Push, body: new string('a', 1_001)), Channels.Push);

        receipt.ShouldBe(ChannelReceipt.Failed(ChannelFailures.MessageRejected, retryable: false));
        _outbox.Sent(Channels.Push).Count.ShouldBe(1);
    }

    [Fact]
    public async Task WhatsApp_HasNoStubBodyLimit()
    {
        var receipt = await Send(Message(Channels.WhatsApp, body: new string('a', 2_000)), Channels.WhatsApp);

        receipt.Accepted.ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Email_WithoutASubject_IsMessageRejected_NotRetryable(string subject)
    {
        var receipt = await Send(Message(Channels.Email, subject: subject), Channels.Email);

        receipt.ShouldBe(ChannelReceipt.Failed(ChannelFailures.MessageRejected, retryable: false));
        _outbox.Sent(Channels.Email).ShouldBeEmpty();
    }

    [Fact]
    public async Task Email_WithANullSubject_IsMessageRejected()
    {
        var message = Message(Channels.Email) with { Subject = null };

        var receipt = await Send(message, Channels.Email);

        receipt.ShouldBe(ChannelReceipt.Failed(ChannelFailures.MessageRejected, retryable: false));
    }

    [Theory]
    [InlineData(Channels.Email, ChannelFailures.ProviderUnavailable, true)]
    [InlineData(Channels.Sms, ChannelFailures.RateLimited, true)]
    [InlineData(Channels.Push, ChannelFailures.AddressRejected, false)]
    [InlineData(Channels.WhatsApp, ChannelFailures.MessageRejected, false)]
    public async Task FailureSwitch_MakesTheChannelAnswerTheFailure_InsteadOfSending(string channel, string failureCode, bool retryable)
    {
        _switches.Fail(channel, failureCode, retryable);

        var receipt = await Send(Message(channel), channel);

        receipt.ShouldBe(ChannelReceipt.Failed(failureCode, retryable));
        _outbox.Sent(channel).ShouldBeEmpty();
    }

    [Fact]
    public async Task FailureSwitch_AffectsOnlyItsOwnChannel()
    {
        _switches.Fail(Channels.Sms, ChannelFailures.ProviderUnavailable, retryable: true);

        (await Send(Message(Channels.Email), Channels.Email)).Accepted.ShouldBeTrue();
        (await Send(Message(Channels.WhatsApp), Channels.WhatsApp)).Accepted.ShouldBeTrue();
        (await Send(Message(Channels.Sms), Channels.Sms)).Accepted.ShouldBeFalse();
    }

    [Fact]
    public async Task FailureSwitch_WhenCleared_LetsTheChannelSendAgain()
    {
        _switches.Fail(Channels.Email, ChannelFailures.ProviderUnavailable, retryable: true);
        (await Send(Message(Channels.Email), Channels.Email)).Accepted.ShouldBeFalse();

        _switches.Clear(Channels.Email);

        (await Send(Message(Channels.Email), Channels.Email)).Accepted.ShouldBeTrue();
        _switches.FailureFor(Channels.Email).ShouldBeNull();
    }

    [Fact]
    public async Task FailureSwitch_ClearAll_LetsEveryChannelSendAgain()
    {
        foreach (var channel in Channels.All)
            _switches.Fail(channel, ChannelFailures.RateLimited, retryable: true);

        _switches.ClearAll();

        foreach (var channel in Channels.All)
            (await Send(Message(channel), channel)).Accepted.ShouldBeTrue();
    }

    [Fact]
    public async Task MessageValidation_ComesBeforeTheFailureSwitch()
    {
        _switches.Fail(Channels.Sms, ChannelFailures.ProviderUnavailable, retryable: true);

        var receipt = await Send(Message(Channels.Sms, address: "0821234567"), Channels.Sms);

        receipt.ShouldBe(ChannelReceipt.Failed(ChannelFailures.AddressRejected, retryable: false));
    }

    [Theory]
    [MemberData(nameof(AllChannels))]
    public void Registry_ResolvesEachChannel_ByItsCode(string channel)
    {
        var resolved = _channels.For(channel);

        resolved.ShouldNotBeNull();
        resolved.Channel.ShouldBe(channel);
    }

    [Theory]
    [InlineData("FAX")]
    [InlineData("email")]
    [InlineData("")]
    public void Registry_AnswersNull_ForAnUnknownCode(string channel) => _channels.For(channel).ShouldBeNull();

    [Fact]
    public void Registry_RefusesTwoChannelsWithTheSameCode()
    {
        var email = _channels.For(Channels.Email)!;

        Should.Throw<InvalidOperationException>(() => new NotificationChannelRegistry([email, email]));
    }

    [Fact]
    public void Registration_IsIdempotent_AndRegistersOneSingletonPerChannel()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_logs);
        services.AddSingleton(typeof(ILogger<>), typeof(CapturingLogger<>));
        services.AddStubNotificationChannels();
        services.AddStubNotificationChannels();
        using var provider = services.BuildServiceProvider();

        var channels = provider.GetServices<INotificationChannel>().ToList();
        channels.Select(c => c.Channel).Order().ShouldBe(Channels.All.Order());
        provider.GetRequiredService<StubChannelOutbox>().ShouldBeSameAs(provider.GetRequiredService<StubChannelOutbox>());
        provider.GetRequiredService<INotificationChannels>().For(Channels.Sms).ShouldBeSameAs(channels.Single(c => c.Channel == Channels.Sms));
    }

    [Theory]
    [MemberData(nameof(AllChannels))]
    public async Task Logs_CarryOnlyChannelCorrelationAndReference_NeverTheAddressSubjectOrBody_Addendum10(string channel)
    {
        var message = Message(channel, subject: channel is Channels.Email or Channels.Push ? "Claim CLM-SECRET-SUBJECT" : null,
            body: "Your claim for the knee operation was approved");

        var sent = await Send(message, channel);
        _switches.Fail(channel, ChannelFailures.ProviderUnavailable, retryable: true);
        await Send(message, channel);
        _switches.Clear(channel);
        await Send(message with { Address = "not a valid address of any kind", CorrelationId = Guid.NewGuid() }, channel);

        var logged = _logs.AllText;
        logged.ShouldNotBeEmpty();
        logged.ShouldContain(channel);
        logged.ShouldContain(message.CorrelationId.ToString());
        logged.ShouldContain(sent.ProviderRef!);
        logged.ShouldNotContain(message.Address);
        logged.ShouldNotContain("not a valid address");
        logged.ShouldNotContain("knee operation");
        logged.ShouldNotContain("CLM-SECRET-SUBJECT");
        logged.ShouldNotContain(message.SenderIdentity);
    }
}
