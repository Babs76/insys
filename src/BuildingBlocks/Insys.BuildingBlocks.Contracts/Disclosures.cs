/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : BuildingBlocks.Contracts
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
namespace Insys.BuildingBlocks.Contracts;

/// <summary>
/// One mandatory notice not yet acknowledged at a display point (F060, DE-039): what the console needs to open its text already filled in
/// (GET /integration/disclosures or /portal/disclosures with the display point and these values, nothing typed). The jurisdiction is the
/// party's country, else the tenant's. The language (Ade, 25 November 2026) is the party's preferred one if a text is in force in it, else the
/// tenant's default if one is, else the only language the notice is in force in; with several and none of those, none is chosen (Language and
/// TextVersion null) and the person picks one of AvailableLanguages. TextVersion is the version in force of the text in the chosen language.
/// </summary>
/// <param name="AvailableLanguages">The languages the notice is in force in, by code; always present.</param>
public sealed record DisclosureNotice(string NoticeType, string Jurisdiction, string? Language, int? TextVersion, IReadOnlyList<string> AvailableLanguages);
