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
"""Fails when this branch adds a numbered migration (db/migrations/V<n>__*.sql) at or below the highest version on main.

The migrator refuses a version below the highest already applied (MigrationRunner, DBA-017), so such a branch would be refused by every database
already migrated from main, though a fresh database (CI's) applies it without complaint. Batch 7.8 met this: its V0145 arrived after 7.5's V0146.

The added migrations are the ones in this commit that the merge base with main doesn't have; main's versions are those on main now. On main itself,
or with no migration added, it passes. Two files with one version also fail.

Usage: python3 tools/migration-numbers.py [--base origin/main]   (exit 0: passes, 1: fails, 2: git couldn't answer)
"""
import re
import subprocess
import sys

DIRECTORY = "db/migrations"
VERSION = re.compile(r"^V(\d+)__.+\.sql$")


def git(*args):
    return subprocess.run(["git", *args], check=True, capture_output=True, text=True).stdout.strip()


def versions(ref):
    """Each numbered migration at the ref, as file name and version."""
    names = git("ls-tree", "--name-only", f"{ref}:{DIRECTORY}").splitlines()
    return {name: int(m.group(1)) for name in names if (m := VERSION.match(name))}


def main(argv):
    base = argv[argv.index("--base") + 1] if "--base" in argv else "origin/main"
    try:
        head = git("rev-parse", "HEAD")
        main_sha = git("rev-parse", f"{base}^{{commit}}")
        merge_base = git("merge-base", "HEAD", base)
    except subprocess.CalledProcessError as error:
        print(f"migration numbers: git couldn't answer ({error.stderr.strip()}). Fetch main with its history first.")
        return 2

    here = versions("HEAD")
    problems = []
    by_version = {}
    for name, version in here.items():
        by_version.setdefault(version, []).append(name)
    for version, names in sorted(by_version.items()):
        if len(names) > 1:
            problems.append(f"V{version:04d} is used by more than one file: {', '.join(sorted(names))}.")

    on_main = versions(base)
    highest = max(on_main.values(), default=0)
    added = [] if head == main_sha else sorted(set(here) - set(versions(merge_base)))
    for name in added:
        if here[name] <= highest:
            problems.append(f"{name} is at or below V{highest:04d}, the highest on {base}. Renumber it above V{highest:04d}.")

    for problem in problems:
        print(f"migration numbers: {problem}")
    if problems:
        return 1

    if head == main_sha:
        print(f"migration numbers: this is {base} itself; nothing to compare.")
    elif not added:
        print(f"migration numbers: no migration added since the merge base with {base} (highest there V{highest:04d}).")
    else:
        print(f"migration numbers: {', '.join(added)} added, above V{highest:04d}, the highest on {base}.")
    return 0

if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
