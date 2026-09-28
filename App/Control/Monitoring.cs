using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace OmenSuperHub.Control {
  internal sealed class Reading {
    internal const long MaxAge = 2000;
    public readonly float? Value;
    public readonly long LastSuccess, SampledAt, InvalidSince;
    public readonly bool Estimated;
    public Reading(float? value, long now, Reading previous = null, bool estimated = false) {
      Value = value.HasValue && !float.IsNaN(value.Value) && !float.IsInfinity(value.Value) ? value : null;
      SampledAt = now; LastSuccess = Value.HasValue ? now : previous == null ? -1 : previous.LastSuccess;
      InvalidSince = Value.HasValue ? -1 : previous != null && previous.InvalidSince >= 0 ? previous.InvalidSince : now;
      Estimated = estimated;
    }
    public bool Fresh(long now, long maxAge = MaxAge) { return Value.HasValue && now - SampledAt <= maxAge; }
  }
  internal sealed class Telemetry {
    public readonly Reading CpuTemperature, CpuPower, GpuTemperature, GpuPower, FanRpm;
    public readonly float? CpuSmoothed, GpuSmoothed;
    public readonly long Version, MonitorVersion;
    public readonly IReadOnlyList<int> FanLevels;
    public readonly bool GpuSleeping;
    public readonly bool CpuMonitorOpen, GpuMonitorOpen;
    public Telemetry(long version, long monitorVersion, Reading cpu, Reading cpuPower, Reading gpu,
      Reading gpuPower, Reading fan, float? cpuSmoothed, float? gpuSmoothed, int[] fanLevels = null,
      bool gpuSleeping = false, bool cpuMonitorOpen = false, bool gpuMonitorOpen = false) {
      Version = version; MonitorVersion = monitorVersion;
      CpuTemperature = cpu; CpuPower = cpuPower; GpuTemperature = gpu; GpuPower = gpuPower; FanRpm = fan;
      CpuSmoothed = cpuSmoothed; GpuSmoothed = gpuSmoothed;
      FanLevels = fanLevels == null ? null : Array.AsReadOnly((int[])fanLevels.Clone());
      GpuSleeping = gpuSleeping;
      CpuMonitorOpen = cpuMonitorOpen; GpuMonitorOpen = gpuMonitorOpen;
    }
    public static Telemetry Empty { get { return new Telemetry(0, 0, new Reading(null, 0), new Reading(null, 0),
      new Reading(null, 0), new Reading(null, 0), new Reading(null, 0), null, null); } }
  }
  // Open, switches, the complete sampling round and Close share one lifetime.
  internal sealed class PollingLifetime {
    private readonly object gate = new object();
    private bool busy, closing;
    private TaskCompletionSource<bool> drained;
    private Task closingTask;
    internal bool TryRun(Action action) {
      lock (gate) { if (closing || busy) return false; busy = true; }
      try { action(); return true; }
      finally { lock (gate) { busy = false; drained?.TrySetResult(true); } }
    }
    internal void Start(Action action) {
      lock (gate) { if (closing || busy) return; busy = true; }
      Task.Run(() => {
        try { action(); }
        finally { lock (gate) { busy = false; drained?.TrySetResult(true); } }
      });
    }
    internal Task CloseAsync(Action close) {
      lock (gate) {
        if (closingTask != null) return closingTask;
        closing = true;
        if (busy) drained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task wait = drained == null ? Task.CompletedTask : drained.Task;
        closingTask = CloseAfterAsync(wait, close);
        return closingTask;
      }
    }
    private static async Task CloseAfterAsync(Task wait, Action close) {
      await wait.ConfigureAwait(false);
      await Task.Run(close).ConfigureAwait(false);
    }
  }
  // At most one queued notification; a producer racing completion cannot lose the last frame.
  internal sealed class LatestPublisher<T> where T : class {
    private readonly object gate = new object();
    private readonly Action<Action> post;
    private readonly Action<T> consume;
    private T latest;
    private bool queued, stopped;
    internal LatestPublisher(Action<Action> post, Action<T> consume) { this.post = post; this.consume = consume; }
    internal void Publish(T value) {
      lock (gate) {
        if (stopped) return;
        latest = value;
        if (queued) return;
        queued = true;
      }
      post(Dispatch);
    }
    private void Dispatch() {
      T value;
      lock (gate) { value = stopped ? null : latest; latest = null; }
      try { if (value != null) consume(value); }
      finally {
        bool again;
        lock (gate) { again = !stopped && latest != null; if (!again) queued = false; }
        if (again) post(Dispatch);
      }
    }
    internal void Stop() { lock (gate) { stopped = true; latest = null; } }
  }
  internal sealed class RateGate {
    private readonly object gate = new object();
    private readonly IClock clock;
    private readonly int interval;
    private long next;
    private bool busy;
    internal RateGate(IClock clock, int interval) { this.clock = clock; this.interval = interval; }
    internal bool TryEnter() {
      lock (gate) {
        if (busy || clock.Milliseconds < next) return false;
        busy = true; next = clock.Milliseconds + interval; return true;
      }
    }
    internal void Leave() { lock (gate) busy = false; }
  }
}
