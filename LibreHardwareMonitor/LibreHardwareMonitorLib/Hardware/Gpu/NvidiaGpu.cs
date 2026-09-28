// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// Copyright (C) LibreHardwareMonitor and Contributors.
using System;
using System.Diagnostics;
using System.Globalization;
using LibreHardwareMonitor.Interop;

namespace LibreHardwareMonitor.Hardware.Gpu;

internal sealed class NvidiaGpu : GenericGpu, IGpuPowerState
{
    private readonly NvApi.NvPhysicalGpuHandle _handle;
    private readonly int _adapterIndex;
    private NvidiaML.NvmlDevice? _nvmlDevice;
    private readonly Sensor _powerUsage, _temperature;
    private readonly Action _refresh;
    private readonly Func<bool> _closing;
    private readonly NvidiaSampleSession _samples = new();
    private bool _closed;
    private long _nextNvmlAttempt;
    internal long SessionGeneration { get; }
    public GpuPowerState PowerState { get; private set; } = GpuPowerState.Unknown;

    // Construction is under the native session gate; even GetName must use the current session.
    public NvidiaGpu(int adapterIndex, NvApi.NvPhysicalGpuHandle handle, NvApi.NvDisplayHandle? displayHandle,
        ISettings settings, Action refresh, Func<bool> closing)
        : base(GetName(handle), new Identifier("gpu-nvidia", adapterIndex.ToString(CultureInfo.InvariantCulture)), settings)
    {
        _handle = handle; _adapterIndex = adapterIndex; _refresh = refresh; _closing = closing;
        SessionGeneration = NvidiaNative.Session.Generation;
        _temperature = new Sensor("GPU Core", 0, SensorType.Temperature, this, settings);
        _powerUsage = new Sensor("GPU Package", 0, SensorType.Power, this, settings);
        ActivateSensor(_temperature); ActivateSensor(_powerUsage);
        // No eager Update/NVML initialization: first sampling obeys the same power policy as later samples.
    }
    public override string DeviceId => null;
    public override HardwareType HardwareType => HardwareType.GpuNvidia;

    public override void Update()
    {
        bool refresh;
        lock (NvidiaNative.Session.Gate)
        {
            _temperature.Value = null; _powerUsage.Value = null;
            if (_closed || _closing()) return;
            var sample = _samples.Read(
                () => SessionGeneration == NvidiaNative.Session.Generation ? ReadState() : (int)NvApi.NvStatus.HandleInvalidated,
                ReadTemperature, ReadPower);
            PowerState = (GpuPowerState)(int)sample.State;
            _temperature.Value = sample.Temperature;
            _powerUsage.Value = sample.Power;
            refresh = sample.Refresh;
        }
        if (refresh) _refresh();
    }
    private int ReadState()
    {
        var state = new NvApi.NvDynamicPStatesInfo
        {
            Version = (uint)NvApi.MAKE_NVAPI_VERSION<NvApi.NvDynamicPStatesInfo>(1),
            Utilizations = new NvApi.NvDynamicPState[NvApi.MAX_GPU_UTILIZATIONS]
        };
        return (int)(NvApi.NvAPI_GPU_GetDynamicPstatesInfoEx?.Invoke(_handle, ref state) ?? NvApi.NvStatus.FunctionNotFound);
    }
    private NvidiaReading ReadTemperature()
    {
        var thermal = new NvApi.NvThermalSettings
        {
            Version = (uint)NvApi.MAKE_NVAPI_VERSION<NvApi.NvThermalSettings>(2),
            Count = NvApi.MAX_THERMAL_SENSORS_PER_GPU
        };
        var status = NvApi.NvAPI_GPU_GetThermalSettings(_handle, (int)NvApi.NvThermalTarget.All, ref thermal);
        if (status == NvApi.NvStatus.OK && thermal.Count > 0 && thermal.Sensor != null)
            return new NvidiaReading(thermal.Sensor[0].CurrentTemp);
        return new NvidiaReading(null, NvidiaSamplePolicy.NeedsRefresh(NvidiaSamplePolicy.Classify((int)status)));
    }
    private NvidiaReading ReadPower()
    {
        long now = Stopwatch.GetTimestamp();
        if (!_nvmlDevice.HasValue && now >= _nextNvmlAttempt)
        {
            _nextNvmlAttempt = now + 30 * Stopwatch.Frequency;
            if (NvidiaML.IsAvailable || NvidiaML.Initialize())
            {
                if (NvApi.NvAPI_GPU_GetBusId != null && NvApi.NvAPI_GPU_GetBusId(_handle, out uint bus) == NvApi.NvStatus.OK)
                    _nvmlDevice = NvidiaML.NvmlDeviceGetHandleByPciBusId($"0000:{bus:X2}:00.0");
                if (!_nvmlDevice.HasValue) _nvmlDevice = NvidiaML.NvmlDeviceGetHandleByIndex(_adapterIndex);
            }
        }
        if (!_nvmlDevice.HasValue) return new NvidiaReading(null);
        var status = NvidiaML.TryGetPowerUsage(_nvmlDevice.Value, out int power);
        if (status == NvidiaML.NvmlReturn.Success) return new NvidiaReading(power / 1000f);
        bool refresh = status == NvidiaML.NvmlReturn.GpuIsLost || status == NvidiaML.NvmlReturn.DriverNotLoaded ||
            status == NvidiaML.NvmlReturn.Uninitialized || status == NvidiaML.NvmlReturn.ResetRequired;
        return new NvidiaReading(null, refresh);
    }
    public override void Close()
    {
        lock (NvidiaNative.Session.Gate)
        {
            if (_closed) return;
            _closed = true; _temperature.Value = null; _powerUsage.Value = null;
            base.Close();
        }
    }
    private static string GetName(NvApi.NvPhysicalGpuHandle handle)
    {
        if (NvApi.NvAPI_GPU_GetFullName(handle, out string name) != NvApi.NvStatus.OK) return "NVIDIA";
        name = name.Trim();
        return name.StartsWith("NVIDIA", StringComparison.OrdinalIgnoreCase) ? name : "NVIDIA " + name;
    }
}
