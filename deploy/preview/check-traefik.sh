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
# Renders the Traefik chart that the Preview's k3s bundles with deploy/preview/traefik.yaml's values, and checks the arguments Traefik gets:
# the resolver le must have the TLS-ALPN-01 challenge, its storage and its CA. Run by the deployment smoke workflow; needs helm and git.
# The chart's version is read from k3s's own manifest at the Preview's k3s version, so a k3s upgrade is checked against its own chart.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck disable=SC2016 # the pattern matches the literal ${...} in preview.sh
k3s_version=$(sed -n 's/^k3s_version=\${PREVIEW_K3S_VERSION:-\(v[^}]*\)}$/\1/p' "$here/preview.sh")
[ -n "$k3s_version" ] || { echo "::error::no k3s version in preview.sh"; exit 1; }
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

chart=$(curl -sSfL "https://raw.githubusercontent.com/k3s-io/k3s/${k3s_version/+/%2B}/manifests/traefik.yaml" \
  | sed -n 's#.*/static/charts/traefik-[0-9.]*+up\([0-9.]*\)\.tgz.*#\1#p' | head -n 1)
[ -n "$chart" ] || { echo "::error::no Traefik chart version in k3s $k3s_version's manifest"; exit 1; }
git clone -q --depth 1 --branch "v$chart" https://github.com/traefik/traefik-helm-chart "$work/chart" 2>/dev/null

# The HelmChartConfig's valuesContent, with preview.sh's substitutions (staging CA, no email).
sed -n '/valuesContent: |-/,$p' "$here/traefik.yaml" | tail -n +2 | sed -e 's/^    //' \
  -e 's#ACME_CA#https://acme-staging-v02.api.letsencrypt.org/directory#' -e 's#ACME_EMAIL##' > "$work/values.yaml"
args=$(helm template traefik "$work/chart/traefik" -n kube-system --kube-version "${k3s_version%%+*}" -f "$work/values.yaml" \
  | grep -o -- '--certificatesresolvers\.[^"]*')

failed=0
for want in '--certificatesresolvers.le.acme.tlsChallenge=true' '--certificatesresolvers.le.acme.storage=/data/acme.json' \
  '--certificatesresolvers.le.acme.caServer=https://acme-staging-v02.api.letsencrypt.org/directory'; do
  grep -qxF -- "$want" <<<"$args" || { echo "::error::Traefik (chart $chart, k3s $k3s_version) doesn't get $want"; failed=1; }
done
echo "Traefik chart $chart (k3s $k3s_version) gets:"
echo "$args"
exit "$failed"
