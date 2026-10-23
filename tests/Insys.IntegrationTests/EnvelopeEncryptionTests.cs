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
using Insys.BuildingBlocks.Application.Security;
using Insys.BuildingBlocks.Infrastructure.Security;
using Insys.Party.Contracts;
using Insys.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Insys.IntegrationTests;

/// <summary>
/// Envelope encryption through the API (FieldProtection:Provider "envelope", ADR-018) with the local stand-in KMS: the
/// host creates the first data key at startup, encrypted fields carry its ID, and the register holds only ciphertext.
/// </summary>
public sealed class EnvelopeEncryptionTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private static readonly Dictionary<string, string?> Envelope = new()
    {
        ["FieldProtection:Provider"] = "envelope",
        ["FieldProtection:Envelope:Kms"] = "local",
        ["FieldProtection:Envelope:LocalMasterKey"] = Convert.ToBase64String(Enumerable.Range(100, 32).Select(i => (byte)i).ToArray()),
    };

    [Fact]
    public async Task IdentifierNumber_IsEncryptedWithTheCurrentDataKey_AndTheRegisterHoldsOnlyItsCiphertext()
    {
        var factory = await fixture.Host(Envelope);
        await fixture.Database.ExecuteAsOwner("""
            INSERT INTO party.ref_identity_document_types (tenant_id, code, version, effective_from, record_status, created_by, change_reason, country, doc_type, label, format_pattern)
            VALUES (@t, 'ZA-NID', 1, current_date - 1, 'ACTIVE', 'seed', 'test seed', 'ZA', 'NATIONAL_ID', 'National identity number', '^[0-9]{13}$')
            ON CONFLICT DO NOTHING
            """, ("t", DatabaseFixture.TenantA));
        await fixture.Database.UserWithPermissions(DatabaseFixture.TenantA, "envelope-user", PartyPermissions.PartyCreate, PartyPermissions.PartyRead, PartyPermissions.PartyUpdate);
        var client = factory.ClientFor(DatabaseFixture.TenantA, "envelope-user");
        var party = await client.RegisterPerson("Envelope");

        var added = await client.Send(HttpMethod.Post, $"/api/v1/party/parties/{party}/identifiers", new { docType = "NATIONAL_ID", country = "ZA", number = "8001015009087" },
            ifMatch: "\"1\"");
        added.StatusCode.ShouldBe(HttpStatusCode.Created, await added.Content.ReadAsStringAsync());

        var keys = await fixture.Database.ScalarAsOwner<long>("SELECT count(*) FROM integration.field_data_key");
        keys.ShouldBe(1L);
        var keyId = await fixture.Database.ScalarAsOwner<string>("SELECT key_id FROM integration.field_data_key");
        var stored = await fixture.Database.ScalarAsOwner<byte[]>("SELECT number_enc FROM party.party_identifier WHERE party_id = @p", ("p", party));
        FieldCipher.KeyIdOf(stored!).ShouldBe(keyId);
        Encoding.UTF8.GetString(stored!).ShouldNotContain("8001015009087");
        factory.Services.GetRequiredService<IFieldProtector>().Unprotect(stored!).ShouldBe("8001015009087");
    }
}
