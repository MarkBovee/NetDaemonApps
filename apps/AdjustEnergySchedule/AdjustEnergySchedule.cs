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
using HomeAssistantGenerated.helpers;
using HomeAssistantGenerated.models.enums;
using Services = HomeAssistantGenerated.Services;

namespace NetDaemonApps.apps.AdjustPowerSchedule
{
    /// <summary>
    /// Adjust the energy appliances schedule based on the power prices
    /// TODO: Split to different apps
    /// Switched to Nordpool API sensor
    /// Template: {{ (0.021 + 0.102 + (current_price * 0.21)) | float }}
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
        /// The prices tomorrow
        /// </summary>
        private IDictionary<DateTime, double>? _pricesTomorrow;
        
        /// <summary>
        /// The heater on
        /// </summary>
        private bool _heaterOn;

        /// <summary>
        /// The heating time
        /// </summary>
        private DateTime _heatingTime;

        /// <summary>
        /// Set the threshold for the price, above this value the appliances will be disabled
        /// </summary>
        private double _priceThreshold;
        
        /// <summary>
        /// The current price
        /// </summary>
        private double? _currentPrice;

        /// <summary>
        /// The energy production
        /// </summary>
        private Level _energyProduction;

        /// <summary>
        /// The target temperature for the water heater
        /// </summary>
        private int _targetTemperature;
        
        /// <summary>
        /// The wait cycles
        /// </summary>
        private int _waitCycles;

        /// <summary>
        /// The away mode
        /// </summary>
        private bool _awayMode;

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
            
            _logger.LogInformation("Started Energy Schedule Assistant program");
            
            if (Debugger.IsAttached)
            {
                // Run once
                RunChecks();
            }
            else
            {
                // Application started
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

            // Get the price threshold
            SetPropertyValues();

            // Set the heating schedule for the heat pump
            SetWaterTemperature();

            // Disable the dishwasher, the washing machine and the dryer if the price is too high
            SetAppliancesSchedule();
            
            // Set the heating schedule for the heating circuit
            SetHeatingTemperature();

            // Set the car charging schedule
        }
        
        /// <summary>
        /// Sets the property values
        /// </summary>
        private void SetPropertyValues()
        {
            _priceThreshold = GetPriceThreshold();
            _currentPrice = GetCurrentPrice();
            
            // Set the energy production level
            _energyProduction = _entities.Sensor.ElectricityMeterPowerProduction.State switch
            {
                // Check if the energy production is above the threshold
                < 0.1 => Level.None,
                > 0.1 and <= 1 => Level.Low,
                > 1 and <= 2 => Level.Medium,
                > 2 and <= 3 => Level.High,
                _ => Level.Maximum
            };
            
            // Set the away mode
            _awayMode = _entities.Switch.OurHomeAwayMode.State == "on";
        }

        /// <summary>
        /// Gets the price threshold
        /// </summary>
        /// <returns>The double</returns>
        private double GetPriceThreshold()
        {
            if (_entities.Sensor.NordpoolKwhNlEur310021.Attributes == null) return 0;
            
            var avgPrice = _entities.Sensor.NordpoolKwhNlEur310021.Attributes.Average;
            double priceThreshold;
            const double fallbackPrice = 0.28;

            if (avgPrice != null)
            {
                // Set the price to the average price
                priceThreshold = avgPrice.Value;

                // Check if the price is below the fallback price
                if (priceThreshold < fallbackPrice)
                {
                    priceThreshold = fallbackPrice;
                }
            }
            else
            {
                // Set the price to a fallback default value
                priceThreshold = fallbackPrice;
            }

            // Get the current price and price threshold
            return Math.Round(priceThreshold, 2);
        }

        /// <summary>
        /// Sets the appliance schedule
        /// </summary>
        private void SetAppliancesSchedule()
        {
            // Get the entities for the appliances
            var washingMachine = _entities.Switch.Wasmachine;
            var washingMachineCurrentPower = _entities.Sensor.WasmachineHuidigGebruik;

            var dryer = _entities.Switch.Droger;
            var dryerCurrentPower = _entities.Sensor.DrogerHuidigGebruik;

            var dishwasher = _entities.Switch.Vaatwasser;
            var dishwasherCurrentPower = _entities.Sensor.VaatwasserHuidigGebruik;

            var garage = _entities.Switch.Garage;
            
            // Check if the price is above the threshold and there is no energy production
            if (_energyProduction < Level.Low && _currentPrice > _priceThreshold)
            {
                // Check if the washing machine is on and if no program is running
                if (washingMachine?.State == "on" && washingMachineCurrentPower.State < 5)
                {
                    _logger.LogInformation("Washing machine disabled due to high power prices");
                    washingMachine.TurnOff();
                }

                // Check if the dryer is on
                if (dryer?.State == "on" && dryerCurrentPower.State < 5)
                {
                    _logger.LogInformation("Dryer disabled due to high power prices");
                    dryer.TurnOff();
                }

                // Check if the dishwasher is on
                if (dishwasher?.State == "on" && dishwasherCurrentPower.State < 5)
                {
                    _logger.LogInformation("Dishwasher disabled due to high power prices");
                    dishwasher.TurnOff();
                }

                // Check if the garage power is on
                if (garage?.State == "on")
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
                
                if (garage?.State == "off" && _energyProduction == Level.Maximum)
                {
                    _logger.LogInformation("Garage enabled again");
                    garage.TurnOn();
                }
            }
        }

        /// <summary>
        /// Sets the heating temperature
        /// </summary>
        private void SetHeatingTemperature()
        {
            var climate = _entities.Climate.OurHomeZoneThuisCircuit0Climate;
            var curve = _entities.Number.OurHomeCircuit0HeatingCurve;
            var programType = _awayMode ? "away" : "none";
            var heatingTemperature = 20;
            var targetCurve = 0.3;

            switch (_energyProduction)
            {
                case Level.High:
                    targetCurve = 0.4;
                    break;
                case Level.Maximum:
                    // If we produce a lot of energy, set the temperature to 21 degrees
                    programType = "boost";
                    heatingTemperature = 21;
                    targetCurve = 0.4;
                    break;
            }
            
            // If prices are below zero, set the temperature to max!
            if (_currentPrice <= 0.1)
            {
                programType = "boost";
                heatingTemperature = 23;
                targetCurve = 0.5;
            }
            
            // Set the heating curve
            if (!Utils.AreDoublesEqual(curve.State,targetCurve))
            {
                curve.SetValue(new NumberSetValueParameters
                {
                    Value = targetCurve.ToString(CultureInfo.InvariantCulture)
                });
            }

            // Set the heating program and temperature when the program is different or the temperature is different
            if ((climate.Attributes?.PresetMode == "boost" && programType != "boost") || (programType == "boost" && (climate.Attributes?.PresetMode != "boost" || !Utils.AreDoublesEqual(climate.Attributes?.TargetTempLow, heatingTemperature))))
            {
                var msg = $"Setting heating program to {programType} with temperature {heatingTemperature}";
                
                _logger.LogInformation(msg);
                DisplayMessage(msg);
                
                climate.SetPresetMode(new ClimateSetPresetModeParameters
                {
                    PresetMode = programType
                });
            
                if (programType == "boost")
                {
                    climate.SetTemperature(new ClimateSetTemperatureParameters
                    {
                        TargetTempLow = heatingTemperature,
                        TargetTempHigh = 23
                    });
                }
            }
        }
        
        /// <summary>
        /// Sets the water temperature for the heat pump
        /// </summary>
        private void SetWaterTemperature()
        {
            // Get the heat pump water entities
            var heatingWater = _entities.WaterHeater.OurHomeDomesticHotWater0;
            
            // Check for price data
            if (_pricesToday == null || _pricesTomorrow == null)
            {
                return;
            }
            
            // Check what type of program should be used
            var timeStamp = DateTime.Now;
            var useNightProgram = timeStamp.Hour < 6;
            var useLegionellaProtection = useNightProgram == false && timeStamp.DayOfWeek == DayOfWeek.Saturday;
            var programType = _awayMode ? "Away" : useNightProgram ? "Night" : useLegionellaProtection ? "Legionella Protection" : "Heating";
            
            // Check bath mode and turn off the bath mode if the water temperature is above 50 degrees
            var bathMode = _entities.InputBoolean.Bath.State == "on";
            if (bathMode)
            {
                if (heatingWater.Attributes?.CurrentTemperature > 50)
                {
                    _entities.InputBoolean.Bath.TurnOff();
                }
            }

            // Set the start and end time for the heating period
            var lowestNightPrice = _pricesToday.Where(p => p.Key.TimeOfDay < TimeSpan.FromHours(6)).OrderBy(p => p.Value).ThenBy(p => p.Key).FirstOrDefault();
            var lowestDayPrice = _pricesToday.Where(p => p.Key.TimeOfDay > TimeSpan.FromHours(8)).OrderBy(p => p.Value).ThenBy(p => p.Key).FirstOrDefault();
            var nextNightPrice = _pricesTomorrow.Where(p => p.Key.TimeOfDay < TimeSpan.FromHours(6)).OrderBy(p => p.Value).ThenBy(p => p.Key).FirstOrDefault();
            
            var startTime = useNightProgram ? lowestNightPrice.Key : lowestDayPrice.Key;

            // Check if the value 1 hour before the start time is lower than 1 hour after the start time
            if (useLegionellaProtection && startTime.Hour is > 0 and < 23 && _pricesToday[startTime.AddHours(-1)] < _pricesToday[startTime.AddHours(1)])
            {
                startTime = startTime.AddHours(-1);
            }

            // Set the end time 3 hours after the start time
            var endTime = startTime.AddHours(3);

            // Set the temperature values
            var programTemperature = 35;
            var heatingTemperature = 35;
            var idleTemperature = 35;
            var currentTemperature = 35;
            var currentTargetTemperature = 35;
            
            if (heatingWater.Attributes is { Temperature: not null })
            {
                currentTemperature = (int)heatingWater.Attributes.CurrentTemperature!;
                currentTargetTemperature = (int)heatingWater.Attributes.Temperature!;
            }
            
            var idleText = startTime >= timeStamp ? $"{programType} program planned at: {startTime:HH:mm}" : "Idle";

            // Set the temperature values for the specific heating program
            if (_awayMode)
            {
                // Set the temperature to 60 degrees for the away mode for legionella protection
                if (useLegionellaProtection && useNightProgram == false)
                {
                    programTemperature = _currentPrice < 0.2 ? 66 : 60;
                }
            }
            else
            {
                // Set the heating temperature
                heatingTemperature = _energyProduction switch
                {
                    Level.Maximum => 58,
                    Level.High => 58,
                    Level.Medium => _currentPrice < _priceThreshold ? 50: 35,
                    _ => bathMode ? 58 : 35
                };
                
                // Set the program temperature value based on the heating program
                if (useNightProgram)
                {
                    // Set the temperature to 56 degrees for the night program if the night price is lower than the day price 
                    programTemperature = lowestNightPrice.Value < lowestDayPrice.Value ? 56 : 52;
                }
                else if (useLegionellaProtection)
                {
                    programTemperature = 62;
                }
                else
                {
                    programTemperature = _energyProduction switch
                    {
                        Level.Maximum => _currentPrice < 0.2 ? 66 : 58, 
                        Level.High => 58,
                        Level.Medium => 58,
                        _ => 50
                    };
                    
                    // Check if the next price for tomorrow is lower than the current price
                    if (nextNightPrice.Value > 0 && nextNightPrice.Value < _currentPrice && _energyProduction < Level.Medium)
                    {
                        // if so, set the temperature to idle and wait for the next cycle
                        programTemperature = heatingTemperature;
                    }
                }
            }

            // If prices are very low, set the temperature to max!
            switch (_currentPrice)
            {
                case <= 0.12:
                    programTemperature = 70;
                    heatingTemperature = 70;
                    break;
                case <= 0.15:
                    programTemperature = 66;
                    heatingTemperature = 66;
                    break;
            }

            // Set the water heating temperature
            try
            {
                // Check if the heater is off and the current timestamp is within the schedule  
                if (timeStamp.TimeOfDay >= startTime.TimeOfDay && timeStamp <= endTime)
                {
                    // Started program
                    if (programTemperature <= _targetTemperature && _heaterOn) return;
                    
                    _targetTemperature = programTemperature;
                    _heaterOn = true;
                    
                    heatingWater.SetOperationMode("Manual");
                    heatingWater.SetTemperature(_targetTemperature);
                    
                    DisplayMessage(currentTemperature < _targetTemperature ? $"{programType} program from: {startTime:HH:mm} to: {endTime:HH:mm}" : idleText);
                }
                else
                {
                    // Set the heater heating temperature if no program is running
                    if (_waitCycles > 0)
                    {
                        _waitCycles--;
                        
                        DisplayMessage(currentTemperature < _targetTemperature ? $"Heating to {_targetTemperature} [{_waitCycles}]" : idleText);
                    }
                    else
                    {
                        // Set the heater to heating temperature
                        _targetTemperature = heatingTemperature;
                        _waitCycles = 10;
                        _heaterOn = false;

                        if (currentTargetTemperature != _targetTemperature)
                        {
                            heatingWater.SetOperationMode("Manual");
                            heatingWater.SetTemperature(_targetTemperature);    
                        }

                        if (_targetTemperature > idleTemperature)
                        {
                            DisplayMessage(currentTemperature < _targetTemperature ? $"Heating to {_targetTemperature} [{_waitCycles}]" : idleText);
                        }
                        else
                        {
                            DisplayMessage(idleText);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to set the heating temperature");

                // Reset the values
                _targetTemperature = 0;
                _waitCycles = 0;
                _heaterOn = false;
            }
        }

        /// <summary>
        /// Displays the message using the specified message
        /// </summary>
        /// <param name="message">The message</param>
        private void DisplayMessage(string message)
        {
            _entities.InputText.HeatingScheduleStatus.SetValue(message);
        }

        /// <summary>
        /// Gets the current price
        /// </summary>
        /// <returns>The current price</returns>
        private double? GetCurrentPrice()
        {
            // Check if the prices are available
            if (_pricesToday == null)
            {
                return null;
            }

            // Get the current timestamp
            var timeStamp = DateTime.Now;

            // Get the current price
            var currentPrice = Math.Round(_pricesToday.FirstOrDefault(p => p.Key.Hour == timeStamp.Hour).Value, 2);

            return currentPrice;
        }

        /// <summary>
        /// Gets the prices
        /// </summary>
        private void GetPrices()
        {
            // Check if the prices are already loaded
            if (_pricesToday != null && _pricesToday.First().Key.DayOfWeek == DateTime.Today.DayOfWeek)
            {
                return;
            }

            // Read power prices for today
            var powerPrices = _ha.Entity("sensor.nordpool_kwh_nl_eur_3_10_021");
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

            // Check if the json is null
            if (json == null || json == "null")
            {
                // Use the fallback data source
                _logger.LogWarning("Power prices sensor has no data, using the fallback data source");

                LoadFallbackData().Wait();
            }
            else
            {
                AverageElectricityPrice? averageElectricityPrice = null;

                try
                {
                    averageElectricityPrice = JsonSerializer.Deserialize<AverageElectricityPrice>(json);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to deserialize power prices sensor data");
                }

                // Check if the price data is correct
                if (averageElectricityPrice == null || averageElectricityPrice.PricesToday.First().GetTimeValue().Date < DateTime.Today.Date)
                {
                    // Use the fallback data source
                    _logger.LogWarning("Power prices sensor has no new data, using the fallback data source");

                    LoadFallbackData().Wait();
                }
                else
                {
                    // Set the prices for today and tomorrow
                    _pricesToday = new Dictionary<DateTime, double>();
                    
                    foreach (var price in averageElectricityPrice.PricesToday)
                    {
                        _pricesToday[price.GetTimeValue()] = price.Price;
                    }
                    
                    _pricesTomorrow = new Dictionary<DateTime, double>();
                    
                    foreach (var price in averageElectricityPrice.PricesTomorrow)
                    {
                        _pricesTomorrow[price.GetTimeValue()] = price.Price;
                    }
                }
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
            if (html == null) return null;
            
            HtmlDocument document = new();
            document.LoadHtml(html);

            // Extract the table with the price information
            var nodes = document.DocumentNode.SelectNodes("//div[@class='row boxinner']");

            if (nodes == null) return null;
                
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
            _pricesTomorrow = pricesTomorrow;

            return null;
        }
    }
}