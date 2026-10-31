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
"""Writes docs/api/openapi-*.json from the documents the build generates in obj/openapi, in the committed escaping. No database needed.
With any argument, it only says whether each committed document already matches."""
import re
import sys

ESCAPES = {"'": "\\u0027", "+": "\\u002B", "<": "\\u003C", ">": "\\u003E", "&": "\\u0026", "`": "\\u0060", '\\"': "\\u0022"}


def escaped(match):
    text = match.group(0)
    if text in ESCAPES:
        return ESCAPES[text]
    if len(text) == 1 and ord(text) > 127:
        units = text.encode("utf-16-be")
        return "".join("\\u%04X" % int.from_bytes(units[i:i + 2], "big") for i in range(0, len(units), 2))
    return text


def committed_form(text):
    out = re.sub(r"\\.|[\s\S]", escaped, text)
    out = re.sub(r"\[ \](,?\r?\n)", r"[]\1", out)
    out = re.sub(r"\{ \}(,?\r?\n)", r"{}\1", out)
    return out.rstrip("\n") + "\n"


check = len(sys.argv) > 1
for name in ("core", "platform-operator", "static-data"):
    out = committed_form(open(f"src/Host/Insys.Api/obj/openapi/openapi_{name}.json", encoding="utf-8", newline="").read())
    path = f"docs/api/openapi-{name}.json"
    if check:
        print(name, out == open(path, encoding="utf-8", newline="").read())
    else:
        open(path, "w", encoding="utf-8", newline="").write(out)
