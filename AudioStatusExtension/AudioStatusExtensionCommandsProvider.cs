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
    private readonly AudioStatusDockBand _dockBand;
    private readonly IDisposable _audioDeviceWatcher;
    private readonly Timer _refreshTimer;

    public AudioStatusExtensionCommandsProvider()
    {
        DisplayName = "Audio Status";
        Icon = IconHelpers.FromRelativePath("Public\\StoreLogo.png");
        _dockBand = new AudioStatusDockBand();
        _commands = [
            new CommandItem(new AudioStatusExtensionPage()) { Title = DisplayName },
        ];
        _audioDeviceWatcher = AudioDeviceService.WatchDefaultDeviceChanges(RefreshDockBand);
        _refreshTimer = new Timer(RefreshDockBand, null, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
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

    private void RefreshDockBand()
    {
        _dockBand.Refresh();
    }

    private void RefreshDockBand(object? state)
    {
        RefreshDockBand();
    }
}
