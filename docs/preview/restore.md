# INSYS Preview: restoring a backup

**Not yet rehearsed.** The plan rehearses one restore before the team gets access (`docs/plans/insys-preview.md`). Correct this page from the rehearsal.

## What there is to restore from
- **Hetzner's daily backups of the whole server,** seven kept. Restoring one brings back the disk as it was, `/var/backups/insys` included.
- **The nightly backup** in `/var/backups/insys` (`deploy/preview/backup.yaml`, 01:00 UTC): `<day>-insys.dump` and `<day>-keycloak.dump` (`pg_dump -Fc`), and `<day>-openbao.tar.gz`, OpenBao's volume. Seven nights.
- **If both fail:** the `reset` action rebuilds from the seed. Accounts and sample data come back; what the team entered doesn't.

## A whole-server restore (Hetzner)
1. In Hetzner's console, restore the chosen backup onto the server `insys-preview`. The address stays the same, so DNS needs nothing.
2. **OpenBao comes back sealed.** Ade runs the workflow's `open-ssh` with his address, then on the server: `kubectl port-forward svc/insys-openbao 18200:8200 &` and `curl -s -X PUT --data @- http://127.0.0.1:18200/v1/sys/unseal`, typing `{"key":"<unseal key>"}` after decrypting it locally from his file, so the key isn't on a command line or in a file there.
3. Restart the API: `kubectl rollout restart deploy/insys-api`.
4. Check, as the smoke test does: sign in, open a party with an identity number, and see its masked number.

## From the nightly backup (one day lost at most)
Run on the server, after `open-ssh`. `<day>` is the backup's date.
1. Stop the API and Keycloak: `kubectl scale deploy/insys-api deploy/insys-keycloak --replicas=0`.
2. Databases, each from its dump:
   `kubectl exec -i insys-postgres-0 -- pg_restore -U insys_owner -d insys --clean --if-exists < /var/backups/insys/<day>-insys.dump`, and the same for `keycloak`.
3. OpenBao: `kubectl scale statefulset/insys-openbao --replicas=0`, then put the archive back on its volume with a short-lived pod that mounts `data-insys-openbao-0` (empty the volume, `tar -xzf` the archive into it), then `--replicas=1`.
4. Unseal OpenBao as above; the restored data needs the unseal key of the OpenBao it was copied from.
5. `kubectl scale deploy/insys-api deploy/insys-keycloak --replicas=1`, and check as above.
