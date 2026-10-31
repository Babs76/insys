#!/bin/zsh
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
# Usage: tools/regen.sh. Regenerates the function catalogue seed, the role templates and the API documents with no database or container,
# so it needs no machine lock. The host starts as it does for build-time document generation (hence the project's GetDocument assembly name).
cd "${0:A:h}/.." || exit 1
ASPNETCORE_TEST_CONTENTROOT_INSYS_API="$PWD/src/Host/Insys.Api" dotnet run --project tools/Insys.Regen || exit 1
dotnet build src/Host/Insys.Api 2>&1 | grep -E " error |Build succeeded" | sort -u | cut -c1-200 | head -5
python3 tools/regen-openapi.py
