# Runbook: AI-1's private evaluation set and results (OQ-191 (a))

Decided by Ade, 19 November 2026, then changed the same day to two private repositories:
- **`Babs76/insys-ai1-eval`** holds the set: D-7's documents and the expert's expected values. The evaluation job reads it with a read-only key, so it can't change the set.
- **`Babs76/insys-ai1-eval-results`** takes each run's per-case review file under `reviews/<run id>`, on its own commit, written with its own key.

This repository (`Babs76/insys`) is public, so nothing from D-7 is ever committed here, and nothing from it goes into an artifact or a log.

**Hold:** nothing from D-7 goes into either private repository, or anywhere else, until Ade confirms that the insurer's written permission covers processing by Anthropic in the US region. Steps 1 to 4 can be done before that; step 5 waits for it.

All steps are Ade's, on his own machine. No key, token or document passes through a Claude session, a chat or a commit.

## 1. Create the two private repositories
```bash
gh repo create Babs76/insys-ai1-eval --private --description "AI-1 evaluation set (D-7). Private: insurer documents."
```
```bash
gh repo create Babs76/insys-ai1-eval-results --private --description "AI-1 evaluation review files. Private: quotes insurer documents."
```
- Only Ade has access to either. Add nobody else.
- Optional: in each repository's settings, turn off Issues, Wiki and Projects; nothing else should live there.

## 2. Make one deploy key for each
A deploy key belongs to one repository. It isn't tied to a person's account, so it doesn't widen anyone's access.

**The set: read only.**
```bash
ssh-keygen -t ed25519 -N "" -C "insys assist-eval read" -f ./ai1-eval-key
```
```bash
gh repo deploy-key add ./ai1-eval-key.pub --repo Babs76/insys-ai1-eval --title "insys assist-eval (read only)"
```
- Don't pass `--allow-write`; without it, the key can only read. The job can't change the documents or the expected values.

**The results: write.**
```bash
ssh-keygen -t ed25519 -N "" -C "insys assist-eval results" -f ./ai1-eval-results-key
```
```bash
gh repo deploy-key add ./ai1-eval-results-key.pub --repo Babs76/insys-ai1-eval-results --title "insys assist-eval results (write)" --allow-write
```
- This key can write only to the results repository. It can't reach the set.

## 3. Store both private keys as secrets in the `assist-eval` Environment only
```bash
gh secret set AI1_EVAL_DEPLOY_KEY --env assist-eval --repo Babs76/insys < ./ai1-eval-key
```
```bash
gh secret set AI1_EVAL_RESULTS_KEY --env assist-eval --repo Babs76/insys < ./ai1-eval-results-key
```
```bash
rm ./ai1-eval-key ./ai1-eval-key.pub ./ai1-eval-results-key ./ai1-eval-results-key.pub
```
- An Environment secret is available only to jobs that run in that Environment. `assist-eval` already needs Ade's approval for each run and accepts only `main`. So only an approved run of `main`'s evaluation job can read the set or write a result.
- Check: `gh secret list --env assist-eval --repo Babs76/insys` lists `AI1_EVAL_DEPLOY_KEY` and `AI1_EVAL_RESULTS_KEY` beside `ASSIST_CLAUDE_API_KEY`.

## 4. The layouts (no D-7 content yet)
The set, in `Babs76/insys-ai1-eval`:
```
ai1-evaluation-set.json    # the same format as docs/evaluation/ai1-evaluation-set.json in INSYS
documents/<case>/...       # the source documents each case cites
```
The results, in `Babs76/insys-ai1-eval-results`:
```
reviews/<run id>/...       # one run's per-case review file, on its own commit
```
The set's format is the public file's. Its one invented case can be copied in to check access before any real document is added.

## 5. After the permission is confirmed: add D-7's set
- Commit the documents and the expert's expected values to `Babs76/insys-ai1-eval` only.
- Then tell Claude. It builds the job's read of the set and write of the results (below), and each run waits for your approval in `assist-eval` as now.

## What INSYS's side will do (built after step 5 is confirmed; not built yet)
- `assist-eval.yml` gains an AI-1 job in the same Environment.
  - It checks out the set with `actions/checkout` (`repository: Babs76/insys-ai1-eval`, `ssh-key: ${{ secrets.AI1_EVAL_DEPLOY_KEY }}`, `persist-credentials: false`) into the runner's temporary folder.
  - It runs `tools/Insys.Assist.Evaluation --ai1` against it.
  - It commits the per-case review file to `Babs76/insys-ai1-eval-results` under `reviews/<run id>`, with `AI1_EVAL_RESULTS_KEY`.
- The job's log and its summary page give only measures and counts: no document text, no quotes, no expected values.
- **The per-case review file:** never as an artifact, never in any log.
- Whether the domain expert gets read access to the results repository, or Ade sends the file himself, stays Ade's decision for later.
- The model runs with `inference_geo` set to `us`, and the run records where inference ran, as for AI-2.

## To revoke
The set's key:
```bash
gh repo deploy-key list --repo Babs76/insys-ai1-eval
```
```bash
gh repo deploy-key delete <key id> --repo Babs76/insys-ai1-eval
```
```bash
gh secret delete AI1_EVAL_DEPLOY_KEY --env assist-eval --repo Babs76/insys
```
The results' key:
```bash
gh repo deploy-key list --repo Babs76/insys-ai1-eval-results
```
```bash
gh repo deploy-key delete <key id> --repo Babs76/insys-ai1-eval-results
```
```bash
gh secret delete AI1_EVAL_RESULTS_KEY --env assist-eval --repo Babs76/insys
```
