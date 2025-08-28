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
    using Microsoft.Extensions.Options;

    /// <summary>
    /// Adjust the energy appliances schedule based on the power prices
    /// </summary>
    [NetDaemonApp]
    public class Battery
    {
        // Dependencies
        private readonly ILogger<Battery> _logger;
        private readonly Entities _entities;
        private readonly BatteryOptions _options;
        private readonly IPriceHelper _priceHelper;
        private readonly INetDaemonScheduler _scheduler;
        private readonly SAJPowerBatteryApi _saiPowerBatteryApi;

        // State
        private ChargingSchema? _preparedSchedule;
        private readonly object _scheduleLock = new();
        private readonly bool _simulationMode;
        private bool _applyRetryScheduled;

        /// <summary>
        /// Initializes a new instance of the <see cref="Battery"/> class.
        /// </summary>
        public Battery(IHaContext ha, INetDaemonScheduler scheduler, ILogger<Battery> logger, IPriceHelper priceHelper, SAJPowerBatteryApi api, IOptions<BatteryOptions> options)
        {
            _logger = logger;
            _entities = new Entities(ha);
            _priceHelper = priceHelper;
            _scheduler = scheduler;
            _saiPowerBatteryApi = api;
            _options = options.Value;
            _simulationMode = _options.SimulationMode || Debugger.IsAttached;

            if (Debugger.IsAttached)
            {
                LogStatus("Debugger attached; Simulation mode forcibly OFF for live API testing");
            }

            LogStatus("Started battery energy program with 3-checkpoint strategy");

            // Clear battery state at startup for fresh testing/operation
            ClearBatteryState();

            // Check if the token is valid (diagnostic only)
            _saiPowerBatteryApi.IsTokenValid(true);

            if (Debugger.IsAttached) {
                // Run schedule preparation immediately for debugging (non-blocking)
                _ = PrepareScheduleForDayAsync();
            }
            else {
                // Start battery mode monitoring every 5 minutes
                StartBatteryModeMonitoring();

                // Start the scheduler and check if we have a active schedule for today or need to create one
                var dailyTime = DateTime.Today.AddDays(1).AddMinutes(5);
                scheduler.RunEvery(TimeSpan.FromDays(1), dailyTime, () => { _ = PrepareScheduleForDayAsync(); });

                var existingSchedule = GetPreparedSchedule();
                if (existingSchedule != null) {
                    LogStatus("Resuming prepared schedule", "Found existing prepared schedule for today, setting up EMS management");
                    lock (_scheduleLock) {
                        _preparedSchedule = existingSchedule;
                    }

                    // If no upcoming windows remain (e.g., late restart), recompute today’s schedule
                    var upcoming = BuildMergedEmsWindows(existingSchedule);
                    if (upcoming.Count == 0) {
                        LogStatus("Recalculating schedule", "Prepared schedule has no upcoming windows; recalculating schedule for today");
                        _ = PrepareScheduleForDayAsync();
                    }
                    else {
                        ScheduleEmsManagementForPeriods(existingSchedule);
                    }
                }
                else {
                    LogStatus("Creating schedule", "No existing schedule found, preparing new schedule");
                    _ = PrepareScheduleForDayAsync();
                }
            }
        }

        #region Core Logic

        /// <summary>
        /// Prepares the daily battery schedule and schedules EMS management around charge/discharge periods.
        /// </summary>
        private async Task PrepareScheduleForDayAsync()
        {
            try
            {
                LogStatus("Calculating schedule");

                // Calculate the schedule for today
                var schedule = CalculateInitialChargingSchedule();
                if (schedule == null)
                {
                    // Retry shortly in case price data arrives a bit later
                    LogStatus("No price data yet - retrying", "Could not calculate schedule for today");

                    _scheduler.RunIn(TimeSpan.FromMinutes(10), () => { _ = PrepareScheduleForDayAsync(); });
                    return;
                }

                // Store the prepared schedule (don't apply yet)
                lock (_scheduleLock)
                {
                    _preparedSchedule = schedule;
                }
                SavePreparedSchedule(schedule);

                // Schedule EMS management for each charge/discharge period (merged windows)
                ScheduleEmsManagementForPeriods(schedule);

                var nextEventSummary = BuildNextEventSummary(schedule);
                LogStatus(nextEventSummary, $"Prepared {schedule.Periods.Count} periods for today");

                // Optionally, in simulation mode apply immediately for visibility
                if (_simulationMode)
                {
                    await ApplyChargingScheduleAsync(schedule, simulateOnly: true);
                }
            }
            catch (Exception ex)
            {
                LogStatus("Error preparing", $"Error preparing daily schedule: {ex.Message}");
            }
        }

        /// <summary>
        /// Applies the given charging schema to the battery system via the SAJ Power API
        /// </summary>
        private async Task ApplyChargingScheduleAsync(ChargingSchema chargingSchedule, bool simulateOnly)
        {
            if (chargingSchedule.Periods == null || chargingSchedule.Periods.Count == 0)
            {
                LogStatus("No schedule to apply", "No charging schedule to apply");
                return;
            }

            // Check if battery is in EMS mode before applying schedule
            if (!simulateOnly && _saiPowerBatteryApi.IsConfigured)
            {
                try
                {
                    var currentUserMode = await _saiPowerBatteryApi.GetUserModeAsync();
                    if (currentUserMode == BatteryUserMode.EmsMode)
                    {
                        var retryAt = RoundUpToNextFiveMinute(DateTime.Now);

                        if (!_applyRetryScheduled)
                        {
                            _applyRetryScheduled = true;

                            _scheduler.RunAt(retryAt, () =>
                            {
                                _applyRetryScheduled = false;
                                LogStatus("Applying schedule");
                                _ = ApplyChargingScheduleAsync(chargingSchedule, simulateOnly: _simulationMode);
                            });

                            LogStatus("EMS Mode active - retry scheduled");
                        }
                        else
                        {
                            LogStatus("EMS Mode active", "Schedule apply blocked by EMS Mode; retry already scheduled");
                        }

                        return;
                    }
                    else
                    {
                        var modeDescription = currentUserMode.ToApiString();
                        LogStatus("Applying schedule");
                    }
                }
                catch (Exception ex)
                {
                    LogStatus("Applying schedule", $"Failed to verify battery user mode; proceeding with schedule application: {ex.Message}");
                }
            }

            try
            {
                var allPeriods = chargingSchedule.Periods.ToList();
                if (allPeriods.Count == 0)
                {
                    LogStatus("No valid periods in schedule", "No valid periods in schedule");
                    return;
                }

                // Dynamic optimization: compute required charge time from SOC, capacity and max power
                var soc = GetCurrentBatterySoc();
                var requiredChargeMinutes = CalculateRequiredChargeMinutes(soc);

                // Compute remaining scheduled charge minutes from now
                var now = DateTime.Now.TimeOfDay;
                var remainingScheduledChargeMinutes = allPeriods
                    .Where(p => p.ChargeType == BatteryChargeType.Charge)
                    .Sum(p => Math.Max(0, (int)Math.Ceiling((p.EndTime - (p.StartTime > now ? p.StartTime : now)).TotalMinutes)));

                // If we have more scheduled charge time than needed, check if we should recalculate optimal window
                var scheduleToApply = chargingSchedule;
                if (remainingScheduledChargeMinutes > 0)
                {
                    if (remainingScheduledChargeMinutes > requiredChargeMinutes)
                    {
                        var requiredHours = requiredChargeMinutes / 60.0; // Allow fractional hours
                        var currentScheduledHours = (int)Math.Ceiling(remainingScheduledChargeMinutes / 60.0);

                        // If required time is significantly different (more than 1 hour difference), recalculate optimal window
                        // Also recalculate if we need less than 1 hour but have much more scheduled
                        var hoursDifference = Math.Abs(currentScheduledHours - Math.Ceiling(requiredHours));
                        var shouldRecalculate = (hoursDifference > 1 || (requiredHours < 1.0 && currentScheduledHours > 1)) && _priceHelper.PricesToday != null;

                        // 🔍 DIAGNOSTIC: Log recalculation decision logic
                        if (Debugger.IsAttached || _simulationMode)
                        {
                            LogStatus($"🔍 Recalculation Decision",
                                $"Required: {requiredHours:F1}h, Scheduled: {currentScheduledHours}h, HoursDiff: {hoursDifference}, " +
                                $"Condition1: {hoursDifference > 1}, Condition2: {requiredHours < 1.0 && currentScheduledHours > 1}, " +
                                $"PricesAvailable: {_priceHelper.PricesToday != null}, ShouldRecalc: {shouldRecalculate}");
                        }

                        if (shouldRecalculate)
                        {
                            LogStatus($"SOC {soc:F1}%, recalculating optimal window",
                                $"Need {requiredHours:F1}h (was {currentScheduledHours}h), finding new optimal {requiredHours:F1}-hour window");

                            // Get the optimal window for the required hours (fractional allowed)
                            var (newChargeStart, newChargeEnd) = PriceHelper.GetLowestPriceTimeslot(_priceHelper.PricesToday!, requiredHours);

                            // Create new schedule with optimal window
                            var newPeriods = chargingSchedule.Periods
                                .Where(p => p.ChargeType != BatteryChargeType.Charge) // Keep non-charge periods
                                .ToList();

                            // Add the new optimal charge period (for tomorrow)
                            newPeriods.Add(CreateChargePeriod(newChargeStart.TimeOfDay, newChargeEnd.TimeOfDay, GetChargeWeekdayPattern()));

                            scheduleToApply = new ChargingSchema { Periods = newPeriods };

                            var suffix = BuildNextChargeSuffix(scheduleToApply);
                            var detail = $"SOC {soc:F1}%, recalculated to optimal {requiredHours:F1}h window: {newChargeStart:HH:mm}-{newChargeEnd:HH:mm} | {suffix}";
                            LogStatus($"SOC {soc:F1}%, new optimal window", detail);
                        }
                        else
                        {
                            // Small difference, just trim the existing schedule
                            var (adjusted, summary) = TrimChargePeriodsToTotalMinutes(chargingSchedule, requiredChargeMinutes);
                            scheduleToApply = adjusted ?? chargingSchedule;
                            var suffix = BuildNextChargeSuffix(scheduleToApply);
                            var detail = $"SOC {soc:F1}%, need ~{requiredChargeMinutes}m to full, had {remainingScheduledChargeMinutes}m scheduled. {summary} | {suffix}";
                            LogStatus($"SOC {soc:F1}%, optimized schedule", detail);
                        }
                    }
                    else
                    {
                        var suffix = BuildNextChargeSuffix(chargingSchedule);
                        var detail = $"SOC {soc:F1}%, need ~{requiredChargeMinutes}m, scheduled {remainingScheduledChargeMinutes}m (<= needed) | {suffix}";
                        LogStatus($"SOC {soc:F1}%, schedule fits", detail);
                    }
                }

                // Idempotency: compare with current applied schedule using the adjusted schedule
                var currentApplied = GetCurrentAppliedSchedule();
                if (!simulateOnly && currentApplied != null && scheduleToApply.IsEquivalentTo(currentApplied))
                {
                    var nextEventSummary = BuildNextEventSummary(scheduleToApply);
                    LogStatus($"{nextEventSummary} (unchanged)", "Schedule unchanged from current applied; skipping API call");
                    return;
                }

                var orderedPeriods = scheduleToApply.Periods
                    .OrderBy(p => p.ChargeType == BatteryChargeType.Charge ? 0 : 1) // Charge periods first, then discharge
                    .ThenBy(p => p.StartTime) // Then by start time within each type
                    .ToList();

                // 🔍 DIAGNOSTIC: Log period ordering sent to SAJ API
                if (Debugger.IsAttached)
                {
                    LogStatus("🔍 DIAGNOSTIC - API Period Ordering", "Checking period order sent to SAJ API");
                    for (int i = 0; i < orderedPeriods.Count; i++)
                    {
                        var period = orderedPeriods[i];
                        LogStatus($"  API Position {i+1}: {period.ChargeType} {period.StartTime:hh\\:mm}-{period.EndTime:hh\\:mm}");
                    }
                }

                var scheduleParameters = SAJPowerBatteryApi.BuildBatteryScheduleParameters(orderedPeriods);
                var liveWrite = !simulateOnly && _saiPowerBatteryApi.IsConfigured;
                bool saved;
                if (liveWrite)
                {
                    saved = await _saiPowerBatteryApi.SaveBatteryScheduleAsync(scheduleParameters);
                }
                else
                {
                    if (!simulateOnly && !_saiPowerBatteryApi.IsConfigured)
                    {
                        LogStatus("SAJ API not configured; simulating", $"Skipping live API write: SAJ API not configured ({_saiPowerBatteryApi.ConfigurationError}); simulating apply");
                    }
                    saved = true; // treat as success for simulated apply
                }

                if (saved)
                {
                    if (liveWrite)
                    {
                        SaveCurrentAppliedSchedule(scheduleToApply);
                    }

                    var nextEventSummary = BuildNextEventSummary(scheduleToApply);
                    LogStatus($"{nextEventSummary}{(liveWrite ? string.Empty : " (sim)")}",
                        $"{scheduleToApply.Periods.Count} periods");

                    // Always update the schedule display entities, even in simulation mode
                    var chargePeriods = scheduleToApply.Periods.Where(p => p.ChargeType == BatteryChargeType.Charge).ToList();
                    var dischargePeriods = scheduleToApply.Periods.Where(p => p.ChargeType == BatteryChargeType.Discharge).ToList();

                    try
                    {
                        if (chargePeriods.Count > 0)
                        {
                            var firstCharge = chargePeriods.First();
                            var lastCharge = chargePeriods.Last();
                            var dayAbbreviation = GetDayAbbreviationFromPattern(firstCharge.Weekdays);
                            var dayDisplay = !string.IsNullOrEmpty(dayAbbreviation) ? $" {dayAbbreviation}" : "";
                            var chargeSchedule = $"{firstCharge.StartTime.ToString(@"hh\:mm")}-{lastCharge.EndTime.ToString(@"hh\:mm")}{dayDisplay}";
                            _entities.InputText.BatteryChargeSchedule.SetValue(chargeSchedule);
                        }
                        else
                        {
                            _entities.InputText.BatteryChargeSchedule.SetValue("No charge scheduled");
                        }

                        if (dischargePeriods.Count > 0)
                        {
                            var firstDischarge = dischargePeriods.First();
                            var lastDischarge = dischargePeriods.Last();
                            var dayAbbreviation = GetDayAbbreviationFromPattern(firstDischarge.Weekdays);
                            var dayDisplay = !string.IsNullOrEmpty(dayAbbreviation) ? $" {dayAbbreviation}" : "";
                            var dischargeSchedule = $"{firstDischarge.StartTime.ToString(@"hh\:mm")}-{lastDischarge.EndTime.ToString(@"hh\:mm")}{dayDisplay}";
                            _entities.InputText.BatteryDischargeSchedule.SetValue(dischargeSchedule);
                        }
                        else
                        {
                            _entities.InputText.BatteryDischargeSchedule.SetValue("No discharge scheduled");
                        }
                    }
                    catch (Exception ex)
                    {
                        LogStatus("Schedule display update failed", $"Failed to update schedule display entities: {ex.Message}");
                    }
                }
                else
                {
                    LogStatus("Failed to apply", "Failed to apply charging schedule via SAJ API");
                }
            }
            catch (Exception ex)
            {
                LogStatus("Error applying", $"Error applying charging schedule: {ex.Message}");
            }
        }

        /// <summary>
        /// Calculates the initial charging schedule based on energy prices and battery state
        /// Implements enhanced 3-checkpoint strategy with cross-day optimization and SOC-aware late charging
        /// </summary>
        private ChargingSchema? CalculateInitialChargingSchedule()
        {
            var pricesToday = _priceHelper.PricesToday;
            if (pricesToday == null || pricesToday.Count < 3)
            {
                LogStatus("Not enough price data", "Not enough price data to set battery schedule");
                return null;
            }

            var periods = new List<ChargingPeriod>();
            var now = DateTime.Now;
            var currentSoc = GetCurrentBatterySoc();

            // Enhanced optimization: Consider cross-day price analysis when available
            var (optimizedChargeStart, optimizedChargeEnd, optimizedDischargeStart, optimizedDischargeEnd) = 
                CalculateOptimalChargingWindows(pricesToday, _priceHelper.PricesTomorrow, currentSoc);

            // CHECKPOINT 1: Morning check (before charge) - add discharge if SOC > configured threshold and still relevant now
            var morningCheckTime = optimizedChargeStart.AddHours(-_options.MorningCheckOffsetHours);
            var morningWindowStart = TimeSpan.FromHours(_options.MorningWindowStartHour);
            if (currentSoc > _options.MorningSocThresholdPercent && morningCheckTime.TimeOfDay > morningWindowStart && now < optimizedChargeStart)
            {
                var morningPrices = pricesToday.Where(p => p.Key.TimeOfDay >= morningWindowStart &&
                                                          p.Key.TimeOfDay < optimizedChargeStart.TimeOfDay).ToList();

                if (morningPrices.Count > 0)
                {
                    var morningHighPrice = morningPrices.OrderByDescending(p => p.Value).First();
                    var morningDischargeStart = morningHighPrice.Key.TimeOfDay;
                    var morningDischargeEnd = morningDischargeStart.Add(TimeSpan.FromHours(1));

                    // Skip if the period already fully passed; trim if we're in the middle of it
                    if (now.TimeOfDay < morningDischargeEnd)
                    {
                        var start = now.TimeOfDay > morningDischargeStart ? now.TimeOfDay : morningDischargeStart;
                        var morningPattern = _options.EnableDaySpecificScheduling ? 
                            GetSingleDayPattern(DateTime.Today.DayOfWeek) : 
                            GetAllDaysPattern();
                        var period = CreateMorningDischargePeriod(start, 1.0, morningPattern);
                        period.EndTime = morningDischargeEnd; // Override the default end time to maintain original logic
                        periods.Add(period);

                        LogStatus("Added morning discharge at {0} (SOC: {1:F1}% > {2:F1}%, Price: €{3:F3})",
                            start.ToString(@"hh\:mm"), currentSoc, _options.MorningSocThresholdPercent, morningHighPrice.Value);
                    }
                }
            }

            // CHECKPOINT 2: Add optimized charge period (for tomorrow)
            periods.Add(CreateChargePeriod(optimizedChargeStart.TimeOfDay, optimizedChargeEnd.TimeOfDay, GetChargeWeekdayPattern()));

            // CHECKPOINT 3: Add optimized discharge period (for today) with SOC target
            periods.Add(CreateEveningDischargePeriodWithTargetSoc(optimizedDischargeStart.TimeOfDay, currentSoc, GetDischargeWeekdayPattern()));

            // Schedule evening check to potentially move discharge to tomorrow morning
            ScheduleEveningPriceCheck(optimizedDischargeStart);

        // Validate and fix any overlapping periods before creating the schedule
        periods = ValidateAndFixOverlaps(periods);

        var schedule = new ChargingSchema { Periods = periods };

        var periodsDescription = string.Join(", ", periods.Select(p => $"{p.ChargeType} {p.StartTime.ToString(@"hh\:mm")}-{p.EndTime.ToString(@"hh\:mm")}"));
        LogStatus($"Created schedule with {periods.Count} periods", periodsDescription);

        // 🔍 DIAGNOSTIC: Log detailed period analysis for charge/discharge debugging
        if (Debugger.IsAttached)
        {
            LogStatus("🔍 DIAGNOSTIC - Period Analysis", "Analyzing created periods for charge/discharge logic verification");
            for (int i = 0; i < periods.Count; i++)
            {
                var period = periods[i];
                var priceAtTime = pricesToday.FirstOrDefault(p => p.Key.TimeOfDay == period.StartTime);
                var priceInfo = priceAtTime.Key != default ? $"Price: €{priceAtTime.Value:F3}" : "Price: N/A";
                LogStatus($"  Period {i+1}: {period.ChargeType} {period.StartTime:hh\\:mm}-{period.EndTime:hh\\:mm}",
                    $"Power: {period.PowerInWatts}W, {priceInfo}");
            }

            // Log charge vs discharge period pricing validation
            var chargePeriods = periods.Where(p => p.ChargeType == BatteryChargeType.Charge).ToList();
            var dischargePeriods = periods.Where(p => p.ChargeType == BatteryChargeType.Discharge).ToList();

            LogStatus($"🔍 Validation: {chargePeriods.Count} charge periods, {dischargePeriods.Count} discharge periods");

            if (chargePeriods.Any())
            {
                var avgChargePrice = chargePeriods.Select(p => pricesToday.FirstOrDefault(pr => pr.Key.TimeOfDay == p.StartTime).Value).Average();
                LogStatus($"  Charge periods avg price: €{avgChargePrice:F3}");
            }

            if (dischargePeriods.Any())
            {
                var avgDischargePrice = dischargePeriods.Select(p => pricesToday.FirstOrDefault(pr => pr.Key.TimeOfDay == p.StartTime).Value).Average();
                LogStatus($"  Discharge periods avg price: €{avgDischargePrice:F3}");
            }
        }
        
        return schedule;
        }

        /// <summary>
        /// Enhanced optimization method that considers cross-day pricing and SOC-aware scheduling
        /// Returns optimal charge and discharge windows considering current battery state and multi-day prices
        /// </summary>
        private (DateTime ChargeStart, DateTime ChargeEnd, DateTime DischargeStart, DateTime DischargeEnd) 
            CalculateOptimalChargingWindows(IDictionary<DateTime, double> pricesToday, IDictionary<DateTime, double>? pricesTomorrow, double currentSoc)
        {
            var now = DateTime.Now;
            
            // Filter out past prices for discharge optimization (only look at future times)
            var futurePrices = pricesToday.Where(p => p.Key > now).ToDictionary(p => p.Key, p => p.Value);
            
            // Default to traditional single-day optimization
            var (defaultChargeStart, defaultChargeEnd) = PriceHelper.GetLowestPriceTimeslot(pricesToday, 3);
            var (defaultDischargeStart, defaultDischargeEnd) = futurePrices.Any() ? 
                PriceHelper.GetHighestPriceTimeslot(futurePrices, 1) : 
                PriceHelper.GetHighestPriceTimeslot(pricesToday, 1);

            // Enhanced optimization when SOC is high and tomorrow's prices are available
            if (currentSoc > _options.HighSocThresholdPercent && pricesTomorrow != null && pricesTomorrow.Any())
            {
                LogStatus($"High SOC optimization (SOC: {currentSoc:F1}% > {_options.HighSocThresholdPercent:F1}%)", 
                    "Analyzing cross-day optimization opportunities");

                // Calculate required charge time based on current SOC
                var requiredChargeMinutes = CalculateRequiredChargeMinutes(currentSoc);
                var requiredChargeHours = requiredChargeMinutes / 60.0;

                // Find today's remaining hours (from now until midnight)
                var remainingTodayHours = (24 - now.Hour) - (now.Minute / 60.0);
                
                // Combine today's remaining prices with tomorrow's prices for analysis
                var combinedPrices = new Dictionary<DateTime, double>();
                
                // Add remaining today's prices
                foreach (var price in pricesToday.Where(p => p.Key > now))
                {
                    combinedPrices[price.Key] = price.Value;
                }
                
                // Add tomorrow's prices
                if (pricesTomorrow != null)
                {
                    foreach (var price in pricesTomorrow)
                    {
                        combinedPrices[price.Key] = price.Value;
                    }
                }

                if (combinedPrices.Count >= 6) // Ensure we have enough data for analysis
                {
                    // Find the absolute cheapest charging window in the next 24-48 hours
                    var (crossDayChargeStart, crossDayChargeEnd) = PriceHelper.GetLowestPriceTimeslot(combinedPrices, requiredChargeHours);
                    
                    // Check if cross-day optimization provides significant savings
                    var todayBestPrice = pricesToday.Where(p => p.Key > now).OrderBy(p => p.Value).Take((int)Math.Ceiling(requiredChargeHours)).Average(p => p.Value);
                    var crossDayPrice = combinedPrices.Where(p => p.Key >= crossDayChargeStart && p.Key <= crossDayChargeEnd).Average(p => p.Value);
                    
                    var savings = (todayBestPrice - crossDayPrice) / todayBestPrice;
                    
                    // If savings are significant (>5%) and the battery can bridge to the optimal time, use cross-day optimization
                    if (savings > 0.05 && CanBatteryBridgeToTime(currentSoc, crossDayChargeStart))
                    {
                        LogStatus($"Cross-day optimization selected", 
                            $"Savings: {savings:P1} (€{todayBestPrice:F3} → €{crossDayPrice:F3}), Charge: {crossDayChargeStart:HH:mm}-{crossDayChargeEnd:HH:mm}");
                        
                        // Find optimal discharge window in the higher price periods (only future times)
                        var highPricePeriods = combinedPrices.Where(p => p.Value > crossDayPrice * 1.15 && p.Key > now).ToDictionary(p => p.Key, p => p.Value);
                        
                        if (highPricePeriods.Any())
                        {
                            var (optimalDischargeStart, optimalDischargeEnd) = PriceHelper.GetHighestPriceTimeslot(highPricePeriods, 1);
                            return (crossDayChargeStart, crossDayChargeEnd, optimalDischargeStart, optimalDischargeEnd);
                        }
                    }
                }
            }

            // Log why we're using default optimization
            var reason = currentSoc <= _options.HighSocThresholdPercent ? 
                $"SOC too low ({currentSoc:F1}% ≤ {_options.HighSocThresholdPercent:F1}%)" :
                "Tomorrow's prices not available or insufficient savings";
                
            LogStatus("Using single-day optimization", reason);
            
            return (defaultChargeStart, defaultChargeEnd, defaultDischargeStart, defaultDischargeEnd);
        }

        /// <summary>
        /// Checks if the battery can bridge to the specified charge time based on current SOC and consumption patterns
        /// </summary>
        private bool CanBatteryBridgeToTime(double currentSoc, DateTime targetChargeTime)
        {
            var hoursUntilCharge = (targetChargeTime - DateTime.Now).TotalHours;
            
            // Rough estimate: assume ~5% SOC consumption per day for a typical household
            // This should ideally be configurable or learned from historical data
            var estimatedSocDrop = (hoursUntilCharge / 24.0) * _options.DailyConsumptionSocPercent;
            var projectedSoc = currentSoc - estimatedSocDrop;
            
            // Ensure we maintain at least minimum SOC
            var canBridge = projectedSoc >= _options.MinimumSocPercent;
            
            if (Debugger.IsAttached)
            {
                LogStatus($"🔍 Bridge analysis: {hoursUntilCharge:F1}h until charge", 
                    $"Current: {currentSoc:F1}%, Estimated drop: {estimatedSocDrop:F1}%, Projected: {projectedSoc:F1}%, Min required: {_options.MinimumSocPercent:F1}%, Can bridge: {canBridge}");
            }
            
            return canBridge;
        }

        #endregion

        #region EMS Management

        /// <summary>
        /// Validates battery mode and turns off EMS if safe to do so.
        /// Returns true if EMS was successfully turned off or was already off.
        /// Returns false if operation should be retried later.
        /// </summary>
        private async Task<bool> ValidateAndTurnOffEmsAsync(string context, ChargingPeriod? period = null)
        {
            var emsState = _entities.Switch.Ems.State;
            if (emsState != "on")
            {
                LogStatus("EMS is already off");
                return true;
            }

            // Guard: if battery is in EMS Mode OR we cannot determine (including API not configured), do not turn off EMS and schedule a retry
            string? blockReason = null;
            if (!_saiPowerBatteryApi.IsConfigured)
            {
                blockReason = "Mode unknown (API not configured)";
            }
            else
            {
                try
                {
                    var mode = await _saiPowerBatteryApi.GetUserModeAsync();
                    if (mode is BatteryUserMode.EmsMode or BatteryUserMode.Unknown)
                    {
                        blockReason = mode == BatteryUserMode.EmsMode ? "EMS Mode active" : "Mode unknown";
                    }
                }
                catch (Exception ex)
                {
                    LogStatus("Mode query failed", $"Failed to get battery user mode: {ex.Message}");
                    blockReason = "Mode unknown (query failed)";
                }
            }

            if (blockReason != null)
            {
                ScheduleRetryWithReason(blockReason, period);
                return false;
            }

            LogStatus($"Turning off EMS before {context}");
            _entities.Switch.Ems.TurnOff();
            await Task.Delay(TimeSpan.FromSeconds(5));

            return true;
        }

        /// <summary>
        /// Schedules a retry for preparing battery period when EMS cannot be turned off.
        /// </summary>
        private void ScheduleRetryWithReason(string blockReason, ChargingPeriod? period, ChargingSchema? schedule = null, string? label = null)
        {
            var retryAt = RoundUpToNextFiveMinute(DateTime.Now);
            if (!_applyRetryScheduled)
            {
                _applyRetryScheduled = true;
                _scheduler.RunAt(retryAt, () =>
                {
                    _applyRetryScheduled = false;
                    LogStatus($"{blockReason}");

                    if (schedule != null && label != null)
                        _ = PrepareForScheduleAsync(schedule, label);
                    else
                        _ = PrepareForBatteryPeriodAsync(period);
                });

                LogStatus($"{blockReason} - retry scheduled");
            }
            else
            {
                LogStatus($"{blockReason}");
            }
        }

        /// <summary>
        /// Build merged EMS windows from periods to avoid flapping between adjacent/overlapping periods.
        /// </summary>
        private List<(DateTime windowStart, DateTime windowEnd)> BuildMergedEmsWindows(ChargingSchema schedule)
        {
            var today = DateTime.Today;
            var windows = new List<(DateTime s, DateTime e)>();
            foreach (var p in schedule.Periods)
            {
                var start = today.Add(p.StartTime).AddMinutes(-_options.EmsPrepMinutesBefore);
                var end = today.Add(p.EndTime).AddMinutes(_options.EmsRestoreMinutesAfter);
                if (end <= DateTime.Now) continue; // skip past
                if (start < DateTime.Now) start = DateTime.Now.AddSeconds(1); // guard in near-past
                windows.Add((start, end));
            }

            // Sort and merge overlapping/contiguous windows
            var merged = new List<(DateTime, DateTime)>();
            foreach (var w in windows.OrderBy(w => w.s))
            {
                if (merged.Count == 0) { merged.Add(w); continue; }
                var (ms, me) = merged[^1];
                if (w.s <= me.AddMinutes(1))
                {
                    // overlap or within 1 minute gap — extend
                    merged[^1] = (ms, w.e > me ? w.e : me);
                }
                else
                {
                    merged.Add(w);
                }
            }
            return merged;
        }

        /// <summary>
        /// Schedules EMS shutdown before each merged window and re-enable after each window ends.
        /// </summary>
        private void ScheduleEmsManagementForPeriods(ChargingSchema schedule)
        {
            var mergedWindows = BuildMergedEmsWindows(schedule);

            foreach (var (windowStart, windowEnd) in mergedWindows)
            {
                if (windowStart > DateTime.Now)
                {
                    _scheduler.RunAt(windowStart, () => { _ = PrepareForBatteryPeriodAsync(null); });
                    LogStatus("Battery schedule active");
                }

                if (windowEnd > DateTime.Now)
                {
                    _scheduler.RunAt(windowEnd, () => { _ = RestoreEmsAfterWindowAsync(); });
                    // Note: This is debug-level scheduling info, not shown on dashboard
                }
            }
        }

        /// <summary>
        /// Prepares for a battery charge/discharge period by shutting down EMS and applying the schedule.
        /// </summary>
        private async Task PrepareForBatteryPeriodAsync(ChargingPeriod? period)
        {
            try
            {
                if (period != null)
                    LogStatus($"{period.ChargeType} period {period.StartTime.ToString(@"hh\:mm")}-{period.EndTime.ToString(@"hh\:mm")}");
                else
                    LogStatus("Battery schedule starting");

                // 1. Validate and turn off EMS
                if (!await ValidateAndTurnOffEmsAsync("battery period", period))
                {
                    ScheduleRetryWithReason("EMS validation failed", period);
                    return; // Retry was scheduled, exit early
                }

                // 2. Apply the prepared schedule if we have one
                ChargingSchema? scheduleToApply;
                lock (_scheduleLock)
                {
                    scheduleToApply = _preparedSchedule ?? GetPreparedSchedule();
                }

                if (scheduleToApply != null)
                {
                    LogStatus("Applying schedule");
                    await ApplyChargingScheduleAsync(scheduleToApply, simulateOnly: _simulationMode);
                }
                else
                {
                    LogStatus("No schedule to apply");
                }
            }
            catch (Exception ex)
            {
                LogStatus("Error preparing battery schedule", ex.Message);
            }
        }

        /// <summary>
        /// Helper to prepare for and apply a specific schedule (used for ad-hoc morning discharge).
        /// </summary>
        private async Task PrepareForScheduleAsync(ChargingSchema schedule, string label)
        {
            try
            {
                LogStatus($"Preparing for ad-hoc schedule: {label}");

                // Validate and turn off EMS
                if (!await ValidateAndTurnOffEmsAsync($"ad-hoc schedule: {label}"))
                    return; // Retry was scheduled, exit early

                await ApplyChargingScheduleAsync(schedule, simulateOnly: _simulationMode);
            }
            catch (Exception ex)
            {
                LogStatus("Error preparing ad-hoc schedule", $"Error preparing for specific schedule: {ex.Message}");
            }
        }

        /// <summary>
        /// Restores EMS after a battery charge/discharge period ends.
        /// </summary>
        private async Task RestoreEmsAfterWindowAsync()
        {
            try
            {
                LogStatus("Restoring EMS after battery schedule");

                // Turn EMS back on (always allowed)
                var emsState = _entities.Switch.Ems.State;
                if (emsState == "off")
                {
                    LogStatus("Turning EMS back on");

                    _entities.Switch.Ems.TurnOn();

                    await Task.Delay(TimeSpan.FromSeconds(1));
                }
                else
                {
                    LogStatus("EMS is already on");
                }
            }
            catch (Exception ex)
            {
                LogStatus("Error restoring EMS", ex.Message);
            }
        }

        /// <summary>
        /// Creates a morning discharge period with the specified start time and duration.
        /// </summary>
        /// <param name="startTime">The start time for the discharge period</param>
        /// <param name="durationHours">Duration in hours (default: 1 hour)</param>
        /// <returns>A configured ChargingPeriod for morning discharge</returns>
        private ChargingPeriod CreateMorningDischargePeriod(TimeSpan startTime, double durationHours = 1.0)
        {
            return CreateMorningDischargePeriod(startTime, durationHours, GetAllDaysPattern());
        }

        /// <summary>
        /// Creates a morning discharge period with the specified start time, duration, and weekday pattern.
        /// </summary>
        /// <param name="startTime">The start time for the discharge period</param>
        /// <param name="durationHours">Duration in hours (default: 1 hour)</param>
        /// <param name="weekdayPattern">Weekday pattern (e.g., "1,1,1,1,1,0,0" for weekdays only)</param>
        /// <returns>A configured ChargingPeriod for morning discharge</returns>
        private ChargingPeriod CreateMorningDischargePeriod(TimeSpan startTime, double durationHours, string weekdayPattern)
        {
            return new ChargingPeriod
            {
                StartTime = startTime,
                EndTime = startTime.Add(TimeSpan.FromHours(durationHours)),
                ChargeType = BatteryChargeType.Discharge,
                PowerInWatts = Math.Min(_options.DefaultDischargePowerW, _options.MaxInverterPowerW),
                Weekdays = weekdayPattern
            };
        }

        /// <summary>
        /// Creates a morning discharge period for a specific day of the week.
        /// </summary>
        /// <param name="startTime">The start time for the discharge period</param>
        /// <param name="durationHours">Duration in hours (default: 1 hour)</param>
        /// <param name="dayOfWeek">Specific day of the week</param>
        /// <returns>A configured ChargingPeriod for morning discharge</returns>
        private ChargingPeriod CreateMorningDischargePeriod(TimeSpan startTime, double durationHours, DayOfWeek dayOfWeek)
        {
            return CreateMorningDischargePeriod(startTime, durationHours, GetSingleDayPattern(dayOfWeek));
        }

        /// <summary>
        /// Creates a charging period with the specified start and end times.
        /// </summary>
        /// <param name="startTime">The start time for the charge period</param>
        /// <param name="endTime">The end time for the charge period</param>
        /// <returns>A configured ChargingPeriod for charging</returns>
        private ChargingPeriod CreateChargePeriod(TimeSpan startTime, TimeSpan endTime)
        {
            return CreateChargePeriod(startTime, endTime, GetAllDaysPattern());
        }

        /// <summary>
        /// Creates a charging period with the specified start and end times and weekday pattern.
        /// </summary>
        /// <param name="startTime">The start time for the charge period</param>
        /// <param name="endTime">The end time for the charge period</param>
        /// <param name="weekdayPattern">Weekday pattern (e.g., "1,1,1,1,1,0,0" for weekdays only)</param>
        /// <returns>A configured ChargingPeriod for charging</returns>
        private ChargingPeriod CreateChargePeriod(TimeSpan startTime, TimeSpan endTime, string weekdayPattern)
        {
            return new ChargingPeriod
            {
                StartTime = startTime,
                EndTime = endTime,
                ChargeType = BatteryChargeType.Charge,
                PowerInWatts = Math.Min(_options.DefaultChargePowerW, _options.MaxInverterPowerW),
                Weekdays = weekdayPattern
            };
        }

        /// <summary>
        /// Creates a charging period for a specific day of the week.
        /// </summary>
        /// <param name="startTime">The start time for the charge period</param>
        /// <param name="endTime">The end time for the charge period</param>
        /// <param name="dayOfWeek">Specific day of the week</param>
        /// <returns>A configured ChargingPeriod for charging</returns>
        private ChargingPeriod CreateChargePeriod(TimeSpan startTime, TimeSpan endTime, DayOfWeek dayOfWeek)
        {
            return CreateChargePeriod(startTime, endTime, GetSingleDayPattern(dayOfWeek));
        }

        /// <summary>
        /// Creates an evening discharge period with the specified start and end times.
        /// </summary>
        /// <param name="startTime">The start time for the discharge period</param>
        /// <param name="endTime">The end time for the discharge period</param>
        /// <returns>A configured ChargingPeriod for evening discharge</returns>
        private ChargingPeriod CreateEveningDischargePeriod(TimeSpan startTime, TimeSpan endTime)
        {
            return CreateEveningDischargePeriod(startTime, endTime, GetAllDaysPattern());
        }

        /// <summary>
        /// Creates an evening discharge period with the specified start and end times and weekday pattern.
        /// </summary>
        /// <param name="startTime">The start time for the discharge period</param>
        /// <param name="endTime">The end time for the discharge period</param>
        /// <param name="weekdayPattern">Weekday pattern (e.g., "1,1,1,1,1,0,0" for weekdays only)</param>
        /// <returns>A configured ChargingPeriod for evening discharge</returns>
        private ChargingPeriod CreateEveningDischargePeriod(TimeSpan startTime, TimeSpan endTime, string weekdayPattern)
        {
            return new ChargingPeriod
            {
                StartTime = startTime,
                EndTime = endTime,
                ChargeType = BatteryChargeType.Discharge,
                PowerInWatts = Math.Min(_options.DefaultDischargePowerW, _options.MaxInverterPowerW),
                Weekdays = weekdayPattern
            };
        }

        /// <summary>
        /// Creates an evening discharge period with optimal duration based on current SOC and target SOC.
        /// </summary>
        /// <param name="startTime">The start time for the discharge period</param>
        /// <param name="currentSoc">Current battery state of charge percentage</param>
        /// <param name="weekdayPattern">Weekday pattern (e.g., "1,1,1,1,1,0,0" for weekdays only)</param>
        /// <returns>A configured ChargingPeriod for evening discharge with calculated duration</returns>
        private ChargingPeriod CreateEveningDischargePeriodWithTargetSoc(TimeSpan startTime, double currentSoc, string weekdayPattern)
        {
            var dischargeDurationHours = CalculateEveningDischargeDuration(currentSoc, _options.EveningDischargeTargetSocPercent);
            var endTime = startTime.Add(TimeSpan.FromHours(dischargeDurationHours));
            
            // Log the SOC-based discharge calculation
            LogStatus($"Evening discharge calculated", 
                $"SOC: {currentSoc:F1}% → {_options.EveningDischargeTargetSocPercent:F1}%, Duration: {dischargeDurationHours:F1}h ({startTime:hh\\:mm}-{endTime:hh\\:mm})");
            
            return new ChargingPeriod
            {
                StartTime = startTime,
                EndTime = endTime,
                ChargeType = BatteryChargeType.Discharge,
                PowerInWatts = Math.Min(_options.DefaultDischargePowerW, _options.MaxInverterPowerW),
                Weekdays = weekdayPattern
            };
        }

        /// <summary>
        /// Creates an evening discharge period for a specific day of the week.
        /// </summary>
        /// <param name="startTime">The start time for the discharge period</param>
        /// <param name="endTime">The end time for the discharge period</param>
        /// <param name="dayOfWeek">Specific day of the week</param>
        /// <returns>A configured ChargingPeriod for evening discharge</returns>
        private ChargingPeriod CreateEveningDischargePeriod(TimeSpan startTime, TimeSpan endTime, DayOfWeek dayOfWeek)
        {
            return CreateEveningDischargePeriod(startTime, endTime, GetSingleDayPattern(dayOfWeek));
        }

        /// <summary>
        /// Validates and fixes overlapping periods by merging or adjusting them.
        /// </summary>
        /// <param name="periods">List of periods to validate</param>
        /// <returns>Validated list with no overlapping periods</returns>
        private List<ChargingPeriod> ValidateAndFixOverlaps(List<ChargingPeriod> periods)
        {
            if (periods.Count <= 1)
                return periods;

            // Sort periods by start time
            var sortedPeriods = periods.OrderBy(p => p.StartTime).ToList();
            var result = new List<ChargingPeriod>();

            foreach (var period in sortedPeriods)
            {
                var lastPeriod = result.LastOrDefault();

                // If no overlap with previous period, add as-is
                if (lastPeriod == null || lastPeriod.EndTime <= period.StartTime)
                {
                    result.Add(period);
                    continue;
                }

                // Handle overlap
                if (lastPeriod.ChargeType == period.ChargeType)
                {
                    // Same type: merge periods by extending the end time
                    var mergedEndTime = period.EndTime > lastPeriod.EndTime ? period.EndTime : lastPeriod.EndTime;
                    lastPeriod.EndTime = mergedEndTime;
                    LogStatus("Merged overlapping periods",
                        $"Merged {period.ChargeType} periods: {lastPeriod.StartTime:hh\\:mm}-{lastPeriod.EndTime:hh\\:mm}");
                }
                else
                {
                    // Different types: adjust the overlapping period to start after the previous one ends
                    var adjustedStart = lastPeriod.EndTime;
                    if (adjustedStart < period.EndTime)
                    {
                        period.StartTime = adjustedStart;
                        result.Add(period);
                        LogStatus("Adjusted overlapping period",
                            $"Adjusted {period.ChargeType} period to start at {period.StartTime:hh\\:mm} (was overlapping with {lastPeriod.ChargeType})");
                    }
                    else
                    {
                        // Period would be invalid after adjustment, skip it
                        LogStatus("Removed invalid overlapping period",
                            $"Removed {period.ChargeType} period {period.StartTime:hh\\:mm}-{period.EndTime:hh\\:mm} (would be invalid after overlap adjustment)");
                    }
                }
            }

            return result;
        }

        #endregion

        #region Price Evaluation

        /// <summary>
        /// Schedule an evening price check to potentially move discharge from tonight to tomorrow morning.
        /// </summary>
        private void ScheduleEveningPriceCheck(DateTime currentDischargeTime)
        {
            var checkTime = currentDischargeTime.AddMinutes(-30);

            if (checkTime > DateTime.Now)
            {
                _scheduler.RunAt(checkTime, () => { _ = EvaluateEveningToMorningShiftAsync(); });
                LogStatus("Evening price check scheduled");
            }
        }

        /// <summary>
        /// Evaluate whether to move tonight's discharge to tomorrow morning based on price comparison.
        /// </summary>
        private async Task EvaluateEveningToMorningShiftAsync()
        {
            try
            {
                // Debug: Evaluating evening to morning discharge shift

                var pricesToday = _priceHelper.PricesToday;
                var pricesTomorrow = _priceHelper.PricesTomorrow;

                if (pricesToday == null || pricesTomorrow == null)
                {
                    LogStatus("Missing price data", "Missing price data for evening evaluation");
                    return;
                }

                // Define evening window to avoid picking midday peak
                var eveningStart = TimeSpan.FromHours(_options.EveningThresholdHour);
                var eveningPrices = pricesToday.Where(p => p.Key.TimeOfDay >= eveningStart).ToList();
                if (eveningPrices.Count == 0)
                {
                    LogStatus("No evening prices", "No evening prices available");
                    return;
                }

                var eveningPrice = eveningPrices.OrderByDescending(p => p.Value).First();

                // Tomorrow morning window
                var tomorrowMorningPrices = pricesTomorrow.Where(p =>
                    p.Key.TimeOfDay >= TimeSpan.FromHours(_options.MorningWindowStartHour) &&
                    p.Key.TimeOfDay < TimeSpan.FromHours(_options.MorningWindowEndHour)).ToList();

                if (tomorrowMorningPrices.Count == 0)
                {
                    LogStatus("No morning prices", "No tomorrow morning prices available");
                    return;
                }

                var bestMorningPrice = tomorrowMorningPrices.OrderByDescending(p => p.Value).First();

                LogStatus("Price comparison", $"Tonight: €{eveningPrice.Value:F3} at {eveningPrice.Key.ToString("HH:mm")}, Tomorrow morning: €{bestMorningPrice.Value:F3} at {bestMorningPrice.Key.ToString("HH:mm")}");

                if (bestMorningPrice.Value > eveningPrice.Value)
                {
                    LogStatus($"Shifting discharge to tomorrow {bestMorningPrice.Key.ToString("HH:mm")}");
                    RescheduleDischargeTomorrowMorning(bestMorningPrice.Key);
                }
                else
                {
                    LogStatus("Keeping evening discharge (better price)");
                }
            }
            catch (Exception ex)
            {
                LogStatus("Error evaluating evening shift", $"Error during evening price evaluation: {ex.Message}");
            }
        }

        /// <summary>
        /// Reschedule the current evening discharge to tomorrow morning.
        /// </summary>
        private void RescheduleDischargeTomorrowMorning(DateTime tomorrowMorningTime)
        {
            try
            {
                // Update the prepared schedule to remove tonight's discharge
                if (_preparedSchedule != null && _preparedSchedule.Periods != null)
                {
                    lock (_scheduleLock)
                    {
                        if (_preparedSchedule.Periods != null)
                        {
                            var periodsToKeep = _preparedSchedule.Periods
                                .Where(p => p.ChargeType != BatteryChargeType.Discharge ||
                                           p.StartTime < TimeSpan.FromHours(_options.EveningThresholdHour))
                                .ToList();

                            _preparedSchedule.Periods = periodsToKeep;
                            SavePreparedSchedule(_preparedSchedule);
                        }
                    }

                    // Debug: Removed tonight's discharge from schedule
                }

                // Schedule the new discharge for tomorrow morning
                var tomorrowDischargeTime = tomorrowMorningTime.AddMinutes(-_options.EmsPrepMinutesBefore);
                _scheduler.RunAt(tomorrowDischargeTime, () => { _ = ExecuteTomorrowMorningDischargeAsync(tomorrowMorningTime); });

                LogStatus("Morning discharge scheduled");
                LogStatus("Evening discharge moved to morning");
            }
            catch (Exception ex)
            {
                LogStatus("Error rescheduling to morning", $"Error rescheduling discharge to tomorrow morning: {ex.Message}");
            }
        }

        /// <summary>
        /// Execute the discharge that was moved to tomorrow morning.
        /// </summary>
        private async Task ExecuteTomorrowMorningDischargeAsync(DateTime dischargeTime)
        {
            try
            {
                LogStatus("Executing morning discharge", $"Starting morning discharge at {dischargeTime.ToString("HH:mm")}");

                // Check if we already have an existing schedule for today
                var existingSchedule = GetPreparedSchedule();
                List<ChargingPeriod> allPeriods;

                if (existingSchedule?.Periods != null)
                {
                    // Add to existing schedule, but remove any overlapping discharge periods first
                    allPeriods = existingSchedule.Periods.ToList();

                    // Remove existing discharge periods that would overlap with the new morning discharge
                    var newDischargeStart = dischargeTime.TimeOfDay;
                    var newDischargeEnd = newDischargeStart.Add(TimeSpan.FromHours(1));

                    var periodsToKeep = allPeriods.Where(p =>
                        p.ChargeType != BatteryChargeType.Discharge ||
                        p.EndTime <= newDischargeStart ||
                        p.StartTime >= newDischargeEnd).ToList();

                    allPeriods = periodsToKeep;
                    LogStatus("Removed overlapping discharge periods",
                        $"Removed {existingSchedule.Periods.Count - periodsToKeep.Count} overlapping discharge periods for new morning discharge");
                }
                else
                {
                    allPeriods = new List<ChargingPeriod>();
                }

                // Add the new morning discharge period (tomorrow specific when day scheduling enabled)
                var tomorrowPattern = _options.EnableDaySpecificScheduling ? 
                    GetSingleDayPattern(dischargeTime.DayOfWeek) : 
                    GetAllDaysPattern();
                allPeriods.Add(CreateMorningDischargePeriod(dischargeTime.TimeOfDay, 1.0, tomorrowPattern));

                // Validate and fix any remaining overlaps
                allPeriods = ValidateAndFixOverlaps(allPeriods);

                var morningDischargeSchedule = new ChargingSchema { Periods = allPeriods };

                await PrepareForScheduleAsync(morningDischargeSchedule, label: "Morning Discharge");

                var restoreTime = dischargeTime.AddHours(1).AddMinutes(_options.EmsRestoreMinutesAfter);
                _scheduler.RunAt(restoreTime, () => { _ = RestoreEmsAfterWindowAsync(); });
            }
            catch (Exception ex)
            {
                LogStatus("Error executing morning discharge", $"Error executing tomorrow morning discharge: {ex.Message}");
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Logs status with separate dashboard and detailed messages.
        /// </summary>
        /// <param name="dashboardMessage">Clean, concise message for the dashboard display</param>
        /// <param name="detailMessage">Optional detailed message for console/logging (includes context, errors, etc.)</param>
        private void LogStatus(string dashboardMessage, string? detailMessage = null)
        {
            var consoleMessage = !string.IsNullOrEmpty(detailMessage) ? $"{dashboardMessage} | {detailMessage}" : dashboardMessage;

            try
            {
                Console.WriteLine($"[BATTERY] {consoleMessage}");
            }
            catch { /* ignore console failures */ }

            // Log the detailed version to the logger
            _logger.LogInformation(consoleMessage);

            try
            {
                _entities.InputText.BatteryManagementStatus.SetValue(dashboardMessage);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to set BatteryManagementStatus");
            }
        }

        /// <summary>
        /// Logs status with format string (backward compatibility).
        /// </summary>
        /// <param name="message">Message with format placeholders for dashboard</param>
        /// <param name="args">Arguments for formatting</param>
        private void LogStatus(string message, params object[] args)
        {
            var formattedMessage = args.Length > 0 ? string.Format(message, args) : message;
            LogStatus(formattedMessage);
        }

        /// <summary>
        /// Starts the battery mode monitoring that checks the SAJ API every 5 minutes
        /// and updates the input_text.battery_management_mode entity.
        /// </summary>
        private void StartBatteryModeMonitoring()
        {
            if (!_saiPowerBatteryApi.IsConfigured)
            {
                LogStatus("Battery monitoring disabled", "SAJ API not configured - battery mode monitoring disabled");
                return;
            }

            // Run initial check immediately (non-blocking)
            _ = MonitorBatteryModeAsync();
            _ = UpdateScheduleStatusAsync();

            // Schedule to run every 5 minutes
            _scheduler.RunEvery(TimeSpan.FromMinutes(5), DateTime.Now.AddMinutes(5), () => { _ = MonitorBatteryModeAsync(); });

            // Schedule status updates every minute for more responsive UI
            _scheduler.RunEvery(TimeSpan.FromMinutes(1), DateTime.Now.AddMinutes(1), () => { _ = UpdateScheduleStatusAsync(); });

            LogStatus("Battery monitoring started", "Battery mode monitoring and status updates started");
        }

        /// <summary>
        /// Monitors the current battery user mode and updates the Home Assistant input text entity.
        /// </summary>
        private async Task MonitorBatteryModeAsync()
        {
            try
            {
                var currentMode = await _saiPowerBatteryApi.GetUserModeAsync();
                var modeText = currentMode.ToApiString();

                // Update the input text entity
                try
                {
                    _entities.InputText.BatteryManagementMode.SetValue(modeText);
                    // Debug-level logging can be omitted or kept minimal
                }
                catch (Exception ex)
                {
                    LogStatus("Battery status update failed", $"Failed to update input_text.battery_management_mode entity: {ex.Message}");
                }

                // Check if mode changed from last known state
                var lastMode = AppStateManager.GetState<BatteryUserMode?>(nameof(Battery), "LastKnownMode");
                if (lastMode != currentMode)
                {
                    // Note: Battery mode is already displayed in input_text.battery_management_mode entity
                    // No status logging needed - mode changes are visible in the dedicated sensor
                    AppStateManager.SetState(nameof(Battery), "LastKnownMode", currentMode);
                    AppStateManager.SetState(nameof(Battery), "LastModeChangeTime", DateTime.Now);
                }
            }
            catch (Exception ex)
            {
                LogStatus("Battery monitoring error", $"Failed to monitor battery mode: {ex.Message}");

                // Update entity with error state
                try
                {
                    _entities.InputText.BatteryManagementMode.SetValue("Error - Check Logs");
                }
                catch
                {
                    // Debug-level error can be omitted or kept minimal for entity update failures
                }
            }
        }

        /// <summary>
        /// Updates the schedule status based on the current applied schedule and time
        /// </summary>
        private async Task UpdateScheduleStatusAsync()
        {
            try
            {
                var currentSchedule = GetCurrentAppliedSchedule();
                if (currentSchedule == null)
                {
                    // No schedule applied, check if we should have one
                    var preparedSchedule = GetPreparedSchedule();
                    if (preparedSchedule != null)
                    {
                        UpdateStatusIfChanged("No active schedule", "Prepared schedule exists but not yet applied");
                    }
                    else
                    {
                        UpdateStatusIfChanged("No battery activity planned today");
                    }
                    return;
                }

                // Update status based on current schedule
                var statusMessage = BuildNextEventSummary(currentSchedule);
                UpdateStatusIfChanged(statusMessage);
            }
            catch (Exception ex)
            {
                LogStatus("Status update error", $"Failed to update schedule status: {ex.Message}");
            }
        }

        /// <summary>
        /// Updates the status only if it has changed from the last status message
        /// </summary>
        private void UpdateStatusIfChanged(string dashboardMessage, string? detailMessage = null)
        {
            var lastStatus = AppStateManager.GetState<string>(nameof(Battery), "LastStatusMessage");
            if (lastStatus != dashboardMessage)
            {
                LogStatus(dashboardMessage, detailMessage);
                AppStateManager.SetState(nameof(Battery), "LastStatusMessage", dashboardMessage);
            }
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Formats a future scheduled time with relative context for dashboard messages.
        /// Examples: "in 2h 15m (19:55)", "in 11 hours", "tomorrow at 02:30"
        /// </summary>
        private static string FormatScheduledTime(DateTime scheduledTime, string action = "")
        {
            var now = DateTime.Now;
            var timeUntil = scheduledTime - now;

            if (timeUntil.TotalMinutes < 1)
                return "now";

            // For times within the next 24 hours - simplified format
            if (timeUntil.TotalHours < 24)
            {
                return $"at {scheduledTime:HH:mm}";
            }

            // For times beyond 24 hours (tomorrow+)
            if (scheduledTime.Date == now.Date.AddDays(1))
                return $"tomorrow at {scheduledTime:HH:mm}";
            else
                return $"{scheduledTime:MMM dd} at {scheduledTime:HH:mm}";
        }

        /// <summary>
        /// Creates a status message for scheduled actions with better context and relative timing.
        /// </summary>
        private static string FormatScheduledAction(string actionType, DateTime scheduledTime, string context = "")
        {
            var timeInfo = FormatScheduledTime(scheduledTime, actionType);
            var contextSuffix = string.IsNullOrEmpty(context) ? "" : $" ({context})";

            return $"{actionType} scheduled {timeInfo}{contextSuffix}";
        }

        /// <summary>
        /// Generates weekday patterns for charging periods.
        /// Format: "Mon,Tue,Wed,Thu,Fri,Sat,Sun" where 1=active, 0=inactive
        /// </summary>
        /// <param name="dayOfWeek">Specific day of week (Monday=1)</param>
        /// <returns>Weekday pattern string</returns>
        private static string GetSingleDayPattern(DayOfWeek dayOfWeek)
        {
            var pattern = new[] { "0", "0", "0", "0", "0", "0", "0" };
            // .NET DayOfWeek: Sunday=0, Monday=1, ... Saturday=6
            // API format: Monday=index 0, Tuesday=index 1, ... Sunday=index 6
            var apiIndex = dayOfWeek == DayOfWeek.Sunday ? 6 : (int)dayOfWeek - 1;
            pattern[apiIndex] = "1";
            return string.Join(",", pattern);
        }

        /// <summary>
        /// Gets a short day abbreviation from weekday pattern.
        /// Returns the first active day found, or empty string if no days active.
        /// </summary>
        /// <param name="weekdayPattern">Pattern like "0,0,0,0,1,0,0"</param>
        /// <returns>Day abbreviation like "Fri" or empty string</returns>
        private static string GetDayAbbreviationFromPattern(string weekdayPattern)
        {
            if (string.IsNullOrEmpty(weekdayPattern))
                return "";
                
            var dayAbbreviations = new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
            var days = weekdayPattern.Split(',');
            
            for (int i = 0; i < Math.Min(days.Length, dayAbbreviations.Length); i++)
            {
                if (days[i].Trim() == "1")
                {
                    return dayAbbreviations[i];
                }
            }
            
            return "";
        }

        /// <summary>
        /// Gets weekday pattern for business days (Monday-Friday).
        /// </summary>
        /// <returns>Weekday pattern string for weekdays only</returns>
        private static string GetWeekdaysPattern()
        {
            return "1,1,1,1,1,0,0"; // Mon-Fri active, Sat-Sun inactive
        }

        /// <summary>
        /// Gets weekday pattern for weekends (Saturday-Sunday).
        /// </summary>
        /// <returns>Weekday pattern string for weekends only</returns>
        private static string GetWeekendsPattern()
        {
            return "0,0,0,0,0,1,1"; // Mon-Fri inactive, Sat-Sun active
        }

        /// <summary>
        /// Gets weekday pattern for all days (default behavior).
        /// </summary>
        /// <returns>Weekday pattern string for all days</returns>
        private static string GetAllDaysPattern()
        {
            return "1,1,1,1,1,1,1"; // All days active
        }

        /// <summary>
        /// Determines the optimal weekday pattern based on configuration and current day context.
        /// </summary>
        /// <returns>Weekday pattern string based on configuration settings</returns>
        private string GetOptimalWeekdayPattern()
        {
            if (!_options.EnableDaySpecificScheduling)
                return GetAllDaysPattern();

            // For day-specific scheduling, use current day only
            // This could be enhanced to consider tomorrow's schedule as well
            var currentDay = DateTime.Now.DayOfWeek;
            var pattern = GetSingleDayPattern(currentDay);
            var description = $"{currentDay} only";

            if (Debugger.IsAttached)
            {
                LogStatus($"Day-specific scheduling: {description}", $"Using weekday pattern: {pattern}");
            }

            return pattern;
        }

        /// <summary>
        /// Gets the optimal weekday pattern for discharge periods (today only).
        /// </summary>
        /// <returns>Weekday pattern string for today</returns>
        private string GetDischargeWeekdayPattern()
        {
            if (!_options.EnableDaySpecificScheduling)
                return GetAllDaysPattern();

            var today = DateTime.Now.DayOfWeek;
            var pattern = GetSingleDayPattern(today);
            
            if (Debugger.IsAttached)
            {
                LogStatus($"Discharge pattern: {today} only", $"Using pattern: {pattern}");
            }
            
            return pattern;
        }

        /// <summary>
        /// Gets the optimal weekday pattern for charge periods (tomorrow only).
        /// </summary>
        /// <returns>Weekday pattern string for tomorrow</returns>
        private string GetChargeWeekdayPattern()
        {
            if (!_options.EnableDaySpecificScheduling)
                return GetAllDaysPattern();

            var tomorrow = DateTime.Now.AddDays(1).DayOfWeek;
            var pattern = GetSingleDayPattern(tomorrow);
            
            if (Debugger.IsAttached)
            {
                LogStatus($"Charge pattern: {tomorrow} only", $"Using pattern: {pattern}");
            }
            
            return pattern;
        }

        /// <summary>
        /// Gets a day-specific pattern for a given day with debug logging when enabled.
        /// </summary>
        /// <param name="dayOfWeek">Day of the week to create pattern for</param>
        /// <param name="context">Optional context for logging (e.g., "charge", "discharge")</param>
        /// <returns>Weekday pattern string for the specified day</returns>
        private string GetDaySpecificPattern(DayOfWeek dayOfWeek, string context = "")
        {
            if (!_options.EnableDaySpecificScheduling)
                return GetAllDaysPattern();

            var pattern = GetSingleDayPattern(dayOfWeek);
            
            if (Debugger.IsAttached)
            {
                var contextStr = string.IsNullOrEmpty(context) ? "" : $" for {context}";
                LogStatus($"Day-specific pattern{contextStr}: {dayOfWeek} only", $"Using pattern: {pattern}");
            }

            return pattern;
        }

        /// <summary>
        /// Creates a user-focused summary of the next significant battery event.
        /// Prioritizes immediate upcoming actions over distant scheduling confirmations.
        /// </summary>
        private static string BuildNextEventSummary(ChargingSchema schedule)
        {
            var now = DateTime.Now.TimeOfDay;
            var today = DateTime.Today;

            var allPeriods = schedule.Periods
                .Select(p => new {
                    Period = p,
                    StartDateTime = today.Add(p.StartTime),
                    EndDateTime = today.Add(p.EndTime),
                    IsActive = now >= p.StartTime && now < p.EndTime,
                    IsUpcoming = p.StartTime > now
                })
                .OrderBy(p => p.StartDateTime)
                .ToList();

            // Check if currently in any period
            var activePeriod = allPeriods.FirstOrDefault(p => p.IsActive);
            if (activePeriod != null)
            {
                var action = activePeriod.Period.ChargeType == BatteryChargeType.Charge ? "Charging Active" : "Discharging Active";
                return action;
            }

            // Find next upcoming period
            var nextPeriod = allPeriods.FirstOrDefault(p => p.IsUpcoming);
            if (nextPeriod != null)
            {
                var action = nextPeriod.Period.ChargeType == BatteryChargeType.Charge ? "Charging" : "Discharging";
                return $"Next: {action} {FormatScheduledTime(nextPeriod.StartDateTime)}";
            }

            return "No battery activity planned today";
        }

        // Round up a DateTime to the next 5-minute boundary (e.g., 13:54 -> 13:55)
        private static DateTime RoundUpToNextFiveMinute(DateTime dt)
        {
            var minutesPart = dt.Minute % 5;
            var addMinutes = minutesPart == 0 ? 5 : 5 - minutesPart;
            var baseMinute = new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, 0, dt.Kind);
            return baseMinute.AddMinutes(addMinutes);
        }

        private double GetCurrentBatterySoc()
        {
            var mainBatterySoc = _entities.Sensor.InverterHst2083j2446e06861BatteryStateOfCharge.State;
            if (mainBatterySoc.HasValue)
            {
                return mainBatterySoc.Value;
            }

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
            return validModules.Count != 0 ? validModules.Average(soc => soc!.Value) : 50.0;
        }

        /// <summary>
        /// Calculates the optimal discharge duration based on current SOC and target SOC.
        /// </summary>
        /// <param name="currentSoc">Current battery state of charge percentage</param>
        /// <param name="targetSoc">Target state of charge percentage to discharge to</param>
        /// <returns>Discharge duration in hours</returns>
        private double CalculateEveningDischargeDuration(double currentSoc, double targetSoc)
        {
            // Ensure we don't discharge below the target SOC
            if (currentSoc <= targetSoc)
            {
                return 0.0; // No discharge needed
            }

            // Calculate the SOC difference that needs to be discharged
            var socDifferencePercent = currentSoc - targetSoc;
            var socDifferenceFraction = socDifferencePercent / 100.0;
            
            // Calculate energy to discharge (Wh)
            var energyToDischarge = _options.MaxBatteryCapacityWh * socDifferenceFraction;
            
            // Calculate discharge duration at current discharge power setting
            var dischargePowerW = Math.Min(_options.DefaultDischargePowerW, _options.MaxInverterPowerW);
            var dischargeDurationHours = energyToDischarge / dischargePowerW;
            
            // Ensure we have at least a minimum discharge period (15 minutes) and cap at maximum (3 hours)
            var minimumHours = 0.25; // 15 minutes
            var maximumHours = 3.0;   // 3 hours
            
            return Math.Max(minimumHours, Math.Min(maximumHours, dischargeDurationHours));
        }

        // Compute minutes required to charge from current SOC to 100% at max inverter power
        private int CalculateRequiredChargeMinutes(double socPercent)
        {
            var missingFraction = Math.Clamp((100.0 - socPercent) / 100.0, 0.0, 1.0);
            var hours = (_options.MaxBatteryCapacityWh * missingFraction) / Math.Max(1, _options.MaxInverterPowerW); // Wh / W = h
            var minutes = (int)Math.Ceiling(hours * 60.0);
            if (missingFraction > 0 && minutes < _options.MinChargeBufferMinutes)
            {
                minutes = _options.MinChargeBufferMinutes;
            }
            return minutes;
        }

        // Trim charge periods so the total remaining charge time from now equals requiredMinutes (if possible)
        private (ChargingSchema? adjusted, string summary) TrimChargePeriodsToTotalMinutes(ChargingSchema original, int requiredMinutes)
        {
            var now = DateTime.Now.TimeOfDay;
            var adjusted = original.Clone();
            var periods = adjusted.Periods.ToList();

            // Calculate available remaining charge minutes
            var remaining = periods.Where(p => p.ChargeType == BatteryChargeType.Charge)
                .Sum(p => Math.Max(0, (int)Math.Ceiling((p.EndTime - (p.StartTime > now ? p.StartTime : now)).TotalMinutes)));

            if (remaining <= requiredMinutes)
            {
                return (original, $"No trim needed (remaining {remaining}m <= required {requiredMinutes}m)");
            }

            var minutesToTrim = remaining - requiredMinutes;
            var trimmed = 0; var removed = 0;

            // Work through charge periods from latest to earliest to preserve optimal pricing
            foreach (var p in periods.Where(p => p.ChargeType == BatteryChargeType.Charge).OrderByDescending(p => p.StartTime).ToList())
            {
                if (minutesToTrim <= 0) break;

                var remStart = p.StartTime > now ? p.StartTime : now;
                if (remStart >= p.EndTime) continue; // nothing left in this period

                var remSpanMin = (int)Math.Ceiling((p.EndTime - remStart).TotalMinutes);

                if (minutesToTrim >= remSpanMin)
                {
                    // Remove the remaining portion entirely
                    if (remStart <= p.StartTime)
                    {
                        // Whole period is within trim window: remove
                        adjusted.Periods.Remove(p);
                        removed++;
                    }
                    else
                    {
                        // Keep only the past portion before now
                        p.EndTime = remStart;
                        trimmed++;
                    }
                    minutesToTrim -= remSpanMin;
                    continue;
                }

                // Partial trim: reduce the end time to preserve optimal start timing
                var newEnd = p.EndTime.Subtract(TimeSpan.FromMinutes(minutesToTrim));
                if (newEnd > remStart)
                {
                    p.EndTime = newEnd;
                    trimmed++;
                    minutesToTrim = 0;
                    break;
                }
            }

            // Clean up invalid periods
            adjusted.Periods = adjusted.Periods.Where(x => x.EndTime > x.StartTime).ToList();

            var afterRemaining = adjusted.Periods.Where(p => p.ChargeType == BatteryChargeType.Charge)
                .Sum(p => Math.Max(0, (int)Math.Ceiling((p.EndTime - (p.StartTime > now ? p.StartTime : now)).TotalMinutes)));

            var summary = $"Trimmed {remaining - afterRemaining}m (removed={removed}, trimmed={trimmed}), target {requiredMinutes}m, now {afterRemaining}m";
            return (adjusted, summary);
        }

        // Returns human-friendly suffix about next/ongoing charge/discharge activity
        private static string BuildNextChargeSuffix(ChargingSchema schedule)
        {
            var now = DateTime.Now.TimeOfDay;
            var today = DateTime.Today;

            var charges = schedule.Periods.Where(p => p.ChargeType == BatteryChargeType.Charge)
                .OrderBy(p => p.StartTime)
                .ToList();

            var discharges = schedule.Periods.Where(p => p.ChargeType == BatteryChargeType.Discharge)
                .OrderBy(p => p.StartTime)
                .ToList();

            // Check if currently in a charge period
            var currentCharge = charges.FirstOrDefault(p => now >= p.StartTime && now < p.EndTime);
            if (currentCharge != null)
            {
                var endTime = today.Add(currentCharge.EndTime);
                return $"charging now, ends {FormatScheduledTime(endTime)}";
            }

            // Check if currently in a discharge period
            var currentDischarge = discharges.FirstOrDefault(p => now >= p.StartTime && now < p.EndTime);
            if (currentDischarge != null)
            {
                var endTime = today.Add(currentDischarge.EndTime);
                return $"discharging now, ends {FormatScheduledTime(endTime)}";
            }

            // Find next activity (charge or discharge)
            var nextCharge = charges.FirstOrDefault(p => p.StartTime >= now);
            var nextDischarge = discharges.FirstOrDefault(p => p.StartTime >= now);

            DateTime? nextActivity = null;
            string activityType = "";

            if (nextCharge != null && nextDischarge != null)
            {
                // Both exist, pick the earlier one
                if (nextCharge.StartTime <= nextDischarge.StartTime)
                {
                    nextActivity = today.Add(nextCharge.StartTime);
                    activityType = "charge";
                }
                else
                {
                    nextActivity = today.Add(nextDischarge.StartTime);
                    activityType = "discharge";
                }
            }
            else if (nextCharge != null)
            {
                nextActivity = today.Add(nextCharge.StartTime);
                activityType = "charge";
            }
            else if (nextDischarge != null)
            {
                nextActivity = today.Add(nextDischarge.StartTime);
                activityType = "discharge";
            }

            if (nextActivity.HasValue)
                return $"next {activityType} {FormatScheduledTime(nextActivity.Value)}";

            return "no activity planned today";
        }


        #endregion

        #region State Management

        private ChargingSchema? GetCurrentAppliedSchedule()
        {
            try
            {
                return AppStateManager.GetState<ChargingSchema?>(nameof(Battery), "CurrentAppliedSchema");
            }
            catch (Exception ex)
            {
                // State retrieval errors can be handled silently or with minimal logging
                LogStatus("State retrieval failed", $"Could not retrieve current applied schedule from state: {ex.Message}");
                return null;
            }
        }

        private void SaveCurrentAppliedSchedule(ChargingSchema schedule)
        {
            try
            {
                AppStateManager.SetState(nameof(Battery), "CurrentAppliedSchema", schedule);
                // Debug-level state save logging can be omitted
            }
            catch (Exception ex)
            {
                LogStatus("State save failed", $"Could not save current applied schedule to state: {ex.Message}");
            }
        }

        private void SavePreparedSchedule(ChargingSchema schedule)
        {
            try
            {
                AppStateManager.SetState(nameof(Battery), "PreparedSchedule", schedule);
                AppStateManager.SetState(nameof(Battery), "PreparedScheduleDate", DateTime.Today);
                // Debug-level state save logging can be omitted
            }
            catch (Exception ex)
            {
                LogStatus("State save failed", $"Could not save prepared schedule to state: {ex.Message}");
            }
        }

        private ChargingSchema? GetPreparedSchedule()
        {
            try
            {
                var preparedDate = AppStateManager.GetState<DateTime?>(nameof(Battery), "PreparedScheduleDate");
                if (preparedDate?.Date != DateTime.Today)
                {
                    return null;
                }

                return AppStateManager.GetState<ChargingSchema?>(nameof(Battery), "PreparedSchedule");
            }
            catch (Exception ex)
            {
                // State retrieval errors can be handled silently or with minimal logging
                LogStatus("State retrieval failed", $"Could not retrieve prepared schedule from state: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Clears all battery-related state data at startup for fresh operation.
        /// Useful for testing and ensuring clean initialization.
        /// </summary>
        private void ClearBatteryState()
        {
            try
            {
                // Clear persisted state
                AppStateManager.SetState(nameof(Battery), "CurrentAppliedSchema", (ChargingSchema?)null);
                AppStateManager.SetState(nameof(Battery), "PreparedSchedule", (ChargingSchema?)null);
                AppStateManager.SetState(nameof(Battery), "PreparedScheduleDate", (DateTime?)null);
                
                // Clear retry flag to prevent hanging EMS messages
                _applyRetryScheduled = false;
                
                LogStatus("Battery state cleared", "Cleared all persisted battery state and reset retry flags for fresh startup");
            }
            catch (Exception ex)
            {
                LogStatus("State clear failed", $"Could not clear battery state: {ex.Message}");
            }
        }

        #endregion
    }
}
