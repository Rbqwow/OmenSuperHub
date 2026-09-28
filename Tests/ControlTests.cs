using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using OmenSuperHub.Control;

internal static partial class ControlTests {
  private sealed class Clock : IClock {
    public long Milliseconds { get; set; }
  }
  private sealed class Writer : IHardwareWriter {
    internal readonly List<HardwareCommand> Calls = new List<HardwareCommand>();
    internal Func<HardwareCommand, bool> OnWrite;
    public bool Execute(HardwareCommand command) {
      Calls.Add(command);
      return OnWrite == null || OnWrite(command);
    }
  }
  private static SettingsTarget Target(long revision, string cpu = "80", string gpu = "1,1,1", string tpp = "60") {
    return new SettingsTarget(revision, 1, cpu, gpu, null, null, tpp);
  }
  private static void Check(bool condition, string message) {
    if (!condition) throw new Exception(message);
  }
  private static HardwareDispatcher Create(Writer writer, Clock clock, Action<string, string> error = null) {
    return new HardwareDispatcher(writer, clock, error ?? ((message, id) => { }), automatic: false);
  }
  private static void Submit(HardwareDispatcher dispatcher, long revision = 1, string cpu = "80", string speed = "30,30,0,0", ReplayScope replay = ReplayScope.None) {
    dispatcher.Submit(Target(revision, cpu), waitForFan: true, replay: replay);
    dispatcher.SubmitFan(1, 0, "speed", speed, revision: revision);
  }
  private static void Settle(HardwareDispatcher dispatcher, Clock clock) {
    dispatcher.Drain();
    clock.Milliseconds += 1000; dispatcher.Drain();
    clock.Milliseconds += 1000; dispatcher.Drain();
  }
  private static void PerformancePrecedesNormalFan() {
    var clock = new Clock(); var writer = new Writer(); var dispatcher = Create(writer, clock);
    Submit(dispatcher); dispatcher.Drain();
    Check(writer.Calls.First(c => c.Kind != CommandKind.KeepAlive).Kind == CommandKind.Performance, "Startup writes the fan before selecting the performance mode.");
  }
  private static void ModeSwitchSettlesBeforeSettings() {
    var clock = new Clock(); var writer = new Writer(); var dispatcher = Create(writer, clock);
    Submit(dispatcher); dispatcher.Drain();
    Check(writer.Calls.All(c => c.Kind == CommandKind.Performance || c.Kind == CommandKind.KeepAlive), "Settings are written while the BIOS mode switch is still settling.");
    clock.Milliseconds = 999; dispatcher.Drain();
    Check(writer.Calls.Count(c => c.Kind != CommandKind.KeepAlive) == 1, "Mode settle deadline was shortened.");
    clock.Milliseconds = 1000; dispatcher.Drain();
    Check(dispatcher.Applied(CommandKind.CpuPower) == "80", "CPU power was not applied after settling.");
    Check(dispatcher.Applied(CommandKind.FanSpeed) == "30,30,0,0", "Fan speed was not applied after settling.");
    Check(dispatcher.Applied(CommandKind.Tpp) == null, "TPP must also wait for the GPU change to settle.");
    clock.Milliseconds = 2000; dispatcher.Drain();
    Check(dispatcher.Applied(CommandKind.Tpp) == "60", "TPP did not finish applying.");
  }
  private static void ChangingFanDoesNotStarvePower() {
    var clock = new Clock(); var writer = new Writer(); var dispatcher = Create(writer, clock);
    Submit(dispatcher); dispatcher.Drain();
    clock.Milliseconds = 1000; dispatcher.Drain();
    clock.Milliseconds = 2000; dispatcher.Drain();
    writer.Calls.Clear();
    int changes = 0;
    writer.OnWrite = command => {
      if (command.Kind == CommandKind.FanSpeed && changes++ < 20)
        dispatcher.SubmitFan(1, changes, "speed", (31 + changes) + "," + (31 + changes) + ",0,0", revision: 2);
      return true;
    };
    Submit(dispatcher, 2, "90", "31,31,0,0"); dispatcher.Drain();
    int power = writer.Calls.FindIndex(c => c.Kind == CommandKind.CpuPower);
    Check(power >= 0 && writer.Calls.Take(power).Count(c => c.Kind == CommandKind.FanSpeed) <= 1,
      "Continuously superseded fan commands starve the CPU power write.");
    Check(dispatcher.Applied(CommandKind.CpuPower) == "90", "The latest CPU power target was lost.");
  }
  private static void DelayedFirmwareResetDoesNotEraseSettings() {
    var clock = new Clock(); var writer = new Writer(); var dispatcher = Create(writer, clock);
    var actual = new Dictionary<CommandKind, string>();
    writer.OnWrite = command => { actual[command.Kind] = command.Value; return true; };
    Submit(dispatcher); dispatcher.Drain();
    // Simulate firmware completing its asynchronous mode reset after WMI returned success.
    clock.Milliseconds = 500; actual.Clear(); dispatcher.Drain();
    clock.Milliseconds = 1000; dispatcher.Drain();
    clock.Milliseconds = 2000; dispatcher.Drain();
    Check(actual.ContainsKey(CommandKind.CpuPower) && actual[CommandKind.CpuPower] == "80", "Firmware erased CPU power after it was cached as applied.");
    Check(actual.ContainsKey(CommandKind.FanSpeed) && actual[CommandKind.FanSpeed] == "30,30,0,0", "Firmware erased fan control after it was cached as applied.");
    Check(actual[CommandKind.Tpp] == "60", "TPP was not restored.");
  }
  private static void SamePresetReappliesValuesWithoutResettingCooling() {
    var clock = new Clock(); var writer = new Writer(); var dispatcher = Create(writer, clock);
    Submit(dispatcher); Settle(dispatcher, clock);
    writer.Calls.Clear();
    Submit(dispatcher, 2, replay: ReplayScope.Settings); Settle(dispatcher, clock);
    Check(!writer.Calls.Any(c => c.Kind == CommandKind.Performance || c.Kind == CommandKind.FanMax), "Reapplying an unchanged preset reset the cooling mode.");
    Check(writer.Calls.Any(c => c.Kind == CommandKind.CpuPower && c.Value == "80"), "Reapplying the same preset skipped CPU power.");
    Check(writer.Calls.Any(c => c.Kind == CommandKind.FanSpeed && c.Value == "30,30,0,0"), "Reapplying the same preset skipped fan speed.");
    Check(writer.Calls.Any(c => c.Kind == CommandKind.Tpp && c.Value == "60"), "Reapplying the same preset skipped TPP.");
  }
  private static void SameFanModeCanBeReappliedIndependently() {
    var clock = new Clock(); var writer = new Writer(); var dispatcher = Create(writer, clock);
    Submit(dispatcher); Settle(dispatcher, clock);
    writer.Calls.Clear();
    Submit(dispatcher, 2, replay: ReplayScope.Fan); dispatcher.Drain();
    Check(!writer.Calls.Any(c => c.Kind == CommandKind.FanMax), "Reapplying the same fan target reset its mode.");
    Check(writer.Calls.Any(c => c.Kind == CommandKind.FanSpeed && c.Value == "30,30,0,0"), "Fan speed was not rewritten.");
    Check(writer.Calls.All(c => c.Kind <= CommandKind.FanSpeed), "Reapplying just the fan reset the performance settings.");
  }
  private static void NewTargetDuringSettleKeepsDeadline() {
    var clock = new Clock(); var writer = new Writer(); var dispatcher = Create(writer, clock);
    Submit(dispatcher); dispatcher.Drain();
    clock.Milliseconds = 500;
    Submit(dispatcher, 2, "120", "45,45,0,0"); dispatcher.Drain();
    Check(writer.Calls.Count(c => c.Kind != CommandKind.KeepAlive) == 1, "A target edit bypassed the firmware settle window.");
    clock.Milliseconds = 1000; dispatcher.Drain();
    Check(dispatcher.Applied(CommandKind.CpuPower) == "120", "The latest CPU target was not applied at the deadline.");
    Check(dispatcher.Applied(CommandKind.FanSpeed) == "45,45,0,0", "The latest fan target was not applied at the deadline.");
    Check(writer.Calls.Count(c => c.Kind == CommandKind.Performance) == 1, "An ordinary edit restarted the mode change.");
  }
  private static void RejectedModeRetriesAndKeepsCooling() {
    var clock = new Clock(); var writer = new Writer(); var dispatcher = Create(writer, clock);
    writer.OnWrite = command => command.Kind != CommandKind.Performance || clock.Milliseconds >= 3000;
    dispatcher.Submit(Target(1), waitForFan: true);
    dispatcher.SubmitFan(1, 0, "max", null);
    dispatcher.Drain();
    Check(dispatcher.Applied(CommandKind.FanMax) == "1", "Unavailable performance control blocked maximum cooling.");
    Check(dispatcher.Applied(CommandKind.CpuPower) == null, "Power was applied despite a rejected mode.");
    clock.Milliseconds = 999; dispatcher.Drain();
    Check(writer.Calls.Count(c => c.Kind == CommandKind.Performance) == 1, "Failure retry ignored backoff.");
    clock.Milliseconds = 1000; dispatcher.Drain();
    clock.Milliseconds = 3000; dispatcher.Drain();
    Check(writer.Calls.Last().Kind == CommandKind.FanMax, "Maximum cooling was not restored immediately after the mode change.");
    int fanWrites = writer.Calls.Count(c => c.Kind == CommandKind.FanMax);
    clock.Milliseconds = 4000; dispatcher.Drain();
    Check(writer.Calls.Count(c => c.Kind == CommandKind.FanMax) == fanWrites + 1, "Maximum cooling was not reasserted after firmware settled.");
    Check(dispatcher.Applied(CommandKind.CpuPower) == "80", "Startup failure never recovered CPU power.");
  }
  private static void ReapplyWaitsForMatchingFanDecision() {
    var clock = new Clock(); var writer = new Writer(); var dispatcher = Create(writer, clock);
    Submit(dispatcher); Settle(dispatcher, clock);
    writer.Calls.Clear();
    dispatcher.Submit(Target(2, "120"), waitForFan: true, replay: ReplayScope.All);
    dispatcher.SubmitFan(1, 0, "speed", "20,20,0,0", revision: 1);
    dispatcher.Drain();
    Check(writer.Calls.All(c => c.Kind == CommandKind.KeepAlive), "Replay started with a stale fan decision.");
    dispatcher.SubmitFan(1, 0, "speed", "45,45,0,0", revision: 2);
    Settle(dispatcher, clock);
    Check(dispatcher.Applied(CommandKind.CpuPower) == "120" && dispatcher.Applied(CommandKind.FanSpeed) == "45,45,0,0", "Replay lost part of the latest target.");
  }
  private static void SupersededWriteIsNotReportedAsBiosFailure() {
    var clock = new Clock(); var writer = new Writer(); var errors = new List<string>();
    var dispatcher = Create(writer, clock, (message, id) => errors.Add(message));
    bool changed = false;
    writer.OnWrite = command => {
      if (command.Kind == CommandKind.Performance && !changed) {
        changed = true;
        Submit(dispatcher, 2, "120");
        Check(!command.StillCurrent(), "Superseded command remained valid.");
        return false;
      }
      return true;
    };
    Submit(dispatcher); Settle(dispatcher, clock);
    Check(errors.Count == 0, "Normal command cancellation was reported as a BIOS failure.");
    Check(dispatcher.Applied(CommandKind.CpuPower) == "120", "The replacement target was not applied.");
  }
  private static void AutomaticTimerCompletesDelayedSettings() {
    var writer = new Writer();
    using (var done = new ManualResetEventSlim()) {
      writer.OnWrite = command => { if (command.Kind == CommandKind.Tpp) done.Set(); return true; };
      var dispatcher = new HardwareDispatcher(writer, new MonotonicClock(), (message, id) => { });
      try {
        Submit(dispatcher);
        Check(done.Wait(10000), "One-shot timer did not wake for dependent settings.");
      } finally { dispatcher.StopAsync().GetAwaiter().GetResult(); }
      Check(dispatcher.Applied(CommandKind.CpuPower) == "80" && dispatcher.Applied(CommandKind.FanSpeed) == "30,30,0,0", "Automatic dispatch did not finish the full target.");
    }
  }
  private static FanCurve CoolingCurve() {
    return new FanCurve(new[] { new KeyValuePair<float, int>(65, 3000), new KeyValuePair<float, int>(95, 6400) });
  }
  private static void MissingInitialTemperatureDoesNotDisableCooling() {
    var clock = new Clock(); var writer = new Writer(); var dispatcher = Create(writer, clock);
    var controller = new FanController(clock);
    var curve = CoolingCurve();
    dispatcher.Submit(Target(1), waitForFan: true);
    var decision = controller.Evaluate(1, "auto", null, null, null, null, null, null, curve, curve);
    dispatcher.SubmitFan(1, 0, decision.Mode, null);
    Settle(dispatcher, clock);
    Check(!writer.Calls.Any(c => c.Kind == CommandKind.FanMax && c.Value == "0"), "Startup disables cooling before a usable fan speed exists.");
    clock.Milliseconds = 5000;
    decision = controller.Evaluate(1, "auto", null, null, null, null, null, null, curve, curve);
    dispatcher.SubmitFan(1, 0, decision.Mode, null); dispatcher.Drain();
    Check(dispatcher.Applied(CommandKind.FanMax) == "1", "Missing temperatures did not enable the cooling failsafe.");
    clock.Milliseconds = 6000;
    decision = controller.Evaluate(1, "auto", null, null, new Reading(50, 6000), 50, null, null, curve, curve);
    Check(decision.Rpm == 3000, "A fresh temperature below the first curve point must retain the curve's minimum RPM.");
    dispatcher.SubmitFan(1, 1, decision.Mode, "30,30,0,0"); dispatcher.Drain();
    Check(dispatcher.Applied(CommandKind.FanSpeed) == "30,30,0,0", "Cooling did not recover after temperatures became available.");
  }
  private static void StableCoolingOnlyRenewsFirmwareLease() {
    foreach (string mode in new[] { "speed", "max" }) {
      var clock = new Clock(); var writer = new Writer(); var dispatcher = Create(writer, clock);
      dispatcher.Submit(Target(1), waitForFan: true);
      dispatcher.SubmitFan(1, 0, mode, mode == "speed" ? "30,30,0,0" : null);
      Settle(dispatcher, clock); writer.Calls.Clear();
      for (int second = 3; second <= 180; second++) { clock.Milliseconds = second * 1000; dispatcher.Drain(); }
      Check(writer.Calls.Count == 6 && writer.Calls.All(c => c.Kind == CommandKind.KeepAlive),
        "Stable cooling should renew the firmware lease without repeatedly rewriting fan/power settings.");
    }
  }
  private static void OneStoppedFanReappliesSpeedWithoutResettingMode() {
    var clock = new Clock(); var writer = new Writer(); var dispatcher = Create(writer, clock);
    Submit(dispatcher); Settle(dispatcher, clock);
    writer.Calls.Clear(); clock.Milliseconds = 3000;
    Check(dispatcher.ObserveFan(1, 3000, new[] { 0, 30, 0 }, false), "One stalled fan was hidden by the other fan's RPM.");
    dispatcher.Drain();
    Check(writer.Calls.Count == 1 && writer.Calls[0].Kind == CommandKind.FanSpeed && writer.Calls[0].Value == "30,30,0,0",
      "Low-RPM recovery reset the mode instead of renewing just the speed.");
  }
  private static void RepeatedPresetReapplicationsDoNotResetRunningFans() {
    var clock = new Clock(); var writer = new Writer(); var dispatcher = Create(writer, clock);
    Submit(dispatcher); Settle(dispatcher, clock); writer.Calls.Clear();
    for (int round = 1; round <= 4; round++) {
      clock.Milliseconds = round * 30000;
      Submit(dispatcher, round + 1, replay: ReplayScope.Settings); Settle(dispatcher, clock);
    }
    Check(!writer.Calls.Any(c => c.Kind == CommandKind.Performance || c.Kind == CommandKind.FanMax),
      "Repeated reapplication resets an already-running fan.");
    Check(writer.Calls.Count(c => c.Kind == CommandKind.CpuPower) == 4 && writer.Calls.Count(c => c.Kind == CommandKind.Tpp) == 4,
      "Avoiding mode resets also dropped the requested power replays.");
  }
  private static void SlowFanSpinUpIsNotRestartedByFeedback() {
    var clock = new Clock(); var writer = new Writer(); var dispatcher = Create(writer, clock);
    long spinUpAt = long.MaxValue;
    writer.OnWrite = command => {
      // Model a controller whose mode switch interrupts spin-up; repeating the
      // same positive RPM target should allow the existing ramp to finish.
      if (command.Kind == CommandKind.Performance || command.Kind == CommandKind.FanMax) spinUpAt = long.MaxValue;
      if (command.Kind == CommandKind.FanSpeed && spinUpAt == long.MaxValue) spinUpAt = clock.Milliseconds + 4000;
      return true;
    };
    Submit(dispatcher); Settle(dispatcher, clock);
    int measured = 0;
    for (int second = 3; second <= 9; second++) {
      clock.Milliseconds = second * 1000;
      measured = clock.Milliseconds >= spinUpAt ? 30 : 0;
      dispatcher.ObserveFan(1, clock.Milliseconds, new[] { measured, measured, 0 }, false);
      dispatcher.Drain();
    }
    Check(measured == 30, "Readback recovery keeps interrupting spin-up, so the fans never reach their target.");
  }
  private static void ResumeStillRestoresModeAndAllSettings() {
    var clock = new Clock(); var writer = new Writer(); var dispatcher = Create(writer, clock);
    Submit(dispatcher); Settle(dispatcher, clock); writer.Calls.Clear();
    Submit(dispatcher, 2, replay: ReplayScope.All); Settle(dispatcher, clock);
    Check(writer.Calls.Any(c => c.Kind == CommandKind.Performance) && writer.Calls.Any(c => c.Kind == CommandKind.FanMax && c.Value == "0"),
      "Resume must still re-establish hardware modes invalidated by sleep.");
    Check(dispatcher.Applied(CommandKind.CpuPower) == "80" && dispatcher.Applied(CommandKind.FanSpeed) == "30,30,0,0" &&
      dispatcher.Applied(CommandKind.Tpp) == "60", "Full hardware restoration lost a dependent setting.");
  }
  private static void MaximumCoolingIsVerifiedAgainstActualRpm() {
    var clock = new Clock(); var writer = new Writer(); var dispatcher = Create(writer, clock);
    dispatcher.Submit(Target(1), waitForFan: true); dispatcher.SubmitFan(1, 0, "max", null);
    Settle(dispatcher, clock);
    writer.Calls.Clear(); clock.Milliseconds = 3000;
    Check(dispatcher.ObserveFan(1, 3000, new[] { 64, 0, 0 }, false), "Acknowledged maximum mode masked a stopped fan.");
    dispatcher.Drain();
    Check(writer.Calls.Count == 1 && writer.Calls[0].Kind == CommandKind.FanMax && writer.Calls[0].Value == "1", "Maximum mode was not retried after a stalled readback.");
  }
  private static void FeedbackIgnoresStaleSamplesSpinUpAndIntentionalStop() {
    var clock = new Clock(); var writer = new Writer(); var dispatcher = Create(writer, clock);
    Submit(dispatcher); Settle(dispatcher, clock);
    Check(!dispatcher.ObserveFan(1, 2000, new[] { 0, 0, 0 }, false), "Normal spin-up was treated as failed control.");
    clock.Milliseconds = 5000;
    Check(!dispatcher.ObserveFan(1, 2000, new[] { 0, 0, 0 }, false), "A stale sample triggered recovery.");
    Check(!dispatcher.ObserveFan(0, 5000, new[] { 0, 0, 0 }, false), "An old fan generation triggered recovery.");
    Check(!dispatcher.ObserveFan(1, 5000, new[] { 30, 31, 0 }, false), "Normal RPM tolerance triggered recovery.");
    Check(!dispatcher.ObserveFan(1, 5000, new[] { 35, 38, 0 }, false), "Extra cooling / normal coast-down triggered recovery.");
    Submit(dispatcher, 2, speed: "0,0,0,0"); dispatcher.Drain();
    clock.Milliseconds = 7000;
    Check(!dispatcher.ObserveFan(1, 7000, new[] { 0, 0, 0 }, false), "An intentional zero-RPM setting was overridden.");
  }
  private static void MissingTemperatureStopsCleaningSafely() {
    var clock = new Clock(); var writer = new Writer(); var dispatcher = Create(writer, clock);
    dispatcher.Submit(Target(1), waitForFan: true); dispatcher.SubmitFan(1, 0, "clean", "30,30,0,1");
    Settle(dispatcher, clock); writer.Calls.Clear();
    dispatcher.SubmitFan(1, 1, "hold", null); dispatcher.Drain();
    Check(writer.Calls.Any(c => c.Kind == CommandKind.FanMax && c.Value == "1"), "Holding cooling left reverse cleaning active.");
    dispatcher.StopAsync().GetAwaiter().GetResult();
  }
  private static void HoldPreservesExistingFanTarget() {
    var clock = new Clock(); var writer = new Writer(); var dispatcher = Create(writer, clock);
    dispatcher.Submit(Target(1), waitForFan: true);
    dispatcher.SubmitFan(1, 0, "speed", "30,30,0,0"); Settle(dispatcher, clock);
    writer.Calls.Clear();
    dispatcher.SubmitFan(1, 1, "hold", null); dispatcher.Drain();
    Check(writer.Calls.Count == 0, "A missing temperature cleared an already-active fan target.");
    clock.Milliseconds = 4000;
    Check(dispatcher.ObserveFan(1, 4000, new[] { 0, 0, 0 }, false), "Hold discarded the last target instead of retaining it for recovery.");
    dispatcher.Drain();
    Check(dispatcher.Applied(CommandKind.FanSpeed) == "30,30,0,0", "Held target was not recovered after a stalled readback.");
  }
  private static void FirmwareLeaseSurvivesBusyControlForThreeMinutes() {
    var clock = new Clock(); var writer = new Writer(); var dispatcher = Create(writer, clock);
    long leaseExpires = 120000, previousKeepAlive = -1, maximumGap = 0, revision = 1;
    int keepAlives = 0;
    writer.OnWrite = command => {
      if (command.Kind == CommandKind.KeepAlive) {
        if (previousKeepAlive >= 0) maximumGap = Math.Max(maximumGap, clock.Milliseconds - previousKeepAlive);
        previousKeepAlive = clock.Milliseconds; leaseExpires = clock.Milliseconds + 120000; keepAlives++;
      }
      return true;
    };
    dispatcher.Submit(Target(revision), waitForFan: true);
    for (int second = 0; second <= 181; second++) {
      clock.Milliseconds = second * 1000;
      Check(clock.Milliseconds < leaseExpires, "Firmware dropped custom fan/power control at its 120-second deadline.");
      if (second > 0 && second % 30 == 0)
        dispatcher.Submit(Target(++revision), waitForFan: true, replay: ReplayScope.Settings);
      int level = 22 + second % 2;
      dispatcher.SubmitFan(1, second, "speed", level + "," + level + ",0,0", revision: revision);
      dispatcher.Drain();
    }
    Check(keepAlives >= 7 && maximumGap <= 30000, "Busy fan/power work skipped required firmware keep-alives.");
    Check(writer.Calls[0].Kind == CommandKind.KeepAlive, "Control was applied before enabling the firmware lease.");
  }
  private static void WaitingForFanCannotSuppressKeepAlive() {
    var clock = new Clock(); var writer = new Writer(); var dispatcher = Create(writer, clock);
    dispatcher.Submit(Target(1), waitForFan: true); dispatcher.Drain();
    Check(writer.Calls.Count == 1 && writer.Calls[0].Kind == CommandKind.KeepAlive, "A pending fan decision blocked the initial keep-alive.");
    clock.Milliseconds = 30000;
    dispatcher.Submit(Target(2), waitForFan: true); dispatcher.Drain();
    Check(writer.Calls.Count == 2 && writer.Calls.All(c => c.Kind == CommandKind.KeepAlive), "Waiting for a target suppressed periodic keep-alives or dispatched incomplete settings.");
  }
  private static void NewSettingsCannotCancelInFlightKeepAlive() {
    var clock = new Clock(); var writer = new Writer(); var dispatcher = Create(writer, clock);
    bool checkedGuard = false;
    writer.OnWrite = command => {
      if (command.Kind == CommandKind.KeepAlive) {
        dispatcher.Submit(Target(2), waitForFan: true);
        checkedGuard = command.StillCurrent();
      }
      return true;
    };
    dispatcher.Submit(Target(1), waitForFan: true); dispatcher.Drain();
    Check(checkedGuard, "An unrelated settings revision cancelled the firmware keep-alive while waiting for WMI.");
  }
  private static void FailedKeepAliveRetriesAndStopsWithDispatcher() {
    var clock = new Clock(); var writer = new Writer(); var dispatcher = Create(writer, clock);
    int attempts = 0;
    writer.OnWrite = command => command.Kind != CommandKind.KeepAlive || ++attempts >= 3;
    Submit(dispatcher); dispatcher.Drain();
    clock.Milliseconds = 1000; dispatcher.Drain();
    clock.Milliseconds = 3000; dispatcher.Drain();
    Check(attempts == 3 && dispatcher.Applied(CommandKind.KeepAlive) != null, "A transient WMI failure lost the firmware keep-alive.");
    dispatcher.StopAsync().GetAwaiter().GetResult();
    clock.Milliseconds = 90000; dispatcher.Drain();
    Check(attempts == 3, "Keep-alive outlived the hardware dispatcher.");
  }
  private static int Main() {
    Action[] tests = {
      PerformancePrecedesNormalFan, ModeSwitchSettlesBeforeSettings, ChangingFanDoesNotStarvePower,
      DelayedFirmwareResetDoesNotEraseSettings, SamePresetReappliesValuesWithoutResettingCooling,
      SameFanModeCanBeReappliedIndependently, NewTargetDuringSettleKeepsDeadline,
      RejectedModeRetriesAndKeepsCooling, ReapplyWaitsForMatchingFanDecision,
      SupersededWriteIsNotReportedAsBiosFailure, AutomaticTimerCompletesDelayedSettings,
      MissingInitialTemperatureDoesNotDisableCooling, StableCoolingOnlyRenewsFirmwareLease,
      OneStoppedFanReappliesSpeedWithoutResettingMode,
      MaximumCoolingIsVerifiedAgainstActualRpm, FeedbackIgnoresStaleSamplesSpinUpAndIntentionalStop,
      MissingTemperatureStopsCleaningSafely, HoldPreservesExistingFanTarget,
      RepeatedPresetReapplicationsDoNotResetRunningFans, SlowFanSpinUpIsNotRestartedByFeedback,
      ResumeStillRestoresModeAndAllSettings, FirmwareLeaseSurvivesBusyControlForThreeMinutes,
      WaitingForFanCannotSuppressKeepAlive, NewSettingsCannotCancelInFlightKeepAlive,
      FailedKeepAliveRetriesAndStopsWithDispatcher,
      NativePacketPreservesFirmwareAbi, NativePacketRejectsInvalidResponses,
      NativeInstanceNamesAreDecoded, NativeInstanceOffsetsAreValidated,
      NativeFallbackProbesBeforeWriting, NativeFallbackRejectsUnverifiedEndpoint,
      NativeFallbackHonorsFirmwareRejection, NativeFallbackCancelsStaleCommands,
      NativeTransportFailureForcesRevalidation, NativeAccessDenialIsPreserved,
      NativeVerifiedEndpointAvoidsRepeatedProbes
    };
    int failed = 0;
    foreach (Action test in tests) {
      try { test(); Console.WriteLine("PASS " + test.Method.Name); }
      catch (Exception ex) { failed++; Console.WriteLine("FAIL " + test.Method.Name + ": " + ex.Message); }
    }
    Console.WriteLine((tests.Length - failed) + "/" + tests.Length + " passed");
    return failed == 0 ? 0 : 1;
  }
}
