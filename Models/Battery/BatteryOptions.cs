// filepath: e:\Projects\Personal\NetDaemonApps\Models\Battery\BatteryOptions.cs
namespace NetDaemonApps.Models.Battery
{
    /// <summary>
    /// Strongly-typed configuration for the Battery app and SAJ Power API.
    /// Bind from configuration section "Battery" in appsettings.*.
    /// </summary>
    /// <remarks>
    /// Time values use local time. Power values are in Watts (W).
    /// In development, set <see cref="SimulationMode"/> to true to avoid writing schedules to the SAJ API.
    /// </remarks>
    public sealed class BatteryOptions
    {
        // SAJ API
        /// <summary>
        /// SAJ/Elekeeper account username used to authenticate against the remote API.
        /// Required for real schedule writes when <see cref="SimulationMode"/> is false.
        /// </summary>
        public string? Username { get; set; }

        /// <summary>
        /// SAJ/Elekeeper account password used during authentication with the remote API.
        /// Treat as a secret. Prefer providing via user secrets, environment variables, or a secure store.
        /// </summary>
        public string? Password { get; set; }

        /// <summary>
        /// The device serial number (SN) of your inverter/battery controller as recognized by the SAJ platform.
        /// The API requires this identifier when saving/clearing schedules.
        /// </summary>
        public string? DeviceSerialNumber { get; set; }

        /// <summary>
        /// Base URL for the SAJ Power (Elekeeper) API. Override only if SAJ changes their endpoint or for testing.
        /// Default: https://eop.saj-electric.com
        /// </summary>
        public string? BaseUrl { get; set; } = "https://eop.saj-electric.com";

        // Behavior
        /// <summary>
        /// When true, the app simulates schedule application: the schedule is computed and logged, but no API write is performed.
        /// Use this in development or dry-run environments. When false, schedules are written to the SAJ API.
        /// Note: Simulation is also automatically enabled when a debugger is attached regardless of this setting.
        /// </summary>
        public bool SimulationMode { get; set; } = false;

        // Timing
        /// <summary>
        /// Number of minutes before a charge/discharge period when the EMS (energy management system) is turned off.
        /// Gives the battery exclusive control during the scheduled window. Typical range: 0–10 minutes.
        /// </summary>
        public int EmsPrepMinutesBefore { get; set; } = 5;

        /// <summary>
        /// Number of minutes after a charge/discharge period when the EMS should be turned back on.
        /// A small delay can prevent flapping between adjacent periods. Typical range: 0–5 minutes.
        /// </summary>
        public int EmsRestoreMinutesAfter { get; set; } = 1;

        /// <summary>
        /// Hours before the lowest-price charge block to perform the "morning checkpoint".
        /// If state-of-charge (SOC) is sufficiently high, a short discharge can be scheduled before the charge window.
        /// </summary>
        public int MorningCheckOffsetHours { get; set; } = 2;

        /// <summary>
        /// Start hour (0–23) of the morning window considered for potential morning discharge.
        /// Default is 6 (06:00). Must be less than <see cref="MorningWindowEndHour"/>.
        /// </summary>
        public int MorningWindowStartHour { get; set; } = 6;

        /// <summary>
        /// End hour (0–23, exclusive) of the morning window considered for potential morning discharge.
        /// Default is 12 (12:00). Must be greater than <see cref="MorningWindowStartHour"/>.
        /// </summary>
        public int MorningWindowEndHour { get; set; } = 12;

        /// <summary>
        /// Hour (0–23) at which the "evening" price window begins for selecting the evening discharge period.
        /// Default is 17 (17:00). Used to avoid picking midday peaks as the evening highest price.
        /// </summary>
        public int EveningThresholdHour { get; set; } = 17;

        // Power limits and device capabilities
        /// <summary>
        /// Maximum inverter charge/discharge power in Watts (hardware capability). Used for clamping and charge-time estimates.
        /// Default: 8000 W.
        /// </summary>
        public int MaxInverterPowerW { get; set; } = 8000;

        /// <summary>
        /// Total battery capacity in Watt-hours (Wh). Used to estimate time to full charge from current SOC.
        /// Default: 25000 Wh (25 kWh).
        /// </summary>
        public int MaxBatteryCapacityWh { get; set; } = 25000;

        /// <summary>
        /// Default charging power (in Watts) used for charge periods that the app creates.
        /// This value is clamped to the inverter's maximum capability at runtime.
        /// </summary>
        public int DefaultChargePowerW { get; set; } = 8000;

        /// <summary>
        /// Default discharging power (in Watts) used for discharge periods that the app creates.
        /// This value is clamped to the inverter's maximum capability at runtime.
        /// </summary>
        public int DefaultDischargePowerW { get; set; } = 8000;

        /// <summary>
        /// Minimum total charge time (in minutes) to keep when trimming/rounding charge schedule based on SOC calculations.
        /// Prevents over-trimming due to rounding and ensures a small top-up is still scheduled if needed (when SOC < 100%).
        /// Default: 10 minutes.
        /// </summary>
        public int MinChargeBufferMinutes { get; set; } = 10;
    }
}
