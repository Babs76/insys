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
"""The stall log's summary, printed into the job's own log (OQ-129, Ade, 19 November 2026).

StallWatchdog appends a report to INSYS_STALL_LOG every 20 seconds while a test runs past its threshold. A job cut off at its limit
shows no test output, and its artifact needs a host this repository's sessions can't reach, so this prints, for each test that stalled:
how long it had run at its last report, how many reports it gave, and that last report (threads, connections, the database's active
sessions and what they wait on). Then the file's last lines. It never fails the job.

Usage: stall-summary.py <stall log> [lines]
"""
import re
import sys
from pathlib import Path

HEADER = re.compile(r"^STALL (?P<label>\S+) at (?P<seconds>\d+)s \((?P<clock>[0-9:]+Z)\)")


def summary(text: str, tail: int) -> str:
    reports = [r for r in text.split("\n\n") if r.strip()]
    tests: dict[str, dict] = {}
    for report in reports:
        match = HEADER.match(report.strip())
        if not match:
            continue
        seen = tests.setdefault(match["label"], {"count": 0})
        seen.update(count=seen["count"] + 1, seconds=int(match["seconds"]), clock=match["clock"], last=report.strip())
    out = [f"Stall summary: {len(tests)} test(s) ran past the watchdog's threshold, {len(reports)} report(s)."]
    for label, seen in sorted(tests.items(), key=lambda t: -t[1]["seconds"]):
        out.append(f"\n== {label}: running {seen['seconds']}s at its last report ({seen['clock']}), {seen['count']} report(s). Its last report:")
        out.append(seen["last"])
    lines = text.rstrip("\n").splitlines()
    out.append(f"\n== The log's last {min(tail, len(lines))} lines:")
    out.extend(lines[-tail:])
    return "\n".join(out)


def main() -> int:
    if len(sys.argv) < 2:
        print("usage: stall-summary.py <stall log> [lines]")
        return 0
    path = Path(sys.argv[1])
    tail = int(sys.argv[2]) if len(sys.argv) > 2 else 40
    if not path.is_file() or path.stat().st_size == 0:
        print(f"Stall summary: no test ran past the watchdog's threshold ({path.name} absent or empty).")
        return 0
    print(summary(path.read_text(encoding="utf-8", errors="replace"), tail))
    return 0


if __name__ == "__main__":
    sys.exit(main())
