# 🛠️ SKILL: Failure Recovery
**Version:** 1.0
**Target runtime:** csAgent (all tools)
**Purpose:** When a tool call or command fails, diagnose it and change approach instead of retrying the same thing — and stop cleanly when it is not solvable.

---

## 🎯 WHEN TO USE

- The **same** error appears twice, or two attempts in a row fail.
- A command "succeeds" but the result is clearly wrong or empty.
- You catch yourself about to repeat a call with a tiny variation.

---

## ⚙️ WORKFLOW

### 1. Stop and read the error
Read the **whole** message once. Note: which tool, which arguments, what the error says, what it does not say. A path error, a permission error, a timeout, a bad argument and a missing program need different fixes.

### 2. Classify
| Class | Typical sign | First move |
|---|---|---|
| Wrong input | "not found", "invalid argument" | Check the real path / name with `list_dir`, `tree` or `search_files`; do not retype it from memory |
| Environment | "command not found", missing program | Check availability (`sh` with `where` / `which`); do not install anything without asking |
| Permission / policy | "not allowed", "refused", outside the working directory, blocked command | Do not try to bypass it; use an allowed route or tell the user |
| Timeout / size | timeout, "too large" | Narrow the work (one file, `head`, a filter) or use a longer-running tool |
| Logic | the command works but the output is wrong | Re-read your assumption; test on a tiny example |

### 3. Change something real
Each new attempt must differ in a way you can **explain in one sentence**. Never repeat a failed call unchanged.
Limit: **two failed attempts of the same approach, then switch approach**; **after about four failures in total, stop**.

### 4. Never "fix" by going around a safety rule
Do not work around a refused path, a blocked shell command, or a confirmation. Report it and propose an alternative that stays inside the rules.

### 5. Stop cleanly
When stopping, give the user:
- what you tried (one line per attempt),
- what you learned (the likely cause),
- what you need from them (a file, a permission, a decision) or the best partial result you have,
- the exact state left on disk (anything half-done, any temp file).

---

## 🚫 HARD CONSTRAINTS

1. Never retry the same call unchanged more than once.
2. Never hide a failure: a failed step is reported even if you recovered.
3. Never bypass a safety rule, a path limit or a confirmation to get a command through.
4. Never leave a half-finished destructive operation without saying so.
