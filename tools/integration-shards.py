#!/usr/bin/env python3
# ============================================================================
# OPENFACTOR TECHNOLOGY GROUP
# CONFIDENTIAL AND PROPRIETARY SOFTWARE
# ============================================================================
# Copyright (c) 2026 OpenFactor Technology Group.
# All rights reserved.
# PRODUCT       : INSYS
# COMPONENT     : Tools
# CONFIDENTIALITY AND INTELLECTUAL PROPERTY NOTICE
# This source code and its associated documentation contain confidential and
# proprietary information belonging to OpenFactor Technology Group or its
# applicable intellectual property rights holder.
# This material is provided solely for authorised development, maintenance,
# operation or evaluation purposes, as applicable. No person may reproduce,
# modify, distribute, disclose, publish or otherwise use this material except
# as expressly permitted by an applicable written agreement or licence.
# No ownership rights or licence to any intellectual property are granted
# by the inclusion of this notice, except as expressly provided in an
# applicable written agreement or licence.
# Third-party software, open-source components and customer-owned materials
# remain subject to their respective ownership rights and applicable licences
# or agreements.
# UNAUTHORISED ACCESS, USE, COPYING OR DISCLOSURE IS PROHIBITED.
# ============================================================================
"""Splits the integration test classes into balanced shards for CI (Wave 1 batch 4).

Usage: integration-shards.py SHARD COUNT   prints the --filter-class arguments of shard SHARD (1-based) of COUNT.
       integration-shards.py --check COUNT  lists each shard and fails unless every class is in exactly one.
       integration-shards.py --check-listed COUNT  reads the compiled classes (the test assembly's "-list classes"
                                                    output) on stdin and fails unless each one is in a shard or excluded.

Every test class in tests/Insys.IntegrationTests is assigned, so a new class always runs. The endpoint sweeps run in
their own job and are left out. A class weighs its measured time (MEASURED: seconds per class from a full run's xUnit
XML report, -result-xml, with the classes running side by side as on CI, so only their ratios matter); a class not yet
measured is estimated from its tests. Classes are placed heaviest first on the lightest shard, so the split is stable.
Re-measure when the shards' CI times drift apart.
"""
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent / "tests" / "Insys.IntegrationTests"
EXCLUDED = {"EndpointSecuritySweepTests"}
# Measured on 27 October 2026 (wave2-3, 487 tests). A new class is estimated as ESTIMATE_BASE + ESTIMATE_PER_TEST * tests.
ESTIMATE_BASE = 60
ESTIMATE_PER_TEST = 50
MEASURED = {
    "AccessTests": 145, "ApprovalEndingsTests": 1934, "ApprovalKindsTests": 500, "ArrearsAgeingTests": 372,
    "AssignRoleBusinessDateTests": 1026, "AuditTargetTypeTests": 59, "BankAccountTests": 249,
    "BusinessDateTests": 784, "CaseConsumersTests": 854, "CaseTests": 260, "CaseTimelineBoundaryTests": 89,
    "ClaimsScopeTests": 155, "CommunicationsTests": 195, "ConsumerParkingTests": 62, "CorsTests": 430,
    "CrossTenantSuiteTests": 253, "DatabaseBusyTests": 147, "DbContextColumnsTests": 86,
    "DevelopmentSigningKeyTests": 640, "EnvelopeEncryptionTests": 210, "ErrorCodeCatalogueTests": 6,
    "ErrorCorrelationOwnHostTests": 132, "ErrorCorrelationTests": 188, "ExternalUserLinkTests": 118,
    "FraudIndicatorDefinitionTests": 159, "FreeCoverBandTests": 145, "FunctionAccessTests": 462,
    "FunctionCatalogueTests": 401, "GeneralTenantIsolationTests": 117, "HealthPlanProductTests": 140,
    "IdempotencyTests": 7, "IdentityTests": 143, "KeycloakProvisioningTests": 1793, "KeycloakRealmTests": 133,
    "LegalHoldReleaseTests": 255, "LocalisationTests": 339, "MakerCheckerTests": 173, "MeTests": 161,
    "MigrationTests": 1, "ModuleRoleAndPartitionTests": 0, "MoneyFlowTests": 299, "ObjectStoreTests": 205,
    "OnboardingReadinessTests": 196, "OpenApiContractTests": 537, "OpenApiReviewTests": 37, "OperationsTests": 92,
    "OperatorTenantTests": 447, "OutboxAndInboxTests": 4, "ParkedDeliveryTests": 5, "PartyAnonymisationTests": 304,
    "PartySearchTests": 165, "PayoutParkingTests": 143, "PlatformBootstrapTests": 120,
    "PlatformParkedDeliveriesMenuTests": 417, "PostedFinancialRecordsTests": 2, "ProviderCallbackTests": 1595,
    "RateLimitResponseTests": 327, "RatingPerformanceSmokeTests": 165, "RatingTestCaseTests": 148,
    "RecordHistoryTests": 179, "RoleTemplateTests": 278, "RowLevelSecurityTests": 0, "SchemaGapTests": 0,
    "SearchLogRetentionTests": 191, "SharedServicesTests": 218, "StaticDataChangeRequestLinkTests": 141,
    "StaticDataTests": 558, "TenancyTests": 66, "TenantAdministratorTests": 535, "TenantIsolationApiTests": 178,
    "TenantNamedRowsTests": 191, "WarmupTests": 134, "WorkflowTests": 143,
}


def classes():
    found = {}
    for path in sorted(ROOT.glob("*.cs")):
        text = path.read_text(encoding="utf-8")
        for name in re.findall(r"\bclass (\w+Tests)\b", text):
            if name not in EXCLUDED:
                tests = len(re.findall(r"^\s*\[(?:Fact|Theory|InlineData)", text, re.M))
                found[name] = MEASURED.get(name, ESTIMATE_BASE + ESTIMATE_PER_TEST * tests)
    return found


def shards(count):
    buckets = [[0, []] for _ in range(count)]
    for name, weight in sorted(classes().items(), key=lambda c: (-c[1], c[0])):
        lightest = min(buckets, key=lambda b: b[0])
        lightest[0] += weight
        lightest[1].append(name)
    return buckets


def main(args):
    if args[:1] == ["--check"]:
        count = int(args[1])
        assigned = [name for _, names in shards(count) for name in names]
        for i, (weight, names) in enumerate(shards(count), 1):
            print(f"shard {i}: weight {weight}, {len(names)} classes")
        if sorted(assigned) != sorted(classes()) or len(assigned) != len(set(assigned)):
            print("a class is in no shard or in two")
            return 1
        return 0

    if args[:1] == ["--check-listed"]:
        # The source scan above finds classes by name; this compares it with what the build actually contains, so a
        # test class the scan misses (another name, another folder) fails CI instead of silently never running.
        assigned = {name for _, names in shards(int(args[1])) for name in names} | EXCLUDED
        listed = {line.strip().rsplit(".", 1)[-1] for line in sys.stdin if line.strip().startswith("Insys.IntegrationTests.")}
        missing = sorted(listed - assigned)
        print(f"{len(listed)} compiled classes, {len(listed) - len(missing)} assigned")
        if missing:
            print("not in any shard: " + ", ".join(missing))
            return 1
        return 0

    shard, count = int(args[0]), int(args[1])
    print(" ".join(f'--filter-class "*.{name}"' for name in sorted(shards(count)[shard - 1][1])))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
