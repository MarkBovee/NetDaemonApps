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
        /// The temperature heat
        /// </summary>
        private readonly double _temperatureHeat;

        /// <summary>
        /// The temperature idle
        /// </summary>
        private readonly double _temperatureIdle;

        /// <summary>
        /// The temperature legionalla
        /// </summary>
        private readonly double _temperatureLegionalla;

        /// <summary>
        /// The is heater on indication
        /// </summary>
        private bool _isHeaterOn;

        /// <summary>
        /// The is legionella protection on indication
        /// </summary>
        private bool _isLegionellaProtectionOn;

        /// <summary>
        /// The heating day
        /// </summary>
        private DateTime _heatingDay;

        /// <summary>
        /// The protection day
        /// </summary>
        private DateTime _protectionDay;

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

            // Set the temperature values
            _temperatureHeat = 58;
            _temperatureIdle = 30;
            _temperatureLegionalla = 63;

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
            var timeStamp = DateTime.Now;
            var startTime = _pricesToday.Where(p => p.Key.TimeOfDay > TimeSpan.FromHours(11)).OrderBy(p => p.Value).First().Key;
            var endTime = startTime.AddHours(2);

            // Set notification for the start of the heating period
            if (startTime.Date != _heatingDay)
            {
                _heatingDay = startTime;

                if (startTime < timeStamp)
                {
                    _services.PersistentNotification.Create(message: $"Heating planned at: {startTime} ", title: "Energy schedule assistant");
                }
            }

            // Check if the heater is off and the current timestamp is within the schedule
            if (timeStamp.TimeOfDay >= startTime.TimeOfDay && timeStamp.TimeOfDay <= endTime.TimeOfDay)
            {
                if (_isHeaterOn == false)
                {
                    _logger.LogInformation($"Started water heating program from: {startTime}, to: {endTime}");

                    try
                    {
                        heater.SetOperationMode("Manual");
                        heater.SetTemperature(_temperatureHeat);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to start water heating program");
                    }

                    _ha.SendEvent("Energy schedule assistant", new { heater = "On" });

                    _isHeaterOn = true;
                }
            }
            else
            {
                heater.SetTemperature(_temperatureIdle);

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

            // Set notification for the start of the protection period
            if (startTime.Date != _protectionDay)
            {
                _protectionDay = startTime;

                if (startTime < timeStamp)
                {
                    _services.PersistentNotification.Create(message: $"Legionella protection planned at: {startTime} ", title: "Energy schedule assistant");
                }
            }

            var endTime = startTime.AddHours(2);

            // Check if the heater is off and the current timestamp is within the schedule
            if (timeStamp.TimeOfDay >= startTime.TimeOfDay && timeStamp.TimeOfDay <= endTime.TimeOfDay)
            {
                if (_isLegionellaProtectionOn == false)
                {
                    try
                    {
                        heater.SetOperationMode("Manual");
                        heater.SetTemperature(_temperatureLegionalla);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to start legionella protection program");
                    }

                    _logger.LogInformation($"Started legionella protection program from: {startTime}, to: {endTime}");

                    _ha.SendEvent("Energy schedule assistant", new { heater = "On", legionella_protection = "On" });
                    _services.PersistentNotification.Create(message: "Started Legionella Protection", title: "Energy schedule assistant");

                    _isHeaterOn = true;
                    _isLegionellaProtectionOn = true;
                }
            }
            else
            {
                heater.SetTemperature(_temperatureIdle);

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
