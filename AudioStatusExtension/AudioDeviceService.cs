using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.Foundation;
using Windows.Media.Devices;

namespace AudioStatusExtension;

internal static partial class AudioDeviceService
{
    private const int DeviceStateActive = 0x00000001;
    private static readonly string[] RegistryDeviceNameProperties =
    [
        "{026e516e-b814-414b-83cd-856d6fef4822},2",
        "{b3f8fa53-0004-438e-9003-51a46e139bfc},6",
        "{a45c254e-df1c-4efd-8020-67d146a850e0},14",
        "{a45c254e-df1c-4efd-8020-67d146a850e0},2",
    ];

    public static IDisposable WatchDefaultDeviceChanges(Action onChanged)
    {
        if (!OperatingSystem.IsWindows())
        {
            return NullDisposable.Instance;
        }

        var watchers = new List<IDisposable>(2);

        try
        {
            watchers.Add(new AudioDeviceWatcher(onChanged));
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException)
        {
        }

        try
        {
            watchers.Add(new MediaDeviceWatcher(onChanged));
        }
        catch (Exception)
        {
        }

        return watchers.Count switch
        {
            0 => NullDisposable.Instance,
            1 => watchers[0],
            _ => new CompositeDisposable(watchers),
        };
    }

    public static AudioStatusSnapshot GetSnapshot()
    {
        return new AudioStatusSnapshot(
            GetDefaultDeviceName(AudioDeviceKind.Output),
            GetDefaultDeviceName(AudioDeviceKind.Input),
            DateTimeOffset.Now);
    }

    public static AudioDeviceInfo[] GetDevices(AudioDeviceKind kind)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        try
        {
            var dataFlow = ToDataFlow(kind);
            var defaultDeviceId = GetDefaultDeviceId(dataFlow);
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();

            try
            {
                Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(dataFlow, (uint)DeviceStateActive, out var devices));

                try
                {
                    Marshal.ThrowExceptionForHR(devices.GetCount(out var count));

                    var result = new List<AudioDeviceInfo>(checked((int)count));
                    for (uint index = 0; index < count; index++)
                    {
                        Marshal.ThrowExceptionForHR(devices.Item(index, out var device));

                        try
                        {
                            Marshal.ThrowExceptionForHR(device.GetId(out var id));
                            result.Add(new AudioDeviceInfo(id, GetDeviceName(device), id == defaultDeviceId));
                        }
                        catch (COMException)
                        {
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(device);
                        }
                    }

                    var registryDevices = GetRegistryDevices(kind, defaultDeviceId);
                    return registryDevices.Length > result.Count ? registryDevices : result.ToArray();
                }
                finally
                {
                    Marshal.ReleaseComObject(devices);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException)
        {
            return GetRegistryDevices(kind, null);
        }
    }

    public static void SetDefaultDevice(AudioDeviceKind kind, string deviceId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var policyConfig = (IPolicyConfig)new PolicyConfigClient();

        try
        {
            Marshal.ThrowExceptionForHR(policyConfig.SetDefaultEndpoint(deviceId, ERole.Console));
            Marshal.ThrowExceptionForHR(policyConfig.SetDefaultEndpoint(deviceId, ERole.Multimedia));
            Marshal.ThrowExceptionForHR(policyConfig.SetDefaultEndpoint(deviceId, ERole.Communications));
        }
        finally
        {
            Marshal.ReleaseComObject(policyConfig);
        }
    }

    private static string GetDefaultDeviceName(AudioDeviceKind kind)
    {
        var dataFlow = ToDataFlow(kind);

        if (!OperatingSystem.IsWindows())
        {
            return "Windows only";
        }

        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();

            try
            {
                Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(dataFlow, ERole.Multimedia, out var device));

                try
                {
                    Marshal.ThrowExceptionForHR(device.OpenPropertyStore(StorageAccessMode.Read, out var store));

                    try
                    {
                        return GetDeviceName(store);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(store);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(device);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException)
        {
            return GetDefaultDeviceNameFallback(kind);
        }
    }

    private static string GetDefaultDeviceNameFallback(AudioDeviceKind kind)
    {
        var defaultDeviceId = GetDefaultDeviceIdFromMediaDevice(kind);
        if (!string.IsNullOrWhiteSpace(defaultDeviceId))
        {
            foreach (var device in GetRegistryDevices(kind, defaultDeviceId))
            {
                if (IsSameEndpointId(device.Id, defaultDeviceId))
                {
                    return device.Name;
                }
            }
        }

        return "Unavailable";
    }

    private static AudioDeviceInfo[] GetDefaultDeviceFallback(AudioDeviceKind kind)
    {
        var dataFlow = ToDataFlow(kind);
        IMMDeviceEnumerator? enumerator = null;

        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(dataFlow, ERole.Multimedia, out var device));

            try
            {
                Marshal.ThrowExceptionForHR(device.GetId(out var id));
                return [new AudioDeviceInfo(id, GetDeviceName(device), true)];
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException)
        {
            return [];
        }
        finally
        {
            if (enumerator is not null)
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }
    }

    private static AudioDeviceInfo[] GetRegistryDevices(AudioDeviceKind kind, string? defaultDeviceId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var devices = new List<AudioDeviceInfo>();
        var subkeyName = kind == AudioDeviceKind.Output ? "Render" : "Capture";
        using var audioKey = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\{subkeyName}");
        if (audioKey is null)
        {
            return GetDefaultDeviceFallback(kind);
        }

        foreach (var endpointKeyName in audioKey.GetSubKeyNames())
        {
            using var endpointKey = audioKey.OpenSubKey(endpointKeyName);
            if (endpointKey is null || !IsActiveEndpoint(endpointKey))
            {
                continue;
            }

            var endpointId = BuildEndpointId(kind, endpointKeyName);
            var deviceName = GetRegistryDeviceName(endpointKey);
            devices.Add(new AudioDeviceInfo(endpointId, deviceName, IsSameEndpointId(endpointId, defaultDeviceId)));
        }

        return devices.ToArray();
    }

    private static bool IsActiveEndpoint(RegistryKey endpointKey)
    {
        return endpointKey.GetValue("DeviceState") is int state && state == DeviceStateActive;
    }

    private static string GetRegistryDeviceName(RegistryKey endpointKey)
    {
        using var propertiesKey = endpointKey.OpenSubKey("Properties");
        if (propertiesKey is null)
        {
            return "Unknown device";
        }

        foreach (var propertyName in RegistryDeviceNameProperties)
        {
            if (propertiesKey.GetValue(propertyName) is string value && !string.IsNullOrWhiteSpace(value))
            {
                return CleanDeviceName(value);
            }
        }

        return "Unknown device";
    }

    private static string BuildEndpointId(AudioDeviceKind kind, string endpointKeyName)
    {
        var prefix = kind == AudioDeviceKind.Output ? "{0.0.0.00000000}" : "{0.0.1.00000000}";
        return $"{prefix}.{endpointKeyName}";
    }

    private static string? GetDefaultDeviceId(EDataFlow dataFlow)
    {
        IMMDeviceEnumerator? enumerator = null;

        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(dataFlow, ERole.Multimedia, out var device));

            try
            {
                Marshal.ThrowExceptionForHR(device.GetId(out var id));
                return id;
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException)
        {
            return GetDefaultDeviceIdFromMediaDevice(ToDeviceKind(dataFlow));
        }
        finally
        {
            if (enumerator is not null)
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }
    }

    private static string? GetDefaultDeviceIdFromMediaDevice(AudioDeviceKind kind)
    {
        try
        {
            var id = kind == AudioDeviceKind.Output
                ? MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default)
                : MediaDevice.GetDefaultAudioCaptureId(AudioDeviceRole.Default);

            return string.IsNullOrWhiteSpace(id) ? null : NormalizeEndpointId(id);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string GetDeviceName(IMMDevice device)
    {
        Marshal.ThrowExceptionForHR(device.OpenPropertyStore(StorageAccessMode.Read, out var store));

        try
        {
            return GetDeviceName(store);
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    private static string GetDeviceName(IPropertyStore store)
    {
        foreach (var propertyKey in PropertyKeys.DeviceNames)
        {
            var key = propertyKey;
            var result = store.GetValue(ref key, out var value);
            if (result < 0)
            {
                continue;
            }

            try
            {
                var deviceName = value.GetString();
                if (!string.IsNullOrWhiteSpace(deviceName))
                {
                    return deviceName.Trim();
                }
            }
            finally
            {
                Marshal.ThrowExceptionForHR(PropVariantClear(ref value));
            }
        }

        return "Unknown device";
    }

    private static string CleanDeviceName(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return "Unknown device";
        }

        return deviceName.Trim();
    }

    private static EDataFlow ToDataFlow(AudioDeviceKind kind)
    {
        return kind == AudioDeviceKind.Output ? EDataFlow.Render : EDataFlow.Capture;
    }

    private static AudioDeviceKind ToDeviceKind(EDataFlow dataFlow)
    {
        return dataFlow == EDataFlow.Capture ? AudioDeviceKind.Input : AudioDeviceKind.Output;
    }

    private static bool IsSameEndpointId(string endpointId, string? otherEndpointId)
    {
        return !string.IsNullOrWhiteSpace(otherEndpointId)
            && string.Equals(NormalizeEndpointId(endpointId), NormalizeEndpointId(otherEndpointId), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeEndpointId(string endpointId)
    {
        var trimmed = endpointId.Trim();
        var start = trimmed.IndexOf("{0.0.", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return trimmed;
        }

        var end = trimmed.IndexOf('#', start);
        return (end > start ? trimmed[start..end] : trimmed[start..]).Replace('#', '.');
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    private sealed partial class AudioDeviceWatcher : IMMNotificationClient, IDisposable
    {
        private readonly Action _onChanged;
        private readonly IMMDeviceEnumerator _enumerator;
        private bool _disposed;

        public AudioDeviceWatcher(Action onChanged)
        {
            _onChanged = onChanged;
            _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            Marshal.ThrowExceptionForHR(_enumerator.RegisterEndpointNotificationCallback(this));
        }

        public int OnDeviceStateChanged(string deviceId, uint newState)
        {
            return 0;
        }

        public int OnDeviceAdded(string deviceId)
        {
            return 0;
        }

        public int OnDeviceRemoved(string deviceId)
        {
            return 0;
        }

        public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? defaultDeviceId)
        {
            if (flow is EDataFlow.Render or EDataFlow.Capture)
            {
                try
                {
                    _onChanged();
                }
                catch
                {
                    // Exceptions must never escape a native Core Audio callback. Doing so can
                    // cause Windows to stop delivering notifications to this client.
                }
            }

            return 0;
        }

        public int OnPropertyValueChanged(string deviceId, PropertyKey key)
        {
            return 0;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                Marshal.ThrowExceptionForHR(_enumerator.UnregisterEndpointNotificationCallback(this));
            }
            catch (COMException)
            {
            }
            finally
            {
                Marshal.ReleaseComObject(_enumerator);
                GC.SuppressFinalize(this);
            }
        }
    }

    private sealed partial class MediaDeviceWatcher : IDisposable
    {
        private readonly Action _onChanged;
        private readonly TypedEventHandler<object, DefaultAudioRenderDeviceChangedEventArgs> _renderHandler;
        private readonly TypedEventHandler<object, DefaultAudioCaptureDeviceChangedEventArgs> _captureHandler;
        private bool _disposed;

        public MediaDeviceWatcher(Action onChanged)
        {
            _onChanged = onChanged;
            _renderHandler = OnRenderChanged;
            _captureHandler = OnCaptureChanged;

            try
            {
                MediaDevice.DefaultAudioRenderDeviceChanged += _renderHandler;
                MediaDevice.DefaultAudioCaptureDeviceChanged += _captureHandler;
            }
            catch
            {
                try
                {
                    MediaDevice.DefaultAudioRenderDeviceChanged -= _renderHandler;
                }
                catch
                {
                }

                try
                {
                    MediaDevice.DefaultAudioCaptureDeviceChanged -= _captureHandler;
                }
                catch
                {
                }

                throw;
            }
        }

        private void OnRenderChanged(object sender, DefaultAudioRenderDeviceChangedEventArgs args)
        {
            NotifyChanged();
        }

        private void OnCaptureChanged(object sender, DefaultAudioCaptureDeviceChangedEventArgs args)
        {
            NotifyChanged();
        }

        private void NotifyChanged()
        {
            try
            {
                _onChanged();
            }
            catch
            {
                // WinRT device change events should not be able to terminate the extension.
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            MediaDevice.DefaultAudioRenderDeviceChanged -= _renderHandler;
            MediaDevice.DefaultAudioCaptureDeviceChanged -= _captureHandler;
            GC.SuppressFinalize(this);
        }
    }

    private sealed partial class CompositeDisposable : IDisposable
    {
        private readonly IDisposable[] _disposables;
        private bool _disposed;

        public CompositeDisposable(IEnumerable<IDisposable> disposables)
        {
            _disposables = [.. disposables];
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var disposable in _disposables)
            {
                disposable.Dispose();
            }

            GC.SuppressFinalize(this);
        }
    }

    private sealed partial class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();

        public void Dispose()
        {
        }
    }

    private static class PropertyKeys
    {
        public static readonly PropertyKey[] DeviceNames =
        [
            Create("026e516e-b814-414b-83cd-856d6fef4822", 2),
            Create("b3f8fa53-0004-438e-9003-51a46e139bfc", 6),
            Create("a45c254e-df1c-4efd-8020-67d146a850e0", 14),
            Create("a45c254e-df1c-4efd-8020-67d146a850e0", 2),
        ];

        private static PropertyKey Create(string formatId, uint propertyId)
        {
            return new PropertyKey
            {
                FormatId = new Guid(formatId),
                PropertyId = propertyId,
            };
        }
    }

    private enum EDataFlow
    {
        Render,
        Capture,
        All,
    }

    private enum ERole
    {
        Console,
        Multimedia,
        Communications,
    }

    private enum StorageAccessMode
    {
        Read,
        Write,
        ReadWrite,
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator
    {
    }

    [ComImport]
    [Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private class PolicyConfigClient
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(
            EDataFlow dataFlow,
            uint stateMask,
            [MarshalAs(UnmanagedType.Interface)] out IMMDeviceCollection devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IMMNotificationClient client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-C0DD2D112A8D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int Item(uint deviceNumber, out IMMDevice device);
    }

    [ComImport]
    [Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMNotificationClient
    {
        [PreserveSig]
        int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, uint newState);

        [PreserveSig]
        int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

        [PreserveSig]
        int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

        [PreserveSig]
        int OnDefaultDeviceChanged(
            EDataFlow flow,
            ERole role,
            [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId);

        [PreserveSig]
        int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, uint classContext, IntPtr activationParams, out IntPtr interfacePointer);

        [PreserveSig]
        int OpenPropertyStore(StorageAccessMode accessMode, out IPropertyStore properties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetState(out uint state);
    }

    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig]
        int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IntPtr format);

        [PreserveSig]
        int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, bool defaultFormat, out IntPtr format);

        [PreserveSig]
        int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

        [PreserveSig]
        int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr endpointFormat, IntPtr mixFormat);

        [PreserveSig]
        int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, bool defaultPeriod, out long defaultPeriodValue, out long minimumPeriodValue);

        [PreserveSig]
        int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr period);

        [PreserveSig]
        int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);

        [PreserveSig]
        int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);

        [PreserveSig]
        int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref PropertyKey key, out PropVariant value);

        [PreserveSig]
        int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref PropertyKey key, ref PropVariant value);

        [PreserveSig]
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);

        [PreserveSig]
        int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, bool visible);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int GetAt(uint propertyIndex, out PropertyKey key);

        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropVariant value);

        [PreserveSig]
        int SetValue(ref PropertyKey key, ref PropVariant value);

        [PreserveSig]
        int Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)]
        private readonly ushort _valueType;

        [FieldOffset(8)]
        private readonly IntPtr _pointerValue;

        public string? GetString()
        {
            const ushort vtLpwstr = 31;
            return _valueType == vtLpwstr && _pointerValue != IntPtr.Zero
                ? Marshal.PtrToStringUni(_pointerValue)
                : null;
        }
    }
}

internal sealed record AudioStatusSnapshot(string OutputDeviceName, string InputDeviceName, DateTimeOffset UpdatedAt);

internal enum AudioDeviceKind
{
    Output,
    Input,
}

internal sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault);
