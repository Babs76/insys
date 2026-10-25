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
using Insys.BuildingBlocks.Infrastructure.StaticData;
using Insys.Integration.Infrastructure.StaticData;

namespace Insys.Integration.Infrastructure.Channels;

/// <summary>ADR-026 §5: a channel transaction row names the function it allows; transaction_type is retired.</summary>
internal sealed class ChannelRowValidator : IStaticRecordValidator
{
    public string Schema => "channel";

    public string Table => "cfg_channel_transaction_permissions";

    public string Rules => "functionCode is required (an ACTION function); transactionType is retired (ADR-026 §5) and must be empty.";

    public IReadOnlyDictionary<string, string[]> Validate(StaticRecord record)
    {
        var errors = new Dictionary<string, string[]>();
        if (record is ChannelTransactionPermissions row)
        {
            if (string.IsNullOrEmpty(row.FunctionCode))
            {
                errors["functionCode"] = ["Required: the function the row allows (ADR-026 §5)."];
            }

            if (!string.IsNullOrEmpty(row.TransactionType))
            {
                errors["transactionType"] = ["Retired: name the function (ADR-026 §5)."];
            }
        }

        return errors;
    }
}
