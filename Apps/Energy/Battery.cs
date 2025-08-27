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

            if (Debugger.IsAttached)
            {
                // Run schedule preparation immediately for debugging (non-blocking)
                _ = PrepareScheduleForDayAsync();
            }
            else
            {
                // Start the scheduler and check if we have a active schedule for today or need to create one
                var dailyTime = DateTime.Today.AddDays(1).AddMinutes(5);
                scheduler.RunEvery(TimeSpan.FromDays(1), dailyTime, () => { _ = PrepareScheduleForDayAsync(); });

                var existingSchedule = GetPreparedSchedule();
                if (existingSchedule != null)
                {
                    _logger.LogInformation("Found existing prepared schedule for today, setting up EMS management");
                    lock (_scheduleLock)
                    {
                        _preparedSchedule = existingSchedule;
                    }

                    // If no upcoming windows remain (e.g., late restart), recompute today’s schedule
                    var upcoming = BuildMergedEmsWindows(existingSchedule);
                    if (upcoming.Count == 0)
                    {
                        _logger.LogInformation("Prepared schedule has no upcoming windows; recalculating schedule for today");
                        _ = PrepareScheduleForDayAsync();
                    }
                    else
                    {
                        ScheduleEmsManagementForPeriods(existingSchedule);
                    }
                }
                else
                {
                    _logger.LogInformation("No existing schedule found, preparing new schedule");
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
                LogStatus("Preparing daily battery schedule");

                // Calculate the schedule for today
                var schedule = CalculateInitialChargingSchedule();
                if (schedule == null)
                {
                    // Retry shortly in case price data arrives a bit later
                    _logger.LogWarning("Could not calculate schedule for today");
                    LogStatus("No price data yet; will retry in 10 min");

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

                var preparedSuffix = BuildNextChargeSuffix(schedule);
                LogStatus($"Prepared schedule: {schedule.Periods.Count} periods | {preparedSuffix}");

                // Optionally, in simulation mode apply immediately for visibility
                if (_simulationMode)
                {
                    await ApplyChargingScheduleAsync(schedule, simulateOnly: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error preparing daily schedule");
                LogStatus("Error preparing schedule");
            }
        }

        /// <summary>
        /// Applies the given charging schema to the battery system via the SAJ Power API
        /// </summary>
        private async Task ApplyChargingScheduleAsync(ChargingSchema chargingSchedule, bool simulateOnly)
        {
            if (chargingSchedule.Periods == null || chargingSchedule.Periods.Count == 0)
            {
                _logger.LogWarning("No charging schedule to apply");
                LogStatus("No schedule to apply");
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

                            _logger.LogInformation("Schedule apply blocked by EMS Mode; retry scheduled at {Time}", retryAt.ToString("HH:mm"));
                        }
                        else
                        {
                            _logger.LogInformation("Schedule apply blocked by EMS Mode; retry already scheduled");
                        }

                        LogStatus("EMS Mode active");

                        return;
                    }
                    else
                    {
                        var modeDescription = currentUserMode.ToApiString();
                        LogStatus($"Mode OK ({modeDescription}) - applying schedule...");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to verify battery user mode; proceeding with schedule application");

                    LogStatus("Mode check failed - proceeding anyway...");
                }
            }

            try
            {
                var allPeriods = chargingSchedule.Periods.ToList();
                if (allPeriods.Count == 0)
                {
                    _logger.LogWarning("No valid periods in schedule");
                    LogStatus("No valid periods in schedule");
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
                        _logger.LogInformation(detail);
                        LogStatus(detail);
                    }
                    else
                    {
                        var suffix = BuildNextChargeSuffix(chargingSchedule);
                        var detail = $"SOC {soc:F1}%, need ~{requiredChargeMinutes}m, scheduled {remainingScheduledChargeMinutes}m (<= needed) | {suffix}";
                        _logger.LogInformation(detail);
                        LogStatus(detail);
                    }
                }

                // Idempotency: compare with current applied schedule using the adjusted schedule
                var currentApplied = GetCurrentAppliedSchedule();
                if (!simulateOnly && currentApplied != null && scheduleToApply.IsEquivalentTo(currentApplied))
                {
                    _logger.LogInformation("Schedule unchanged from current applied; skipping API call");
                    var suffix = BuildNextChargeSuffix(scheduleToApply);
                    LogStatus($"Schedule unchanged; skipping apply | {suffix}");
                    return;
                }

                var scheduleParameters = SAJPowerBatteryApi.BuildBatteryScheduleParameters(scheduleToApply.Periods.ToList());
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
                        _logger.LogWarning($"Skipping live API write: SAJ API not configured ({_saiPowerBatteryApi.ConfigurationError}); simulating apply");
                    }
                    saved = true; // treat as success for simulated apply
                }

                if (saved)
                {
                    if (liveWrite)
                    {
                        SaveCurrentAppliedSchedule(scheduleToApply);
                    }

                    var suffix = BuildNextChargeSuffix(scheduleToApply);
                    LogStatus($"Applied schedule{(liveWrite ? string.Empty : " (sim)")}: {scheduleToApply.Periods.Count} periods | {suffix}");

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
                    _logger.LogWarning("Failed to apply charging schedule via SAJ API");
                    LogStatus("Failed to apply schedule");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error applying charging schedule");
                LogStatus("Error applying schedule");
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
                _logger.LogWarning("Not enough price data to set battery schedule.");
                return null;
            }

            // Get basic charge and discharge timeslot
            var (chargeStart, chargeEnd) = PriceHelper.GetLowestPriceTimeslot(pricesToday, 3);
            var (dischargeStart, dischargeEnd) = PriceHelper.GetHighestPriceTimeslot(pricesToday, 1);

            var periods = new List<ChargingPeriod>();
            var now = DateTime.Now;

            // CHECKPOINT 1: Morning check (before charge) - add discharge if SOC > 40% and still relevant now
            var currentSoc = GetCurrentBatterySoc();
            var morningCheckTime = chargeStart.AddHours(-_options.MorningCheckOffsetHours);
            var morningWindowStart = TimeSpan.FromHours(_options.MorningWindowStartHour);
            if (currentSoc > 40.0 && morningCheckTime.TimeOfDay > morningWindowStart && now < chargeStart)
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
                        periods.Add(new ChargingPeriod
                        {
                            StartTime = start,
                            EndTime = morningDischargeEnd,
                            ChargeType = BatteryChargeType.Discharge,
                            PowerInWatts = Math.Min(_options.DefaultDischargePowerW, _options.MaxInverterPowerW)
                        });

                        LogStatus("Added morning discharge at {0} (SOC: {1:F1}%, Price: €{2:F3})",
                            start.ToString(@"hh\:mm"), currentSoc, morningHighPrice.Value);
                    }
                }
            }

            // CHECKPOINT 2: Always add charge moment at lowest price
            periods.Add(new ChargingPeriod
            {
                StartTime = chargeStart.TimeOfDay,
                EndTime = chargeEnd.TimeOfDay,
                ChargeType = BatteryChargeType.Charge,
                PowerInWatts = Math.Min(_options.DefaultChargePowerW, _options.MaxInverterPowerW)
            });

            // CHECKPOINT 3: Add evening discharge (will be checked and potentially moved later)
            periods.Add(new ChargingPeriod
            {
                StartTime = dischargeStart.TimeOfDay,
                EndTime = dischargeEnd.TimeOfDay,
                ChargeType = BatteryChargeType.Discharge,
                PowerInWatts = Math.Min(_options.DefaultDischargePowerW, _options.MaxInverterPowerW)
            });

            // Schedule evening check to potentially move discharge to tomorrow morning
            ScheduleEveningPriceCheck(dischargeStart);

            var schedule = new ChargingSchema { Periods = periods };

            LogStatus("Created schedule with {0} periods: {1}",
                periods.Count,
                string.Join(", ", periods.Select(p => $"{p.ChargeType} {p.StartTime.ToString(@"hh\:mm")}-{p.EndTime.ToString(@"hh\:mm")}")));

            return schedule;
        }

        #endregion

        #region EMS Management

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
                    LogStatus("Scheduled EMS shutdown at {0}", windowStart.ToString("HH:mm"));
                }

                if (windowEnd > DateTime.Now)
                {
                    _scheduler.RunAt(windowEnd, () => { _ = RestoreEmsAfterWindowAsync(); });
                    _logger.LogInformation("Scheduled EMS restore at {Time}", windowEnd.ToString("HH:mm"));
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
                    LogStatus("Preparing for {0} period {1}-{2}",
                        period.ChargeType, period.StartTime.ToString(@"hh\:mm"), period.EndTime.ToString(@"hh\:mm"));
                else
                    LogStatus("Preparing for battery window");

                // 1. Turn off EMS
                var emsState = _entities.Switch.Ems.State;
                if (emsState == "on")
                {
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
                            _logger.LogWarning(ex, "Failed to get battery user mode");
                            blockReason = "Mode unknown (query failed)";
                        }
                    }

                    if (blockReason != null)
                    {
                        var retryAt = RoundUpToNextFiveMinute(DateTime.Now);
                        if (!_applyRetryScheduled)
                        {
                            _applyRetryScheduled = true;
                            _scheduler.RunAt(retryAt, () =>
                            {
                                _applyRetryScheduled = false;

                                LogStatus($"{blockReason}");

                                _ = PrepareForBatteryPeriodAsync(period);
                            });

                            LogStatus($"{blockReason}");
                        }
                        else
                        {
                            LogStatus($"{blockReason}; retry already scheduled");
                        }
                        return;
                    }

                    LogStatus("Turning off EMS before battery period");

                    _entities.Switch.Ems.TurnOff();

                    await Task.Delay(TimeSpan.FromSeconds(5));
                }
                else
                {
                    LogStatus("EMS is already off");
                }

                // 2. Apply the prepared schedule if we have one
                ChargingSchema? scheduleToApply;
                lock (_scheduleLock)
                {
                    scheduleToApply = _preparedSchedule ?? GetPreparedSchedule();
                }

                if (scheduleToApply != null)
                {
                    LogStatus("Applying prepared battery schedule");
                    await ApplyChargingScheduleAsync(scheduleToApply, simulateOnly: _simulationMode);
                }
                else
                {
                    LogStatus("No prepared schedule available to apply");
                }
            }
            catch (Exception ex)
            {
                LogStatus("Error preparing for battery window");
            }
        }

        /// <summary>
        /// Helper to prepare for and apply a specific schedule (used for ad-hoc morning discharge).
        /// </summary>
        private async Task PrepareForScheduleAsync(ChargingSchema schedule, string label)
        {
            try
            {
                LogStatus("Preparing for ad-hoc schedule: {0}", label);

                var emsState = _entities.Switch.Ems.State;
                if (emsState == "on")
                {
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
                            if (mode == BatteryUserMode.EmsMode || mode == BatteryUserMode.Unknown)
                            {
                                blockReason = mode == BatteryUserMode.EmsMode ? "EMS Mode active" : "Mode unknown";
                            }
                        }
                        catch (Exception ex)
                        {
                            blockReason = "Mode unknown (query failed)";
                        }
                    }

                    if (blockReason != null)
                    {
                        var retryAt = RoundUpToNextFiveMinute(DateTime.Now);
                        if (!_applyRetryScheduled)
                        {
                            _applyRetryScheduled = true;
                            _scheduler.RunAt(retryAt, () =>
                            {
                                _applyRetryScheduled = false;

                                LogStatus($"{blockReason}");

                                _ = PrepareForScheduleAsync(schedule, label);
                            });
                        }

                        LogStatus($"{blockReason}");

                        return;
                    }

                    LogStatus("Turning off EMS before ad-hoc schedule");

                    _entities.Switch.Ems.TurnOff();
                    await Task.Delay(TimeSpan.FromSeconds(5));
                }
                await ApplyChargingScheduleAsync(schedule, simulateOnly: _simulationMode);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error preparing for specific schedule");
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
                LogStatus("Scheduled evening price check at {0} to evaluate discharge timing",
                    checkTime.ToString("HH:mm"));
            }
        }

        /// <summary>
        /// Evaluate whether to move tonight's discharge to tomorrow morning based on price comparison.
        /// </summary>
        private async Task EvaluateEveningToMorningShiftAsync()
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

                // Define evening window to avoid picking midday peak
                var eveningStart = TimeSpan.FromHours(_options.EveningThresholdHour);
                var eveningPrices = pricesToday.Where(p => p.Key.TimeOfDay >= eveningStart).ToList();
                if (eveningPrices.Count == 0)
                {
                    _logger.LogWarning("No evening prices available");
                    return;
                }

                var eveningPrice = eveningPrices.OrderByDescending(p => p.Value).First();

                // Tomorrow morning window
                var tomorrowMorningPrices = pricesTomorrow.Where(p =>
                    p.Key.TimeOfDay >= TimeSpan.FromHours(_options.MorningWindowStartHour) &&
                    p.Key.TimeOfDay < TimeSpan.FromHours(_options.MorningWindowEndHour)).ToList();

                if (tomorrowMorningPrices.Count == 0)
                {
                    _logger.LogWarning("No tomorrow morning prices available");
                    return;
                }

                var bestMorningPrice = tomorrowMorningPrices.OrderByDescending(p => p.Value).First();

                LogStatus("Price comparison - Tonight: €{0:F3} at {1}, Tomorrow morning: €{2:F3} at {3}",
                    eveningPrice.Value, eveningPrice.Key.ToString("HH:mm"),
                    bestMorningPrice.Value, bestMorningPrice.Key.ToString("HH:mm"));

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
                _logger.LogError(ex, "Error during evening price evaluation");
                LogStatus("Error evaluating evening shift");
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

                    _logger.LogInformation("Removed tonight's discharge from schedule");
                }

                // Schedule the new discharge for tomorrow morning
                var tomorrowDischargeTime = tomorrowMorningTime.AddMinutes(-_options.EmsPrepMinutesBefore);
                _scheduler.RunAt(tomorrowDischargeTime, () => { _ = ExecuteTomorrowMorningDischargeAsync(tomorrowMorningTime); });

                LogStatus("Scheduled tomorrow morning discharge at {0}",
                    tomorrowMorningTime.ToString("HH:mm"));
                LogStatus($"Rescheduled discharge to {tomorrowMorningTime.ToString("HH:mm")}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error rescheduling discharge to tomorrow morning");
                LogStatus("Error rescheduling to morning");
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
                        new ChargingPeriod
                        {
                            StartTime = dischargeTime.TimeOfDay,
                            EndTime = dischargeTime.TimeOfDay.Add(TimeSpan.FromHours(1)),
                            ChargeType = BatteryChargeType.Discharge,
                            PowerInWatts = Math.Min(_options.DefaultDischargePowerW, _options.MaxInverterPowerW)
                        }
                    }
                };

                await PrepareForScheduleAsync(morningDischargeSchedule, label: "Morning Discharge");

                var restoreTime = dischargeTime.AddHours(1).AddMinutes(_options.EmsRestoreMinutesAfter);
                _scheduler.RunAt(restoreTime, () => { _ = RestoreEmsAfterWindowAsync(); });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing tomorrow morning discharge");
                LogStatus("Error executing morning discharge");
            }
        }

        #endregion

        #region Helper Methods

        private void LogStatus(string message, params object[] args)
        {
            var formattedMessage = args.Length > 0 ? string.Format(message, args) : message;
            try
            {
                Console.WriteLine($"[BATTERY] {formattedMessage}");
            }
            catch { /* ignore console failures */ }

            _logger.LogInformation(message, args);

            try
            {
                _entities.InputText.BatteryManagementStatus.SetValue(formattedMessage);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to set BatteryManagementStatus");
            }
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

        // Returns human-friendly suffix about next/ongoing charge
        private static string BuildNextChargeSuffix(ChargingSchema schedule)
        {
            var now = DateTime.Now.TimeOfDay;
            var charges = schedule.Periods.Where(p => p.ChargeType == BatteryChargeType.Charge)
                .OrderBy(p => p.StartTime)
                .ToList();

            var current = charges.FirstOrDefault(p => now >= p.StartTime && now < p.EndTime);
            if (current != null)
                return $"charging now until {current.EndTime.ToString(@"hh\:mm")}";

            var next = charges.FirstOrDefault(p => p.StartTime >= now);
            if (next != null)
                return $"next charge {next.StartTime.ToString(@"hh\:mm")}";

            return "no charge planned";
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
                _logger.LogWarning(ex, "Could not retrieve current applied schedule from state");
                return null;
            }
        }

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
                _logger.LogWarning(ex, "Could not retrieve prepared schedule from state");
                return null;
            }
        }

        #endregion
    }
}
