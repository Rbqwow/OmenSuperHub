using System;
using System.Collections.Generic;
using System.Linq;

namespace OmenSuperHub.Control {
  internal sealed class FanCurve {
    private readonly KeyValuePair<float, int>[] points;
    internal FanCurve(IEnumerable<KeyValuePair<float, int>> points) { this.points = points.OrderBy(p => p.Key).ToArray(); }
    internal int? Speed(float value) {
      if (points.Length == 0) return null;
      var low = points[0];
      foreach (var high in points) {
        if (value <= high.Key) return low.Key == high.Key ? low.Value :
          (int)(low.Value + (high.Value - low.Value) * (value - low.Key) / (high.Key - low.Key));
        low = high;
      }
      return low.Value;
    }
  }
  internal sealed class FanDecision {
    public readonly string Mode;
    public readonly int? Rpm;
    public readonly bool Protecting;
    public readonly bool CanReportRecovery;
    internal FanDecision(string mode, int? rpm, bool protecting, bool canReportRecovery = false) {
      Mode = mode; Rpm = rpm; Protecting = protecting; CanReportRecovery = canReportRecovery;
    }
  }
  internal sealed class FanController {
    private readonly IClock clock;
    private long generation = -1, invalidSince;
    private bool protecting;
    internal FanController(IClock clock) { this.clock = clock; }
    internal FanDecision Evaluate(long generation, string userMode, int? fixedRpm, string cleaning,
      Reading cpu, float? cpuSmoothed, Reading gpu, float? gpuSmoothed, FanCurve cpuCurve, FanCurve gpuCurve) {
      long now = clock.Milliseconds;
      if (this.generation != generation) {
        this.generation = generation; invalidSince = now; protecting = false;
      }
      int? rpm = null;
      if (cpu != null && cpu.Fresh(now) && cpuSmoothed.HasValue) rpm = cpuCurve.Speed(cpuSmoothed.Value);
      if (gpu != null && gpu.Fresh(now) && gpuSmoothed.HasValue) {
        int? gpuRpm = gpuCurve.Speed(gpuSmoothed.Value);
        if (gpuRpm.HasValue) rpm = Math.Max(rpm ?? 0, gpuRpm.Value);
      }
      if (userMode == "auto") {
        if (rpm.HasValue) {
          invalidSince = -1; protecting = false;
        } else {
          if (invalidSince < 0) {
            long lost = Math.Max(LostAt(cpu, now), LostAt(gpu, now));
            invalidSince = lost < 0 ? now : lost;
          }
          protecting = now - invalidSince >= 5000;
        }
      } else protecting = false;
      if (protecting) return new FanDecision("max", null, true);
      if (cleaning != null) return new FanDecision(cleaning, null, false, userMode == "auto" && rpm.HasValue);
      if (userMode == "max") return new FanDecision("max", null, false);
      // Until a usable sample arrives, keep the existing cooling state. Turning off
      // maximum cooling without a replacement speed can leave the EC at zero RPM.
      if (userMode == "auto" && !rpm.HasValue) return new FanDecision("hold", null, false);
      return new FanDecision("speed", userMode == "auto" ? rpm : fixedRpm, false, userMode == "auto" && rpm.HasValue);
    }
    private static long LostAt(Reading reading, long now) {
      if (reading == null) return -1;
      // Valid data with a missing curve becomes unusable when the control check observes it.
      if (reading.Fresh(now)) return now;
      if (reading.Value.HasValue) return reading.SampledAt + Reading.MaxAge;
      return reading.LastSuccess >= 0 ? Math.Min(reading.InvalidSince, reading.LastSuccess + Reading.MaxAge) : reading.InvalidSince;
    }
    internal static bool FixedTooSlow(Reading measured, long now, int fixedRpm, int platformMax) {
      float actual = measured != null && measured.Fresh(now) ? measured.Value.Value : fixedRpm;
      return actual < platformMax * 0.8;
    }
  }
}
