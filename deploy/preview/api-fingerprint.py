#!/usr/bin/env python3
# ============================================================================
# OPENFACTOR TECHNOLOGY GROUP
# CONFIDENTIAL AND PROPRIETARY SOFTWARE
# ============================================================================
# Copyright (c) 2026 OpenFactor Technology Group.
# All rights reserved.
# PRODUCT       : INSYS
# COMPONENT     : Build
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
"""The text in each INSYS assembly of a published API, as a fingerprint of the source it was built from (Ade, 27 November 2026).

Usage: api-fingerprint.py DIRECTORY   prints one line per Insys.*.dll: its name, how many texts it holds, and the sha256 of
                                      those texts, sorted and unique.

A text is a run of at least six printable ASCII characters in UTF-16, which is how .NET stores string literals (the #US heap):
messages, routes, SQL, error codes. They change with the source, not with when or where it was built, so an image whose
assemblies print the same lines as a reference build holds the same code, to the literals. The published API's Docker image
can't be checked by its OpenAPI documents: .dockerignore leaves docs/ out of the build, so none is embedded.
"""
import hashlib
import pathlib
import re
import sys

TEXT = re.compile(rb"(?:[\x20-\x7e]\x00){6,}")


def main(directory):
    found = sorted(pathlib.Path(directory).glob("Insys.*.dll"))
    if not found:
        print(f"no Insys.*.dll in {directory}", file=sys.stderr)
        return 1
    for dll in found:
        texts = sorted({m.group().decode("utf-16-le") for m in TEXT.finditer(dll.read_bytes())})
        print(dll.name, len(texts), hashlib.sha256("\n".join(texts).encode()).hexdigest())
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1]))
