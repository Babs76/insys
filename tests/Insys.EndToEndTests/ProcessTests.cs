/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : EndToEndTests
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
namespace Insys.EndToEndTests;

/// <summary>
/// The six cross-context processes in domain-model.json, end to end with their compensations: product publication in
/// <see cref="ProductPublicationTests"/>, the General processes in <see cref="GeneralProcessTests"/>, the Life processes
/// in <see cref="LifeProcessTests"/> and the Health process in <see cref="HealthProcessTests"/>.
/// </summary>
public sealed class ProcessTests
{
    [Fact]
    public void DomainModel_ListsExactlyTheseSixProcesses()
    {
        using var model = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(Insys.Testing.RepositoryPaths.Specs, "domain-model.json")));
        model.RootElement.GetProperty("processes").EnumerateArray().Select(p => p.GetProperty("name").GetString()).ShouldBe(
        [
            "General quote to cash", "Life proposal to policy", "General claim to settlement",
            "Health care to provider payment", "Product publication", "Missed premium to lapse",
        ]);
    }
}
