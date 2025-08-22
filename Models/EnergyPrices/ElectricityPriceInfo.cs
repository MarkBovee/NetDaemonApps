// -----------------------------------------------------------------------------
// Model representing average electricity prices for today and tomorrow
// -----------------------------------------------------------------------------

namespace NetDaemonApps.Models.EnergyPrices
{
    using System.Collections.Generic;
    using System.Text.Json.Serialization;

    /// <summary>
    /// The average electricity price class holds price lists for today and tomorrow.
    /// </summary>
    public class ElectricityPriceInfo
    {
        /// <summary>
        /// Gets or sets the value of the prices today.
        /// </summary>
        [JsonPropertyName("raw_today")]
        public required List<PriceInfo> PricesToday { get; set; }

        /// <summary>
        /// Gets or sets the value of the prices tomorrow.
        /// </summary>
        [JsonPropertyName("raw_tomorrow")]
        public required List<PriceInfo> PricesTomorrow { get; set; }
    }
}
