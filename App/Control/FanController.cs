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
    public readonly int? Fan1Rpm;
    public readonly int? Fan2Rpm;
    public readonly bool Protecting;
    public readonly bool CanReportRecovery;
    internal FanDecision(string mode, int? fan1Rpm, int? fan2Rpm, bool protecting, bool canReportRecovery = false) {
      Mode = mode; Fan1Rpm = fan1Rpm; Fan2Rpm = fan2Rpm;
      Protecting = protecting; CanReportRecovery = canReportRecovery;
    }
    internal bool HasSpeed { get { return Fan1Rpm.HasValue && Fan2Rpm.HasValue; } }
  }
  internal sealed class FanController {
    private readonly IClock clock;
    private long generation = -1, invalidSince;
    private bool protecting;
    internal FanController(IClock clock) { this.clock = clock; }
    internal FanDecision Evaluate(long generation, string userMode, int? fixedRpm, string cleaning,
      Reading cpu, float? cpuSmoothed, Reading gpu, float? gpuSmoothed,
      FanCurve cpuFan1Curve, FanCurve cpuFan2Curve, FanCurve gpuFan1Curve, FanCurve gpuFan2Curve) {
      long now = clock.Milliseconds;
      if (this.generation != generation) {
        this.generation = generation; invalidSince = now; protecting = false;
      }
      int? fan1Rpm = null;
      int? fan2Rpm = null;
      if (cpu != null && cpu.Fresh(now) && cpuSmoothed.HasValue) {
        fan1Rpm = cpuFan1Curve.Speed(cpuSmoothed.Value);
        fan2Rpm = cpuFan2Curve.Speed(cpuSmoothed.Value);
      }
      if (gpu != null && gpu.Fresh(now) && gpuSmoothed.HasValue) {
        int? gpuFan1Rpm = gpuFan1Curve.Speed(gpuSmoothed.Value);
        int? gpuFan2Rpm = gpuFan2Curve.Speed(gpuSmoothed.Value);
        if (gpuFan1Rpm.HasValue) fan1Rpm = Math.Max(fan1Rpm ?? 0, gpuFan1Rpm.Value);
        if (gpuFan2Rpm.HasValue) fan2Rpm = Math.Max(fan2Rpm ?? 0, gpuFan2Rpm.Value);
      }
      bool hasRpm = fan1Rpm.HasValue && fan2Rpm.HasValue;
      if (userMode == "auto") {
        if (hasRpm) {
          invalidSince = -1; protecting = false;
        } else {
          if (invalidSince < 0) {
            long lost = Math.Max(LostAt(cpu, now), LostAt(gpu, now));
            invalidSince = lost < 0 ? now : lost;
          }
          protecting = now - invalidSince >= 5000;
        }
      } else protecting = false;
      if (protecting) return new FanDecision("max", null, null, true);
      if (cleaning != null) return new FanDecision(cleaning, null, null, false, userMode == "auto" && hasRpm);
      if (userMode == "max") return new FanDecision("max", null, null, false);
      // Until a usable sample arrives, keep the existing cooling state. Turning off
      // maximum cooling without replacement speeds can leave the EC at zero RPM.
      if (userMode == "auto" && !hasRpm) return new FanDecision("hold", null, null, false);
      return new FanDecision("speed", userMode == "auto" ? fan1Rpm : fixedRpm,
        userMode == "auto" ? fan2Rpm : fixedRpm, false, userMode == "auto" && hasRpm);
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
