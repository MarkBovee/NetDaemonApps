using HomeAssistantGenerated;
using HtmlAgilityPack;
using NetDaemon.Extensions.Scheduler;
using NetDaemonApps.models.energy_prices;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace NetDaemonApps.apps.AdjustPowerSchedule
{
    /// <summary>
    /// Adjust the energy appliances schedule based on the power prices
    /// TODO: Split to different apps
    /// </summary>
    [NetDaemonApp]
    public class AdjustEnergySchedule
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
        /// The prices today
        /// </summary>
        private IDictionary<DateTime, double>? _pricesToday;

        /// <summary>
        /// The prices tommorow
        /// </summary>
        private IDictionary<DateTime, double>? _pricesTommorow;

        /// <summary>
        /// The is heater on indication
        /// </summary>
        private bool _isHeaterOn;

        /// <summary>
        /// The heating time
        /// </summary>
        private DateTime _heatingTime;

        /// <summary>
        /// Set the threshold for the price, above this value the appliances will be disabled
        /// </summary>
        private readonly double _priceThreshold = 0.280;

        /// <summary>
        /// Initializes a new instance of the <see cref="AdjustEnergySchedule"/> class
        /// </summary>
        /// <param name="ha">The ha</param>
        /// <param name="scheduler">The scheduler</param>
        /// <param name="logger">The logger</param>
        public AdjustEnergySchedule(IHaContext ha, INetDaemonScheduler scheduler, ILogger<AdjustEnergySchedule> logger)
        {
            _ha = ha;
            _logger = logger;

            // Read the Home assistant services and entities
            _services = new Services(ha);
            _entities = new Entities(ha);

            if (Debugger.IsAttached)
            {
                // Run once
                RunChecks();
            }
            else
            {
                // Application started
                _services.Logbook.Log("Energy schedule assistant", "Succesfully loaded");

                // Run every 5 minutes
                scheduler.RunEvery(TimeSpan.FromMinutes(5), RunChecks);
            }
        }

        /// <summary>
        /// Runs the checks
        /// </summary>
        private void RunChecks()
        {
            // Get the current power prices
            GetPrices();

            // Set the heating schedule for the heatpump
            SetHeatingSchedule();

            // Disable the dishwasher, the washing machine and the dryer if the price is too high
            SetAppliancesSchedule();

            // Set the car charging schedule
        }

        /// <summary>
        /// Sets the appliances schedule
        /// </summary>
        private void SetAppliancesSchedule()
        {
            // Get the entities for the appliances
            var washingMachine = _entities.Switch.Wasmachine;
            var washingMachineCurrentPower = _entities.Sensor.WasmachineCurrentPower;

            var dryer = _entities.Switch.Droger;
            var dryerCurrentPower = _entities.Sensor.DrogerCurrentPower;

            var dishwasher = _entities.Switch.Vaatwasser;
            var dishwasherCurrentPower = _entities.Sensor.VaatwasserCurrentPower;

            // Get the current solar energy production
            var energyProduction = _entities.Sensor.ElectricityMeterPowerProduction;
            var noEnergyProduction = energyProduction.State < 0.3;

            // Check if the prices are available
            if (_pricesToday == null)
            {
                return;
            }

            // Get the current timestamp
            var timeStamp = DateTime.Now;

            // Get the current price
            var currentPrice = _pricesToday.FirstOrDefault(p => p.Key.Hour == timeStamp.Hour).Value;

            // Check if the price is above the threshold and there is no energy production
            if (currentPrice > _priceThreshold && noEnergyProduction)
            {
                // Check if the washing machine is on and if no program is running
                if (washingMachine?.State == "on" && washingMachineCurrentPower.State < 5)
                {
                    _services.Logbook.Log("Energy schedule assistant", "Washing machine disabled due to high power prices");
                    washingMachine.TurnOff();
                }

                // Check if the dryer is on
                if (dryer?.State == "on" && dryerCurrentPower.State < 5)
                {
                    _services.Logbook.Log("Energy schedule assistant", "Dryer disabled due to high power prices");
                    dryer.TurnOff();
                }

                // Check if the dishwasher is on
                if (dishwasher?.State == "on" && dishwasherCurrentPower.State < 5)
                {
                    _services.Logbook.Log("Energy schedule assistant", "Dishwasher disabled due to high power prices");
                    dishwasher.TurnOff();
                }
            }
            else
            {
                // Turn on the appliances if they are off
                if (washingMachine?.State == "off")
                {
                    _services.Logbook.Log("Energy schedule assistant", "Washing machine  enabled again");
                    washingMachine.TurnOn();
                }

                if (dryer?.State == "off")
                {
                    _services.Logbook.Log("Energy schedule assistant", "Dryer enabled again");
                    dryer.TurnOn();
                }

                if (dishwasher?.State == "off")
                {
                    _services.Logbook.Log("Energy schedule assistant", "Dishwasher enabled again");
                    dishwasher.TurnOn();
                }
            }
        }

        /// <summary>
        /// Sets the heating schedule
        /// </summary>
        private void SetHeatingSchedule()
        {
            // Get the heat pump warm water heater entity
            var heater = _entities.WaterHeater.OurHomeDomesticHotWater0;
            if (heater == null)
            {
                _logger.LogWarning("Hot water heater not found");

                return;
            }

            // Check if the prices are available
            if (_pricesToday == null)
            {
                return;
            }

            // Get the current timestamp
            var timeStamp = DateTime.Now;

            // Get the current price
            var currentPrice = _pricesToday.FirstOrDefault(p => p.Key.Hour == timeStamp.Hour).Value;

            // Check if the what type of programe should be used
            var awayMode = _entities.Switch.OurHomeAwayMode.State == "on";
            var useNightProgram = timeStamp.Hour < 6;
            var useLegionellaProtection = useNightProgram == false && timeStamp.DayOfWeek == DayOfWeek.Saturday;
            var programType = awayMode ? "Away" : useNightProgram ? "Night" : useLegionellaProtection ? "Legionella Protection" : "Heating";

            // Set the start and end time for the heating period
            DateTime startTime;
            if (useNightProgram)
            {
                startTime = _pricesToday.Where(p => p.Key.TimeOfDay < TimeSpan.FromHours(6)).OrderBy(p => p.Value).ThenBy(p => p.Key).First().Key;
            }
            else
            {
                startTime = _pricesToday.Where(p => p.Key.TimeOfDay > TimeSpan.FromHours(8)).OrderBy(p => p.Value).ThenBy(p => p.Key).First().Key;
            }

            // Check if the value 1 hour before before the start time is lower than 1 hour after the start time
            if (useLegionellaProtection && startTime.Hour > 0 && _pricesToday[startTime.AddHours(-1)] < _pricesToday[startTime.AddHours(1)])
            {
                startTime = startTime.AddHours(-1);
            }

            // Set the end time 3 hours after the start time
            var endTime = startTime.AddHours(3);

            // Set the temperature values
            var temperatureHeat = 35;
            var temperatureIdle = 35;

            if (awayMode)
            {
                // Set the temperature to 60 degrees for the away mode for legionella protection
                if (useLegionellaProtection == true && useNightProgram == false)
                {
                    temperatureHeat = 60;
                }
            }
            else
            {
                temperatureHeat = useNightProgram ? 48 : useLegionellaProtection ? 64 : 56;
                temperatureIdle = currentPrice < _priceThreshold ? 40 : 35;
            }

            // If prices are below zero, set the temperature to max!
            if (currentPrice < 0)
            {
                temperatureHeat = 70;
                temperatureIdle = 70;
            }

            // Set notification for the start of the heating period
            if (_heatingTime != startTime)
            {
                _heatingTime = startTime;

                // Check if the start time is in the future
                if (_heatingTime >= timeStamp)
                {
                    _services.PersistentNotification.Create(message: $"Next {programType} program planned at: {startTime} ", title: "Energy schedule assistant");
                }
            }

            // Check if the heater is off and the current timestamp is within the schedule
            if (timeStamp.TimeOfDay >= startTime.TimeOfDay && timeStamp.TimeOfDay <= endTime.TimeOfDay)
            {
                if (_isHeaterOn == false)
                {
                    _services.Logbook.Log("Energy schedule assistant", $"Started {programType} program from: {startTime}, to: {endTime}");

                    try
                    {
                        heater.SetOperationMode("Manual");
                        heater.SetTemperature(temperatureHeat);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to set heating schedule");
                    }

                    _isHeaterOn = true;
                }
            }
            else
            {
                // Set the heater to idle temperature after the heating period
                if (_isHeaterOn || heater.Attributes?.Temperature != temperatureIdle)
                {
                    heater.SetTemperature(temperatureIdle);
                }

                _isHeaterOn = false;
            }
        }

        /// <summary>
        /// Gets the prices
        /// </summary>
        private void GetPrices()
        {
            // Check if the prices are already loaded
            if (_pricesToday != null && _pricesToday.First().Key.Date == DateTime.Today.Date)
            {
                return;
            }

            // Read power prices for today
            var powerPrices = _ha.Entity("sensor.energy_prices_average_electricity_price_today");
            if (powerPrices == null)
            {
                _logger.LogWarning("Power prices sensor not found");
                return;
            }

            var jsonAttributes = powerPrices.EntityState?.AttributesJson;
            if (jsonAttributes == null)
            {
                _logger.LogWarning("Power prices sensor has no attributes");
                return;
            }

            var json = jsonAttributes.Value.ToString();
            var averageElectricyPrice = JsonSerializer.Deserialize<AverageElectricityPrice>(json);

            // Check if the price data is correct
            if (averageElectricyPrice == null || averageElectricyPrice.PricesToday.First().GetTimeValue().Date < DateTime.Today.Date)
            {
                // Use the fallback data source
                _logger.LogWarning("Power prices sensor has no new data, using the fallback data sourc");

                LoadFallbackData().Wait();
            }
            else
            {
                _pricesToday = averageElectricyPrice.PricesToday.ToDictionary(p => p.GetTimeValue(), p => p.Price);
                _pricesTommorow = averageElectricyPrice.PricesTomorrow.ToDictionary(p => p.GetTimeValue(), p => p.Price);
            }
        }

        /// <summary>
        /// Loads the fallback data
        /// </summary>
        /// <returns>A task containing a dictionary of date time and double</returns>
        private async Task LoadFallbackData()
        {
            try
            {
                using HttpClient client = new();

                // Add a user agent header in case the requested URI contains a query.
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/4.0 (compatible; MSIE 6.0; Windows NT 5.2; .NET CLR 1.0.3705;)");

                var html = await client.GetStringAsync("https://jeroen.nl/dynamische-energie/prijzen/vandaag");

                ParseHtmlToPrices(html);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load fallback data");
            }
        }

        /// <summary>
        /// Parses the html to prices using the specified html
        /// </summary>
        /// <param name="html">The html</param>
        /// <returns>A dictionary of date time and double</returns>
        private Dictionary<DateTime, double>? ParseHtmlToPrices(string html)
        {
            var pricesToday = new Dictionary<DateTime, double>();
            var pricesTomorrow = new Dictionary<DateTime, double>();

            // Parse the html
            if (html != null)
            {
                HtmlDocument document = new();
                document.LoadHtml(html);

                // Extract the table with the price information
                var nodes = document.DocumentNode.SelectNodes("//div[@class='row boxinner']");

                if (nodes != null)
                {
                    foreach (var node in nodes)
                    {
                        // Load the html of the row
                        HtmlDocument nodeHtml = new();
                        nodeHtml.LoadHtml($"<html>{node.OuterHtml}</html>");

                        // Parse the time text
                        var timeText = nodeHtml.DocumentNode.SelectSingleNode("//div[@class='col-6 col-md-2']").InnerText.Trim();
                        var timeStart = timeText.Substring(0, 5);
                        var day = timeText.Substring(11);

                        // Parse the price text
                        HtmlDocument priceHtml = new();
                        priceHtml.LoadHtml($"<html>{nodeHtml.DocumentNode.SelectSingleNode("//div[@class='collapse']").OuterHtml}</html>");
                        var priceText = priceHtml.DocumentNode.SelectNodes("//strong").Last().InnerText.Trim();

                        if (day == "Vandaag" && double.TryParse(priceText, NumberStyles.Any, CultureInfo.InvariantCulture, out var priceToday))
                        {
                            var date = DateTime.ParseExact(timeStart, "HH:mm", CultureInfo.InvariantCulture);

                            pricesToday[date] = priceToday;
                        }

                        if (day == "Morgen" && double.TryParse(priceText, NumberStyles.Any, CultureInfo.InvariantCulture, out var priceTomorrow))
                        {
                            var date = DateTime.ParseExact(timeStart, "HH:mm", CultureInfo.InvariantCulture).AddDays(1);

                            pricesTomorrow[date] = priceTomorrow;
                        }
                    }

                    _pricesToday = pricesToday;
                    _pricesTommorow = pricesTomorrow;
                }
            }

            return null;
        }
    }
}
