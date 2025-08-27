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
   - Full 8kW power for 3 hours

3. **🌆 Evening Decision** (30 min before discharge)
   - Compares tonight vs tomorrow morning prices
   - Moves discharge to tomorrow if more profitable

### Key Benefits
- **Revenue Maximization**: Up to 3 discharge periods per day during optimal prices
- **Automatic EMS Management**: Prevents conflicts with Energy Management System
- **Smart Scheduling**: Adapts to price trends rather than fixed schedules
- **State Persistence**: Survives system restarts and maintains schedules

### Monitoring
- Check `input_text.battery_charge_schedule` and `input_text.battery_discharge_schedule` in Home Assistant
- Watch logs for decision points and price comparisons
- Monitor `switch.ems` state changes around battery periods

### Debug Mode
When running with debugger attached:
- All operations are simulated (no actual battery changes)
- Detailed logging of all decisions
- Safe for testing and development
