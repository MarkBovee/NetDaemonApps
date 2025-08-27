# NetDaemonApps - AI Coding Instructions

## Project Overview
NetDaemonApps is a .NET-based Home Assistant automation daemon using the NetDaemon framework. It provides smart home automations for energy management, vacation security, and appliance control.

## General Guidelines

- Always follow the formatting and styling rules defined in `.editorconfig` for all files and languages in this repository
- Use language- and framework-specific best practices for code structure, error handling, naming, and security
- Prefer explicit over implicit code, avoid magic numbers/strings, and keep code readable and maintainable

---

## Development Session Management (Required)
When working on development tasks, especially complex implementations or multi-step processes:

1. **Always check for existing progress**: Start by reading the `.github/copilot-progress.md` file to understand any ongoing work or previous session context.

2. **Maintain progress documentation**: Update the progress file throughout your session with:
[    - Current status and completed steps
]()    - Technical decisions and implementation details
    - Issues encountered and solutions applied
    - Next steps and remaining work

3. **Session completion**: When completing a task or major milestone:
    - Add a "Task Completed Successfully" section with comprehensive summary
    - Document what was implemented, tested, and verified
    - Include key features delivered and technical approach
    - Note any follow-up actions needed for production

### Progress File Structure
The `.github/copilot-progress.md` should follow this pattern:
- **Header**: Task name, date, and completion status
- **Summary**: Brief overview of accomplishments (for completed tasks)
- **Detailed Steps**: Numbered list of completed work with checkmarks
- **Implementation Details**: Technical specifics, file changes, patterns used
- **Testing/Verification**: Test results, compilation status, validation steps
- **Next Steps**: Future work or production deployment notes

This approach ensures continuity between development sessions and provides clear documentation of progress for complex implementations.

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