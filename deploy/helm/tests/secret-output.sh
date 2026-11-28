#!/usr/bin/env bash
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
# Sourced by the Preview's stubbed action tests (deploy/helm/tests/preview-masking.sh): only_masks FILE VALUE... fails when a line of FILE holds
# one of the values and isn't that value's own mask command. A mask line is what GitHub consumes and hides; any other line holding it would
# show the value in the log.
only_masks() {
  local file=$1 value leaked=0; shift
  for value in "$@"; do
    [ -n "$value" ] || continue
    if grep -F -- "$value" "$file" | grep -vxF -- "::add-mask::$value" | grep -q .; then
      echo "a secret is printed outside its mask line in $file:" >&2
      grep -nF -- "$value" "$file" | grep -vF -- ":::add-mask::$value" | sed "s/$(printf '%s' "$value" | sed 's/[][\.*^$/]/\\&/g')/<the secret>/g" >&2
      leaked=1
    fi
  done
  return "$leaked"
}
