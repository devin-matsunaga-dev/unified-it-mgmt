# SESSION.md — Codex Session Kickoff

**If you (Codex) have been told to read this file, this is your complete instruction set for the session. Follow it exactly.**

## Step 1 — Load context (in this order)

1. `docs/ARCHITECTURE.md` — system design and invariants. Binding.
2. `docs/CONVENTIONS.md` — code standards. Binding.
3. `docs/DESIGN.md` — UI design system (required for any WP touching the frontend; reference screenshot at `docs/design/reference-overview.png`). Binding.
4. `docs/STATUS.md` — current project position and in-flight notes.
5. `docs/DECISIONS.md` — settled choices. Do not relitigate any of them.

## Step 2 — Identify the work package

- Read **Current WP** from `docs/STATUS.md`.
- Find that WP's full text in `docs/WORK_PACKAGES.md` and treat it as your specification.
- Confirm the git branch matches `feat/wp-X.Y-*` for that WP (check with `git branch --show-current` if you have shell access). If it doesn't match, STOP and tell me before writing anything.
- State back to me in 3-5 bullets: the WP number, your understanding of scope, and anything ambiguous. **Wait for my "go" before implementing.**

## Step 3 — Implementation rules

1. Implement ONLY this work package's scope. If something outside scope seems necessary, stop and ask; the default answer is "record it in STATUS.md under In flight and stay in scope."
2. Follow ARCHITECTURE.md and CONVENTIONS.md exactly. No new patterns, folders, or libraries without asking.
3. Write automated tests for everything (unit for logic; Testcontainers integration for anything touching DB/bus). Include at least one failure-path test. Run them and show results.
4. Never modify: auth configuration, credential handling, bus topology, or applied migrations from previous packages — unless the WP text explicitly says so.
5. If the WP is marked **[SENSITIVE]**, keep changes minimal and flag every security-relevant line in your report.

## Step 4 — Completion (do all of this, then STOP)

Produce a **Package Completion Report**:

1. **Changes** — files added/modified, one line of purpose each.
2. **Automated tests** — what you wrote, coverage summary, pass/fail output.
3. **Manual verification checklist** — numbered steps for me to perform personally: exact URLs/commands, expected results, at least one failure-path check (e.g., "submit invalid payload → expect 400 ProblemDetails"). Merge in the "Verify" bullets from the WP text — don't drop any.
4. **Regression command** — the one command proving nothing prior broke.
5. **Git suggestion** — commit message in conventional format referencing the WP.

Then update the docs:

6. Append new decisions to `docs/DECISIONS.md` ("chose X over Y because Z", one line each; if none, say "no new decisions").
7. Update `docs/STATUS.md`: check off this WP, set **Current WP** to the next one, update **Current branch** to its expected name, and record anything unfinished under **In flight**.

Then **STOP**. Do not begin the next work package. The human will verify, review, merge, and open a new session.

## If I report a failed verification

Fix only what's needed to make that check pass, re-run the affected tests, and issue an updated report (deltas only). Do not refactor unrelated code while fixing.
