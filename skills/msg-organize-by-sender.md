# 🛠️ SKILL: Organize .msg Emails by Sender
**Version:** 1.0
**Target runtime:** csAgent (uses `read_msg`, `sh`/`run_terminal` with PowerShell, `tree`)
**Purpose:** Move a set of Outlook `.msg` email files into per-sender subdirectories, working around a verified gap in `read_msg`'s handling of accented filenames rather than failing on them.

---

## ⚠️ CRITICAL — VERIFIED TOOL LIMITATION

`read_msg` **cannot open a `.msg` file whose path contains accented characters** (é, è, à, ç, ô, etc.). It returns `Error: read_msg - not found '<path>'` even though the file exists on disk. This is a path-encoding limitation of the tool, not a missing file.

This matters because French-locale email filenames routinely contain accents (e.g. `Mobilisation du 5 novembre _ Communiqué de presse.msg`, `Newsletter UNSA Finances _ Pétition Rémunération _ ...`). Attempting to read them directly will fail every time.

**Workaround:** copy each accented file to a temporary name without accents (e.g. `temp1.msg`), read the temp copy with `read_msg`, then clean up. Do **not** try to hardcode accented paths into PowerShell `Copy-Item`/`Move-Item` commands — the shell↔PowerShell encoding mismatch makes those fail too. Enumerate files programmatically instead.

---

## 🎯 MISSION STATEMENT

Reliably determine the sender ("From" field) of every `.msg` file in a directory and move each one into a per-sender subfolder inside a target `emails` directory, preserving original filenames, without tripping over accented filenames or temp-file recursion.

---

## ⚙️ EXECUTION WORKFLOW

### PHASE 1 — INVENTORY
- List the `.msg` files in the working directory (`list_dir` or `dir /b`).
- Identify which filenames contain accented characters (these cannot be read directly).
- Confirm the target `emails` folder exists (create it if not).

### PHASE 2 — READ EACH EMAIL (via temp copies)
- Copy every original `.msg` file to a numbered temp name using PowerShell, **excluding any existing `temp*.msg`** so you don't copy temp files onto themselves (this caused recursive temp-file creation in the past):
  ```powershell
  $i=1; Get-ChildItem -Path 'C:\temp' -Filter '*.msg' | Where-Object { $_.Name -notlike 'temp*' } | ForEach-Object { Copy-Item $_.FullName ('C:\temp\temp' + $i + '.msg'); $i++ }
  ```
- Read each `tempN.msg` with `read_msg` and record the **sender** (the `From:` field) and which original file it corresponds to.
- Keep a mapping: `tempN.msg → original filename → sender`.

### PHASE 3 — CREATE SENDER DIRECTORIES
- For each unique sender, create a subdirectory inside `emails/` named after the sender (e.g. `emails/CFTC (DGCCRF)/`, `emails/Bureau 2A/`).
- Use `New-Item -ItemType Directory -Force` via PowerShell.

### PHASE 4 — MOVE FILES
- Move each original `.msg` file into its sender's directory, **preserving the original filename**.
- Enumerate the original files programmatically (by index) and pair them with the destination directories in order — do **not** hardcode accented paths:
  ```powershell
  $files = Get-ChildItem -Path 'C:\temp' -Filter '*.msg' | Where-Object { $_.Name -notlike 'temp*' }
  $dest = @('C:\temp\emails\<Sender1>', 'C:\temp\emails\<Sender2>', ...)
  for ($i=0; $i -lt $files.Count; $i++) { Move-Item $files[$i].FullName (Join-Path $dest[$i] $files[$i].Name) }
  ```
- **Important:** the enumeration order of `Get-ChildItem` must match the order in which you read the temp files, so the sender mapping stays correct. Verify each move's output line.

### PHASE 5 — CLEANUP & VERIFY
- Delete all `temp*.msg` files.
- Verify with `tree emails/` that every email sits in the correct sender folder.
- Confirm no `.msg` files remain at the root of the working directory.

---

## 🛡️ ERROR HANDLING & FALLBACKS

| Scenario | Fallback |
|---|---|
| `read_msg` returns "not found" on an accented path | It's the known limitation — copy to a temp name and read that (Phase 2). Do not retry the direct read. |
| PowerShell `Copy-Item`/`Move-Item` fails on a hardcoded accented path | Encoding mismatch — enumerate files programmatically (`Get-ChildItem`) instead of typing the accented path (Phase 2/4). |
| Temp files get copied onto themselves (recursive growth) | Always exclude `temp*` from the source enumeration (`Where-Object { $_.Name -notlike 'temp*' }`). |
| Sender mapping looks wrong after moving | Re-verify by reading the moved file's `From:` field, or re-check the enumeration order used in Phase 4. |

---

## 🚫 HARD CONSTRAINTS

1. Never call `read_msg` directly on a path containing accented characters — it will fail; use a temp copy.
2. Never hardcode accented paths into PowerShell commands — enumerate files programmatically.
3. Never include `temp*.msg` files in copy/move source lists — exclude them explicitly.
4. Always preserve the original filename when moving (do not rename to the temp name).
5. Always clean up temp files and verify the final structure before reporting completion.
