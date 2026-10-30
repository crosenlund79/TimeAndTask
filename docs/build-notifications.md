# "New version ready" emails

After every successful Windows build, CI (`.github/workflows/ci.yml`) can email you a link to the new
installer (`WorkTrace-Setup-<version>`).

## When an email is sent
Only when **all** of these hold:
- the run was a push (or a manual *Run workflow*) on `main`, `feature/**` or `claude/**`. Pull requests and the `board`
  branch never send.
- restore, build, **all tests**, publish, the **installer** and the **artifact uploads** succeeded. A failed or cancelled run stops before
  the email step.
- the push changed something besides documentation (files under `docs/` or `*.md`).
- no email was sent for this commit before. A cache entry `build-email-<commit>` marks a sent commit, so re-runs
  don't resend, and Resend's idempotency key covers simultaneous runs.
- both secrets below are set. Without them the step is skipped with a notice and the build stays green.

If sending fails (wrong key, Resend down), the run shows a warning and stays green. The key is never printed.

## Delivery: Resend (free)
[Resend](https://resend.com) is an email API. The free plan sends 3,000 emails/month (100/day), far more than needed.
Without your own domain it sends from `onboarding@resend.dev`, **only to the email address you signed up with**,
which is all a personal notification needs.

## Setup (about 5 minutes)
1. Create a free account at https://resend.com/signup **with the address that should receive the emails**.
   Confirm it.
2. In Resend: **API Keys › Create API Key**. Name: `WorkTrace CI`. Permission: **Sending access** (not Full access).
   Domain: *All domains*. Copy the key (`re_…`). It is shown only once.
3. In GitHub: **crosenlund79/TimeAndTask › Settings › Secrets and variables › Actions › Secrets › New repository
   secret**, and add two secrets:
   | Name | Value |
   |---|---|
   | `RESEND_API_KEY` | the key from step 2 |
   | `WORKTRACE_NOTIFY_EMAIL` | the address from step 1 (several: separate with commas) |
   Both are secrets because this repository and its CI logs are public.
4. Optional, under **Variables**: `WORKTRACE_NOTIFY_FROM`, for example `WorkTrace <builds@your-domain>`. This only works
   after you verify your own domain in Resend, which also lets you send to other addresses.
5. Test it: **Actions › CI › Run workflow** on your branch. On the first run for a commit you get
   "✅ WorkTrace — New version ready". Check spam once and mark it *Not spam*.

To stop the emails, delete either secret. To revoke the key, delete it in Resend.

## The email
- **Subject:** ✅ WorkTrace — New version ready
- **Link:** the installer artifact of that exact run (`…/actions/runs/<run>/artifacts/<id>`). Downloading needs a
  GitHub sign-in, and installers are kept 30 days. The email also names the version.
- **Changes:** the commit subjects of the push (up to 15), then the full commit hash, the branch and a link to the CI run.
