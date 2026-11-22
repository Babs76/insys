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
# Mounts the login theme insys (BR-168) in a running release's Keycloak without a Helm upgrade, as the chart would: for the Preview's
# keycloak-theme action, which changes Keycloak only. It applies the theme's ConfigMap as the chart renders it (templates/keycloak-theme.yaml,
# rendered by the caller), marks it as the release's so a later upgrade or reset adopts it, and patches the Deployment with the chart's volume,
# mount and checksum annotation. A second run changes nothing: the patch merges the volume by name and the mount by path, and replaces the
# items. Keycloak restarts only when the theme changed. Nothing secret passes through it.
#
#   theme-mount.sh <release> <the rendered ConfigMap> <the chart's checksum> [<the approved list>]
#
# The checksum is the one the chart renders on the Deployment (checksum/keycloak-theme); this computes it again from the applied ConfigMap,
# as the chart does (sha256 of its binaryData as compact JSON with sorted keys), and stops before the patch if they differ.
# With the approved list (files/keycloak-theme/insys.sha256, as the Preview passes it), every file the ConfigMap carries is decoded and its
# sha256 compared with the list before anything is applied: exactly those files with those hashes, or it stops having changed nothing.
set -euo pipefail
release=${1:?the release}
rendered=${2:?the rendered ConfigMap}
want=${3:?the checksum the chart renders}
list=${4:-}
cm=$release-keycloak-theme
fail() { echo "theme-mount: $*" >&2; exit 1; }
[[ $want =~ ^[0-9a-f]{64}$ ]] || fail "the checksum isn't a sha256: $want"
{ grep -qx 'kind: ConfigMap' "$rendered" && grep -qx "  name: $cm" "$rendered" && grep -qx 'binaryData:' "$rendered"; } \
  || fail "$rendered isn't the chart's ConfigMap $cm"
if [ -n "$list" ]; then
  approved=$(grep -E '^[0-9a-f]{64}  keycloak-theme/insys/' "$list" | LC_ALL=C sort -k2)
  [ -n "$approved" ] || fail "$list holds no approved files"
  shipped=$(sed -n '/^binaryData:$/,/^[^ ]/p' "$rendered" | sed -n 's/^  \([-._a-zA-Z0-9]*\): \([A-Za-z0-9+\/=]*\)$/\1 \2/p' | while read -r key b64; do
      printf '%s  keycloak-theme/insys/%s\n' "$(printf '%s' "$b64" | base64 -d | sha256sum | cut -d' ' -f1)" "${key//__//}"
    done | LC_ALL=C sort -k2)
  if [ "$shipped" != "$approved" ]; then
    { diff <(printf '%s\n' "$approved") <(printf '%s\n' "$shipped") || true; } | sed -n 's/^< /  approved, not shipped: /p; s/^> /  shipped, not approved: /p' >&2
    fail "the shipped theme isn't exactly the approved files: nothing was applied"
  fi
  echo "Theme files: the $(wc -l <<<"$shipped") shipped are exactly the approved ones, each sha256 checked."
fi
namespace=$(kubectl config view --minify -o jsonpath='{..namespace}')
namespace=${namespace:-default}

# Server-side apply: a client-side apply would copy the whole theme into the last-applied annotation, which is limited to 256 KiB.
kubectl apply --server-side --force-conflicts --field-manager=insys-keycloak-theme -f "$rendered" >/dev/null
# Helm adopts an existing object only if it carries the release's ownership marks; without them, its next upgrade or install refuses it.
kubectl label --overwrite configmap "$cm" app.kubernetes.io/managed-by=Helm >/dev/null
kubectl annotate --overwrite configmap "$cm" "meta.helm.sh/release-name=$release" "meta.helm.sh/release-namespace=$namespace" >/dev/null
live=$(kubectl get configmap "$cm" -o json)
[ "$(jq -r '.data // {} | length' <<<"$live")" = 0 ] || fail "$cm has text data besides binaryData: not as the chart renders it"
sum=$(jq -jcS .binaryData <<<"$live" | sha256sum | cut -d' ' -f1)
[ "$sum" = "$want" ] || fail "the ConfigMap's checksum is $sum, the chart's $want: not patched"
echo "Theme ConfigMap $cm: $(jq -r '.binaryData | length' <<<"$live") files, checksum $sum, marked as the release $release's ($namespace)."

before=$(kubectl get deploy "$release-keycloak" -o jsonpath='{.metadata.generation}')
jq -c --arg cm "$cm" --arg sum "$sum" '{spec: {template: {
    metadata: {annotations: {"checksum/keycloak-theme": $sum}},
    spec: {
      containers: [{name: "keycloak", volumeMounts: [{name: "keycloak-theme", mountPath: "/opt/keycloak/themes/insys", readOnly: true}]}],
      volumes: [{name: "keycloak-theme", configMap: {name: $cm, items: (.binaryData | keys | map({key: ., path: gsub("__"; "/")}))}}]}}}}' <<<"$live" \
  | kubectl patch deploy "$release-keycloak" --type strategic --patch-file /dev/stdin >/dev/null
after=$(kubectl get deploy "$release-keycloak" -o jsonpath='{.metadata.generation}')
spec=$(kubectl get deploy "$release-keycloak" -o json)
[ "$(jq '[.spec.template.spec.volumes[] | select(.name == "keycloak-theme")] | length' <<<"$spec")" = 1 ] || fail "the Deployment hasn't exactly one volume keycloak-theme"
[ "$(jq '[.spec.template.spec.containers[] | select(.name == "keycloak") | .volumeMounts[] | select(.mountPath == "/opt/keycloak/themes/insys")] | length' <<<"$spec")" = 1 ] \
  || fail "Keycloak's container hasn't exactly one mount at /opt/keycloak/themes/insys"
if [ "$before" = "$after" ]; then
  echo "Deployment $release-keycloak: the theme was already mounted as it is; unchanged, no restart."
else
  echo "Deployment $release-keycloak: the theme's volume, mount and checksum added; Keycloak restarts."
fi
kubectl rollout status "deploy/$release-keycloak" --timeout=900s >/dev/null \
  || fail "Keycloak didn't roll out with the theme within 15 minutes; the earlier pod keeps serving (RollingUpdate), the login theme isn't set"
echo "Deployment $release-keycloak: rolled out and ready."
