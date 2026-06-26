// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Threading;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace AudioStatusExtension;

public partial class AudioStatusExtensionCommandsProvider : CommandProvider
{
    private readonly ICommandItem[] _commands;
    private readonly AudioStatusExtensionPage _page;
    private readonly AudioStatusDockBand _dockBand;
    private readonly IDisposable _audioDeviceWatcher;
    private readonly Timer _refreshDebounceTimer;
    private readonly Action _scheduleRefreshCallback;
    private readonly object _refreshLock = new();

    public AudioStatusExtensionCommandsProvider()
    {
        DisplayName = "Audio Status";
        Icon = IconHelpers.FromRelativePath("Public\\StoreLogo.png");
        _scheduleRefreshCallback = CreateWeakScheduleRefreshCallback(this);
        _page = new AudioStatusExtensionPage(_scheduleRefreshCallback);
        _dockBand = new AudioStatusDockBand(_scheduleRefreshCallback);
        _commands = [
            new CommandItem(_page) { Title = DisplayName },
        ];
        _refreshDebounceTimer = new Timer(Refresh, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _audioDeviceWatcher = AudioDeviceService.WatchDefaultDeviceChanges(_scheduleRefreshCallback);
    }

    public override ICommandItem[] TopLevelCommands()
    {
        return _commands;
    }

    public override ICommandItem[] GetDockBands()
    {
        return [_dockBand];
    }

    public override void Dispose()
    {
        _audioDeviceWatcher.Dispose();
        _refreshDebounceTimer.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private void ScheduleRefresh()
    {
        try
        {
            _refreshDebounceTimer.Change(TimeSpan.FromMilliseconds(250), Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static Action CreateWeakScheduleRefreshCallback(AudioStatusExtensionCommandsProvider provider)
    {
        var weakProvider = new WeakReference<AudioStatusExtensionCommandsProvider>(provider);
        return () =>
        {
            if (weakProvider.TryGetTarget(out var target))
            {
                target.ScheduleRefresh();
            }
        };
    }

    private void Refresh()
    {
        lock (_refreshLock)
        {
            try
            {
                _dockBand.Refresh();
            }
            catch
            {
                // Timer and native audio callbacks run outside the Command Palette call stack.
                // A transient refresh failure must not terminate the extension or its watcher.
            }
        }
    }

    private void Refresh(object? state)
    {
        Refresh();
    }
}
