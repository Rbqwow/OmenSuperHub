using System;

namespace LibreHardwareMonitor.Hardware.Gpu
{
    internal enum NvidiaReadState { Unknown, Available, Sleeping, Unsupported, InvalidHandle, Unavailable }
    internal sealed class NvidiaReading
    {
        internal readonly float? Value;
        internal readonly bool Refresh;
        internal NvidiaReading(float? value, bool refresh = false) { Value = value; Refresh = refresh; }
    }
    internal sealed class NvidiaSample
    {
        internal NvidiaReadState State;
        internal float? Temperature, Power;
        internal bool Refresh;
    }
    internal sealed class NvidiaSampleSession
    {
        private bool invalidated;
        internal NvidiaSample Read(Func<int> state, Func<NvidiaReading> temperature, Func<NvidiaReading> power)
        {
            // A later OK response cannot make an invalidated (possibly reused) handle safe.
            // Only construction of a new GPU after enumeration starts a new sampling session.
            if (invalidated) return new NvidiaSample { State = NvidiaReadState.Unavailable };
            NvidiaSample sample = NvidiaSamplePolicy.Read(state, temperature, power);
            invalidated = sample.Refresh;
            return sample;
        }
    }
    internal static class NvidiaSamplePolicy
    {
        internal static NvidiaReadState Classify(int status)
        {
            switch (status)
            {
                case 0: return NvidiaReadState.Available;
                case -220: return NvidiaReadState.Sleeping;
                case -8: case -10: return NvidiaReadState.InvalidHandle;
                case -2: case -4: case -6: case -156: case -157: return NvidiaReadState.Unavailable;
                case -3: case -104: case -136: return NvidiaReadState.Unsupported;
                default: return NvidiaReadState.Unknown;
            }
        }
        internal static bool NeedsRefresh(NvidiaReadState state)
        {
            return state == NvidiaReadState.InvalidHandle || state == NvidiaReadState.Unavailable || state == NvidiaReadState.Unknown;
        }
        // Each call starts empty. Failure of one sensor cannot refresh or erase the other sample.
        internal static NvidiaSample Read(Func<int> state, Func<NvidiaReading> temperature, Func<NvidiaReading> power)
        {
            var result = new NvidiaSample();
            try { result.State = Classify(state()); }
            catch { result.State = NvidiaReadState.Unknown; }
            result.Refresh = NeedsRefresh(result.State);
            if (result.State != NvidiaReadState.Available && result.State != NvidiaReadState.Unsupported) return result;
            try
            {
                var reading = temperature();
                result.Temperature = Finite(reading.Value); result.Refresh |= reading.Refresh;
            }
            catch { result.Refresh = true; }
            try
            {
                var reading = power();
                result.Power = Finite(reading.Value); result.Refresh |= reading.Refresh;
            }
            catch { result.Refresh = true; }
            return result;
        }
        private static float? Finite(float? value)
        {
            return value.HasValue && !float.IsNaN(value.Value) && !float.IsInfinity(value.Value) ? value : null;
        }
    }
}
