using System;

namespace OmenSuperHub.Control {
  internal static class SensorRules {
    internal static bool Finite(float? value) {
      return value.HasValue && !float.IsNaN(value.Value) && !float.IsInfinity(value.Value);
    }
    internal static float? FitAmbient(float? value) {
      if (!Finite(value) || value <= 1) return null;
      return value < 25 ? value : value * 1.2f - 5;
    }
    internal static float? CpuTemperature(float? real, bool fallbackSupported, Func<float?> ambient, out bool estimated) {
      estimated = false;
      if (Finite(real)) return real;
      if (!fallbackSupported) return null;
      float? fitted = FitAmbient(ambient());
      estimated = fitted.HasValue;
      return fitted;
    }
    internal static int PollingInterval(bool onBattery, bool highRefresh) { return onBattery ? 1000 : highRefresh ? 250 : 1000; }
    internal static bool NeedsCpuMonitor(string fanMode, bool cpu, bool gpu) { return fanMode == "auto" && !cpu && !gpu; }
  }
}
