# Container images the deployment runs

Third-party images the chart and the INSYS Preview's run (`deploy/preview/preview.sh`) pull, with their licences. `CLAUDE.md`'s rule applies:
OSI-approved licences only, checked at the source before an image is added, with its current version and maintenance. Pin by version, and by
digest where a run pulls the image itself.

| Image | Version (pin) | Used by | Licence, from the source | OSI-approved | Maintenance | Checked |
|---|---|---|---|---|---|---|
| `curlimages/curl` | 8.23.0, `sha256:3fb7e13e5d179a845c97d78204f3f1064d1907037f26a3ebf91710571151fd93` | `preview.sh`'s `storage_check`: one short-lived pod per run, signing S3 requests with `--aws-sigv4` | curl: the curl License (`COPYING` at tag `curl-8_23_0`). The image's build, `curl/curl-container`: MIT (its `LICENSE`) | Yes, both: the SPDX licence list records `curl` and `MIT` as `isOsiApproved: true` (opensource.org itself couldn't be reached from the session that checked) | Active: 8.23.0 on 28 October 2026, after 8.22.0 (2 September) and 8.21.0 (24 June) | 5 November 2026 |

- **Not audited yet:** the Alpine packages inside `curlimages/curl` (musl, OpenSSL and others), each under its own licence.
- **Not recorded here yet:** the chart's own images (`postgres`, `quay.io/openbao/openbao` (MPL-2.0, per ADR-027), `chrislusf/seaweedfs`, `quay.io/keycloak/keycloak`, the console's) and `busybox` in the Preview's Traefik settings. Each is to be checked the same way before go-live.
- **Planned:** Mailpit, as the Preview's in-cluster mail catcher (Ade, 5 November): its repository's `LICENSE` reads MIT; its current version is to be confirmed and recorded here when it's added.
