// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using SysPulse.Core.Processes;
using SysPulse.Core.Settings;
using SysPulse.Logging;
using SysPulse.Properties;

namespace SysPulse.Commands;

/// <summary>
/// Spec §5.6's kill action. One instance is reused per <see cref="Pages.ProcessListItem"/> slot
/// (spec §5.5's "reuse command instances" requirement) and rebound to the currently displayed
/// process via <see cref="Bind"/> on every list refresh, rather than being reallocated.
/// </summary>
/// <remarks>
/// <see cref="Invoke"/> only decides whether confirmation is needed; the actual kill runs in
/// <see cref="DoKill"/>, invoked either directly (when <see cref="SysPulseOptions.ConfirmKill"/>
/// is off) or via the inner <see cref="DoKillConfirmedCommand"/> handed to
/// <c>ConfirmationArgs.PrimaryCommand</c> -- the pattern documented in docs/sdk-research.md §5 for
/// <c>CommandResult.Confirm</c>.
/// </remarks>
internal sealed partial class KillProcessCommand : InvokableCommand
{
    private readonly Func<SysPulseOptions> _optionsAccessor;
    private readonly Action _requestRefresh;

    private int _pid;
    private long _createTime;
    private string _processName = string.Empty;

    public KillProcessCommand(Func<SysPulseOptions> optionsAccessor, Action requestRefresh)
    {
        ArgumentNullException.ThrowIfNull(optionsAccessor);
        ArgumentNullException.ThrowIfNull(requestRefresh);

        _optionsAccessor = optionsAccessor;
        _requestRefresh = requestRefresh;
        Id = "com.syspulse.killprocess";
        Name = Resources.Command_Kill;
        Icon = new IconInfo("\uE894"); // Segoe Fluent "ChromeClose"-adjacent "delete" glyph.
    }

    /// <summary>
    /// Rebinds this reused command instance to the process currently occupying the slot.
    /// </summary>
    /// <param name="pid">The process id.</param>
    /// <param name="createTime">The process's creation time (spec §5.6 identity check).</param>
    /// <param name="processName">The process's display name, for confirmation/status text.</param>
    public void Bind(int pid, long createTime, string processName)
    {
        _pid = pid;
        _createTime = createTime;
        _processName = processName;
    }

    public override CommandResult Invoke()
    {
        SysPulseOptions options = _optionsAccessor();

        // Capture the target now: the slot is rebound on every refresh, and the confirmation
        // dialog may stay open across several refreshes. The kill must hit exactly the process
        // the user was shown, never whatever occupies the slot when they click Confirm.
        var target = new KillTarget(_pid, _createTime, _processName);

        if (!options.ConfirmKill)
        {
            return DoKill(target);
        }

        string description = string.Format(CultureInfo.InvariantCulture, Resources.Kill_ConfirmDescriptionFormat, target.Name, target.Pid);
        return CommandResult.Confirm(new ConfirmationArgs
        {
            Title = Resources.Kill_ConfirmTitle,
            Description = description,
            PrimaryCommand = new DoKillConfirmedCommand(this, target),
            IsPrimaryCommandCritical = true,
        });
    }

    /// <summary>
    /// Performs the actual kill (spec §5.6): verifies the process identity hasn't changed since
    /// binding, kills it, and reports the outcome via <see cref="ToastStatusMessage"/>. Never
    /// throws.
    /// </summary>
    internal CommandResult DoKill(KillTarget target)
    {
        int pid = target.Pid;
        long expectedCreateTime = target.CreateTime;
        string name = string.IsNullOrEmpty(target.Name) ? $"PID {pid}" : target.Name;

        if (pid <= 0)
        {
            return ShowToast(Resources.Kill_NothingToKill, MessageState.Info, refresh: false);
        }

        Log.Info(FormattableString.Invariant($"Kill requested: {name} (PID {pid})."));

        try
        {
            long? currentCreateTime = ProcessImagePath.TryGetCreateTimeTicks(pid);
            if (currentCreateTime is null || currentCreateTime.Value != expectedCreateTime)
            {
                Log.Info(FormattableString.Invariant($"Kill skipped: {name} (PID {pid}) already exited."));
                return ShowToast(Fmt(Resources.Kill_AlreadyExitedFormat, name), MessageState.Info, refresh: true);
            }

            SysPulseOptions options = _optionsAccessor();
            using Process process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: options.KillProcessTree);

            Log.Info(FormattableString.Invariant($"Kill succeeded: {name} (PID {pid}), tree={options.KillProcessTree}."));
            return ShowToast(Fmt(Resources.Kill_SuccessFormat, name), MessageState.Success, refresh: true);
        }
        catch (ArgumentException)
        {
            // Process.GetProcessById: no process with this id.
            Log.Info(FormattableString.Invariant($"Kill skipped: {name} (PID {pid}) already exited."));
            return ShowToast(Fmt(Resources.Kill_AlreadyExitedFormat, name), MessageState.Info, refresh: true);
        }
        catch (InvalidOperationException)
        {
            // Process.Kill: process already exited between the identity check and the kill call.
            Log.Info(FormattableString.Invariant($"Kill skipped: {name} (PID {pid}) already exited."));
            return ShowToast(Fmt(Resources.Kill_AlreadyExitedFormat, name), MessageState.Info, refresh: true);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
        {
            Log.Warning(FormattableString.Invariant($"Kill denied (access denied / elevated): {name} (PID {pid})."));
            return ShowToast(Fmt(Resources.Kill_AdminRequiredFormat, name), MessageState.Warning, refresh: false);
        }
        catch (Win32Exception ex)
        {
            Log.Warning(FormattableString.Invariant($"Kill failed: {name} (PID {pid}): {ex.Message}"));
            return ShowToast(Fmt(Resources.Kill_Win32ErrorFormat, name, ex.Message), MessageState.Warning, refresh: false);
        }
        catch (Exception ex)
        {
            Log.Error(FormattableString.Invariant($"Kill failed unexpectedly: {name} (PID {pid})"), ex);
            Debug.WriteLine($"SysPulse: KillProcessCommand.DoKill failed unexpectedly: {ex}");
            return ShowToast(Fmt(Resources.Kill_UnexpectedErrorFormat, name), MessageState.Warning, refresh: false);
        }
    }

    private static string Fmt(string format, params object?[] args) => string.Format(CultureInfo.InvariantCulture, format, args);

    private CommandResult ShowToast(string message, MessageState state, bool refresh)
    {
        try
        {
            new ToastStatusMessage(new StatusMessage { Message = message, State = state }).Show();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SysPulse: failed to show kill-result toast: {ex}");
        }

        if (refresh)
        {
            try
            {
                _requestRefresh();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SysPulse: post-kill refresh failed: {ex}");
            }
        }

        return CommandResult.KeepOpen();
    }

    /// <summary>
    /// The confirmation dialog's primary command: a thin, single-use wrapper that performs the
    /// already-confirmed kill via the owning <see cref="KillProcessCommand"/>. A distinct instance
    /// per confirmation (rather than the outer command itself) avoids re-triggering the
    /// confirmation prompt if the host ever re-invoked the same command object.
    /// </summary>
    private sealed partial class DoKillConfirmedCommand : InvokableCommand
    {
        private readonly KillProcessCommand _owner;
        private readonly KillTarget _target;

        public DoKillConfirmedCommand(KillProcessCommand owner, KillTarget target)
        {
            _owner = owner;
            _target = target;
            Name = Resources.Command_Kill;
        }

        public override CommandResult Invoke() => _owner.DoKill(_target);
    }

    /// <summary>Immutable identity of the process a kill was requested for.</summary>
    internal readonly record struct KillTarget(int Pid, long CreateTime, string Name);
}
