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
# The Preview's certificate check (deploy/preview/preview.sh: tls_probe and issued_by; Ade, 24 November 2026), against local test CAs:
# production passes only for a chain that verifies against the trust store, for the host name, with a Let's Encrypt issuer (any
# intermediate name); staging passes only for a "(STAGING) Let's Encrypt" issuer. No network beyond 127.0.0.1.
set -euo pipefail
repo=$(cd "$(dirname "$0")/../../.." && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
cd "$work"

root_ca() { openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:P-256 -nodes -keyout "$1.key" -subj "/O=Test $1/CN=$1" -days 2 \
  -addext basicConstraints=critical,CA:TRUE -out "$1.crt" 2>/dev/null; }
signed() { # name, subject, signer, extension
  openssl req -new -newkey ec -pkeyopt ec_paramgen_curve:P-256 -nodes -keyout "$1.key" -subj "$2" -out "$1.csr" 2>/dev/null
  printf '%s\n' "$4" > "$1.ext"
  openssl x509 -req -in "$1.csr" -CA "$3.crt" -CAkey "$3.key" -CAcreateserial -days 2 -extfile "$1.ext" -out "$1.crt" 2>/dev/null
}
for r in trusted untrusted staging_root; do root_ca "$r"; done
ca="basicConstraints=critical,CA:TRUE"
signed le "/C=US/O=Let's Encrypt/CN=YR1" trusted "$ca"
signed le_old "/C=US/O=Let's Encrypt/CN=R11" trusted "$ca"
signed other_ca "/C=US/O=Some Other CA/CN=X1" trusted "$ca"
signed le_untrusted "/C=US/O=Let's Encrypt/CN=YR1" untrusted "$ca"
signed staging "/C=US/O=(STAGING) Let's Encrypt/CN=(STAGING) Pretend YR1" staging_root "$ca"
for i in le le_old other_ca le_untrusted staging; do signed "leaf_$i" "/CN=insys.example" "$i" "subjectAltName=DNS:insys.example"; done

sed -n '/^tls_probe()/,/^}/p;/^issued_by()/,/^}/p' "$repo/deploy/preview/preview.sh" > functions.sh
if ! grep -q '^tls_probe()' functions.sh || ! grep -q '^issued_by()' functions.sh; then echo "tls_probe or issued_by not found in preview.sh"; exit 1; fi
# shellcheck disable=SC1091
. ./functions.sh
address() { echo 127.0.0.1; }
export PREVIEW_TLS_TRUST=$work/trusted.crt PREVIEW_TLS_PORT=18443

failed=0
case_() { # chain (or none), host, want, expected pass or fail
  local pid="" line got
  if [ "$1" != none ]; then
    openssl s_server -accept "$PREVIEW_TLS_PORT" -cert "leaf_$1.crt" -key "leaf_$1.key" -cert_chain "$1.crt" -quiet >/dev/null 2>&1 &
    pid=$!
    for _ in $(seq 1 50); do (exec 3<>"/dev/tcp/127.0.0.1/$PREVIEW_TLS_PORT") 2>/dev/null && break; sleep 0.1; done
  fi
  line=$(tls_probe "$2")
  [ -z "$pid" ] || { kill "$pid" 2>/dev/null || true; wait "$pid" 2>/dev/null || true; }
  if issued_by "$line" "$3"; then got=pass; else got=fail; fi
  if [ "$got" = "$4" ]; then echo "ok    $1, $2, $3: $got ($line)"; else echo "WRONG $1, $2, $3: $got, expected $4 ($line)"; failed=1; fi
}
case_ le insys.example production pass            # Generation Y intermediate, trusted
case_ le_old insys.example production pass        # an older intermediate name, trusted
case_ le other.example production fail            # the wrong host name
case_ other_ca insys.example production fail      # trusted, but not Let's Encrypt
case_ le_untrusted insys.example production fail  # Let's Encrypt's name on a chain the trust store doesn't hold
case_ staging insys.example production fail
case_ staging insys.example staging pass
case_ le insys.example staging fail
case_ none insys.example production fail          # nothing answering
if [ "$failed" != 0 ]; then exit 1; fi
echo "The certificate check: every case as expected."
