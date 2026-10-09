# 🛠️ SKILL: Task Framing
**Version:** 1.0
**Target runtime:** csAgent (no special tool; uses the conversation itself)
**Purpose:** Before starting a non-trivial request, state what will be done, on which assumptions and with what expected result — so a wrong reading is caught in one message, not after ten tool calls.

---

## 🎯 WHEN TO USE / WHEN NOT

**Use** for requests that are large, vague, or have several deliverables ("clean up this folder", "prepare a report", "fix the build and update the docs").
**Do not use** for a direct question, a one-line fix, or a task whose result is obvious. Framing a trivial request is noise.

---

## ⚙️ WORKFLOW

### 1. Read the request for what is missing
Check, silently: the **goal**, the **inputs** (which files / folder / data), the **output** (what to produce, which format, where), the **limits** (what must not be touched, deadline, language), and the **success test** (how the user will judge it).

### 2. Look before asking
If a missing item can be found with `list_dir`, `tree`, `search_files` or `read_file`, find it yourself. Never ask the user something the workspace can answer.

### 3. Write a short frame (5 lines maximum)
```
Goal: …
Inputs: …
Output: … (file, format, location)
Assumptions: … (each one the user could correct)
Out of scope: …
```
Answer in the user's language.

### 4. Ask at most ONE question — and only if it is blocking
A question is blocking when two plausible readings lead to different, costly work. Offer your best guess as the default ("I will assume X unless you say otherwise") so the user can answer with one word.
- If the user is not available to answer, **go ahead with the stated assumptions** and say so in the final answer.
- Never ask for confirmation of something reversible and cheap.

### 5. Start
After the frame, begin the work. Do not wait for approval unless the question was blocking.

---

## 🚫 HARD CONSTRAINTS

1. Never invent a requirement the user did not state ("while I'm in here…"). Scope grows only if the user asks.
2. Never ask more than one question at a time, and never several in a row.
3. Never present a guess as a fact: assumptions are labelled as assumptions.
4. Do not create task folders, tracking files or plans on disk for framing; the frame lives in the reply.
