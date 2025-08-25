using System;

namespace NetDaemonApps.Models.Battery;

/// <summary>
/// Represents a single charging or discharging period for the battery schedule
/// </summary>
public class ChargingPeriod
{
    /// <summary>
    /// Gets or sets the charge type (Charge or Discharge)
    /// </summary>
    public BatteryChargeType ChargeType { get; set; }

    /// <summary>
    /// Gets or sets the start time
    /// </summary>
    public TimeSpan StartTime { get; set; }

    /// <summary>
    /// Gets or sets the end time
    /// </summary>
    public TimeSpan EndTime { get; set; }

    /// <summary>
    /// Gets or sets the power in watts
    /// </summary>
    public int PowerInWatts { get; set; }

    /// <summary>
    /// Gets or sets which weekdays this period is active (default: all days)
    /// </summary>
    public string Weekdays { get; set; } = "1,1,1,1,1,1,1";

    /// <summary>
    /// Converts the period to the API format: "HH:mm|HH:mm|power_weekdays"
    /// </summary>
    public string ToApiFormat()
    {
        return $"{StartTime:hh\\:mm}|{EndTime:hh\\:mm}|{PowerInWatts}_{Weekdays}";
    }
}