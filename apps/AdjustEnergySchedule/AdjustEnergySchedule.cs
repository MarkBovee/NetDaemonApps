using HomeAssistantGenerated;
using NetDaemon.Extensions.Scheduler;
using NetDaemonApps.models.energy_prices;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;

namespace NetDaemonApps.apps.AdjustPowerSchedule
{
    /// <summary>
    /// Adjust the energy appliances schedule based on the power prices
    /// </summary>
    [NetDaemonApp]
    public class AdjustEnergySchedule
    {
        /// <summary>
        /// The ha
        /// </summary>
        private readonly IHaContext _ha;

        /// <summary>
        /// The logger
        /// </summary>
        private readonly ILogger<AdjustEnergySchedule> _logger;

        /// <summary>
        /// The services
        /// </summary>
        private readonly Services _services;

        /// <summary>
        /// The entities
        /// </summary>
        private readonly Entities _entities;

        /// <summary>
        /// The prices today
        /// </summary>
        private IDictionary<DateTime, double>? _pricesToday;

        /// <summary>
        /// The prices tommorow
        /// </summary>
        private IDictionary<DateTime, double>? _pricesTommorow;

        /// <summary>
        /// The is heater on indication
        /// </summary>
        private bool _isHeaterOn;

        /// <summary>
        /// The is legionella protection on indication
        /// </summary>
        private bool _isLegionellaProtectionOn;

        /// <summary>
        /// Initializes a new instance of the <see cref="AdjustEnergySchedule"/> class
        /// </summary>
        /// <param name="ha">The ha</param>
        /// <param name="scheduler">The scheduler</param>
        /// <param name="logger">The logger</param>
        public AdjustEnergySchedule(IHaContext ha, INetDaemonScheduler scheduler, ILogger<AdjustEnergySchedule> logger)
        {
            _ha = ha;
            _logger = logger;

            // Read the Home assistant services and entities
            _services = new Services(ha);
            _entities = new Entities(ha);

            if (Debugger.IsAttached)
            {
                // Run once
                RunChecks();
            }
            else
            {
                // Application started
                _services.PersistentNotification.Create(message: "Succesfully started!", title: "Energy schedule assistant");
                _logger.LogInformation("Succesfully started Energy schedule assistant");

                // Run every 5 minutes
                scheduler.RunEvery(TimeSpan.FromMinutes(5), RunChecks);
            }
        }

        /// <summary>
        /// Runs the checks
        /// </summary>
        private void RunChecks()
        {
            // Get the current power prices
            GetPrices();

            // Set the heating schedule for the heatpump
            SetHeatingSchedule();

            // Set the legionella protection schedule for the heatpump
            SetLegionellaProtection();

            // Disable the dishwasher if the price is too high

            // Disable the washing machine if the price is too high

            // Disable the dryer if the price is too high

            // Set the car charging schedule
        }

        /// <summary>
        /// Sets the heating schedule
        /// </summary>
        private void SetHeatingSchedule()
        {
            // Check if the prices are available
            if (_pricesToday == null)
            {
                return;
            }

            // Get the heat pump warm water heater entity
            var heater = _entities.WaterHeater.OurHomeDomesticHotWater0;
            if (heater == null)
            {
                _logger.LogWarning("Hot water heater not found");
                return;
            }

            // Get the start time for the lowest price after 11:00
            var startTime = _pricesToday.Where(p => p.Key.TimeOfDay > TimeSpan.FromHours(11)).OrderBy(p => p.Value).First().Key;
            var endTime = startTime.AddHours(2);

            // Check if the heater is off and the current timestamp is within the schedule
            var timeStamp = DateTime.Now;
            if (_isHeaterOn == false && timeStamp.TimeOfDay >= startTime.TimeOfDay && timeStamp.TimeOfDay <= endTime.TimeOfDay)
            {
                _logger.LogInformation($"Started water heating program");
                _logger.LogInformation($"Start time: {startTime}, End time: {endTime}"); ;

                heater.SetOperationMode("Manual");
                heater.SetTemperature(58);
                heater.TurnOn();

                _ha.SendEvent("Energy schedule assistant", new { heater = "On" });

                _isHeaterOn = true;
            }
            else
            {
                heater.SetTemperature(55);
                heater.TurnOff();

                _ha.SendEvent("Energy schedule assistant", new { heater = "Off" });

                _isHeaterOn = false;
            }
        }

        /// <summary>
        /// Sets the legionella protection
        /// </summary>
        private void SetLegionellaProtection()
        {
            // Check if the prices are available
            if (_pricesToday == null)
            {
                return;
            }

            // Get the heat pump warm water heater entity
            var heater = _entities.WaterHeater.OurHomeDomesticHotWater0;
            if (heater == null)
            {
                _logger.LogWarning("Hot water heater not found");
                return;
            }

            // Check if it's Saturday
            var timeStamp = DateTime.Now;
            if (timeStamp.DayOfWeek != DayOfWeek.Saturday)
            {
                return;
            }

            // Get the start time for the lowest price after 07:00
            var startTime = _pricesToday.Where(p => p.Key.TimeOfDay > TimeSpan.FromHours(7)).OrderBy(p => p.Value).First().Key;

            // Set the start time 30 minutes earlier
            if (startTime.Hour > 2)
            {
                startTime = startTime.AddMinutes(-30);
            }

            var endTime = startTime.AddHours(2);

            // Check if the heater is off and the current timestamp is within the schedule
            if (_isLegionellaProtectionOn == false && timeStamp.TimeOfDay >= startTime.TimeOfDay && timeStamp.TimeOfDay <= endTime.TimeOfDay)
            {
                heater.SetOperationMode("Manual");
                heater.SetTemperature(65);
                heater.TurnOn();

                _logger.LogInformation($"Started legionella protection program");
                _logger.LogInformation($"Start time: {startTime}, End time: {endTime}");

                _ha.SendEvent("Energy schedule assistant", new { heater = "On", legionella_protection = "On" });
                _services.PersistentNotification.Create(message: "Started Legionella Protection", title: "Energy schedule assistant");

                _isHeaterOn = true;
                _isLegionellaProtectionOn = true;
            }
            else
            {
                heater.SetTemperature(55);
                heater.TurnOff();

                _ha.SendEvent("Energy schedule assistant", new { heater = "Off", legionella_protection = "Off" });

                _isHeaterOn = false;
                _isLegionellaProtectionOn = false;
            }
        }

        /// <summary>
        /// Gets the prices
        /// </summary>
        private void GetPrices()
        {
            // Read power prices for today
            var powerPrices = _ha.Entity("sensor.energy_prices_average_electricity_price_today");
            if (powerPrices == null)
            {
                _logger.LogWarning("Power prices sensor not found");
                return;
            }

            var jsonAttributes = powerPrices.EntityState?.AttributesJson;
            if (jsonAttributes == null)
            {
                _logger.LogWarning("Power prices sensor has no attributes");
                return;
            }

            var json = jsonAttributes.Value.ToString();
            var averageElectricyPrice = JsonSerializer.Deserialize<AverageElectricityPrice>(json);

            if (averageElectricyPrice != null)
            {
                _pricesToday = averageElectricyPrice.PricesToday.ToDictionary(p => p.GetTimeValue(), p => p.Price);
                _pricesTommorow = averageElectricyPrice.PricesTomorrow.ToDictionary(p => p.GetTimeValue(), p => p.Price);
            }
        }
    }
}
