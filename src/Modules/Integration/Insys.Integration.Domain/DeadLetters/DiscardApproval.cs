/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Integration
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
namespace Insys.Integration.Domain.DeadLetters;

/// <summary>
/// DE-007 (Ade, 24 November 2026): which dead letters are "money", whose discard needs a second person's approval (DEAD_LETTER_DISCARD) before
/// it takes effect. Discarding one gives up a payment, a posting or a recovery for good, so a second person must agree. Every other dead
/// letter is discarded at once by the operator.
/// <list type="bullet">
/// <item>The message came from the outbox of Payments (payment), Billing (billing), Finance (finance), Claims Framework (claims) or
/// Distribution (commission): its Source is "outbox:&lt;schema&gt;".</item>
/// <item>Or it is an event of Health or Workflow (event type "health.*" or "workflow.*") whose name contains Payment, Remittance, Recovery,
/// CreditNote or Redress.</item>
/// </list>
/// A dead letter the delivery ledger made (Source "ledger:&lt;consumer&gt;") names its consumer, not where the event came from, so its event
/// type's module (the prefix, which is the publishing module's schema) stands for the outbox. TODO(spec): confirm with Ade that a
/// ledger-made dead letter of a money module's event is money too (DE-007 names only outbox sources).
/// </summary>
public static class DiscardApproval
{
    /// <summary>The approval type Workflow decides (workflow.cfg_delegation_and_approval_authority.transaction_type).</summary>
    public const string ApprovalType = "DEAD_LETTER_DISCARD";

    /// <summary>The business reference type of the approval: the dead letter's ID is its reference.</summary>
    public const string BusinessRefType = "DEAD_LETTER";

    /// <summary>The outbox schemas whose messages move money: Payments, Billing, Finance, Claims Framework and Distribution.</summary>
    public static readonly IReadOnlyList<string> MoneySchemas = ["payment", "billing", "finance", "claims", "commission"];

    /// <summary>The modules some of whose events move money, named by their event type's prefix.</summary>
    public static readonly IReadOnlyList<string> MixedModules = ["health", "workflow"];

    /// <summary>The words in an event's name that mark a Health or Workflow event as money.</summary>
    public static readonly IReadOnlyList<string> MoneyWords = ["Payment", "Remittance", "Recovery", "CreditNote", "Redress"];

    private const string OutboxPrefix = "outbox:";
    private const string LedgerPrefix = "ledger:";

    /// <param name="source">The dead letter's Source, for example "outbox:payment".</param>
    /// <param name="eventType">The event type of the envelope it holds (for example "health.ProviderPaymentRunReleased"), or null when it holds none.</param>
    public static bool NeedsSecondPerson(string source, string? eventType)
    {
        var (module, name) = Split(eventType);
        string? origin = source.StartsWith(OutboxPrefix, StringComparison.Ordinal) ? source[OutboxPrefix.Length..]
            : source.StartsWith(LedgerPrefix, StringComparison.Ordinal) ? module
            : null;
        if (origin is not null && MoneySchemas.Contains(origin, StringComparer.Ordinal))
        {
            return true;
        }

        return module is not null && name is not null && MixedModules.Contains(module, StringComparer.Ordinal)
            && MoneyWords.Any(word => name.Contains(word, StringComparison.Ordinal));
    }

    private static (string? Module, string? Name) Split(string? eventType)
    {
        var dot = eventType?.IndexOf('.') ?? -1;
        return dot > 0 && dot < eventType!.Length - 1 ? (eventType[..dot], eventType[(dot + 1)..]) : (null, null);
    }
}
