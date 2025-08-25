namespace NetDaemonApps.Models.Battery;

/// <summary>
/// The charging moment class
/// </summary>
public class ChargingMoment
{
    /// <summary>
    /// Gets or sets the value of the charge type
    /// </summary>
    public BatteryChargeType ChargeType { get; set; }

    /// <summary>
    /// Gets or sets the value of the start time
    /// </summary>
    public TimeSpan StartTime { get; set; }

    /// <summary>
    /// Gets or sets the value of the end time
    /// </summary>
    public TimeSpan EndTime { get; set; }

    /// <summary>
    /// Gets or sets the value of the power in watts
    /// </summary>
    public int PowerInWatts { get; set; }
}
