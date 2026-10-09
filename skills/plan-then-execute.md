# 🛠️ SKILL: Plan Then Execute
**Version:** 1.0
**Target runtime:** csAgent (uses the normal tools; the plan lives in the conversation)
**Purpose:** For work with several steps, write a short numbered plan with a check for each step, follow it in order, and keep it up to date — so the work stays on track and the user can see what remains.

---

## 🎯 WHEN TO USE / WHEN NOT

**Use** when the task needs **three or more** distinct actions, touches **several files**, or has steps that depend on each other (build → test → fix → re-test).
**Do not use** for a single action or a quick answer.

---

## ⚙️ WORKFLOW

### 1. Write the plan (before the first tool call)
A numbered list, each step with **what** and **how you will know it worked**:
```
1. Inventory the .csv files in data/ — list_dir shows the files and sizes
2. Convert each to UTF-8 — each output file exists and is non-empty
3. Merge into all.csv — row count equals the sum of the inputs
4. Report — numbers recomputed by script, not estimated
```
Keep it to 3–8 steps. If it needs more, group them into phases.

### 2. Order by risk
Put read-only and reversible steps first, destructive ones (overwrite, delete, move) last and only after the earlier steps succeeded. Every destructive step must be preceded by a way back (a copy, a commit, or an untouched original).

### 3. Execute one step at a time
- Run the step, **read the result**, check it against the step's success test, then move on.
- Do not chain a second step on top of a result you have not looked at.
- Mark progress in your messages in one line ("Step 2/4 done: 12 files converted").

### 4. Replan when reality differs
If a step fails or reveals something unexpected, **stop and rewrite the remaining plan** in one or two lines ("Step 3 changes: the files have two header formats, so I will normalise first"). Do not silently improvise a different plan.

### 5. Close
Finish with the state of each step (done / skipped / not done and why). If a step could not be completed, say so plainly.

---

## 🚫 HARD CONSTRAINTS

1. Never start a destructive step without its way back.
2. Never mark a step done without its success check having been seen.
3. Never expand the plan beyond the request without telling the user.
4. Do not create `.csagent/tasks/` folders or `PLAN.md` files yourself: if a task folder exists (created by the host), fill its `PLAN.md`; otherwise the plan stays in the conversation.
