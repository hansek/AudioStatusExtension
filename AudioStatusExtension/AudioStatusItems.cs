using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace AudioStatusExtension;

internal static class AudioStatusItems
{
    public static IListItem[] Create()
    {
        var snapshot = AudioDeviceService.GetSnapshot();

        return
        [
            CreateItem(AudioDeviceKind.Output, snapshot.OutputDeviceName, "\uE767"),
            CreateItem(AudioDeviceKind.Input, snapshot.InputDeviceName, "\uE720"),
        ];
    }

    private static ListItem CreateItem(AudioDeviceKind kind, string deviceName, string iconGlyph)
    {
        var title = kind == AudioDeviceKind.Output ? "Output" : "Input";
        var command = new AudioDevicesPage(kind);
        var icon = new IconInfo(iconGlyph);

        return new ListItem(command)
        {
            Title = deviceName,
            Subtitle = title,
            Icon = icon,
            MoreCommands = CreateDeviceContextCommands(kind, title, icon),
        };
    }

    private static IContextItem[] CreateDeviceContextCommands(AudioDeviceKind kind, string title, IconInfo icon)
    {
        var devices = AudioDeviceService.GetDevices(kind);
        if (devices.Length == 0)
        {
            return
            [
                new CommandContextItem(new AudioDevicesPage(kind))
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
            commands[index] = new CommandContextItem(new SetDefaultAudioDeviceCommand(kind, device))
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
    public AudioStatusDockBand()
        : base(AudioStatusItems.Create(), "audio-status.default-devices", "Audio Status")
    {
    }

    public void Refresh()
    {
        Items = AudioStatusItems.Create();
    }
}
