/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Payments
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
namespace Insys.Payments.Contracts;

/// <summary>Whether a tenant's payment configuration names a provider this environment refuses, for the Tenancy module's reactivation.</summary>
public interface ITenantPaymentProviders
{
    /// <summary>The error code of the refusal, the same as Payments' own.</summary>
    public const string ProviderNotAllowed = "PAYMENT_PROVIDER_NOT_ALLOWED";

    /// <summary>True when the tenant's provider register or callback endpoints name the simulator and it isn't allowed here.</summary>
    Task<bool> NamesARefusedProvider(Guid tenantId, CancellationToken cancellationToken);
}
