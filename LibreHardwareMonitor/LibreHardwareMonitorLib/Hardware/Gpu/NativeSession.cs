using System;

namespace LibreHardwareMonitor.Hardware.Gpu
{
    // The lock covers initialization, every native access and final release.
    // An application using another NVAPI wrapper owns its own reference.
    internal sealed class NativeSession
    {
        internal readonly object Gate = new object();
        private readonly Func<bool> initialize;
        private readonly Action close;
        private int users;
        private bool initialized;
        internal long Generation { get; private set; }

        internal NativeSession(Func<bool> initialize, Action close)
        {
            this.initialize = initialize;
            this.close = close;
        }

        internal void AddUser() { lock (Gate) users++; }
        internal bool EnsureInitialized()
        {
            lock (Gate)
            {
                if (users == 0) return false;
                if (!initialized)
                {
                    try { initialized = initialize(); }
                    catch
                    {
                        try { Reset(); } catch { }
                        throw;
                    }
                    if (!initialized) Reset();
                }
                return initialized;
            }
        }
        internal void Reset()
        {
            lock (Gate)
            {
                try { close(); }
                finally { initialized = false; Generation++; }
            }
        }
        internal void RemoveUser()
        {
            lock (Gate)
            {
                if (users == 0) return;
                if (--users == 0) Reset();
            }
        }
    }

    internal sealed class TopologySchedule
    {
        private int failures;
        internal long Due { get; private set; }
        internal long Earliest { get; private set; }
        internal void Completed(long now, bool success)
        {
            failures = success ? 0 : Math.Min(3, failures + 1);
            int delay = success ? 30000 : failures == 1 ? 5000 : failures == 2 ? 10000 : 30000;
            Earliest = now + (success ? 5000 : delay);
            Due = now + delay;
        }
        internal void Request(long now) { Due = Math.Min(Due, Math.Max(now, Earliest)); }
    }
}
