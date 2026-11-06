# INSYS Preview: restoring a backup

**Rehearsal: built, not yet run on the Preview** (5 November). The plan rehearses one restore before the team gets access (`docs/plans/insys-preview.md`). Correct this page from it.

## What there is to restore from
- **Hetzner's daily backups of the whole server,** seven kept. Restoring one brings back the disk as it was, `/var/backups/insys` included.
- **The nightly backup** in `/var/backups/insys` (`deploy/preview/backup.yaml`, 01:00 UTC): `<day>-insys.dump` and `<day>-keycloak.dump` (`pg_dump -Fc`), and `<day>-openbao.tar.gz`, OpenBao's volume. Seven nights. OpenBao's volume is copied while OpenBao runs; its `file` storage barely changes once set up, but a copy taken mid-write could be inconsistent, so check each backup as below.
- **If both fail:** the `reset` action rebuilds from the seed. Accounts and sample data come back; what the team entered doesn't.

## Taking and checking a backup: the `backup` action
Actions, then preview, then Run workflow, with `action: backup` (no confirm word; it changes no data). It runs the nightly job now, then checks each file: the dumps list their contents (`pg_restore --list`), the archive opens (`tar -t`). It prints row counts (parties, users, documents, policies, memberships, approvals, data keys), stores, reads back and deletes an object with the API's keys, and checks that OpenBao is unsealed and decrypts a data key the database holds, with the API's own token. Nothing secret is printed.

## Restoring the nightly backup: the `restore` action
`action: restore`, `confirm: restore`, `backup_day`: the backup's date (`yyyy-mm-dd`), `ssh_from`: your own IP address. **It replaces the databases and OpenBao's volume with the backup's: everything entered since is lost.**
1. It prints the counts, stops the API and Keycloak, and makes each database again from its dump: dropped and restored with `pg_restore --create`, which brings back its settings and grants. Not `--clean`: on an INSYS database, `--clean` can't drop the partitions' inherited constraints (240 errors in rehearsal); drop-and-create left none, with the same grants, row-level security policies and rows, and a module role reading as before.
2. It restores OpenBao's volume from the archive and starts OpenBao, which comes back **sealed**. It starts Keycloak and the API, and prints the counts again.
3. It leaves SSH open from your address, and stops. **Your turn: unseal OpenBao**, with the key of the OpenBao the backup came from (today, run 41835262697's file):
   1. On your machine: `age -d -i ~/insys-preview-age-key.txt preview-secrets.age | jq -r .openbao.unsealKey`, and copy the key.
   2. `ssh -i <the PREVIEW_SSH_KEY private key> root@2.31.133.22`, then on the server:
      ```bash
      read -rs key && printf '%s' "$key" | /root/insys/openbao/openbao.sh unseal insys && unset key && kubectl rollout restart deploy/insys-api
      ```
      Paste the key at the silent prompt and press Enter. `read -rs` keeps it off the screen, the command line, the history and the disk. Nothing prints on success; `/root/insys/openbao/openbao.sh sealed insys` then prints `false`.
4. Run `backup` again: its counts should match the backup's, and its OpenBao check should pass. That run also closes SSH.

## A whole-server restore (Hetzner)
1. In Hetzner's console, restore the chosen backup onto the server `insys-preview`. The address stays the same, so DNS needs nothing.
2. OpenBao comes back sealed: run `open-ssh` with your address and unseal it as in step 3 above.
3. Run `backup` to check it.
