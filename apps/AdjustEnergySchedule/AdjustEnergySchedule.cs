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
    /// TODO: Split to different apps
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
        /// The heating time
        /// </summary>
        private DateTime _heatingTime;

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
                _services.Logbook.Log("Energy schedule assistant", "Succesfully started");

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

            // Set the temperature values
            var temperatureHeat = 58;
            var temperatureIdle = 35;
            var temperatureLegionallaProtection = 64;

            // Get the start time for the lowest price after 8:00
            var timeStamp = DateTime.Now;
            var startTime = _pricesToday.Where(p => p.Key.TimeOfDay > TimeSpan.FromHours(8)).OrderBy(p => p.Value).First().Key;
            var endTime = startTime.AddHours(2);
            var useLegionellaProtection = timeStamp.DayOfWeek == DayOfWeek.Saturday;
            var programType = useLegionellaProtection ? "Legionella Protection" : "Heating";

            // Set notification for the start of the heating period
            if (_heatingTime.Date < startTime.Date)
            {
                _heatingTime = startTime;

                // Check if the start time is in the future
                if (startTime > timeStamp)
                {
                    _services.PersistentNotification.Create(message: $"Next {programType} planned at: {startTime} ", title: "Energy schedule assistant");
                }
            }

            // Check if the heater is off and the current timestamp is within the schedule
            if (timeStamp.TimeOfDay >= startTime.TimeOfDay && timeStamp.TimeOfDay <= endTime.TimeOfDay)
            {
                if (_isHeaterOn == false)
                {
                    _services.Logbook.Log("Energy schedule assistant", $"Started {programType} program from: {startTime}, to: {endTime}");

                    try
                    {
                        heater.SetOperationMode("Manual");
                        heater.SetTemperature(useLegionellaProtection ? temperatureLegionallaProtection : temperatureHeat);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to set heating schedule");
                    }

                    _isHeaterOn = true;
                }
            }
            else
            {
                heater.SetTemperature(temperatureIdle);

                _isHeaterOn = false;
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
