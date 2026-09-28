using System;

namespace OmenSuperHub.Control {
  // Only used by the UI thread. Nestable and exception safe.
  internal sealed class UpdateScope {
    private int depth;
    public bool Active { get { return depth != 0; } }
    public IDisposable Enter() { depth++; return new Lease(this); }
    private sealed class Lease : IDisposable {
      private UpdateScope owner;
      public Lease(UpdateScope owner) { this.owner = owner; }
      public void Dispose() { if (owner != null) { owner.depth--; owner = null; } }
    }
  }
}
