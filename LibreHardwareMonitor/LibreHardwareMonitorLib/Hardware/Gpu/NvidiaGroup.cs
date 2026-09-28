// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
// Copyright (C) LibreHardwareMonitor and Contributors.
// Partial Copyright (C) Michael Möller <mmoeller@openhardwaremonitor.org> and Contributors.
// All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibreHardwareMonitor.Interop;

namespace LibreHardwareMonitor.Hardware.Gpu;

internal class NvidiaGroup : IGroup, IHardwareChanged
{
    private readonly Dictionary<NvApi.NvPhysicalGpuHandle, NvidiaGpu> _hardware = new();
    private readonly ISettings _settings;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly TopologySchedule _schedule = new();
    private readonly object _scheduleGate = new();
    private readonly Task _monitorTask;
    private volatile bool _disposed;
    private int _rebuild, _resetNative;
    private int _notificationThread;
    private string _report = "NvAPI: waiting for topology";

    public NvidiaGroup(ISettings settings)
    {
        _settings = settings;
        NvidiaNative.Session.AddUser();
        try
        {
            Probe(false);
            _monitorTask = Task.Run(MonitorLoop);
        }
        catch
        {
            lock (NvidiaNative.Session.Gate)
            {
                try { RemoveAll(new List<NvidiaGpu>()); }
                finally { NvidiaNative.Session.RemoveUser(); }
            }
            _stop.Dispose(); _wake.Dispose();
            throw;
        }
    }
    public event HardwareEventHandler HardwareAdded;
    public event HardwareEventHandler HardwareRemoved;
    public IReadOnlyList<IHardware> Hardware
    {
        get { lock (NvidiaNative.Session.Gate) return _hardware.Values.Cast<IHardware>().ToArray(); }
    }
    public string GetReport() { lock (NvidiaNative.Session.Gate) return _report; }
    internal void RequestRefresh(bool resetNative = false)
    {
        lock (_scheduleGate)
        {
            if (_disposed) return;
            Interlocked.Exchange(ref _rebuild, 1);
            if (resetNative) Interlocked.Exchange(ref _resetNative, 1);
            _schedule.Request(_clock.ElapsedMilliseconds);
            if (_wake.CurrentCount == 0) _wake.Release();
        }
    }
    private async Task MonitorLoop()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                int delay;
                lock (_scheduleGate) delay = (int)Math.Max(0, _schedule.Due - _clock.ElapsedMilliseconds);
                if (delay > 0)
                {
                    await _wake.WaitAsync(delay, _stop.Token).ConfigureAwait(false);
                    continue;
                }
                if (!_disposed)
                {
                    try { Probe(true); }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(ex);
                        lock (_scheduleGate) _schedule.Completed(_clock.ElapsedMilliseconds, false);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }
    private void Probe(bool raiseEvents)
    {
        var removed = new List<NvidiaGpu>();
        var added = new List<NvidiaGpu>();
        bool success = false;
        lock (NvidiaNative.Session.Gate)
        {
            if (_disposed) return;
            try
            {
                bool rebuild = Interlocked.Exchange(ref _rebuild, 0) != 0;
                if (rebuild) RemoveAll(removed);
                if (Interlocked.Exchange(ref _resetNative, 0) != 0) NvidiaNative.Session.Reset();
                if (!Software.OperatingSystem.IsUnix && NvidiaNative.Session.EnsureInitialized())
                {
                    var handles = new NvApi.NvPhysicalGpuHandle[NvApi.MAX_PHYSICAL_GPUS];
                    NvApi.NvStatus status = NvApi.NvAPI_EnumPhysicalGPUs(handles, out int count);
                    success = status == NvApi.NvStatus.OK && count > 0;
                    _report = $"NvAPI: {status}; GPUs: {count}";
                    if (success)
                    {
                        var displays = GetDisplayHandles();
                        var current = new HashSet<NvApi.NvPhysicalGpuHandle>(handles.Take(count));
                        foreach (var pair in _hardware.ToArray())
                            if (!current.Contains(pair.Key) || pair.Value.SessionGeneration != NvidiaNative.Session.Generation)
                            {
                                pair.Value.Close(); removed.Add(pair.Value); _hardware.Remove(pair.Key);
                            }
                        for (int i = 0; i < count; i++)
                            if (!_hardware.ContainsKey(handles[i]))
                            {
                                displays.TryGetValue(handles[i], out var display);
                                var gpu = new NvidiaGpu(i, handles[i], display, _settings, () => RequestRefresh(true),
                                    () => _disposed || _stop.IsCancellationRequested);
                                _hardware.Add(handles[i], gpu); added.Add(gpu);
                            }
                    }
                    else if (status != NvApi.NvStatus.OK)
                    {
                        RemoveAll(removed);
                        NvidiaNative.Session.Reset();
                    }
                }
                if (!success) RemoveAll(removed);
            }
            catch (Exception ex)
            {
                success = false; RemoveAll(removed);
                _report = "NvAPI topology failed: " + ex.GetType().Name;
                try { NvidiaNative.Session.Reset(); } catch { }
            }
        }
        lock (_scheduleGate)
        {
            _schedule.Completed(_clock.ElapsedMilliseconds, success);
            if (Volatile.Read(ref _rebuild) != 0) _schedule.Request(_clock.ElapsedMilliseconds);
        }
        // No native/list lock while calling external handlers. Computer never waits for us under its list lock.
        if (raiseEvents && !_disposed)
        {
            _notificationThread = Thread.CurrentThread.ManagedThreadId;
            try
            {
                foreach (var gpu in removed)
                {
                    if (_disposed) break;
                    try { HardwareRemoved?.Invoke(gpu); } catch (Exception ex) { Debug.WriteLine(ex); }
                }
                foreach (var gpu in added)
                {
                    if (_disposed) break;
                    try { HardwareAdded?.Invoke(gpu); } catch (Exception ex) { Debug.WriteLine(ex); }
                }
            }
            finally { _notificationThread = 0; }
        }
    }
    private void RemoveAll(List<NvidiaGpu> removed)
    {
        foreach (var gpu in _hardware.Values) { gpu.Close(); removed.Add(gpu); }
        _hardware.Clear();
    }
    public void Close()
    {
        lock (_scheduleGate)
        {
            if (_disposed) return;
            _disposed = true; _stop.Cancel();
        }
        // Called by the application's background lifecycle. Never release a DLL still in use.
        // A subscriber may close this group from its own notification. That callback is already
        // outside native access, and the cancelled loop cannot start another probe after it returns.
        if (Thread.CurrentThread.ManagedThreadId != Volatile.Read(ref _notificationThread))
            _monitorTask?.GetAwaiter().GetResult();
        lock (NvidiaNative.Session.Gate)
        {
            RemoveAll(new List<NvidiaGpu>());
            NvidiaNative.Session.RemoveUser();
        }
        _stop.Dispose();
        _wake.Dispose();
    }

    internal void StopRefreshing()
    {
        lock (_scheduleGate)
        {
            if (!_disposed) _stop.Cancel();
        }
    }

    private static IDictionary<NvApi.NvPhysicalGpuHandle, NvApi.NvDisplayHandle> GetDisplayHandles()
    {
        Dictionary<NvApi.NvPhysicalGpuHandle, NvApi.NvDisplayHandle> displayHandles = [];

        if (NvApi.NvAPI_EnumNvidiaDisplayHandle == null || NvApi.NvAPI_GetPhysicalGPUsFromDisplay == null)
        {
            return displayHandles;
        }

        NvApi.NvStatus status = NvApi.NvStatus.OK;
        int i = 0;

        while (status == NvApi.NvStatus.OK)
        {
            NvApi.NvDisplayHandle displayHandle = new();
            status = NvApi.NvAPI_EnumNvidiaDisplayHandle(i, ref displayHandle);
            i++;

            if (status != NvApi.NvStatus.OK)
            {
                continue;
            }

            NvApi.NvPhysicalGpuHandle[] handlesFromDisplay = new NvApi.NvPhysicalGpuHandle[NvApi.MAX_PHYSICAL_GPUS];
            if (NvApi.NvAPI_GetPhysicalGPUsFromDisplay(displayHandle, handlesFromDisplay, out uint countFromDisplay) != NvApi.NvStatus.OK)
            {
                continue;
            }

            for (int j = 0; j < countFromDisplay; j++)
            {
                if (!displayHandles.ContainsKey(handlesFromDisplay[j]))
                {
                    displayHandles.Add(handlesFromDisplay[j], displayHandle);
                }
            }
        }

        return displayHandles;
    }

}
