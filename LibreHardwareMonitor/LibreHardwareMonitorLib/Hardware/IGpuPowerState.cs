namespace LibreHardwareMonitor.Hardware;

/// <summary>Result of an explicit device power-state query, separate from temperature and power samples.</summary>
public enum GpuPowerState
{
    /// <summary>No reliable state result.</summary>
    Unknown,
    /// <summary>The state query succeeded.</summary>
    Available,
    /// <summary>The driver explicitly reports that the GPU is not powered.</summary>
    Sleeping,
    /// <summary>This driver does not support the state query; sensor reads may still succeed.</summary>
    Unsupported,
    /// <summary>Handles must be enumerated again.</summary>
    InvalidHandle,
    /// <summary>The driver or device is unavailable.</summary>
    Unavailable
}

/// <summary>Optional device state information for GPU monitoring.</summary>
public interface IGpuPowerState
{
    /// <summary>Result from the last update.</summary>
    GpuPowerState PowerState { get; }
}
