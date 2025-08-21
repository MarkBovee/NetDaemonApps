namespace NetDaemonApps.Apps.Energy
{
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;

    using HomeAssistantGenerated;

    using Models;
    using Models.Battery;
    using Models.EnergyPrices;

    using NetDaemon.Extensions.Scheduler;

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
        /// The last applied schedule
        /// </summary>
        private DateTime? _lastAppliedSchedule;

        /// <summary>
        /// The sai power battery api
        /// </summary>
        private readonly SAJPowerBatteryApi _saiPowerBatteryApi;

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

            // Set the SAJ Power Battery API with your credentials
            _saiPowerBatteryApi = new SAJPowerBatteryApi("MBovee", "fnq@tce8CTQ5kcm4cuw", "HST2083J2446E06861");

            // Check if the token is valid
            _saiPowerBatteryApi.IsTokenValid(true);

            // Load last applied schedule from the state file
            _lastAppliedSchedule = AppStateManager.GetState<DateTime?>(nameof(Battery), "LastAppliedSchedule");

            if (Debugger.IsAttached)
            {
                // Run once
                SetBatterySchedule();
            }
            else
            {
                // Run every 5 minutes
                scheduler.RunEvery(TimeSpan.FromMinutes(5), SetBatterySchedule);
            }
        }

        /// <summary>
        /// Sets the battery schedule using the external API.
        /// </summary>
        private void SetBatterySchedule()
        {
            // Check if we applied the schedule today
            if (_lastAppliedSchedule != null && _lastAppliedSchedule.Value.Date == DateTime.Now.Date)
            {
                return;
            }

            var pricesToday = _priceHelper.PricesToday;
            if (pricesToday == null || pricesToday.Count < 3)
            {
                _logger.LogWarning("Not enough price data to set battery schedule.");
                return;
            }

            // Get charge and discharge timeslots
            var (chargeStart, chargeEnd) = GetLowestPriceTimeslot(pricesToday, 3);
            var (dischargeStart, dischargeEnd) = GetHighestPriceTimeslot(pricesToday, 1);

            // Set charge/discharge periods using calculated times
            var scheduleParameters = _saiPowerBatteryApi.BuildBatteryScheduleParameters(
                chargeStart: chargeStart.ToString("HH:mm"),
                chargeEnd: chargeEnd.ToString("HH:mm"),
                chargePower: 8000,
                dischargeStart: dischargeStart.ToString("HH:mm"),
                dischargeEnd: dischargeEnd.ToString("HH:mm"),
                dischargePower: 8000
            );

            // Apply the schedule to the battery
            var saved = _saiPowerBatteryApi.SaveBatteryScheduleAsync(scheduleParameters).Result;
            if (saved)
            {
                // Set the last applied schedule time to now
                _lastAppliedSchedule = DateTime.Now;
                AppStateManager.SetState(nameof(Battery), "LastAppliedSchedule", _lastAppliedSchedule);

                _logger.LogInformation("Battery schedule applied and saved.");
            }
            else
            {
                _logger.LogWarning("Failed to apply battery schedule.");
            }
        }

        /// <summary>
        /// Gets the lowest price timeslot using the specified prices
        /// </summary>
        /// <param name="prices">The prices</param>
        /// <param name="windowHours">The window hours</param>
        /// <returns>The date time start date time end</returns>
        private static (DateTime Start, DateTime End) GetLowestPriceTimeslot(IDictionary<DateTime, double> prices, int windowHours = 3)
        {
            var sortedPrices = prices.OrderBy(p => p.Key).ToList();
            var minSum = double.MaxValue;
            var start = sortedPrices[0].Key;
            var end = sortedPrices[windowHours - 1].Key;

            for (var i = 0; i <= sortedPrices.Count - windowHours; i++)
            {
                var sum = 0d;
                for (var j = 0; j < windowHours; j++)
                {
                    sum += sortedPrices[i + j].Value;
                }

                if (!(sum < minSum)) continue;

                minSum = sum;
                start = sortedPrices[i].Key;
                end = sortedPrices[i + windowHours - 1].Key.AddMinutes(59);
            }

            return (start, end);
        }

        /// <summary>
        /// Finds the timeslot with the highest total price over a specified window of hours.
        /// </summary>
        /// <param name="prices">Dictionary of prices by DateTime.</param>
        /// <param name="windowHours">Number of consecutive hours in the window.</param>
        /// <returns>Tuple with start and end DateTime of the highest price window.</returns>
        private static (DateTime Start, DateTime End) GetHighestPriceTimeslot(IDictionary<DateTime, double> prices, int windowHours = 1)
        {
            var sortedPrices = prices.OrderBy(p => p.Key).ToList();
            var maxSum = double.MinValue;
            var start = sortedPrices[0].Key;
            var end = sortedPrices[windowHours - 1].Key;

            for (var i = 0; i <= sortedPrices.Count - windowHours; i++)
            {
                var sum = 0d;
                for (var j = 0; j < windowHours; j++)
                {
                    sum += sortedPrices[i + j].Value;
                }

                if (!(sum > maxSum)) continue;

                maxSum = sum;
                start = sortedPrices[i].Key;
                end = sortedPrices[i + windowHours - 1].Key.AddMinutes(59);
            }

            return (start, end);
        }
    }
}
