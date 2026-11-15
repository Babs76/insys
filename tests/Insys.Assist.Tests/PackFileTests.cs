/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Assist.Tests
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
using System.Text.Json;
using Insys.Assist.Application;
using Insys.Assist.Domain;
using Shouldly;
using Xunit;

namespace Insys.Assist.Tests;

/// <summary>Batch 7.6: a pack file read with its marks' sources (D-20), and the content hash an approval binds to (§5.3).</summary>
public sealed class PackFileTests
{
    private static PackFile Read(string json) => PackFile.Read(JsonDocument.Parse(json).RootElement).Value;

    [Fact]
    public void TheHash_IsTheSame_ForTheSameValuesInAnyKeyOrder_AndChangesWithAMarkOrAValue()
    {
        var a = Read("""{"pack":"P","kind":"LINE","rows":[{"table":"t","code":"A","class":"FINANCE","values":{"rate":0.15,"cap":100}}]}""");
        var b = Read("""{"pack":"P","kind":"LINE","rows":[{"table":"t","code":"A","class":"FINANCE","values":{"cap":100,"rate":0.15}}]}""");
        var marked = Read("""{"pack":"P","kind":"LINE","rows":[{"table":"t","code":"A","class":"FINANCE","regulated":true,"values":{"rate":0.15,"cap":100}}]}""");
        var changed = Read("""{"pack":"P","kind":"LINE","rows":[{"table":"t","code":"A","class":"FINANCE","values":{"rate":0.16,"cap":100}}]}""");
        a.Hash(1, PackScopes.AllTenants).ShouldBe(b.Hash(1, PackScopes.AllTenants));
        marked.Hash(1, PackScopes.AllTenants).ShouldNotBe(a.Hash(1, PackScopes.AllTenants), "the same mark, but now explicit: the reviewer confirmed something else");
        changed.Hash(1, PackScopes.AllTenants).ShouldNotBe(a.Hash(1, PackScopes.AllTenants));
        a.Hash(2, PackScopes.AllTenants).ShouldNotBe(a.Hash(1, PackScopes.AllTenants));
        a.Hash(1, PackScopes.TenantOnly).ShouldNotBe(a.Hash(1, PackScopes.AllTenants));
    }

    [Fact]
    public void ACountryOrLinePack_TakesItsCountryOrLineFromItsCode()
    {
        (Read("""{"pack":"NG","kind":"COUNTRY","rows":[{"table":"t","code":"A","class":"OPERATIONAL","values":{}}]}""").Country).ShouldBe("NG");
        (Read("""{"pack":"GENERAL_MOTOR","kind":"LINE","rows":[{"table":"t","code":"A","class":"OPERATIONAL","values":{}}]}""").Line).ShouldBe("GENERAL");
    }

    [Theory]
    [InlineData("""{"kind":"LINE","rows":[]}""")]
    [InlineData("""{"pack":"P","kind":"OTHER","rows":[{"table":"t","code":"A","class":"FINANCE","values":{}}]}""")]
    [InlineData("""{"pack":"P","kind":"LINE","rows":[{"table":"t","code":"A","class":"SECRET","values":{}}]}""")]
    [InlineData("""{"pack":"P","kind":"LINE","rows":[{"table":"t","code":"A","class":"FINANCE","tailorable":"maybe","values":{}}]}""")]
    public void AMalformedFile_IsRefused(string json) => PackFile.Read(JsonDocument.Parse(json).RootElement).IsFailure.ShouldBeTrue();

    [Fact]
    public void AVersion_ForEveryTenant_IsApprovedOnlyAfterReview_ByNeitherItsPreparerNorItsReviewer_AndOnlyForTheHashItHas()
    {
        var pack = StarterPack.Load("P", PackKinds.Line, 1, null, null, "GENERAL", PackScopes.AllTenants, "h", 1, "loader").Value;
        pack.Approve("lead", "h", PackScopes.AllTenants, DateTimeOffset.UnixEpoch).Error!.Code.ShouldBe("INVALID_STATE_TRANSITION");
        pack.MarkReviewed("reviewer", "Partner", "Name", new DateOnly(2026, 11, 14), Guid.CreateVersion7()).IsSuccess.ShouldBeTrue();
        pack.Approve("loader", "h", PackScopes.AllTenants, DateTimeOffset.UnixEpoch).Error!.Code.ShouldBe("MAKER_CHECKER_SAME_PERSON");
        pack.Approve("reviewer", "h", PackScopes.AllTenants, DateTimeOffset.UnixEpoch).Error!.Code.ShouldBe("MAKER_CHECKER_SAME_PERSON");
        pack.Approve("lead", "other", PackScopes.AllTenants, DateTimeOffset.UnixEpoch).Error!.Code.ShouldBe("ASSIST_PACK_CHANGED");
        pack.Approve("lead", "h", PackScopes.TenantOnly, DateTimeOffset.UnixEpoch).Error!.Code.ShouldBe("FORBIDDEN", "a tenant's officer can't approve a platform version");
        pack.Approve("lead", "h", PackScopes.AllTenants, DateTimeOffset.UnixEpoch).IsSuccess.ShouldBeTrue();
        pack.Status.ShouldBe(PackStatuses.Approved);
    }
}
