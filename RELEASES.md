# Burrow 0.7.2

A feature release on top of the 0.7.0 redesign: cleanup, software management, and
the live dashboard all get more capable, with a couple of opt-in surfaces and a
quieter crash reporter. Everything stays local-first — no new always-on network.

## Cleanup
- **Purge and Installers fold into Clean.** One Clean tab now offers three
  category cards — system & app caches, project build artifacts, and leftover
  installers — instead of three separate tabs. Same engines, fewer pills.
- **Real result screens everywhere.** Every run now leads with the structured
  summary and tucks the raw terminal output behind a collapsed "View Log."
  Purge and Installers get a proper done screen instead of a wall of text.

## Software
- **Homebrew updates show up on their own.** Open Apps → Updates and your
  outdated `brew` packages are already listed — no "Check for updates" click.
  (Live App Store / Sparkle version checks still wait for the button, so the
  app keeps its "no silent network" promise.)
- **Startup items you can actually toggle.** Your own login agents now have an
  on/off switch right in the list; system- and app-managed items stay
  review-only, as macOS requires.

## Status & dashboard
- **Network charts read at a glance.** Both the Status tile and the History
  chart now draw download and upload as two separate lines — green down, blue
  up — instead of one combined trace.
- **Analyze feels alive.** The first scan of your home folder shows live
  per-folder progress ("● ~/Downloads · 3/12") instead of a static "Measuring…".
- **Optional camera/mic indicator.** The menu-bar popover can show when your
  camera or microphone is in use (off by default — turn it on in
  Settings → Menu bar). It reads the same system signal as Control Center.

## Staying current
- **Burrow checks itself for updates.** On by default (once a day, one
  lightweight request), it shows a banner and a menu-bar dot when a new version
  is out — and never installs anything on its own. "Check for Updates" and
  "About Burrow" now live in Settings too. You can turn the auto-check off.

## Fewer false alarms
- **Confirm dialogs are no longer reported as freezes.** Pausing at a
  confirmation or the Touch ID prompt blocks the main thread by design; the
  crash reporter no longer mistakes that for an app hang.
