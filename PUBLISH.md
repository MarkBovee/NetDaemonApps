# NetDaemonApps Publish Scripts

This directory contains convenient scripts to build and publish your NetDaemon apps to Home Assistant.

## 🚀 Quick Start

### Option 1: Double-click (Windows)
Simply double-click `publish.bat` to run a complete publish cycle.

### Option 2: PowerShell (Recommended)
```powershell
.\publish.ps1
```

### Option 3: Manual VS Code Task
Use the VS Code task: `Ctrl+Shift+P` → "Tasks: Run Task" → "publish to NetDaemon5"

## 📋 What the Script Does

1. **🔄 Code Generation**: Generates type-safe C# classes from Home Assistant entities
2. **🔨 Build**: Compiles the project in Release mode
3. **📦 Publish**: Deploys to `\\192.168.1.135\config\netdaemon5`
4. **✅ Verification**: Checks accessibility and provides next steps

## ⚙️ Script Options

### PowerShell Parameters
```powershell
# Skip code generation (if entities haven't changed)
.\publish.ps1 -SkipCodegen

# Skip build step (if already built)
.\publish.ps1 -SkipBuild

# Force publish even if warnings exist
.\publish.ps1 -Force

# Combine options
.\publish.ps1 -SkipCodegen -SkipBuild
```

## 🔧 Prerequisites

- **Home Assistant** running at `192.168.1.135`
- **NetDaemon5 add-on** installed and configured
- **Network access** to Home Assistant config directory
- **PowerShell** (Windows 10/11 built-in)

## 📝 Current Features Being Deployed

- **🔋 Battery Management**: SAJ Power battery automation with mode monitoring
- **⚡ Energy Optimization**: Smart charging based on electricity prices
- **🏠 Vacation Mode**: Security automation and presence simulation
- **🌡️ Water Heater**: Smart heating schedule optimization

## 🛠️ Troubleshooting

### Common Issues

**"Cannot access publish directory"**
- Ensure Home Assistant is running
- Check network connectivity: `ping 192.168.1.135`
- Verify NetDaemon5 add-on is installed

**"Code generation failed"**
- Check Home Assistant connection in `appsettings.json`
- Verify Home Assistant token is valid
- Ensure Home Assistant API is accessible

**"Build failed"**
- Check for compilation errors in VS Code
- Ensure all NuGet packages are restored
- Try cleaning: `dotnet clean` then republish

### Getting Help
1. Check VS Code Problems panel for errors
2. Review Home Assistant NetDaemon5 logs
3. Verify `appsettings.json` configuration

## 🎯 After Publishing

1. **Restart NetDaemon5** add-on in Home Assistant
2. **Check logs** in Home Assistant → Add-ons → NetDaemon5 → Log
3. **Verify entities** are updating (especially `input_text.battery_management_mode`)
4. **Monitor automations** are working as expected

## 📁 Related Files

- `publish.ps1` - Main PowerShell script
- `publish.bat` - Windows batch file wrapper  
- `Properties/PublishProfiles/NetDaemon5Profile.pubxml` - Publish configuration
- `.vscode/tasks.json` - VS Code build tasks
