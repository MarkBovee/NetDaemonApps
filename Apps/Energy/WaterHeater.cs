namespace NetDaemonApps.Apps.Energy
{
    using System.Diagnostics;
    using HomeAssistantGenerated;
    using Models.EnergyPrices;
    using NetDaemon.Extensions.Scheduler;
    using Models;
    using Models.Enums;

    /// <summary>
    /// Adjust the energy appliances schedule based on the power prices
    /// </summary>
    [NetDaemonApp]
    public class WaterHeater
    {
        /// <summary>
        /// The logger
        /// </summary>
        private readonly ILogger<WaterHeater> _logger;

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
        /// Initializes a new instance of the <see cref="WaterHeater"/> class
        /// </summary>
        /// <param name="ha">The ha</param>
        /// <param name="scheduler">The scheduler</param>
        /// <param name="logger">The logger</param>
        /// <param name="priceHelper">The price helper</param>
        public WaterHeater(IHaContext ha, INetDaemonScheduler scheduler, ILogger<WaterHeater> logger, IPriceHelper priceHelper)
        {
            _logger = logger;
            _entities = new Entities(ha);
            _priceHelper = priceHelper;

            _logger.LogInformation("Started water heater energy program");

            // Set the away mode based on entity state
            _awayMode = _entities.Switch.OurHomeAwayMode.State == "on";

            // Load state from State file
            _heaterOn = AppStateManager.GetState<bool>(nameof(WaterHeater), "HeaterOn");
            _targetTemperature = AppStateManager.GetState<int>(nameof(WaterHeater), "TargetTemperature");
            _waitCyclesWater = AppStateManager.GetState<int>(nameof(WaterHeater), "WaitCyclesWater");

            if (Debugger.IsAttached)
            {
                // Run once
                //SetWaterTemperature();
            }
            else
            {
                // Run every 5 minutes
                scheduler.RunEvery(TimeSpan.FromMinutes(5), SetWaterTemperature);
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

            // Use PriceHelper for price calculations
            var lowestNightPrice = PriceHelper.GetLowestNightPrice(_priceHelper.PricesToday);
            var lowestDayPrice = PriceHelper.GetLowestDayPrice(_priceHelper.PricesToday);
            var nextNightPrice = PriceHelper.GetNextNightPrice(_priceHelper.PricesTomorrow);

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
                    AppStateManager.SetState(nameof(WaterHeater), "TargetTemperature", _targetTemperature);
                    AppStateManager.SetState(nameof(WaterHeater), "HeaterOn", _heaterOn);

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
                        AppStateManager.SetState(nameof(WaterHeater), "WaitCyclesWater", _waitCyclesWater);

                        DisplayMessage(currentTemperature < _targetTemperature ? $"Heating to {_targetTemperature} [{_waitCyclesWater}]" : idleText);
                    }
                    else
                    {
                        // Check if current temperature is already adequate before starting heating cycle
                        if (currentTemperature >= heatingTemperature - 2) // Allow 2-degree tolerance below target
                        {
                            // Water is already at adequate temperature, don't start heating cycle
                            _targetTemperature = heatingTemperature;
                            _waitCyclesWater = 0;
                            _heaterOn = false;
                            
                            AppStateManager.SetState(nameof(WaterHeater), "TargetTemperature", _targetTemperature);
                            AppStateManager.SetState(nameof(WaterHeater), "WaitCyclesWater", _waitCyclesWater);
                            AppStateManager.SetState(nameof(WaterHeater), "HeaterOn", _heaterOn);

                            DisplayMessage(idleText);
                        }
                        else
                        {
                            // Set the heater to heating temperature
                            _targetTemperature = heatingTemperature;
                            _waitCyclesWater = 10;
                            _heaterOn = false;

                            AppStateManager.SetState(nameof(WaterHeater), "TargetTemperature", _targetTemperature);
                            AppStateManager.SetState(nameof(WaterHeater), "WaitCyclesWater", _waitCyclesWater);
                            AppStateManager.SetState(nameof(WaterHeater), "HeaterOn", _heaterOn);

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
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to set the heating temperature");

                // Reset the values
                _targetTemperature = 0;
                _waitCyclesWater = 0;
                _heaterOn = false;
                AppStateManager.SetState(nameof(WaterHeater), "TargetTemperature", _targetTemperature);
                AppStateManager.SetState(nameof(WaterHeater), "WaitCyclesWater", _waitCyclesWater);
                AppStateManager.SetState(nameof(WaterHeater), "HeaterOn", _heaterOn);
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
