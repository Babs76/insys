/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Identity.Tests
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
using Insys.Identity.Domain.Users;
using Shouldly;
using Xunit;

namespace Insys.Identity.Tests;

/// <summary>No invitation or other identity email goes to a domain reserved for testing (RFC 2606, RFC 6761; Ade, 6 November 2026).</summary>
public sealed class ReservedEmailAddressesTests
{
    [Theory]
    [InlineData("admin01@example.test")]
    [InlineData("someone@mail.example.test")]
    [InlineData("a@b.example")]
    [InlineData("a@nowhere.invalid")]
    [InlineData("a@example.com")]
    [InlineData("a@EXAMPLE.ORG")]
    [InlineData("a@sub.example.net")]
    [InlineData("a@example.test.")]
    public void AnAddressUnderAReservedTestDomain_IsReserved_SoNothingIsSentToIt(string email) =>
        ReservedEmailAddresses.IsReserved(email).ShouldBeTrue();

    [Theory]
    [InlineData("insys-preview@openfactorgroup.com")]
    [InlineData("a@example.co.za")]
    [InlineData("a@test.com")]
    [InlineData("a@examples.com")]
    [InlineData("a@notexample.com")]
    [InlineData("a@contest")]
    [InlineData("")]
    [InlineData("no-at-sign")]
    [InlineData(null)]
    public void AnyOtherAddress_IsNotReserved(string? email) =>
        ReservedEmailAddresses.IsReserved(email).ShouldBeFalse();
}
