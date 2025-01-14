using System.Globalization;
using System.Text.Json.Serialization;

namespace NetDaemonApps.models.energy_prices
{
    /// <summary>
    /// The price info class
    /// </summary>
    public class PriceInfo
    {
        /// <summary>
        /// Gets or sets the value of the time
        /// </summary>
        [JsonPropertyName("start")]
        public required string Time { get; set; }

        /// <summary>
        /// Gets or sets the value of the price
        /// </summary>
        [JsonPropertyName("value")]
        public double Price { get; set; }

        /// <summary>
        /// Gets the time value
        /// </summary>
        /// <returns>The date time</returns>
        public DateTime GetTimeValue()
        {
            try
            {
                // Parse the timestamp string to a DateTimeOffset using a format provider
                DateTimeOffset dateTimeOffset = DateTimeOffset.ParseExact(Time, "yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);

                // Convert DateTimeOffset to DateTime, considering the local time zone
                DateTime dateTime = dateTimeOffset.LocalDateTime;

                //// Check for an error in the date, this can happen when we dont have any new data
                //if (dateTime.Date < DateTime.Today.Date)
                //{
                //    // Calculate the difference in days
                //    int daysDifference = (DateTime.Today.Date - dateTime.Date).Days;

                //    // Add it to the current date
                //    dateTime = dateTime.AddDays(daysDifference);
                //}

                return dateTime;
            }
            catch (FormatException)
            {
                // Handle the case where the timestamp format is invalid
                throw new FormatException("Time is not in a valid format");
            }
        }
    }
}