# Runbook: AI-1's private evaluation set (OQ-191 (a))

Decided by Ade, 19 November 2026: D-7's documents and the expert's expected values are kept in a private repository that only the evaluation job can reach. The job also writes each run's per-case review file there. This repository (`Babs76/insys`) is public, so nothing from D-7 is ever committed here, and nothing from it goes into an artifact or a log.

**Hold:** nothing from D-7 goes into the private repository, or anywhere else, until Ade confirms that the insurer's written permission covers processing by Anthropic in the US region. Steps 1 to 4 can be done before that; step 5 waits for it.

All steps are Ade's, on his own machine. No key, token or document passes through a Claude session, a chat or a commit.

## 1. Create the private repository
```bash
gh repo create Babs76/insys-ai1-eval --private --description "AI-1 evaluation set (D-7). Private: insurer documents."
```
- Only Ade has access. Add nobody else.
- Optional: in the repository's settings, turn off Issues, Wiki and Projects; nothing else should live there.

## 2. Make a deploy key for the evaluation job, with write access
A deploy key belongs to that one repository. It isn't tied to a person's account, so it doesn't widen anyone's access. It needs write access because the job commits each run's review file (below).
```bash
ssh-keygen -t ed25519 -N "" -C "insys assist-eval" -f ./ai1-eval-key
```
```bash
gh repo deploy-key add ./ai1-eval-key.pub --repo Babs76/insys-ai1-eval --title "insys assist-eval (read and write)" --allow-write
```
- **Plainly:** with `--allow-write`, the key can change the set as well as add review files. Anyone who can run the job with that key, which means an approved run of `main` in `assist-eval`, could alter the documents or the expected values.
- **A suggestion for Ade, not decided:** protect the set's default branch so the key can't push to it (in the repository's settings, a branch rule that blocks pushes without review; check that deploy keys aren't exempt). The job would then commit only to a `reviews/<run id>` branch, or under a `reviews/<run id>/` path if branch protection isn't used.

## 3. Store the private key as a secret in the `assist-eval` Environment only
```bash
gh secret set AI1_EVAL_DEPLOY_KEY --env assist-eval --repo Babs76/insys < ./ai1-eval-key
```
```bash
rm ./ai1-eval-key ./ai1-eval-key.pub
```
- An Environment secret is available only to jobs that run in that Environment. `assist-eval` already needs Ade's approval for each run and accepts only `main`. So only an approved run of `main`'s evaluation job can read the set.
- Check: `gh secret list --env assist-eval --repo Babs76/insys` lists `AI1_EVAL_DEPLOY_KEY` beside `ASSIST_CLAUDE_API_KEY`.

## 4. The layout the job will read (no D-7 content yet)
```
ai1-evaluation-set.json    # the same format as docs/evaluation/ai1-evaluation-set.json in INSYS
documents/<case>/...       # the source documents each case cites
```
The format is the public file's. Its one invented case can be copied in to check access before any real document is added.

## 5. After the permission is confirmed: add D-7's set
- Commit the documents and the expert's expected values to the private repository only.
- Then tell Claude. It builds the job's read of the private set (below), and the run waits for your approval in `assist-eval` as now.

## What INSYS's side will do (built after step 5 is confirmed; not built yet)
- `assist-eval.yml` gains an AI-1 job in the same Environment. It checks out the private repository with `actions/checkout` (`repository: Babs76/insys-ai1-eval`, `ssh-key: ${{ secrets.AI1_EVAL_DEPLOY_KEY }}`) into the runner's temporary folder, runs `tools/Insys.Assist.Evaluation --ai1` against it, and commits the review file there.
- The job's log and its summary page give only measures and counts: no document text, no quotes, no expected values.
- **The per-case review file (decided, Ade 19 November 2026):** the job writes it into `Babs76/insys-ai1-eval`, on its own commit. Never as an artifact, never in any log.
- Whether the domain expert gets read access to that repository, or Ade sends the file himself, stays Ade's decision for later.
- The model runs with `inference_geo` set to `us`, and the run records where inference ran, as for AI-2.

## To revoke
```bash
gh repo deploy-key list --repo Babs76/insys-ai1-eval
```
```bash
gh repo deploy-key delete <key id> --repo Babs76/insys-ai1-eval
```
```bash
gh secret delete AI1_EVAL_DEPLOY_KEY --env assist-eval --repo Babs76/insys
```
