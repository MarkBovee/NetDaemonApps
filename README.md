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
- **Energy/Battery**: Manages battery storage, charging schedules, and integrates with SAJ Power Battery systems.
- **Energy/WaterHeater**: Controls water heater operations for optimal energy savings.

### Vacation Mode
- **Vacation/Alarm**: Enhances home security when you’re away, automating alarms and notifications.
- **Vacation/LightsOnVacation**: Simulates presence by randomly turning on lights and lamps when it’s dark during vacation mode. Automatically discovers all lights and smart switches that are likely to be lamps, and excludes switches for appliances (like TVs, dishwashers, etc.). Lights change hourly for realism and turn off at night. Debug mode shows which entities are considered lights for easy verification.

### Utilities & Models
- **Models/**: Contains helper classes for managing app state, battery APIs, and price information.
- **Enums/**: Defines levels and other enums used throughout the apps.
- **Utils.cs**: Utility functions shared by multiple apps.

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
- When running in debug mode (with a debugger attached), the Vacation/LightsOnVacation app will log the list of entities it considers to be lights or lamps. This helps you verify and tune the filter so only real lamps are included.
- In production, only these entities will be controlled for vacation lighting.

---

## Need Help?
- For questions or help, check the [NetDaemon documentation](https://netdaemon.xyz/) or Home Assistant forums.
- Feel free to customize or extend the apps to fit your needs!

---

Enjoy a smarter, more automated home with NetDaemonApps!
