using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using OmenSuperHub.Control;
using LibreHardwareMonitor.Hardware;
using static OmenSuperHub.OmenHardware;
using static OmenSuperHub.GpuAppManager;

namespace OmenSuperHub {
  static partial class Program {
    static readonly PollingLifetime monitorLifetime = new PollingLifetime();
    static readonly PollingLifetime optimiseLifetime = new PollingLifetime();
    static readonly RateGate infoRate = new RateGate(controlClock, 1000);
    static readonly RateGate fallbackRate = new RateGate(controlClock, 1000);
    static readonly object infoTaskGate = new object();
    static Task infoTask = Task.CompletedTask;
    static readonly LatestFileWriter fileWriter = new LatestFileWriter(
      (name, value) => File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name), value),
      ex => Logger.Error(ex.Message, "monitor.file"));
    static LatestPublisher<UiFrame> uiPublisher;
    static readonly object uiFrameGate = new object();
    static readonly PowerUpdates powerUpdates = new PowerUpdates();
    static UiFrame pendingFrame = new UiFrame(), renderedFrame = new UiFrame();
    static DeviceChangeWindow deviceChangeWindow;
    static Telemetry telemetry = Telemetry.Empty, displayedTelemetry = Telemetry.Empty;
    static bool computerOpened;
    static long sampleVersion, monitorRetryAt, monitorRetryVersion = -1;
    static int monitorFailures;
    static int sysInfoSession;
    static string cachedCpuModel, cachedGpuModel;
    static int cachedAdapterPower;
    static byte cachedGfxModes;
    static bool cachedLoadLineSupported;
    static int cachedLoadLineLevels;
    static ToolStripMenuItem gpuLimitsItem;
    private sealed class ExtraInfo {
      internal int Session;
      internal int[] Temperatures;
      internal float[] Limits;
    }
    private sealed class UiFrame {
      internal readonly Telemetry Data;
      internal readonly ControlNotice Control;
      internal readonly ExtraInfo Extra;
      internal readonly PowerChange Power;
      internal UiFrame(Telemetry data = null, ControlNotice control = null, ExtraInfo extra = null, PowerChange power = null) {
        Data = data; Control = control; Extra = extra; Power = power;
      }
    }
    private sealed class DeviceChangeWindow : NativeWindow, IDisposable {
      internal DeviceChangeWindow() { CreateHandle(new CreateParams { Caption = "OmenSuperHub device events" }); }
      protected override void WndProc(ref Message message) {
        if (message.Msg == 0x0219 && !_isExiting) libreComputer.RequestGpuRefresh();
        base.WndProc(ref message);
      }
      public void Dispose() { DestroyHandle(); }
    }
    static void InitializeBackgroundControl() {
      hardwareDispatcher = new HardwareDispatcher(new BiosWriter(), controlClock, Logger.Error);
      uiPublisher = new LatestPublisher<UiFrame>(a => uiContext.Post(_ => a(), null), frame => {
        bool resume;
        if (!_isExiting && frame.Power != null && powerUpdates.Consume(frame.Power, out resume)) ApplyPowerChange(resume);
        if (frame.Data != null && frame.Data != renderedFrame.Data) ApplyTelemetry(frame.Data);
        if (frame.Extra != null && frame.Extra != renderedFrame.Extra) ApplyExtraInfo(frame.Extra);
        if (frame.Control != null && frame.Control != renderedFrame.Control) ApplyControlNotice(frame.Control);
        renderedFrame = frame;
      });
      deviceChangeWindow = new DeviceChangeWindow();
    }
    static void PublishUi(Telemetry data = null, ControlNotice control = null, ExtraInfo extra = null, PowerChange power = null) {
      if (_isExiting) return;
      lock (uiFrameGate) {
        if (power != null && pendingFrame.Power != null && power.Version < pendingFrame.Power.Version) power = pendingFrame.Power;
        pendingFrame = new UiFrame(data ?? pendingFrame.Data, control ?? pendingFrame.Control, extra ?? pendingFrame.Extra, power ?? pendingFrame.Power);
        uiPublisher?.Publish(pendingFrame);
      }
    }
    static void UpdateTooltip() {
      if (_isExiting) return;
      monitorLifetime.TryRun(() => {
        if (_isExiting) return;
        try { QueryHardware(); }
        catch (Exception ex) { Logger.Error(ex.ToString(), "monitor.round"); }
      });
      RequestExtraInfo();
    }
    static bool Valid(float? value) { return value.HasValue && !float.IsNaN(value.Value) && !float.IsInfinity(value.Value); }
    static float? SensorValue(IHardware hw, SensorType type, Func<string, bool> name) {
      foreach (ISensor sensor in hw.Sensors)
        if (sensor.SensorType == type && name(sensor.Name) && Valid(sensor.Value)) return sensor.Value;
      return null;
    }
    static void QueryHardware() {
      RuntimeTarget target = Volatile.Read(ref runtimeTarget);
      if (target == null || _isExiting) return;
      Telemetry previous = Volatile.Read(ref telemetry);
      long now = controlClock.Milliseconds;
      var cpu = new Reading(null, now, previous.CpuTemperature);
      var gpu = new Reading(null, now, previous.GpuTemperature);
      var cpuPowerReading = new Reading(null, now, previous.CpuPower);
      var gpuPowerReading = new Reading(null, now, previous.GpuPower);
      var fan = new Reading(null, now, previous.FanRpm);
      int[] levels = null;
      bool sleeping = false;
      if (monitorRetryVersion != target.MonitorVersion) {
        monitorRetryVersion = target.MonitorVersion; monitorFailures = 0; monitorRetryAt = 0;
      }
      try {
        if (now >= monitorRetryAt) {
          if (!computerOpened) {
            libreComputer.IsCpuEnabled = target.Cpu; libreComputer.IsGpuEnabled = target.Gpu;
            if (_isExiting) return;
            libreComputer.Open(); computerOpened = true;
          }
          if (_isExiting) return;
          libreComputer.IsCpuEnabled = target.Cpu;
          if (_isExiting) return;
          libreComputer.IsGpuEnabled = target.Gpu;
          foreach (IHardware hw in libreComputer.Hardware) {
            if (_isExiting) return;
            bool isCpu = hw.HardwareType == HardwareType.Cpu && target.Cpu;
            bool isGpu = target.Gpu && (hw.HardwareType == HardwareType.GpuNvidia || !hasNVIDIAGpu && hw.HardwareType == HardwareType.GpuAmd);
            if (!isCpu && !isGpu) continue;
            try {
              hw.Update();
              now = controlClock.Milliseconds;
              if (isCpu) {
                cpu = new Reading(SensorValue(hw, SensorType.Temperature, name => name.Contains("Package") || name.Contains("Tctl/Tdie")), now, previous.CpuTemperature);
                float? power = SensorValue(hw, SensorType.Power, name => name.Contains("Package"));
                cpuPowerReading = new Reading(power >= 0 && power < 9999 ? power : null, now, previous.CpuPower);
              } else {
                sleeping = (hw as IGpuPowerState)?.PowerState == GpuPowerState.Sleeping;
                gpu = new Reading(SensorValue(hw, SensorType.Temperature, name => name == "GPU Core"), now, previous.GpuTemperature);
                float? power = SensorValue(hw, SensorType.Power, name => name == "GPU Package");
                gpuPowerReading = new Reading(power >= 0 && power < 9999 ? power : null, now, previous.GpuPower);
              }
            } catch (Exception ex) { Logger.Error(ex.ToString(), isCpu ? "monitor.cpu" : "monitor.gpu"); }
          }
          monitorFailures = 0;
        }
      } catch (Exception ex) {
        monitorRetryAt = controlClock.Milliseconds + Math.Min(30000, 1000 << Math.Min(5, monitorFailures++));
        Logger.Error(ex.ToString(), "monitor.lifecycle");
      }
      if (_isExiting) return;
      if (target.Cpu && !cpu.Value.HasValue && isAmbientSensorSupported) {
        if (!hardwareDispatcher.HasUrgentWork && fallbackRate.TryEnter()) {
          try {
            bool estimated;
            float? temperature = SensorRules.CpuTemperature(null, true,
              () => GetSensorTemperature(1, () => !_isExiting && target.MonitorVersion == Volatile.Read(ref runtimeTarget).MonitorVersion &&
                !hardwareDispatcher.HasUrgentWork), out estimated);
            cpu = new Reading(temperature, controlClock.Milliseconds, previous.CpuTemperature, estimated);
          } finally { fallbackRate.Leave(); }
        } else if (previous.MonitorVersion == target.MonitorVersion && previous.CpuTemperature.Estimated &&
          previous.CpuTemperature.Fresh(controlClock.Milliseconds)) cpu = previous.CpuTemperature;
      }
      if (_isExiting) return;
      bool same = previous.MonitorVersion == target.MonitorVersion;
      // 风扇转速和温度采样保持同一轮直接读取。控制命令与读取由 BIOS/WMI
      // 串行屏障协调，监控层不再因控制队列繁忙而暂缓或复用旧转速。
      if (target.Fan && GetFanLevel(out levels,
        () => !_isExiting && target.MonitorVersion == Volatile.Read(ref runtimeTarget).MonitorVersion))
        fan = new Reading((levels[0] + levels[1]) * 50, controlClock.Milliseconds, previous.FanRpm);
      float? cpuSmooth = same && cpu == previous.CpuTemperature ? previous.CpuSmoothed : Smooth(cpu.Value, same ? previous.CpuSmoothed : null, target.Smoothing);
      float? gpuSmooth = Smooth(gpu.Value, same ? previous.GpuSmoothed : null, target.Smoothing);
      var result = new Telemetry(++sampleVersion, target.MonitorVersion, cpu, cpuPowerReading, gpu, gpuPowerReading, fan, cpuSmooth, gpuSmooth, levels,
        sleeping, computerOpened && libreComputer.IsCpuEnabled, computerOpened && libreComputer.IsGpuEnabled);
      if (_isExiting || target.MonitorVersion != Volatile.Read(ref runtimeTarget).MonitorVersion) return;
      Volatile.Write(ref telemetry, result);
      PublishUi(data: result);
    }
    static float? Smooth(float? value, float? previous, float factor) {
      return value.HasValue ? value.Value * factor + (previous ?? value.Value) * (1 - factor) : (float?)null;
    }
    static void ApplyTelemetry(Telemetry data) {
      if (_isExiting || runtimeTarget == null || data.MonitorVersion != runtimeTarget.MonitorVersion) return;
      displayedTelemetry = data;
      long now = controlClock.Milliseconds;
      cpuTempReady = monitorCPU && data.CpuTemperature.Fresh(now);
      gpuTempReady = monitorGPU && data.GpuTemperature.Fresh(now);
      if (cpuTempReady) CPUTemp = tempDisplayMode == "raw" ? data.CpuTemperature.Value.Value : data.CpuSmoothed.Value;
      if (gpuTempReady) GPUTemp = tempDisplayMode == "raw" ? data.GpuTemperature.Value.Value : data.GpuSmoothed.Value;
      CPUPower = data.CpuPower.Fresh(now) ? data.CpuPower.Value.Value : float.NaN;
      GPUPower = data.GpuPower.Fresh(now) ? data.GpuPower.Value.Value : float.NaN;
      UpdateTrayIconText(); UpdateFloatingText();
      if (customIcon == "dynamic") UpdateDynamicIcon();
      SyncDataToTxt();
    }
    static void SyncDataToTxt() {
      if (_isExiting || dataLocalize != "on") return;
      Telemetry data = displayedTelemetry;
      fileWriter.Publish(cpuTempReady ? Math.Round(CPUTemp).ToString() : "--",
        gpuTempReady ? Math.Round(GPUTemp).ToString() : "--",
        monitorFan && data.FanRpm.Fresh(controlClock.Milliseconds) ? data.FanRpm.Value.Value.ToString() : "--");
    }
    static void RequestExtraInfo() {
      lock (infoTaskGate) {
        if (_isExiting || !isSysInfoMenuOpen || !infoTask.IsCompleted || !infoRate.TryEnter()) return;
        int session = Volatile.Read(ref sysInfoSession);
        infoTask = Task.Run(() => {
          try {
            Func<bool> current = () => !_isExiting && isSysInfoMenuOpen && session == Volatile.Read(ref sysInfoSession) && !hardwareDispatcher.HasUrgentWork;
            var temperatures = new int[4];
            for (byte i = 0; i < 4; i++) {
              if (!current()) return;
              temperatures[i] = GetSensorTemperature(i, current);
            }
            if (!current()) return;
            float[] limits = hasNVIDIAGpu ? GetGpuPowerLimits() : null;
            if (current()) PublishUi(extra: new ExtraInfo { Session = session, Temperatures = temperatures, Limits = limits });
          } catch (Exception ex) { Logger.Error(ex.ToString(), "monitor.system-info"); }
          finally { infoRate.Leave(); }
        });
      }
    }
    static void ApplyExtraInfo(ExtraInfo data) {
      if (_isExiting || !isSysInfoMenuOpen || data.Session != sysInfoSession) return;
      irSensorMenu.Text = Strings.SysIRSensor + ": " + FormatSensorTemperature(data.Temperatures[0]);
      ambientSensorMenu.Text = Strings.SysAmbient + ": " + FormatSensorTemperature(data.Temperatures[1]);
      pchSensorMenu.Text = Strings.SysPCH + ": " + FormatSensorTemperature(data.Temperatures[2]);
      vrSensorMenu.Text = Strings.SysVR + ": " + FormatSensorTemperature(data.Temperatures[3]);
      if (gpuLimitsItem != null && data.Limits != null)
        gpuLimitsItem.Text = Strings.SysNvidiaPower + ": " + (data.Limits[0] < 0 ? "--W / --W" : $"{data.Limits[0]:F0}W / {data.Limits[1]:F0}W");
    }
    static void OnDisplayChange(object sender, EventArgs args) { if (!_isExiting) libreComputer.RequestGpuRefresh(); }
  }
}
