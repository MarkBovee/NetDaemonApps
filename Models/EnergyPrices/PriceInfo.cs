// -----------------------------------------------------------------------------
// Model representing electricity price information for a time period
// -----------------------------------------------------------------------------

namespace NetDaemonApps.Models.EnergyPrices
{
    using System.Globalization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// The price info class represents a price entry for a specific time.
    /// </summary>
    public class PriceInfo
    {
        /// <summary>
        /// Gets or sets the value of the time.
        /// </summary>
        [JsonPropertyName("start")]
        public required string Time { get; set; }

        /// <summary>
        /// Gets or sets the value of the price.
        /// </summary>
        [JsonPropertyName("value")]
        public double Price { get; set; }

        /// <summary>
        /// Parses the time string and returns the corresponding DateTime value.
        /// Energy prices are always in Dutch time (CET/CEST), so we convert to that timezone.
        /// </summary>
        /// <returns>The parsed DateTime value in Dutch timezone.</returns>
        public DateTime GetTimeValue()
        {
            try
            {
                // Parse the timestamp string to a DateTimeOffset using a format provider
                DateTimeOffset dateTimeOffset = DateTimeOffset.ParseExact(Time, "yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);

                // Convert to Dutch timezone (CET/CEST) since energy prices are always in Dutch time
                // This ensures consistent time interpretation regardless of system timezone
                TimeZoneInfo dutchTimeZone = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
                DateTime dutchTime = TimeZoneInfo.ConvertTime(dateTimeOffset, dutchTimeZone).DateTime;

                return dutchTime;
            }
            catch (FormatException)
            {
                // Return today's date if parsing fails
                return DateTime.Today;
            }
        }
    }
}
