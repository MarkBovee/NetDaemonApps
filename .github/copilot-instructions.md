# NetDaemonApps - AI Coding Instructions

## Project Overview
NetDaemonApps is a .NET-based Home Assistant automation daemon using the NetDaemon framework. It provides smart home automations for energy management, vacation security, and appliance control.

## General Guidelines

- Always follow the formatting and styling rules defined in `.editorconfig` for all files and languages in this repository
- Use language- and framework-specific best practices for code structure, error handling, naming, and security
- Prefer explicit over implicit code, avoid magic numbers/strings, and keep code readable and maintainable

## Architecture & Key Patterns

### App Structure
- **Apps are NetDaemon classes**: Each automation app inherits from NetDaemon patterns and uses `[NetDaemonApp]` attribute
- **Dependency Injection**: Apps receive `IHaContext`, `INetDaemonScheduler`, `ILogger<T>`, and custom services via constructor injection
- **Generated HA Types**: Use `HomeAssistantGenerated.Entities` and `HomeAssistantGenerated.Services` for type-safe Home Assistant interaction

### Critical File Organization
```
Apps/                          # Main automation logic
├── GlobalUsings.cs           # Shared using statements for all apps  
├── Energy/                   # Energy management automations
└── Vacation/                 # Security & presence simulation
Models/                       # Business logic & external APIs
├── AppStateManager.cs        # Persistent state across restarts
├── EnergyPrices/            # Price calculation helpers
└── Battery/                 # SAJ Power Battery API integration
NetDaemonCodegen/            # HA metadata for code generation
```

### Essential Development Workflows

**Code Generation (Always do this first)**:
```powershell
dotnet tool restore          # Install nd-codegen tool
dotnet tool run nd-codegen   # Generate HomeAssistantGenerated.cs
```
This creates type-safe C# classes from your Home Assistant entities/services. Run whenever HA config changes.

**State Management Pattern**:
```csharp
// Persist data across daemon restarts using AppStateManager
_lastRun = AppStateManager.GetState<DateTime?>(nameof(MyApp), "LastRun");
AppStateManager.SetState(nameof(MyApp), "LastRun", DateTime.Now);
```

**Standard App Constructor Pattern**:
```csharp
public MyApp(IHaContext ha, INetDaemonScheduler scheduler, ILogger<MyApp> logger)
{
    _entities = new Entities(ha);
    _services = new Services(ha);
    // Initialize app logic in constructor
}
```

### External Integration Patterns

**SAJ Power Battery API**: Uses custom authentication with signature-based tokens. See `Models/Battery/SAJPowerBatteryApi.cs` for the pattern of:
- Token management with expiration
- Password hashing for API authentication  
- HTTP client with custom headers

**Energy Price Integration**: `IPriceHelper` service provides electricity pricing logic. Registered as scoped service in `Program.cs`.

### Configuration & Deployment

**Settings**: Use `appsettings.json` for HA connection, `appsettings.Development.json` for debug overrides.

**Key Config Sections**:
- `HomeAssistant.*`: Connection details (host, port, token)
- `CodeGeneration.*`: Controls HomeAssistantGenerated.cs output
- `NetDaemon.*`: Framework configuration

**Deployment**: Project includes Dockerfile and publish profile. The publish target shows deployment path message for Home Assistant add-on.

### Debug & Verification Features
- Many apps include debug-only logging when `Debugger.IsAttached`
- `LightsOnVacation` logs discovered entities in debug mode
- `AppStateManager` provides centralized logging for state persistence

### Common Gotchas
- Always run code generation after HA entity changes
- State files persist in `/State` directory - check when debugging persistence issues
- External API credentials stored in project files (SAJ-token, hardcoded in Battery.cs)
- Apps auto-register via assembly scanning in `Program.cs`
