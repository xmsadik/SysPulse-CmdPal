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
/// </remarks>
internal sealed partial class ProcessListItem : ListItem
{
    private readonly KillProcessCommand _killCommand;
    private readonly CopyTextCommand _copyPidCommand;
    private readonly CommandContextItem _copyPidContextItem;
    private readonly CommandContextItem[] _sharedTrailingItems;

    private ShowFileInFolderCommand? _openFileCommand;
    private CommandContextItem? _openFileContextItem;

    private int _pid = -1;
    private long _createTime = -1;
    private bool _lastIsProtected;
    private bool _commandsInitialized;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProcessListItem"/> class.
    /// </summary>
    /// <param name="optionsAccessor">Accessor for the current <see cref="SysPulse.Core.Settings.SysPulseOptions"/> (kill confirmation/tree, read at kill time).</param>
    /// <param name="requestRefresh">Callback to force an immediate Top-5 refresh (used after a kill).</param>
    /// <param name="sharedTrailingItems">
    /// Context items shared by every slot and appended after the per-process ones (Refresh,
    /// Settings) -- see <see cref="TopProcessesPage"/>.
    /// </param>
    public ProcessListItem(
        Func<SysPulseOptions> optionsAccessor,
        Action requestRefresh,
        CommandContextItem[] sharedTrailingItems)
    {
        ArgumentNullException.ThrowIfNull(sharedTrailingItems);

        _killCommand = new KillProcessCommand(optionsAccessor, requestRefresh);
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

        _killCommand.Bind(sample.Pid, sample.CreateTime, sample.Name);
        UpdateCopyPidText(sample.Pid);

        if (identityChanged)
        {
            string? exePath = ProcessIconCache.GetPath(sample.Pid, sample.CreateTime);
            UpdateIcon(exePath);
            UpdateOpenFileCommand(exePath);
        }

        if (!_commandsInitialized || identityChanged || isProtected != _lastIsProtected)
        {
            UpdatePrimaryCommand(isProtected);
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

    private void UpdateOpenFileCommand(string? exePath)
    {
        // ShowFileInFolderCommand has no mutable path property (docs/sdk-research.md §5), so it is
        // only reallocated when the slot's process identity -- and therefore its path -- changes.
        _openFileCommand = string.IsNullOrEmpty(exePath) ? null : new ShowFileInFolderCommand(exePath) { Name = Resources.Command_OpenFileLocation };
        _openFileContextItem = _openFileCommand is null ? null : new CommandContextItem(_openFileCommand);
    }

    private void UpdatePrimaryCommand(bool isProtected)
    {
        // Spec §5.6: never offer Kill for a protected process. Copy PID is used as a
        // non-destructive primary command instead (documented in the task report).
        ICommand primary = isProtected ? _copyPidCommand : _killCommand;
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
        var items = new List<CommandContextItem>(4);
        if (_openFileContextItem is not null)
        {
            items.Add(_openFileContextItem);
        }

        // Copy PID is already the primary command for protected processes; avoid offering it twice.
        if (!isProtected)
        {
            items.Add(_copyPidContextItem);
        }

        items.AddRange(_sharedTrailingItems);

        try
        {
            MoreCommands = items.ToArray();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SysPulse: failed to set process item MoreCommands: {ex}");
        }
    }
}
