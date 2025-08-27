namespace NetDaemonApps.Apps.Energy
{
    using System.Diagnostics;
    using System.Linq;
    using System.Threading.Tasks;
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
        /// The scheduler
        /// </summary>
        private readonly INetDaemonScheduler _scheduler;

        /// <summary>
        /// The current prepared schedule (not yet applied)
        /// </summary>
        private ChargingSchema? _preparedSchedule;

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
            _scheduler = scheduler;

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
                PrepareScheduleForDay();
            }
            else
            {
                // Calculate and prepare schedules daily at 00:05  
                var dailyTime = DateTime.Today.AddDays(1).AddMinutes(5); // Tomorrow at 00:05
                scheduler.RunEvery(TimeSpan.FromDays(1), dailyTime, PrepareScheduleForDay);
                
                // On startup, check if we have a prepared schedule for today or need to create one
                var existingSchedule = GetPreparedSchedule();
                if (existingSchedule != null)
                {
                    _logger.LogInformation("Found existing prepared schedule for today, setting up EMS management");
                    _preparedSchedule = existingSchedule;
                    ScheduleEmsManagementForPeriods(existingSchedule);
                }
                else
                {
                    _logger.LogInformation("No existing schedule found, preparing new schedule");
                    PrepareScheduleForDay();
                }
            }
        }

        /// <summary>
        /// Prepares the daily battery schedule and schedules EMS management around charge/discharge periods.
        /// This replaces the old approach of immediately applying schedules.
        /// </summary>
        private void PrepareScheduleForDay()
        {
            try
            {
                _logger.LogInformation("Preparing daily battery schedule");

                // Calculate the schedule for today
                var schedule = CalculateInitialChargingSchedule();
                if (schedule == null)
                {
                    _logger.LogWarning("Could not calculate schedule for today");
                    return;
                }

                // Store the prepared schedule (don't apply yet)
                _preparedSchedule = schedule;
                SavePreparedSchedule(schedule);

                // Schedule EMS management for each charge/discharge period
                ScheduleEmsManagementForPeriods(schedule);

                _logger.LogInformation("Daily schedule prepared with {PeriodCount} periods", schedule.Periods.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error preparing daily schedule");
            }
        }

        /// <summary>
        /// Schedules EMS shutdown before each period and re-enable after each period ends.
        /// </summary>
        /// <param name="schedule">The charging schedule to manage EMS around</param>
        private void ScheduleEmsManagementForPeriods(ChargingSchema schedule)
        {
            var today = DateTime.Today;

            foreach (var period in schedule.Periods)
            {
                var periodStart = today.Add(period.StartTime);
                var periodEnd = today.Add(period.EndTime);

                // Skip periods that have already passed
                if (periodEnd <= DateTime.Now)
                {
                    _logger.LogDebug("Skipping past period: {PeriodType} {StartTime}-{EndTime}", 
                        period.ChargeType, period.StartTime, period.EndTime);
                    continue;
                }

                // Schedule EMS shutdown 5 minutes before period starts
                var emsShutdownTime = periodStart.AddMinutes(-5);
                if (emsShutdownTime > DateTime.Now)
                {
                    _scheduler.RunAt(emsShutdownTime, () => PrepareForBatteryPeriod(period));
                    _logger.LogInformation("Scheduled EMS shutdown at {Time} for {PeriodType} period {StartTime}-{EndTime}", 
                        emsShutdownTime.ToString("HH:mm"), period.ChargeType, period.StartTime, period.EndTime);
                }

                // Schedule EMS re-enable after period ends
                var emsRestoreTime = periodEnd.AddMinutes(1);
                if (emsRestoreTime > DateTime.Now)
                {
                    _scheduler.RunAt(emsRestoreTime, () => RestoreEmsAfterPeriod(period));
                    _logger.LogInformation("Scheduled EMS restore at {Time} after {PeriodType} period", 
                        emsRestoreTime.ToString("HH:mm"), period.ChargeType);
                }
            }
        }

        /// <summary>
        /// Prepares for a battery charge/discharge period by shutting down EMS and applying the schedule.
        /// </summary>
        /// <param name="period">The period that is about to start</param>
        private void PrepareForBatteryPeriod(ChargingPeriod period)
        {
            try
            {
                _logger.LogInformation("Preparing for {PeriodType} period {StartTime}-{EndTime}", 
                    period.ChargeType, period.StartTime, period.EndTime);

                // 1. Turn off EMS
                var emsState = _entities.Switch.Ems.State;
                if (emsState == "on")
                {
                    _logger.LogInformation("Turning off EMS before battery period");
                    _entities.Switch.Ems.TurnOff();
                    
                    // Wait a moment for EMS to shut down
                    Task.Delay(TimeSpan.FromSeconds(10)).Wait();
                }
                else
                {
                    _logger.LogInformation("EMS is already off");
                }

                // 2. Apply the prepared schedule if we have one
                var scheduleToApply = _preparedSchedule ?? GetPreparedSchedule();
                if (scheduleToApply != null)
                {
                    _logger.LogInformation("Applying prepared battery schedule");
                    ApplyChargingSchedule(scheduleToApply, simulateOnly: Debugger.IsAttached);
                }
                else
                {
                    _logger.LogWarning("No prepared schedule available to apply");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error preparing for battery period");
            }
        }

        /// <summary>
        /// Restores EMS after a battery charge/discharge period ends.
        /// </summary>
        /// <param name="period">The period that just ended</param>
        private void RestoreEmsAfterPeriod(ChargingPeriod period)
        {
            try
            {
                _logger.LogInformation("Restoring EMS after {PeriodType} period", period.ChargeType);

                // Turn EMS back on
                var emsState = _entities.Switch.Ems.State;
                if (emsState == "off")
                {
                    _logger.LogInformation("Turning EMS back on");
                    _entities.Switch.Ems.TurnOn();
                }
                else
                {
                    _logger.LogInformation("EMS is already on");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error restoring EMS after period");
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
                // Convert our charging schedule to SAJ API format using all periods
                var allPeriods = chargingSchedule.Periods.ToList();

                // Validate the charging periods
                if (allPeriods.Count == 0)
                {
                    _logger.LogWarning("No valid periods in schedule");
                    return;
                }

                // Build the schedule parameters using the new method signature
                var scheduleParameters = SAJPowerBatteryApi.BuildBatteryScheduleParameters(allPeriods);

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
                        var chargePeriods = allPeriods.Where(p => p.ChargeType == BatteryChargeType.Charge).ToList();
                        var dischargePeriods = allPeriods.Where(p => p.ChargeType == BatteryChargeType.Discharge).ToList();

                        if (chargePeriods.Count > 0)
                        {
                            var chargeSchedule = $@"{chargePeriods.First().StartTime:hh\:mm}-{chargePeriods.Last().EndTime:hh\:mm}";
                            _entities.InputText.BatteryChargeSchedule?.SetValue(chargeSchedule);
                        }

                        if (dischargePeriods.Count > 0)
                        {
                            var dischargeSchedule = $@"{dischargePeriods.First().StartTime:hh\:mm}-{dischargePeriods.Last().EndTime:hh\:mm}";
                            _entities.InputText.BatteryDischargeSchedule?.SetValue(dischargeSchedule);
                        }
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
                AppStateManager.SetState(nameof(Battery), "CurrentAppliedSchema", schedule);

                _logger.LogDebug("Saved current applied schedule to state");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not save current applied schedule to state");
            }
        }

        /// <summary>
        /// Saves the prepared schedule (not yet applied) to persistent state
        /// </summary>
        /// <param name="schedule">The prepared charging schedule</param>
        private void SavePreparedSchedule(ChargingSchema schedule)
        {
            try
            {
                AppStateManager.SetState(nameof(Battery), "PreparedSchedule", schedule);
                AppStateManager.SetState(nameof(Battery), "PreparedScheduleDate", DateTime.Today);

                _logger.LogDebug("Saved prepared schedule to state");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not save prepared schedule to state");
            }
        }

        /// <summary>
        /// Retrieves the prepared schedule from persistent state
        /// </summary>
        /// <returns>The prepared schedule for today, or null if none exists</returns>
        private ChargingSchema? GetPreparedSchedule()
        {
            try
            {
                var preparedDate = AppStateManager.GetState<DateTime?>(nameof(Battery), "PreparedScheduleDate");
                if (preparedDate?.Date != DateTime.Today)
                {
                    return null; // Schedule is for a different day
                }

                return AppStateManager.GetState<ChargingSchema?>(nameof(Battery), "PreparedSchedule");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not retrieve prepared schedule from state");
                return null;
            }
        }

        #endregion
    }
}
