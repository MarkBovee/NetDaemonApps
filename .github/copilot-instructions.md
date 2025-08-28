# NetDaemonApps - AI Coding Instructions

## Project Overview
NetDaemonApps is a .NET-based Home Assistant automation daemon using the NetDaemon framework. It provides smart home automations for energy management, vacation security, and appliance control.

## General Guidelines

- Always follow the formatting and styling rules defined in `.editorconfig` for all files and languages in this repository
- Use language- and framework-specific best practices for code structure, error handling, naming, and security
- Prefer explicit over implicit code, avoid magic numbers/strings, and keep code readable and maintainable

---

## 📋 Progress Tracking & Documentation (Required)

### Development Session Management
When working on development tasks, especially complex implementations or multi-step processes:

1. **Always check for existing progress**: Start by checking if the `.github/copilot-progress.md` file exists. If not, create it. Then read it to understand any ongoing work or previous session context.

2. **Maintain progress documentation**: Update the progress file throughout your session with:
   - Current status and completed steps
   - Technical decisions and implementation details
   - Issues encountered and solutions applied
   - Next steps and remaining work

3. **Session completion**: When completing a task or major milestone:
   - Add a "Task Completed Successfully" section with comprehensive summary
   - Document what was implemented, tested, and verified
   - Include key features delivered and technical approach
   - Note any follow-up actions needed for production

4. **Start of a new task (housekeeping)**:
   - If the previous task was completed successfully and the new task is unrelated, reset `.github/copilot-progress.md` so it contains only the new task’s progress. Do not keep prior task logs in this file.
   - If the new task is a direct continuation of the previous one, keep the same task entry and append progress under the existing header.
   - Optionally archive prior completed task notes elsewhere (e.g., a dated entry in a separate document) if historical context is needed, but keep `copilot-progress.md` focused on a single active task.

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

## Code Quality & Refactoring Patterns

### Method Extraction Principles
When developing or refactoring automation apps, follow these established patterns for maintainable code:

**Extract Duplicate Logic**: 
- Identify repeated code patterns across methods (>10 lines of similar logic)
- Extract into focused helper methods with single responsibility
- Examples: EMS validation, period creation, retry scheduling patterns

**Period Creation Patterns**:
```csharp
// Create focused factory methods for common data structures
private ChargingPeriod CreateMorningDischargePeriod(TimeSpan startTime, double durationHours = 1.0)
{
    return new ChargingPeriod
    {
        StartTime = startTime,
        EndTime = startTime.Add(TimeSpan.FromHours(durationHours)),
        ChargeType = BatteryChargeType.Discharge,
        PowerInWatts = Math.Min(_options.DefaultDischargePowerW, _options.MaxInverterPowerW)
    };
}
```

**Async Validation Patterns**:
```csharp
// Extract complex validation logic into reusable async methods
private async Task<bool> ValidateAndTurnOffEmsAsync(string context, ChargingPeriod? period = null)
{
    // Centralized validation logic with consistent error handling
    // Returns bool for retry logic integration
}
```

### Method Size Guidelines
- **Target**: Methods should be <50 lines, ideally <30 lines
- **Large Methods (>75 lines)**: Break into logical sections using helper methods
- **Complex Logic**: Extract into well-named private methods with clear parameters
- **Maintain**: Existing error handling, logging, and async patterns when refactoring

### Refactoring Workflow
1. **Identify**: Look for duplicate code patterns and overly complex methods
2. **Extract**: Create focused helper methods with descriptive names
3. **Replace**: Update original methods to use extracted helpers
4. **Verify**: Ensure builds pass and no behavioral changes occur
5. **Document**: Update copilot-progress.md with refactoring details

### Code Structure Best Practices
- **Region Organization**: Group related methods in logical regions (EMS Management, Schedule Creation, etc.)
- **Naming Conventions**: Use descriptive method names that indicate purpose and return type
- **Parameter Design**: Prefer explicit parameters over complex object passing
- **Error Handling**: Maintain consistent exception handling and logging patterns
- **Documentation**: Add XML documentation for public and complex private methods

### Logging Best Practices
- **Centralized Logging**: All logging should go through the `LogStatus` method, never direct `_logger` calls
- **Dual-Message Pattern**: Use `LogStatus(dashboardMessage, detailMessage?)` for user-friendly dashboard messages with optional technical details
- **Dashboard Messages**: Keep clean, concise, and user-relevant (e.g., "Schedule applied", "EMS Mode detected")
- **Detail Messages**: Include technical information, error details, retry information (e.g., "Applied 3 periods: charge 02:00-05:00, discharge 18:00-19:00")
- **Backward Compatibility**: Format string pattern still supported: `LogStatus("Message {0}", value)`

```csharp
// Preferred dual-message pattern
LogStatus("Schedule applied", "Applied 3 periods: charge 02:00-05:00, discharge 18:00-19:00, etc.");
LogStatus("EMS Mode detected", $"Retry scheduled at {retryTime.ToString("HH:mm")} due to battery in EMS mode");
LogStatus("Error executing discharge", $"Error executing tomorrow morning discharge: {ex.Message}");

// Clean dashboard message only
LogStatus("Battery monitoring started");

// Avoid direct logger calls
// DON'T: _logger.LogInformation("Schedule applied");
// DO: LogStatus("Schedule applied");
```

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

### Anti-Patterns to Avoid
- **Code Duplication**: Don't copy-paste similar logic across methods - extract into helpers
- **Magic Numbers**: Use configuration options instead of hardcoded values (e.g., `_options.MorningSocThresholdPercent`)
- **Overly Large Methods**: Break down methods >50 lines into focused, single-purpose functions
- **Manual Object Creation**: Use factory methods for complex data structures (ChargingPeriod, ChargingSchema)
- **Inconsistent Error Handling**: Maintain uniform patterns for exceptions, logging, and retry logic
- **Mixed Concerns**: Separate validation, creation, and application logic into distinct methods
- **Direct Logger Calls**: Never use `_logger` directly - always use `LogStatus` for centralized messaging
- **Verbose Dashboard Messages**: Avoid technical details in dashboard messages - use detail parameter instead
- **Silent Failures**: Always log errors and state changes, even if using minimal dashboard messages