using System;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace AudioStatusExtension;

internal sealed partial class AudioDevicesPage : ListPage
{
    private readonly AudioDeviceKind _kind;
    private readonly Action _onChanged;

    public AudioDevicesPage(AudioDeviceKind kind, Action onChanged)
    {
        _kind = kind;
        _onChanged = onChanged;
        Icon = new IconInfo(kind == AudioDeviceKind.Output ? "\uE767" : "\uE720");
        Title = $"{GetKindLabel(kind)} devices";
        Name = Title;
    }

    public override IListItem[] GetItems()
    {
        var devices = AudioDeviceService.GetDevices(_kind);
        if (devices.Length == 0)
        {
            return
            [
                new ListItem(new NoOpCommand())
                {
                    Title = "No active devices found",
                    Subtitle = GetKindLabel(_kind),
                    Icon = Icon,
                },
            ];
        }

        var items = new IListItem[devices.Length];
        for (var index = 0; index < devices.Length; index++)
        {
            var device = devices[index];
            items[index] = new ListItem(new SetDefaultAudioDeviceCommand(_kind, device, Refresh))
            {
                Title = device.Name,
                Subtitle = device.IsDefault ? $"{GetKindLabel(_kind)} - current" : GetKindLabel(_kind),
                Icon = Icon,
            };
        }

        return items;
    }

    private static string GetKindLabel(AudioDeviceKind kind)
    {
        return kind == AudioDeviceKind.Output ? "Output" : "Input";
    }

    private void Refresh()
    {
        RaiseItemsChanged();
        _onChanged();
    }
}

internal sealed partial class SetDefaultAudioDeviceCommand : InvokableCommand
{
    private readonly AudioDeviceKind _kind;
    private readonly AudioDeviceInfo _device;
    private readonly Action _onChanged;

    public SetDefaultAudioDeviceCommand(AudioDeviceKind kind, AudioDeviceInfo device, Action onChanged)
    {
        _kind = kind;
        _device = device;
        _onChanged = onChanged;
        Name = device.Name;
        Icon = new IconInfo(kind == AudioDeviceKind.Output ? "\uE767" : "\uE720");
    }

    public override ICommandResult Invoke()
    {
        try
        {
            AudioDeviceService.SetDefaultDevice(_kind, _device.Id);

            try
            {
                _onChanged();
            }
            catch
            {
                // The device was changed successfully. A UI refresh failure is recovered by
                // the provider's audio notification or periodic refresh.
            }

            return CommandResult.Hide();
        }
        catch (Exception ex)
        {
            return CommandResult.ShowToast($"Could not set default device: {ex.Message}");
        }
    }
}
