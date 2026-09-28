namespace OmenSuperHub.Control {
  internal sealed class PowerChange {
    internal readonly long Version, ResumeVersion;
    internal PowerChange(long version, long resumeVersion) { Version = version; ResumeVersion = resumeVersion; }
  }
  // Keep a resume event when a subsequent status change replaces its queued UI frame.
  internal sealed class PowerUpdates {
    private readonly object gate = new object();
    private long version, resumeVersion, appliedVersion, appliedResume;
    internal PowerChange Offer(bool resume) {
      lock (gate) {
        version++;
        if (resume) resumeVersion = version;
        return new PowerChange(version, resumeVersion);
      }
    }
    internal bool Consume(PowerChange change, out bool resume) {
      lock (gate) {
        resume = false;
        if (change.Version <= appliedVersion) return false;
        resume = change.ResumeVersion > appliedResume;
        appliedVersion = change.Version; appliedResume = change.ResumeVersion;
        return true;
      }
    }
  }
}
