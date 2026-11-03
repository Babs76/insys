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
# The chart's durable OpenBao (openbao.devMode false), through its HTTP API on a port-forward. Every secret goes in on stdin and out on stdout, never
# on a command line or in a log, so the caller decides where it goes (the Preview encrypts it to Ade's key; the smoke test throws it away).
#
#   openbao.sh init <release>                   prints the init JSON: one unseal key and the root token. Once, on a new volume.
#   openbao.sh unseal <release>                 reads an unseal key on stdin. After every start of the pod.
#   openbao.sh configure <release> <mount> <key>  reads the root token on stdin; enables Transit and the key, and prints a token for the API
#                                               allowed only the data key and decrypt calls on that key (what kmsToken holds).
#   openbao.sh sealed <release>                 prints true or false.
set -euo pipefail
command=${1:?init, unseal, configure or sealed}
release=${2:?the release name}
port=${OPENBAO_LOCAL_PORT:-18200}

kubectl port-forward "svc/$release-openbao" "$port:8200" >/dev/null 2>&1 &
forward=$!
trap 'kill $forward 2>/dev/null || true' EXIT
for _ in $(seq 1 30); do curl -s -o /dev/null "http://127.0.0.1:$port/v1/sys/health" && break; sleep 1; done
bao() { # method path [token]; the body, if any, on stdin
  curl -sS --fail-with-body -X "$1" ${3:+-H "X-Vault-Token: $3"} -H 'Content-Type: application/json' --data-binary @- "http://127.0.0.1:$port/v1/$2"
}

case "$command" in
  init)
    echo '{"secret_shares":1,"secret_threshold":1}' | bao PUT sys/init
    ;;
  unseal)
    jq -Rc '{key: .}' | bao PUT sys/unseal >/dev/null
    ;;
  configure)
    mount=${3:?the Transit mount}; key=${4:?the key name}
    root=$(cat)
    echo '{"type":"transit"}' | bao POST "sys/mounts/$mount" "$root" >/dev/null 2>&1 || true
    echo '{}' | bao POST "$mount/keys/$key" "$root" >/dev/null
    jq -nc --arg m "$mount" --arg k "$key" '{policy: ("path \"" + $m + "/datakey/plaintext/" + $k + "\" { capabilities = [\"update\"] }\npath \"" + $m + "/decrypt/" + $k + "\" { capabilities = [\"update\"] }")}' \
      | bao PUT sys/policies/acl/insys-kms "$root" >/dev/null
    # Ten years: the token lasts as long as the deployment; rotating it is the runbook's.
    echo '{"max_lease_ttl":"87600h","default_lease_ttl":"87600h"}' | bao POST sys/auth/token/tune "$root" >/dev/null
    echo '{"policies":["insys-kms"],"no_default_policy":true,"ttl":"87600h","display_name":"insys-api"}' | bao POST auth/token/create-orphan "$root" | jq -r .auth.client_token
    ;;
  sealed)
    echo '' | curl -sS "http://127.0.0.1:$port/v1/sys/seal-status" | jq -r .sealed
    ;;
  *)
    echo "unknown command: $command" >&2; exit 2
    ;;
esac
