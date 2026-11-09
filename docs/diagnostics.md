# Registration diagnostics

`WorkTrace.exe --registration-report [yyyy-MM-dd] [--names]`

Writes `%LocalAppData%\WorkTrace\Diagnostics\registration-report-<date>.txt` and opens it. Default date: today.

- The database is opened **read-only**; nothing is changed. It can run while WorkTrace is running.
- Task and stakeholder names are replaced by numbers (`T12`, `S3`) unless `--names` is given. Notes are never included.
- Sections: settings, summary, chronological events (tracking start/end with reason, locks, prompts and outcomes,
  check-ins with their effect, manual registrations), the computed registrations with the reason for each boundary,
  and findings (unclassified time and why, check-ins outside tracking or during a lock, sleep/clock splits, gaps, overlaps).

Installed WorkTrace (per-user install):

```
"%LocalAppData%\Programs\WorkTrace\WorkTrace.exe" --registration-report 2026-11-09
```
