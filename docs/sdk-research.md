# SysPulse — CmdPal SDK Research (2026-09-28)

Sources: `microsoft/PowerToys` GitHub repo (`gh api`, current `main` branch, fetched 2026-09-28),
`learn.microsoft.com/windows/powertoys/command-palette/*` (fetched live), `nuget.org` API, and the
local NuGet cache (`%USERPROFILE%\.nuget\packages`). Every claim below is either backed by a cited
file/URL or marked **UNVERIFIED**.

Local NuGet cache observed: `microsoft.commandpalette.extensions` present in **two** versions:
`0.9.260303001` and `0.11.260520004` (`%USERPROFILE%\.nuget\packages\microsoft.commandpalette.extensions\`).
There is **no separate `microsoft.commandpalette.extensions.toolkit` folder** in the cache — see §9.
The already-scaffolded `SysPulse\SysPulse\SysPulse.csproj` currently restores `0.9.260303001`
(`SysPulse\obj\project.assets.json`), pinned via `SysPulse\Directory.Packages.props`.

---

## 1. `GetDockBands()`, `WrappedDockItem`, Id requirements, ICommandProvider3/4

Confirmed by the WinRT IDL (source of truth for all managed interfaces):
`src/modules/cmdpal/extensionsdk/Microsoft.CommandPalette.Extensions/Microsoft.CommandPalette.Extensions.idl`

```idl
interface ICommandProvider3 requires ICommandProvider2 {
    ICommandItem[] GetDockBands();
};
interface ICommandProvider4 requires ICommandProvider3 {
    ICommandItem GetCommandItem(String id);
};
interface ICommandItem requires INotifyPropChanged {
    ICommand Command{ get; };
    IContextItem[] MoreCommands{ get; };
    IIconInfo Icon{ get; };
    String Title{ get; };
    String Subtitle{ get; };
}
```

In C#, `CommandProvider` (toolkit base class) exposes these as overridable methods
`ICommandItem[]? GetDockBands()` and `ICommandItem? GetCommandItem(string id)` — spec §3 is correct.

`WrappedDockItem` — `extensionsdk/Microsoft.CommandPalette.Extensions.Toolkit/Dock/WrappedDockItem.cs`:

```csharp
public partial class WrappedDockItem : CommandItem
{
    public WrappedDockItem(ICommand command, string displayTitle);
    public WrappedDockItem(IListItem[] items, string id, string displayTitle);
}
```

Exact signature matches spec §3's `WrappedDockItem(IListItem[] items, string id, string displayTitle)`.
Internally it wraps the items in a `WrappedDockList : ListPage` (same file's sibling
`WrappedDockList.cs`) whose `Id` becomes the band's id and whose `GetItems()`/`SetItems()` use
`ListHelpers.InPlaceUpdateList` to update items **in place** rather than replacing the array — this
is the toolkit's own leak-avoidance helper (see §2).

Note a **removed** constructor, kept as a commented-out warning in the same file: a
`WrappedDockItem(ICommandItem item, string id, string displayTitle)` overload existed and was
deleted because "This was too much of a foot gun — we'd internally create a ListItem that didn't
bubble the prop change events back up." Only use the two constructors above.

**Id requirement** confirmed by `learn.microsoft.com/.../adding-dock-support`: "All `ICommandItem`
objects returned from `GetDockBands()` must have a `Command` with a non-empty `Id`. Items without an
ID are ignored." Matches spec §3.

**Important — GetDockBands() need not return WrappedDockItem at all.** The built-in Performance
Monitor provider (`ext/Microsoft.CmdPal.Ext.PerformanceMonitor/PerformanceMonitorCommandsProvider.cs`)
returns plain `CommandItem(page) { Title = ... }` and `WrappedDockItem([...], ...)` only for its
*disabled* placeholder state. A single-button band (spec's "compact CPU/MEM readout, click → open
flyout") is more naturally a bare `CommandItem` whose `Command` is an `IListPage` (renders as an
expandable flyout per the docs' table below), not necessarily wrapped in `WrappedDockItem` (which is
for multi-button strips). Table from the docs page:

| Command type on the returned `ICommandItem` | Dock behavior |
|---|---|
| `IInvokableCommand` | single button, executes on click |
| `IListPage` | all page items render as buttons in one band |
| `IContentPage` | single expandable button with a flyout |

Source: `https://learn.microsoft.com/en-us/windows/powertoys/command-palette/adding-dock-support`.

---

## 2. `NowDockBand` update thread/dispatcher; Performance Monitor structure; leak fix pattern

**This is the single biggest deviation from the spec.** The spec's §2/§3 describe `NowDockBand` as a
simple always-ticking `ListItem`. The *current* `main` branch implementation is materially different
and reflects the post-leak-fix design:

`ext/Microsoft.CmdPal.Ext.TimeDate/NowDockBand.cs` (current):
- `NowDockBand : ListItem, IDisposable` — plain `Title`/`Subtitle` setters, **no dispatcher/marshalling
  of any kind**. It's a bare-thread-synchronous update: `Title = timeString; Subtitle = dateString;`
  called from `ClockUpdateService_Tick`, which itself runs off a `System.Timers.Timer` elapsed
  callback (`ClockUpdateService.cs`, `Timer_Elapsed` → `DispatchTick` → `InvokeHandlers` on the
  timer's own thread pool thread). **Confirms plan §1 item 5: "NowDockBand properties are set from a
  plain timer thread; no dispatcher required."**
- Only sets `Title`/`Subtitle` **when the string actually changed** (`if (timeString == Title &&
  dateString == Subtitle) return;`) — matches spec §5.3's "only raise property changes when the
  displayed string actually changes."
- **Lifecycle gating is the real leak fix**, not just "reuse instances." `NowDockBand` does **not**
  tick unconditionally; it only subscribes to the shared `ClockUpdateService` when
  `StartUpdating()`/`StopUpdating()` are called. Those are wired through a wrapper,
  `OnLoadDockBandItem` (`ext/Microsoft.CmdPal.Ext.TimeDate/Pages/OnLoadDockBandItem.cs`):

  ```csharp
  internal sealed partial class OnLoadDockBandItem : CommandItem
  {
      internal OnLoadDockBandItem(IListItem[] items, string id, string bandTitle,
          Action onLoaded, Action onUnloaded);
  }
  internal sealed partial class OnLoadDockBandPage : OnLoadDynamicListPage
  {
      protected override void Loaded() => _onLoaded();
      protected override void Unloaded() => _onUnloaded();
  }
  ```
  Doc comment: "CmdPal attaches an items-changed handler to a band's page when the band goes on
  screen and removes it when the band goes away; that pairing is the only signal an extension gets
  ... Bands are constructed for every clock the user has defined, whether or not any of them are
  pinned, so without this an unpinned clock would tick for the whole session." This is a materially
  more important leak-avoidance pattern for SysPulse than "reuse item instances": **a provider is
  instantiated even if its band is never pinned**, so the sampling timer must be lazy-started only
  when the band/page is actually loaded/rendered, and stopped when unloaded — not just started once
  in the constructor and left running for the process lifetime as the spec's §4.1 comment
  ("The monitor starts when the provider is constructed") assumes.
  `OnLoadDynamicListPage` itself was not located in the fetched file set (base class location
  **UNVERIFIED** — likely in `Microsoft.CmdPal.Common`); its contract (`Loaded()`/`Unloaded()`
  virtuals) is demonstrated by this override.

- `ClockUpdateService` (`ext/Microsoft.CmdPal.Ext.TimeDate/ClockUpdateService.cs`) is a **shared,
  extension-wide** single timer that multiple bands/pages subscribe to (`Subscribe`/`Unsubscribe`),
  rather than each band owning its own timer — worth mirroring for `HealthMonitor` if multiple bands
  are ever added, though SysPulse only needs one.

**Performance Monitor structure** (`PerformanceMonitorCommandsProvider.cs`):
- Holds one `PerformanceWidgetsPage` per metric kind (`_cpuBandPage`, `_memoryBandPage`, etc.),
  constructed once in `SetEnabledState()` and disposed in `DisposeActivePages()`.
- `GetDockBands()` returns `CommandItem(_cpuBandPage) { Title = ... }` — **not** wrapped in
  `WrappedDockItem** — for single-metric bands. It only uses `WrappedDockItem` for its *disabled*
  placeholder bands (`SetDisabledState()`), each holding one static `ListItem`.
- Uses `DockLabelWidth`/`SetDockLabelWidthLimits`/`SetDockLabelReservations` (toolkit, see §5.3 below)
  to reserve fixed-width label space (e.g. `DockLabelWidth.Sample("100%")`) so the band doesn't jitter
  in width as the percentage changes — directly relevant to spec's `CompactLabel`/truncation concern.

**The actual leak fix PR cited by the spec** is **PR #48880**
(`https://github.com/microsoft/PowerToys/pull/48880`, "[CmdPal] Fix memory leak in
PerformanceWidgetsPage network band items"): `GetItems()` was creating **new** `ListItem` instances
for `_networkUpItem`/`_networkDownItem` on every call; the old instances stayed referenced by
`DockItemViewModel` wrappers until the next refresh, leaking ~2 objects/sec. Fix: move item
construction into the constructor so `GetItems()` always returns the same stable references, and let
the `Updated` handler mutate `.Title` in place (which raises `PropChanged`). **Confirms spec §3's
pitfall exactly**: "Always reuse stable item instances; only mutate their properties."

A **second**, more comprehensive fix landed later in **PR #49742** ("CmdPal: Fix Dock refresh
resource leak", closes issue #49428): reuses Dock item view models while source items are stable,
coalesces bursty `ItemsChanged` notifications, cleans up replaced/discarded view models, and prevents
queued refreshes from repopulating bands after cleanup — this is host-side (`Microsoft.CmdPal.UI`)
code, not something an extension author touches directly, but it means recent PowerToys builds are
more forgiving of the old leak pattern than earlier ones; still reuse instances regardless.

---

## 3. Band item opening a `ListPage` flyout on click

Confirmed by the docs table in §1: an `ICommandItem` whose `Command` is an `IListPage` renders all of
the page's items as buttons in that one band; clicking the compact "band" item itself (if its own
`Command` resolves to a page) opens a flyout. Concretely, for SysPulse: give the single status
`ListItem`'s `Command` property a `ListPage` (the `TopProcessesPage`) — `WrappedDockItem` isn't
required for a single-button-opens-flyout band; a bare `CommandItem(topProcessesPage) { Title = ...
}` returned from `GetDockBands()` is the simpler, more idiomatic shape (mirrors how
`PerformanceMonitorCommandsProvider` returns `CommandItem(_cpuBandPage)` directly). Source:
`https://learn.microsoft.com/en-us/windows/powertoys/command-palette/adding-dock-support` +
`ext/Microsoft.CmdPal.Ext.PerformanceMonitor/PerformanceMonitorCommandsProvider.cs`.

---

## 4. Settings: `JsonSettingsManager`, `Settings`, setting types, change notification, file location

`extensionsdk/Microsoft.CommandPalette.Extensions.Toolkit/JsonSettingsManager.cs`:

```csharp
public abstract class JsonSettingsManager
{
    public Settings Settings { get; } = new();
    public string FilePath { get; init; } = string.Empty;
    public virtual void LoadSettings();   // reads FilePath, calls Settings.Update(json)
    public virtual void SaveSettings();   // merges + writes FilePath
    protected virtual void LoadAdditionalSettings(JsonObject settings);
    protected virtual void SaveAdditionalSettings(JsonObject settings);
}
```
You subclass this, add typed `Setting<T>` instances to `Settings` via `Add<T>(Setting<T>)`, and set
`FilePath` yourself — **the toolkit does not choose the path for you**; built-in extensions
(`ext/Microsoft.CmdPal.Ext.TimeDate/Helpers/SettingsManager.cs` and PerformanceMonitor's
`SettingsManager.cs`) compute it themselves, typically under
`Utilities.BaseSettingsPath("<extension-id>")` → `%LOCALAPPDATA%\Microsoft\CommandPalette\...` or the
package's `ApplicationData.Current.LocalFolder` when packaged (exact helper **UNVERIFIED** — not
directly fetched in this pass; the pattern is "you own `FilePath`", so SysPulse should use the
packaged app's `LocalState` folder as spec §7 already assumes).

**`Settings` class** (`Settings.cs`) has:
```csharp
public event TypedEventHandler<object, Settings>? SettingsChanged;
public void Add<T>(Setting<T> s);
public T? GetSetting<T>(string key);
public bool TryGetSetting<T>(string key, out T? val);
public void Update(string data);        // apply persisted values
internal void UpdateFromForm(string data); // apply values submitted from the rendered card
internal void RaiseSettingsChanged();
public IContentPage SettingsPage { get; } // auto-generated Adaptive-Cards settings UI
```
`TimeDateCommandsProvider` wires the change notification exactly as the plan assumes:
`_settingsManager.Settings.SettingsChanged += SettingsChanged;` → handler calls
`_nowDockBand?.UpdateSettings(_settingsManager)`. **Confirms plan §1 item: "settings-changed event
exists and is how a live monitor reconfigures without restart."**

**Available setting types** (files in the same folder) — **no numeric setting type exists**:
- `ToggleSetting` (bool) — renders `Input.Toggle`
- `TextSetting` (string, optional `Multiline`, `Placeholder`) — renders `Input.Text`
- `ChoiceSetSetting` (string, `List<Choice>`, `IgnoreUnknownValue`) — renders `Input.ChoiceSet`
- `FilePathSetting`, `FilePathListSetting`, `StringListSetting`, `KeyValueListSetting` — not
  numeric either.

**Confirms plan §1 item 10 exactly**: "Toolkit'te numeric setting yoksa `TextSetting` + parse/clamp" —
there is genuinely no `NumberSetting`/`Input.Number` type in the toolkit as of this SDK version;
`ScanIntervalSeconds`, `RetryIntervalSeconds`, `RetryCount`, `CpuThreshold`, `MemoryThreshold`,
`HysteresisPercent` must all be `TextSetting` with manual `int.TryParse` + clamp on load, exactly as
the plan proposes. This is a genuine **deviation from spec §6**, which lists these as "number" type.

---

## 5. `CommandResult.Confirm`, `ToastStatusMessage`/`StatusMessage`, `CommandContextItem`, copy/open helpers

**`CommandResult.Confirm`** (`CommandResult.cs` + `ConfirmationArgs.cs`):
```csharp
public static CommandResult Confirm(ConfirmationArgs args);

public partial class ConfirmationArgs : IConfirmationArgs
{
    public virtual string? Title { get; set; }
    public virtual string? Description { get; set; }
    public virtual ICommand? PrimaryCommand { get; set; }
    public virtual bool IsPrimaryCommandCritical { get; set; }
}
```
Exact usage pattern demonstrated by the toolkit's own `ConfirmableCommand`
(`extensionsdk/.../Commands/ConfirmableCommand.cs`, used by `Microsoft.CmdPal.Common`):
```csharp
return CommandResult.Confirm(new ConfirmationArgs
{
    Title = ConfirmationTitle,
    Description = ConfirmationMessage,
    PrimaryCommand = Command,          // the actual kill/delete InvokableCommand
    IsPrimaryCommandCritical = true,   // renders as a destructive/red confirm button
});
```
`ConfirmableCommand` is a generically reusable **wrapper** (`InvokableCommand` that wraps another
`IInvokableCommand`, shows the confirm dialog on `Invoke()`, and forwards to the inner command's
`Invoke()` once confirmed) — SysPulse's `KillProcessCommand` can either implement this pattern
directly or wrap the real kill command with `ConfirmableCommand` (though `ConfirmableCommand` itself
lives in `Microsoft.CmdPal.Common`, an internal PowerToys-repo project, not the public NuGet package —
**UNVERIFIED whether it ships in the public `Microsoft.CommandPalette.Extensions` package**; safest is
to replicate the ~15-line pattern locally rather than depend on it being public).

**`ToastStatusMessage` / `StatusMessage`** (`ToastStatusMessage.cs`, `StatusMessage.cs`):
```csharp
public partial class StatusMessage : BaseObservable, IStatusMessage
{
    public virtual string Message { get; set; } = string.Empty;
    public virtual MessageState State { get; set; } = MessageState.Info; // Info/Success/Warning/Error
    public virtual IProgressState? Progress { get; set; }
}
public partial class ToastStatusMessage
{
    public ToastStatusMessage(StatusMessage message);
    public ToastStatusMessage(string text);
    public virtual int Duration { get; init; } = 2500; // ms, auto-hides
    public void Show(); // ExtensionHost.ShowStatus(...) then auto ExtensionHost.HideStatus(...) after Duration
}
```
This is the in-app transient status/toast (rendered inside CmdPal itself), **not** a Windows shell
toast notification — good fit for spec §5.6's "show a status/toast message with the reason" on kill
failure. For an actual **Windows notification-center toast** (spec §5.4's `AppNotificationManager`
requirement), none of the fetched toolkit files implement that — it's application-level WinRT
(`Microsoft.Windows.AppNotifications` / `Windows.UI.Notifications.ToastNotificationManager`), same as
plan §1 item 9 assumes; **UNVERIFIED whether any built-in CmdPal extension does this** (Time & Date's
`NotificationCenterDockBand` only *opens* `ms-actioncenter:` via `OpenUrlCommand`, it doesn't post a
toast itself) — no counter-example found either. Plan's WinRT `ToastNotificationManager` approach is
reasonable but unverified against a working built-in example.

**`CommandContextItem`** (`CommandContextItem.cs`):
```csharp
public partial class CommandContextItem : CommandItem, ICommandContextItem
{
    public virtual bool IsCritical { get; set; }
    public virtual KeyChord RequestedShortcut { get; set; }
    public CommandContextItem(ICommand command);
    public CommandContextItem(string title, string subtitle = "", string name = "",
        Action? action = null, ICommandResult? result = null);
}
```
Used for `MoreCommands` arrays (context menu), e.g. `MoreCommands = [new
CommandContextItem(_copyTitleCommand), new CommandContextItem(_copySubtitleCommand)]` — directly
matches spec §5.5's "Open file location", "Copy PID" context-menu items.

**Copy/open helpers** (`extensionsdk/.../Commands/`):
- `CopyTextCommand(string text)` — copies `text` to clipboard via `ClipboardHelper.SetText`, shows a
  `CommandResult.ShowToast("Copied to clipboard")` by default. Perfect for "Copy PID": `new
  CommandContextItem(new CopyTextCommand(pid.ToString()))`.
- `ShowFileInFolderCommand(string path)` — runs `explorer.exe /select,"<path>"`. Exact fit for "Open
  file location" using the process's `MainModule.FileName`.
- `OpenUrlCommand(string target)` — `ShellHelpers.OpenInShell(target)`; also works for file paths, not
  just URLs.
- `CopyPathCommand`, `OpenFileCommand`, `OpenWithCommand`, `OpenInConsoleCommand`,
  `OpenPropertiesCommand` also exist in the same folder (not individually fetched — names only,
  **UNVERIFIED** signatures).

---

## 6. `IconInfo` from an .exe path

`IconInfo.cs`:
```csharp
public partial class IconInfo : IIconInfo
{
    public IconInfo(string? icon);                       // single icon for light+dark
    public IconInfo(IconData light, IconData dark);
    public IconInfo(IconData icon);
    public static IconInfo FromStream(IRandomAccessStream stream);
}
```
`IconData.cs` just wraps a `string? Icon` (a path, a segoe glyph like `""`, or a resource URI) or
an `IRandomAccessStreamReference`. **Confirmed real usage passing a raw .exe path**, from the
in-repo `ProcessMonitorExtension` sample (`ext/ProcessMonitorExtension/SwitchToProcess.cs`):
```csharp
this.Icon = new IconInfo(process.ExePath == string.Empty ? "" : process.ExePath);
```
So `new IconInfo(exePath)` is the documented, in-the-wild pattern for SysPulse's `ProcessListItem`
icon — the **host is responsible for extracting the icon from the path** (SysPulse just supplies the
string); this matches plan §1 item 8's "extraction işini host yapar." The toolkit's own
`ThumbnailHelper.cs` (`GetFileIconStream`/`TryExtractUsingPIDL`, using `SHGetFileInfo` /
`SHDefExtractIconW`) shows the underlying Win32 mechanics the host itself uses for exe/file icon
extraction — SysPulse does **not** need to reimplement any of this; just pass the path to `IconInfo`.

`IconHelpers.FromRelativePath(string path)` / `FromRelativePaths(light, dark)` — for bundled
asset icons (e.g. `pulse.svg`/`warning-yellow.svg`), resolved relative to
`AppDomain.CurrentDomain.BaseDirectory`. Already used by the scaffolded
`SysPulseCommandsProvider.cs`: `Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");`

---

## 7. Localization: .resx, not .resw

**Confirmed: built-in CmdPal extensions use `.resx`, not `.resw`.**
`ext/Microsoft.CmdPal.Ext.TimeDate/Microsoft.CmdPal.Ext.TimeDate.csproj`:
```xml
<EmbeddedResource Update="Properties\Resources.resx">
  <LastGenOutput>Resources.Designer.cs</LastGenOutput>
  <Generator>PublicResXFileCodeGenerator</Generator>
  <CustomToolNamespace>Microsoft.CmdPal.Ext.TimeDate</CustomToolNamespace>
</EmbeddedResource>
```
`Properties/` folder contains `Resources.resx` + generated `Resources.Designer.cs` (standard .NET
resx code-gen, accessed as `Resources.SomeKey`), same pattern the toolkit itself uses internally
(`CopyTextCommand` references `Properties.Resources.CopyTextCommand_Copy`). **Confirms plan §1 item
11 ("Built-in eklentiler `.resx` kullanıyorsa ona uyulur") — spec §7's `.resw` is wrong for this SDK;
use `.resx`.** `.resw` is the UWP/WinUI XAML resource format and isn't what these out-of-proc,
non-XAML-hosted extension DLLs use.

---

## 8. Template location, TFM, SDK/package versions, CLI build/deploy, COM entry point, manifest

**Template location in repo:**
`src/modules/cmdpal/ExtensionTemplate/TemplateCmdPalExtension/` — this is the actual scaffold CmdPal's
"Create a new extension" command instantiates (verified structurally identical to what
`docs/creating-an-extension` describes and to the already-scaffolded local `SysPulse/` folder).

**TFM / platform settings** (`TemplateCmdPalExtension/TemplateCmdPalExtension/TemplateCmdPalExtension.csproj`,
matches the local scaffold exactly):
```xml
<OutputType>WinExe</OutputType>
<WindowsSdkPackageVersion>10.0.26100.68-preview</WindowsSdkPackageVersion>
<TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
<TargetPlatformMinVersion>10.0.19041.0</TargetPlatformMinVersion>
<SupportedOSPlatformVersion>10.0.19041.0</SupportedOSPlatformVersion>
<RuntimeIdentifiers>win-x64;win-arm64</RuntimeIdentifiers>
<PublishSingleFile>true</PublishSingleFile>
<IsAotCompatible>true</IsAotCompatible>
```
**net10.0**, not net8/net9 — confirms plan's ".NET SDK 10.0.401" is the right toolchain.
Packages (template's `Directory.Packages.props`):
`Microsoft.CommandPalette.Extensions 0.11.260520004`, `Microsoft.Windows.CsWinRT 2.2.0`,
`Microsoft.WindowsAppSDK 2.2.0`, `Shmuelie.WinRTServer 2.1.1`,
`Microsoft.Windows.SDK.BuildTools.MSIX 1.7.20250829.1`.
**Deviation flag:** the already-scaffolded local `SysPulse\Directory.Packages.props` pins
`Microsoft.CommandPalette.Extensions` to the older `0.9.260303001` and `Microsoft.WindowsAppSDK` to
`2.0.1` (vs. current repo template's `0.11.260520004` / `2.2.0`). Both versions are present in the
local NuGet cache, so a bump is low-risk; per spec §2.1 ("bump the SDK packages to the minimum
version above if the template is older") this bump is in-scope and recommended before relying on
newer SDK members like `DockLabelWidth`/`OnLoadDockBandItem`-equivalent patterns (those live in
`Microsoft.CommandPalette.Extensions.Toolkit`, bundled inside the `Extensions` NuGet package — see §9
— so they come along with a version bump for free).

**COM server entry point** — `Program.cs` (template, identical pattern in local scaffold):
```csharp
[MTAThread]
public static void Main(string[] args)
{
    if (args.Length > 0 && args[0] == "-RegisterProcessAsComServer")
    {
        var server = new Shmuelie.WinRTServer.ComServer();
        var extensionInstance = new TemplateCmdPalExtension(extensionDisposedEvent);
        server.RegisterClass<TemplateCmdPalExtension, IExtension>(() => extensionInstance);
        server.Start();
        extensionDisposedEvent.WaitOne();   // blocks until IExtension.Dispose() is called
        server.Stop();
        server.UnsafeDispose();
    }
}
```
The out-of-proc COM server is implemented via the **`Shmuelie.WinRTServer`** NuGet package, not
hand-rolled COM registration — confirms "the template project handles creating the COM server... Don't
worry about the details" from `extensibility-overview` docs.

`[ComVisible]`/`[Guid(...)]`: the extension class itself carries the CLSID, not a separate attribute
class: `TemplateCmdPalExtension.cs`:
```csharp
[Guid("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF")]
public sealed partial class TemplateCmdPalExtension : IExtension, IDisposable
{
    public object? GetProvider(ProviderType providerType) =>
        providerType switch { ProviderType.Commands => _provider, _ => null };
    public void Dispose() => _extensionDisposedEvent.Set();
}
```
(The template's placeholder GUID `FFFFFFFF-...` **must** be replaced with a real generated GUID —
matched in three places: this attribute, and the two CLSID references in `Package.appxmanifest`
below.)

**Manifest `com.microsoft.commandpalette` declaration** — `Package.appxmanifest`
(template; local scaffold's `SysPulse\SysPulse\Package.appxmanifest` should mirror this):
```xml
<Extensions>
  <com:Extension Category="windows.comServer">
    <com:ComServer>
      <com:ExeServer Executable="TemplateCmdPalExtension.exe" Arguments="-RegisterProcessAsComServer" DisplayName="TemplateDisplayName">
        <com:Class Id="FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF" DisplayName="TemplateDisplayName" />
      </com:ExeServer>
    </com:ComServer>
  </com:Extension>
  <uap3:Extension Category="windows.appExtension">
    <uap3:AppExtension Name="com.microsoft.commandpalette" Id="ID" PublicFolder="Public"
                        DisplayName="TemplateDisplayName" Description="TemplateDisplayName">
      <uap3:Properties>
        <CmdPalProvider>
          <Activation><CreateInstance ClassId="FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF" /></Activation>
          <SupportedInterfaces><Commands/></SupportedInterfaces>
        </CmdPalProvider>
      </uap3:Properties>
    </uap3:AppExtension>
  </uap3:Extension>
</Extensions>
<Capabilities>
  <Capability Name="internetClient" />
  <rescap:Capability Name="runFullTrust" />
</Capabilities>
```
`AppExtension/@Name` **must** literally be `com.microsoft.commandpalette` — this is how CmdPal's host
discovers the package via the Windows Package Catalog (`extensibility-overview` doc). Confirms spec's
assumption implicitly (not stated explicitly in spec, but foundational).

**CLI-only build/deploy (`dotnet build -p:Platform=x64` + `Add-AppxPackage -Register`):
UNVERIFIED against official docs.** `learn.microsoft.com/.../creating-an-extension` **only documents
Visual Studio deployment** ("In the navigation bar, click Build... Click Deploy <ExtensionName>...
Running '<ExtensionName> (Unpackaged)' from Visual Studio will not deploy your app package"). No
CLI-only / `dotnet build` + `Add-AppxPackage -Register` workflow is documented on learn.microsoft.com
as of this fetch. This is a real gap for the plan's environment (VS Build Tools only, no full VS IDE):
the `RuntimeIdentifiers win-x64;win-arm64` + `PublishProfile win-$(Platform).pubxml` +
`EnableMsixTooling` MSBuild properties strongly suggest `dotnet build -p:Platform=x64` (or `dotnet
publish` with the matching `PublishProfile`) produces a loose MSIX-layout output directory that
`Add-AppxPackage -Register <path>\AppxManifest.xml` can sideload (this is the standard
single-project-MSIX pattern for any WinAppSDK app, not CmdPal-specific), but **no built-in
PowerToys/CmdPal repo script doing exactly this for a single extension was found** in the folders
inspected (`check-extensions.ps1`, `clean-sdk.ps1` exist at `src/modules/cmdpal/` but were not
fetched/read in this pass — **UNVERIFIED** whether either automates loose-deploy). Plan step A5
(`scripts/deploy.ps1`) should be treated as a novel script to write and test early (as the plan
already schedules), not as a documented/guaranteed-to-work recipe.

---

## 9. Latest NuGet versions

`GET https://api.nuget.org/v3-flatcontainer/microsoft.commandpalette.extensions/index.json`:
```
... "0.8.260206001", "0.9.260204002-experimental", "0.9.260225002-experimental",
"0.9.260303001", "0.11.260520004", "0.12.260812002"
```
**Latest stable: `Microsoft.CommandPalette.Extensions` 0.12.260812002** (published on nuget.org,
verified publisher `Microsoft`/`Microsoft.CommandPalette`, `https://www.nuget.org/packages/Microsoft.CommandPalette.Extensions`).

**`Microsoft.CommandPalette.Extensions.Toolkit` is NOT a separate NuGet package** — a direct
flat-container query for it 404s (`BlobNotFound`), and a NuGet search for `Microsoft.CommandPalette*`
returns exactly one hit, `Microsoft.CommandPalette.Extensions` itself. Confirmed by inspecting the
package contents in the local cache:
```
.nuget\packages\microsoft.commandpalette.extensions\0.9.260303001\lib\net8.0-windows10.0.19041.0\
    Microsoft.CommandPalette.Extensions.Toolkit.dll   <-- bundled inside the Extensions package
```
and the template's `.csproj` only lists `<PackageReference Include="Microsoft.CommandPalette.Extensions" />`
with no separate Toolkit reference, yet compiles against `Microsoft.CommandPalette.Extensions.Toolkit`
namespaces (confirmed in the local scaffold's `SysPulseCommandsProvider.cs`, which `using`s
`Microsoft.CommandPalette.Extensions.Toolkit` with no extra package reference).

Inside the **PowerToys monorepo itself**, built-in extensions instead consume Toolkit via a
**project reference** to its source (`ext/Common.ExtDependencies.props`):
```xml
<ProjectReference Include="..\..\extensionsdk\Microsoft.CommandPalette.Extensions.Toolkit\Microsoft.CommandPalette.Extensions.Toolkit.csproj" />
```
— that's a repo-internal build convenience (lets first-party extensions build against Toolkit source
changes before a new SDK NuGet ships), not something third-party extensions like SysPulse can or
need to replicate. **Conclusion: SysPulse only needs the single `Microsoft.CommandPalette.Extensions`
PackageReference; the Toolkit DLL rides along inside it.** This resolves plan §1 item 10's open
question about a Toolkit package version line — there isn't one to track separately; bumping
`Microsoft.CommandPalette.Extensions` bumps Toolkit too.

---

## 10. Issue #50483 — exceptions in PropChanged handlers blocking updates

**Confirmed to exist and is closed.** `gh issue view 50483 --repo microsoft/PowerToys`:
- Title: **"CmdPal: Failing event handler blocks extension updates"**
- Body: "Cutting extension cluster-mother-bug into digestible pieces... A failing notification
  subscriber blocks later subscribers, leaving list and dock items stale while the extension remains
  responsive."
- State: **CLOSED**.

This directly validates plan §1 item 4's mitigation ("Her property set `try/catch` içinde — host
tarafı handler exception'ı sonraki güncellemeleri bloklayabiliyor — issue #50483"): a host-side
`PropChanged` subscriber throwing can stall delivery to *other* subscribers on the same event, so an
extension should not assume a bad property value / setter exception is contained — wrap dock-item
property sets that could throw (e.g. anything computed at set-time) in `try/catch` and log rather than
propagate. Related fixed-on-the-host-side work: `ClockUpdateService.InvokeHandlers` already wraps each
handler invocation in `try/catch` (`ext/Microsoft.CmdPal.Ext.TimeDate/ClockUpdateService.cs`) —
good defensive precedent to mirror in `HealthMonitor`'s own tick loop and in any place SysPulse itself
raises `PropChanged`/`ItemsChanged` to multiple listeners.

---

## Bonus: Dock label width / truncation API (relevant to spec §5.3, §11 open question 1)

Not asked for explicitly but directly answers the plan's open question about dock label truncation.
`extensionsdk/Microsoft.CommandPalette.Extensions.Toolkit/Dock/`:
- `DockLabelWidth` — a width expressed as DIPs (`.Dips(double)`), character units (`.Characters(double)`,
  e.g. `"4ch"`), or a measured literal sample (`.Sample(string)`, e.g. `DockLabelWidth.Sample("100%")`).
- `DockLabelWidthExtensions.SetDockLabelReservations(titleWidth, subtitleWidth)` / 
  `SetDockLabelWidthLimits(minimum, maximum)` — extension methods on `CommandItem` (when it also
  implements `IExtendedAttributesProvider`) that reserve/clamp the rendered label width so the band
  doesn't jitter/truncate as text changes.
- `DockLabelPresentationExtensions` — tabular-digit and trailing-alignment hints (not fully inspected;
  method names **UNVERIFIED** beyond what's referenced from `PerformanceMonitorDockItemPresentation.cs`:
  `.SetDockLabelTabularDigits()`).
- Used by the built-in Performance Monitor band
  (`PerformanceMonitorDockItemPresentation.cs`): `DisabledLabelWidth = DockLabelWidth.Characters(8)`,
  `PercentageTitleWidth = DockLabelWidth.Sample("100%")`.

SysPulse's `StatusDockItem` should call `SetDockLabelReservations`/`SetDockLabelWidthLimits` with a
sample like `DockLabelWidth.Sample("CPU 100% · MEM 100%")` instead of inventing an ad-hoc
`CompactLabel` truncation scheme — this is a more idiomatic, SDK-native answer to spec §11's open
question 1 than the spec's own proposed setting.

---

## Summary of confirmed deviations from spec/plan (for `DECISIONS.md`)

| # | Spec/plan assumption | Verified reality |
|---|---|---|
| 1 | `ICommandProvider3.GetDockBands()`, `WrappedDockItem(IListItem[], string, string)`, non-empty Id required | **Confirmed as-written** |
| 2 | NowDockBand ticks unconditionally once constructed; leak fix = "reuse item instances" | **NowDockBand only ticks while its band is actually rendered** (`OnLoadDockBandItem` Loaded/Unloaded gating via a shared `ClockUpdateService`); reusing instances is necessary but not sufficient — lazy start/stop tied to render visibility is the bigger lesson for `HealthMonitor` |
| 3 | Thread/dispatcher for property updates | **Confirmed: plain timer thread, no dispatcher, only set when value changed** |
| 4 | Numeric setting type may not exist, use `TextSetting` + clamp | **Confirmed: no numeric setting type in the toolkit at all** |
| 5 | `.resx` localization (vs spec's `.resw`) | **Confirmed: built-ins use `.resx`** |
| 6 | Toolkit ships as its own NuGet package "same version line" as Extensions (spec §2) | **False: `Microsoft.CommandPalette.Extensions.Toolkit` is not a separate NuGet package; it's bundled inside `Microsoft.CommandPalette.Extensions`'s `lib/` folder. One PackageReference covers both.** |
| 7 | `IconInfo(exePath)` extracts the exe icon via the host | **Confirmed via `ProcessMonitorExtension/SwitchToProcess.cs`'s real usage** |
| 8 | Issue #50483 (PropChanged exceptions block updates) | **Confirmed, closed issue, exact title "CmdPal: Failing event handler blocks extension updates"** |
| 9 | Minimum SDK `0.9.260303001` | **Still valid as a floor, but current repo/nuget latest are `0.11.260520004` / `0.12.260812002`; local scaffold currently pins the older `0.9.260303001` — recommend bumping per spec §2.1's own instruction** |
| 10 | CLI-only build+deploy (`dotnet build -p:Platform=x64` + `Add-AppxPackage -Register`) is a supported workflow | **UNVERIFIED against official docs — learn.microsoft.com only documents Visual Studio deploy; the MSBuild properties present (RuntimeIdentifiers, PublishProfile, EnableMsixTooling) support the loose-MSIX-sideload approach in principle, but no first-party script or doc doing exactly this for one extension was found in this pass** |
| 11 | Dock label truncation handling | **SDK has a purpose-built `DockLabelWidth`/`SetDockLabelReservations` API (post-dates spec); prefer it over an ad-hoc `CompactLabel` setting** |
