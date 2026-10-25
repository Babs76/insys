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

Every test class in tests/Insys.IntegrationTests is assigned, so a new class always runs. The endpoint sweeps run in
their own job and are left out. A class weighs a fixed start-up cost (its database and host) plus one per test, and
known slow classes weigh more; classes are placed heaviest first on the lightest shard, so the split is stable.
"""
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent / "tests" / "Insys.IntegrationTests"
EXCLUDED = {"EndpointSecuritySweepTests"}
STARTUP = 4
# Classes whose tests drive whole processes or their own hosts (minutes on CI, not seconds).
SLOW = {"OnboardingReadinessTests": 30, "MoneyFlowTests": 25, "ProviderCallbackTests": 15, "CommunicationsTests": 15, "FunctionAccessTests": 15,
        "TenantAdministratorTests": 15, "ConsumerParkingTests": 12, "BusinessDateTests": 12, "ArrearsAgeingTests": 10, "OpenApiContractTests": 10}


def classes():
    found = {}
    for path in sorted(ROOT.glob("*.cs")):
        text = path.read_text(encoding="utf-8")
        for name in re.findall(r"\bclass (\w+Tests)\b", text):
            if name not in EXCLUDED:
                tests = len(re.findall(r"^\s*\[(?:Fact|Theory|InlineData)", text, re.M))
                found[name] = STARTUP + tests + SLOW.get(name, 0)
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

    shard, count = int(args[0]), int(args[1])
    print(" ".join(f'--filter-class "*.{name}"' for name in sorted(shards(count)[shard - 1][1])))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
