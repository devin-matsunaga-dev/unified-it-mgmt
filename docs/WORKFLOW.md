# WORKFLOW.md — Complete Playbook: Setup → First Package → Every Package After

The one-sentence version: set up the machine once, bootstrap the repo once, then repeat a 10-step loop ~65 times. Codex kickoff is always: **"Read docs/SESSION.md and proceed."**

---

# PART A — Machine Setup (one-time, before anything)

## Version policy (why these versions)

LTS-or-latest-supported only. As of August 2026:

| Tech | Version | Support until | Notes |
|---|---|---|---|
| .NET SDK | **10 (LTS)** | Nov 2028 | Do NOT use 8 or 9 — both die Nov 2026 |
| Aspire | **13.x (latest)** | rolling | Only the latest release is supported; `aspire update` to stay current |
| Node.js | **24 LTS "Krypton"** | ~Apr 2028 | Node 26 becomes LTS Oct 2026 — optionally move then |
| Python | **3.13+ (3.14 preferred)** | Oct 2029 / Oct 2030 | Use 3.14 if pysnmp/deps are clean on it; 3.13 is the floor |
| React / Vite | **19 / latest** | rolling | Scaffold via latest `create-vite`; never pin an old template |
| PostgreSQL | **newest major TimescaleDB supports (17/18)** | 5 yrs/major | Timescale lags PG majors slightly — let it decide |
| Ubuntu (WSL + prod VM) | **26.04 LTS** (24.04 LTS fine) | 2031 / 2029 | Match dev distro and prod VM |

Rule for the whole project: at each phase gate, run `dotnet outdated` equivalents / `aspire update` / Dependabot review — never let a dependency cross its EOL while you're still building.

### Windows side
1. Install **WSL2 + Ubuntu 26.04 LTS**: `wsl --install -d Ubuntu-26.04` (admin PowerShell), reboot, create your Linux user.
2. Install **Docker Desktop** → Settings → Resources → WSL Integration → enable your distro.
3. Install **VS Code** + the **WSL extension**.
4. Create `C:\Users\<you>\.wslconfig`:
   ```
   [wsl2]
   memory=12GB
   processors=6
   ```
   Then `wsl --shutdown` once to apply.

### WSL side (Ubuntu terminal)
```bash
sudo apt update && sudo apt install -y git curl build-essential unzip pipx

# .NET 10 SDK — distro package if available, else Microsoft's install script:
sudo apt install -y dotnet-sdk-10.0 \
  || (curl -fsSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 10.0)

# Aspire CLI — per aspire.dev (one-line installer; also on WinGet/Homebrew/npm):
curl -fsSL https://aspire.dev/install.sh | bash

# Node 24 LTS via nvm:
curl -fsSL https://raw.githubusercontent.com/nvm-sh/nvm/master/install.sh | bash
nvm install 24 && nvm alias default 24

# Python 3.13/3.14 (26.04 ships a current one):
sudo apt install -y python3 python3-venv
python3 --version   # confirm 3.13+

# Codex CLI: install per its docs, then authenticate

git config --global user.name "Your Name"
git config --global user.email "you@example.com"
# GitHub auth: SSH key in WSL, or share Windows credential manager
```
**Sanity check:** `dotnet --version` (10.x), `aspire --version` (13.x), `node -v` (24.x), `python3 --version` (3.13+), `docker run hello-world`, `git --version` — all *inside WSL*.

> Rule that saves you the most pain: **the repo lives in WSL's filesystem (`~/projects/...`), never under `/mnt/c/`.** Builds, file-watchers, and Docker mounts are 5-10x faster and actually reliable.

---

# PART B — Repo Bootstrap (one-time)

```bash
cd ~ && mkdir -p projects && cd projects
mkdir it-platform && cd it-platform
git init -b main
mkdir docs
```

1. Copy the generated docs into `docs/`:
   `ARCHITECTURE.md`, `CONVENTIONS.md`, `DESIGN.md`, `STATUS.md`, `SESSION.md`, `WORK_PACKAGES.md` (the work-packages doc, renamed), and this file. Then:
   ```bash
   touch docs/DECISIONS.md
   mkdir -p docs/design
   # drop the UI reference screenshot in as docs/design/reference-overview.png
   ```
2. **Read ARCHITECTURE.md, CONVENTIONS.md, and DESIGN.md once, edit to your taste.** After this commit they are law — every Codex session obeys them verbatim.
3. Commit and push:
   ```bash
   git add -A
   git commit -m "chore: bootstrap steering docs"
   git remote add origin <your-remote-url>
   git push -u origin main
   ```
4. Open the project in VS Code: `code .` (opens connected to WSL; its integrated terminal is your WSL shell — do everything there).

---

# PART C — First Package (WP-0.1) — the loop's shakedown run

```bash
git checkout -b feat/wp-0.1-skeleton
```
1. Start Codex (CLI in the repo folder, or your chat setup) → say: **"Read docs/SESSION.md and proceed."**
2. Codex loads the docs, sees STATUS.md points at WP-0.1, and states its scope summary (solution skeleton targeting **net10.0**: AppHost, Web.Host, module assemblies, Platform, Contracts, tests, .gitignore, .editorconfig). Since the docs already exist, its summary should note that — if the summary matches, say **"go"**.
3. It builds → ends with the **Package Completion Report** → updates `STATUS.md` (0.1 ✅, Current WP → 0.2) and `DECISIONS.md` → stops.
4. Verify: `dotnet build` succeeds on net10.0; layout matches CONVENTIONS.md; walk its manual checklist.
5. Merge (the standard block — memorize this shape):
   ```bash
   git add -A
   git commit -m "feat: solution skeleton (WP-0.1)"
   git checkout main
   git merge --squash feat/wp-0.1-skeleton
   git commit -m "feat: solution skeleton (WP-0.1)"
   git push
   git branch -D feat/wp-0.1-skeleton
   ```
6. Close the chat. The system is now self-advancing.

---

# PART D — Every Subsequent Package (the steady-state loop)

**1. Orient (1 min).** Open `docs/STATUS.md` → note Current WP, its branch name, and any In-flight notes.

**2. Branch (1 min).**
```bash
git checkout main && git pull
git checkout -b feat/wp-X.Y-short-name    # name is written in STATUS.md
```

**3. Kick off (10 sec).** Fresh Codex chat → **"Read docs/SESSION.md and proceed."**

**4. Gate the scope.** Codex states its understanding of the WP and waits. Read it. Matches your intent → **"go"**. Doesn't → correct it now, before any code exists. (Bundling small WPs? Say: "do WP-2.6 and WP-2.7 together.")

**5. Build.** Let it work. It may run `dotnet add package` / `npm install` — approve when it matches the WP; new dependencies must be justified in the report. If it asks to exceed scope: "note it in STATUS.md under In flight, stay in scope."

**6. Receive.** It ends with the Completion Report, updates STATUS.md + DECISIONS.md, and stops on its own.

**7. Verify (15-45 min — your real job, never skipped).**
- Run the regression command (e.g. `dotnet test && npm test`). Red → step 8.
- `aspire run` (or `dotnet run` on the AppHost) → walk the manual checklist personally in your Windows browser (localhost forwards automatically). Click the things. Try the failure cases.
- `git diff main` in VS Code — skim normally; **line-by-line if the WP is [SENSITIVE]** (auth, credentials, bus, SLA math, automation).
- UI-touching WP? Open the screens next to `docs/design/reference-overview.png` — same tokens, same density, or it goes back.
- Glance at the STATUS.md / DECISIONS.md updates for accuracy.

**8. Fix loop (only if needed).** Same chat: *"Manual check 3 failed: expected 409, got 500. Fix."* → re-verify. Chat gone long and confused? Nuke and restart — that's what branches are for:
```bash
git checkout main && git branch -D feat/wp-X.Y-short-name
git checkout -b feat/wp-X.Y-short-name    # fresh branch, fresh chat, retry
```

**9. Merge (2 min).**
```bash
git add -A
git commit -m "feat(module): short description (WP-X.Y)"
git checkout main
git merge --squash feat/wp-X.Y-short-name
git commit -m "feat(module): short description (WP-X.Y)"
git push
git branch -D feat/wp-X.Y-short-name
```
*(`-D` is required after squash-merge — git can't tell the branch is merged; it's safe, the commit is on main.)*

**10. Close the chat.** Next package starts at step 1 — STATUS.md already points there.

### Phase gates (after a phase's final WP)
Run the 🏁 gate check from WORK_PACKAGES.md (e.g. Phase 0: fresh clone → `aspire run` → log in → ping event round-trips), **plus the dependency-health pass**: `aspire update`, review Dependabot PRs, confirm nothing in the version table above has crossed EOL. Then:
```bash
git tag vX.Y-phaseN && git push --tags
```

---

# PART E — Quick Reference Card

| Situation | Action |
|---|---|
| Start any session | `"Read docs/SESSION.md and proceed."` → read summary → `"go"` |
| Tests red / check failed | Tell the same chat exactly what failed; re-verify |
| Session confused/looping | Delete branch, re-branch, fresh chat, retry WP |
| WP fighting you 2+ sessions | Split it — edit WORK_PACKAGES.md, it's yours |
| Codex wants a new library | Must justify it; unlisted-in-ARCHITECTURE libs need your OK |
| UI package looks off-style | DESIGN.md + reference screenshot are law — compare side by side, have it restyle |
| Codex scaffolds with an old version (net8.0, Node 18 image, old Vite template) | Reject — versions table above is law; have it re-scaffold |
| Machine-level install (sudo/Windows) | You do it; Codex writes commands into docs/SETUP.md |
| localhost not reachable from browser | `wsl --shutdown`, relaunch |
| Phase's last WP merged | Run 🏁 gate + dependency-health pass → tag |

**Non-negotiables:** repo in WSL filesystem · main always green · manual checklist every package · line-by-line diff on [SENSITIVE] · one WP = one commit on main · LTS/latest-supported versions only.
