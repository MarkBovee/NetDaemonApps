using System;
using System.Collections.Generic;
using System.Linq;

namespace NetDaemonApps.Models.Battery;

/// <summary>
/// Represents a complete battery charging schema with multiple periods
/// </summary>
public class ChargingSchema
{
    /// <summary>
    /// Gets or sets the list of charging periods
    /// </summary>
    public List<ChargingPeriod> Periods { get; set; } = new();

    /// <summary>
    /// Gets or sets when this schema was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// Gets or sets when this schema was last applied
    /// </summary>
    public DateTime? AppliedAt { get; set; }

    /// <summary>
    /// Gets or sets the schema generation reason/source
    /// </summary>
    public string Source { get; set; } = "Unknown";

    /// <summary>
    /// Checks if this schema is functionally equivalent to another schema
    /// </summary>
    /// <param name="other">The other schema to compare with</param>
    /// <returns>True if the schemas are equivalent</returns>
    public bool IsEquivalentTo(ChargingSchema? other)
    {
        if (other == null) return false;
        if (Periods.Count != other.Periods.Count) return false;

        // Sort periods by start time for comparison
        var thisSorted = Periods.OrderBy(p => p.StartTime).ToList();
        var otherSorted = other.Periods.OrderBy(p => p.StartTime).ToList();

        for (int i = 0; i < thisSorted.Count; i++)
        {
            var thisPeriod = thisSorted[i];
            var otherPeriod = otherSorted[i];

            if (thisPeriod.ChargeType != otherPeriod.ChargeType ||
                thisPeriod.StartTime != otherPeriod.StartTime ||
                thisPeriod.EndTime != otherPeriod.EndTime ||
                thisPeriod.PowerInWatts != otherPeriod.PowerInWatts)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Gets a string representation of the schema for logging
    /// </summary>
    public string ToLogString()
    {
        var periods = Periods.OrderBy(p => p.StartTime)
            .Select(p => $"{p.ChargeType} {p.StartTime:hh\\:mm}-{p.EndTime:hh\\:mm} @ {p.PowerInWatts}W");
        return string.Join(", ", periods);
    }

    /// <summary>
    /// Creates a copy of this schema
    /// </summary>
    public ChargingSchema Clone()
    {
        return new ChargingSchema
        {
            Periods = Periods.Select(p => new ChargingPeriod
            {
                ChargeType = p.ChargeType,
                StartTime = p.StartTime,
                EndTime = p.EndTime,
                PowerInWatts = p.PowerInWatts,
                Weekdays = p.Weekdays
            }).ToList(),
            CreatedAt = CreatedAt,
            AppliedAt = AppliedAt,
            Source = Source
        };
    }
}
