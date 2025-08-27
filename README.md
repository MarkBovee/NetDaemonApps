# NetDaemonApps: .NET Daemon for Home Assistant

## Overview
NetDaemonApps is a collection of smart automation apps for Home Assistant, built using .NET. It helps you manage and automate your home’s energy, security, and more, making your smart home smarter and easier to use.

This project is designed for both non-developers and developers:
- **Non-developers**: Enjoy ready-made automations for energy management, vacation security, and more.
- **Developers**: Easily extend and customize automations using C# and the NetDaemon platform.

---

## Main Features & Apps

### Energy Management
- **Energy/Appliances**: Automates and monitors home appliances for efficient energy use.
- **Energy/Battery**: Advanced battery storage management with intelligent 3-checkpoint optimization strategy. Features include:
  - **Smart Morning Check**: Automatically discharges in the morning if battery SOC > 40% during high price periods
  - **Optimal Charging**: Always charges during the lowest price periods (typically early morning)
  - **Dynamic Evening Evaluation**: Compares tomorrow morning vs tonight evening prices to optimize discharge timing
  - **EMS Integration**: Automatically manages Energy Management System (EMS) shutdown/restore to prevent conflicts
  - **SAJ Power Battery API**: Full integration with SAJ Power battery systems for schedule application
  - **State Persistence**: Maintains schedules across daemon restarts with robust error handling
- **Energy/WaterHeater**: Controls water heater operations for optimal energy savings.

### Vacation Mode
- **Vacation/Alarm**: Enhances home security when you’re away, automating alarms and notifications.
- **Vacation/LightsOnVacation**: Simulates presence by randomly turning on lights and lamps when it’s dark during vacation mode. Automatically discovers all lights and smart switches that are likely to be lamps, and excludes switches for appliances (like TVs, dishwashers, etc.). Lights change hourly for realism and turn off at night. Debug mode shows which entities are considered lights for easy verification.

### Utilities & Models
- **Models/**: Contains helper classes for managing app state, battery APIs, and price information.
- **Enums/**: Defines levels and other enums used throughout the apps.
- **Utils.cs**: Utility functions shared by multiple apps.

---

## 🔋 Smart Battery Management: 3-Checkpoint Strategy

The Energy/Battery app features an intelligent optimization strategy that adapts to price trends and battery conditions throughout the day. This advanced system maximizes energy savings and revenue by making smart decisions at three critical checkpoints.

### How It Works

#### 🌅 **Checkpoint 1: Morning Evaluation (Pre-Charge)**
- **When**: 2 hours before the planned charging period
- **Logic**: Checks current battery State of Charge (SOC)
- **Action**: If SOC > 40%, automatically adds a morning discharge during high price periods (6 AM - charge time)
- **Benefit**: Captures morning price peaks when battery has sufficient capacity

#### ⚡ **Checkpoint 2: Optimal Charging**
- **When**: During the lowest price period of the day (typically 2-5 AM)
- **Logic**: Always charges at maximum power (8kW) during cheapest electricity hours
- **Action**: Fills battery when energy costs are minimal
- **EMS Integration**: Automatically shuts down Energy Management System 5 minutes before charging to prevent conflicts

#### 🌆 **Checkpoint 3: Evening Price Comparison**
- **When**: 30 minutes before scheduled evening discharge
- **Logic**: Compares tomorrow morning prices (6-12 AM) vs tonight's evening prices
- **Decision**:
  - If **tomorrow morning price > tonight evening price**: Cancels tonight's discharge and reschedules for tomorrow morning
  - If **tonight evening price ≥ tomorrow morning price**: Keeps original evening discharge schedule
- **Benefit**: Maximizes revenue by choosing the most profitable discharge timing

### Technical Features

- **EMS Management**: Automatically handles Energy Management System shutdown/restore with proper timing buffers
- **State Persistence**: Maintains schedules across daemon restarts using robust state management
- **SAJ Power Integration**: Full API integration for applying schedules to SAJ Power battery systems
- **Comprehensive Logging**: Detailed logs for all decisions, price comparisons, and schedule changes
- **Error Handling**: Graceful fallbacks and recovery from API failures or missing data

### Configuration Requirements

- **Home Assistant Entities**:
  - `switch.ems` - Energy Management System control
  - Battery SOC sensors (main inverter + individual modules)
  - Price data from energy provider integration

- **SAJ Power Battery**: Valid credentials and network access to battery system

### Example Daily Flow

```
00:05 - Daily schedule calculation
├── SOC Check: 65% → Add morning discharge at 08:00-09:00 (€0.45/kWh)
├── Charge: 02:00-05:00 at €0.18/kWh  
└── Evening discharge: 20:00-21:00 at €0.38/kWh (pending evaluation)

19:30 - Evening price comparison
├── Tonight: €0.38/kWh at 20:00
├── Tomorrow morning: €0.47/kWh at 08:00
└── Decision: Cancel tonight, reschedule for tomorrow 08:00

Result: 3 discharge periods (morning + rescheduled + next day) capturing optimal prices
```

---

## How It Works (For Everyone)
- The daemon connects to your Home Assistant instance and runs automation apps in real-time.
- Each app is focused on a specific area (energy, security, etc.) and works out-of-the-box.
- Configuration is managed via Home Assistant and appsettings files.

---

## Getting Started (For Developers)

### 1. Code Generation (Entities & Services)
To keep your automations up-to-date with your Home Assistant setup, generate entity and service classes:

```powershell
# Restore required .NET tools
 dotnet tool restore
# Generate entity/service code from Home Assistant metadata
 dotnet tool run nd-codegen
```
This will update `HomeAssistantGenerated.cs` and related files based on your Home Assistant configuration.

### 2. Configuration
Edit `appsettings.json` or `appsettings.Development.json` to set up your Home Assistant connection and app preferences.

---

## Publishing to Home Assistant

1. **Build the Project**
   ```powershell
   dotnet build
   ```
2. **Publish the Daemon**
   ```powershell
   dotnet publish -c Release -o ./publish
   ```
3. **Deploy to Home Assistant**
   - Copy the contents of the `publish` folder to your Home Assistant server.
   - Configure the daemon to run as a service or via Docker, depending on your setup.
   - Ensure your Home Assistant instance is accessible and credentials are set in the configuration files.

---

## Debugging & Verification

### Battery Strategy Debugging
- **Debug Mode**: When running with a debugger attached, the Battery app simulates all operations without actually applying schedules to the battery system
- **Comprehensive Logging**: All checkpoint decisions, price comparisons, and schedule changes are logged with detailed information
- **State Inspection**: Use Home Assistant's Developer Tools to check `input_text.battery_charge_schedule` and `input_text.battery_discharge_schedule` entities for current schedules
- **EMS Monitoring**: Watch the `switch.ems` entity to verify proper shutdown/restore timing around battery periods

### Vacation Lighting Debugging
- When running in debug mode (with a debugger attached), the Vacation/LightsOnVacation app will log the list of entities it considers to be lights or lamps. This helps you verify and tune the filter so only real lamps are included.
- In production, only these entities will be controlled for vacation lighting.

---

## Need Help?
- For questions or help, check the [NetDaemon documentation](https://netdaemon.xyz/) or Home Assistant forums.
- Feel free to customize or extend the apps to fit your needs!

---

Enjoy a smarter, more automated home with NetDaemonApps!
