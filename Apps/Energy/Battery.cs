namespace NetDaemonApps.Apps.Energy
{
    using System.Diagnostics;
    using System.Linq;
    using HomeAssistantGenerated;
    using Models;
    using Models.Battery;
    using Models.EnergyPrices;
    using NetDaemon.Extensions.Scheduler;

    /// <summary>
    /// Adjust the energy appliances schedule based on the power prices
    /// </summary>
    [NetDaemonApp]
    public class Battery
    {
        /// <summary>
        /// The logger
        /// </summary>
        private readonly ILogger<Battery> _logger;

        /// <summary>
        /// The entities
        /// </summary>
        private readonly Entities _entities;

        /// <summary>
        /// The away mode
        /// </summary>
        private readonly bool _awayMode;

        /// <summary>
        /// The price helper
        /// </summary>
        private readonly IPriceHelper _priceHelper;

        /// <summary>
        /// The last applied schedule
        /// </summary>
        private DateTime? _lastAppliedSchedule;

        /// <summary>
        /// The sai power battery api
        /// </summary>
        private readonly SAJPowerBatteryApi _saiPowerBatteryApi;

        // Max values
        private const double MaxInverterPower = 8000;       // W
        private const double MaxSolarProduction = 4500;     // W
        private const double MaxBatteryCapacity = 25000;    // Wh

        /// <summary>
        /// Initializes a new instance of the <see cref="Battery"/> class.
        /// </summary>
        /// <param name="ha">The ha</param>
        /// <param name="scheduler">The scheduler</param>
        /// <param name="logger">The logger</param>
        /// <param name="priceHelper">The price helper</param>
        public Battery(IHaContext ha, INetDaemonScheduler scheduler, ILogger<Battery> logger, IPriceHelper priceHelper)
        {
            _logger = logger;
            _entities = new Entities(ha);
            _priceHelper = priceHelper;

            _logger.LogInformation("Started battery energy program");

            // Set the away mode based on entity state
            _awayMode = _entities.Switch.OurHomeAwayMode.State == "on";

            // Set the SAJ Power Battery API with your credentials
            _saiPowerBatteryApi = new SAJPowerBatteryApi("MBovee", "fnq@tce8CTQ5kcm4cuw", "HST2083J2446E06861");

            // Check if the token is valid
            _saiPowerBatteryApi.IsTokenValid(true);

            if (Debugger.IsAttached)
            {
                // Run simulation for debugging
                SetBatterySchedule();
            }
            else
            {
                // Run evaluation every 15 minutes to adjust the schema
                scheduler.RunEvery(TimeSpan.FromMinutes(15), SetBatterySchedule);
            }
        }

        /// <summary>
        /// Sets the battery schedule using the external API.
        /// </summary>
        private void SetBatterySchedule()
        {
            // Get the currently applied schedule
            var currentAppliedSchedule = GetCurrentAppliedSchedule();

            // Check if we need to set a new schedule (once per day) or evaluate the existing schedule
            if (_lastAppliedSchedule != null && _lastAppliedSchedule.Value.Date == DateTime.Now.Date)
            {
                var evaluatedSchedule = EvaluateAndAdjustChargingSchedule();

                if (evaluatedSchedule == null) return;
                if (currentAppliedSchedule != null && currentAppliedSchedule.IsEquivalentTo(evaluatedSchedule)) return;

                _logger.LogInformation("Apply new charging schedule");

                ApplyChargingSchedule(evaluatedSchedule);
            }
            else
            {
                _logger.LogInformation("Calculating initial schedule");

                var initialSchedule = CalculateInitialChargingSchedule();
                if (initialSchedule == null) return;

                ApplyChargingSchedule(initialSchedule);
            }
        }

        /// <summary>
        /// Evaluates current conditions and adjusts the charging schedule every 15 minutes
        /// Currently simplified - returns null to avoid complex logic during development
        /// </summary>
        /// <returns>New charging schedule if recalculation was needed, null if only minor adjustments were performed</returns>
        private ChargingSchema? EvaluateAndAdjustChargingSchedule()
        {
            // FUTURE ENHANCEMENTS: This method can implement real-time schedule adjustments:

            // 1. SOLAR PRODUCTION MONITORING
            //    - Monitor current solar production vs forecast
            //    - If excess solar available, temporarily increase charging power
            //    - If solar lower than expected, reduce charging to save battery capacity

            // 2. CONSUMPTION PATTERN DETECTION
            //    - Use EstimateDailyEnergyConsumption() to detect unusual consumption
            //    - Adjust discharge timing if higher consumption detected
            //    - Emergency mode: immediate charging if battery critically low and expensive hours ahead

            // 3. GRID CONDITIONS
            //    - Monitor grid frequency and voltage for grid services participation
            //    - Implement demand response: reduce discharge during grid stress events
            //    - Peak shaving: discharge during local consumption peaks regardless of price

            // 4. WEATHER-BASED ADJUSTMENTS
            //    - Check updated weather forecast and adjust solar expectations
            //    - Prepare for storms: ensure battery is charged before bad weather
            //    - Heat wave response: reserve capacity for increased AC usage

            // 5. TIME-BASED LOGIC
            //    - During solar hours (10-16): optimize for solar excess capture
            //    - During evening peak (17-22): ensure adequate discharge if profitable
            //    - Night hours: minimal adjustments, prepare for next day
            //    - Use IsChargingAllowedAfterSunset() to prevent wasteful charging

            // 6. PRICE CHANGE RESPONSE
            //    - React to intraday price updates from energy markets
            //    - Implement imbalance price monitoring for additional revenue
            //    - Dynamic pricing: adjust schedule if day-ahead prices change significantly

            // CURRENT IMPLEMENTATION: Always return null to keep existing schedule
            // This prevents unwanted schedule changes during development and testing
            return null;
        }

        /// <summary>
        /// Applies a charging schedule by converting it to SAJ API format and uploading
        /// </summary>
        /// <param name="chargingSchedule">The charging schedule to apply</param>
        private void ApplyChargingSchedule(ChargingSchema chargingSchedule)
        {
            ApplyChargingSchedule(chargingSchedule, simulateOnly: Debugger.IsAttached);
        }

        /// <summary>
        /// Applies the given charging schema to the battery system via the SAJ Power API
        /// </summary>
        /// <param name="chargingSchema">The charging schema to apply</param>
        /// <param name="simulateOnly">If true, simulates the API call without actually executing it</param>
        private void ApplyChargingSchedule(ChargingSchema chargingSchedule, bool simulateOnly)
        {
            if (chargingSchedule?.Periods == null || chargingSchedule.Periods.Count == 0)
            {
                _logger.LogWarning("No charging schedule to apply");
                return;
            }

            try
            {
                // Convert our charging schedule to SAJ API format
                var chargePeriods = chargingSchedule.Periods.Where(cm => cm.ChargeType == BatteryChargeType.Charge).ToList();
                var dischargePeriods = chargingSchedule.Periods.Where(cm => cm.ChargeType == BatteryChargeType.Discharge).ToList();

                // Validate the charging periods
                if (chargePeriods.Count == 0 && dischargePeriods.Count == 0)
                {
                    _logger.LogWarning("No valid charge or discharge periods in schedule");
                    return;
                }

                // Build the schedule parameters
                var scheduleParameters = SAJPowerBatteryApi.BuildBatteryScheduleParameters(chargePeriods, dischargePeriods);

                // Apply the schedule to the battery
                var saved = simulateOnly || _saiPowerBatteryApi.SaveBatteryScheduleAsync(scheduleParameters).Result;

                if (saved)
                {
                    // Update state tracking
                    SaveCurrentAppliedSchedule(chargingSchedule);

                    // Log the applied schedule
                    if (simulateOnly)
                    {
                        // Log the applied schedule details
                        var scheduleDescription = string.Join(", ", chargingSchedule.Periods.Select(p =>  $@"{p.ChargeType} {p.StartTime:hh\:mm}-{p.EndTime:hh\:mm} @ {p.PowerInWatts}W"));
                        _logger.LogInformation("Applied charging schedule: {ScheduleDescription}", scheduleDescription);
                    }
                    else
                    {
                        var chargeSchedule = $@"{chargePeriods.First().StartTime:hh\:mm}-{chargePeriods.Last().EndTime:hh\:mm}";
                        var dischargeSchedule = $@"{dischargePeriods.First().StartTime:hh\:mm}-{dischargePeriods.Last().EndTime:hh\:mm}";

                        _entities.InputText.BatteryChargeSchedule?.SetValue(chargeSchedule);
                        _entities.InputText.BatteryDischargeSchedule?.SetValue(dischargeSchedule);
                    }
                }
                else
                {
                    _logger.LogWarning("Failed to apply charging schedule via SAJ API");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error applying charging schedule");
            }
        }

        /// <summary>
        /// Calculates the initial charging schedule based on energy prices, solar production, and battery state
        /// Currently implements the simplest strategy: charge during lowest prices, discharge during highest prices
        /// </summary>
        /// <returns>Complete charging schedule for the day</returns>
        private ChargingSchema? CalculateInitialChargingSchedule()
        {
            var pricesToday = _priceHelper.PricesToday;
            if (pricesToday == null || pricesToday.Count < 3)
            {
                _logger.LogWarning("Not enough price data to set battery schedule.");
                return null;
            }

            // CURRENT STRATEGY: Simple lowest/highest price periods
            // Get charge and discharge timeslots using basic price analysis
            var (chargeStart, chargeEnd) = PriceHelper.GetLowestPriceTimeslot(pricesToday, 3);
            var (dischargeStart, dischargeEnd) = PriceHelper.GetHighestPriceTimeslot(pricesToday, 1);

            // FUTURE ENHANCEMENTS: The following strategies can be implemented in the future:

            // 1. DYNAMIC POWER CALCULATION
            //    - Use GetCurrentBatterySOC() to adjust charge power based on current battery level
            //    - Calculate optimal power based on battery capacity (16.4 kWh total) and max inverter power
            //    - Consider charging time constraints (e.g., must be 80% charged by evening peak)

            // 2. SOLAR FORECAST INTEGRATION
            //    - Use weather API to get solar production forecast for tomorrow
            //    - Reduce charging if high solar production is expected
            //    - Adjust discharge timing based on expected solar self-consumption
            //    - Use EstimateDailyEnergyConsumption() vs solar forecast to determine net energy need

            // 3. MULTI-STRATEGY OPTIMIZATION
            //    - Implement the 4 strategies from previous complex version:
            //      a) Lowest Price Strategy (current implementation)
            //      b) Solar Excess Strategy: charge only during solar overproduction
            //      c) Peak Shaving Strategy: discharge during consumption peaks regardless of price
            //      d) Hybrid Strategy: combine price and solar optimization

            // 4. ADVANCED SCHEDULING
            //    - Multiple charge/discharge periods per day
            //    - Partial charging cycles (e.g., charge to 60% during cheap hours, top off during solar peak)
            //    - Emergency reserve logic: always keep 20% for outages
            //    - Grid services: participate in demand response programs

            // 5. MACHINE LEARNING OPTIMIZATION
            //    - Track actual vs predicted consumption and solar production
            //    - Learn household usage patterns for better prediction
            //    - Optimize based on actual battery efficiency and degradation

            // 6. REAL-TIME ADJUSTMENTS
            //    - Use EvaluateAndAdjustChargingSchedule() for dynamic updates
            //    - Implement IsChargingAllowedAfterSunset() logic for sunset detection
            //    - React to unexpected consumption spikes or grid outages

            // CURRENT IMPLEMENTATION: Fixed 8kW charge/discharge at optimal price times
            var schedule = new ChargingSchema
            {
                Periods =
                [
                    new ChargingPeriod
                    {
                        StartTime = chargeStart.TimeOfDay,
                        EndTime = chargeEnd.TimeOfDay,
                        ChargeType = BatteryChargeType.Charge,
                        PowerInWatts = 8000  // Future: make this dynamic based on SOC and time available
                    },

                    new ChargingPeriod
                    {
                        StartTime = dischargeStart.TimeOfDay,
                        EndTime = dischargeEnd.TimeOfDay,
                        ChargeType = BatteryChargeType.Discharge,
                        PowerInWatts = 8000  // Future: adjust based on consumption patterns and grid export limits
                    }
                ]
            };

            return schedule;
        }

        #region Helper Methods

        /// <summary>
        /// Gets the current battery state of charge from the main battery sensor
        /// </summary>
        /// <returns>Battery SOC as percentage (0-100)</returns>
        private double GetCurrentBatterySOC()
        {
            // Try to get SOC from the main inverter sensor first
            var mainBatterySoc = _entities.Sensor.InverterHst2083j2446e06861BatteryStateOfCharge.State;
            if (mainBatterySoc.HasValue)
            {
                return mainBatterySoc.Value;
            }

            // Fallback: Calculate average SOC from individual battery modules
            var batteryModules = new[]
            {
                _entities.Sensor.BatteryB2n0200j2403e01735BatSoc.State,
                _entities.Sensor.BatteryB2u4250j2511e06231BatSoc.State,
                _entities.Sensor.BatteryB2u4250j2511e06243BatSoc.State,
                _entities.Sensor.BatteryB2u4250j2511e06244BatSoc.State,
                _entities.Sensor.BatteryB2u4250j2511e06245BatSoc.State,
                _entities.Sensor.BatteryB2u4250j2511e06247BatSoc.State
            };

            var validModules = batteryModules.Where(soc => soc.HasValue).ToList();

            // Default to 50% if no data
            return validModules.Count != 0 ? validModules.Average(soc => soc!.Value) : 50.0;
        }

        /// <summary>
        /// Estimates daily energy consumption based on historical patterns and current usage
        /// </summary>
        /// <returns>Estimated daily consumption in kWh</returns>
        private double EstimateDailyEnergyConsumption()
        {
            // Get current grid power (positive = consuming, negative = feeding back)
            var currentGridPower = _entities.Sensor.BatteryGridPower.State ?? 0;
            var currentSolarPower = _entities.Sensor.PowerProductionNow.State ?? 0;
            var currentTime = DateTime.Now;

            // Base consumption estimate (typical Dutch household with heat pump)
            var baseConsumptionKwh = _awayMode ? 15.0 : 25.0; // Reduced when away

            // Seasonal adjustments
            var month = currentTime.Month;
            var seasonalMultiplier = month switch
            {
                12 or 1 or 2 => 1.4,            // Winter - higher heating
                3 or 11 => 1.2,                 // Shoulder months
                4 or 5 or 9 or 10 => 1.0,       // Moderate months
                6 or 7 or 8 => 0.9,             // Summer - lower heating, some cooling
                _ => 1.0
            };

            // If we have real-time data, factor in current consumption patterns
            if (!(currentGridPower > 0) || !(currentSolarPower >= 0)) return baseConsumptionKwh * seasonalMultiplier;

            var currentTotalConsumption = currentGridPower + currentSolarPower;
            var hourlyConsumption = currentTotalConsumption / 1000.0; // Convert W to kWh

            // Extrapolate based on time of day patterns
            var timeMultiplier = currentTime.Hour switch
            {
                >= 7 and <= 9 => 1.2,    // Morning peak
                >= 10 and <= 16 => 0.8,  // Daytime low
                >= 17 and <= 22 => 1.4,  // Evening peak
                _ => 0.6                 // Night
            };

            var estimatedDailyFromCurrent = (hourlyConsumption / timeMultiplier) * 24;

            // Average the base estimate with real-time extrapolation
            return (baseConsumptionKwh * seasonalMultiplier + estimatedDailyFromCurrent) / 2;

        }

        /// <summary>
        /// Determines if charging should be allowed based on remaining solar production
        /// If there's no more solar production expected today, the sun has set and we shouldn't charge
        /// </summary>
        /// <param name="energyProductionTodayRemaining">Remaining solar production expected today in kWh</param>
        /// <returns>True if charging is allowed, false if sun has set</returns>
        private bool IsChargingAllowedAfterSunset(double energyProductionTodayRemaining)
        {
            // If there's less than 0.1 kWh remaining production, consider the sun to be down
            const double sunsetThreshold = 0.1;
            return energyProductionTodayRemaining > sunsetThreshold;
        }

        #endregion

        #region State Management

        /// <summary>
        /// Retrieves the currently applied charging schedule from persistent state
        /// </summary>
        /// <returns>The currently applied charging schedule, or null if none exists</returns>
        private ChargingSchema? GetCurrentAppliedSchedule()
        {
            try
            {
                _lastAppliedSchedule = AppStateManager.GetState<DateTime?>(nameof(Battery), "LastAppliedSchedule");
                return AppStateManager.GetState<ChargingSchema?>(nameof(Battery), "CurrentAppliedSchema");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not retrieve current applied schedule from state");
                return null;
            }
        }

        /// <summary>
        /// Saves the currently applied charging schedule to persistent state
        /// </summary>
        /// <param name="schedule">The charging schedule that was applied</param>
        private void SaveCurrentAppliedSchedule(ChargingSchema schedule)
        {
            try
            {
                AppStateManager.SetState(nameof(Battery), "LastAppliedSchedule", DateTime.Now);
                AppStateManager.SetState(nameof(Battery), "CurrentAppliedSchema", schedule);

                _logger.LogDebug("Saved current applied schedule to state");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not save current applied schedule to state");
            }
        }

        #endregion
    }
}
