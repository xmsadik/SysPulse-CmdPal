// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.
//
// Ported from PowerToys Microsoft.CmdPal.Ext.TimeDate
// (src/modules/cmdpal/ext/Microsoft.CmdPal.Ext.TimeDate/Pages/OnLoadDynamicListPage.cs).
// Deviation: the upstream implementation raises ItemsChanged via a toolkit
// `EventHelpers.Raise` helper that is not present in the installed
// Microsoft.CommandPalette.Extensions 0.12.260812002 package (verified: absent from
// Microsoft.CommandPalette.Extensions.Toolkit.dll's type table). RaiseItemsChanged below
// invokes the event delegate directly instead.

using System;
using System.Threading;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Foundation;

namespace SysPulse.Dock;

/// <summary>
/// A list page that starts its live work only while CmdPal is displaying it.
/// CmdPal attaches an <see cref="ItemsChanged"/> handler when the page is loaded
/// and removes it after the page is no longer in use.
/// </summary>
internal abstract partial class OnLoadDynamicListPage : Page, IDynamicListPage
{
    private readonly Lock _loadLock = new();
    private int _loadCount;
    private string _searchText = string.Empty;

    private event TypedEventHandler<object, IItemsChangedEventArgs>? InternalItemsChanged;

    public virtual string PlaceholderText { get; set => SetProperty(ref field, value); } = string.Empty;

    public virtual string SearchText
    {
        get => _searchText;
        set
        {
            var oldSearch = _searchText;
            SetSearchNoUpdate(value);
            UpdateSearchText(oldSearch, value);
        }
    }

    public virtual bool ShowDetails { get; set => SetProperty(ref field, value); }

    public virtual bool HasMoreItems { get; set => SetProperty(ref field, value); }

    public virtual IFilters? Filters { get; set => SetProperty(ref field, value); }

    public virtual IGridProperties? GridProperties { get; set => SetProperty(ref field, value); }

    public virtual ICommandItem? EmptyContent { get; set => SetProperty(ref field, value); }

    public event TypedEventHandler<object, IItemsChangedEventArgs> ItemsChanged
    {
        add
        {
            InternalItemsChanged += value;
            lock (_loadLock)
            {
                if (_loadCount++ == 0)
                {
                    Loaded();
                }
            }
        }

        remove
        {
            InternalItemsChanged -= value;
            lock (_loadLock)
            {
                _loadCount = Math.Max(0, _loadCount - 1);
                if (_loadCount == 0)
                {
                    Unloaded();
                }
            }
        }
    }

    public void LoadMore()
    {
    }

    public abstract IListItem[] GetItems();

    public abstract void UpdateSearchText(string oldSearch, string newSearch);

    protected void SetSearchNoUpdate(string newSearchText) => _searchText = newSearchText;

    protected void RaiseItemsChanged(int totalItems = -1)
    {
        try
        {
            InternalItemsChanged?.Invoke(this, new ItemsChangedEventArgs(totalItems));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SysPulse: ItemsChanged handler threw: {ex}");
        }
    }

    protected abstract void Loaded();

    protected abstract void Unloaded();
}
