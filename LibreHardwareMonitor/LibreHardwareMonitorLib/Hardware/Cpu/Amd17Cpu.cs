// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
// Copyright (C) LibreHardwareMonitor and Contributors.
// All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using LibreHardwareMonitor.PawnIo;

namespace LibreHardwareMonitor.Hardware.Cpu;

internal sealed class Amd17Cpu : AmdCpu
{
    private readonly Processor _processor;
    private readonly Dictionary<SensorType, int> _sensorTypeIndex;
    private readonly RyzenSMU _smu;
    private readonly AmdFamily17 _pawnModule;

    private bool IsZen5 => _family == 0x1a;

    public Amd17Cpu(int processorIndex, CpuId[][] cpuId, ISettings settings) : base(processorIndex, cpuId, settings)
    {
        _pawnModule = new AmdFamily17();

        _sensorTypeIndex = new Dictionary<SensorType, int>();
        foreach (SensorType type in Enum.GetValues(typeof(SensorType)))
            _sensorTypeIndex.Add(type, 0);

        _sensorTypeIndex[SensorType.Load] = _active.Count(x => x.SensorType == SensorType.Load);

        _smu = new RyzenSMU();

        _processor = new Processor(this);

        int coreId = 0;
        int lastCoreId = -1;

        foreach (CpuId[] cpu in cpuId.OrderBy(x => x[0].ExtData[0x1e, 1] & 0xFF))
        {
            CpuId thread = cpu[0];
            int coreIdRead = (int)(thread.ExtData[0x1e, 1] & 0xff);
            int nodeId = (int)(thread.ExtData[0x1e, 2] & 0xff);

            if (coreIdRead != lastCoreId)
                coreId++;

            lastCoreId = coreIdRead;
            _processor.AppendThread(thread, nodeId, coreId);
        }

        Update();
    }

    public override string GetReport()
    {
        StringBuilder r = new();
        r.Append(base.GetReport());
        r.Append(_smu.GetReport());
        return r.ToString();
    }

    public override void Close()
    {
        base.Close();
        _pawnModule.Close();
        _smu.Close();
    }

    public override void Update()
    {
        _processor.ClearSamples();
        base.Update();
        _processor.UpdateSensors();
    }

    private class Processor
    {
        private readonly Sensor _coreTemperatureTctlTdie;
        private readonly Amd17Cpu _cpu;
        private readonly Sensor _packagePower;
        private DateTime _lastSampleTime = new(0);
        private uint _lastPwrValue;

        public Processor(Hardware hardware)
        {
            _cpu = (Amd17Cpu)hardware;

            _packagePower = new Sensor("Package", _cpu._sensorTypeIndex[SensorType.Power]++, SensorType.Power, _cpu, _cpu._settings);
            _coreTemperatureTctlTdie = new Sensor("Core (Tctl/Tdie)", _cpu._sensorTypeIndex[SensorType.Temperature]++, SensorType.Temperature, _cpu, _cpu._settings);

            _cpu.ActivateSensor(_packagePower);
        }

        public List<NumaNode> Nodes { get; } = new();

        public void ClearSamples()
        {
            _coreTemperatureTctlTdie.Value = null;
            _packagePower.Value = null;
        }

        public void UpdateSensors()
        {
            ClearSamples();
            CpuId cpuId = Nodes.FirstOrDefault()?.Cores.FirstOrDefault()?.Threads.FirstOrDefault();
            if (cpuId == null) return;
            GroupAffinity previousAffinity = ThreadAffinity.Set(cpuId.Affinity);
            try
            {
                // A failed MSR read only invalidates power; still attempt temperature.
                if (_cpu._pawnModule.ReadMsr(MSR_PWR_UNIT, out uint units, out _) &&
                    _cpu._pawnModule.ReadMsr(MSR_PKG_ENERGY_STAT, out uint totalEnergy, out _))
                {
                    DateTime sampleTime = DateTime.UtcNow;
                    double delta = (sampleTime - _lastSampleTime).TotalSeconds;
                    if (_lastSampleTime.Ticks != 0 && delta > 0)
                    {
                        double energy = Math.Pow(0.5, (units >> 8) & 0x1F) * unchecked(totalEnergy - _lastPwrValue) / delta;
                        if (!double.IsNaN(energy) && !double.IsInfinity(energy)) _packagePower.Value = (float)energy;
                    }
                    _lastSampleTime = sampleTime; _lastPwrValue = totalEnergy;
                }
                else _lastSampleTime = new DateTime(0);

                if (!Mutexes.WaitPciBus(10)) return;
                try
                {
                    uint raw = _cpu._pawnModule.ReadSmn(F17H_M01H_THM_TCON_CUR_TMP);
                    // A disconnected/failed SMN read must not become a fresh zero-degree sample.
                    if (raw == 0 || raw == uint.MaxValue) return;
                    bool offsetFlag = (raw & F17H_TEMP_RANGE_SEL_MASK) != 0 ||
                        (raw & F17H_TEMP_TJ_SEL_MASK) == F17H_TEMP_TJ_SEL_MASK;
                    float value = (raw >> 21) * 0.125f - (offsetFlag ? 49 : 0);
                    // Preserve the existing model exclusions: their Tctl offsets have not
                    // been validated by this provider. Failure cleanup must not publish
                    // an uncompensated value as a newly supported Tctl/Tdie sample.
                    string name = cpuId.Name ?? string.Empty;
                    if (name.Contains("1600X") || name.Contains("1700X") || name.Contains("1800X") ||
                        name.Contains("Threadripper 19") || name.Contains("Threadripper 29") || name.Contains("2700X")) return;
                    _coreTemperatureTctlTdie.Value = value;
                    _cpu.ActivateSensor(_coreTemperatureTctlTdie);
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
                finally { Mutexes.ReleasePciBus(); }
            }
            finally { ThreadAffinity.Set(previousAffinity); }
        }

        public void AppendThread(CpuId thread, int numaId, int coreId)
        {
            NumaNode node = null;
            foreach (NumaNode n in Nodes)
            {
                if (n.NodeId == numaId)
                {
                    node = n;
                    break;
                }
            }

            if (node == null)
            {
                node = new NumaNode(_cpu, numaId);
                Nodes.Add(node);
            }

            if (thread != null)
                node.AppendThread(thread, coreId);
        }
    }

    private class NumaNode
    {
        private readonly Amd17Cpu _cpu;

        public NumaNode(Amd17Cpu cpu, int id)
        {
            Cores = new List<Core>();
            NodeId = id;
            _cpu = cpu;
        }

        public List<Core> Cores { get; }
        public int NodeId { get; }

        public void AppendThread(CpuId thread, int coreId)
        {
            Core core = null;
            foreach (Core c in Cores)
            {
                if (c.CoreId == coreId)
                    core = c;
            }

            if (core == null)
            {
                core = new Core(_cpu, coreId);
                Cores.Add(core);
            }

            if (thread != null)
                core.AppendThread(thread);
        }
    }

    private class Core
    {
        public Core(Amd17Cpu cpu, int id)
        {
            CoreId = id;
            _ = cpu; // 保留参数以维持 AppendThread 调用链（Processor.AppendThread 需要构造 Core）
        }

        public int CoreId { get; }

        public List<CpuId> Threads { get; } = new();

        public void AppendThread(CpuId cpuId)
        {
            if (cpuId != null)
                Threads.Add(cpuId);
        }
    }

    // ReSharper disable InconsistentNaming
    private const uint F17H_M01H_THM_TCON_CUR_TMP = 0x00059800;
    private const uint F17H_TEMP_RANGE_SEL_MASK = 0x80000;
    private const uint F17H_TEMP_TJ_SEL_MASK = 0x30000;
    private const uint MSR_PKG_ENERGY_STAT = 0xC001029B;
    private const uint MSR_PWR_UNIT = 0xC0010299;
    // ReSharper restore InconsistentNaming
}
