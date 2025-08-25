# Debug Instructions for NetDaemonApps

## VS Code Debug Profiles

### 🔋 Battery Simulation Test
Use this profile to test the Battery app's charging schema logic without making real API calls.

**How to use:**
1. Press `F5` or go to Run and Debug (Ctrl+Shift+D)
2. Select "Battery Simulation Test" from the dropdown
3. Click the green play button or press F5

**What happens:**
- Battery app detects debugger is attached
- Runs comprehensive simulation in 3 steps:
  1. **Initial Schema**: Calculates and "applies" charging schema
  2. **No-Change Test**: Runs evaluation cycle, should skip API calls
  3. **Change Detection**: Modifies schema and tests change detection
- All API calls are simulated (no real battery commands sent)
- All Home Assistant entity updates are skipped
- Detailed logging shows the complete decision flow

**Expected output:**
```
=== Starting Debug Simulation ===
Step 1: Calculating initial charging schema (simulating first run)
Initial schema calculated with X periods:
  - Charge: 02:00-05:00 @ 8000W
  - Discharge: 17:00-18:00 @ 8000W
SIMULATION MODE: Skipping actual API call to SAJ Power Battery API
Step 2: Simulating 15-minute evaluation cycle...
Schema unchanged, skipping API call
Step 3: Simulating conditions that would trigger recalculation...
=== Debug Simulation Complete ===
```

### 🏠 Debug Apps
Standard debug configuration for all NetDaemon apps.

**Use this for:**
- General debugging of other apps
- Setting breakpoints in any app
- Normal development work

## Tips
- The Battery app automatically uses simulation mode when any debugger is attached
- Check the integrated terminal for detailed simulation logs
- State files are still created/updated during simulation for testing persistence
- All pricing and energy calculations use real data, only API calls are simulated
