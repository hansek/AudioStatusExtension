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
    private readonly Timer _refreshTimer;
    private readonly object _refreshLock = new();

    public AudioStatusExtensionCommandsProvider()
    {
        DisplayName = "Audio Status";
        Icon = IconHelpers.FromRelativePath("Public\\StoreLogo.png");
        _page = new AudioStatusExtensionPage(Refresh);
        _dockBand = new AudioStatusDockBand(Refresh);
        _commands = [
            new CommandItem(_page) { Title = DisplayName },
        ];
        _audioDeviceWatcher = AudioDeviceService.WatchDefaultDeviceChanges(Refresh);
        _refreshTimer = new Timer(Refresh, null, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
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
        _refreshTimer.Dispose();
        _audioDeviceWatcher.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private void Refresh()
    {
        lock (_refreshLock)
        {
            try
            {
                _dockBand.Refresh();
                _page.Refresh();
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
