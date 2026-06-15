# UI follow-ups implementation plan — 2026-06-14

Branch: `feat/ui-followups-2026-06-14` (worktree `/Users/henry/Desktop/Burrow-work`, off `main` 0.7.1).
Grounded in a per-area survey of the real 0.7.1 tree (the prior survey read a stale checkout 55
commits behind — that checkout, `/Users/henry/Desktop/Burrow` on `fix/ci-streaming-apphang`,
must NOT be used for this work).

## Decisions (locked 2026-06-14)

1. **App updates auto-surface** — keep the "no silent network" contract. On Apps→Updates open:
   auto-run `brew outdated` (the user's own tool, no app-controlled egress) + show **cached**
   prior results; the first **live** third-party check (Sparkle appcast / iTunes lookup) still
   needs a click. No SECURITY.md change.
2. **Burrow self-update** — auto-check **ON by default**, at launch + ~daily; surface a found
   update as a **dismissible in-window banner + a menu-bar dot**; add About + Check-for-Updates
   to Settings with a disable toggle; **document the periodic GitHub GET in SECURITY.md**.
3. **Merge Purge + Installer into Clean** — one Clean pane with **three category cards**
   (Caches / Project artifacts / Leftover installers); drop the two top-nav pills; engines and
   views reused byte-for-byte; single teal theme.
4. **Heavy items — "do it all"** — build a signed privileged helper enabling fan Auto/Cool/Max,
   elevated system-daemon startup disable, and root purgeable-space reclaim. **See the blocker
   below.**

Defaults I chose without asking (say so, easy to revisit): camera/mic indicators **opt-in,
off by default**; **do not embed Sparkle** (third-party app updates hand off to each vendor's
own updater; only Burrow self-updates).

## ⚠️ Blocker on decision 4 (privileged helper)

A root helper installed via `SMAppService.daemon` (or `SMJobBless`) only loads if it is signed
with a stable **Developer ID Application** identity, with matching `SMAuthorizedClients` (helper)
and `SMPrivilegedExecutables` (app) bundle-id+designated-requirement pairs, and notarized. The
current release pipeline is ad-hoc / notarize-only-when-secrets-present. So I can write and wire
**all** the helper code, XPC protocol, SMC-write logic, and UI — but it will be **inert until
signed with the project's Developer ID**, which needs the Apple Developer account. Additionally:
fan manual-override via SMC (`FS! `, `F{n}Tg`) is **unreliable-to-unsupported on Apple Silicon**
firmware, so Auto/Cool/Max may be a no-op on exactly the hardware most users run. Helper writes
get a mandatory **auto-revert-to-Auto timer** + rpm clamped to the SMC-reported `F{n}Mn..F{n}Mx`
range, and a per-action root confirm for daemon disable. This track ships LAST, behind the safe
wins, and stays feature-flagged off until signing is sorted.

## PR sequence (each independently shippable + verified `xcodebuild`)

### PR-A · Clean & results  ✅ analyze done (`d3c2beb`)
- [x] **Analyze first-open progress** — cap 40→200 + bounded-concurrent per-child walk.
- [ ] **Unified result component** — `OperationResultView`: DoneBanner + summary header
      (primary) + TaskReportView body + a **demoted "View Log" disclosure**. Thread the raw
      stream through `OperationFlow` (add `@Published rawLog`; today lines are discarded at
      OperationFlow.swift:195-228) so View Log has content.
- [ ] **Purge/Installer result screens** — route their `MoInteractive` finish through the same
      `OperationResultView` (summary line from removal count + View Log), replacing the raw
      `resultText` dump (InstallerView.swift:366-387).
- [ ] **System busy badge (honest)** — populate `.systemBusy` only from a **prior run's
      per-path failure** (thread failed paths out of `parseTaskReport` into the next review's
      lock map). No static deny-list (would fight `CleanLockTests` `com.apple.helpd` = Safe).

### PR-B · Merge Clean (decision 3)
- [ ] Drop `.purge`/`.installer` from `Tool.navOrder` (keep enum cases — accents/copy/MCP/Explain
      keep compiling). Add a category chooser (three cards) to `CleanView`'s hero switching among
      `CleanView` body / `MoInteractiveView(.purge)` / `MoInteractiveView(.installer)`.
- [ ] Update Explain deep-links + `BURROW_OPEN_ON_LAUNCH` router + onboarding copy.

### PR-C · Updates & self-update (decisions 1 & 2)
- [ ] **Auto-surface** — `UpdatesModel`: auto-run brew on tab open via `SoftwareView` isActive
      hooks; persist + reload a results cache (Application Support JSON, keyed by bundleID);
      live third-party check stays click-gated.
- [ ] **Brew streaming** — route `brew upgrade` through `OperationFlow` for live progress.
- [ ] **Self-update** — new `AppUpdate` model (GitHub releases GET, semver compare, default-on
      `Store.autoCheckForUpdates`, ~daily); in-window banner (reuse the bottom overlay slot) +
      menu-bar dot; Settings About + Check-for-Updates rows; **SECURITY.md/TELEMETRY.md** note.

### PR-D · Camera/mic privacy indicators (opt-in)
- [ ] New `CameraMicSensor` (CoreMediaIO `DeviceIsRunningSomewhere` + CoreAudio equivalent,
      passive reads, no TCC). `Store.cameraMicIndicatorEnabled` (default false). Popover
      utility-strip row, only-when-active. Honest "in use" label (no fake attribution).

### PR-E · Tune-Up run-all (safe subset of decision 4)
- [ ] `TuneUpView` + `TuneUpRunner` sequencing existing `OperationFlow`s (Clean + Optimize,
      conservative default, per-step opt-out, visible pre-run plan, expanding section cards →
      done summary). Entry point: Home action (not a new tinted Tool pane). N honest auth
      prompts for now (helper pools them later).

### PR-F · Startup user-level disable (safe subset of decision 4)
- [ ] User-scope agent toggles (controllable = `scope==.user && !bundledInApp`): disable via
      `launchctl bootout gui/$UID + disable` (reversible). System/bundled stay read-only.

### PR-G · Privileged helper (decision 4 "do it all") — BLOCKED on signing
- [ ] `SMAppService.daemon` target + launchd plist + XPC protocol + entitlements
      (`SMPrivilegedExecutables`/`SMAuthorizedClients`).
- [ ] Helper: SMC fan write (`FS!`/`F{n}Md`/`F{n}Tg`) with rpm clamp + auto-revert timer;
      `/bin/launchctl` system-daemon disable behind per-action root confirm + deny-list;
      `/usr/sbin/tmutil thinlocalsnapshots` purgeable reclaim.
- [ ] App-side client + Status/Popover fan controls (Auto/Cool/Max) + Optimize purgeable task.
- [ ] Release pipeline: Developer ID Application signing for the helper. **Needs the Apple
      Developer account — inert until then.**

## Standing rules
mo stays authoritative for clean/uninstall (helper only adds fan/launchctl/tmutil, never
deletes user files); honest verbs + no fake affordances; zh-Hans + accessibility per PR;
keep SECURITY.md/TELEMETRY.md truthful on any new egress (PR-C self-update).
