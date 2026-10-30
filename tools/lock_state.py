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
"""
Reads the machine lock shared by the back end and the front end, and prints one line: free, held <holder>, stale <holder> or
unreadable <text>.

The file is key=value lines, one per line, all four required:
    holder=<backend|frontend> <run id or branch>
    pid=<process id>
    taken_at=<ISO 8601 UTC>
    heartbeat_at=<ISO 8601 UTC>

Stale only if heartbeat_at is more than 10 minutes old AND the pid isn't alive. A file this can't parse is never stale, however old:
the waiter keeps waiting and reports it.
"""
import os
import re
import sys
import time
from datetime import datetime

STALE_SECONDS = 600
UTC_INSTANT = r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?Z"
LINE_FORMATS = {
    "holder": r"(backend|frontend) \S.*",
    "pid": r"[1-9]\d*",
    "taken_at": UTC_INSTANT,
    "heartbeat_at": UTC_INSTANT,
}


def parse(text):
    """The lock's fields, or None if any line, or any of the four keys, isn't as above."""
    fields = {}
    for line in text.splitlines():
        key, separator, value = line.partition("=")
        if not separator or key not in LINE_FORMATS or key in fields or not re.fullmatch(LINE_FORMATS[key], value):
            return None
        fields[key] = value
    return fields if len(fields) == len(LINE_FORMATS) else None


def instant(value):
    return datetime.fromisoformat(value.replace("Z", "+00:00")).timestamp()


def alive(pid):
    try:
        os.kill(pid, 0)
        return True
    except PermissionError:
        return True
    except (ProcessLookupError, OverflowError):
        return False


def main(path):
    try:
        text = open(path, encoding="utf-8").read()
    except FileNotFoundError:
        return "free"
    fields = parse(text)
    try:
        beat = instant(fields["heartbeat_at"]) if fields else None
    except ValueError:
        beat = None
    if beat is None:
        return "unreadable " + " | ".join(text.split("\n")).strip(" |")
    state = "stale" if time.time() - beat > STALE_SECONDS and not alive(int(fields["pid"])) else "held"
    return f"{state} {fields['holder']}"


if __name__ == "__main__":
    print(main(sys.argv[1]))
