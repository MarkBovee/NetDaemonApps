namespace NetDaemonApps.Apps.Vacation
{
    using System.Diagnostics;

    using Energy;

    using HomeAssistantGenerated;

    using NetDaemon.Extensions.Scheduler;

    /// <summary>
    /// The vacation alarm class
    /// </summary>
    [NetDaemonApp]
    public class Alarm
    {
        /// <summary>
        /// The ha
        /// </summary>
        private readonly IHaContext _ha;

        /// <summary>
        /// The logger
        /// </summary>
        private readonly ILogger<Appliances> _logger;

        /// <summary>
        /// The services
        /// </summary>
        private readonly Services _services;

        /// <summary>
        /// The entities
        /// </summary>
        private readonly Entities _entities;

        /// <summary>
        /// Initializes a new instance of the <see cref="Alarm"/> class
        /// </summary>
        /// <param name="ha">The ha</param>
        /// <param name="scheduler">The scheduler</param>
        /// <param name="logger">The logger</param>
        public Alarm(IHaContext ha, INetDaemonScheduler scheduler, ILogger<Appliances> logger)
        {
            _ha = ha;
            _logger = logger;

            // Read the Home assistant services and entities
            _services = new Services(ha);
            _entities = new Entities(ha);

            _logger.LogInformation("Started alarm program");

            if (Debugger.IsAttached)
            {
                // Run once
                //SetAlarm();
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
        /// Sets the alarm based on away mode and time of day.
        /// </summary>
        private void SetAlarm()
        {
            // Check if away mode is active
            var awayMode = _entities.Switch.OurHomeAwayMode.State == "on";

            // Start the away mode alarm if away mode is active
            if (awayMode)
            {
                // Get the current time
                var now = DateTime.Now;

                // If the current time is between 00:00 and 07:00, arm the alarm
                if (now.Hour >= 0 && now.Hour < 7)
                {
                    // Check if the alarm is already armed
                    var alarmSet = _entities.AlarmControlPanel.EmmeloordAlarm.State != "disarmed";

                    if (!alarmSet)
                    {
                        // Arm the alarm
                        _entities.AlarmControlPanel.EmmeloordAlarm.AlarmArmAway();
                    }
                }
                else
                {
                    // Disarm the alarm outside of night hours
                    _entities.AlarmControlPanel.EmmeloordAlarm.AlarmDisarm();
                }
            }
        }
    }
}
