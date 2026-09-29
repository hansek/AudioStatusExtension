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
    private static readonly TimeSpan ListenerHealthCheckInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ListenerStaleAfter = TimeSpan.FromMinutes(30);
    private readonly ICommandItem[] _commands;
    private readonly AudioStatusExtensionPage _page;
    private readonly AudioDevicesPage _outputDevicesPage;
    private readonly AudioDevicesPage _communicationsOutputDevicesPage;
    private readonly AudioDevicesPage _inputDevicesPage;
    private readonly AudioDevicesPage _communicationsInputDevicesPage;
    private readonly AudioDevicesPage _combinedOutputDevicesPage;
    private readonly AudioDevicesPage _combinedInputDevicesPage;
    private readonly AudioStatusDockBand _dockBand;
    private readonly Timer _refreshDebounceTimer;
    private readonly Timer _listenerHealthTimer;
    private readonly Action _scheduleRefreshCallback;
    private readonly object _refreshLock = new();
    private readonly AudioStatusSettings _settingsManager = new();
    private IDisposable? _audioDeviceWatcher;
    private AudioStatusSnapshot _cachedSnapshot;
    private AudioStatusSnapshot? _unreliableSnapshotCandidate;
    private long _lastCallbackUtcTicks;
    private bool _listenerRegistrationSucceeded;
    private bool _disposed;

    public AudioStatusExtensionCommandsProvider()
    {
        DisplayName = "Audio Status";
        Icon = IconHelpers.FromRelativePath("Public\\StoreLogo.png");
        Settings = _settingsManager.Settings;
        ApplyDeviceNameFormat();
        _settingsManager.Settings.SettingsChanged += OnSettingsChanged;
        _scheduleRefreshCallback = CreateWeakScheduleRefreshCallback(this);
        _page = new AudioStatusExtensionPage(_scheduleRefreshCallback);
        _outputDevicesPage = new AudioDevicesPage(AudioDeviceTarget.Output, _scheduleRefreshCallback);
        _communicationsOutputDevicesPage = new AudioDevicesPage(AudioDeviceTarget.CommunicationsOutput, _scheduleRefreshCallback);
        _inputDevicesPage = new AudioDevicesPage(AudioDeviceTarget.Input, _scheduleRefreshCallback);
        _communicationsInputDevicesPage = new AudioDevicesPage(AudioDeviceTarget.CommunicationsInput, _scheduleRefreshCallback);
        _combinedOutputDevicesPage = new AudioDevicesPage(AudioDeviceTarget.CombinedOutput, _scheduleRefreshCallback);
        _combinedInputDevicesPage = new AudioDevicesPage(AudioDeviceTarget.CombinedInput, _scheduleRefreshCallback);
        _dockBand = new AudioStatusDockBand(_scheduleRefreshCallback);
        _commands = [
            new CommandItem(_page) { Title = DisplayName },
            new CommandItem(_outputDevicesPage)
            {
                Title = "Switch output device",
                Subtitle = "Choose the default speakers or headphones",
                Icon = new IconInfo("\uE767"),
            },
            new CommandItem(_communicationsOutputDevicesPage)
            {
                Title = "Switch communications output device",
                Subtitle = "Choose the speakers or headphones used for calls",
                Icon = new IconInfo("\uE767"),
            },
            new CommandItem(_combinedOutputDevicesPage)
            {
                Title = AudioDevicesPage.GetSwitchCommandName(AudioDeviceTarget.CombinedOutput),
                Subtitle = "Set the same speakers or headphones for media and calls",
                Icon = new IconInfo("\uE767"),
            },
            new CommandItem(_inputDevicesPage)
            {
                Title = "Switch input device",
                Subtitle = "Choose the default microphone",
                Icon = new IconInfo("\uE720"),
            },
            new CommandItem(_communicationsInputDevicesPage)
            {
                Title = "Switch communications input device",
                Subtitle = "Choose the microphone used for calls",
                Icon = new IconInfo("\uE720"),
            },
            new CommandItem(_combinedInputDevicesPage)
            {
                Title = AudioDevicesPage.GetSwitchCommandName(AudioDeviceTarget.CombinedInput),
                Subtitle = "Set the same microphone for media and calls",
                Icon = new IconInfo("\uE720"),
            },
        ];
        _refreshDebounceTimer = new Timer(Refresh, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _cachedSnapshot = AudioDeviceService.GetSnapshot();
        RecordCallbackActivity();
        InitializeListener("startup");
        _listenerHealthTimer = new Timer(
            CheckListenerHealth,
            null,
            ListenerHealthCheckInterval,
            ListenerHealthCheckInterval);
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
        lock (_refreshLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _settingsManager.Settings.SettingsChanged -= OnSettingsChanged;
            _settingsManager.Dispose();
            _listenerHealthTimer.Dispose();
            _refreshDebounceTimer.Dispose();
            DisposeListener();
        }

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

    private void OnSettingsChanged(object sender, Settings args)
    {
        ApplyDeviceNameFormat();
        ScheduleRefresh();
    }

    private void ApplyDeviceNameFormat()
    {
        AudioDeviceService.DeviceNameFormat = _settingsManager.DeviceNameFormat;
    }

    private static Action CreateWeakScheduleRefreshCallback(AudioStatusExtensionCommandsProvider provider)
    {
        var weakProvider = new WeakReference<AudioStatusExtensionCommandsProvider>(provider);
        return () =>
        {
            if (weakProvider.TryGetTarget(out var target))
            {
                target.OnAudioDeviceCallback();
            }
        };
    }

    private void OnAudioDeviceCallback()
    {
        RecordCallbackActivity();
        Log("Audio device callback fired.");
        ScheduleRefresh();
    }

    private void Refresh()
    {
        lock (_refreshLock)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                // Always ask Windows for the current defaults. The callback is only a prompt;
                // it is never the source of truth.
                _cachedSnapshot = AudioDeviceService.GetSnapshot();
                _dockBand.Refresh();
                _page.Refresh();
                _outputDevicesPage.RefreshItems();
                _communicationsOutputDevicesPage.RefreshItems();
                _inputDevicesPage.RefreshItems();
                _communicationsInputDevicesPage.RefreshItems();
                _combinedOutputDevicesPage.RefreshItems();
                _combinedInputDevicesPage.RefreshItems();
            }
            catch (Exception ex)
            {
                // Timer and native audio callbacks run outside the Command Palette call stack.
                // A transient refresh failure must not terminate the extension or its watcher.
                Log($"Status refresh failed: {ex.Message}");
            }
        }
    }

    private void Refresh(object? state)
    {
        Refresh();
    }

    private void CheckListenerHealth(object? state)
    {
        lock (_refreshLock)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                var currentSnapshot = AudioDeviceService.GetSnapshot();
                var stateChanged = IsConfirmedStateChange(currentSnapshot);
                var lastCallbackAt = new DateTimeOffset(
                    Interlocked.Read(ref _lastCallbackUtcTicks),
                    TimeSpan.Zero);
                var callbackStale = DateTimeOffset.UtcNow - lastCallbackAt >= ListenerStaleAfter;

                if (stateChanged)
                {
                    Log("Current Windows audio state differs from cached state; refreshing and reinitializing listener.");
                    _cachedSnapshot = currentSnapshot;
                    _dockBand.Refresh();
                    _page.Refresh();
                    _outputDevicesPage.RefreshItems();
                    _communicationsOutputDevicesPage.RefreshItems();
                    _inputDevicesPage.RefreshItems();
                    _communicationsInputDevicesPage.RefreshItems();
                    _combinedOutputDevicesPage.RefreshItems();
                    _combinedInputDevicesPage.RefreshItems();
                }

                if (stateChanged || callbackStale || !_listenerRegistrationSucceeded)
                {
                    var reason = stateChanged
                        ? "missed device change"
                        : _listenerRegistrationSucceeded
                            ? "callback health timeout"
                            : "registration retry";
                    InitializeListener(reason);
                }
            }
            catch (Exception ex)
            {
                Log($"Listener health check failed: {ex.Message}");
            }
        }
    }

    private void InitializeListener(string reason)
    {
        // Unregister the old callback before registering its replacement, so there is
        // never more than one active registration owned by this provider.
        DisposeListener();
        _listenerRegistrationSucceeded = AudioDeviceService.TryWatchDefaultDeviceChanges(
            _scheduleRefreshCallback,
            out var watcher);
        _audioDeviceWatcher = watcher;
        RecordCallbackActivity();
        Log(_listenerRegistrationSucceeded
            ? $"Audio device listener registered ({reason})."
            : $"Audio device listener registration failed ({reason}); retrying on the next health check.");
    }

    private void RecordCallbackActivity()
    {
        Interlocked.Exchange(ref _lastCallbackUtcTicks, DateTimeOffset.UtcNow.Ticks);
    }

    private void DisposeListener()
    {
        var watcher = _audioDeviceWatcher;
        _audioDeviceWatcher = null;
        _listenerRegistrationSucceeded = false;
        if (watcher is null)
        {
            return;
        }

        try
        {
            watcher.Dispose();
            Log("Audio device listener unregistered.");
        }
        catch (Exception ex)
        {
            // A failed native unregistration must not prevent shutdown or a replacement
            // listener from being created.
            Log($"Audio device listener unregistration failed: {ex.Message}");
        }
    }

    private bool IsConfirmedStateChange(AudioStatusSnapshot currentSnapshot)
    {
        if (_cachedSnapshot.HasSameDevices(currentSnapshot))
        {
            _unreliableSnapshotCandidate = null;
            return false;
        }

        if (currentSnapshot.IsReliable())
        {
            _unreliableSnapshotCandidate = null;
            return true;
        }

        // A transient Core Audio query failure can produce an unavailable snapshot.
        // Require the same result twice before treating it as real device state.
        if (_unreliableSnapshotCandidate?.HasSameDevices(currentSnapshot) == true)
        {
            _unreliableSnapshotCandidate = null;
            return true;
        }

        _unreliableSnapshotCandidate = currentSnapshot;
        Log("Ignoring one unconfirmed unavailable audio snapshot.");
        return false;
    }

    private static void Log(string message)
    {
        Console.WriteLine($"[{DateTimeOffset.Now:O}] [AudioStatus] {message}");
    }
}
