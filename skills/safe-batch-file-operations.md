# 🛠️ SKILL: Safe Batch File Operations
**Version:** 1.0
**Target runtime:** csAgent (uses `list_dir`, `tree`, `search_files`, `copy_file`, `move_file`, `delete_file`, `zip`, `sh` / `run_terminal`)
**Purpose:** Rename, move, convert, classify or delete many files without losing any: show the plan first, work on copies or with a way back, verify the result by counts.

---

## 🎯 WHEN TO USE

Any operation applied to **about five files or more**, or to a whole folder, or that overwrites or deletes. For one or two files, act directly.

---

## ⚙️ WORKFLOW

### PHASE 1 — INVENTORY
- List exactly what is concerned (`list_dir` / `tree`; `search_files` for patterns). Record the **count** and total size.
- Note odd names: accents, spaces, double spaces, very long names, duplicates that differ only by case.
- **Take paths from the listing; do not retype them.** Names with accents or unusual characters often break when typed again. Enumerate programmatically (a loop in the shell) rather than writing each path by hand.

### PHASE 2 — PLAN (show it, then wait if anything is destructive)
Show a table or list **source → destination**, for every file, or for a representative sample plus the rule if there are more than ~30:
```
Rule: move *.pdf to archive/<year>/ using the year in the file name
12 files → archive/2025/ (7), archive/2026/ (5); 0 unknown
```
Also show: collisions (two files with the same target name), files the rule does not cover, and anything that will be overwritten. If the user has not clearly approved a destructive plan, **ask once and wait**.

### PHASE 3 — SAFETY NET
Choose the lightest way back that fits:
- **Copy, don't move**, when the originals might still be needed (`copy_file`, then delete the originals only after verification).
- A `zip` of the folder before an in-place change.
- A text log of every operation (old name → new name) saved next to the results, so a rename can be undone.
- Never overwrite: on a name collision, add a suffix like ` (1)` and report it.

### PHASE 4 — DRY RUN, THEN A SMALL TEST
- If a `--dry-run` or "what-if" option exists, use it first.
- Otherwise run the operation on **one or two files**, inspect the result, then run the rest.

### PHASE 5 — EXECUTE
- One operation per file, in a loop; stop at the first unexpected error instead of continuing blindly.
- Do not process the output folder as input (a classic recursion: temp or result files being picked up again). Exclude it explicitly.
- Deletions come **last**, as a separate step, with their own confirmation.

### PHASE 6 — VERIFY AND REPORT
- Count again: files in = files out + files skipped, with each difference explained.
- Spot-check three results by reading or opening them.
- Report: what was done, counts, anything skipped or renamed because of a collision, where the log and the safety copy are, and how to undo.

---

## 🚫 HARD CONSTRAINTS

1. Never delete or overwrite without a way back and the user's clear approval.
2. Never run a batch whose plan the user has not seen when it deletes, overwrites or renames in place.
3. Never retype a path that came from a listing; enumerate it.
4. Never continue after an unexpected error without reporting it.
5. Never touch anything outside the current working directory.
