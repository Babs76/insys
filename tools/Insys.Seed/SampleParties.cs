/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Seed
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
namespace Insys.Seed;

public sealed record SamplePerson(string FirstNames, string Surname, string BirthDate, string IdNumber, string Email, string Mobile);

/// <summary>
/// The sample tenant's invented people and companies. "Sample" is in every surname and company name. Nothing here is meant to match a real person; docs/plans/workstream-f-oftza-seed.md says how far that goes.
/// </summary>
public static class SampleParties
{
    private static readonly (string First, string Last)[] Names =
    [
        ("Sipho", "Vilakazi-Sample"),
        ("Lerato", "Mokoena-Sample"),
        ("Johan", "Pretorius-Sample"),
        ("Aisha", "Patel-Sample"),
        ("Thabo", "Nkosi-Sample"),
        ("Naledi", "Khumalo-Sample"),
        ("Pieter", "Botha-Sample"),
        ("Zanele", "Dube-Sample"),
        ("Ravi", "Naidoo-Sample"),
        ("Karin", "Fourie-Sample"),
        ("Mandla", "Zulu-Sample"),
        ("Palesa", "Molefe-Sample"),
        ("Andre", "Steyn-Sample"),
        ("Nomsa", "Sithole-Sample"),
        ("Imran", "Khan-Sample"),
        ("Lindiwe", "Ngcobo-Sample"),
        ("Hennie", "Marais-Sample"),
        ("Busi", "Mthembu-Sample"),
        ("Kagiso", "Tau-Sample"),
        ("Elna", "Swart-Sample"),
    ];

    public static readonly string[] Companies =
    [
        "Acacia Sample Holdings (Pty) Ltd", "Protea Sample Logistics (Pty) Ltd", "Baobab Sample Foods (Pty) Ltd", "Karoo Sample Mining (Pty) Ltd", "Fynbos Sample Retail (Pty) Ltd",
    ];

    public static IReadOnlyList<SamplePerson> People { get; } = Names.Select((n, i) => new SamplePerson(n.First, n.Last, $"{1960 + i}-0{(i % 9) + 1}-15", IdNumber(i), $"sample.{i + 1}@example.test", Mobile(i))).ToList();

    /// <summary>
    /// 13 digits with a valid South African checksum, that Home Affairs wouldn't issue: its date is an impossible 30 February, with a citizenship digit of 9, which is neither of those in use (0 and 1).
    /// INSYS checks only the format, so nothing parses this date; a party's birth date is its own field.
    /// </summary>
    public static string IdNumber(int index)
    {
        var body = $"{60 + (index % 40):00}0230{5000 + index:0000}98";
        return body + LuhnDigit(body);
    }

    /// <summary>+27 then a 0: a national number after a country code never starts with 0, so it can't be dialled, on any network.</summary>
    public static string Mobile(int index) => $"+2700{5550100 + index}";

    public static char LuhnDigit(string digits)
    {
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var d = digits[digits.Length - 1 - i] - '0';
            sum += i % 2 == 0 ? (d * 2 > 9 ? (d * 2) - 9 : d * 2) : d;
        }

        return (char)('0' + ((10 - (sum % 10)) % 10));
    }
}
