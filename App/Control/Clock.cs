using System.Diagnostics;

namespace OmenSuperHub.Control {
  internal interface IClock { long Milliseconds { get; } }
  internal sealed class MonotonicClock : IClock {
    private readonly Stopwatch watch = Stopwatch.StartNew();
    public long Milliseconds { get { return watch.ElapsedMilliseconds; } }
  }
}
