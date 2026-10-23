/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Audit
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
namespace Insys.Audit.Contracts;

/// <summary>A retention period from audit.cfg_audit_retention_policies.</summary>
public sealed record RetentionPeriod(int Years, int Months, int Days)
{
    /// <summary>The earliest moment still within the period, counted back from <paramref name="now"/>.</summary>
    public DateTimeOffset Before(DateTimeOffset now) => now.AddYears(-Years).AddMonths(-Months).AddDays(-Days);

    public override string ToString() => $"P{Years}Y{Months}M{Days}D";
}

/// <summary>
/// Public query interface: the retention period of an event class (SECURITY, FINANCIAL, OPERATIONAL) in force for the
/// caller's tenant. A tenant's own policy overrides the global ones; where several jurisdictions apply, the longest
/// period wins, so nothing is deleted early. Null when none is in force. Read on Audit's own connection.
/// </summary>
public interface IAuditRetention
{
    Task<RetentionPeriod?> Period(string eventClass, DateOnly day, CancellationToken cancellationToken);
}

public static class RetentionClasses
{
    public const string Security = "SECURITY";
    public const string Financial = "FINANCIAL";
    public const string Operational = "OPERATIONAL";
}
