# Skills Index

This file is the single entry point for csAgent's skill library. Read this file
first — it is small by design. Only read a skill's full `.md` file (via
`read_file`) if its description below actually matches the current task.

Do not read every skill file "just in case." That defeats the purpose of this
index.

| Skill | Version | Use when… | File |
|---|---|---|---|
| Tech-Watch Intelligence | 3.0 | The user asks for current tech/AI/science/cybersecurity news, a "veille techno," or wants a summarized brief of recent developments in a technology domain. | `skills/tech-watch-intelligence.md` |
| Excel Report Builder | 1.2 | The user wants raw data turned into a formatted Excel report (pasted, fetched, or read from a file), with professional styling and a confirmed save. | `skills/export-report-builder.md` |
| CSV Handling | 1.0 | The user asks to read, parse, import, or work with a `.csv` file — especially before turning it into a report or passing it to another tool. | `skills/csv-handling.md` |
| Pre-Commit Review & Message Drafting | 1.0 | The user asks to commit changes, or asks for a commit message — before any `git_commit` call. | `skills/precommit-review.md` |
| Organize .msg Emails by Sender | 1.0 | The user asks to sort, organize, or move Outlook `.msg` email files into per-sender folders, especially when filenames contain accented characters. | `skills/msg-organize-by-sender.md` |
| Task Framing | 1.0 | The user gives a large, vague or multi-deliverable request ("prépare…", "nettoie…", "fix X and update Y") — before starting; not for simple questions or one-line fixes. | `skills/task-framing.md` |
| Plan Then Execute | 1.0 | The task needs three or more dependent steps or touches several files — write and follow a short plan with a check per step. | `skills/plan-then-execute.md` |
| Final Verification | 1.0 | Before the final answer of any task that produced or changed files, code, numbers or a report — check the result against the request with real evidence. | `skills/final-verification.md` |
| Failure Recovery | 1.0 | A tool call or command has failed twice, or the same error repeats, or a result is clearly wrong — diagnose and change approach instead of retrying. | `skills/failure-recovery.md` |
| Safe Batch File Operations | 1.0 | The user asks to rename, move, convert, classify, archive or delete about five or more files, or a whole folder. | `skills/safe-batch-file-operations.md` |

---

## Maintenance

- Adding a skill: create its `.md` file in `skills/`, then add one row here —
  name, version, a one-sentence trigger description precise enough to match
  real user phrasing, and the file path. No code change or rebuild required.
- Updating a skill: bump the version number here to match the file's own
  `**Version:**` header, so the two never silently drift out of sync.
- Removing a skill: delete its row here and its file. An orphaned row (file
  path that no longer exists) will cause a read_file error if ever matched —
  keep this table in sync with the actual folder contents.
- Keep each trigger description to one sentence. If a skill needs paragraphs
  to explain when it applies, that description belongs in the skill file
  itself, not here — this index must stay cheap to read every session.