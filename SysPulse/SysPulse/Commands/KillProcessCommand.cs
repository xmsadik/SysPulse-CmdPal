// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
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
/// Spec §5.6's kill action. Immutable (code-review findings 1/3): each instance is bound for life
/// to the single <see cref="KillTarget"/> (PID + creation time + name) it was constructed with.
/// <see cref="Pages.ProcessListItem"/> creates a new instance only when its slot's process
/// identity -- or protected status -- changes, and assigns it to <c>Command</c> in that same step;
/// there is no mutable rebind, so a confirmation dialog left open across refreshes can never end
/// up acting on whatever process happens to occupy the slot when the user finally clicks Confirm.
/// </summary>
/// <remarks>
/// <see cref="Invoke"/> only decides whether confirmation is needed; the actual kill runs in
/// <see cref="DoKill"/>, invoked either directly (when <see cref="SysPulseOptions.ConfirmKill"/>
/// is off) or via the inner <see cref="DoKillConfirmedCommand"/> handed to
/// <c>ConfirmationArgs.PrimaryCommand</c> -- the pattern documented in docs/sdk-research.md §5 for
/// <c>CommandResult.Confirm</c>. <see cref="DoKill"/> re-checks the protected-process list at kill
/// time (not just whatever was true when the command was built or the dialog opened) and, for a
/// single process, terminates it via <see cref="ProcessTerminator.TryTerminate"/> -- one native
/// handle, identity-checked and terminated atomically through that same handle, closing the
/// classic "kill the wrong process because its PID got reused" TOCTOU window that a two-step
/// check-then-<c>Process.Kill</c> sequence has.
/// </remarks>
internal sealed partial class KillProcessCommand : InvokableCommand
{
    private readonly KillTarget _target;
    private readonly Func<SysPulseOptions> _optionsAccessor;
    private readonly Func<ProtectedProcessList> _protectedListAccessor;
    private readonly Action _requestRefresh;

    /// <summary>
    /// Initializes a new instance of the <see cref="KillProcessCommand"/> class, permanently bound
    /// to <paramref name="target"/>.
    /// </summary>
    /// <param name="target">The process this command kills. Immutable for the command's lifetime.</param>
    /// <param name="optionsAccessor">Accessor for the current <see cref="SysPulseOptions"/> (confirm/tree, read at kill time).</param>
    /// <param name="protectedListAccessor">Accessor for the current <see cref="ProtectedProcessList"/>, re-checked at kill time.</param>
    /// <param name="requestRefresh">Callback to force an immediate Top-5 refresh (used after a kill).</param>
    public KillProcessCommand(KillTarget target, Func<SysPulseOptions> optionsAccessor, Func<ProtectedProcessList> protectedListAccessor, Action requestRefresh)
    {
        ArgumentNullException.ThrowIfNull(optionsAccessor);
        ArgumentNullException.ThrowIfNull(protectedListAccessor);
        ArgumentNullException.ThrowIfNull(requestRefresh);

        _target = target;
        _optionsAccessor = optionsAccessor;
        _protectedListAccessor = protectedListAccessor;
        _requestRefresh = requestRefresh;
        Id = "com.syspulse.killprocess";
        Name = Resources.Command_Kill;
        Icon = new IconInfo(""); // Segoe Fluent "ChromeClose"-adjacent "delete" glyph.
    }

    public override CommandResult Invoke()
    {
        SysPulseOptions options = _optionsAccessor();

        if (!options.ConfirmKill)
        {
            return DoKill(_target);
        }

        string description = string.Format(CultureInfo.InvariantCulture, Resources.Kill_ConfirmDescriptionFormat, _target.Name, _target.Pid);
        return CommandResult.Confirm(new ConfirmationArgs
        {
            Title = Resources.Kill_ConfirmTitle,
            Description = description,
            PrimaryCommand = new DoKillConfirmedCommand(this, _target),
            IsPrimaryCommandCritical = true,
        });
    }

    /// <summary>
    /// Performs the actual kill (spec §5.6). Re-checks the protected list and, for a single
    /// process, delegates to <see cref="ProcessTerminator.TryTerminate"/>; for a process tree, to
    /// <see cref="DoKillTree"/>. Reports the outcome via <see cref="ToastStatusMessage"/>. Never
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
            // Re-check protection at kill time (findings 1/3): the confirmation dialog may have
            // sat open while settings changed, so the isProtected value captured when this
            // command was built can no longer be trusted.
            ProtectedProcessList protectedList = _protectedListAccessor();
            if (protectedList.IsProtected(name, pid))
            {
                Log.Warning(FormattableString.Invariant($"Kill refused: {name} (PID {pid}) is protected."));
                return ShowToast(Fmt(Resources.Kill_ProtectedFormat, name), MessageState.Warning, refresh: false);
            }

            SysPulseOptions options = _optionsAccessor();

            return options.KillProcessTree
                ? DoKillTree(pid, expectedCreateTime, name, protectedList)
                : ReportSingleResult(ProcessTerminator.TryTerminate(pid, expectedCreateTime), name, pid);
        }
        catch (Exception ex)
        {
            Log.Error(FormattableString.Invariant($"Kill failed unexpectedly: {name} (PID {pid})"), ex);
            Debug.WriteLine($"SysPulse: KillProcessCommand.DoKill failed unexpectedly: {ex}");
            return ShowToast(Fmt(Resources.Kill_UnexpectedErrorFormat, name), MessageState.Warning, refresh: false);
        }
    }

    /// <summary>
    /// Kills the process tree rooted at (<paramref name="rootPid"/>, <paramref name="rootCreateTime"/>)
    /// (spec §5.6, code-review finding 2): takes one fresh, short-lived process sample, computes
    /// the live descendant set via <see cref="ProcessTree.GetDescendants"/>, refuses the whole
    /// operation if any descendant is protected, then terminates descendants deepest-first and the
    /// root last -- each through its own <see cref="ProcessTerminator.TryTerminate"/> call with
    /// that descendant's own creation time, so a descendant whose PID got reused since the sample
    /// is skipped rather than mis-targeted.
    /// </summary>
    private CommandResult DoKillTree(int rootPid, long rootCreateTime, string name, ProtectedProcessList protectedList)
    {
        IReadOnlyList<ProcessSample> all;
        try
        {
            using var sampler = new ProcessSampler();
            all = sampler.Sample();
        }
        catch (Exception ex)
        {
            Log.Error(FormattableString.Invariant($"Kill tree failed: could not sample processes for {name} (PID {rootPid})"), ex);
            return ShowToast(Fmt(Resources.Kill_UnexpectedErrorFormat, name), MessageState.Warning, refresh: false);
        }

        IReadOnlyList<ProcessSample> descendants = ProcessTree.GetDescendants(all, rootPid, rootCreateTime);

        foreach (ProcessSample descendant in descendants)
        {
            if (protectedList.IsProtected(descendant.Name, descendant.Pid))
            {
                Log.Warning(FormattableString.Invariant(
                    $"Kill tree refused: {name} (PID {rootPid})'s descendant {descendant.Name} (PID {descendant.Pid}) is protected."));
                return ShowToast(Fmt(Resources.Kill_TreeProtectedFormat, name, descendant.Name), MessageState.Warning, refresh: false);
            }
        }

        // Deepest descendants first (GetDescendants returns shallowest-first, so this is a simple
        // reverse walk) so a child is never orphaned by killing its parent first; the root goes
        // last. Best-effort: a descendant that fails to terminate does not abort the rest -- it is
        // logged and the walk continues, matching "kill everything you can".
        for (int i = descendants.Count - 1; i >= 0; i--)
        {
            ProcessSample descendant = descendants[i];
            TerminateResult descendantResult = ProcessTerminator.TryTerminate(descendant.Pid, descendant.CreateTime);
            LogDescendantResult(descendant, descendantResult);
        }

        TerminateResult rootResult = ProcessTerminator.TryTerminate(rootPid, rootCreateTime);
        return ReportSingleResult(rootResult, name, rootPid);
    }

    private static void LogDescendantResult(ProcessSample descendant, TerminateResult result)
    {
        switch (result.Outcome)
        {
            case TerminateOutcome.Terminated:
                Log.Info(FormattableString.Invariant($"Kill tree: terminated descendant {descendant.Name} (PID {descendant.Pid})."));
                break;
            case TerminateOutcome.NotFound:
            case TerminateOutcome.IdentityMismatch:
                // Already gone by the time we got to it (exited on its own, or taken down as
                // another descendant's own child earlier in this same walk) -- not an error.
                break;
            default:
                Log.Warning(FormattableString.Invariant(
                    $"Kill tree: failed to terminate descendant {descendant.Name} (PID {descendant.Pid}): {result.Outcome}."));
                break;
        }
    }

    private CommandResult ReportSingleResult(TerminateResult result, string name, int pid)
    {
        switch (result.Outcome)
        {
            case TerminateOutcome.Terminated:
                Log.Info(FormattableString.Invariant($"Kill succeeded: {name} (PID {pid})."));
                return ShowToast(Fmt(Resources.Kill_SuccessFormat, name), MessageState.Success, refresh: true);

            case TerminateOutcome.NotFound:
            case TerminateOutcome.IdentityMismatch:
                Log.Info(FormattableString.Invariant($"Kill skipped: {name} (PID {pid}) already exited."));
                return ShowToast(Fmt(Resources.Kill_AlreadyExitedFormat, name), MessageState.Info, refresh: true);

            case TerminateOutcome.AccessDenied:
                Log.Warning(FormattableString.Invariant($"Kill denied (access denied / elevated): {name} (PID {pid})."));
                return ShowToast(Fmt(Resources.Kill_AdminRequiredFormat, name), MessageState.Warning, refresh: false);

            case TerminateOutcome.Failed:
            default:
                string win32Message = new Win32Exception(result.Win32Error).Message;
                Log.Warning(FormattableString.Invariant($"Kill failed: {name} (PID {pid}): {win32Message}"));
                return ShowToast(Fmt(Resources.Kill_Win32ErrorFormat, name, win32Message), MessageState.Warning, refresh: false);
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

    /// <summary>Immutable identity of the process a <see cref="KillProcessCommand"/> instance is bound to.</summary>
    internal readonly record struct KillTarget(int Pid, long CreateTime, string Name);
}
