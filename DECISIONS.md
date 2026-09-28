# DECISIONS

SDK deviations, trade-offs and measurements for SysPulse. Research details and sources: `docs/sdk-research.md`.

## D1 — SDK version: `Microsoft.CommandPalette.Extensions` 0.12.260812002
- Template generated 0.9.260303001 (spec minimum). Installed host (PowerToys 0.101 / CmdPal 0.12) ships SDK **0.12.260824005**, so the latest nuget.org release (0.12.260812002) is safe and gives access to `DockLabelWidth` APIs.
- The Toolkit is **not** a separate package; `Microsoft.CommandPalette.Extensions.Toolkit.dll` ships inside the same package (spec §2 table lists it separately).

## D2 — Project split: `SysPulse` + `SysPulse.Core` + `SysPulse.Tests`
- All SDK-independent logic (samplers, state machine, ranking, protected list, options validation) lives in `SysPulse.Core` so xUnit tests don't reference the MSIX/COM-server project. Spec §4 tree shows a single project.

## D3 — Monitor runs only while the band is on screen (lifecycle gating)
- The host constructs the provider and its bands even when nothing is pinned. The host subscribes to a band page's `ItemsChanged` when the band is rendered and unsubscribes when it goes away; that is the only load signal.
- SysPulse ports `OnLoadDynamicListPage` / `OnLoadDockBandItem` (MIT, from PowerToys `Microsoft.CmdPal.Ext.TimeDate`) and starts/stops the monitor loop from those callbacks (ref-counted with the Top-5 page being open). Spec §4.1 said "monitor starts when the provider is constructed" — replaced by this.
- Consequence: with the band unpinned, no background sampling and no toasts.

## D4 — Threading
- Confirmed (TimeDate `NowDockBand`): item properties are set directly from a timer thread; no dispatcher. Properties set only when the string changes. Every set wrapped in try/catch (issue #50483: a throwing host handler blocked later updates).

## D5 — Settings: no numeric setting type
- Toolkit offers Text/Toggle/ChoiceSet only. Numeric keys use `TextSetting` + parse + clamp (`SysPulseOptions.Normalize`).

## D6 — Localization uses `.resx`
- Built-in CmdPal extensions (e.g. TimeDate) use `.resx` + `PublicResXFileCodeGenerator`. Spec §7 says `.resw`.

## D7 — Dock label width
- Use the SDK's `DockLabelWidth` / `SetDockLabelReservations` (as Performance Monitor does) to prevent width jitter. `CompactLabel` setting is kept as the spec requires, for narrow/vertical docks.
- **Verified unavailable in the installed package (2026-09-28):** `Microsoft.CommandPalette.Extensions.Toolkit.dll` from NuGet `Microsoft.CommandPalette.Extensions` 0.12.260812002 has no `DockLabelWidth`/`DockLabelWidthExtensions`/`DockLabelPresentationExtensions` types at all (confirmed via `System.Reflection.PortableExecutable.PEReader` type-table dump of the DLL — zero matches for `DockLabel*`, vs. `WrappedDockItem`/`IExtendedAttributesProvider` which are present). The PowerToys `main`-branch source docs/sdk-research.md cites for this API post-dates this NuGet release. `StatusDockItem` therefore does **not** call these APIs; `Dock\StatusDockItem.cs` documents this in a code comment. Same finding applies to `EventHelpers` (used by upstream `OnLoadDynamicListPage.RaiseItemsChanged`): absent from the installed Toolkit DLL, so the ported `SysPulse.Dock.OnLoadDynamicListPage` invokes its internal event delegate directly instead.

## D8 — Band shape
- Single status item whose `Command` is `TopProcessesPage` (click → flyout), hosted by the ported `OnLoadDockBandItem` instead of `WrappedDockItem` (needed for D3).

## D9 — Process sampling via `NtQuerySystemInformation` (planned, D-phase)
- One call returns all processes (PID, name, create time, CPU times, private bytes, working set) without opening handles; elevated processes are included. Spec §5.1 prescribes `Process.GetProcesses()`.

## D10 — Toast via WinRT `ToastNotificationManager` (planned, G-phase)
- No COM activator registration needed. Click-to-open Top-5 is out of scope (no host API).

## Future options
- `GroupByName` (group multi-process apps in Top-5) — not in v1.
