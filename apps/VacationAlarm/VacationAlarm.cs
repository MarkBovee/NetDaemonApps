using HomeAssistantGenerated;
using NetDaemon.Extensions.Scheduler;
using NetDaemonApps.apps.AdjustPowerSchedule;
using System.Diagnostics;

namespace NetDaemonApps.apps.VacationAlarm
{
    /// <summary>
    /// The vacation alarm class
    /// </summary>
    [NetDaemonApp]
    public class VacationAlarm
    {
        /// <summary>
        /// The ha
        /// </summary>
        private readonly IHaContext _ha;

        /// <summary>
        /// The logger
        /// </summary>
        private readonly ILogger<AdjustEnergySchedule> _logger;

        /// <summary>
        /// The services
        /// </summary>
        private readonly Services _services;

        /// <summary>
        /// The entities
        /// </summary>
        private readonly Entities _entities;

        /// <summary>
        /// Initializes a new instance of the <see cref="VacationAlarm"/> class
        /// </summary>
        /// <param name="ha">The ha</param>
        /// <param name="scheduler">The scheduler</param>
        /// <param name="logger">The logger</param>
        public VacationAlarm(IHaContext ha, INetDaemonScheduler scheduler, ILogger<AdjustEnergySchedule> logger)
        {
            _ha = ha;
            _logger = logger;

            // Read the Home assistant services and entities
            _services = new Services(ha);
            _entities = new Entities(ha);

            if (Debugger.IsAttached)
            {
                // Run once
                SetAlarm();
            }
            else
            {
                // Application started
                _services.Logbook.Log("Vacation Alarm program", "Succesfully loaded");

                // Run every 1 minute
                scheduler.RunEvery(TimeSpan.FromMinutes(1), SetAlarm);
            }
        }

        /// <summary>
        /// Sets the alarm
        /// </summary>
        private void SetAlarm()
        {
            // Check if away mode is active
            var awayMode = _entities.Switch.OurHomeAwayMode.State == "on";

            // Start the away mode alarm
            if (awayMode)
            {
                // Check if the current time is between 00:00 and 06:00
                var now = DateTime.Now;

                if (now.Hour >= 0 && now.Hour < 6)
                {
                    // Check if the alarm is active
                    var alarmSet = _entities.AlarmControlPanel.EmmeloordAlarm.State != "disarmed";

                    if (!alarmSet)
                    {
                        // Turn on the alarm
                        _entities.AlarmControlPanel.EmmeloordAlarm.AlarmArmAway();
                    }
                }
                else
                {
                    // Turn off the alarm
                    _entities.AlarmControlPanel.EmmeloordAlarm.AlarmDisarm();
                }
            }
        }
    }
}
