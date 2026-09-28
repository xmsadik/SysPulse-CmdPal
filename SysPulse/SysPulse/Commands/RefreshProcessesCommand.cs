// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Diagnostics;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace SysPulse.Commands;

/// <summary>
/// Spec §5.5's "Refresh" command for the Top-5 process list. <see cref="Pages.TopProcessesPage"/>
/// has no page-level command slot of its own (only <c>ICommandItem</c> -- not <c>Page</c> --
/// exposes <c>MoreCommands</c>), so a single shared instance of this command is offered as a
/// context ("more commands") item on every <see cref="Pages.ProcessListItem"/> slot instead.
/// </summary>
internal sealed partial class RefreshProcessesCommand : InvokableCommand
{
    private readonly Action _requestRefresh;

    public RefreshProcessesCommand(Action requestRefresh)
    {
        ArgumentNullException.ThrowIfNull(requestRefresh);
        _requestRefresh = requestRefresh;
        Name = "Refresh";
        Icon = new IconInfo("\uE72C"); // Segoe Fluent "Refresh".
    }

    public override CommandResult Invoke()
    {
        try
        {
            _requestRefresh();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SysPulse: RefreshProcessesCommand failed: {ex}");
        }

        return CommandResult.KeepOpen();
    }
}
