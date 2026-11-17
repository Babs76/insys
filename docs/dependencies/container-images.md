# Container images the deployment runs

Third-party images the chart and the INSYS Preview's run (`deploy/preview/preview.sh`) pull, with their licences. `CLAUDE.md`'s rule applies:
OSI-approved licences only, checked at the source before an image is added, with its current version and maintenance. Pin by version, and by
digest where a run pulls the image itself.

| Image | Version (pin) | Used by | Licence, from the source | OSI-approved | Maintenance | Checked |
|---|---|---|---|---|---|---|
| `curlimages/curl` | 8.23.0, `sha256:3fb7e13e5d179a845c97d78204f3f1064d1907037f26a3ebf91710571151fd93` | `preview.sh`'s `storage_check` and `kms_check`: one short-lived pod per run, signing S3 requests with `--aws-sigv4`; and the nightly backup's seal step (`deploy/preview/backup.yaml`), which asks OpenBao's seal status and can send one alert email | curl: the curl License (`COPYING` at tag `curl-8_23_0`). The image's build, `curl/curl-container`: MIT (its `LICENSE`) | Yes, both: the SPDX licence list records `curl` and `MIT` as `isOsiApproved: true` (opensource.org itself couldn't be reached from the session that checked) | Active: 8.23.0 on 28 October 2026, after 8.22.0 (2 September) and 8.21.0 (24 June) | 5 November 2026 |
| `clamav/clamav` | 1.5.4 (index `sha256:7054e3f34308c384ea43cd786ea17abf899bd44af40af9f1b099853d7a5c214e`) | The chart's malware scanner (`scanner.enabled`, AIA-010), and compose's profile `scanner`. A separate service the API reaches over TCP (clamd), never linked | ClamAV: GPL-2.0 (its `COPYING`); the image is built from ClamAV's own repository | Yes: GPL-2.0 is OSI-approved. Not linked, so it puts no terms on INSYS's code (ai-assist.md §4) | Active: 1.5.4 on 7 August 2026; the 1.4 LTS line is supported to August 2027 | 17 November 2026 |

- **Not audited yet:** the Alpine packages inside `curlimages/curl` (musl, OpenSSL and others), each under its own licence.
- **Not recorded here yet:** the chart's own images (`postgres`, `quay.io/openbao/openbao` (MPL-2.0, per ADR-027), `chrislusf/seaweedfs`, `quay.io/keycloak/keycloak`, the console's) and `busybox` in the Preview's Traefik settings. Each is to be checked the same way before go-live.
- **Not used: Mailpit.** It was planned as an in-cluster mail catcher (Ade, 5 November); Ade chose Google Workspace's SMTP relay instead (6 November), so no mail image is deployed.
