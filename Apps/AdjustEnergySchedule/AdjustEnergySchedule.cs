namespace NetDaemonApps.Apps.AdjustEnergySchedule
{
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;

    using Helpers;

    using HomeAssistantGenerated;

    using Models.Enums;

    using NetDaemon.Extensions.Scheduler;

    /// <summary>
    /// Adjust the energy appliances schedule based on the power prices

    /// </summary>
    [NetDaemonApp]
    public class AdjustEnergySchedule
    {
        /// <summary>
        /// The logger
        /// </summary>
        private readonly ILogger<AdjustEnergySchedule> _logger;

        /// <summary>
        /// The entities
        /// </summary>
        private readonly Entities _entities;

        /// <summary>
        /// The heater on
        /// </summary>
        private bool _heaterOn;

        /// <summary>
        /// The target temperature for the water heater
        /// </summary>
        private int _targetTemperature;

        /// <summary>
        /// The wait cycles for water
        /// </summary>
        private int _waitCyclesWater;

        /// <summary>
        /// The away mode
        /// </summary>
        private readonly bool _awayMode;

        /// <summary>
        /// The price helper
        /// </summary>
        private readonly IPriceHelper _priceHelper;

        /// <summary>
        /// Initializes a new instance of the <see cref="AdjustEnergySchedule"/> class
        /// </summary>
        /// <param name="ha">The ha</param>
        /// <param name="scheduler">The scheduler</param>
        /// <param name="logger">The logger</param>
        /// <param name="priceHelper">The price helper</param>
        public AdjustEnergySchedule(IHaContext ha, INetDaemonScheduler scheduler, ILogger<AdjustEnergySchedule> logger, IPriceHelper priceHelper)
        {
            _logger = logger;
            _entities = new Entities(ha);
            _priceHelper = priceHelper;

            _logger.LogInformation("Started Energy Schedule Assistant program");

            // Set the away mode based on entity state
            _awayMode = _entities.Switch.OurHomeAwayMode.State == "on";

            if (Debugger.IsAttached)
            {
                // Run once
                RunEnergyPrograms();
            }
            else
            {
                // Application started
                // Run every 5 minutes
                scheduler.RunEvery(TimeSpan.FromMinutes(5), RunEnergyPrograms);
            }
        }

        /// <summary>
        /// Runs the checks for prices, schedules, and appliance states.
        /// </summary>
        private void RunEnergyPrograms()
        {
            // Set the schedule for the battery charging and discharging
            // SetBatterySchedule();

            // Disable the dishwasher, the washing machine and the dryer if the price is too high
            SetAppliancesSchedule();

            // Set the heating schedule for the heat pump
            SetWaterTemperature();

            // Set the car charging schedule
        }

        /// <summary>
        /// Sets the battery schedule using the external API.
        /// </summary>
        private void SetBatterySchedule()
        {
            // Create battery API client and authenticate
            var battery = new SaiPowerBatteryApi("MBovee", "fnq@tce8CTQ5kcm4cuw", "HST2083J2446E06861");

            //var token = battery.EnsureAuthenticatedAsync().Result;

            // TODO: Add logic for scheduling battery operations

            // Example schedule entries based on schedule.jpg
            var scheduleEntries = new List<SaiPowerBatteryApi.ScheduleEntry>
            {
                new(1, "12:00", "14:30", 8000, [true, true, true, true, true, true, true]),
                new(1, "09:00", "10:00", 5, [false, false, false, true, false, false, false]),
                new(1, "20:00", "21:30", 8000, [true, true, true, true, true, true, true]),
                new(1, "08:30", "09:00", 8000, [true, true, true, true, true, true, true])
            };

            // Build the schedule value string
            var scheduleValue = battery.BuildBatteryScheduleParameters(scheduleEntries);

            // Output the schedule value for verification
            Console.WriteLine(scheduleValue);
        }

        /// <summary>
        /// Sets the appliance schedule
        /// </summary>
        private void SetAppliancesSchedule()
        {
            // Get the entities for the appliances
            var washingMachine = _entities.Switch.Wasmachine;
            var washingMachineCurrentPower = _entities.Sensor.WasmachineHuidigGebruik;
            var dryer = _entities.Switch.DrogerEnVriezer;
            var dryerCurrentPower = _entities.Sensor.DrogerEnVriezerHuidigGebruik;
            var dishwasher = _entities.Switch.Vaatwasser;
            var dishwasherCurrentPower = _entities.Sensor.VaatwasserHuidigGebruik;
            var garage = _entities.Switch.Garage;
            var garageCurrentPower = _entities.Sensor.GarageHuidigGebruik;

            // Check if the price is above the threshold or if the away mode is active
            if (_awayMode || _priceHelper.EnergyPriceLevel > Level.Medium)
            {
                // Check if the washing machine is on and if no program is running
                if (washingMachine?.State == "on" && washingMachineCurrentPower.State < 3)
                {
                    _logger.LogInformation("Washing machine disabled due to high power prices");
                    washingMachine.TurnOff();
                }

                // Check if the dryer is on
                if (dryer?.State == "on" && dryerCurrentPower.State < 3)
                {
                    _logger.LogInformation("Dryer disabled due to high power prices");
                    dryer.TurnOff();
                }

                // Check if the dishwasher is on
                if (dishwasher?.State == "on" && dishwasherCurrentPower.State < 3)
                {
                    _logger.LogInformation("Dishwasher disabled due to high power prices");
                    dishwasher.TurnOff();
                }

                // Check if the garage power is on
                if (garage?.State == "on" && garageCurrentPower.State < 3)
                {
                    _logger.LogInformation("Garage disabled due to high power prices");
                    garage.TurnOff();
                }
            }
            else
            {
                // Turn on the appliances if they are off
                if (washingMachine?.State == "off")
                {
                    _logger.LogInformation("Washing machine enabled again");
                    washingMachine.TurnOn();
                }

                if (dryer?.State == "off")
                {
                    _logger.LogInformation("Dryer enabled again");
                    dryer.TurnOn();
                }

                if (dishwasher?.State == "off")
                {
                    _logger.LogInformation("Dishwasher enabled again");
                    dishwasher.TurnOn();
                }

                if (garage?.State == "off")
                {
                    _logger.LogInformation("Garage enabled again");
                    garage.TurnOn();
                }
            }
        }

        /// <summary>
        /// Sets the water temperature for the heat pump
        /// </summary>
        private void SetWaterTemperature()
        {
            // Get the heat pump water entities
            var heatingWater = _entities.WaterHeater.OurHomeDomesticHotWater0;

            // Check for price data
            if (_priceHelper.PricesToday == null || _priceHelper.PricesTomorrow == null)
            {
                return;
            }

            // Check what type of program should be used
            var timeStamp = DateTime.Now;
            var useNightProgram = timeStamp.Hour < 6;
            var useLegionellaProtection = useNightProgram == false && timeStamp.DayOfWeek == DayOfWeek.Saturday;
            var programType = _awayMode ? "Away" : useNightProgram ? "Night" : useLegionellaProtection ? "Legionella Protection" : "Heating";

            // Check bath mode and turn off the bath mode if the water temperature is above 50 degrees
            var bathMode = _entities.InputBoolean.Bath.State == "on";
            if (bathMode)
            {
                if (heatingWater.Attributes?.CurrentTemperature > 50)
                {
                    _entities.InputBoolean.Bath.TurnOff();
                }
            }

            // Set the start and end time for the heating period
            var lowestNightPrice = _priceHelper.PricesToday.Where(p => p.Key.TimeOfDay < TimeSpan.FromHours(6)).OrderBy(p => p.Value).ThenBy(p => p.Key).FirstOrDefault();
            var lowestDayPrice = _priceHelper.PricesToday.Where(p => p.Key.TimeOfDay > TimeSpan.FromHours(8)).OrderBy(p => p.Value).ThenBy(p => p.Key).FirstOrDefault();
            var nextNightPrice = _priceHelper.PricesTomorrow.Where(p => p.Key.TimeOfDay < TimeSpan.FromHours(6)).OrderBy(p => p.Value).ThenBy(p => p.Key).FirstOrDefault();

            var startTime = useNightProgram ? lowestNightPrice.Key : lowestDayPrice.Key;

            // Check if the value 1 hour before the start time is lower than 1 hour after the start time
            if (useLegionellaProtection && startTime.Hour is > 0 and < 23 && _priceHelper.PricesToday[startTime.AddHours(-1)] < _priceHelper.PricesToday[startTime.AddHours(1)])
            {
                startTime = startTime.AddHours(-1);
            }

            // Set the end time 3 hours after the start time
            var endTime = startTime.AddHours(3);

            // Set the temperature values
            var programTemperature = 35;
            var heatingTemperature = 35;
            var idleTemperature = 35;
            var currentTemperature = 35;
            var currentTargetTemperature = 35;

            if (heatingWater.Attributes is { Temperature: not null })
            {
                currentTemperature = (int)heatingWater.Attributes.CurrentTemperature!;
                currentTargetTemperature = (int)heatingWater.Attributes.Temperature!;
            }

            var idleText = startTime >= timeStamp ? $"{programType} program planned at: {startTime:HH:mm}" : "Idle";

            // Set the temperature values for the specific heating program
            if (_awayMode)
            {
                // Set the temperature to 60 degrees for the away mode for legionella protection
                if (useLegionellaProtection && useNightProgram == false)
                {
                    programTemperature = _priceHelper.CurrentPrice < 0.2 ? 66 : 60;
                }
            }
            else
            {
                // Set the heating temperature based on the energy price level
                heatingTemperature = _priceHelper.EnergyPriceLevel switch
                {
                    Level.None => 70,
                    Level.Low => 50,
                    _ => bathMode ? 58 : 35
                };

                // Set the program temperature value based on the heating program
                if (useNightProgram)
                {
                    // Set the temperature to 56 degrees for the night program if the night price is lower than the day price
                    var nightValue = lowestNightPrice.Value;
                    var dayValue = lowestDayPrice.Value;

                    programTemperature = nightValue < dayValue ? 56 : 52;
                }
                else if (useLegionellaProtection)
                {
                    programTemperature = _priceHelper.EnergyPriceLevel switch
                    {
                        Level.None => 70,
                        _ => 62
                    };
                }
                else
                {
                    programTemperature = _priceHelper.EnergyPriceLevel switch
                    {
                        Level.None => 70,
                        _ => 58
                    };

                    // Check if the next price for tomorrow is lower than the current price
                    if (nextNightPrice.Value > 0 && nextNightPrice.Value < _priceHelper.CurrentPrice && _priceHelper.EnergyPriceLevel > Level.Medium)
                    {
                        // if so, set the temperature to idle and wait for the next cycle
                        programTemperature = heatingTemperature;
                    }
                }
            }

            // Set the water heating temperature
            try
            {
                // Check if the heater is off and the current timestamp is within the schedule
                if (timeStamp.TimeOfDay >= startTime.TimeOfDay && timeStamp <= endTime)
                {
                    // Started program
                    if (programTemperature <= _targetTemperature && _heaterOn) return;

                    _targetTemperature = programTemperature;
                    _heaterOn = true;

                    heatingWater.SetOperationMode("Manual");
                    heatingWater.SetTemperature(Convert.ToDouble(_targetTemperature));

                    DisplayMessage(currentTemperature < _targetTemperature ? $"{programType} program from: {startTime:HH:mm} to: {endTime:HH:mm}" : idleText);
                }
                else
                {
                    // Set the heater heating temperature if no program is running
                    if (_waitCyclesWater > 0)
                    {
                        _waitCyclesWater--;

                        DisplayMessage(currentTemperature < _targetTemperature ? $"Heating to {_targetTemperature} [{_waitCyclesWater}]" : idleText);
                    }
                    else
                    {
                        // Set the heater to heating temperature
                        _targetTemperature = heatingTemperature;
                        _waitCyclesWater = 10;
                        _heaterOn = false;

                        if (currentTargetTemperature != _targetTemperature)
                        {
                            heatingWater.SetOperationMode("Manual");
                            heatingWater.SetTemperature(Convert.ToDouble(_targetTemperature));
                        }

                        if (_targetTemperature > idleTemperature)
                        {
                            DisplayMessage(currentTemperature < _targetTemperature ? $"Heating to {_targetTemperature} [{_waitCyclesWater}]" : idleText);
                        }
                        else
                        {
                            DisplayMessage(idleText);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to set the heating temperature");

                // Reset the values
                _targetTemperature = 0;
                _waitCyclesWater = 0;
                _heaterOn = false;
            }
        }

        /// <summary>
        /// Displays the message using the specified message
        /// </summary>
        /// <param name="message">The message</param>
        private void DisplayMessage(string message)
        {
            _entities.InputText.HeatingScheduleStatus.SetValue(message);
        }
    }
}
