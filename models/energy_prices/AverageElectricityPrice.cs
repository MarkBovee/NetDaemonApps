// Root myDeserializedClass = JsonConvert.DeserializeObject<Root>(myJsonResponse);
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace NetDaemonApps.models.energy_prices
{
    /// <summary>
    /// The average electricity class
    /// </summary>
    public class AverageElectricityPrice
    {
        /// <summary>
        /// Gets or sets the value of the prices today
        /// </summary>
        [JsonPropertyName("raw_today")]
        public required List<PriceInfo> PricesToday { get; set; }

        /// <summary>
        /// Gets or sets the value of the prices tomorrow
        /// </summary>
        [JsonPropertyName("raw_tomorrow")]
        public required List<PriceInfo> PricesTomorrow { get; set; }
    }
}