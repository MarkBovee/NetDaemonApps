namespace NetDaemonApps.Models.Battery;

public record ScheduleEntry(BatteryChargeType BatteryChargeType, string StartTime, string EndTime, int Power, bool[] Days);
