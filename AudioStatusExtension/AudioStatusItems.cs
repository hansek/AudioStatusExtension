using System;
using System.Collections.Generic;
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
            CreateItem(AudioDeviceTarget.Output, snapshot.OutputDeviceName, onChanged),
            CreateItem(AudioDeviceTarget.CommunicationsOutput, snapshot.CommunicationsOutputDeviceName, onChanged),
            CreateItem(AudioDeviceTarget.Input, snapshot.InputDeviceName, onChanged),
            CreateItem(AudioDeviceTarget.CommunicationsInput, snapshot.CommunicationsInputDeviceName, onChanged),
        ];
    }

    public static IListItem[] CreateDockItems(Action onChanged)
    {
        var snapshot = AudioDeviceService.GetSnapshot();

        return
        [
            CreateItem(AudioDeviceTarget.Output, snapshot.OutputDeviceName, onChanged),
            CreateItem(AudioDeviceTarget.Input, snapshot.InputDeviceName, onChanged),
        ];
    }

    public static void UpdateCurrentDeviceItems(IListItem[] items, Action onChanged)
    {
        if (items.Length < 2)
        {
            return;
        }

        var snapshot = AudioDeviceService.GetSnapshot();
        UpdateItem(items[0], AudioDeviceTarget.Output, snapshot.OutputDeviceName, onChanged);
        UpdateItem(items[1], AudioDeviceTarget.Input, snapshot.InputDeviceName, onChanged);
    }

    private static ListItem CreateItem(AudioDeviceTarget target, string deviceName, Action onChanged)
    {
        var title = AudioDevicesPage.GetTargetLabel(target);
        var command = new AudioDevicesPage(target, onChanged);
        var icon = new IconInfo(AudioDevicesPage.GetIconGlyph(target));

        return new ListItem(command)
        {
            Title = deviceName,
            Subtitle = title,
            Icon = icon,
            MoreCommands = CreateDeviceContextCommands(target, title, icon, onChanged),
        };
    }

    private static IContextItem[] CreateDeviceContextCommands(
        AudioDeviceTarget target,
        string title,
        IconInfo icon,
        Action onChanged)
    {
        var devices = AudioDeviceService.GetDevices(target);
        var commands = new List<IContextItem>(devices.Length + 1);
        if (devices.Length == 0)
        {
            commands.Add(
                new CommandContextItem(new AudioDevicesPage(target, onChanged))
                {
                    Title = $"Select {title} device",
                    Icon = icon,
                });
        }
        else
        {
            foreach (var device in devices)
            {
                commands.Add(new CommandContextItem(new SetDefaultAudioDeviceCommand(target, device, onChanged))
                {
                    Title = device.Name,
                    Subtitle = device.IsDefault ? $"{title} - current" : title,
                    Icon = icon,
                });
            }
        }

        if (target.Role == AudioEndpointRole.Default)
        {
            var communicationsTarget = target.Kind == AudioDeviceKind.Output
                ? AudioDeviceTarget.CommunicationsOutput
                : AudioDeviceTarget.CommunicationsInput;
            commands.Add(new CommandContextItem(new AudioDevicesPage(communicationsTarget, onChanged))
            {
                Title = $"Select {AudioDevicesPage.GetTargetLabel(communicationsTarget).ToLowerInvariant()} device",
                Icon = icon,
            });
        }

        var combinedTarget = target.Kind == AudioDeviceKind.Output
            ? AudioDeviceTarget.CombinedOutput
            : AudioDeviceTarget.CombinedInput;
        commands.Add(new CommandContextItem(new AudioDevicesPage(combinedTarget, onChanged))
        {
            Title = AudioDevicesPage.GetSwitchCommandName(combinedTarget),
            Icon = icon,
        });

        return [.. commands];
    }

    private static void UpdateItem(
        IListItem item,
        AudioDeviceTarget target,
        string title,
        Action onChanged)
    {
        if (item is not ListItem listItem)
        {
            return;
        }

        if (listItem.Title != title)
        {
            listItem.Title = title;
        }

        listItem.MoreCommands = CreateDeviceContextCommands(
            target,
            AudioDevicesPage.GetTargetLabel(target),
            new IconInfo(AudioDevicesPage.GetIconGlyph(target)),
            onChanged);
    }
}

internal sealed partial class AudioStatusDockBand : WrappedDockItem
{
    private readonly Action _onChanged;

    public AudioStatusDockBand(Action onChanged)
        : base(AudioStatusItems.CreateDockItems(onChanged), "audio-status.default-devices", "Audio Status")
    {
        _onChanged = onChanged;
    }

    public void Refresh()
    {
        // WrappedDockItem does not expose an ItemsChanged notification. Keep the
        // existing ListItem instances and update their observable properties so the
        // host receives the change through each item's PropChanged event.
        AudioStatusItems.UpdateCurrentDeviceItems(Items, _onChanged);
    }
}
