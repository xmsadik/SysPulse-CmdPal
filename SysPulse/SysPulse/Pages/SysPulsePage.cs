// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace SysPulse;

internal sealed partial class SysPulsePage : ListPage
{
    public SysPulsePage()
    {
        // Non-empty Id required: this page is also used as the dock band item's Command,
        // and CmdPal ignores dock band items whose Command.Id is empty.
        Id = "com.syspulse.statuspage";
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");
        Title = "SysPulse";
        Name = "Open";
    }

    public override IListItem[] GetItems()
    {
        return [
            new ListItem(new NoOpCommand()) { Title = "TODO: Implement your extension here" }
        ];
    }
}
