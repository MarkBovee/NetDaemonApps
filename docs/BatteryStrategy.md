# Smart Battery Management: 3-Checkpoint Strategy

## Overview

The NetDaemonApps Battery management system implements an intelligent 3-checkpoint optimization strategy that adapts to electricity price trends and battery conditions throughout the day. This system maximizes energy savings and revenue by making smart decisions at three critical points in time.

## Strategy Architecture

### Design Philosophy

The 3-checkpoint strategy was designed based on typical Dutch electricity price patterns, where:
- **Night/Early Morning**: Lowest prices (charging optimal)
- **Morning Peak**: High prices (discharge opportunity)
- **Afternoon**: Moderate prices (solar production)
- **Evening Peak**: High prices (primary discharge target)

However, the system is smart enough to adapt when tomorrow's morning prices are higher than tonight's evening prices, creating additional revenue opportunities.

## Detailed Checkpoint Analysis

### 🌅 Checkpoint 1: Morning Evaluation (Pre-Charge)

**Timing**: 2 hours before the planned charging period  
**Primary Goal**: Capture morning price peaks if battery has sufficient capacity

#### Logic Flow
```csharp
if (currentSOC > 40.0 && morningCheckTime > 06:00)
{
    // Find highest price period between 6 AM and charge start time
    var morningPrices = pricesToday.Where(p => 
        p.Key.TimeOfDay >= 06:00 && 
        p.Key.TimeOfDay < chargeStart.TimeOfDay);
    
    // Add 1-hour discharge at highest price
    if (morningPrices.Any())
    {
        AddMorningDischarge(morningPrices.OrderByDescending(p => p.Value).First());
    }
}
```

#### Key Parameters
- **SOC Threshold**: 40% (ensures sufficient capacity for planned operations)
- **Time Window**: 6:00 AM to charge start time
- **Discharge Duration**: 1 hour
- **Power**: 8kW (maximum inverter capacity)

#### Decision Factors
- Battery state of charge
- Price differential between morning and base prices
- Available time window before charging

### ⚡ Checkpoint 2: Optimal Charging

**Timing**: During the lowest price period of the day  
**Primary Goal**: Fill battery when energy costs are minimal

#### Implementation
```csharp
var (chargeStart, chargeEnd) = PriceHelper.GetLowestPriceTimeslot(pricesToday, 3);

periods.Add(new ChargingPeriod
{
    StartTime = chargeStart.TimeOfDay,
    EndTime = chargeEnd.TimeOfDay,
    ChargeType = BatteryChargeType.Charge,
    PowerInWatts = 8000
});
```

#### Key Parameters
- **Duration**: 3 hours (sufficient for full charge from typical SOC levels)
- **Power**: 8kW (maximum safe charging rate)
- **Timing**: Automatically determined by price analysis

#### EMS Integration
```csharp
// 5 minutes before charge period
emsShutdownTime = periodStart.AddMinutes(-5);
scheduler.RunAt(emsShutdownTime, () => {
    entities.Switch.Ems.TurnOff();
    Task.Delay(10000).Wait(); // 10 second stabilization
    ApplyChargingSchedule();
});
```

### 🌆 Checkpoint 3: Evening Price Comparison

**Timing**: 30 minutes before scheduled evening discharge  
**Primary Goal**: Maximize discharge revenue by comparing timing options

#### Algorithm
```csharp
// Get tonight's evening price (current highest)
var eveningPrice = pricesToday.OrderByDescending(p => p.Value).First();

// Get tomorrow morning high prices (6-12 AM window)
var tomorrowMorningPrices = pricesTomorrow.Where(p => 
    p.Key.TimeOfDay >= TimeSpan.FromHours(6) && 
    p.Key.TimeOfDay < TimeSpan.FromHours(12));

var bestMorningPrice = tomorrowMorningPrices.OrderByDescending(p => p.Value).First();

// Decision logic
if (bestMorningPrice.Value > eveningPrice.Value)
{
    RescheduleDischargeTomorrowMorning(bestMorningPrice.Key);
}
```

#### Rescheduling Process
1. **Cancel Tonight**: Remove evening discharge from current schedule
2. **Schedule Tomorrow**: Create new discharge period for tomorrow morning
3. **State Management**: Update persistent state to survive restarts
4. **EMS Coordination**: Ensure proper EMS management for new timing

## Technical Implementation

### State Management

The system uses `AppStateManager` for persistent state across daemon restarts:

```csharp
// Save prepared schedule
AppStateManager.SetState(nameof(Battery), "PreparedSchedule", schedule);
AppStateManager.SetState(nameof(Battery), "PreparedScheduleDate", DateTime.Today);

// Retrieve on startup
var preparedDate = AppStateManager.GetState<DateTime?>(nameof(Battery), "PreparedScheduleDate");
var existingSchedule = AppStateManager.GetState<ChargingSchema?>(nameof(Battery), "PreparedSchedule");
```

### EMS Management

Energy Management System coordination ensures no conflicts:

```csharp
private void PrepareForBatteryPeriod(ChargingPeriod period)
{
    // 1. Shutdown EMS with verification
    if (entities.Switch.Ems.State == "on")
    {
        entities.Switch.Ems.TurnOff();
        Task.Delay(TimeSpan.FromSeconds(10)).Wait(); // Stabilization
    }
    
    // 2. Apply battery schedule
    ApplyChargingSchedule(preparedSchedule, simulateOnly: Debugger.IsAttached);
}

private void RestoreEmsAfterPeriod(ChargingPeriod period)
{
    // Restore EMS 1 minute after period ends
    if (entities.Switch.Ems.State == "off")
    {
        entities.Switch.Ems.TurnOn();
    }
}
```

### SAJ Power Integration

Direct API integration for schedule application:

```csharp
var scheduleParameters = SAJPowerBatteryApi.BuildBatteryScheduleParameters(allPeriods);
var saved = saiPowerBatteryApi.SaveBatteryScheduleAsync(scheduleParameters).Result;
```

## Configuration Requirements

### Home Assistant Entities

```yaml
# Required entities in Home Assistant
switch.ems                              # Energy Management System control
sensor.inverter_hst2083j2446e06861_battery_state_of_charge  # Main battery SOC
input_text.battery_charge_schedule      # Display current charge schedule
input_text.battery_discharge_schedule   # Display current discharge schedule

# Fallback battery modules (if main sensor unavailable)
sensor.battery_b2n0200j2403e01735_bat_soc
sensor.battery_b2u4250j2511e06231_bat_soc
# ... additional battery modules
```

### Price Data Integration

The system requires access to `IPriceHelper` which provides:
- `PricesToday`: Dictionary<DateTime, double> with hourly prices for today
- `PricesTomorrow`: Dictionary<DateTime, double> with hourly prices for tomorrow

### SAJ Power Battery Configuration

```csharp
// In Battery constructor
_saiPowerBatteryApi = new SAJPowerBatteryApi(
    username: "MBovee", 
    password: "fnq@tce8CTQ5kcm4cuw", 
    serialNumber: "HST2083J2446E06861"
);
```

## Monitoring & Debugging

### Log Analysis

Key log messages to monitor:

```
[INFO] Added morning discharge at 08:00 (SOC: 65%, Price: €0.45)
[INFO] Scheduled EMS shutdown at 01:55 for Charge period 02:00-05:00
[INFO] Price comparison - Tonight: €0.38 at 20:00, Tomorrow morning: €0.47 at 08:00
[INFO] Tomorrow morning price is higher, rescheduling discharge to tomorrow morning
```

### Debug Mode

When running with debugger attached:
- All battery operations are simulated (no actual API calls)
- Detailed schedule information logged
- EMS operations are logged but not executed
- State changes are preserved for testing

### Performance Metrics

Track these metrics for optimization:
- Daily revenue from discharge operations
- Battery cycle efficiency (charge vs discharge amounts)
- EMS downtime (should be minimal)
- Schedule adherence (actual vs planned timing)

## Future Enhancements

The current implementation provides a foundation for advanced features:

### Solar Integration
```csharp
// Planned: Solar forecast integration
var solarForecast = GetTomorrowSolarForecast();
if (solarForecast.TotalProduction > dailyConsumption)
{
    ReduceChargingPower(); // Less grid charging needed
}
```

### Machine Learning
```csharp
// Planned: Pattern recognition
var historicalData = GetBatteryPerformanceHistory();
var optimizedPower = MLModel.PredictOptimalChargingPower(
    currentSOC, timeToTarget, historicalEfficiency);
```

### Grid Services
```csharp
// Planned: Demand response participation
if (GridFrequency < 49.8) // Grid stress
{
    PauseDischargeTemporilyToSupportGrid();
}
```

## Troubleshooting

### Common Issues

1. **Missing Price Data**
   - Check `IPriceHelper` implementation
   - Verify energy provider API connection
   - Ensure prices are available for both today and tomorrow

2. **EMS Not Responding**
   - Verify `switch.ems` entity exists in Home Assistant
   - Check network connectivity to EMS system
   - Monitor EMS state changes in HA Developer Tools

3. **SAJ API Failures**
   - Verify credentials in SAJPowerBatteryApi constructor
   - Check network access to battery system
   - Monitor API response in debug logs

4. **Schedule Not Applied**
   - Check battery system is in correct mode for external scheduling
   - Verify SAJ API response codes
   - Monitor `input_text` entities for schedule updates

### Diagnostic Commands

```csharp
// Check current state
var currentSchedule = GetCurrentAppliedSchedule();
var preparedSchedule = GetPreparedSchedule();
var batterySOC = GetCurrentBatterySOC();

// Verify EMS state
var emsState = entities.Switch.Ems.State;

// Check price data availability
var pricesAvailable = priceHelper.PricesToday?.Count > 0 && 
                     priceHelper.PricesTomorrow?.Count > 0;
```

This intelligent 3-checkpoint strategy ensures optimal battery utilization while maintaining system reliability and providing maximum financial benefit from energy arbitrage opportunities.
