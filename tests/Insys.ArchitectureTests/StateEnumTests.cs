/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : ArchitectureTests
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
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Insys.ArchitectureTests;

/// <summary>
/// The state enums the API declares (GAP-008) hold the states of domain-model.json, written as C# names ("In force" is
/// InForce). A difference must be one recorded below with its reason, so neither side drifts unnoticed.
/// </summary>
public sealed partial class StateEnumTests
{
    /// <summary>domain-model.json context and aggregate → the module and enum that carries its state.</summary>
    private static readonly (string Context, string Aggregate, string Module, string Enum)[] Map =
    [
        ("life", "LifeProposal", "Life", "ProposalStatus"),
        ("life", "LifeUnderwritingCase", "Life", "CaseStatus"),
        ("life", "LifePolicy", "Life", "LifePolicyStatus"),
        ("life", "LifePolicyLoan", "Life", "LoanStatus"),
        ("life", "LifeClaim", "Life", "LifeClaimStatus"),
        ("life", "GroupScheme", "Life", "SchemeStatus"),
        ("general", "GeneralQuote", "General", "QuoteStatus"),
        ("general", "GeneralProposal", "General", "ProposalStatus"),
        ("general", "GeneralRiskObject", "General", "RiskObjectStatus"),
        ("general", "GeneralPolicy", "General", "PolicyStatus"),
        ("general", "GeneralEndorsement", "General", "EndorsementStatus"),
        ("general", "GeneralClaim", "General", "ClaimStatus"),
        ("general", "LossAssessment", "General", "AssessmentStatus"),
        ("general", "RecoveryCase", "General", "RecoveryStatus"),
        ("health", "HealthScheme", "Health", "SchemeStatus"),
        ("health", "HealthMembership", "Health", "MembershipStatus"),
        ("health", "Preauthorisation", "Health", "PreauthStatus"),
        ("health", "MedicalClaim", "Health", "MedicalClaimStatus"),
        ("health", "UtilisationCase", "Health", "UtilisationStatus"),
        ("health", "ProviderPaymentRun", "Health", "RunStatus"),
        ("health", "HealthFraudCase", "Health", "FraudStatus"),
        ("product", "ProductVersion", "ProductFactory", "ProductVersionStatus"),
        ("party", "Party", "Party", "PartyStatus"),
        ("claims", "ClaimPaymentInstruction", "ClaimsFramework", "InstructionStatus"),
        ("workflow", "ProcessInstance", "Workflow", "ProcessStatus"),
        ("workflow", "Approval", "Workflow", "ApprovalStatus"),
        ("document", "Document", "Documents", "DocumentStatus"),
        ("sdm", "StaticDataChange", "StaticData", "ChangeStatus"),
        ("sdm", "ImportBatch", "StaticData", "ImportStatus"),
        ("billing", "BillingAccount", "Billing", "AccountStatus"),
        ("billing", "Invoice", "Billing", "InvoiceStatus"),
        ("billing", "RefundRequest", "Billing", "RefundStatus"),
        ("payments", "Payment", "Payments", "PaymentStatus"),
        ("payments", "PayoutBatch", "Payments", "BatchStatus"),
        ("payments", "ReconciliationException", "Payments", "ExceptionStatus"),
        ("finance", "AccountingPeriod", "Finance", "PeriodStatus"),
        ("commission", "Intermediary", "Distribution", "IntermediaryStatus"),
        ("commission", "CommissionEntitlement", "Distribution", "EntitlementStatus"),
        ("commission", "CommissionStatement", "Distribution", "StatementStatus"),
        ("reinsurance", "ReinsuranceRecovery", "Reinsurance", "RecoveryStatus"),
        ("reinsurance", "Bordereau", "Reinsurance", "BordereauStatus"),
        ("compliance", "RegulatoryReturn", "Compliance", "ReturnStatus"),
        ("integration", "DeadLetter", "Integration", "DeadLetterStatus"),
        ("integration", "WebhookDelivery", "Integration", "WebhookDeliveryStatus"),
        ("tenant", "Tenant", "Tenancy", "TenantStatus"),
        ("iam", "UserAccount", "Identity", "UserStatus"),
        ("iam", "PrivilegedAccessGrant", "Identity", "GrantStatus"),
        ("iam", "AccessReview", "Identity", "ReviewStatus"),
    ];

    /// <summary>Recorded differences: (aggregate, state, "missing" from the enum or "extra" in it) and why.</summary>
    private static readonly Dictionary<(string Aggregate, string State, string Side), string> Recorded = new()
    {
        [("Document", "Requested", "missing")] = "The Requested state is document.generation_request, before a document exists.",
        [("StaticDataChange", "Draft", "missing")] = "The Draft state is the draft row in the owning module's table; a change request starts at submission.",
        [("PrivilegedAccessGrant", "Revoked", "extra")] = "OQ-041: V0007's CHECK allows REVOKED; domain-model.json has no transition to it.",
        [("Approval", "NotApplied", "extra")] = "D-5, extended (25 October 2026, Ade): a granted approval its record will not take is NotApplied; domain-model.json has no such state.",
        [("PayoutBatch", "Rejected", "extra")] = "D-1 (Wave 2 batch 1, signed off): an approval that is not granted ends the batch as Rejected; domain-model.json has no reject transition.",
        [("ProviderPaymentRun", "Rejected", "extra")] = "D-2 (Wave 2 batch 1, signed off): an approval that is not granted ends the run as Rejected; domain-model.json has no reject transition.",
        [("UserAccount", "PendingIdentity", "extra")] = "keycloak-user-provisioning (signed off 24 October 2026) §4: a user of INSYS's own realm waits for its Keycloak user.",
    };

    [Fact]
    public void StateEnums_HoldTheDomainModelStates_GAP008()
    {
        using var model = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), "docs", "specs", "domain-model.json")));
        var aggregates = model.RootElement.GetProperty("contexts").EnumerateArray()
            .SelectMany(c => c.GetProperty("aggregates").EnumerateArray().Select(a => (Context: c.GetProperty("id").GetString()!, Aggregate: a)))
            .ToDictionary(x => (x.Context, x.Aggregate.GetProperty("name").GetString()!), x => x.Aggregate);

        var differences = new List<string>();
        foreach (var (context, aggregate, module, enumName) in Map)
        {
            var states = aggregates[(context, aggregate)].GetProperty("stateTransitions").EnumerateArray()
                .SelectMany(t => new[] { t.GetProperty("from").GetString()!, t.GetProperty("to").GetString()! })
                .Select(CSharpName).Distinct(StringComparer.Ordinal).ToList();
            var type = Solution.Assemblies.Single(a => a.GetName().Name == $"Insys.{module}.Domain").GetTypes()
                .Single(t => t.IsEnum && t.Name == enumName);
            var names = Enum.GetNames(type);

            differences.AddRange(states.Except(names).Where(s => !Recorded.ContainsKey((aggregate, s, "missing")))
                .Select(s => $"{aggregate}: {s} is missing from {module}.{enumName}"));
            differences.AddRange(names.Except(states).Where(n => !Recorded.ContainsKey((aggregate, n, "extra")))
                .Select(n => $"{aggregate}: {module}.{enumName}.{n} is not a domain-model.json state"));
        }

        differences.ShouldBeEmpty();
    }

    [Fact]
    public void EveryAggregateWithStates_IsMapped_OrHasNoStateInTheApi()
    {
        // Aggregates whose state the API does not expose as an enum yet, with the reason.
        string[] unmapped =
        [
            "MaturityCase",       // Life maturities are not built (OQ-052).
            "BenefitAccumulator", // Its state is not exposed by any endpoint.
            "Journal",            // OQ-057: a journal stays POSTED; a reversal is a new journal.
            "ReconciliationRun",  // Its state is not exposed by any endpoint.
        ];
        using var model = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), "docs", "specs", "domain-model.json")));
        var withStates = model.RootElement.GetProperty("contexts").EnumerateArray()
            .SelectMany(c => c.GetProperty("aggregates").EnumerateArray())
            .Where(a => a.GetProperty("stateTransitions").GetArrayLength() > 0)
            .Select(a => a.GetProperty("name").GetString()!);

        withStates.Except(Map.Select(m => m.Aggregate)).Except(unmapped).ShouldBeEmpty();
    }

    /// <summary>"In force" → InForce, "Dead-lettered" → DeadLettered.</summary>
    private static string CSharpName(string state) =>
        string.Concat(Separators().Split(state).Where(w => w.Length > 0).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "docs", "specs", "domain-model.json")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [GeneratedRegex(@"[\s\-]+")]
    private static partial Regex Separators();
}
