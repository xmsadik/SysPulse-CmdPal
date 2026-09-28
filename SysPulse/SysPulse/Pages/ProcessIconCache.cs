// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Concurrent;
using Microsoft.CommandPalette.Extensions.Toolkit;
using SysPulse.Core.Processes;

namespace SysPulse.Pages;

/// <summary>
/// Bounded, process-lifetime caches backing <see cref="ProcessListItem"/>'s icon resolution (spec
/// §5.5): the executable path per (PID, creation time) -- guarding against PID reuse between
/// samples -- and the <see cref="IconInfo"/> built from each distinct path (docs/sdk-research.md
/// §6: <c>new IconInfo(exePath)</c> is the documented pattern; the host extracts the actual icon).
/// </summary>
/// <remarks>
/// Eviction is deliberately simple for a v1: once either cache exceeds
/// <see cref="MaxEntries"/> entries it is cleared outright rather than LRU-evicted. Process
/// churn across the handful of distinct executables typically shown makes this adequate, and
/// avoids the bookkeeping of a proper LRU for a cache that is rebuilt cheaply on the next sample.
/// </remarks>
internal static class ProcessIconCache
{
    private const int MaxEntries = 256;

    /// <summary>Generic app glyph shown when a process's executable path can't be resolved.</summary>
    internal static readonly IconInfo Fallback = new("\uE7C4");

    private static readonly ConcurrentDictionary<(int Pid, long CreateTime), string?> PathsByIdentity = new();
    private static readonly ConcurrentDictionary<string, IconInfo> IconsByPath = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, CommandContextItem> OpenFileContextItemsByPath = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves (and caches) the executable path for a process identity.
    /// </summary>
    /// <param name="pid">The process id.</param>
    /// <param name="createTime">The process's creation time (disambiguates PID reuse).</param>
    /// <returns>The executable path, or <see langword="null"/> if it couldn't be resolved.</returns>
    public static string? GetPath(int pid, long createTime)
    {
        (int, long) key = (pid, createTime);
        if (PathsByIdentity.TryGetValue(key, out string? cached))
        {
            return cached;
        }

        if (PathsByIdentity.Count > MaxEntries)
        {
            PathsByIdentity.Clear();
        }

        string? path = ProcessImagePath.TryGetImagePath(pid);
        PathsByIdentity[key] = path;
        return path;
    }

    /// <summary>
    /// Resolves (and caches) the <see cref="IconInfo"/> for an executable path.
    /// </summary>
    /// <param name="exePath">The executable path, or <see langword="null"/>/empty for the fallback icon.</param>
    /// <returns>An <see cref="IconInfo"/> pointing at <paramref name="exePath"/>, or <see cref="Fallback"/>.</returns>
    public static IconInfo GetIcon(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath))
        {
            return Fallback;
        }

        if (IconsByPath.TryGetValue(exePath, out IconInfo? cached))
        {
            return cached;
        }

        if (IconsByPath.Count > MaxEntries)
        {
            IconsByPath.Clear();
        }

        var icon = new IconInfo(exePath);
        IconsByPath[exePath] = icon;
        return icon;
    }

    /// <summary>
    /// Resolves (and caches, by executable path) the "Open file location" <see cref="CommandContextItem"/>
    /// for a process's executable. Code-review finding 7: <see cref="ShowFileInFolderCommand"/> has
    /// no mutable path property (docs/sdk-research.md §5), so without this cache every identity
    /// change in every slot would allocate a fresh command + context item even when several
    /// processes (or the same process across refreshes) share the same executable path.
    /// </summary>
    /// <param name="exePath">The executable path, or <see langword="null"/>/empty if unresolved.</param>
    /// <param name="commandName">The localized command name (<c>Resources.Command_OpenFileLocation</c>).</param>
    /// <returns>The cached <see cref="CommandContextItem"/> for <paramref name="exePath"/>, or <see langword="null"/> if <paramref name="exePath"/> is empty.</returns>
    public static CommandContextItem? GetOpenFileContextItem(string? exePath, string commandName)
    {
        if (string.IsNullOrEmpty(exePath))
        {
            return null;
        }

        if (OpenFileContextItemsByPath.TryGetValue(exePath, out CommandContextItem? cached))
        {
            return cached;
        }

        if (OpenFileContextItemsByPath.Count > MaxEntries)
        {
            OpenFileContextItemsByPath.Clear();
        }

        var command = new ShowFileInFolderCommand(exePath) { Name = commandName };
        var item = new CommandContextItem(command);
        OpenFileContextItemsByPath[exePath] = item;
        return item;
    }
}
