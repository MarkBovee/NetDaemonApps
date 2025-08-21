namespace NetDaemonApps.Apps.Energy
{
    using System.Diagnostics;
    using HomeAssistantGenerated;
    using NetDaemon.Extensions.Scheduler;
    using Helpers;

    /// <summary>
    /// Adjust the energy appliances schedule based on the power prices
    /// </summary>
    [NetDaemonApp]
    public class Battery
    {
        /// <summary>
        /// The logger
        /// </summary>
        private readonly ILogger<Battery> _logger;

        /// <summary>
        /// The entities
        /// </summary>
        private readonly Entities _entities;

        /// <summary>
        /// The away mode
        /// </summary>
        private readonly bool _awayMode;

        /// <summary>
        /// The price helper
        /// </summary>
        private readonly IPriceHelper _priceHelper;

        /// <summary>
        /// Initializes a new instance of the <see cref="Battery"/> class.
        /// </summary>
        /// <param name="ha">The ha</param>
        /// <param name="scheduler">The scheduler</param>
        /// <param name="logger">The logger</param>
        /// <param name="priceHelper">The price helper</param>
        public Battery(IHaContext ha, INetDaemonScheduler scheduler, ILogger<Battery> logger, IPriceHelper priceHelper)
        {
            _logger = logger;
            _entities = new Entities(ha);
            _priceHelper = priceHelper;

            _logger.LogInformation("Started battery energy program");

            // Set the away mode based on entity state
            _awayMode = _entities.Switch.OurHomeAwayMode.State == "on";

            if (Debugger.IsAttached)
            {
                // Run once
                SetBatterySchedule();
            }
            else
            {
                // Run every 5 minutes, disabled for now
                // scheduler.RunEvery(TimeSpan.FromMinutes(5), SetBatterySchedule);
            }
        }

        /// <summary>
        /// Sets the battery schedule using the external API.
        /// </summary>
        private void SetBatterySchedule()
        {
            // Create battery API client and authenticate
            var battery = new SaiPowerBatteryApi("MBovee", "fnq@tce8CTQ5kcm4cuw", "HST2083J2446E06861");

            // Set a single charge and discharge period as per requirements
            var scheduleParameters = battery.BuildBatteryScheduleParameters(
                chargeStart: "12:00",
                chargeEnd: "15:00",
                chargePower: 8000,
                dischargeStart: "20:00",
                dischargeEnd: "21:00",
                dischargePower: 8000
            );

            // This works, but is commented out to avoid actual API calls during debugging
            // var scheduled = battery.SaveBatteryScheduleAsync(scheduleParameters).Result;

            // Output the schedule value for verification
            Console.WriteLine($"Generated schedule: {scheduleParameters.Value}");
        }
    }
}
