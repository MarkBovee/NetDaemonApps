# Battery Management Quick Reference

## 🔋 3-Checkpoint Strategy Summary

### What it does
Automatically optimizes battery charging and discharging based on electricity prices and battery state, maximizing savings and revenue.

### Three Smart Checkpoints

1. **🌅 Morning Check** (before charge)
   - If battery SOC > 40% → discharge during morning high prices
   - Captures morning price peaks when possible

2. **⚡ Charge Period** (lowest prices)
   - Always charges during cheapest hours (typically 2-5 AM)
   - Full configured power for the configured duration

3. **🌆 Evening Decision** (30 min before discharge)
   - Compares tonight vs tomorrow morning prices
   - Moves discharge to tomorrow if more profitable

### Key Benefits
- **Revenue Maximization**: Up to 3 discharge periods per day during optimal prices
- **Automatic EMS Management**: Prevents conflicts with Energy Management System
- **Smart Scheduling**: Adapts to price trends rather than fixed schedules
- **State Persistence**: Survives system restarts and maintains schedules

### Monitoring
- input_text.battery_charge_schedule → human-readable charge window
- input_text.battery_discharge_schedule → human-readable discharge window
- input_text.battery_management_status → latest decision/status (now includes next-charge info)
- switch.ems → automatically toggled around battery control windows

---

## ⚙️ Configurable Options (appsettings Battery section)

- BaseUrl: SAJ API base URL (default https://eop.saj-electric.com)
- SimulationMode: if true, simulate without writing to SAJ API
- EmsPrepMinutesBefore: minutes to turn EMS off before a window
- EmsRestoreMinutesAfter: minutes to turn EMS on after a window
- MorningCheckOffsetHours: hours before charge window to consider morning discharge
- MorningWindowStartHour / MorningWindowEndHour: morning window
- EveningThresholdHour: when "evening" price window starts
- MaxInverterPowerW: inverter capability in W (default 8000)
- MaxBatteryCapacityWh: battery capacity in Wh (default 25000)
- DefaultChargePowerW / DefaultDischargePowerW: defaults for scheduled periods
- MinChargeBufferMinutes: minimum minutes to retain when trimming by SOC (default 10)

Tip: Change these in appsettings.json/appsettings.Development.json.

---

## 🧠 SOC-based Charge Time (simple optimization)

At schedule apply-time, the app estimates how long to charge to 100%:

- neededMinutes = ceil(((100 − SOC)% × MaxBatteryCapacityWh) / MaxInverterPowerW × 60)
- if SOC < 100% and neededMinutes is small → keep at least MinChargeBufferMinutes
- If currently scheduled charge minutes from "now" exceed neededMinutes → trim earliest charge windows to match
- Discharge windows are not changed

The status message shows SOC, needed minutes, scheduled minutes, and any trim summary.

---

## 🛰️ Status Messages (BatteryManagementStatus)

Examples you’ll see:
- Startup: "Started. Simulation: ON|OFF"
- Preparation: "Prepared schedule: N periods"
- SOC trim: "SOC 62.0%, need ~95m to full, had 140m scheduled. Trimmed 45m (removed=1, trimmed=0), target 95m, now 95m | next charge 02:00"
- Unchanged: "Schedule unchanged; skipping apply | next charge 02:00" (or "charging now until 03:00" / "no charge planned")
- Apply: "Applied schedule (sim)?: N periods | next charge 02:00"
- EMS toggles: "EMS OFF for battery window" / "EMS ON after battery window"
- Evening decision: "Shifting discharge to tomorrow 08:00" or "Keeping evening discharge (better price)"

---

## 🧪 Debug Mode
When a debugger is attached:
- Simulation mode is auto-enabled
- No schedule is written to SAJ API
- Detailed logging for safe testing
