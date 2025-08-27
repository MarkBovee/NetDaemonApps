// -----------------------------------------------------------------------------
// SAJ Power Battery User Mode Enumeration
// -----------------------------------------------------------------------------

namespace NetDaemonApps.Models.Battery
{
    /// <summary>
    /// Represents the available user modes for the SAJ Power Battery system.
    /// These correspond to the userModeName values returned by the SAJ API.
    /// </summary>
    public enum BatteryUserMode
    {
        /// <summary>
        /// Unknown or unrecognized user mode.
        /// </summary>
        Unknown = 0,

        /// <summary>
        /// EMS (Energy Management System) Mode - Automated energy optimization.
        /// The system automatically manages charging and discharging based on schedules and algorithms.
        /// </summary>
        EmsMode = 1,

        /// <summary>
        /// Self-Use Mode - Prioritizes self-consumption of solar energy.
        /// Battery charges from solar during the day and discharges when solar is insufficient.
        /// </summary>
        SelfUseMode = 2,

        /// <summary>
        /// Time-of-Use Mode - Optimizes based on electricity pricing.
        /// Charges during low-cost periods and discharges during high-cost periods.
        /// </summary>
        TimeOfUseMode = 3,

        /// <summary>
        /// Backup Mode - Maintains battery charge for emergency backup power.
        /// Minimizes discharge to ensure power availability during outages.
        /// </summary>
        BackupMode = 4,

        /// <summary>
        /// Feed-in Priority Mode - Maximizes energy export to the grid.
        /// Prioritizes selling solar energy over battery charging.
        /// </summary>
        FeedInPriorityMode = 5,

        /// <summary>
        /// Manual Mode - Manual control of charging and discharging.
        /// User has direct control over battery operations.
        /// </summary>
        ManualMode = 6,

        /// <summary>
        /// Off-Grid Mode - Operates independently from the electrical grid.
        /// Battery and solar provide all power without grid connection.
        /// </summary>
        OffGridMode = 7
    }

    /// <summary>
    /// Extension methods for BatteryUserMode enum.
    /// </summary>
    public static class BatteryUserModeExtensions
    {
        /// <summary>
        /// Converts a string user mode name from the SAJ API to the corresponding enum value.
        /// </summary>
        /// <param name="userModeName">The user mode name string from the API</param>
        /// <returns>The corresponding BatteryUserMode enum value</returns>
        public static BatteryUserMode FromApiString(string? userModeName)
        {
            if (string.IsNullOrWhiteSpace(userModeName))
                return BatteryUserMode.Unknown;

            var name = userModeName.Trim();
            // First try strict matches (case-insensitive)
            switch (name.ToLowerInvariant())
            {
                case "ems mode":
                case "ems":
                case "ems-mode":
                    return BatteryUserMode.EmsMode;
                case "self-use mode":
                case "self use mode":
                case "self-use":
                case "self use":
                    return BatteryUserMode.SelfUseMode;
                case "time-of-use mode":
                case "time of use mode":
                case "time-of-use":
                case "time of use":
                case "tou mode":
                case "tou":
                    return BatteryUserMode.TimeOfUseMode;
                case "backup mode":
                case "backup":
                    return BatteryUserMode.BackupMode;
                case "feed-in priority mode":
                case "feed in priority mode":
                case "feed-in priority":
                case "feed in priority":
                    return BatteryUserMode.FeedInPriorityMode;
                case "manual mode":
                case "manual":
                    return BatteryUserMode.ManualMode;
                case "off-grid mode":
                case "off grid mode":
                case "off-grid":
                case "off grid":
                    return BatteryUserMode.OffGridMode;
            }

            // Fallback: heuristic contains checks
            var lower = name.ToLowerInvariant();
            if (lower.Contains("ems")) return BatteryUserMode.EmsMode;
            if (lower.Contains("self") && lower.Contains("use")) return BatteryUserMode.SelfUseMode;
            if (lower.Contains("time") && lower.Contains("use")) return BatteryUserMode.TimeOfUseMode;
            if (lower.Contains("backup")) return BatteryUserMode.BackupMode;
            if (lower.Contains("feed") && lower.Contains("priority")) return BatteryUserMode.FeedInPriorityMode;
            if (lower.Contains("manual")) return BatteryUserMode.ManualMode;
            if (lower.Contains("off") && lower.Contains("grid")) return BatteryUserMode.OffGridMode;

            return BatteryUserMode.Unknown;
        }

        /// <summary>
        /// Converts the enum value back to the API string format.
        /// </summary>
        /// <param name="mode">The BatteryUserMode enum value</param>
        /// <returns>The corresponding API string representation</returns>
        public static string ToApiString(this BatteryUserMode mode)
        {
            return mode switch
            {
                BatteryUserMode.EmsMode => "EMS Mode",
                BatteryUserMode.SelfUseMode => "Self-Use Mode",
                BatteryUserMode.TimeOfUseMode => "Time-of-Use Mode",
                BatteryUserMode.BackupMode => "Backup Mode",
                BatteryUserMode.FeedInPriorityMode => "Feed-in Priority Mode",
                BatteryUserMode.ManualMode => "Manual Mode",
                BatteryUserMode.OffGridMode => "Off-Grid Mode",
                _ => "Unknown"
            };
        }

        /// <summary>
        /// Gets a user-friendly description of the battery user mode.
        /// </summary>
        /// <param name="mode">The BatteryUserMode enum value</param>
        /// <returns>A descriptive string explaining the mode</returns>
        public static string GetDescription(this BatteryUserMode mode)
        {
            return mode switch
            {
                BatteryUserMode.EmsMode => "Automated energy optimization with intelligent scheduling",
                BatteryUserMode.SelfUseMode => "Maximizes self-consumption of solar energy",
                BatteryUserMode.TimeOfUseMode => "Optimizes charging/discharging based on electricity pricing",
                BatteryUserMode.BackupMode => "Maintains charge for emergency backup power",
                BatteryUserMode.FeedInPriorityMode => "Prioritizes selling energy to the grid",
                BatteryUserMode.ManualMode => "Manual control of battery operations",
                BatteryUserMode.OffGridMode => "Independent operation without grid connection",
                _ => "Unknown or unrecognized mode"
            };
        }

        /// <summary>
        /// Determines if the mode allows automated charging/discharging schedules.
        /// </summary>
        /// <param name="mode">The BatteryUserMode enum value</param>
        /// <returns>True if the mode supports automated scheduling</returns>
        public static bool SupportsAutomatedScheduling(this BatteryUserMode mode)
        {
            return mode is BatteryUserMode.EmsMode or BatteryUserMode.TimeOfUseMode;
        }
    }
}
