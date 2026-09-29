using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Microsoft.Win32;
using Windows.Foundation;
using Windows.Media.Devices;

namespace AudioStatusExtension;

internal static partial class AudioDeviceService
{
    private const int DeviceStateActive = 0x00000001;
    private const uint ClsctxInprocServer = 0x1;
    private static readonly Guid MMDeviceEnumeratorClassId = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid MMDeviceEnumeratorInterfaceId = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid PolicyConfigClassId = new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");
    private static readonly Guid PolicyConfigInterfaceId = new("F8679F50-850A-41CF-9C72-430F290290C8");
    private static readonly StrategyBasedComWrappers ComWrappers = new();
    private static readonly string[] WindowsDisplayNameRegistryProperties =
    [
        "{a45c254e-df1c-4efd-8020-67d146a850e0},14",
        "{026e516e-b814-414b-83cd-856d6fef4822},2",
        "{b3f8fa53-0004-438e-9003-51a46e139bfc},6",
        "{a45c254e-df1c-4efd-8020-67d146a850e0},2",
    ];
    private static readonly string[] AudioAdapterNameRegistryProperties =
    [
        "{026e516e-b814-414b-83cd-856d6fef4822},2",
        "{b3f8fa53-0004-438e-9003-51a46e139bfc},6",
        "{a45c254e-df1c-4efd-8020-67d146a850e0},14",
        "{a45c254e-df1c-4efd-8020-67d146a850e0},2",
    ];

    public static AudioDeviceNameFormat DeviceNameFormat { get; set; } = AudioDeviceNameFormat.WindowsDisplayName;

    public static bool TryWatchDefaultDeviceChanges(Action onChanged, out IDisposable watcher)
    {
        if (!OperatingSystem.IsWindows())
        {
            watcher = NullDisposable.Instance;
            return false;
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

        watcher = watchers.Count switch
        {
            0 => NullDisposable.Instance,
            1 => watchers[0],
            _ => new CompositeDisposable(watchers),
        };

        return watchers.Count > 0;
    }

    public static AudioStatusSnapshot GetSnapshot()
    {
        var outputDeviceId = GetDefaultDeviceId(AudioDeviceTarget.Output);
        var communicationsOutputDeviceId = GetDefaultDeviceId(AudioDeviceTarget.CommunicationsOutput);
        var inputDeviceId = GetDefaultDeviceId(AudioDeviceTarget.Input);
        var communicationsInputDeviceId = GetDefaultDeviceId(AudioDeviceTarget.CommunicationsInput);
        return new AudioStatusSnapshot(
            outputDeviceId,
            GetDefaultDeviceName(AudioDeviceTarget.Output),
            communicationsOutputDeviceId,
            GetDefaultDeviceName(AudioDeviceTarget.CommunicationsOutput),
            inputDeviceId,
            GetDefaultDeviceName(AudioDeviceTarget.Input),
            communicationsInputDeviceId,
            GetDefaultDeviceName(AudioDeviceTarget.CommunicationsInput),
            DateTimeOffset.Now);
    }

    public static AudioDeviceInfo[] GetDevices(AudioDeviceTarget target)
    {
        if (target.Role != AudioEndpointRole.Both)
        {
            return GetDevicesForRole(target);
        }

        var mediaTarget = new AudioDeviceTarget(target.Kind, AudioEndpointRole.Default);
        var callsTarget = new AudioDeviceTarget(target.Kind, AudioEndpointRole.Communications);
        var devices = GetDevicesForRole(mediaTarget);
        var callsDeviceId = GetDefaultDeviceId(callsTarget);
        for (var index = 0; index < devices.Length; index++)
        {
            var device = devices[index];
            var isCallsDefault = IsSameEndpointId(device.Id, callsDeviceId);
            devices[index] = device with
            {
                IsDefault = device.IsDefault && isCallsDefault,
                IsMediaDefault = device.IsDefault,
                IsCommunicationsDefault = isCallsDefault,
            };
        }

        return devices;
    }

    private static AudioDeviceInfo[] GetDevicesForRole(AudioDeviceTarget target)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var defaultDeviceId = GetDefaultDeviceId(target);
        try
        {
            var dataFlow = ToDataFlow(target.Kind);
            var enumerator = CreateDeviceEnumerator();

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
                            ReleaseComObject(device);
                        }
                    }

                    AddMissingRegistryDevices(result, GetRegistryDevices(target, defaultDeviceId));
                    return result.ToArray();
                }
                finally
                {
                    ReleaseComObject(devices);
                }
            }
            finally
            {
                ReleaseComObject(enumerator);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException)
        {
            return GetRegistryDevices(target, defaultDeviceId);
        }
    }

    public static void SetDefaultDevice(AudioDeviceTarget target, string deviceId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var policyConfig = ActivateComObject<IPolicyConfig>(PolicyConfigClassId, PolicyConfigInterfaceId);

        var changedRoles = 0;
        try
        {
            if (target.Role != AudioEndpointRole.Communications)
            {
                Marshal.ThrowExceptionForHR(policyConfig.SetDefaultEndpoint(deviceId, ERole.Console));
                changedRoles++;
                Marshal.ThrowExceptionForHR(policyConfig.SetDefaultEndpoint(deviceId, ERole.Multimedia));
                changedRoles++;
            }

            if (target.Role is AudioEndpointRole.Communications or AudioEndpointRole.Both)
            {
                Marshal.ThrowExceptionForHR(policyConfig.SetDefaultEndpoint(deviceId, ERole.Communications));
            }
        }
        catch (Exception ex) when (changedRoles > 0)
        {
            throw new InvalidOperationException("Some audio defaults changed, but the switch could not be completed. Check the current devices and try again.", ex);
        }
        finally
        {
            ReleaseComObject(policyConfig);
        }
    }

    private static string GetDefaultDeviceName(AudioDeviceTarget target)
    {
        var dataFlow = ToDataFlow(target.Kind);

        if (!OperatingSystem.IsWindows())
        {
            return "Windows only";
        }

        try
        {
            var enumerator = CreateDeviceEnumerator();

            try
            {
                Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(dataFlow, ToERole(target.Role), out var device));

                try
                {
                    Marshal.ThrowExceptionForHR(device.OpenPropertyStore(StorageAccessMode.Read, out var store));

                    try
                    {
                        return GetDeviceName(store);
                    }
                    finally
                    {
                        ReleaseComObject(store);
                    }
                }
                finally
                {
                    ReleaseComObject(device);
                }
            }
            finally
            {
                ReleaseComObject(enumerator);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException)
        {
            return GetDefaultDeviceNameFallback(target);
        }
    }

    private static string GetDefaultDeviceNameFallback(AudioDeviceTarget target)
    {
        var defaultDeviceId = GetDefaultDeviceIdFromMediaDevice(target);
        if (!string.IsNullOrWhiteSpace(defaultDeviceId))
        {
            foreach (var device in GetRegistryDevices(target, defaultDeviceId))
            {
                if (IsSameEndpointId(device.Id, defaultDeviceId))
                {
                    return device.Name;
                }
            }
        }

        return "Unavailable";
    }

    private static AudioDeviceInfo[] GetDefaultDeviceFallback(AudioDeviceTarget target)
    {
        var dataFlow = ToDataFlow(target.Kind);
        IMMDeviceEnumerator? enumerator = null;

        try
        {
            enumerator = CreateDeviceEnumerator();
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(dataFlow, ToERole(target.Role), out var device));

            try
            {
                Marshal.ThrowExceptionForHR(device.GetId(out var id));
                return [new AudioDeviceInfo(id, GetDeviceName(device), true)];
            }
            finally
            {
                ReleaseComObject(device);
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
                ReleaseComObject(enumerator);
            }
        }
    }

    private static AudioDeviceInfo[] GetRegistryDevices(AudioDeviceTarget target, string? defaultDeviceId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var kind = target.Kind;
        var devices = new List<AudioDeviceInfo>();
        var subkeyName = kind == AudioDeviceKind.Output ? "Render" : "Capture";
        using var audioKey = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\{subkeyName}");
        if (audioKey is null)
        {
            return GetDefaultDeviceFallback(target);
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

    private static void AddMissingRegistryDevices(List<AudioDeviceInfo> devices, AudioDeviceInfo[] registryDevices)
    {
        foreach (var registryDevice in registryDevices)
        {
            var alreadyPresent = false;
            foreach (var device in devices)
            {
                if (IsSameEndpointId(device.Id, registryDevice.Id))
                {
                    alreadyPresent = true;
                    break;
                }
            }

            if (!alreadyPresent)
            {
                devices.Add(registryDevice);
            }
        }
    }

    private static string GetRegistryDeviceName(RegistryKey endpointKey)
    {
        using var propertiesKey = endpointKey.OpenSubKey("Properties");
        if (propertiesKey is null)
        {
            return "Unknown device";
        }

        if (DeviceNameFormat == AudioDeviceNameFormat.WindowsDisplayName)
        {
            var friendlyName = GetRegistryString(propertiesKey, WindowsDisplayNameRegistryProperties[0]);
            if (friendlyName is not null)
            {
                return friendlyName;
            }

            var customName = GetRegistryString(propertiesKey, WindowsDisplayNameRegistryProperties[1]);
            var endpointName = GetRegistryString(propertiesKey, WindowsDisplayNameRegistryProperties[2]);
            var deviceDescription = GetRegistryString(propertiesKey, WindowsDisplayNameRegistryProperties[3]);
            var displayName = customName ?? deviceDescription;
            if (displayName is not null && endpointName is not null)
            {
                return $"{displayName} ({endpointName})";
            }

            return displayName ?? endpointName ?? "Unknown device";
        }

        foreach (var propertyName in AudioAdapterNameRegistryProperties)
        {
            var value = GetRegistryString(propertiesKey, propertyName);
            if (value is not null)
            {
                return value;
            }
        }

        return "Unknown device";
    }

    private static string? GetRegistryString(RegistryKey propertiesKey, string propertyName)
    {
        return propertiesKey.GetValue(propertyName) is string value && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;
    }

    private static string BuildEndpointId(AudioDeviceKind kind, string endpointKeyName)
    {
        var prefix = kind == AudioDeviceKind.Output ? "{0.0.0.00000000}" : "{0.0.1.00000000}";
        return $"{prefix}.{endpointKeyName}";
    }

    private static string? GetDefaultDeviceId(AudioDeviceTarget target)
    {
        var dataFlow = ToDataFlow(target.Kind);
        IMMDeviceEnumerator? enumerator = null;

        try
        {
            enumerator = CreateDeviceEnumerator();
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(dataFlow, ToERole(target.Role), out var device));

            try
            {
                Marshal.ThrowExceptionForHR(device.GetId(out var id));
                return id;
            }
            finally
            {
                ReleaseComObject(device);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException)
        {
            return GetDefaultDeviceIdFromMediaDevice(target);
        }
        finally
        {
            if (enumerator is not null)
            {
                ReleaseComObject(enumerator);
            }
        }
    }

    private static string? GetDefaultDeviceIdFromMediaDevice(AudioDeviceTarget target)
    {
        try
        {
            var role = target.Role == AudioEndpointRole.Communications
                ? AudioDeviceRole.Communications
                : AudioDeviceRole.Default;
            var id = target.Kind == AudioDeviceKind.Output
                ? MediaDevice.GetDefaultAudioRenderId(role)
                : MediaDevice.GetDefaultAudioCaptureId(role);

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
            ReleaseComObject(store);
        }
    }

    private static string GetDeviceName(IPropertyStore store)
    {
        var propertyKeys = DeviceNameFormat == AudioDeviceNameFormat.AudioAdapter
            ? PropertyKeys.AudioAdapterNames
            : PropertyKeys.WindowsDisplayNames;
        foreach (var propertyKey in propertyKeys)
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

    private static ERole ToERole(AudioEndpointRole role)
    {
        return role == AudioEndpointRole.Communications ? ERole.Communications : ERole.Console;
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

    [LibraryImport("ole32.dll")]
    private static partial int PropVariantClear(ref PropVariant pvar);

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(
        in Guid classId,
        nint outer,
        uint context,
        in Guid interfaceId,
        out nint instance);

    private static IMMDeviceEnumerator CreateDeviceEnumerator()
    {
        return ActivateComObject<IMMDeviceEnumerator>(
            MMDeviceEnumeratorClassId,
            MMDeviceEnumeratorInterfaceId);
    }

    private static T ActivateComObject<T>(in Guid classId, in Guid interfaceId)
        where T : class
    {
        Marshal.ThrowExceptionForHR(CoCreateInstance(
            in classId,
            0,
            ClsctxInprocServer,
            in interfaceId,
            out var instance));

        try
        {
            return (T)ComWrappers.GetOrCreateObjectForComInstance(
                instance,
                CreateObjectFlags.UniqueInstance);
        }
        finally
        {
            Marshal.Release(instance);
        }
    }

    private static void ReleaseComObject(object instance)
    {
        if (instance is ComObject comObject)
        {
            comObject.FinalRelease();
        }
    }

    [GeneratedComClass]
    internal sealed partial class AudioDeviceWatcher : IMMNotificationClient, IDisposable
    {
        private readonly Action _onChanged;
        private readonly IMMDeviceEnumerator _enumerator;
        private bool _disposed;

        public AudioDeviceWatcher(Action onChanged)
        {
            _onChanged = onChanged;
            _enumerator = CreateDeviceEnumerator();
            try
            {
                Marshal.ThrowExceptionForHR(_enumerator.RegisterEndpointNotificationCallback(this));
            }
            catch
            {
                ReleaseComObject(_enumerator);
                throw;
            }
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
            if (PropertyKeys.IsDeviceName(key))
            {
                try
                {
                    _onChanged();
                }
                catch
                {
                    // Exceptions must never escape a native Core Audio callback.
                }
            }

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
                ReleaseComObject(_enumerator);
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
                try
                {
                    disposable.Dispose();
                }
                catch
                {
                    // Continue unregistering the remaining notification sources.
                }
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
        public static readonly PropertyKey[] WindowsDisplayNames =
        [
            Create("a45c254e-df1c-4efd-8020-67d146a850e0", 14),
            Create("026e516e-b814-414b-83cd-856d6fef4822", 2),
            Create("b3f8fa53-0004-438e-9003-51a46e139bfc", 6),
            Create("a45c254e-df1c-4efd-8020-67d146a850e0", 2),
        ];
        public static readonly PropertyKey[] AudioAdapterNames =
        [
            Create("026e516e-b814-414b-83cd-856d6fef4822", 2),
            Create("b3f8fa53-0004-438e-9003-51a46e139bfc", 6),
            Create("a45c254e-df1c-4efd-8020-67d146a850e0", 14),
            Create("a45c254e-df1c-4efd-8020-67d146a850e0", 2),
        ];

        public static bool IsDeviceName(PropertyKey key)
        {
            foreach (var candidate in WindowsDisplayNames)
            {
                if (candidate.FormatId == key.FormatId && candidate.PropertyId == key.PropertyId)
                {
                    return true;
                }
            }

            return false;
        }

        private static PropertyKey Create(string formatId, uint propertyId)
        {
            return new PropertyKey
            {
                FormatId = new Guid(formatId),
                PropertyId = propertyId,
            };
        }
    }

    internal enum EDataFlow
    {
        Render,
        Capture,
        All,
    }

    internal enum ERole
    {
        Console,
        Multimedia,
        Communications,
    }

    internal enum StorageAccessMode
    {
        Read,
        Write,
        ReadWrite,
    }

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal partial interface IMMDeviceEnumerator
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

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("0BD7A1BE-7A1A-44DB-8397-C0DD2D112A8D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal partial interface IMMDeviceCollection
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int Item(uint deviceNumber, out IMMDevice device);
    }

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal partial interface IMMNotificationClient
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

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal partial interface IMMDevice
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

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal partial interface IPolicyConfig
    {
        [PreserveSig]
        int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IntPtr format);

        [PreserveSig]
        int GetDeviceFormat(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
            [MarshalAs(UnmanagedType.Bool)] bool defaultFormat,
            out IntPtr format);

        [PreserveSig]
        int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

        [PreserveSig]
        int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr endpointFormat, IntPtr mixFormat);

        [PreserveSig]
        int GetProcessingPeriod(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
            [MarshalAs(UnmanagedType.Bool)] bool defaultPeriod,
            out long defaultPeriodValue,
            out long minimumPeriodValue);

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
        int SetEndpointVisibility(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
            [MarshalAs(UnmanagedType.Bool)] bool visible);
    }

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal partial interface IPropertyStore
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
    internal struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct PropVariant
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

internal sealed record AudioStatusSnapshot(
    string? OutputDeviceId,
    string OutputDeviceName,
    string? CommunicationsOutputDeviceId,
    string CommunicationsOutputDeviceName,
    string? InputDeviceId,
    string InputDeviceName,
    string? CommunicationsInputDeviceId,
    string CommunicationsInputDeviceName,
    DateTimeOffset UpdatedAt)
{
    public bool HasSameDevices(AudioStatusSnapshot other)
    {
        return string.Equals(OutputDeviceId, other.OutputDeviceId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(CommunicationsOutputDeviceId, other.CommunicationsOutputDeviceId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(InputDeviceId, other.InputDeviceId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(CommunicationsInputDeviceId, other.CommunicationsInputDeviceId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(OutputDeviceName, other.OutputDeviceName, StringComparison.Ordinal)
            && string.Equals(CommunicationsOutputDeviceName, other.CommunicationsOutputDeviceName, StringComparison.Ordinal)
            && string.Equals(InputDeviceName, other.InputDeviceName, StringComparison.Ordinal)
            && string.Equals(CommunicationsInputDeviceName, other.CommunicationsInputDeviceName, StringComparison.Ordinal);
    }

    public bool IsReliable()
    {
        return !string.IsNullOrWhiteSpace(OutputDeviceId)
            && !string.IsNullOrWhiteSpace(CommunicationsOutputDeviceId)
            && !string.IsNullOrWhiteSpace(InputDeviceId)
            && !string.IsNullOrWhiteSpace(CommunicationsInputDeviceId)
            && OutputDeviceName != "Unavailable"
            && CommunicationsOutputDeviceName != "Unavailable"
            && InputDeviceName != "Unavailable"
            && CommunicationsInputDeviceName != "Unavailable";
    }
}

internal enum AudioDeviceKind
{
    Output,
    Input,
}

internal enum AudioEndpointRole
{
    Default,
    Communications,
    Both,
}

internal readonly record struct AudioDeviceTarget(AudioDeviceKind Kind, AudioEndpointRole Role)
{
    public static readonly AudioDeviceTarget Output = new(AudioDeviceKind.Output, AudioEndpointRole.Default);
    public static readonly AudioDeviceTarget CommunicationsOutput = new(AudioDeviceKind.Output, AudioEndpointRole.Communications);
    public static readonly AudioDeviceTarget Input = new(AudioDeviceKind.Input, AudioEndpointRole.Default);
    public static readonly AudioDeviceTarget CommunicationsInput = new(AudioDeviceKind.Input, AudioEndpointRole.Communications);
    public static readonly AudioDeviceTarget CombinedOutput = new(AudioDeviceKind.Output, AudioEndpointRole.Both);
    public static readonly AudioDeviceTarget CombinedInput = new(AudioDeviceKind.Input, AudioEndpointRole.Both);
}

internal enum AudioDeviceNameFormat
{
    WindowsDisplayName,
    AudioAdapter,
}

internal sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault)
{
    public bool IsMediaDefault { get; init; }

    public bool IsCommunicationsDefault { get; init; }
}
