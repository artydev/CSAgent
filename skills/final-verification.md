# 🛠️ SKILL: Final Verification
**Version:** 1.0
**Target runtime:** csAgent (uses `read_file`, `list_dir`, `tree`, `sh` / `run_terminal`, `git_status`, `git_diff`)
**Purpose:** Before saying a task is finished, check the result against the request with real evidence. "Done" means checked, not "I believe it works".

---

## 🎯 WHEN TO USE

Before the final answer of any task that **produced or changed something**: files, code, numbers, a report, a conversion, a batch operation. Skip for a plain explanation.

---

## ⚙️ CHECKLIST — run the ones that apply

### Against the request
- Re-read the user's original message. List each thing they asked for and tick it with the evidence (file path, command output, number).
- Anything asked but not done: say so explicitly. Do not hide it in a long summary.

### Files
- The expected files **exist** (`list_dir` / `tree`), are **non-empty**, and sit at the **requested location and name**.
- Open or read the beginning (and the end) of what you wrote; check encoding, accents and line endings are not garbled.
- Nothing unexpected left behind: temp files, copies, scratch outputs (`git_status` shows stray files).

### Numbers
- Totals, counts, averages and dates are **recomputed by a command or script**, never by estimating in the reply.
- State the source of each figure (which file, which column) and any row excluded.

### Code
- It **builds** and the **tests run**. Show the actual command and its result. Never write "tests pass" without having run them.
- If tests could not be run, say that, and why.
- `git_diff` shows only the intended changes; no debug output, no secrets, no unrelated edits.

### Batch operations
- Count before and after (files in, files out); explain any difference.
- Spot-check three items by reading them, not just by counting.

---

## 📣 HOW TO REPORT

- Lead with the outcome in one or two sentences.
- Separate **verified** (with the check you ran) from **not verified** (and why).
- Mention anything left for the user: a decision, a manual step, a limitation.

---

## 🚫 HARD CONSTRAINTS

1. Never claim a check you did not run.
2. Never report success when a step failed or was skipped.
3. A failed check is reported and, when possible, fixed — then re-checked. Do not pass over it.
4. Keep the report short; the evidence is in the commands, not in adjectives.
