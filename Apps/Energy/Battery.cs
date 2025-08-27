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

            LogBatteryInfo("Started battery energy program with 3-checkpoint strategy");

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
                LogBatteryInfo("Preparing daily battery schedule");

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

                LogBatteryInfo("Daily schedule prepared with {0} periods", schedule.Periods.Count);
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
                    LogBatteryInfo("Scheduled EMS shutdown at {0} for {1} period {2}-{3}", 
                        emsShutdownTime.ToString("HH:mm"), period.ChargeType, 
                        period.StartTime.ToString(@"hh\:mm"), period.EndTime.ToString(@"hh\:mm"));
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
                LogBatteryInfo("Preparing for {0} period {1}-{2}", 
                    period.ChargeType, period.StartTime.ToString(@"hh\:mm"), period.EndTime.ToString(@"hh\:mm"));

                // 1. Turn off EMS
                var emsState = _entities.Switch.Ems.State;
                if (emsState == "on")
                {
                    LogBatteryInfo("Turning off EMS before battery period");
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
                    LogBatteryInfo("Applying prepared battery schedule");
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
                LogBatteryInfo("Restoring EMS after {0} period", period.ChargeType);

                // Turn EMS back on
                var emsState = _entities.Switch.Ems.State;
                if (emsState == "off")
                {
                    LogBatteryInfo("Turning EMS back on");
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
        /// Calculates the initial charging schedule based on energy prices and battery state
        /// Implements 3-checkpoint strategy: morning check, charge moment, evening check
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

            // Get basic charge and discharge timeslots
            var (chargeStart, chargeEnd) = PriceHelper.GetLowestPriceTimeslot(pricesToday, 3);
            var (dischargeStart, dischargeEnd) = PriceHelper.GetHighestPriceTimeslot(pricesToday, 1);

            var periods = new List<ChargingPeriod>();

            // CHECKPOINT 1: Morning check (before charge) - add discharge if SOC > 40%
            var currentSOC = GetCurrentBatterySOC();
            var morningCheckTime = chargeStart.AddHours(-2); // 2 hours before charge start
            
            if (currentSOC > 40.0 && morningCheckTime.TimeOfDay > TimeSpan.FromHours(6)) // Not too early
            {
                // Find morning high price period for discharge
                var morningPrices = pricesToday.Where(p => p.Key.TimeOfDay >= TimeSpan.FromHours(6) && 
                                                          p.Key.TimeOfDay < chargeStart.TimeOfDay).ToList();
                
                if (morningPrices.Count > 0)
                {
                    var morningHighPrice = morningPrices.OrderByDescending(p => p.Value).First();
                    var morningDischargeStart = morningHighPrice.Key.TimeOfDay;
                    var morningDischargeEnd = morningDischargeStart.Add(TimeSpan.FromHours(1));

                    periods.Add(new ChargingPeriod
                    {
                        StartTime = morningDischargeStart,
                        EndTime = morningDischargeEnd,
                        ChargeType = BatteryChargeType.Discharge,
                        PowerInWatts = 8000
                    });

                    LogBatteryInfo("Added morning discharge at {0} (SOC: {1:F1}%, Price: €{2:F3})", 
                        morningDischargeStart.ToString(@"hh\:mm"), currentSOC, morningHighPrice.Value);
                }
            }

            // CHECKPOINT 2: Always add charge moment at lowest price
            periods.Add(new ChargingPeriod
            {
                StartTime = chargeStart.TimeOfDay,
                EndTime = chargeEnd.TimeOfDay,
                ChargeType = BatteryChargeType.Charge,
                PowerInWatts = 8000
            });

            // CHECKPOINT 3: Add evening discharge (will be checked and potentially moved later)
            periods.Add(new ChargingPeriod
            {
                StartTime = dischargeStart.TimeOfDay,
                EndTime = dischargeEnd.TimeOfDay,
                ChargeType = BatteryChargeType.Discharge,
                PowerInWatts = 8000
            });

            // Schedule evening check to potentially move discharge to tomorrow morning
            ScheduleEveningPriceCheck(dischargeStart);

            var schedule = new ChargingSchema { Periods = periods };

            LogBatteryInfo("Created schedule with {0} periods: {1}", 
                periods.Count, 
                string.Join(", ", periods.Select(p => $"{p.ChargeType} {p.StartTime:hh\\:mm}-{p.EndTime:hh\\:mm}")));

            return schedule;
        }

        /// <summary>
        /// Schedules an evening price check to potentially move discharge from tonight to tomorrow morning
        /// This implements the third checkpoint: comparing tomorrow morning vs tonight evening prices
        /// </summary>
        /// <param name="currentDischargeTime">The currently scheduled discharge time for tonight</param>
        private void ScheduleEveningPriceCheck(DateTime currentDischargeTime)
        {
            // Schedule the check 30 minutes before the evening discharge period
            var checkTime = currentDischargeTime.AddMinutes(-30);
            
            if (checkTime > DateTime.Now)
            {
                _scheduler.RunAt(checkTime, () => EvaluateEveningToMorningShift());
                LogBatteryInfo("Scheduled evening price check at {0} to evaluate discharge timing", 
                    checkTime.ToString("HH:mm"));
            }
        }

        /// <summary>
        /// Evaluates whether to move tonight's discharge to tomorrow morning based on price comparison
        /// If tomorrow morning price > tonight evening price, reschedule discharge for tomorrow morning
        /// </summary>
        private void EvaluateEveningToMorningShift()
        {
            try
            {
                _logger.LogInformation("Evaluating evening to morning discharge shift");

                var pricesToday = _priceHelper.PricesToday;
                var pricesTomorrow = _priceHelper.PricesTomorrow;

                if (pricesToday == null || pricesTomorrow == null)
                {
                    _logger.LogWarning("Missing price data for evening evaluation");
                    return;
                }

                // Get current evening discharge price (highest price today)
                var eveningPrice = pricesToday.OrderByDescending(p => p.Value).First();
                
                // Get tomorrow morning high price period (6-12 AM)
                var tomorrowMorningPrices = pricesTomorrow.Where(p => 
                    p.Key.TimeOfDay >= TimeSpan.FromHours(6) && 
                    p.Key.TimeOfDay < TimeSpan.FromHours(12)).ToList();

                if (tomorrowMorningPrices.Count == 0)
                {
                    _logger.LogWarning("No tomorrow morning prices available");
                    return;
                }

                var bestMorningPrice = tomorrowMorningPrices.OrderByDescending(p => p.Value).First();

                LogBatteryInfo("Price comparison - Tonight: €{0:F3} at {1}, Tomorrow morning: €{2:F3} at {3}",
                    eveningPrice.Value, eveningPrice.Key.ToString("HH:mm"),
                    bestMorningPrice.Value, bestMorningPrice.Key.ToString("HH:mm"));

                // If tomorrow morning price is higher than tonight, reschedule
                if (bestMorningPrice.Value > eveningPrice.Value)
                {
                    LogBatteryInfo("Tomorrow morning price is higher, rescheduling discharge to tomorrow morning");
                    RescheduleDischargeTomorrowMorning(bestMorningPrice.Key);
                }
                else
                {
                    LogBatteryInfo("Keeping tonight's discharge schedule (better price)");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during evening price evaluation");
            }
        }

        /// <summary>
        /// Reschedules the current evening discharge to tomorrow morning
        /// </summary>
        /// <param name="tomorrowMorningTime">The optimal time tomorrow morning for discharge</param>
        private void RescheduleDischargeTomorrowMorning(DateTime tomorrowMorningTime)
        {
            try
            {
                // Update the prepared schedule to remove tonight's discharge
                if (_preparedSchedule?.Periods != null)
                {
                    // Remove evening discharge periods
                    var periodsToKeep = _preparedSchedule.Periods
                        .Where(p => p.ChargeType != BatteryChargeType.Discharge || 
                                   p.StartTime < TimeSpan.FromHours(17)) // Keep morning discharge if any
                        .ToList();

                    _preparedSchedule.Periods = periodsToKeep;
                    SavePreparedSchedule(_preparedSchedule);
                    
                    _logger.LogInformation("Removed tonight's discharge from schedule");
                }

                // Schedule the new discharge for tomorrow morning
                var tomorrowDischargeTime = tomorrowMorningTime.AddMinutes(-5); // 5 minutes before to prepare
                _scheduler.RunAt(tomorrowDischargeTime, () => ExecuteTomorrowMorningDischarge(tomorrowMorningTime));
                
                LogBatteryInfo("Scheduled tomorrow morning discharge at {0}", 
                    tomorrowMorningTime.ToString("HH:mm"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error rescheduling discharge to tomorrow morning");
            }
        }

        /// <summary>
        /// Executes the discharge that was moved to tomorrow morning
        /// </summary>
        /// <param name="dischargeTime">The time for the discharge period</param>
        private void ExecuteTomorrowMorningDischarge(DateTime dischargeTime)
        {
            try
            {
                LogBatteryInfo("Executing tomorrow morning discharge period at {0}", dischargeTime.ToString("HH:mm"));

                // Create a temporary schedule with just the morning discharge
                var morningDischargeSchedule = new ChargingSchema
                {
                    Periods = new List<ChargingPeriod>
                    {
                        new ChargingPeriod
                        {
                            StartTime = dischargeTime.TimeOfDay,
                            EndTime = dischargeTime.TimeOfDay.Add(TimeSpan.FromHours(1)),
                            ChargeType = BatteryChargeType.Discharge,
                            PowerInWatts = 8000
                        }
                    }
                };

                // Apply the discharge schedule (this will handle EMS management)
                var period = morningDischargeSchedule.Periods.First();
                PrepareForBatteryPeriod(period);

                // Schedule EMS restoration after the period
                var restoreTime = dischargeTime.AddHours(1).AddMinutes(1);
                _scheduler.RunAt(restoreTime, () => RestoreEmsAfterPeriod(period));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing tomorrow morning discharge");
            }
        }

        #region Helper Methods

        /// <summary>
        /// Logs a message to both console (with [BATTERY] prefix) and the standard logger
        /// </summary>
        /// <param name="message">The message to log</param>
        /// <param name="args">Optional format arguments</param>
        private void LogBatteryInfo(string message, params object[] args)
        {
            var formattedMessage = args.Length > 0 ? string.Format(message, args) : message;
            Console.WriteLine($"[BATTERY] {formattedMessage}");
            _logger.LogInformation(message, args);
        }

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
