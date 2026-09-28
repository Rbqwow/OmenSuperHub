using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using OmenSuperHub.Control;

namespace OmenSuperHub {
  static partial class Program {
    static readonly IClock controlClock = new MonotonicClock();
    static readonly UpdateScope uiUpdates = new UpdateScope();
    static readonly object fanDecisionGate = new object();
    static readonly FanController fanController = new FanController(controlClock);
    static HardwareDispatcher hardwareDispatcher;
    static RuntimeTarget runtimeTarget;
    static long settingsRevision, fanGeneration, monitorVersion;
    static string previousFanMode, cleaningMode;
    static bool previousCpu, previousGpu, previousFanMonitor, lastProtectionNotice;
    private sealed class RuntimeTarget {
      internal readonly long Revision, FanGeneration, MonitorVersion;
      internal readonly string FanMode, Cleaning;
      internal readonly bool Cpu, Gpu, Fan, Protect;
      internal readonly float Smoothing;
      internal readonly FanCurve CpuCurve, GpuCurve;
      internal readonly int? FixedRpm;
      internal RuntimeTarget() {
        Revision = settingsRevision; FanGeneration = fanGeneration; MonitorVersion = monitorVersion;
        FanMode = fanControl; Cleaning = cleaningMode;
        Cpu = monitorCPU; Gpu = monitorGPU; Fan = monitorFan; Protect = autoFanProtect == "on";
        Smoothing = respondSpeed;
        CpuCurve = new FanCurve(CPUTempFanMap); GpuCurve = new FanCurve(GPUTempFanMap);
        FixedRpm = fanControl.EndsWith(" RPM", StringComparison.Ordinal) ? Number(fanControl, " RPM", 0, 20000) : null;
      }
    }
    private sealed class ControlNotice {
      internal RuntimeTarget Target;
      internal bool Protecting, HighTemperature, CanReportRecovery;
      internal float Temperature;
    }
    private sealed class BiosWriter : IHardwareWriter {
      public bool Execute(HardwareCommand command) {
        OmenHardware.CommandGuard = command.StillCurrent;
        try { return ExecuteCore(command); }
        finally { OmenHardware.CommandGuard = null; }
      }
      private bool ExecuteCore(HardwareCommand command) {
        var values = command.Value.Split(',');
        int value;
        int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        switch (command.Kind) {
          case CommandKind.Performance: return OmenHardware.SetUnleashMode();
          case CommandKind.LegacyClean: return OmenHardware.SetLegacyCleanCreek(value == 1);
          case CommandKind.FanMax: return value == 1 ? OmenHardware.SetMaxFanSpeedOn() : OmenHardware.SetMaxFanSpeedOff();
          case CommandKind.FanSpeed: return OmenHardware.SetFanLevel(value, int.Parse(values[1]), values[2] == "1", values[3] == "1");
          case CommandKind.CpuPower: return OmenHardware.SetCpuPowerLimit((byte)value);
          case CommandKind.GpuPower: return OmenHardware.SetGpuPowerState(value == 1, values[1] == "1", int.Parse(values[2]));
          case CommandKind.IccMax: return OmenHardware.SetIccMaxByWmi(value);
          case CommandKind.LoadLine: return OmenHardware.SetLoadLine(value);
          case CommandKind.Tpp: return OmenHardware.SetConcurrentTdp((byte)value);
          default: return false;
        }
      }
    }
    static int? Number(string text, string unit, int minimum, int maximum) {
      return SettingValues.Number(text, unit, minimum, maximum);
    }
    static string ManagedNumber(string text, string unit, int minimum, int maximum) {
      return Number(text, unit, minimum, maximum)?.ToString(CultureInfo.InvariantCulture);
    }
    static void EnsureAutomaticMonitor() {
      if (!SensorRules.NeedsCpuMonitor(fanControl, monitorCPU, monitorGPU)) return;
      monitorCPU = true; SaveConfig("MonitorCPU");
      UpdateCheckedState("monitorCPUGroup", Strings.MonitorCpuOn);
    }
    // All user edits, presets and replay requests arrive on the UI thread.
    static void CommitSettings(CommandKind? debounce = null) {
      if (_isExiting || uiUpdates.Active || hardwareDispatcher == null) return;
      EnsureAutomaticMonitor();
      string mode = fanControl.EndsWith(" RPM", StringComparison.Ordinal) ? "fixed" : fanControl;
      if (previousFanMode != mode) { previousFanMode = mode; fanGeneration++; }
      if (previousCpu != monitorCPU || previousGpu != monitorGPU || previousFanMonitor != monitorFan) {
        previousCpu = monitorCPU; previousGpu = monitorGPU; previousFanMonitor = monitorFan; monitorVersion++;
      }
      settingsRevision++;
      var target = new RuntimeTarget();
      hardwareDispatcher.Submit(new SettingsTarget(target.Revision, target.FanGeneration,
        isCPUPowerControlSupported ? ManagedNumber(cpuPower, " W", 5, 254) : null,
        (tgpPower == "on" ? "1" : "0") + "," + (ppabPower == "on" ? "1" : "0") + "," + (dState == "normal" ? "1" : "2"),
        ManagedNumber(iccMax, " A", 150, 350), ManagedNumber(acLoadline, "", 1, 255),
        ManagedNumber(tppPower, " W", 20, 254)), debounce, waitForFan: true);
      Volatile.Write(ref runtimeTarget, target);
      EvaluateFan(debounce == CommandKind.FanSpeed);
    }
    static void UserSettingChanged(string name, CommandKind? debounce = null) {
      if (uiUpdates.Active || _isExiting) return;
      SaveConfig(name); CommitSettings(debounce);
    }
    static void RequestPresetHardwareApply(string presetKey) { CommitSettings(); }
    static void RestoreCPUPower() { hardwareDispatcher?.Invalidate(); CommitSettings(); }
    static void RestorePowerConfig() { hardwareDispatcher?.Invalidate(); CommitSettings(); }
    static void RestoreFanControl() { hardwareDispatcher?.Invalidate(true); CommitSettings(); }
    static void SetCleaning(string mode) { cleaningMode = mode; CommitSettings(); }
    static void RenderOtherSettings() {
      using (uiUpdates.Enter()) {
        var item = FindMenuItemByName(trayIcon.ContextMenuStrip.Items, currentPreset);
        if (item != null) UpdateCheckedState("presetsGroup", null, item);
        UpdateCheckedState("autoStartGroup", autoStart == "on" ? Strings.Enable : Strings.Disable);
        UpdateCheckedState("customIconGroup", customIcon == "dynamic" ? Strings.IconDynamic : customIcon == "custom" ? Strings.IconCustom : Strings.IconOriginal);
        UpdateCheckedState("omenKeyGroup", GetOmenKeyActionMenuText(omenKey));
        UpdateCheckedState("floatingBarGroup", floatingBar == "on" ? Strings.FloatingShow : Strings.FloatingHide);
        UpdateCheckedState("floatingBarLocGroup", floatingBarLoc == "left" ? Strings.FloatingLocLeft : Strings.FloatingLocRight);
        UpdateCheckedState("dataLocalizeGroup", dataLocalize == "on" ? Strings.Enable : Strings.Disable);
        UpdateCheckedState("autoFanProtectGroup", autoFanProtect == "on" ? Strings.FanAutoProtectOn : Strings.FanAutoProtectOff);
        if (textSizeTrackBar != null) textSizeTrackBar.Value = Math.Max(textSizeTrackBar.Minimum, Math.Min(textSizeTrackBar.Maximum, textSize / 4));
        UpdateMonitorMetricCheckedStates(); RestoreLanguageChecked();
        RefreshSliderLabels();
      }
      if (floatingBar == "on") ShowFloatingForm(); else CloseFloatingForm();
      RefreshMonitorDisplay();
    }
    static void RefreshSliderLabels() {
      if (fanValueLabel != null) fanValueLabel.Text = string.Format(Strings.CurrentSliderValueTemp, fanTrackBar.Value * 100 + " RPM");
      if (cpuPowerValueLabel != null) cpuPowerValueLabel.Text = string.Format(Strings.CurrentSliderValueTemp, cpuPowerTrackBar.Value + " W");
      if (tppValueLabel != null) tppValueLabel.Text = string.Format(Strings.CurrentSliderValueTemp, tppTrackBar.Value + " W");
      if (textSizeLabel != null) textSizeLabel.Text = string.Format(Strings.CurrentSliderValueTemp, textSizeTrackBar.Value * 4);
    }
    static void EvaluateFan(bool debounce = false) {
      if (_isExiting) return;
      lock (fanDecisionGate) {
        RuntimeTarget target = Volatile.Read(ref runtimeTarget);
        if (target == null) return;
        Telemetry data = Volatile.Read(ref telemetry);
        bool matching = data.MonitorVersion == target.MonitorVersion;
        Reading cpu = matching && target.Cpu ? data.CpuTemperature : null;
        Reading gpu = matching && target.Gpu ? data.GpuTemperature : null;
        FanDecision decision = fanController.Evaluate(target.FanGeneration, target.FanMode, target.FixedRpm,
          target.Cleaning, cpu, data.CpuSmoothed, gpu, data.GpuSmoothed, target.CpuCurve, target.GpuCurve);
        string speed = decision.Rpm.HasValue ? (decision.Rpm.Value / 100) + "," + (decision.Rpm.Value / 100) + "," + (Is3FanNb ? "1" : "0") + ",0" : null;
        if (decision.Mode == "clean") speed = platformSettings.CleanCreekCpuFanSpeed + "," + platformSettings.CleanCreekGpuFanSpeed + "," + (Is3FanNb ? "1" : "0") + ",1";
        hardwareDispatcher.SubmitFan(target.FanGeneration, data.Version, decision.Mode, speed, debounce, target.Revision);
        bool high = target.Protect && target.FixedRpm.HasValue && platformMaxFanSpeed.HasValue &&
          cpu != null && cpu.Fresh(controlClock.Milliseconds) && cpu.Value > (maxCPUTemp ?? 97) - 2 &&
          FanController.FixedTooSlow(matching && target.Fan ? data.FanRpm : null, controlClock.Milliseconds, target.FixedRpm.Value, platformMaxFanSpeed.Value);
        PublishUi(control: new ControlNotice { Target = target, Protecting = decision.Protecting,
          HighTemperature = high, CanReportRecovery = decision.CanReportRecovery, Temperature = cpu?.Value ?? 0 });
      }
    }
    static Task StopHardwareAsync() {
      lock (fanDecisionGate) {
        RuntimeTarget target = runtimeTarget;
        if (target == null) return hardwareDispatcher.StopAsync();
        Telemetry data = Volatile.Read(ref telemetry);
        bool matches = data.MonitorVersion == target.MonitorVersion;
        var final = fanController.Evaluate(target.FanGeneration, target.FanMode, target.FixedRpm, null,
          matches && target.Cpu ? data.CpuTemperature : null, data.CpuSmoothed,
          matches && target.Gpu ? data.GpuTemperature : null, data.GpuSmoothed, target.CpuCurve, target.GpuCurve);
        string speed = final.Rpm.HasValue ? final.Rpm.Value / 100 + "," + final.Rpm.Value / 100 + "," + (Is3FanNb ? "1" : "0") + ",0" : null;
        return hardwareDispatcher.StopAsync(final.Mode, speed);
      }
    }
    static void ApplyControlNotice(ControlNotice notice) {
      if (_isExiting || runtimeTarget == null || notice.Target.Revision != runtimeTarget.Revision) return;
      // Refresh missing/stale labels even when acquisition is blocked and no new frame arrives.
      ApplyTelemetry(Volatile.Read(ref telemetry));
      if (notice.Protecting != lastProtectionNotice) {
        lastProtectionNotice = notice.Protecting;
        if (notice.Protecting || notice.CanReportRecovery) {
          trayIcon.BalloonTipTitle = Strings.FanControl;
          trayIcon.BalloonTipText = notice.Protecting ? Strings.TemperatureLost : Strings.TemperatureRestored;
          trayIcon.BalloonTipIcon = notice.Protecting ? ToolTipIcon.Warning : ToolTipIcon.Info;
          trayIcon.ShowBalloonTip(3000);
        }
      }
      if (notice.HighTemperature && fanControl == notice.Target.FanMode) {
        fanTable = "cool"; fanControl = "auto"; LoadFanConfig("cool.txt");
        SaveConfig("FanTable"); SaveConfig("FanControl");
        ApplyPresetSettings(currentPreset); CommitSettings();
        trayIcon.BalloonTipTitle = Strings.HighTempBalloonTitle;
        trayIcon.BalloonTipText = Strings.HighTempBalloonText(maxCPUTemp ?? 97, notice.Temperature);
        trayIcon.BalloonTipIcon = ToolTipIcon.Warning; trayIcon.ShowBalloonTip(3000);
      }
    }
  }
}
