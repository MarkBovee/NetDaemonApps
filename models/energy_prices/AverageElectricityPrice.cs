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
        /// Gets or sets the value of the state class
        /// </summary>
        [JsonPropertyName("state_class")]
        public string? StateClass { get; set; }

        /// <summary>
        /// Gets or sets the value of the prices today
        /// </summary>
        [JsonPropertyName("prices_today")]
        public required List<PriceInfo> PricesToday { get; set; }

        /// <summary>
        /// Gets or sets the value of the prices tomorrow
        /// </summary>
        [JsonPropertyName("prices_tomorrow")]
        public required List<PriceInfo> PricesTomorrow { get; set; }

        /// <summary>
        /// Gets or sets the value of the prices
        /// </summary>
        [JsonPropertyName("prices")]
        public required List<PriceInfo> Prices { get; set; }

        /// <summary>
        /// Gets or sets the value of the unit of measurement
        /// </summary>
        [JsonPropertyName("unit_of_measurement")]
        public string? UnitOfMeasurement { get; set; }

        /// <summary>
        /// Gets or sets the value of the attribution
        /// </summary>
        [JsonPropertyName("attribution")]
        public string? Attribution { get; set; }

        /// <summary>
        /// Gets or sets the value of the device class
        /// </summary>
        [JsonPropertyName("device_class")]
        public string? DeviceClass { get; set; }

        /// <summary>
        /// Gets or sets the value of the icon
        /// </summary>
        [JsonPropertyName("icon")]
        public string? Icon { get; set; }

        /// <summary>
        /// Gets or sets the value of the friendly name
        /// </summary>
        [JsonPropertyName("friendly_name")]
        public string? FriendlyName { get; set; }
    }
}