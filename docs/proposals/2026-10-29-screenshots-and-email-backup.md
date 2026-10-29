# Proposal: screenshot storage and email backup (decision needed, WT-047)

Status: **waiting for the owner's decision.** Nothing in this proposal is implemented. Until then, screenshots stay
in the browser that captured them, and Backup & Export downloads ZIP files.

## Constraints

- `crosenlund79/TimeAndTask` and its GitHub Pages site are **public**. Anything committed there, including Git history,
  is readable by anyone and stays in history even after deletion.
- The board is a static page. Its only credential is the owner's fine-grained token, kept in the browser.
- Screenshots of work can be confidential. They must never be uploaded publicly without explicit approval.
- No new servers, databases or paid subscriptions without a clear reason.

## 1. Screenshot storage

| Option | Privacy | Syncs between devices | Backup job can read it | Setup and cost | Verdict |
|---|---|---|---|---|---|
| Public repository (`TimeAndTask`) | Public | Yes | Yes | None | **Rejected** |
| Browser only (IndexedDB, what exists now) | Private to that browser | No | No | None | Interim only: lost if browser data is cleared |
| **Private GitHub repository**, e.g. `crosenlund79/worktrace-vault` | Private (owner and GitHub) | Yes | Yes, with the repository's own token | One new repository; same token type | **Recommended** |
| Cloud object storage (S3, Azure Blob, R2) | Private | Yes | Yes | Needs a signing service or keys, plus an account | Too heavy |
| Git LFS in a private repository | Private | Yes | Yes | Storage and bandwidth quotas, may cost money | Not needed at screenshot sizes |

How the recommended option works:

- Images go to `attachments/<YYYY>/<MM>/<card uid>/<attachment id>.png` in the private repository.
  - Captions and file names go in a manifest in the same repository, not in the public `board.json`.
  - Writes use the same version-checked Contents API as the board.
- The board shows images by fetching them with the token and showing them as `blob:` URLs. No image ever has a
  public URL.
- The fine-grained token gains one more repository, the private one, with **Contents: Read and write**.
- Images that today exist only in a browser are offered for upload once, with a one-click "upload to private
  storage" step.
- **Limits:** 100 MB per file through the API. GitHub recommends repositories stay under about 1 GB, which is
  several thousand screenshots at 100–500 KB each. Removing a screenshot deletes the file, but it remains in the
  private repository's history.
- **Cost:** none. Private repositories are free.
- **Optional, decide separately:** encrypt screenshots in the browser (AES-GCM with a passphrase) before upload, so
  GitHub stores only ciphertext. Safer, but a forgotten passphrase means unreadable backups.

**Related question: should the board data move to the private repository too?** The board is turning into a
"second brain" with notes, interruptions and comments, which is more personal than development cards.

- **What moves:** `board.json` and `BOARD.md` would live in the private repository. The app could still be hosted
  for free from the public repository's Pages site.
- **Cost of moving:** the board then always needs sign-in to view, and Claude Code sessions need the private
  repository added.
- **Recommendation:** move it if you plan to capture non-project thoughts; otherwise keep it public, as agreed.

## 2. Email backup

### Where it runs: GitHub Actions (suitable, with caveats)

- **Schedule:** a daily scheduled workflow in the **private** repository.
  - Scheduled runs can start late (minutes, sometimes hours, at busy times) and are not guaranteed to the minute.
    That is fine for a daily backup.
  - GitHub disables scheduled workflows in *public* repositories after 60 days without activity. In the private
    repository the job's own state commits count as activity anyway.
- **Authentication:** the job reads the private repository with its built-in `GITHUB_TOKEN`. The board data is
  readable either way: publicly, or from the same private repository if it moves.
  - **The job cannot see screenshots that exist only in a browser.** Daily email backups of screenshots therefore
    depend on the screenshot storage above (WT-048).
- **Cost:** free. Private repositories get 2,000 Actions minutes a month on GitHub Free, and the job needs about
  one minute a day.

### Who sends the email

| Option | Credentials | "Accepted" signal | Verdict |
|---|---|---|---|
| SMTP with an Outlook/Microsoft account password | Password in a secret; basic SMTP authentication is being switched off by Microsoft | SMTP 250 | **Rejected** (insecure, being retired) |
| Microsoft Graph `sendMail` from your account | App registration plus a refresh token in a secret; renewal and consent maintenance | HTTP 202 | Possible, but fragile for a headless job |
| **Transactional email API** (Resend recommended; Postmark or Brevo as alternatives) | One send-only API key in an encrypted Actions secret | API returns a message id | **Recommended** |

- **Credential:** the API key lives only in the private repository's encrypted Actions secrets. It is never in the
  browser, the repository files or the logs.
- **Success rule:** a backup counts as successful only when the provider has accepted every part (message id
  returned).
- **Provider details to confirm when setting up the account, because they change:** free-tier volume (far above a
  few emails a day), maximum message size, and whether sending to your own address works without verifying a
  domain.

### What is sent

- **Grouping:** one archive per **creation month (UTC)**, using the ZIP format that Backup & Export already
  produces (`app/backup.js`).
  - Each archive holds every card created that month, with its complete latest state and all its screenshots.
  - Each archive restores on its own.
- **Change detection:** each month has a content hash (`contentHash()` in `app/backup.js`), compared with the hash
  recorded after the last successful send in `backup-state.json` in the private repository.
  - Only changed months are sent.
  - A changed older task causes its creation month to be resent.
  - Several changed months are each sent.
- **Subject:** `[WorkTrace backup] 2026-10 · part 1/1 · 14 tasks · 2026-11-02`. An Outlook rule matching
  "[WorkTrace backup]" can file these automatically.
- **Size:** Outlook.com rejects large messages (around 20 MB, and base64 makes attachments about a third larger).
  - Each email carries at most **10 MB** of attachments.
  - A larger month is split into numbered parts. Part 1 has the cards and manifest; later parts carry the
    remaining screenshots. The manifest lists every file and which part carries it.
  - Restore accepts all parts together.
  - Nothing is ever omitted. If any part fails, the month is not marked as backed up and is retried next run.
- **Failures:** the workflow fails visibly. The board shows the last successful backup, pending months and the last
  error, and **Retry** re-runs the job.

### Board settings (Backup & Export)

- **Recipient address:** stored in the private repository (`backup-config.json`), not in the public one.
- **Send backup now, Send test email and Retry:** each starts the workflow via `workflow_dispatch`. The token then
  also needs **Actions: Read and write** on the private repository.
- **Status:** last success, pending months and failures, read from `backup-state.json` and the latest workflow runs.

## Decisions needed

1. **Screenshot storage:** private GitHub repository (recommended), or keep them browser-only for now?
2. **Board data:** keep public (as agreed), or move to the private repository as well?
3. **Email provider:** Resend (recommended), Postmark, Brevo, or Microsoft Graph? You create the account and add its
   send-only key as an Actions secret. I never see the key.
4. **Size per email:** 10 MB of attachments per part (recommended)?
5. **Optional:** encrypt screenshots in the browser with a passphrase?

## After approval (estimated order)

1. Create the private repository (you), then extend the token to it (you, two clicks).
2. Screenshot sync: upload, display via the API, one-time upload of browser-only images (WT-048).
3. The backup job: monthly archives, change detection, parts, provider API, state file, workflow (WT-049).
4. Backup & Export settings: recipient, test email, send now, status, retry (WT-049).
5. Real tests: a test month, an oversized month that splits, a failing send that is retried, and a restore from email.
