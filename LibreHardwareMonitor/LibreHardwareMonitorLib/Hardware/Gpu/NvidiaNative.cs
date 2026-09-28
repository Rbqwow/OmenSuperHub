using LibreHardwareMonitor.Interop;

namespace LibreHardwareMonitor.Hardware.Gpu;

internal static class NvidiaNative
{
    internal static readonly NativeSession Session = new(
        () => { NvApi.Initialize(); return NvApi.IsAvailable; },
        () => { try { NvidiaML.Close(); } finally { NvApi.Close(); } });
}
