# Lessons

## 2026-09-28 — Settings entry placed where the user doesn't look
- **What happened:** Settings was only added to the launcher top-level command's MoreCommands. The user opens SysPulse from the Dock flyout, so "More commands" there had no Settings.
- **Rule:** Put every user-facing action on the surface the user actually uses (Dock flyout first, launcher second). When a feature has an "entry point", list each surface explicitly in the agent spec and in the test steps.

## 2026-09-28 — Blind string Replace hit two call sites
- **What happened:** A `.Replace()` on `OnBandLoaded, OnBandUnloaded);` also changed the `OnLoadDockBandItem(...)` call, breaking the build.
- **Rule:** Before a text replace, grep the pattern and confirm it is unique; otherwise use the Edit tool with enough surrounding context.
