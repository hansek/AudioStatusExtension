using System;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace AudioStatusExtension;

internal static class AudioStatusItems
{
    public static IListItem[] Create(Action onChanged)
    {
        var snapshot = AudioDeviceService.GetSnapshot();

        return
        [
            CreateItem(AudioDeviceKind.Output, snapshot.OutputDeviceName, "\uE767", onChanged),
            CreateItem(AudioDeviceKind.Input, snapshot.InputDeviceName, "\uE720", onChanged),
        ];
    }

    private static ListItem CreateItem(AudioDeviceKind kind, string deviceName, string iconGlyph, Action onChanged)
    {
        var title = kind == AudioDeviceKind.Output ? "Output" : "Input";
        var command = new AudioDevicesPage(kind, onChanged);
        var icon = new IconInfo(iconGlyph);

        return new ListItem(command)
        {
            Title = deviceName,
            Subtitle = title,
            Icon = icon,
            MoreCommands = CreateDeviceContextCommands(kind, title, icon, onChanged),
        };
    }

    private static IContextItem[] CreateDeviceContextCommands(AudioDeviceKind kind, string title, IconInfo icon, Action onChanged)
    {
        var devices = AudioDeviceService.GetDevices(kind);
        if (devices.Length == 0)
        {
            return
            [
                new CommandContextItem(new AudioDevicesPage(kind, onChanged))
                {
                    Title = $"Select {title} device",
                    Icon = icon,
                },
            ];
        }

        var commands = new IContextItem[devices.Length];
        for (var index = 0; index < devices.Length; index++)
        {
            var device = devices[index];
            commands[index] = new CommandContextItem(new SetDefaultAudioDeviceCommand(kind, device, onChanged))
            {
                Title = device.Name,
                Subtitle = device.IsDefault ? $"{title} - current" : title,
                Icon = icon,
            };
        }

        return commands;
    }
}

internal sealed partial class AudioStatusDockBand : WrappedDockItem
{
    private readonly Action _onChanged;

    public AudioStatusDockBand(Action onChanged)
        : base(AudioStatusItems.Create(onChanged), "audio-status.default-devices", "Audio Status")
    {
        _onChanged = onChanged;
    }

    public void Refresh()
    {
        Items = AudioStatusItems.Create(_onChanged);
    }
}
