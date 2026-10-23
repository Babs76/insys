/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Tests
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
using Insys.BuildingBlocks.Domain;

namespace Insys.BuildingBlocks.Tests;

public sealed class ReferencesTests
{
    /// <summary>UUIDv7 IDs made in the same millisecond share their leading digits; their references must not.</summary>
    [Fact]
    public void References_FromIdsMadeTogether_AreDistinct_AndKeepThePrefixAndLength()
    {
        var references = Enumerable.Range(0, 5_000).Select(_ => References.Of("HM", Guid.CreateVersion7(), 12)).ToList();
        references.Distinct().Count().ShouldBe(5_000);
        references.ShouldAllBe(r => r.Length == 12 && r.StartsWith("HM", StringComparison.Ordinal) && r == r.ToUpperInvariant());
        References.Of("A-VERY-LONG-PREFIX", Guid.CreateVersion7(), 10).ShouldBe("A-VERY-LON");
        References.Of("MC", Guid.CreateVersion7(), 25).Length.ShouldBe(25);
    }
}
