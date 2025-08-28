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
                    LogStatus("No price data yet; will retry in 10 min", "Could not calculate schedule for today");

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
                                LogStatus("Retrying schedule apply now (after EMS Mode delay)...");
                                _ = ApplyChargingScheduleAsync(chargingSchedule, simulateOnly: _simulationMode);
                            });

                            LogStatus($"EMS Mode active - {FormatScheduledAction("retry", retryAt).ToLower()}");
                        }
                        else
                        {
                            LogStatus("EMS Mode active - retry already scheduled", "Schedule apply blocked by EMS Mode; retry already scheduled");
                        }

                        return;
                    }
                    else
                    {
                        var modeDescription = currentUserMode.ToApiString();
                        LogStatus($"Mode OK ({modeDescription}) - applying...");
                    }
                }
                catch (Exception ex)
                {
                    LogStatus("Mode check failed - proceeding...", $"Failed to verify battery user mode; proceeding with schedule application: {ex.Message}");
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

                // If we have more scheduled charge time than needed, trim the schedule to just what we need
                var scheduleToApply = chargingSchedule;
                if (remainingScheduledChargeMinutes > 0)
                {
                    if (remainingScheduledChargeMinutes > requiredChargeMinutes)
                    {
                        var (adjusted, summary) = TrimChargePeriodsToTotalMinutes(chargingSchedule, requiredChargeMinutes);
                        scheduleToApply = adjusted ?? chargingSchedule;
                        var suffix = BuildNextChargeSuffix(scheduleToApply);
                        var detail = $"SOC {soc:F1}%, need ~{requiredChargeMinutes}m to full, had {remainingScheduledChargeMinutes}m scheduled. {summary} | {suffix}";
                        LogStatus($"SOC {soc:F1}%, optimized schedule", detail);
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

                    if (liveWrite)
                    {
                        var chargePeriods = scheduleToApply.Periods.Where(p => p.ChargeType == BatteryChargeType.Charge).ToList();
                        var dischargePeriods = scheduleToApply.Periods.Where(p => p.ChargeType == BatteryChargeType.Discharge).ToList();

                        if (chargePeriods.Count > 0)
                        {
                            var chargeSchedule = $"{chargePeriods.First().StartTime.ToString(@"hh\:mm")}-{chargePeriods.Last().EndTime.ToString(@"hh\:mm")}";
                            _entities.InputText.BatteryChargeSchedule.SetValue(chargeSchedule);
                        }

                        if (dischargePeriods.Count > 0)
                        {
                            var dischargeSchedule = $"{dischargePeriods.First().StartTime.ToString(@"hh\:mm")}-{dischargePeriods.Last().EndTime.ToString(@"hh\:mm")}";
                            _entities.InputText.BatteryDischargeSchedule.SetValue(dischargeSchedule);
                        }
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
        /// Implements 3-checkpoint strategy: morning check, charge moment, evening check
        /// </summary>
        private ChargingSchema? CalculateInitialChargingSchedule()
        {
            var pricesToday = _priceHelper.PricesToday;
            if (pricesToday == null || pricesToday.Count < 3)
            {
                LogStatus("Not enough price data", "Not enough price data to set battery schedule");
                return null;
            }

            // Get basic charge and discharge timeslot
            var (chargeStart, chargeEnd) = PriceHelper.GetLowestPriceTimeslot(pricesToday, 3);
            var (dischargeStart, dischargeEnd) = PriceHelper.GetHighestPriceTimeslot(pricesToday, 1);

            var periods = new List<ChargingPeriod>();
            var now = DateTime.Now;

            // CHECKPOINT 1: Morning check (before charge) - add discharge if SOC > configured threshold and still relevant now
            var currentSoc = GetCurrentBatterySoc();
            var morningCheckTime = chargeStart.AddHours(-_options.MorningCheckOffsetHours);
            var morningWindowStart = TimeSpan.FromHours(_options.MorningWindowStartHour);
            if (currentSoc > _options.MorningSocThresholdPercent && morningCheckTime.TimeOfDay > morningWindowStart && now < chargeStart)
            {
                var morningPrices = pricesToday.Where(p => p.Key.TimeOfDay >= morningWindowStart &&
                                                          p.Key.TimeOfDay < chargeStart.TimeOfDay).ToList();

                if (morningPrices.Count > 0)
                {
                    var morningHighPrice = morningPrices.OrderByDescending(p => p.Value).First();
                    var morningDischargeStart = morningHighPrice.Key.TimeOfDay;
                    var morningDischargeEnd = morningDischargeStart.Add(TimeSpan.FromHours(1));

                    // Skip if the period already fully passed; trim if we're in the middle of it
                    if (now.TimeOfDay < morningDischargeEnd)
                    {
                        var start = now.TimeOfDay > morningDischargeStart ? now.TimeOfDay : morningDischargeStart;
                        var period = CreateMorningDischargePeriod(start);
                        period.EndTime = morningDischargeEnd; // Override the default end time to maintain original logic
                        periods.Add(period);

                        LogStatus("Added morning discharge at {0} (SOC: {1:F1}% > {2:F1}%, Price: €{3:F3})",
                            start.ToString(@"hh\:mm"), currentSoc, _options.MorningSocThresholdPercent, morningHighPrice.Value);
                    }
                }
            }

            // CHECKPOINT 2: Always add charge moment at lowest price
            periods.Add(CreateChargePeriod(chargeStart.TimeOfDay, chargeEnd.TimeOfDay));

            // CHECKPOINT 3: Add evening discharge (will be checked and potentially moved later)
            periods.Add(CreateEveningDischargePeriod(dischargeStart.TimeOfDay, dischargeEnd.TimeOfDay));

            // Schedule evening check to potentially move discharge to tomorrow morning
            ScheduleEveningPriceCheck(dischargeStart);

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
        }            return schedule;
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

                LogStatus($"{blockReason} - {FormatScheduledAction("retry", retryAt).ToLower()}");
            }
            else
            {
                LogStatus($"{blockReason}; retry already scheduled");
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
                    LogStatus(FormatScheduledAction("Battery window", windowStart));
                }

                if (windowEnd > DateTime.Now)
                {
                    _scheduler.RunAt(windowEnd, () => { _ = RestoreEmsAfterWindowAsync(); });
                    // Note: This is debug-level scheduling info, not shown on dashboard
                }
            }
        }

        /// <summary>
        /// Prepares for a battery charge/discharge window by shutting down EMS and applying the schedule.
        /// </summary>
        private async Task PrepareForBatteryPeriodAsync(ChargingPeriod? period)
        {
            try
            {
                if (period != null)
                    LogStatus($"{period.ChargeType} period {period.StartTime.ToString(@"hh\:mm")}-{period.EndTime.ToString(@"hh\:mm")}");
                else
                    LogStatus("Battery window starting");

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
                LogStatus("Error preparing battery window");
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
        /// Restores EMS after a battery charge/discharge window ends.
        /// </summary>
        private async Task RestoreEmsAfterWindowAsync()
        {
            try
            {
                LogStatus("Restoring EMS after battery window");

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
                LogStatus("Error restoring EMS");
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
            return new ChargingPeriod
            {
                StartTime = startTime,
                EndTime = startTime.Add(TimeSpan.FromHours(durationHours)),
                ChargeType = BatteryChargeType.Discharge,
                PowerInWatts = Math.Min(_options.DefaultDischargePowerW, _options.MaxInverterPowerW)
            };
        }

        /// <summary>
        /// Creates a charging period with the specified start and end times.
        /// </summary>
        /// <param name="startTime">The start time for the charge period</param>
        /// <param name="endTime">The end time for the charge period</param>
        /// <returns>A configured ChargingPeriod for charging</returns>
        private ChargingPeriod CreateChargePeriod(TimeSpan startTime, TimeSpan endTime)
        {
            return new ChargingPeriod
            {
                StartTime = startTime,
                EndTime = endTime,
                ChargeType = BatteryChargeType.Charge,
                PowerInWatts = Math.Min(_options.DefaultChargePowerW, _options.MaxInverterPowerW)
            };
        }

        /// <summary>
        /// Creates an evening discharge period with the specified start and end times.
        /// </summary>
        /// <param name="startTime">The start time for the discharge period</param>
        /// <param name="endTime">The end time for the discharge period</param>
        /// <returns>A configured ChargingPeriod for evening discharge</returns>
        private ChargingPeriod CreateEveningDischargePeriod(TimeSpan startTime, TimeSpan endTime)
        {
            return new ChargingPeriod
            {
                StartTime = startTime,
                EndTime = endTime,
                ChargeType = BatteryChargeType.Discharge,
                PowerInWatts = Math.Min(_options.DefaultDischargePowerW, _options.MaxInverterPowerW)
            };
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
                LogStatus(FormatScheduledAction("Evening price check", checkTime, "discharge timing evaluation"));
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

                LogStatus(FormatScheduledAction("Morning discharge", tomorrowMorningTime));
                LogStatus($"Evening discharge moved to {FormatScheduledTime(tomorrowMorningTime).ToLower()}");
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
                LogStatus($"Executing morning discharge {dischargeTime.ToString("HH:mm")}");

                var morningDischargeSchedule = new ChargingSchema
                {
                    Periods = new List<ChargingPeriod>
                    {
                        CreateMorningDischargePeriod(dischargeTime.TimeOfDay)
                    }
                };

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

            // Schedule to run every 5 minutes
            _scheduler.RunEvery(TimeSpan.FromMinutes(5), DateTime.Now.AddMinutes(5), () => { _ = MonitorBatteryModeAsync(); });
            
            LogStatus("Battery monitoring started", "Battery mode monitoring started - checking every 5 minutes");
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
                    LogStatus($"Battery mode: {modeText}", $"Battery mode changed from {lastMode?.ToApiString() ?? "Unknown"} to {modeText}");
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
                
            // For times within the next 24 hours
            if (timeUntil.TotalHours < 24)
            {
                if (timeUntil.TotalHours < 1)
                {
                    var minutes = (int)Math.Ceiling(timeUntil.TotalMinutes);
                    return $"in {minutes}m ({scheduledTime:HH:mm})";
                }
                else if (timeUntil.TotalHours < 12)
                {
                    var hours = (int)timeUntil.TotalHours;
                    var minutes = (int)(timeUntil.TotalMinutes % 60);
                    if (minutes > 0)
                        return $"in {hours}h {minutes}m ({scheduledTime:HH:mm})";
                    else
                        return $"in {hours}h ({scheduledTime:HH:mm})";
                }
                else
                {
                    // For longer times, just show hours and time
                    var hours = (int)Math.Ceiling(timeUntil.TotalHours);
                    return $"in {hours}h ({scheduledTime:HH:mm})";
                }
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
                var action = activePeriod.Period.ChargeType == BatteryChargeType.Charge ? "Charging" : "Discharging";
                return $"{action} now - ends {FormatScheduledTime(activePeriod.EndDateTime)}";
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

            // Work through charge periods from earliest to latest
            foreach (var p in periods.Where(p => p.ChargeType == BatteryChargeType.Charge).OrderBy(p => p.StartTime).ToList())
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

                // Partial trim: move the start of remaining part forward
                var newStart = remStart.Add(TimeSpan.FromMinutes(minutesToTrim));
                if (newStart <= p.EndTime)
                {
                    p.StartTime = newStart;
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

        #endregion
    }
}
