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
        /// Set the threshold for the price, above this value the appliances will be disabled
        /// </summary>
        private readonly double _priceThreshold = 0.30;

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

                // Run every 1 minute
                scheduler.RunEvery(TimeSpan.FromMinutes(1), RunChecks);
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

            // Disable the dishwasher, the washing machine and the dryer if the price is too high
            SetAppliancesSchedule();

            // Set the car charging schedule
        }

        /// <summary>
        /// Sets the appliances schedule
        /// </summary>
        private void SetAppliancesSchedule()
        {
            // Get the entities for the appliances
            var washingMachine = _entities.Switch.Wasmachine;
            var washingMachineCurrentPower = _entities.Sensor.WasmachineCurrentPower;

            var dryer = _entities.Switch.Droger;
            var dryerCurrentPower = _entities.Sensor.DrogerCurrentPower;

            var dishwasher = _entities.Switch.Vaatwasser;
            var dishwasherCurrentPower = _entities.Sensor.VaatwasserCurrentPower;

            // Check if the prices are available
            if (_pricesToday == null)
            {
                return;
            }

            // Get the current timestamp
            var timeStamp = DateTime.Now;

            // Get the current price
            var currentPrice = _pricesToday.FirstOrDefault(p => p.Key.Hour == timeStamp.Hour).Value;

            // Check if the price is above the threshold
            if (currentPrice > _priceThreshold)
            {
                // Check if the washing machine is on and if no program is running
                if (washingMachine?.State == "on" && washingMachineCurrentPower.State < 10)
                {
                    _services.Logbook.Log("Energy schedule assistant", "Washing machine disabled due to high power prices");
                    washingMachine.TurnOff();
                }

                // Check if the dryer is on
                if (dryer?.State == "on" && dryerCurrentPower.State < 10)
                {
                    _services.Logbook.Log("Energy schedule assistant", "Dryer disabled due to high power prices");
                    dryer.TurnOff();
                }

                // Check if the dishwasher is on
                if (dishwasher?.State == "on" && dishwasherCurrentPower.State < 10)
                {
                    _services.Logbook.Log("Energy schedule assistant", "Dishwasher disabled due to high power prices");
                    dishwasher.TurnOff();
                }
            }
            else
            {
                // Turn on the appliances if they are off
                if (washingMachine?.State == "off")
                {
                    _services.Logbook.Log("Energy schedule assistant", "Washing machine  enabled again");
                    washingMachine.TurnOn();
                }

                if (dryer?.State == "off")
                {
                    _services.Logbook.Log("Energy schedule assistant", "Dryer enabled again");
                    dryer.TurnOn();
                }

                if (dishwasher?.State == "off")
                {
                    _services.Logbook.Log("Energy schedule assistant", "Dishwasher enabled again");
                    dishwasher.TurnOn();
                }
            }
        }

        /// <summary>
        /// Sets the heating schedule
        /// </summary>
        private void SetHeatingSchedule()
        {
            // Get the heat pump warm water heater entity
            var heater = _entities.WaterHeater.OurHomeDomesticHotWater0;
            if (heater == null)
            {
                _logger.LogWarning("Hot water heater not found");

                return;
            }

            // Check if the prices are available
            if (_pricesToday == null)
            {
                return;
            }

            // Get the current timestamp
            var timeStamp = DateTime.Now;

            // Get the current price
            var currentPrice = _pricesToday.FirstOrDefault(p => p.Key.Hour == timeStamp.Hour).Value;

            // Check if the legionella protection should be used
            var useNightProgram = timeStamp.Hour < 6;
            var useLegionellaProtection = useNightProgram == false && timeStamp.DayOfWeek == DayOfWeek.Saturday;
            var programType = useNightProgram ? "Night" : useLegionellaProtection ? "Legionella Protection" : "Heating";

            // Set the start and end time for the heating period
            DateTime startTime;
            if (useNightProgram)
            {
                startTime = _pricesToday.Where(p => p.Key.TimeOfDay < TimeSpan.FromHours(6)).OrderBy(p => p.Value).First().Key;
            }
            else
            {
                startTime = _pricesToday.Where(p => p.Key.TimeOfDay > TimeSpan.FromHours(8)).OrderBy(p => p.Value).First().Key;
            }

            // Check if the value 1 hour before before the start time is lower than 1 hour after the start time
            if (useLegionellaProtection && startTime.Hour > 0 && _pricesToday[startTime.AddHours(-1)] < _pricesToday[startTime.AddHours(1)])
            {
                startTime = startTime.AddHours(-1);
            }

            // Set the end time 3 hours after the start time
            var endTime = startTime.AddHours(3);

            // Set the temperature values
            var temperatureHeat = useNightProgram ? 50 : useLegionellaProtection ? 64 : 56;
            var temperatureIdle = currentPrice < _priceThreshold ? 40 : 35;

            // Set notification for the start of the heating period
            if (_heatingTime.Date < startTime.Date)
            {
                _heatingTime = startTime;

                // Check if the start time is in the future
                if (startTime > timeStamp)
                {
                    _services.PersistentNotification.Create(message: $"Next {programType} program planned at: {startTime} ", title: "Energy schedule assistant");
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
                        heater.SetTemperature(temperatureHeat);
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
                // Set the heater to idle temperature after the heating period
                if (_isHeaterOn || heater.Attributes?.Temperature != temperatureIdle)
                {
                    heater.SetTemperature(temperatureIdle);
                }

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
