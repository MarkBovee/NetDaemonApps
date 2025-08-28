# NetDaemonApps: .NET Daemon for Home Assistant

## Overview
NetDaemonApps is a comprehensive collection of smart automation apps for Home Assistant, built using .NET 9.0 and the NetDaemon framework. It provides intelligent energy management, vacation security automation, and advanced battery optimization for modern smart homes.

This project is designed for both non-developers and developers:
- **Non-developers**: Enjoy ready-made automations for energy management, vacation security, and more.
- **Developers**: Easily extend and customize automations using C# and the NetDaemon platform.

---

## 🚀 Key Features & Apps

### 🔋 Energy Management
- **Energy/Appliances**: Automates and monitors home appliances for efficient energy use.
- **Energy/Battery**: Advanced battery storage management with intelligent 3-checkpoint optimization strategy featuring:
  - **Day-Specific Scheduling**: Target specific weekdays for charge/discharge periods using "1,1,1,1,1,1,1" format (Mon-Sun)
  - **Smart Morning Check**: Automatically discharges in the morning if battery SOC > 40% during high price periods
  - **Optimal Charging**: Always charges during the lowest price periods (typically early morning)
  - **Dynamic Evening Evaluation**: Compares tomorrow morning vs tonight evening prices to optimize discharge timing
  - **EMS Integration**: Automatically manages Energy Management System (EMS) shutdown/restore to prevent conflicts
  - **SAJ Power Battery API**: Full integration with SAJ Power battery systems for schedule application
  - **State Persistence**: Maintains schedules across daemon restarts with robust error handling
- **Energy/WaterHeater**: Controls water heater operations for optimal energy savings.

### 🏡 Vacation Mode
- **Vacation/Alarm**: Enhances home security when you're away, automating alarms and notifications.
- **Vacation/LightsOnVacation**: Simulates presence by randomly turning on lights and lamps when it's dark during vacation mode. Automatically discovers all lights and smart switches that are likely to be lamps, and excludes switches for appliances (like TVs, dishwashers, etc.). Lights change hourly for realism and turn off at night. Debug mode shows which entities are considered lights for easy verification.

### 🛠️ Utilities & Models
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
- **Action**: Fills battery when energy costs are minimal
- **EMS Integration**: Automatically shuts down Energy Management System 5 minutes before charging to prevent conflicts

#### 🌆 **Checkpoint 3: Evening Price Comparison**
- **When**: 30 minutes before scheduled evening discharge
- **Logic**: Compares tomorrow morning prices (6-12 AM) vs tonight's evening prices
- **Decision**:
  - If **tomorrow morning price > tonight evening price**: Cancels tonight's discharge and reschedules for tomorrow morning
  - If **tonight evening price ≥ tomorrow morning price**: Keeps original evening discharge schedule
- **Benefit**: Maximizes revenue by choosing the most profitable discharge timing

### 📅 Day-Specific Scheduling (NEW!)

The battery system now supports precise day-of-week targeting for optimal scheduling:

- **Weekday Format**: Uses "1,1,1,1,1,1,1" pattern (Monday through Sunday, 1=active, 0=inactive)
- **Smart Targeting**:
  - **Charge periods**: Automatically target tomorrow's day for optimal schedule application
  - **Discharge periods**: Target today's day for immediate application
- **Configuration**: Enable with `EnableDaySpecificScheduling: true` in battery options
- **Benefits**: Avoid daily schedules running every day - precise surgical control for energy optimization

### 🔄 Optimization Schedule

#### **📅 Daily Schedule Preparation**
- **Once per day** at **00:05** (5 minutes after midnight)
- Full recalculation of the entire day's charging/discharging schedule
- Uses latest energy price data and battery parameters

#### **⚡ Continuous Monitoring & Status Updates**
- **Battery Mode Monitoring**: Every **5 minutes**
  - Checks SAJ Power API for current battery mode (EMS/Manual/etc.)
  - Updates Home Assistant entities with current status
  
- **Schedule Status Updates**: Every **1 minute** 
  - Updates dashboard with current schedule status
  - Provides responsive UI feedback

#### **🔧 Dynamic Adjustments**
- **Real-time EMS Management**: The system doesn't constantly re-optimize the schedule, but it does:
  - Monitor for EMS mode conflicts and automatically retry when needed
  - Apply/remove EMS mode precisely at scheduled charge/discharge windows
  - Handle retries with 5-minute intervals when battery is in conflicting mode

#### **🎯 Key Schedule Events**
1. **00:05 Daily**: Complete schedule recalculation for the new day
2. **Before each period**: EMS mode disabled 5 minutes before charge/discharge starts
3. **During periods**: EMS mode enabled during active charge/discharge windows
4. **After periods**: EMS mode restored after completion

#### **⚙️ Retry Logic**
- If price data isn't available at 00:05, retry every **10 minutes**
- If battery is in EMS mode when trying to apply schedule, retry every **5 minutes**
- Failed operations are logged and rescheduled automatically

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

```text
00:05 - Daily schedule calculation
├── SOC Check: 65% → Add morning discharge at 08:00-09:00 (€0.45/kWh) [Wed only]
├── Charge: 02:00-05:00 at €0.18/kWh [Thu only - tomorrow]
└── Evening discharge: 20:00-21:00 at €0.38/kWh [Wed only - today]

19:30 - Evening price comparison
├── Tonight: €0.38/kWh at 20:00
├── Tomorrow morning: €0.47/kWh at 08:00
└── Decision: Cancel tonight, reschedule for tomorrow 08:00

Result: Precise day targeting with surgical schedule application
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

## 🐛 Debugging & Verification

### Battery Strategy Debugging

- **Debug Mode**: When running with a debugger attached, the Battery app simulates all operations without actually applying schedules to the battery system
- **Comprehensive Logging**: All checkpoint decisions, price comparisons, and schedule changes are logged with detailed information
- **State Inspection**: Use Home Assistant's Developer Tools to check `input_text.battery_charge_schedule` and `input_text.battery_discharge_schedule` entities for current schedules
- **EMS Monitoring**: Watch the `switch.ems` entity to verify proper shutdown/restore timing around battery periods

### Vacation Lighting Debugging

- When running in debug mode (with a debugger attached), the Vacation/LightsOnVacation app will log the list of entities it considers to be lights or lamps. This helps you verify and tune the filter so only real lamps are included.
- In production, only these entities will be controlled for vacation lighting.

---

## 🏗️ Project Structure

```text
NetDaemonApps/
├── Apps/                          # Main automation logic
│   ├── GlobalUsings.cs           # Shared using statements
│   ├── Energy/                   # Energy management automations
│   │   ├── Appliances.cs         # Appliance control
│   │   ├── Battery.cs            # Smart battery management
│   │   └── WaterHeater.cs        # Water heater optimization
│   └── Vacation/                 # Security & presence simulation
│       ├── Alarm.cs              # Security automation
│       └── LightsOnVacation.cs   # Presence simulation
├── Models/                       # Business logic & external APIs
│   ├── AppStateManager.cs        # Persistent state across restarts
│   ├── Utils.cs                  # Utility functions
│   ├── Battery/                  # Battery management models
│   │   ├── BatteryChargeType.cs
│   │   ├── BatteryOptions.cs
│   │   ├── ChargingPeriod.cs
│   │   ├── ChargingSchema.cs
│   │   └── SAJPowerBatteryApi.cs # SAJ API integration
│   ├── EnergyPrices/            # Price calculation helpers
│   │   ├── IPriceHelper.cs
│   │   ├── PriceHelper.cs
│   │   └── ElectricityPriceInfo.cs
│   └── Enums/                   # Enumerations
│       └── Level.cs
├── NetDaemonCodegen/            # HA metadata for code generation
│   ├── EntityMetaData.json
│   └── ServicesMetaData.json
├── docs/                        # Documentation
│   ├── BatteryStrategy.md       # Detailed battery strategy
│   └── BatteryQuickReference.md # Quick reference guide
├── Properties/                  # Build configuration
│   └── PublishProfiles/
├── appsettings.json            # Production configuration
├── appsettings.Development.json # Development configuration
├── Program.cs                  # Application entry point
├── Dockerfile                  # Container deployment
└── README.md                   # This file
```

---

## ⚙️ Configuration Guide

### Battery Options (appsettings.json)

```json
{
  "BatteryOptions": {
    "EnableDaySpecificScheduling": true,
    "SimulationMode": false,
    "DefaultChargePowerW": 8000,
    "DefaultDischargePowerW": 8000,
    "MaxInverterPowerW": 8000,
    "BatteryCapacityWh": 30000,
    "MorningSocThresholdPercent": 40.0,
    "TargetSocPercent": 98.0,
    "ChargingWindowHours": 3.0,
    "DischargeWindowHours": 1.0
  }
}
```

### Home Assistant Requirements

**Required Entities:**
- `switch.ems` - Energy Management System control
- `sensor.battery_soc` - Battery State of Charge
- `input_text.battery_charge_schedule` - Charge schedule display
- `input_text.battery_discharge_schedule` - Discharge schedule display
- `input_text.battery_management_mode` - Current battery mode

**Required Integrations:**
- Energy price data (Nordpool, Tibber, etc.)
- SAJ Power battery system integration
- NetDaemon add-on or custom installation

---

## 🚀 Deployment Options

### Option 1: Home Assistant Add-on (Recommended)

1. Install NetDaemon add-on from Home Assistant Community Store
2. Configure with your repository URL
3. Set environment variables in add-on configuration

### Option 2: Docker Container

```bash
docker build -t netdaemonapps .
docker run -d --name netdaemonapps \
  -v /path/to/config:/app/config \
  netdaemonapps
```

### Option 3: Standalone Service

```powershell
# Build and publish
dotnet publish -c Release -o ./publish

# Deploy to your preferred host
# Configure as Windows Service or systemd service
```

---

## 🔧 Development Workflow

### Essential Commands

```powershell
# Restore tools and generate HA types
dotnet tool restore
dotnet tool run nd-codegen

# Build and test
dotnet build
dotnet run --environment Development

# Deploy to production
.\publish.ps1
```

### Code Quality Guidelines

- **Method Extraction**: Break down large methods (>50 lines) into focused helpers
- **Logging Standards**: Use centralized `LogStatus()` method for all user-facing messages
- **Error Handling**: Implement graceful fallbacks and comprehensive retry logic
- **State Management**: Persist important data using `AppStateManager`
- **Configuration**: Use strongly-typed options classes for all settings

---

## 📊 Monitoring & Observability

### Home Assistant Dashboard Entities

Monitor your system through these entities:

- **Battery Management**:
  - `input_text.battery_charge_schedule` - Current charge periods
  - `input_text.battery_discharge_schedule` - Current discharge periods
  - `input_text.battery_management_mode` - SAJ battery mode status

- **Energy Monitoring**:
  - `sensor.battery_soc` - Current battery level
  - `switch.ems` - EMS system status

### Log Analysis

- **NetDaemon Logs**: Monitor for scheduling decisions and API interactions
- **Home Assistant Logs**: Check entity state changes and integration status
- **SAJ Power Logs**: Review battery system responses and mode changes

---

## 🆘 Need Help?

- **NetDaemon Documentation**: [netdaemon.xyz](https://netdaemon.xyz/)
- **Home Assistant Community**: [community.home-assistant.io](https://community.home-assistant.io)
- **SAJ Power Support**: Check official SAJ Power documentation for API details
- **Repository Issues**: Use GitHub Issues for bug reports and feature requests

---

## 🎯 Roadmap & Future Enhancements

- **Weather Integration**: Factor in solar production forecasts
- **Grid Export Optimization**: Maximize solar export during peak prices
- **Multi-Battery Support**: Coordinate multiple battery systems
- **Machine Learning**: Predict optimal charging patterns based on usage history
- **Mobile Notifications**: Real-time alerts for important energy events

---

**Enjoy a smarter, more automated home with NetDaemonApps!** 🏠⚡
