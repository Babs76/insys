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
# The Preview's read-authority action (deploy/preview/preview.sh: read_authority; Ade approved one read-only run, 26 November 2026), against a
# stubbed server: the action does nothing but open SSH for the runner and read; the SQL it sends is read only (no statement that writes, the
# read-only guards present and the transaction rolled back); psql gets the read-only setting at connection; its output is passed through; and a
# failed read fails the run. No network, no server, no database.
set -euo pipefail
repo=$(cd "$(dirname "$0")/../../.." && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

sed -n '/^read_authority_sql()/,/^}/p;/^read_authority()/,/^}/p' "$repo/deploy/preview/preview.sh" > "$work/functions.sh"
for f in read_authority_sql read_authority; do grep -q "^$f()" "$work/functions.sh" || { echo "$f not found in preview.sh"; exit 1; }; done
# shellcheck disable=SC2016 # the lines of preview.sh, as written
grep -A1 '^  read-authority)' "$repo/deploy/preview/preview.sh" | grep -q '^    ssh_key; open_ssh "${RUNNER_IP:?}"; wait_for_ssh; read_authority$' \
  || { echo "FAIL: the read-authority action does more than open SSH and read"; exit 1; }
if ! grep -q "fromJSON('\[\"upgrade-check\", \"read-authority\", " "$repo/.github/workflows/preview.yml" || ! grep -q 'options: \[.* read-authority,' "$repo/.github/workflows/preview.yml"; then
  echo "FAIL: preview.yml doesn't offer read-authority with its confirm word"; exit 1
fi

# Whether SQL is read only: no statement that writes or changes the session's guard, both guards present, and only a ROLLBACK at the end.
read_only() { # the SQL on stdin; prints why it isn't, and fails
  local sql
  sql=$(cat)
  if grep -qiwE 'INSERT|UPDATE|DELETE|ALTER|CREATE|DROP|TRUNCATE|GRANT|REVOKE|MERGE|COPY|CALL|DO|COMMIT|VACUUM|REINDEX|CLUSTER|LOCK|NOTIFY|REFRESH|SECURITY' <<<"$sql"; then
    echo "a statement that writes: $(grep -oiwE 'INSERT|UPDATE|DELETE|ALTER|CREATE|DROP|TRUNCATE|GRANT|REVOKE|MERGE|COPY|CALL|DO|COMMIT|VACUUM|REINDEX|CLUSTER|LOCK|NOTIFY|REFRESH|SECURITY' <<<"$sql" | sort -u | tr '\n' ' ')"
    return 1
  fi
  grep -qiE '(default_)?transaction_read_only *(=|to) *(off|false|0)' <<<"$sql" && { echo "the read-only guard switched off"; return 1; }
  grep -qx 'SET default_transaction_read_only = on;' <<<"$sql" || { echo "no SET default_transaction_read_only = on"; return 1; }
  grep -qx 'BEGIN READ ONLY;' <<<"$sql" || { echo "no BEGIN READ ONLY"; return 1; }
  [ "$(tail -n 1 <<<"$sql")" = "ROLLBACK;" ] || { echo "the transaction isn't rolled back at the end"; return 1; }
  # before BEGIN READ ONLY, only the SET
  if sed '/^BEGIN READ ONLY;$/,$d' <<<"$sql" | grep -vx 'SET default_transaction_read_only = on;' | grep -q .; then
    echo "a statement before BEGIN READ ONLY other than its SET"; return 1
  fi
  # after ROLLBACK, nothing
  if sed '1,/^ROLLBACK;$/d' <<<"$sql" | grep -q .; then echo "a statement after ROLLBACK"; return 1; fi
  # only SELECTs between BEGIN and ROLLBACK
  if sed -n '/^BEGIN READ ONLY;$/,/^ROLLBACK;$/p' <<<"$sql" | grep -vxE 'BEGIN READ ONLY;|ROLLBACK;|SELECT .*;' | grep -q .; then
    echo "a statement other than SELECT in the transaction"; return 1
  fi
}

# The checker itself: each of these must be refused.
for bad in "INSERT INTO workflow.approval VALUES (1);" "update iam.user_account SET status = 'X';" "DELETE FROM x;" "ALTER TABLE x ADD c int;" \
           "CREATE TABLE x (i int);" "DROP TABLE x;" "TRUNCATE x;" "GRANT ALL ON x TO y;" "SET default_transaction_read_only = off;" "COMMIT;" "END;" "SET transaction_read_only = off;"; do
  if printf 'SET default_transaction_read_only = on;\nBEGIN READ ONLY;\nSELECT 1;\n%s\nROLLBACK;\n' "$bad" | read_only > /dev/null; then
    echo "FAIL: the read-only check accepted: $bad"; exit 1
  fi
done
if printf 'BEGIN;\nSELECT 1;\nROLLBACK;\n' | read_only > /dev/null; then echo "FAIL: the read-only check accepted no guard"; exit 1; fi
if printf 'SET default_transaction_read_only = on;\nBEGIN READ ONLY;\nSELECT 1;\n' | read_only > /dev/null; then echo "FAIL: the read-only check accepted no ROLLBACK"; exit 1; fi
if printf 'SELECT 1;\nSET default_transaction_read_only = on;\nBEGIN READ ONLY;\nSELECT 1;\nROLLBACK;\n' | read_only > /dev/null; then echo "FAIL: the read-only check accepted a read before the guard"; exit 1; fi
if printf 'SET default_transaction_read_only = on;\nBEGIN READ ONLY;\nSELECT 1;\nROLLBACK;\nSELECT 2;\n' | read_only > /dev/null; then echo "FAIL: the read-only check accepted a statement after ROLLBACK"; exit 1; fi

# One scenario, in a subshell: $1 its name; PSQL_CODE the stubbed psql's exit code.
# shellcheck disable=SC2034,SC2329   # the stubs are used by the functions sourced from preview.sh
run() (
  export RUNNER_TEMP=$work/$1
  mkdir -p "$RUNNER_TEMP"; : > "$RUNNER_TEMP/log"
  fail() { echo "::error::$*" >&2; exit 1; }
  on_server() {
    echo "server: $*" >> "$RUNNER_TEMP/log"
    cat > "$RUNNER_TEMP/sql"
    printf '%s\n' "SET" "BEGIN" "tenant OFTZA: found" "transaction read only: on" "(a) STATIC_DATA_T1 authority rows: 1" \
      "(a) DEMO_CHECKER limit 1000000.00 base EFFECTIVE v1 from 2026-11-01 to open" "(b) DEMO_CHECKER holds authority in force today for STATIC_DATA_T1: yes" "ROLLBACK"
    return "${PSQL_CODE:-0}"
  }
  # shellcheck disable=SC1091
  . "$work/functions.sh"
  read_authority
)

pass=0
expect() { # name, wanted outcome (ok or fail), then the scenario's settings
  local name=$1 outcome=$2 got why; shift 2
  if env "$@" bash -c "$(declare -f run); $(declare -p work); run $name" > "$work/$name.out" 2>&1; then got=ok; else got=fail; fi
  [ "$got" = "$outcome" ] || { echo "FAIL $name: $got, not $outcome"; cat "$work/$name.out"; exit 1; }
  [ "$(grep -c '^server: ' "$work/$name/log")" = 1 ] || { echo "FAIL $name: not exactly one server command"; cat "$work/$name/log"; exit 1; }
  grep -qx "server: kubectl exec -i insys-postgres-0 -- env PGOPTIONS='-c default_transaction_read_only=on' psql -U insys_owner -d insys -At -v ON_ERROR_STOP=1" "$work/$name/log" \
    || { echo "FAIL $name: psql isn't run read only at connection, as insys_owner on insys, stopping at an error"; cat "$work/$name/log"; exit 1; }
  why=$(read_only < "$work/$name/sql") || { echo "FAIL $name: the SQL isn't read only: $why"; exit 1; }
  if ! grep -q "tenant_code = 'OFTZA'" "$work/$name/sql" || grep -qiE "legal_name|display_name|email|phone|birth|national|first_name|last_name" "$work/$name/sql"; then
    echo "FAIL $name: the SQL isn't OFTZA's, or reads more than codes, counts and dates"; exit 1
  fi
  pass=$((pass + 1)); echo "PASS $name"
}

expect a-read-passes-and-prints-what-it-read ok
grep -q "^(b) DEMO_CHECKER holds authority in force today for STATIC_DATA_T1: yes$" "$work/a-read-passes-and-prints-what-it-read.out" \
  || { echo "FAIL: the read isn't printed"; cat "$work/a-read-passes-and-prints-what-it-read.out"; exit 1; }
expect a-failed-read-fails-the-run fail PSQL_CODE=3
grep -q "::error::reading OFTZA's approval authority (read only; nothing was changed)" "$work/a-failed-read-fails-the-run.out" || { echo "FAIL: no error for a failed read"; exit 1; }

echo "$pass scenarios passed"
