/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : ClaimsFramework
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

namespace Insys.ClaimsFramework.Domain.Claims;

public enum MovementType
{
    Reserve,
    Payment,
    Recovery,
}

public sealed record ClaimReserveChanged(Guid MovementId, string ClaimNumber, string Domain, string ReserveType, decimal Change, decimal Outstanding, string Currency,
    DateOnly MovementDate, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record ClaimRecoveryRecorded(Guid MovementId, string ClaimNumber, string Domain, string ReserveType, decimal Amount, string Currency, DateOnly MovementDate,
    DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// Entity FinancialMovement (claims.claim_financial_movement): a reserve change, a payment or a recovery. Append-only;
/// a correction is a new movement (V0014 refuses UPDATE and DELETE). Each is the AggregateRoot it is stored as, so its
/// events reach the outbox with it.
/// </summary>
public sealed class FinancialMovement : AggregateRoot<Guid>
{
    private FinancialMovement()
    {
    }

    public string ClaimNumber { get; private set; } = "";

    public MovementType MovementType { get; private set; }

    /// <summary>A payment made less the policy's arrears (OQ-137): what was deducted. <see cref="Amount"/> is the gross.</summary>
    public decimal? PremiumOffset { get; private set; }

    /// <summary>A payment made less the policy's arrears (OQ-137): what the payee received.</summary>
    public decimal? NetAmount { get; private set; }

    internal void NetOf(decimal premiumOffset)
    {
        PremiumOffset = premiumOffset;
        NetAmount = Amount - premiumOffset;
    }

    public string ReserveType { get; private set; } = "";

    /// <summary>Signed for a reserve (positive raises it); positive for a payment or recovery.</summary>
    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = "";

    public DateTimeOffset MovementAt { get; private set; }

    public Guid? SubledgerJournalId { get; private set; }

    internal static FinancialMovement Create(Guid id, string claimNumber, MovementType type, string reserveType, decimal amount, string currency, DateTimeOffset now)
    {
        var movement = new FinancialMovement
        {
            Id = Guard.NotEmpty(id), ClaimNumber = claimNumber, MovementType = type, ReserveType = Guard.MaxLength(reserveType, 15), Amount = amount, Currency = currency,
            MovementAt = now,
        };
        movement.MarkChanged();
        return movement;
    }

    internal void Announce(IDomainEvent domainEvent) => Raise(domainEvent);
}

/// <summary>
/// The ClaimFinancials aggregate: a claim's reserves, payments and recoveries as traceable financial movements
/// (CLM-004). Append-only; the outstanding reserve of each type never goes below zero.
/// </summary>
public sealed class ClaimFinancials
{
    private readonly List<FinancialMovement> _movements;

    public ClaimFinancials(string claimNumber, string domain, string currency, IEnumerable<FinancialMovement> movements)
    {
        ClaimNumber = claimNumber;
        Domain = domain;
        Currency = currency;
        _movements = movements.Where(m => m.ClaimNumber == claimNumber).ToList();
    }

    public string ClaimNumber { get; }

    public string Domain { get; }

    public string Currency { get; }

    public IReadOnlyList<FinancialMovement> Movements => _movements;

    /// <summary>Reserves set or changed less payments made, per reserve type.</summary>
    public decimal Outstanding(string reserveType) =>
        _movements.Where(m => m.ReserveType == reserveType && m.MovementType == MovementType.Reserve).Sum(m => m.Amount)
        - _movements.Where(m => m.ReserveType == reserveType && m.MovementType == MovementType.Payment).Sum(m => m.Amount);

    /// <summary>Command SetReserve: the reserve becomes the amount; the movement is the difference.</summary>
    public Result<FinancialMovement> SetReserve(string reserveType, decimal amount, DateOnly day, DateTimeOffset now) =>
        ChangeReserve(reserveType, amount - Outstanding(reserveType), day, now);

    /// <summary>Command ChangeReserve by a signed amount; never below zero.</summary>
    public Result<FinancialMovement> ChangeReserve(string reserveType, decimal change, DateOnly day, DateTimeOffset now)
    {
        if (change == 0 || decimal.Round(change, 2) != change)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "A reserve change is not zero, to two decimal places.");
        }

        var outstanding = Outstanding(reserveType) + change;
        if (outstanding < 0)
        {
            return Error.Validation(ClaimsErrorCodes.ReserveBelowZero, $"The {reserveType} reserve would be {outstanding} {Currency}; it never goes below zero.");
        }

        var movement = Add(MovementType.Reserve, reserveType, change, now);
        movement.Announce(new ClaimReserveChanged(movement.Id, ClaimNumber, Domain, reserveType, change, outstanding, Currency, day, now));
        return movement;
    }

    /// <summary>
    /// Command RecordPayment: a completed claim payment draws down its reserve, which never goes below zero. The movement
    /// is the gross the claim paid; one made less the policy's arrears (OQ-137) also keeps the deduction and the net paid.
    /// </summary>
    public Result<FinancialMovement> RecordPayment(Guid movementId, string reserveType, decimal amount, DateTimeOffset now, decimal premiumOffset = 0m)
    {
        if (amount > Outstanding(reserveType))
        {
            return Error.Validation(ClaimsErrorCodes.ReserveBelowZero, $"A payment of {amount} {Currency} exceeds the outstanding {reserveType} reserve.");
        }

        var movement = Add(MovementType.Payment, reserveType, amount, now, movementId);
        if (premiumOffset > 0)
        {
            movement.NetOf(premiumOffset);
        }

        return movement;
    }

    /// <summary>Command RecordRecovery: money recovered (salvage, subrogation).</summary>
    public Result<FinancialMovement> RecordRecovery(string reserveType, decimal amount, DateOnly day, DateTimeOffset now)
    {
        if (amount <= 0)
        {
            return Error.Validation(ErrorCodes.ValidationFailed, "A recovery is a positive amount.");
        }

        var movement = Add(MovementType.Recovery, reserveType, amount, now);
        movement.Announce(new ClaimRecoveryRecorded(movement.Id, ClaimNumber, Domain, reserveType, amount, Currency, day, now));
        return movement;
    }

    private FinancialMovement Add(MovementType type, string reserveType, decimal amount, DateTimeOffset now, Guid? id = null)
    {
        var movement = FinancialMovement.Create(id ?? Guid.CreateVersion7(), ClaimNumber, type, reserveType, amount, Currency, now);
        _movements.Add(movement);
        return movement;
    }
}
