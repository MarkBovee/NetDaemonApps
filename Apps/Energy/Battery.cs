namespace NetDaemonApps.Apps.Energy
{
    using System.Collections.Generic;
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
        private const double MaxInverterPower = 8000; // W
        private const double MaxSolarProduction = 4500; // W
        private const double MaxBatteryCapacity = 25000; // Wh

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

            // Load last applied schedule from the state file
            _lastAppliedSchedule = AppStateManager.GetState<DateTime?>(nameof(Battery), "LastAppliedSchedule");

            if (Debugger.IsAttached)
            {
                // Run simulation for debugging
                RunDebugSimulation();
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
            if (Debugger.IsAttached)
            {
                _logger.LogInformation($"SetBatterySchedule called. Last applied: {_lastAppliedSchedule?.ToString("yyyy-MM-dd HH:mm") ?? "never"}");
            }

            // Check if we need to set a new schedule (once per day) or adjust existing (every 15 minutes)
            if (_lastAppliedSchedule != null && _lastAppliedSchedule.Value.Date == DateTime.Now.Date)
            {
                if (Debugger.IsAttached)
                {
                    _logger.LogInformation("Schedule already applied today, running evaluation cycle");
                }

                var evaluatedSchema = EvaluateAndAdjustChargingSchema();
                if (evaluatedSchema != null)
                {
                    var currentAppliedSchema = GetCurrentAppliedSchema();

                    if (currentAppliedSchema == null || !currentAppliedSchema.IsEquivalentTo(evaluatedSchema))
                    {
                        _logger.LogInformation("Evaluation resulted in schema changes, applying new schema");
                        ApplyChargingSchema(evaluatedSchema);
                        SaveCurrentAppliedSchema(evaluatedSchema);
                    }
                    else
                    {
                        _logger.LogInformation("Evaluation complete - no schema changes needed");
                    }
                }
            }
            else
            {
                if (Debugger.IsAttached)
                {
                    _logger.LogInformation("No schedule applied today or first run, calculating initial schema");
                }

                var initialSchema = CalculateInitialChargingSchema();
                var currentAppliedSchema = GetCurrentAppliedSchema();

                if (currentAppliedSchema == null || !currentAppliedSchema.IsEquivalentTo(initialSchema))
                {
                    _logger.LogInformation("Initial schema setup or changed schema detected");

                    ApplyChargingSchema(initialSchema);
                    SaveCurrentAppliedSchema(initialSchema);
                }
                else
                {
                    _logger.LogInformation("Schema unchanged from last run");
                }
            }
        }

        /// <summary>
        /// Evaluates current conditions and adjusts the charging schema every 15 minutes
        /// </summary>
        /// <returns>New charging schema if recalculation was needed, null if only minor adjustments were performed</returns>
        private ChargingSchema? EvaluateAndAdjustChargingSchema()
        {
            try
            {
                var currentTime = DateTime.Now;
                var currentBatterySoc = GetCurrentBatterySOC();
                var currentSolarProduction = _entities.Sensor.PowerProductionNow.State ?? 0;
                var currentGridPower = _entities.Sensor.BatteryGridPower.State ?? 0;

                if (Debugger.IsAttached)
                {
                    _logger.LogInformation($"15-min evaluation: SOC={currentBatterySoc:F1}%, Solar={currentSolarProduction}W, Grid={currentGridPower}W");
                }

                // Check if we need to recalculate the entire schema (major changes)
                var shouldRecalculate = false;

                // Recalculate if battery SOC is critically low or very high
                if (currentBatterySoc < 15 || currentBatterySoc > 95)
                {
                    shouldRecalculate = true;
                    _logger.LogInformation($"Recalculating schema due to extreme battery SOC: {currentBatterySoc:F1}%");
                }

                // Recalculate if actual solar production is very different from expected
                var energyCurrentHour = _entities.Sensor.EnergyCurrentHour.State ?? 0;
                var energyNextHour = _entities.Sensor.EnergyNextHour.State ?? 0;

                // If current hour production is 50% higher or lower than expected, recalculate
                if (energyCurrentHour > 0 && (currentSolarProduction > energyCurrentHour * 1.5 || currentSolarProduction < energyCurrentHour * 0.5))
                {
                    shouldRecalculate = true;
                    _logger.LogInformation($"Recalculating schema due to solar production variance: actual={currentSolarProduction}W vs expected={energyCurrentHour}W");
                }

                // Recalculate once daily (if not done today)
                if (_lastAppliedSchedule == null || _lastAppliedSchedule.Value.Date != currentTime.Date)
                {
                    shouldRecalculate = true;
                    _logger.LogInformation("Recalculating daily schema");
                }

                if (shouldRecalculate)
                {
                    var newSchema = CalculateInitialChargingSchema();
                    newSchema.Source = "Evaluation-triggered recalculation";

                    if (Debugger.IsAttached)
                    {
                        _logger.LogInformation($"Recalculation triggered. New schema has {newSchema.Periods.Count} periods");
                        _logger.LogInformation($"New schema: {newSchema.ToLogString()}");
                    }

                    return newSchema;
                }
                else
                {
                    // Minor adjustments based on current conditions (no schema changes)
                    PerformMinorAdjustments(currentBatterySoc, currentSolarProduction, currentGridPower);
                    return null; // No schema changes
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during charging schema evaluation");
                return null;
            }
        }

        /// <summary>
        /// Performs minor power adjustments without changing the overall charging schema
        /// </summary>
        /// <param name="currentBatterySoc">Current battery state of charge</param>
        /// <param name="currentSolarProduction">Current solar production in watts</param>
        /// <param name="currentGridPower">Current grid power flow in watts</param>
        private void PerformMinorAdjustments(double currentBatterySoc, double currentSolarProduction, double currentGridPower)
        {
            var currentTime = DateTime.Now;

            // During solar hours (10-16), adjust charging based on excess solar
            if (currentTime.Hour >= 10 && currentTime.Hour <= 16 && currentSolarProduction > 1000)
            {
                // If we have excess solar and battery isn't full, increase charging
                if (currentGridPower < -500 && currentBatterySoc < 90) // Feeding back to grid
                {
                    // TODO: Implement dynamic power adjustment via API
                    _logger.LogInformation($"Could increase charging power due to excess solar: {-currentGridPower}W excess");
                }
            }

            // During evening peak (17-22), ensure we're discharging if battery is sufficiently charged
            if (currentTime.Hour >= 17 && currentTime.Hour <= 22 && currentBatterySoc > 60)
            {
                if (currentGridPower > 500) // Consuming from grid
                {
                    // TODO: Implement dynamic discharge adjustment via API
                    _logger.LogInformation($"Could increase discharge power during peak hours: consuming {currentGridPower}W from grid");
                }
            }

            // Emergency charging if battery critically low during expensive hours
            if (currentBatterySoc < 10 && currentTime.Hour >= 17 && currentTime.Hour <= 22)
            {
                _logger.LogWarning($"Emergency: Battery critically low ({currentBatterySoc:F1}%) during peak hours");
                // TODO: Implement emergency charging protocol
            }
        }

        /// <summary>
        /// Applies a charging schema by converting it to SAJ API format and uploading
        /// </summary>
        /// <param name="chargingSchema">The charging schema to apply</param>
        private void ApplyChargingSchema(ChargingSchema chargingSchema)
        {
            ApplyChargingSchema(chargingSchema, simulateOnly: Debugger.IsAttached);
        }

        /// <summary>
        /// Applies the given charging schema to the battery system via the SAJ Power API
        /// </summary>
        /// <param name="chargingSchema">The charging schema to apply</param>
        /// <param name="simulateOnly">If true, simulates the API call without actually executing it</param>
        private void ApplyChargingSchema(ChargingSchema chargingSchema, bool simulateOnly)
        {
            if (chargingSchema?.Periods == null || !chargingSchema.Periods.Any())
            {
                _logger.LogWarning("No charging schema to apply");
                return;
            }

            try
            {
                // Convert our charging schema to SAJ API format
                // Determine if we should use legacy format (1 charge + 1 discharge) or extended format (multiple charges + 1 discharge)
                var chargePeriods = chargingSchema.Periods.Where(cm => cm.ChargeType == BatteryChargeType.Charge).ToList();
                var dischargePeriods = chargingSchema.Periods.Where(cm => cm.ChargeType == BatteryChargeType.Discharge).ToList();

                if (!chargePeriods.Any() && !dischargePeriods.Any())
                {
                    _logger.LogWarning("No valid charge or discharge periods in schema");
                    return;
                }

                BatteryScheduleParameters scheduleParameters;

                // Use extended format if we have multiple charge periods, otherwise use legacy format
                if (chargePeriods.Count > 1 && dischargePeriods.Any())
                {
                    // Extended format: Multiple charge periods + 1 discharge period
                    scheduleParameters = SAJPowerBatteryApi.BuildBatteryScheduleParametersExtended(chargePeriods, dischargePeriods.First());
                    _logger.LogInformation($"Using extended format: {chargePeriods.Count} charge periods + 1 discharge period");
                }
                else
                {
                    // Legacy format: Use first charge and discharge periods
                    var chargePeriod = chargePeriods.FirstOrDefault();
                    var dischargePeriod = dischargePeriods.FirstOrDefault();

                    if (chargePeriod == null || dischargePeriod == null)
                    {
                        _logger.LogWarning("Legacy format requires exactly 1 charge and 1 discharge period");
                        return;
                    }

                    scheduleParameters = SAJPowerBatteryApi.BuildBatteryScheduleParameters(
                        chargeStart: chargePeriod.StartTime.ToString(@"hh\:mm"),
                        chargeEnd: chargePeriod.EndTime.ToString(@"hh\:mm"),
                        chargePower: chargePeriod.PowerInWatts,
                        dischargeStart: dischargePeriod.StartTime.ToString(@"hh\:mm"),
                        dischargeEnd: dischargePeriod.EndTime.ToString(@"hh\:mm"),
                        dischargePower: dischargePeriod.PowerInWatts
                    );
                    _logger.LogInformation("Using legacy format: 1 charge + 1 discharge period");
                }

                // Apply the schedule to the battery
                bool saved;
                if (simulateOnly)
                {
                    _logger.LogInformation("SIMULATION MODE: Skipping actual API call to SAJ Power Battery API");
                    _logger.LogInformation($"Would have applied schedule with Value: {scheduleParameters.Value}");
                    saved = true; // Simulate successful save
                }
                else
                {
                    saved = _saiPowerBatteryApi.SaveBatteryScheduleAsync(scheduleParameters).Result;
                }

                if (saved)
                {
                    // Update state tracking
                    _lastAppliedSchedule = DateTime.Now;
                    AppStateManager.SetState(nameof(Battery), "LastAppliedSchedule", _lastAppliedSchedule);

                    // Update Home Assistant input entities if they exist
                    if (!simulateOnly)
                    {
                        try
                        {
                            var firstCharge = chargePeriods.FirstOrDefault();
                            var firstDischarge = dischargePeriods.FirstOrDefault();

                            if (firstCharge != null)
                                _entities.InputText.BatteryChargeSchedule?.SetValue($"{firstCharge.StartTime:hh\\:mm}-{firstCharge.EndTime:hh\\:mm}");

                            if (firstDischarge != null)
                                _entities.InputText.BatteryDischargeSchedule?.SetValue($"{firstDischarge.StartTime:hh\\:mm}-{firstDischarge.EndTime:hh\\:mm}");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Could not update HA input entities - they may not exist");
                        }
                    }
                    else
                    {
                        _logger.LogInformation("SIMULATION MODE: Skipping Home Assistant input entity updates");
                    }

                    // Log the applied schedule details
                    var scheduleDescription = string.Join(", ", chargingSchema.Periods.Select(p =>
                        $"{p.ChargeType} {p.StartTime:hh\\:mm}-{p.EndTime:hh\\:mm} @ {p.PowerInWatts}W"));
                    _logger.LogInformation($"Applied charging schema: {scheduleDescription}");
                }
                else
                {
                    _logger.LogWarning("Failed to apply charging schema via SAJ API");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error applying charging schema");
            }
        }

        /// <summary>
        /// Calculates the initial charging schema based on energy prices, solar production, and battery state
        /// </summary>
        /// <returns>Complete charging schema for the day</returns>
        private ChargingSchema CalculateInitialChargingSchema()
        {
            var chargingSchema = new List<ChargingPeriod>();
            var currentTime = DateTime.Now;

            // Get current energy data
            var currentBatterySoc = GetCurrentBatterySOC();
            var currentSolarProduction = _entities.Sensor.PowerProductionNow.State ?? 0;
            var energyProductionToday = _entities.Sensor.EnergyProductionToday.State ?? 0;
            var energyProductionTomorrow = _entities.Sensor.EnergyProductionTomorrow.State ?? 0;
            var energyProductionTodayRemaining = _entities.Sensor.EnergyProductionTodayRemaining.State ?? 0;
            var currentGridPower = _entities.Sensor.BatteryGridPower.State ?? 0;

            // Get price data
            var pricesToday = _priceHelper.PricesToday;
            if (pricesToday == null || pricesToday.Count < 3)
            {
                _logger.LogWarning("Insufficient price data for charging schema calculation");
                return new ChargingSchema { Periods = chargingSchema };
            }

            // Calculate energy needs and capacity
            var currentBatteryCapacityWh = (currentBatterySoc / 100.0) * MaxBatteryCapacity;
            var availableBatteryCapacity = MaxBatteryCapacity - currentBatteryCapacityWh;
            var estimatedDailyConsumption = EstimateDailyEnergyConsumption();

            if (Debugger.IsAttached)
            {
                _logger.LogInformation($"Battery SOC: {currentBatterySoc:F1}%, Capacity: {currentBatteryCapacityWh:F0}Wh");
                _logger.LogInformation($"Solar Production: Now={currentSolarProduction}W, Today={energyProductionToday}kWh, Tomorrow={energyProductionTomorrow}kWh, Remaining={energyProductionTodayRemaining}kWh");
                _logger.LogInformation($"Grid Power: {currentGridPower}W, Daily Consumption Est: {estimatedDailyConsumption:F1}kWh");
            }

            // Strategy 1: Morning charge if battery is low and insufficient solar expected
            if (currentTime.Hour < 10 && currentBatterySoc < 30 && energyProductionTodayRemaining < estimatedDailyConsumption * 0.5)
            {
                var morningPrices = pricesToday.Where(p => p.Key.Hour >= currentTime.Hour && p.Key.Hour < 10).ToDictionary(p => p.Key, p => p.Value);
                var (morningChargeStart, morningChargeEnd) = PriceHelper.GetLowestPriceTimeslot(morningPrices, 2);
                if (morningChargeStart != DateTime.MinValue)
                {
                    var morningChargePower = Math.Min(MaxInverterPower, (int)(availableBatteryCapacity * 0.6 / 2)); // Charge 60% of available capacity in 2 hours
                    chargingSchema.Add(new ChargingPeriod
                    {
                        ChargeType = BatteryChargeType.Charge,
                        StartTime = morningChargeStart.TimeOfDay,
                        EndTime = morningChargeEnd.TimeOfDay,
                        PowerInWatts = (int)morningChargePower
                    });
                }
            }

            // Strategy 2: Solar optimization charge (mid-day when solar production peaks)
            if (energyProductionTodayRemaining > 0 && currentSolarProduction > 0)
            {
                var solarChargeStartHour = Math.Max(currentTime.Hour, 11);
                var solarChargeEndHour = Math.Min(solarChargeStartHour + 4, 15);

                // Calculate optimal solar charging power based on expected production
                var expectedSolarPeak = Math.Max(currentSolarProduction, MaxSolarProduction * 0.8);
                var solarChargePower = Math.Min((int)expectedSolarPeak, MaxInverterPower);

                chargingSchema.Add(new ChargingPeriod
                {
                    ChargeType = BatteryChargeType.Charge,
                    StartTime = new TimeSpan(solarChargeStartHour, 0, 0),
                    EndTime = new TimeSpan(solarChargeEndHour, 0, 0),
                    PowerInWatts = (int)solarChargePower
                });
            }

            // Strategy 3: Evening discharge during peak price hours
            var eveningPrices = pricesToday.Where(p => p.Key.Hour >= 17 && p.Key.Hour <= 22).ToDictionary(p => p.Key, p => p.Value);
            var (dischargeStart, dischargeEnd) = PriceHelper.GetHighestPriceTimeslot(eveningPrices, 3);
            if (dischargeStart != DateTime.MinValue && currentBatterySoc > 40)
            {
                // Discharge power based on typical evening consumption
                var eveningDischargePower = Math.Min(MaxInverterPower, (int)(estimatedDailyConsumption * 1000 / 6)); // Spread consumption over 6 hours
                chargingSchema.Add(new ChargingPeriod
                {
                    ChargeType = BatteryChargeType.Discharge,
                    StartTime = dischargeStart.TimeOfDay,
                    EndTime = dischargeEnd.TimeOfDay,
                    PowerInWatts = (int)eveningDischargePower
                });
            }

            // Strategy 4: Overnight charging if tomorrow's solar forecast is poor and prices are very low
            if (energyProductionTomorrow < estimatedDailyConsumption * 0.7)
            {
                var nightPricesList = pricesToday.Where(p => p.Key.Hour >= 23 || p.Key.Hour <= 5).OrderBy(p => p.Value).Take(2).ToList();
                var averagePrice = pricesToday.Values.Average();

                if (nightPricesList.Any() && nightPricesList.First().Value < averagePrice * 0.6)
                {
                    var nightChargeStart = nightPricesList.First().Key;
                    var nightChargeEnd = nightChargeStart.AddHours(2);
                    var nightChargePower = Math.Min(MaxInverterPower, (int)(availableBatteryCapacity * 0.4 / 2)); // Conservative overnight charging

                    chargingSchema.Add(new ChargingPeriod
                    {
                        ChargeType = BatteryChargeType.Charge,
                        StartTime = nightChargeStart.TimeOfDay,
                        EndTime = nightChargeEnd.TimeOfDay,
                        PowerInWatts = (int)nightChargePower
                    });
                }
            }

            // Sort charging moments by start time
            chargingSchema = chargingSchema.OrderBy(cm => cm.StartTime).ToList();

            if (Debugger.IsAttached)
            {
                _logger.LogInformation($"Generated {chargingSchema.Count} charging moments:");
                foreach (var moment in chargingSchema)
                {
                    _logger.LogInformation($"  {moment.ChargeType}: {moment.StartTime:hh\\:mm}-{moment.EndTime:hh\\:mm} @ {moment.PowerInWatts}W");
                }
            }

            return new ChargingSchema { Periods = chargingSchema };
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
                return mainBatterySoc.Value;

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
            return validModules.Any() ? validModules.Average(soc => soc!.Value) : 50.0; // Default to 50% if no data
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
                12 or 1 or 2 => 1.4,    // Winter - higher heating
                3 or 11 => 1.2,         // Shoulder months
                4 or 5 or 9 or 10 => 1.0, // Moderate months
                6 or 7 or 8 => 0.9,     // Summer - lower heating, some cooling
                _ => 1.0
            };

            // If we have real-time data, factor in current consumption patterns
            if (currentGridPower > 0 && currentSolarPower >= 0)
            {
                var currentTotalConsumption = currentGridPower + currentSolarPower;
                var hourlyConsumption = currentTotalConsumption / 1000.0; // Convert W to kWh

                // Extrapolate based on time of day patterns
                var timeMultiplier = currentTime.Hour switch
                {
                    >= 7 and <= 9 => 1.2,   // Morning peak
                    >= 10 and <= 16 => 0.8, // Daytime low
                    >= 17 and <= 22 => 1.4, // Evening peak
                    _ => 0.6                 // Night
                };

                var estimatedDailyFromCurrent = (hourlyConsumption / timeMultiplier) * 24;

                // Average the base estimate with real-time extrapolation
                return (baseConsumptionKwh * seasonalMultiplier + estimatedDailyFromCurrent) / 2;
            }

            return baseConsumptionKwh * seasonalMultiplier;
        }

        /// <summary>
        /// Runs a debugging simulation to test initial schema calculation and adjustment logic
        /// </summary>
        private void RunDebugSimulation()
        {
            _logger.LogInformation("=== Starting Debug Simulation ===");

            // Clear any existing state to start fresh
            AppStateManager.SetState(nameof(Battery), "CurrentAppliedSchema", (ChargingSchema?)null);
            AppStateManager.SetState(nameof(Battery), "LastAppliedSchedule", (DateTime?)null);
            _lastAppliedSchedule = null;

            _logger.LogInformation("Step 1: Calculating initial charging schema (simulating first run)");

            // Simulate initial schema calculation
            var initialSchema = CalculateInitialChargingSchema();
            _logger.LogInformation($"Initial schema calculated with {initialSchema.Periods.Count} periods:");
            foreach (var period in initialSchema.Periods)
            {
                _logger.LogInformation($"  - {period.ChargeType}: {period.StartTime:hh\\:mm}-{period.EndTime:hh\\:mm} @ {period.PowerInWatts}W");
            }

            // Apply the initial schema
            _logger.LogInformation("Applying initial schema (simulation mode)...");
            ApplyChargingSchema(initialSchema, simulateOnly: true);
            SaveCurrentAppliedSchema(initialSchema);
            _lastAppliedSchedule = DateTime.Now;
            AppStateManager.SetState(nameof(Battery), "LastAppliedSchedule", _lastAppliedSchedule);

            _logger.LogInformation("Step 2: Simulating 15-minute evaluation cycle (should detect no changes)");

            // Simulate running the evaluation again - should detect no changes
            EvaluateAndAdjustChargingSchema();

            _logger.LogInformation("Step 3: Simulating conditions that would trigger recalculation");

            // Simulate a condition that would trigger recalculation by temporarily modifying the stored schema
            var modifiedSchema = initialSchema.Clone();
            if (modifiedSchema.Periods.Any())
            {
                // Modify the first period's power to simulate a change
                modifiedSchema.Periods.First().PowerInWatts += 1000;
                SaveCurrentAppliedSchema(modifiedSchema);
                _logger.LogInformation("Modified stored schema to simulate changed conditions");

                // Now run evaluation again - should detect the change and recalculate
                _logger.LogInformation("Running evaluation with modified conditions...");
                EvaluateAndAdjustChargingSchema();
            }

            _logger.LogInformation("=== Debug Simulation Complete ===");
        }

        /// <summary>
        /// Retrieves the currently applied charging schema from persistent state
        /// </summary>
        /// <returns>The currently applied charging schema, or null if none exists</returns>
        private ChargingSchema? GetCurrentAppliedSchema()
        {
            try
            {
                return AppStateManager.GetState<ChargingSchema?>(nameof(Battery), "CurrentAppliedSchema");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not retrieve current applied schema from state");
                return null;
            }
        }

        /// <summary>
        /// Saves the currently applied charging schema to persistent state
        /// </summary>
        /// <param name="schema">The charging schema that was applied</param>
        private void SaveCurrentAppliedSchema(ChargingSchema schema)
        {
            try
            {
                AppStateManager.SetState(nameof(Battery), "CurrentAppliedSchema", schema);
                _logger.LogDebug("Saved current applied schema to state");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not save current applied schema to state");
            }
        }
    }
}
