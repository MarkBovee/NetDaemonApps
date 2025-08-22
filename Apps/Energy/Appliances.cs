namespace NetDaemonApps.Apps.Energy
{
    using System.Diagnostics;
    using HomeAssistantGenerated;

    using Models.EnergyPrices;

    using NetDaemon.Extensions.Scheduler;

    using Models.Enums;

    /// <summary>
    /// Adjust the energy appliances schedule based on the power prices
    /// </summary>
    [NetDaemonApp]
    public class Appliances
    {
        /// <summary>
        /// The logger
        /// </summary>
        private readonly ILogger<Appliances> _logger;

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
        /// Initializes a new instance of the <see cref="Appliances"/> class
        /// </summary>
        /// <param name="ha">The ha</param>
        /// <param name="scheduler">The scheduler</param>
        /// <param name="logger">The logger</param>
        /// <param name="priceHelper">The price helper</param>
        public Appliances(IHaContext ha, INetDaemonScheduler scheduler, ILogger<Appliances> logger, IPriceHelper priceHelper)
        {
            _logger = logger;
            _entities = new Entities(ha);
            _priceHelper = priceHelper;

            _logger.LogInformation("Started appliances energy program");

            // Set the away mode based on entity state
            _awayMode = _entities.Switch.OurHomeAwayMode.State == "on";

            if (Debugger.IsAttached)
            {
                // Run once
                SetAppliancesSchedule();
            }
            else
            {
                // Run every 5 minutes
                scheduler.RunEvery(TimeSpan.FromMinutes(1), SetAppliancesSchedule);
            }
        }

        /// <summary>
        /// Sets the appliance schedule
        /// </summary>
        private void SetAppliancesSchedule()
        {
            // Get the entities for the appliances
            var washingMachine = _entities.Switch.Wasmachine;
            var washingMachineCurrentPower = _entities.Sensor.WasmachineHuidigGebruik;
            var dryer = _entities.Switch.DrogerEnVriezer;
            var dryerCurrentPower = _entities.Sensor.DrogerEnVriezerHuidigGebruik;
            var dishwasher = _entities.Switch.Vaatwasser;
            var dishwasherCurrentPower = _entities.Sensor.VaatwasserHuidigGebruik;
            var garage = _entities.Switch.Garage;
            var garageCurrentPower = _entities.Sensor.GarageHuidigGebruik;

            // Check if the price is above the threshold or if the away mode is active
            if (_awayMode || _priceHelper.EnergyPriceLevel > Level.Medium)
            {
                // Check if the washing machine is on and if no program is running
                if (washingMachine?.State == "on" && washingMachineCurrentPower.State < 3)
                {
                    _logger.LogInformation("Washing machine disabled due to high power prices");
                    washingMachine.TurnOff();
                }

                // Check if the dryer is on
                if (dryer?.State == "on" && dryerCurrentPower.State < 3)
                {
                    _logger.LogInformation("Dryer disabled due to high power prices");
                    dryer.TurnOff();
                }

                // Check if the dishwasher is on
                if (dishwasher?.State == "on" && dishwasherCurrentPower.State < 3)
                {
                    _logger.LogInformation("Dishwasher disabled due to high power prices");
                    dishwasher.TurnOff();
                }

                // Check if the garage power is on
                if (garage?.State == "on" && garageCurrentPower.State < 3)
                {
                    _logger.LogInformation("Garage disabled due to high power prices");
                    garage.TurnOff();
                }
            }
            else
            {
                // Turn on the appliances if they are off
                if (washingMachine?.State == "off")
                {
                    _logger.LogInformation("Washing machine enabled again");
                    washingMachine.TurnOn();
                }

                if (dryer?.State == "off")
                {
                    _logger.LogInformation("Dryer enabled again");
                    dryer.TurnOn();
                }

                if (dishwasher?.State == "off")
                {
                    _logger.LogInformation("Dishwasher enabled again");
                    dishwasher.TurnOn();
                }

                if (garage?.State == "off")
                {
                    _logger.LogInformation("Garage enabled again");
                    garage.TurnOn();
                }
            }
        }
    }
}
