/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : IntegrationTests
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
using System.Net;
using System.Text;
using System.Text.Json;
using Insys.BuildingBlocks.Api.StaticData;
using Insys.BuildingBlocks.Application.Messaging;
using Insys.BuildingBlocks.Application.ProviderCallbacks;
using Insys.BuildingBlocks.Contracts;
using Insys.BuildingBlocks.Infrastructure.Persistence;
using Insys.Communications.Contracts;
using Insys.Communications.Infrastructure;
using Insys.Communications.Infrastructure.Channels;
using Insys.Party.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.IntegrationTests;

/// <summary>
/// Notifications (addenda/communications.md, Wave 1): a trigger queues one notification per party and type; the sending
/// job sends it on the party's first channel in their language, with the address masked; missing configuration parks;
/// a service notice keeps a channel and a refused channel falls back; marketing needs consent when it is sent, and opting
/// out withdraws it in Party; delivery callbacks are signed and replay-safe; members see only their own notices; staff
/// see only their scope; templates merge only their type's fields; an anonymised party's notices are blanked.
/// </summary>
public sealed class CommunicationsTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private static readonly Guid Tenant = DatabaseFixture.TenantA;
    private static readonly DateOnly From = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-30);

    private static readonly string[] Permissions =
    [
        StaticDataPermissions.Read, StaticDataPermissions.Create, StaticDataPermissions.Update, PartyPermissions.PartyCreate, PartyPermissions.PartyRead, PartyPermissions.PartyUpdate,
        PartyPermissions.ConsentRead, PartyPermissions.ConsentUpdate, CommunicationPermissions.NotificationRead, CommunicationPermissions.PreferenceRead,
        CommunicationPermissions.PreferenceUpdate, CommunicationPermissions.CallbackEndpointRead, CommunicationPermissions.CallbackEndpointUpdate,
    ];

    private InsysApiFactory _factory = null!;
    private HttpClient _staff = null!;

    public async ValueTask InitializeAsync()
    {
        var db = fixture.Database;
        _factory = await fixture.Host();
        await db.UserWithPermissions(Tenant, "comms-staff", Permissions);
        _staff = _factory.ClientFor(Tenant, "comms-staff");
        foreach (var (code, name) in new[] { ("en", "English"), ("af", "Afrikaans") })
        {
            await db.ExecuteAsOwner("""
                INSERT INTO refdata.ref_languages (code, version, effective_from, record_status, created_by, name)
                VALUES (@c, 1, @f, 'ACTIVE', 'seed', @n) ON CONFLICT DO NOTHING
                """, ("c", code), ("f", From), ("n", name));
        }

        await Seed("tenant.cfg_locale_defaults", "LOCALE", ("tenant", "A"), ("base_currency", "ZAR"), ("languages", new[] { "en", "af" }), ("default_language", "en"),
            ("time_zone", "Africa/Johannesburg"), ("country", "ZA"));
        await Seed("party.cfg_consent_purposes", "MKT_SMS", ("purpose_code", "MKT_SMS"), ("label", "Marketing by SMS"), ("legal_basis", "CONSENT"), ("jurisdiction", "ZA"),
            ("default_value", false));
        await Seed("communication.cfg_channel_providers", "PRV-EMAIL", ("channel", "EMAIL"), ("provider_code", "MAIL-1"), ("sender_identity", "noreply@insurer.test"), ("enabled", true));
        await Seed("communication.cfg_channel_providers", "PRV-SMS", ("channel", "SMS"), ("provider_code", "SMS-1"), ("sender_identity", "INSURER"), ("enabled", true));

        await Type(NotificationTypes.ClaimStatus, "SERVICE", null, ["EMAIL", "SMS"], portalVisible: true);
        await Type(NotificationTypes.PaymentReminder, "SERVICE", null, ["SMS", "EMAIL"], portalVisible: false);
        await Type(NotificationTypes.PreauthDecision, "SERVICE", null, ["WHATSAPP"], portalVisible: false);
        await Type(NotificationTypes.RenewalInvitation, "SERVICE", null, ["EMAIL"], portalVisible: false);
        await Type("PROMO_SMS", "MARKETING", "MKT_SMS", ["SMS"], portalVisible: false);

        await Template(NotificationTypes.ClaimStatus, "EMAIL", "en", "Claim {claim_number}", "Your claim {claim_number} is {status}.");
        await Template(NotificationTypes.ClaimStatus, "EMAIL", "af", "Eis {claim_number}", "U eis {claim_number} is {status}.");
        await Template(NotificationTypes.ClaimStatus, "SMS", "en", null, "Claim {claim_number}: {status}. {sender_identity}");
        await Template(NotificationTypes.PaymentReminder, "SMS", "en", null, "{amount_overdue} {currency} is overdue on {account_number}.");
        await Template("PROMO_SMS", "SMS", "en", null, "New cover from {sender_identity}. To stop: {opt_out_contact}");
    }

    public ValueTask DisposeAsync()
    {
        Channels().Clear();
        Switches().ClearAll();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ServiceNotice_IsSentInThePartysLanguage_OnItsFirstChannel_WithTheAddressMaskedAndQueuedOnce()
    {
        var party = await Party("Botha", email: "tumi@example.com", mobile: "+27821234567", language: "af");
        var envelope = Request(NotificationTypes.ClaimStatus, party, new() { ["claim_number"] = "GC-0001", ["status"] = "REGISTERED" });
        await Publish(envelope);
        await Publish(envelope);
        (await Count(party)).ShouldBe(1L, "a redelivered trigger queues nothing (INT-005)");

        await Send();
        var sent = await Single(party);
        Documented(sent, "CommunicationsGetNotification");
        Documented(await (await _staff.GetAsync($"/api/v1/communications/notifications?partyId={party}")).Json(), "CommunicationsListNotifications");
        Documented(await (await _staff.GetAsync($"/api/v1/communications/parties/{party}/preferences")).Json(), "CommunicationsGetPreferences");
        sent.GetProperty("status").GetString().ShouldBe("Sent");
        sent.GetProperty("channel").GetString().ShouldBe("EMAIL");
        sent.GetProperty("language").GetString().ShouldBe("af");
        sent.GetProperty("subject").GetString().ShouldBe("Eis GC-0001");
        sent.GetProperty("body").GetString().ShouldBe("U eis GC-0001 is REGISTERED.");
        var attempt = sent.GetProperty("attempts")[0];
        attempt.GetProperty("maskedAddress").GetString().ShouldBe("t***@example.com");
        attempt.GetProperty("providerRef").GetString()!.ShouldStartWith("STUB-");
        Channels().Sent("EMAIL").ShouldContain(m => m.Message.Address == "tumi@example.com" && m.Message.Body == "U eis GC-0001 is REGISTERED.");
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM communication.delivery_attempt WHERE masked_address LIKE '%tumi@%'")).ShouldBe(0L,
            "the address is never stored in full");
        (await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM communication.outbox_event WHERE event_type = 'communication.NotificationSent'"))
            .ShouldBeGreaterThan(0L);
    }

    [Fact]
    public async Task MissingTypeProviderOrTemplate_ParksTheTrigger_ADR022()
    {
        var party = await Party("Parked", email: "p@example.com", mobile: null, language: null);
        foreach (var (type, code) in new[]
                 {
                     ("UNKNOWN_TYPE", PrerequisiteCodes.NotificationTypeNotConfigured),
                     (NotificationTypes.PreauthDecision, PrerequisiteCodes.ChannelProviderNotConfigured),
                     (NotificationTypes.RenewalInvitation, PrerequisiteCodes.NotificationTemplateNotInForce),
                 })
        {
            var envelope = Request(type, party, new() { ["status"] = "X" });
            await Publish(envelope);
            (await fixture.Database.ScalarAsOwner<string>("SELECT prerequisite_code FROM integration.parked_delivery WHERE event_id = @e", ("e", envelope.EventId)))
                .ShouldBe(code, type);
        }

        (await Count(party)).ShouldBe(0L);
    }

    [Fact]
    public async Task Preferences_OrderTheChannels_AServiceNoticeKeepsOne_AndARefusedChannelFallsBack()
    {
        var party = await Party("Order", email: "o@example.com", mobile: "+27820000001", language: null);
        var url = $"/api/v1/communications/parties/{party}/preferences/{NotificationTypes.ClaimStatus}";
        var listed = (await (await _staff.GetAsync($"/api/v1/communications/parties/{party}/preferences")).Json()).EnumerateArray().ToList();
        var claimStatus = listed.Single(p => p.GetProperty("typeCode").GetString() == NotificationTypes.ClaimStatus);
        claimStatus.GetProperty("canOptOut").GetBoolean().ShouldBeFalse();
        claimStatus.GetProperty("channels").EnumerateArray().Select(c => c.GetString()).ShouldBe(["EMAIL", "SMS"], "the type's default order until the party sets one");
        claimStatus.GetProperty("rowVersion").ValueKind.ShouldBe(JsonValueKind.Null);
        var promo = listed.Single(p => p.GetProperty("typeCode").GetString() == "PROMO_SMS");
        (promo.GetProperty("canOptOut").GetBoolean(), promo.GetProperty("consentGranted").GetBoolean()).ShouldBe((true, false), "every type is listed, set or not");

        var set = await (await _staff.Send(HttpMethod.Put, url, new { channels = new[] { "SMS", "EMAIL" }, optedOut = false })).Json();
        var version = set.GetProperty("rowVersion").GetInt64();
        (await _staff.Send(HttpMethod.Put, url, new { channels = new[] { "SMS", "EMAIL" }, optedOut = false }, ifMatch: $"\"{version + 5}\"")).StatusCode
            .ShouldBe(HttpStatusCode.PreconditionFailed, "a stale If-Match is refused");
        (await _staff.Send(HttpMethod.Put, url, new { channels = new[] { "SMS", "EMAIL" }, optedOut = false }, ifMatch: $"\"{version}\"")).StatusCode
            .ShouldBe(HttpStatusCode.OK);
        var optOut = await _staff.Send(HttpMethod.Put, url, new { channels = Array.Empty<string>(), optedOut = true });
        optOut.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await optOut.Code()).ShouldBe("PREFERENCE_NOT_ALLOWED");
        (await _staff.Send(HttpMethod.Put, url, new { channels = new[] { "WHATSAPP" }, optedOut = false })).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        // SMS refuses, retryably: retried with back-off, three times, then email.
        Switches().Fail("SMS", ChannelFailuresCode("RATE_LIMITED"), retryable: true);
        try
        {
            await Publish(Request(NotificationTypes.ClaimStatus, party, new() { ["claim_number"] = "GC-0002", ["status"] = "APPROVED" }));
            for (var pass = 0; pass < 3; pass++)
            {
                await Send();
                (await Single(party)).GetProperty("status").GetString().ShouldBe("Queued");
                await fixture.Database.ExecuteAsOwner("UPDATE communication.notification SET next_attempt_at = now() WHERE recipient_party_id = @p", ("p", party));
            }

            await Send();
            var sent = await Single(party);
            sent.GetProperty("status").GetString().ShouldBe("Sent");
            sent.GetProperty("channel").GetString().ShouldBe("EMAIL");
            sent.GetProperty("attempts").EnumerateArray().Count(a => a.GetProperty("channel").GetString() == "SMS").ShouldBe(3);
        }
        finally
        {
            Switches().ClearAll();
        }
    }

    [Fact]
    public async Task AChannelThatRejectsTheAddress_IsNotRetried_AndAPartyWithNoAddressIsUndeliverable()
    {
        var rejected = await Party("Rejected", email: "r@example.com", mobile: "+27820000002", language: null);
        await _staff.Send(HttpMethod.Put, $"/api/v1/communications/parties/{rejected}/preferences/{NotificationTypes.ClaimStatus}",
            new { channels = new[] { "SMS", "EMAIL" }, optedOut = false });
        Switches().Fail("SMS", "ADDRESS_REJECTED", retryable: false);
        try
        {
            await Publish(Request(NotificationTypes.ClaimStatus, rejected, new() { ["claim_number"] = "GC-0003", ["status"] = "SETTLED" }));
            await Send();
            await Send();
            var sent = await Single(rejected);
            sent.GetProperty("channel").GetString().ShouldBe("EMAIL");
            sent.GetProperty("attempts").GetArrayLength().ShouldBe(2);
        }
        finally
        {
            Switches().ClearAll();
        }

        var nowhere = await Party("Nowhere", email: null, mobile: null, language: null);
        await Publish(Request(NotificationTypes.ClaimStatus, nowhere, new() { ["claim_number"] = "GC-0004", ["status"] = "CLOSED" }));
        await Send();
        await Send();
        var undeliverable = await Single(nowhere);
        undeliverable.GetProperty("status").GetString().ShouldBe("Undeliverable");
        undeliverable.GetProperty("attempts").EnumerateArray().Select(a => a.GetProperty("failureCode").GetString()).ShouldAllBe(c => c == "NO_ADDRESS");
    }

    [Fact]
    public async Task Marketing_NeedsConsentWhenSent_AndAMemberOptingOutWithdrawsIt_POPIA69()
    {
        var party = await Party("Consent", email: null, mobile: "+27820000003", language: null);
        var consents = $"/api/v1/party/parties/{party}/consents";

        // No consent: recorded, never sent.
        await Publish(Request("PROMO_SMS", party, new()));
        (await Single(party)).GetProperty("status").GetString().ShouldBe("SuppressedNoConsent");

        // Consent given, then withdrawn before the job sends: suppressed at once.
        (await _staff.Send(HttpMethod.Post, $"{consents}/grant", new { purposeCode = "MKT_SMS", captureChannel = "PORTAL" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var second = Request("PROMO_SMS", party, new());
        await Publish(second);
        (await _staff.Send(HttpMethod.Post, $"{consents}/withdraw", new { purposeCode = "MKT_SMS", captureChannel = "CALL_CENTRE" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        (await Status(second.EventId)).ShouldBe("SUPPRESSED_NO_CONSENT");
        (await Promo(party)).GetProperty("optedOut").GetBoolean().ShouldBeTrue("a withdrawal in Party opts the party out, so the two agree");

        // Consent again: sent, naming the sender and how to stop.
        (await _staff.Send(HttpMethod.Post, $"{consents}/grant", new { purposeCode = "MKT_SMS", captureChannel = "PORTAL" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        var regranted = await Promo(party);
        (regranted.GetProperty("optedOut").GetBoolean(), regranted.GetProperty("consentGranted").GetBoolean()).ShouldBe((false, true), "consent in Party clears the opt-out");
        var third = Request("PROMO_SMS", party, new());
        await Publish(third);
        await Send();
        (await Status(third.EventId)).ShouldBe("SENT");
        Channels().Sent("SMS").ShouldContain(m => m.Message.Address == "+27820000003" && m.Message.Body.StartsWith("New cover from INSURER. To stop:"));

        // The member opts out in the portal: the preference, and the consent ledger's withdrawal.
        var member = await Member("comms-member", party);
        (await member.Send(HttpMethod.Put, "/api/v1/portal/communications/preferences/PROMO_SMS", new { optedOut = true })).StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.DrainOutbox();
        var current = await (await _staff.GetAsync(consents)).Json();
        current.EnumerateArray().Single(c => c.GetProperty("purposeCode").GetString() == "MKT_SMS").GetProperty("granted").GetBoolean().ShouldBeFalse();
        var fourth = Request("PROMO_SMS", party, new());
        (await member.GetAsync("/api/v1/portal/communications/preferences")).StatusCode.ShouldBe(HttpStatusCode.OK);
        await Publish(fourth);
        (await Status(fourth.EventId)).ShouldBe("SUPPRESSED_NO_CONSENT");

        // A service notice cannot be opted out of in the portal either.
        (await (await member.Send(HttpMethod.Put, $"/api/v1/portal/communications/preferences/{NotificationTypes.ClaimStatus}", new { optedOut = true })).Code())
            .ShouldBe("PREFERENCE_NOT_ALLOWED");
    }

    [Fact]
    public async Task DeliveryCallbacks_AreSigned_ARepeatIsRefused_AndDeliveryIsRecorded_ADR023()
    {
        var issued = await (await _staff.Send(HttpMethod.Post, "/api/v1/communications/callback-endpoints", new { providerCode = "MAIL-1", requireClientToken = false },
            idempotent: true)).Json();
        var path = issued.GetProperty("callbackPath").GetString()!;
        var secret = issued.GetProperty("signingSecret").GetString()!;

        var party = await Party("Delivered", email: "d@example.com", mobile: null, language: null);
        await Publish(Request(NotificationTypes.ClaimStatus, party, new() { ["claim_number"] = "GC-0005", ["status"] = "APPROVED" }));
        await Send();
        var notification = await Single(party);
        var providerRef = notification.GetProperty("attempts")[0].GetProperty("providerRef").GetString()!;

        var body = JsonSerializer.Serialize(new { providerRef, status = "DELIVERED" });
        (await Callback(path, secret, body, "cb-1")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Single(party)).GetProperty("status").GetString().ShouldBe("Delivered");

        var replay = await Callback(path, secret, body, "cb-1");
        replay.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await replay.Code()).ShouldBe("CALLBACK_REPLAYED");
        var forged = await Callback(path, "not-the-secret", body, "cb-2");
        forged.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await forged.Code()).ShouldBe("CALLBACK_SIGNATURE_INVALID");
        (await Callback("/api/v1/communications/callbacks/" + CallbackKeys.NewSecret(), secret, body, "cb-3")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Callback(path, secret, JsonSerializer.Serialize(new { providerRef = "STUB-unknown", status = "BOUNCED" }), "cb-4")).StatusCode
            .ShouldBe(HttpStatusCode.NoContent, "an unknown reference is acknowledged, so the provider does not retry it forever");
        (await _staff.GetAsync("/api/v1/communications/callback-endpoints")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Members_SeeOnlyTheirOwnNotices_OfTypesShownInThePortal_ExternalUserAccess()
    {
        var mine = await Party("Mine", email: "m@example.com", mobile: "+27820000004", language: null);
        var theirs = await Party("Theirs", email: "t@example.com", mobile: null, language: null);
        await Publish(Request(NotificationTypes.ClaimStatus, mine, new() { ["claim_number"] = "GC-0006", ["status"] = "REGISTERED" }));
        await Publish(Request(NotificationTypes.PaymentReminder, mine, new() { ["account_number"] = "BA-1", ["amount_overdue"] = "100.00", ["currency"] = "ZAR" }));
        await Publish(Request(NotificationTypes.ClaimStatus, theirs, new() { ["claim_number"] = "GC-0007", ["status"] = "REGISTERED" }));
        await Send();

        var member = await Member("comms-member-2", mine);
        var page = await (await member.GetAsync("/api/v1/portal/communications/notifications")).Json();
        Documented(page, "PortalCommunicationsListNotifications");
        var listed = page.GetProperty("items").EnumerateArray().ToList();
        listed.Select(n => n.GetProperty("typeCode").GetString()).ShouldBe([NotificationTypes.ClaimStatus], "the payment reminder is not shown in the portal");
        listed[0].TryGetProperty("maskedAddress", out _).ShouldBeFalse();
        (await member.GetAsync($"/api/v1/portal/communications/notifications/{listed[0].GetProperty("id").GetGuid()}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var other = (await Single(theirs)).GetProperty("id").GetGuid();
        (await member.GetAsync($"/api/v1/portal/communications/notifications/{other}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await member.GetAsync("/api/v1/communications/notifications")).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "staff endpoints refuse portal users");
    }

    [Fact]
    public async Task Staff_SeeOnlyNotificationsWithinTheirScope_F103()
    {
        var party = await Party("Scoped", email: "s@example.com", mobile: null, language: null);
        await Publish(Request(NotificationTypes.ClaimStatus, party, new() { ["claim_number"] = "GC-0008", ["status"] = "REGISTERED" }, product: "GEN-A"));
        await Publish(Request(NotificationTypes.ClaimStatus, party, new() { ["claim_number"] = "GC-0009", ["status"] = "REGISTERED" }, product: "GEN-B"));
        var user = await fixture.Database.UserWithPermissions(Tenant, "comms-scoped", CommunicationPermissions.NotificationRead);
        await fixture.Database.ExecuteAsOwner("UPDATE iam.user_role_assignment SET scope_products = ARRAY['GEN-A'] WHERE user_id = @u", ("u", user));
        var scoped = _factory.ClientFor(Tenant, "comms-scoped");
        var items = (await (await scoped.GetAsync($"/api/v1/communications/notifications?partyId={party}")).Json()).GetProperty("items");
        items.GetArrayLength().ShouldBe(1);
        var outside = (await (await _staff.GetAsync($"/api/v1/communications/notifications?partyId={party}")).Json()).GetProperty("items").EnumerateArray()
            .Single(n => n.GetProperty("id").GetGuid() != items[0].GetProperty("id").GetGuid()).GetProperty("id").GetGuid();
        (await scoped.GetAsync($"/api/v1/communications/notifications/{outside}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Templates_MergeOnlyTheirTypesFields_AndMarketingNamesTheSenderAndHowToStop()
    {
        var unknownField = await _staff.Send(HttpMethod.Post, "/api/v1/communications/static/notification-templates",
            TemplateBody("TPL-BAD", NotificationTypes.ClaimStatus, "EMAIL", "Claim", "Your ID {id_number}"), idempotent: true);
        unknownField.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await unknownField.Content.ReadAsStringAsync());
        (await unknownField.Json()).GetProperty("errors").GetProperty("body")[0].GetString()!.ShouldContain("{id_number}");

        var noOptOut = await _staff.Send(HttpMethod.Post, "/api/v1/communications/static/notification-templates",
            TemplateBody("TPL-PROMO", "PROMO_SMS", "SMS", null, "New cover from {sender_identity}"), idempotent: true);
        noOptOut.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await noOptOut.Json()).GetProperty("errors").GetProperty("body")[0].GetString()!.ShouldContain("opt_out_contact");

        var marketingWithoutConsent = await _staff.Send(HttpMethod.Post, "/api/v1/communications/static/notification-types",
            new { code = "PROMO_X", typeCode = "PROMO_X", category = "MARKETING", channels = new[] { "SMS" }, portalVisible = false, changeReason = "test" }, idempotent: true);
        marketingWithoutConsent.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        (await _staff.Send(HttpMethod.Post, "/api/v1/communications/static/notification-templates",
            TemplateBody("TPL-OK", NotificationTypes.ClaimStatus, "EMAIL", "Claim {claim_number}", "Claim {claim_number} is {status}."), idempotent: true))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task AnAnonymisedPartysNotices_AreBlanked_POPIA()
    {
        var party = await Party("Forgotten", email: "f@example.com", mobile: null, language: null);
        await Publish(Request(NotificationTypes.ClaimStatus, party, new() { ["claim_number"] = "GC-0010", ["status"] = "SETTLED" }));
        await Send();
        await _factory.Services.GetRequiredService<IEventBus>().Publish(Envelope("party.PartyAnonymised", party, new PartyAnonymised(party)), CancellationToken.None);
        var blanked = await Single(party);
        blanked.GetProperty("body").ValueKind.ShouldBe(JsonValueKind.Null);
        blanked.GetProperty("attempts")[0].GetProperty("maskedAddress").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task AHealthNoticesText_IsShownOnlyToThoseWhoMayReadHealthClaims_HLT008()
    {
        var party = await Party("Patient", email: "h@example.com", mobile: null, language: null);
        var source = Guid.CreateVersion7();
        await Publish(Envelope("communication.NotificationRequested", source, new NotificationRequested(NotificationTypes.ClaimStatus, party, "HEALTH_CLAIM", source,
            new Dictionary<string, string> { ["claim_number"] = "HC-0001", ["status"] = "PAID" })));
        await Send();
        var notice = await Single(party);
        (notice.GetProperty("body").ValueKind, notice.GetProperty("bodyRedacted").GetBoolean()).ShouldBe((JsonValueKind.Null, true));
        notice.GetProperty("sourceReference").GetString().ShouldBe("HC-0001");

        await fixture.Database.UserWithPermissions(Tenant, "comms-health", CommunicationPermissions.NotificationRead, "medical_claim.read");
        var reader = await (await _factory.ClientFor(Tenant, "comms-health").GetAsync($"/api/v1/communications/notifications/{notice.GetProperty("id").GetGuid()}")).Json();
        (reader.GetProperty("body").GetString(), reader.GetProperty("bodyRedacted").GetBoolean()).ShouldBe(("Your claim HC-0001 is PAID.", false));
    }

    [Fact]
    public async Task AMember_ListsAndRemovesTheirPushDevices_AndNeverSeesTheToken()
    {
        var party = await Party("Device", email: null, mobile: null, language: null);
        var user = await fixture.Database.UserWithPermissions(Tenant, "comms-device", CommunicationPermissions.DeviceUpdate);
        await fixture.Database.ExecuteAsOwner("""
            INSERT INTO iam.user_subject_link (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, user_id, kind, party_id,
                                               status, requested_by, decided_by, decided_at)
            VALUES (@t, gen_random_uuid(), 1, now(), 'seed', now(), 'seed', @u, 'PARTY', @p, 'ACTIVE', gen_random_uuid(), gen_random_uuid(), now())
            """, ("t", Tenant), ("u", user), ("p", party));
        await Member("comms-device-setup", party);
        var member = _factory.ClientFor(Tenant, "comms-device", "member-portal");
        var registered = await member.Send(HttpMethod.Post, "/api/v1/portal/communications/devices", new { platform = "ANDROID", token = "push-token-123" }, idempotent: true);
        registered.StatusCode.ShouldBe(HttpStatusCode.Created, await registered.Content.ReadAsStringAsync());
        var device = (await registered.Json()).GetProperty("id").GetGuid();
        var listed = await (await member.GetAsync("/api/v1/portal/communications/devices")).Content.ReadAsStringAsync();
        listed.ShouldContain(device.ToString());
        listed.ShouldNotContain("push-token-123");
        (await member.DeleteAsync($"/api/v1/portal/communications/devices/{device}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await (await member.GetAsync("/api/v1/portal/communications/devices")).Json())[0].GetProperty("revokedAt").ValueKind.ShouldNotBe(JsonValueKind.Null);
    }

    private static void Documented(JsonElement response, string operationId) =>
        DocumentedSchema.ResponseProblems(response, "core", operationId).ShouldBeEmpty(operationId);

    private async Task<JsonElement> Promo(Guid party) =>
        (await (await _staff.GetAsync($"/api/v1/communications/parties/{party}/preferences")).Json()).EnumerateArray()
        .Single(p => p.GetProperty("typeCode").GetString() == "PROMO_SMS");

    private static string ChannelFailuresCode(string code) => code;

    private StubChannelOutbox Channels() => _factory.Services.GetRequiredService<StubChannelOutbox>();

    private StubChannelSwitches Switches() => _factory.Services.GetRequiredService<StubChannelSwitches>();

    private Task Send() => _factory.Services.GetRequiredService<NotificationSender>().RunForTenant(Tenant, CancellationToken.None);

    private Task Publish(EventEnvelope envelope) => _factory.Services.GetRequiredService<IEventBus>().Publish(envelope, CancellationToken.None);

    /// <summary>A trigger about its own record: deliveries for one record are kept in order (ADR-022), so each test record is new.</summary>
    private static EventEnvelope Request(string type, Guid party, Dictionary<string, string> values, string? product = null)
    {
        var source = Guid.CreateVersion7();
        return Envelope("communication.NotificationRequested", source, new NotificationRequested(type, party, "GENERAL_CLAIM", source, values, product));
    }

    private static EventEnvelope Envelope<T>(string type, Guid aggregate, T payload) =>
        new(Guid.CreateVersion7(), Tenant, type, 1, DateTimeOffset.UtcNow, "Test", aggregate, Guid.CreateVersion7(), null,
            JsonSerializer.SerializeToElement(payload, ModuleDbContext.EventJson));

    private Task<long> Count(Guid party) =>
        fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM communication.notification WHERE recipient_party_id = @p", ("p", party));

    private async Task<string?> Status(Guid eventId) =>
        await fixture.Database.ScalarAsOwner<string>("SELECT status FROM communication.notification WHERE request_key LIKE @k", ("k", $"{eventId:N}:%"));

    private async Task<JsonElement> Single(Guid party)
    {
        var id = await fixture.Database.ScalarAsOwner<Guid>("SELECT id FROM communication.notification WHERE recipient_party_id = @p ORDER BY id DESC LIMIT 1", ("p", party));
        return await (await _staff.GetAsync($"/api/v1/communications/notifications/{id}")).Json();
    }

    private async Task<Guid> Party(string surname, string? email, string? mobile, string? language)
    {
        var party = await _staff.RegisterPerson(surname);
        foreach (var (type, value) in new[] { ("EMAIL", email), ("MOBILE", mobile) })
        {
            if (value is not null)
            {
                var version = (await _staff.GetAsync($"/api/v1/party/parties/{party}")).ETag();
                (await _staff.Send(HttpMethod.Post, $"/api/v1/party/parties/{party}/contact-points", new { channelType = type, value, isPrimary = true }, ifMatch: version))
                    .StatusCode.ShouldBe(HttpStatusCode.NoContent);
            }
        }

        if (language is not null)
        {
            var etag = (await _staff.GetAsync($"/api/v1/party/parties/{party}")).ETag();
            (await _staff.Send(HttpMethod.Put, $"/api/v1/party/parties/{party}/preferred-language", new { language }, ifMatch: etag)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        return party;
    }

    private async Task<HttpClient> Member(string subject, Guid party)
    {
        var user = await fixture.Database.UserWithPermissions(Tenant, subject, CommunicationPermissions.NotificationRead, CommunicationPermissions.PreferenceRead,
            CommunicationPermissions.PreferenceUpdate);
        await fixture.Database.ExecuteAsOwner("""
            INSERT INTO iam.user_subject_link (tenant_id, id, row_version, created_at, created_by, updated_at, updated_by, user_id, kind, party_id,
                                               status, requested_by, decided_by, decided_at)
            VALUES (@t, gen_random_uuid(), 1, now(), 'seed', now(), 'seed', @u, 'PARTY', @p, 'ACTIVE', gen_random_uuid(), gen_random_uuid(), now())
            """, ("t", Tenant), ("u", user), ("p", party));
        await fixture.Database.ExecuteAsOwner("""
            INSERT INTO channel.cfg_channel_definitions (tenant_id, code, version, effective_from, record_status, created_by, channel_code, audience, name, client_ids)
            VALUES (@t, 'CH-MEM', 1, @f, 'EFFECTIVE', 'seed', 'MEM', 'CUSTOMER', 'Member portal', ARRAY['member-portal']) ON CONFLICT DO NOTHING
            """, ("t", Tenant), ("f", From));
        return _factory.ClientFor(Tenant, subject, "member-portal");
    }

    private async Task<HttpResponseMessage> Callback(string path, string secret, string body, string callbackId)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Callback-Timestamp", timestamp);
        request.Headers.Add("X-Callback-Id", callbackId);
        request.Headers.Add("X-Signature", CallbackKeys.Sign(secret, timestamp, callbackId, body));
        return await _factory.CreateClient().SendAsync(request);
    }

    private Task Seed(string table, string code, params (string Column, object Value)[] fields) =>
        fixture.Database.SeedStatic(Tenant, table, "EFFECTIVE", code, From, fields);

    private Task Type(string type, string category, string? consent, string[] channels, bool portalVisible) =>
        Seed("communication.cfg_notification_types", type, ("type_code", type), ("category", category), ("consent_purpose", (object?)consent ?? DBNull.Value),
            ("channels", channels), ("portal_visible", portalVisible));

    private Task Template(string type, string channel, string language, string? subject, string body) =>
        Seed("communication.cfg_notification_templates", $"{type}-{channel}-{language}", ("type_code", type), ("channel", channel), ("language", language),
            ("subject", (object?)subject ?? DBNull.Value), ("body", body));

    private static object TemplateBody(string code, string type, string channel, string? subject, string body) =>
        new { code, typeCode = type, channel, language = "en", subject, body, changeReason = "test" };
}
