// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using SysPulse.Commands;
using SysPulse.Core.Formatting;
using SysPulse.Core.Processes;
using SysPulse.Core.Settings;
using SysPulse.Properties;

namespace SysPulse.Pages;

/// <summary>
/// One reusable slot in the Top-5 process list (spec §5.5). <see cref="TopProcessesPage"/> creates
/// exactly 5 instances once and calls <see cref="Update"/> on them in place every refresh; it
/// never allocates a new <see cref="ProcessListItem"/>.
/// </summary>
/// <remarks>
/// Threading: <see cref="Update"/> is called from a single background refresh task at a time
/// (single-flight, see <see cref="TopProcessesPage"/>), directly mutating <see cref="ListItem"/>
/// properties with no dispatcher -- the same model as <c>StatusDockItem.Apply</c> (DECISIONS.md
/// D4). Every property set is wrapped in its own try/catch for the same reason (PowerToys issue
/// #50483: a throwing host-side PropChanged handler must not block later updates), and a property
/// is only set when its value actually changed.
/// <para>
/// Code-review findings 1/7: <see cref="Commands.KillProcessCommand"/> is immutable, so a new
/// instance is created only when this slot's (Pid, CreateTime) identity or protected status
/// changes -- never a mutable rebind -- and <c>Command</c> is (re)assigned in that same step. The
/// "Open file location" <see cref="CommandContextItem"/> and the composed <c>MoreCommands</c>
/// array are cached (by executable path, and by (path item, isProtected) respectively) so that in
/// steady state -- the same small set of processes rotating among the fixed slots -- no new
/// per-refresh objects are allocated beyond the unavoidable <see cref="Commands.KillProcessCommand"/>.
/// </para>
/// </remarks>
internal sealed partial class ProcessListItem : ListItem
{
    private const int MoreCommandsCacheLimit = 32;

    private readonly Func<SysPulseOptions> _optionsAccessor;
    private readonly Func<ProtectedProcessList> _protectedListAccessor;
    private readonly Action _requestRefresh;
    private readonly CopyTextCommand _copyPidCommand;
    private readonly CommandContextItem _copyPidContextItem;
    private readonly CommandContextItem[] _sharedTrailingItems;

    // Per-instance cache of the composed MoreCommands array, keyed by the (reference-cached)
    // "Open file location" context item currently in play and the protected flag -- both of which
    // fully determine the array's contents for a given slot instance (finding 7).
    private readonly Dictionary<(CommandContextItem? PathItem, bool IsProtected), CommandContextItem[]> _moreCommandsCache = new();

    private CommandContextItem? _openFileContextItem;

    private int _pid = -1;
    private long _createTime = -1;
    private bool _lastIsProtected;
    private bool _commandsInitialized;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProcessListItem"/> class.
    /// </summary>
    /// <param name="optionsAccessor">Accessor for the current <see cref="SysPulse.Core.Settings.SysPulseOptions"/> (kill confirmation/tree, read at kill time).</param>
    /// <param name="protectedListAccessor">Accessor for the current <see cref="ProtectedProcessList"/>, handed to each <see cref="Commands.KillProcessCommand"/> for its kill-time re-check.</param>
    /// <param name="requestRefresh">Callback to force an immediate Top-5 refresh (used after a kill).</param>
    /// <param name="sharedTrailingItems">
    /// Context items shared by every slot and appended after the per-process ones (Refresh,
    /// Settings) -- see <see cref="TopProcessesPage"/>.
    /// </param>
    public ProcessListItem(
        Func<SysPulseOptions> optionsAccessor,
        Func<ProtectedProcessList> protectedListAccessor,
        Action requestRefresh,
        CommandContextItem[] sharedTrailingItems)
    {
        ArgumentNullException.ThrowIfNull(optionsAccessor);
        ArgumentNullException.ThrowIfNull(protectedListAccessor);
        ArgumentNullException.ThrowIfNull(requestRefresh);
        ArgumentNullException.ThrowIfNull(sharedTrailingItems);

        _optionsAccessor = optionsAccessor;
        _protectedListAccessor = protectedListAccessor;
        _requestRefresh = requestRefresh;
        _copyPidCommand = new CopyTextCommand(string.Empty) { Name = Resources.Command_CopyPid };
        _copyPidContextItem = new CommandContextItem(_copyPidCommand);
        _sharedTrailingItems = sharedTrailingItems;

        Icon = ProcessIconCache.Fallback;
    }

    /// <summary>Whether this slot currently holds a sampled process (vs. its initial empty state).</summary>
    public bool HasData { get; private set; }

    /// <summary>
    /// Updates this slot in place for <paramref name="sample"/>. Only assigns properties whose
    /// displayed value actually changed (spec §5.5/DECISIONS.md D4).
    /// </summary>
    /// <param name="sample">The process to display.</param>
    /// <param name="isProtected">Whether the process is on the protected list (spec §5.6) -- no Kill command is offered.</param>
    public void Update(ProcessSample sample, bool isProtected)
    {
        HasData = true;
        bool identityChanged = sample.Pid != _pid || sample.CreateTime != _createTime;
        _pid = sample.Pid;
        _createTime = sample.CreateTime;

        SetTitleIfChanged(sample.Name);
        SetSubtitleIfChanged(ProcessSubtitleFormatter.Format(sample, isProtected));

        UpdateCopyPidText(sample.Pid);

        if (identityChanged)
        {
            string? exePath = ProcessIconCache.GetPath(sample.Pid, sample.CreateTime);
            UpdateIcon(exePath);
            _openFileContextItem = ProcessIconCache.GetOpenFileContextItem(exePath, Resources.Command_OpenFileLocation);
        }

        bool protectedChanged = isProtected != _lastIsProtected;
        if (!_commandsInitialized || identityChanged || protectedChanged)
        {
            // Immutable KillProcessCommand (findings 1/3): a new instance every time the slot's
            // identity or protected status changes, never a mutable rebind. Command is assigned
            // in this same step (UpdatePrimaryCommand below).
            var killCommand = new KillProcessCommand(
                new KillProcessCommand.KillTarget(sample.Pid, sample.CreateTime, sample.Name),
                _optionsAccessor,
                _protectedListAccessor,
                _requestRefresh);

            UpdatePrimaryCommand(killCommand, isProtected);
            UpdateMoreCommands(isProtected);
            _commandsInitialized = true;
        }

        _lastIsProtected = isProtected;
    }

    private void SetTitleIfChanged(string title)
    {
        if (string.Equals(Title, title, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            Title = title;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SysPulse: failed to set process item Title: {ex}");
        }
    }

    private void SetSubtitleIfChanged(string subtitle)
    {
        if (string.Equals(Subtitle, subtitle, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            Subtitle = subtitle;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SysPulse: failed to set process item Subtitle: {ex}");
        }
    }

    private void UpdateCopyPidText(int pid)
    {
        string pidText = pid.ToString(CultureInfo.InvariantCulture);
        if (string.Equals(_copyPidCommand.Text, pidText, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            _copyPidCommand.Text = pidText;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SysPulse: failed to rebind Copy PID command: {ex}");
        }
    }

    private void UpdateIcon(string? exePath)
    {
        IconInfo icon = ProcessIconCache.GetIcon(exePath);
        try
        {
            Icon = icon;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SysPulse: failed to set process item Icon: {ex}");
        }
    }

    private void UpdatePrimaryCommand(KillProcessCommand killCommand, bool isProtected)
    {
        // Spec §5.6: never offer Kill for a protected process. Copy PID is used as a
        // non-destructive primary command instead (documented in the task report).
        ICommand primary = isProtected ? _copyPidCommand : killCommand;
        try
        {
            Command = primary;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SysPulse: failed to set process item Command: {ex}");
        }
    }

    private void UpdateMoreCommands(bool isProtected)
    {
        var cacheKey = (_openFileContextItem, isProtected);
        if (!_moreCommandsCache.TryGetValue(cacheKey, out CommandContextItem[]? items))
        {
            var list = new List<CommandContextItem>(2 + _sharedTrailingItems.Length);
            if (_openFileContextItem is not null)
            {
                list.Add(_openFileContextItem);
            }

            // Copy PID is already the primary command for protected processes; avoid offering it twice.
            if (!isProtected)
            {
                list.Add(_copyPidContextItem);
            }

            list.AddRange(_sharedTrailingItems);
            items = list.ToArray();

            if (_moreCommandsCache.Count >= MoreCommandsCacheLimit)
            {
                _moreCommandsCache.Clear();
            }

            _moreCommandsCache[cacheKey] = items;
        }

        try
        {
            MoreCommands = items;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SysPulse: failed to set process item MoreCommands: {ex}");
        }
    }
}
