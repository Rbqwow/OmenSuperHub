using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OmenSuperHub.Control {
  internal enum CommandKind { LegacyClean, FanMax, FanSpeed, Performance, CpuPower, GpuPower, IccMax, LoadLine, Tpp, KeepAlive }
  internal enum ReplayScope { None, Fan, Settings, All }
  internal sealed class HardwareCommand {
    public readonly CommandKind Kind;
    public readonly string Value;
    internal Func<bool> StillCurrent;
    public HardwareCommand(CommandKind kind, string value) { Kind = kind; Value = value; }
  }
  internal interface IHardwareWriter { bool Execute(HardwareCommand command); }
  internal sealed class SettingsTarget {
    public readonly long Revision, FanGeneration;
    public readonly string Cpu, Gpu, Icc, LoadLine, Tpp;
    public SettingsTarget(long revision, long fanGeneration, string cpu, string gpu, string icc, string loadLine, string tpp) {
      Revision = revision; FanGeneration = fanGeneration;
      Cpu = cpu; Gpu = gpu; Icc = icc; LoadLine = loadLine; Tpp = tpp;
    }
  }
  // One consumer, one fixed slot per command. Never cancel native calls or release their lock early.
  internal sealed class HardwareDispatcher {
    private const int FanSpinUpMs = 2000, KeepAliveMs = 30000;
    private sealed class Slot {
      public string Target, Applied;
      public long Version, Due, AcceptedAt;
      public int Failures;
    }
    private sealed class Work {
      public HardwareCommand Command;
      public long Version, Revision;
    }
    private readonly object gate = new object();
    private readonly Slot[] slots = Enumerable.Range(0, Enum.GetValues(typeof(CommandKind)).Length).Select(_ => new Slot()).ToArray();
    private readonly IHardwareWriter writer;
    private readonly IClock clock;
    private readonly Action<string, string> error;
    private readonly Timer timer;
    private readonly TaskCompletionSource<bool> stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    private SettingsTarget target;
    private bool busy, stopping, legacyMayBeOn, fanInFlight, reverseMayBeOn, finishing, fanPending;
    private string shutdownFanMode, shutdownFanSpeed;
    private long fanSample = -1;
    private long scheduledAt = long.MaxValue;
    private long performanceReadyAt;
    private int nextSlot;
    private string fanMode;
    internal HardwareDispatcher(IHardwareWriter writer, IClock clock, Action<string, string> error, bool automatic = true) {
      this.writer = writer; this.clock = clock; this.error = error;
      if (automatic) timer = new Timer(_ => Drain(), null, Timeout.Infinite, Timeout.Infinite);
    }
    private Slot S(CommandKind kind) { return slots[(int)kind]; }
    internal bool HasUrgentWork {
      get { lock (gate) return !stopping && (fanPending || fanInFlight ||
        Enumerable.Range(0, 3).Any(i => slots[i].Target != null && slots[i].Target != slots[i].Applied &&
          slots[i].Due <= clock.Milliseconds && Dependencies((CommandKind)i))); }
    }
    internal string Applied(CommandKind kind) { lock (gate) return S(kind).Applied; }
    internal void Submit(SettingsTarget next, CommandKind? debounce = null, bool waitForFan = false, ReplayScope replay = ReplayScope.None) {
      lock (gate) {
        if (stopping || target != null && next.Revision <= target.Revision) return;
        bool newFan = target == null || next.FanGeneration != target.FanGeneration;
        target = next;
        fanPending = waitForFan;
        // Routine reapplication refreshes values without resetting an established mode.
        // Only a known hardware reset (e.g. resume) invalidates all mode acknowledgements.
        // Keep invalidation atomic with submission so an old target cannot start in between.
        if (replay != ReplayScope.None)
          for (int i = 0; i < slots.Length; i++)
            if (Replays((CommandKind)i, replay)) InvalidateSlot((CommandKind)i);
        Set(CommandKind.KeepAlive, "1");
        Set(CommandKind.Performance, "unleash");
        Set(CommandKind.CpuPower, next.Cpu, debounce);
        Set(CommandKind.GpuPower, next.Gpu, debounce);
        Set(CommandKind.IccMax, next.Icc, debounce);
        Set(CommandKind.LoadLine, next.LoadLine, debounce);
        Set(CommandKind.Tpp, next.Tpp, debounce);
        if (newFan) {
          fanSample = -1;
          if (fanMode != "clean" && fanMode != "legacy") {
            fanMode = null;
            Set(CommandKind.FanMax, null); Set(CommandKind.FanSpeed, null);
          }
        }
        Arm();
      }
    }
    internal void SubmitFan(long generation, long sample, string mode, string speed, bool debounce = false, long revision = -1) {
      lock (gate) {
        if (stopping || target == null || generation != target.FanGeneration || sample < fanSample ||
          revision >= 0 && revision != target.Revision) return;
        fanPending = false;
        fanSample = sample;
        // A missing speed is not a request to release cooling. Cleaning must still
        // be stopped safely when no temperature-derived replacement is available.
        if (mode == "speed" && speed == null) mode = "hold";
        if (mode == "hold") {
          if (legacyMayBeOn || reverseMayBeOn || fanMode == "clean" || fanMode == "legacy") mode = "max";
          else { Arm(); return; }
        }
        if (fanMode != mode) {
          InvalidateSlot(CommandKind.FanMax); InvalidateSlot(CommandKind.FanSpeed); fanMode = mode;
        }
        Set(CommandKind.LegacyClean, mode == "legacy" ? "1" : legacyMayBeOn ? "0" : null);
        Set(CommandKind.FanMax, mode == "max" ? "1" : "0");
        Set(CommandKind.FanSpeed, mode == "speed" || mode == "clean" ? speed : null,
          debounce ? (CommandKind?)CommandKind.FanSpeed : null);
        Arm();
      }
    }
    internal bool ObserveFan(long generation, long sampledAt, IReadOnlyList<int> levels, bool threeFans) {
      lock (gate) {
        if (stopping || fanPending || target == null || generation != target.FanGeneration || levels == null ||
          levels.Count < (threeFans ? 3 : 2) || sampledAt > clock.Milliseconds || clock.Milliseconds - sampledAt > 2000) return false;
        if (fanMode != "speed" && fanMode != "max") return false;
        Slot speed = S(fanMode == "max" ? CommandKind.FanMax : CommandKind.FanSpeed);
        if (speed.Target == null || speed.Applied != speed.Target || sampledAt - speed.AcceptedAt < FanSpinUpMs) return false;
        string[] values = fanMode == "speed" ? speed.Target.Split(',') : null;
        bool mismatch = false;
        for (int i = 0; i < (threeFans ? 3 : 2); i++) {
          if (fanMode == "max") mismatch |= levels[i] == 0;
          else {
            int expected = i < 2 ? int.Parse(values[i]) : (int.Parse(values[0]) + int.Parse(values[1])) / 2;
            // Coasting down, firmware minimum RPM and extra cooling are not a stall.
            mismatch |= expected - levels[i] > 1;
          }
        }
        if (!mismatch) return false;
        // A low speed does not imply a mode change. Retry the speed itself.
        InvalidateSlot(fanMode == "max" ? CommandKind.FanMax : CommandKind.FanSpeed);
        Arm();
        return true;
      }
    }
    internal void Flush() {
      lock (gate) { foreach (Slot slot in slots) if (slot.Failures == 0) slot.Due = 0; Arm(); }
    }
    private bool Replays(CommandKind kind, ReplayScope replay) {
      if (replay == ReplayScope.All) return true;
      if (kind == CommandKind.KeepAlive) return false;
      if (replay == ReplayScope.Settings && kind >= CommandKind.CpuPower) return true;
      // Real fan mode changes still invalidate these slots in SubmitFan. An unchanged
      // speed/max target only needs its value renewed; never restart reverse cleaning.
      return kind == CommandKind.FanSpeed && fanMode == "speed" ||
        kind == CommandKind.FanMax && S(kind).Target == "1";
    }
    private void InvalidateSlot(CommandKind kind, bool preserveSchedule = false) {
      Slot s = S(kind); s.Applied = null; s.Version++;
      if (!preserveSchedule) { s.Due = 0; s.Failures = 0; }
    }
    private void Set(CommandKind kind, string value, CommandKind? debounce = null) {
      Slot s = S(kind);
      if (s.Target == value) return;
      s.Target = value; s.Version++; s.Failures = 0; s.Due = 0;
      if (value == null) s.Applied = null;
      if (debounce == kind) s.Due = clock.Milliseconds + 150;
      if (kind == CommandKind.GpuPower) InvalidateSlot(CommandKind.Tpp);
    }
    private bool Pending(CommandKind kind) { Slot s = S(kind); return s.Target != null && s.Target != s.Applied; }
    private bool Ready(CommandKind kind) { return !Pending(kind); }
    private bool Dependencies(CommandKind kind) {
      if (kind == CommandKind.KeepAlive) return true;
      if (kind == CommandKind.LegacyClean && S(kind).Target == "1") return S(CommandKind.FanMax).Applied == "0";
      if (kind == CommandKind.FanMax || kind == CommandKind.FanSpeed)
        if (legacyMayBeOn && S(CommandKind.LegacyClean).Target != "1") return false;
      if (kind == CommandKind.FanSpeed) return S(CommandKind.FanMax).Applied == "0";
      if (kind >= CommandKind.CpuPower) return Ready(CommandKind.Performance);
      return true;
    }
    private bool Urgent(CommandKind kind) {
      return kind == CommandKind.LegacyClean && S(kind).Target == "0" ||
        kind == CommandKind.FanMax && S(kind).Target == "1";
    }
    private Work Select(CommandKind kind, ref long nextDue) {
      Slot s = S(kind);
      bool refresh = kind == CommandKind.KeepAlive && s.Target != null && s.Target == s.Applied;
      if ((!Pending(kind) && !refresh) || !Dependencies(kind)) return null;
      if (kind == CommandKind.Tpp && !Ready(CommandKind.GpuPower)) return null;
      // Renew the firmware lease; unchanged fan values need no periodic rewrite.
      long due = refresh ? Math.Max(s.Due, s.AcceptedAt + KeepAliveMs) : s.Due;
      // A successful WMI return only acknowledges the mode change. The firmware can
      // still reset power/fan settings for a short time afterwards. Keep safety writes immediate.
      if (kind != CommandKind.Performance && kind != CommandKind.KeepAlive && !Urgent(kind)) due = Math.Max(due, performanceReadyAt);
      if (kind == CommandKind.Tpp) {
        // Derive the deadline from accepted dependencies. A coalesced A -> B -> A
        // request need not rewrite the already-applied GPU value to unblock TPP.
        long dependencyAccepted = Math.Max(S(CommandKind.Performance).AcceptedAt,
          S(CommandKind.GpuPower).Target == null ? 0 : S(CommandKind.GpuPower).AcceptedAt);
        due = Math.Max(due, dependencyAccepted + 1000);
      }
      nextDue = Math.Min(nextDue, due);
      return due <= clock.Milliseconds ? new Work {
        Command = new HardwareCommand(kind, s.Target), Version = s.Version, Revision = target.Revision
      } : null;
    }
    private Work Select(out long nextDue) {
      nextDue = long.MaxValue;
      if (stopping || target == null) return null;
      // 0x10 enables/renews HP's 120-second custom thermal-control lease. This is
      // mandatory control traffic, not optional monitoring: neither changing fan
      // targets nor a pending UI decision may suppress it.
      Work keepAlive = Select(CommandKind.KeepAlive, ref nextDue);
      if (keepAlive != null) return keepAlive;
      if (fanPending) return null;
      // Stop cleaning / provide maximum cooling first, then establish the performance
      // mode before normal fan writes. Rotate the remaining slots so changing fan targets
      // cannot monopolize a slow BIOS bus and starve power settings forever.
      for (int i = 0; i <= (int)CommandKind.FanSpeed; i++) {
        CommandKind kind = (CommandKind)i;
        Work urgent = Urgent(kind) ? Select(kind, ref nextDue) : null;
        if (urgent != null) return urgent;
      }
      Work performance = Select(CommandKind.Performance, ref nextDue);
      if (performance != null) return performance;
      for (int offset = 0; offset < slots.Length; offset++) {
        Work work = Select((CommandKind)((nextSlot + offset) % slots.Length), ref nextDue);
        if (work != null) return work;
      }
      return null;
    }
    private void Arm() {
      if (timer == null || busy || stopping) return;
      // Once a timer callback can already be queued, let that one consume the latest target.
      // Repeated Change(0) calls while the thread pool is busy must not queue a callback per edit.
      if (scheduledAt <= clock.Milliseconds) return;
      long due; Select(out due);
      if (due == scheduledAt) return;
      scheduledAt = due;
      timer.Change(due == long.MaxValue ? Timeout.Infinite : (int)Math.Min(int.MaxValue, Math.Max(0, due - clock.Milliseconds)), Timeout.Infinite);
    }
    private bool Current(Work work) {
      return !stopping && S(work.Command.Kind).Version == work.Version &&
        (work.Command.Kind == CommandKind.KeepAlive || !fanPending &&
          (work.Command.Kind <= CommandKind.FanSpeed || target.Revision == work.Revision));
    }
    // Also used by the deterministic test harness; production uses the one-shot timer.
    internal void Drain() {
      lock (gate) { if (stopping || busy) return; scheduledAt = long.MaxValue; busy = true; }
      try {
        while (true) {
          Work work;
          lock (gate) {
            long due; work = Select(out due);
            if (work == null) return;
            nextSlot = ((int)work.Command.Kind + 1) % slots.Length;
            fanInFlight = work.Command.Kind <= CommandKind.FanSpeed;
            if (work.Command.Kind == CommandKind.LegacyClean && work.Command.Value == "1") legacyMayBeOn = true;
            if (work.Command.Kind == CommandKind.FanSpeed && work.Command.Value.EndsWith(",1", StringComparison.Ordinal)) reverseMayBeOn = true;
          }
          bool ok = false;
          work.Command.StillCurrent = () => {
            lock (gate) return Current(work);
          };
          try { if (work.Command.StillCurrent()) ok = writer.Execute(work.Command); }
          catch (Exception ex) { error(ex.ToString(), "hardware." + work.Command.Kind); }
          bool current;
          lock (gate) {
            fanInFlight = false;
            Slot s = S(work.Command.Kind);
            current = Current(work);
            if (work.Command.Kind == CommandKind.LegacyClean && work.Command.Value == "0" && ok) legacyMayBeOn = false;
            if (ok && (work.Command.Kind == CommandKind.FanMax && work.Command.Value == "1" ||
              work.Command.Kind == CommandKind.FanSpeed && work.Command.Value.EndsWith(",0", StringComparison.Ordinal))) reverseMayBeOn = false;
            // Even a superseded/failed mode call might have reset hardware state.
            if (work.Command.Kind == CommandKind.Performance) {
              if (ok) performanceReadyAt = clock.Milliseconds + 1000;
              for (int i = 0; i < slots.Length; i++)
                if (i != (int)CommandKind.Performance && i != (int)CommandKind.KeepAlive) InvalidateSlot((CommandKind)i, true);
            }
            if (current && ok) {
              s.Applied = work.Command.Value; s.AcceptedAt = clock.Milliseconds; s.Failures = 0;
              if (work.Command.Kind == CommandKind.FanMax && work.Command.Value == "1" && clock.Milliseconds < performanceReadyAt) {
                // Provide cooling immediately, then reassert it once the mode reset has settled.
                s.Applied = null; s.Due = performanceReadyAt;
              }
            } else {
              s.Applied = null;
              if (current) {
                s.Failures++;
                s.Due = clock.Milliseconds + (work.Command.Kind <= CommandKind.FanSpeed ? 1000 :
                  Math.Min(30000, 1000 << Math.Min(5, s.Failures - 1)));
              }
            }
            if (legacyMayBeOn && fanMode != "legacy") Set(CommandKind.LegacyClean, "0");
          }
          if (!ok && current) error("BIOS rejected " + work.Command.Kind + " (" + work.Command.Value + ")", "hardware." + work.Command.Kind);
        }
      } finally {
        lock (gate) { busy = false; if (stopping) FinishStopping(); else Arm(); }
      }
    }
    internal Task StopAsync(string finalFanMode = null, string finalFanSpeed = null) {
      lock (gate) {
        if (!stopping) { stopping = true; shutdownFanMode = finalFanMode; shutdownFanSpeed = finalFanSpeed; timer?.Dispose(); }
        if (!busy) FinishStopping();
        return stopped.Task;
      }
    }
    private void FinishStopping() {
      if (finishing) return;
      finishing = true;
      if (!legacyMayBeOn && !reverseMayBeOn) { stopped.TrySetResult(true); return; }
      // Final reverse cleanup is part of this consumer, after its last in-flight call.
      // A rejected stop is reported; it must never be treated as confirmed success.
      Task.Run(() => {
        try {
          if (legacyMayBeOn && !FinalCommand(CommandKind.LegacyClean, "0")) return;
          if (shutdownFanMode == "speed" && shutdownFanSpeed != null) {
            if (FinalCommand(CommandKind.FanMax, "0")) FinalCommand(CommandKind.FanSpeed, shutdownFanSpeed);
          } else FinalCommand(CommandKind.FanMax, "1");
        } finally { stopped.TrySetResult(true); }
      });
    }
    private bool FinalCommand(CommandKind kind, string value) {
      bool ok = false;
      try { ok = writer.Execute(new HardwareCommand(kind, value) { StillCurrent = () => true }); }
      catch (Exception ex) { error(ex.ToString(), "hardware.shutdown." + kind); }
      if (!ok) error("Reverse cleanup rejected: " + kind, "hardware.shutdown." + kind);
      return ok;
    }
  }
}
