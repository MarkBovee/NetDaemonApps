namespace NetDaemonApps.Models.EnergyPrices;

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

using HomeAssistantGenerated;

using HtmlAgilityPack;

using Enums;

using NetDaemon.Extensions.Scheduler;

/// <summary>
/// The price helper class
/// </summary>
/// Switched to Nordpool API sensor
/// Template: {{ (0.021 + 0.102 + (current_price * 0.21)) | float }}
public class PriceHelper : IPriceHelper
{
    /// <summary>
    /// The entities
    /// </summary>
    private readonly Entities _entities;

    /// <summary>
    /// The logger
    /// </summary>
    private readonly ILogger<PriceHelper> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PriceHelper"/> class
    /// </summary>
    /// <param name="ha">The ha</param>
    /// <param name="logger">The logger</param>
    /// <param name="scheduler">The scheduler</param>
    public PriceHelper(IHaContext ha, ILogger<PriceHelper> logger, INetDaemonScheduler scheduler)
    {
        _entities = new Entities(ha);
        _logger = logger;

        // Try to load prices and timestamp from state file first
        LoadPricesFromStateFile();

        // If prices are not loaded or are outdated, fetch new prices
        GetPrices();

        // And update the prices every hour
        scheduler.RunEvery(TimeSpan.FromHours(1), GetPrices);
    }

    /// <summary>
    /// The prices for today
    /// </summary>
    public IDictionary<DateTime, double>? PricesToday { get; private set; }

    /// <summary>
    /// The prices for tomorrow
    /// </summary>
    public IDictionary<DateTime, double>? PricesTomorrow { get; private set; }

    /// <summary>
    /// Set the threshold for the price, above this value the appliances will be disabled
    /// </summary>
    public double PriceThreshold { get => GetPriceThreshold(); }

    /// <summary>
    /// The current price
    /// </summary>
    public double? CurrentPrice { get => GetCurrentPrice(); }

    /// <summary>
    /// The energy price level, none is price of 0, highest is sky high
    /// </summary>
    public Level EnergyPriceLevel { get => GetEnergyPriceLevel(); }

    /// <summary>
    /// Gets the energy price level
    /// </summary>
    /// <returns>The energy price level</returns>
    private Level GetEnergyPriceLevel()
    {
        var energyPriceLevel = CurrentPrice switch
        {
            < 0 => Level.None,
            < 0.1 => Level.Low,
            < 0.35 => CurrentPrice < PriceThreshold ? Level.Medium : Level.High,
            < 0.45 => Level.High,
            _ => Level.Maximum
        };

        return energyPriceLevel;
    }

    /// <summary>
    /// The last time prices were updated
    /// </summary>
    public DateTime? LastPricesUpdate { get; private set; }

    /// <summary>
    /// Gets the price threshold from the Nordpool sensor.
    /// </summary>
    /// <returns>The price threshold as double.</returns>
    private double GetPriceThreshold()
    {
        if (_entities.Sensor.Epex.Attributes == null) return 0;

        var avgPrice = _entities.Sensor.AveragePrice.State;
        double priceThreshold;
        const double fallbackPrice = 0.28;

        if (avgPrice != null)
        {
            // Set the price to the average price
            // Check if the price is below the fallback price
            if (avgPrice.Value < fallbackPrice)
            {
                priceThreshold = fallbackPrice;
            }
            else
            {
                // Use the average price as threshold
                priceThreshold = avgPrice.Value;
            }
        }
        else
        {
            // Use fallback price if average is not available
            priceThreshold = fallbackPrice;
        }

        return priceThreshold;
    }

    /// <summary>
    /// Gets the current price.
    /// </summary>
    /// <returns>The current price for the current hour, or null if unavailable.</returns>
    private double? GetCurrentPrice()
    {
        if (PricesToday == null)
        {
            return null;
        }

        var timeStamp = DateTime.Now;
        var currentPrice = PricesToday.FirstOrDefault(p => p.Key.Hour == timeStamp.Hour).Value;

        return currentPrice;
    }

    /// <summary>
    /// Loads prices from the state file if available and up-to-date.
    /// </summary>
    private void LoadPricesFromStateFile()
    {
        var pricesTodayRaw = AppStateManager.GetState<IDictionary<string, double>>(nameof(PriceHelper), "PricesToday");
        var pricesTomorrowRaw = AppStateManager.GetState<IDictionary<string, double>>(nameof(PriceHelper), "PricesTomorrow");
        var lastUpdateRaw = AppStateManager.GetState<DateTime?>(nameof(PriceHelper), "LastPricesUpdate");

        PricesToday = pricesTodayRaw?.ToDictionary(kvp => DateTime.Parse(kvp.Key), kvp => kvp.Value) ?? new Dictionary<DateTime, double>();
        PricesTomorrow = pricesTomorrowRaw?.ToDictionary(kvp => DateTime.Parse(kvp.Key), kvp => kvp.Value) ?? new Dictionary<DateTime, double>();
        LastPricesUpdate = lastUpdateRaw;
    }

    /// <summary>
    /// Saves prices to the state file.
    /// </summary>
    private void SavePricesToStateFile()
    {
        if (PricesToday != null)
            AppStateManager.SetState(nameof(PriceHelper), "PricesToday", PricesToday);
        if (PricesTomorrow != null)
            AppStateManager.SetState(nameof(PriceHelper), "PricesTomorrow", PricesTomorrow);
        if (LastPricesUpdate != null)
            AppStateManager.SetState(nameof(PriceHelper), "LastPricesUpdate", LastPricesUpdate);
    }

    /// <summary>
    /// Gets the prices
    /// </summary>
    private void GetPrices()
    {
        // Check if we already have prices for today and if they are up-to-date
        if (LastPricesUpdate != null && LastPricesUpdate.Value.Hour == DateTime.Now.Hour) return;

        // Read power prices for today
        var powerPrices = _entities.Sensor.Epex;
        var jsonAttributes = powerPrices.EntityState?.AttributesJson;
        if (jsonAttributes == null)
        {
            _logger.LogWarning("Power prices sensor has no attributes");
            return;
        }

        // Check if the attributes contain the JSON data and use fallback if no data is available
        var json = jsonAttributes.Value.ToString();
        if (json is null or "null")
        {
            // Use the fallback data source
            _logger.LogWarning("Power prices sensor has no data, using the fallback data source");

            LoadFallbackData().Wait();
        }
        else
        {
            ElectricityPriceInfo? electricityPriceInfo = null;

            try
            {
                electricityPriceInfo = JsonSerializer.Deserialize<ElectricityPriceInfo>(json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deserialize power prices sensor data");
            }

            // Check if the price data is correct
            if (electricityPriceInfo == null || electricityPriceInfo.PricesToday.First().GetTimeValue().Date < DateTime.Today.Date)
            {
                // Use the fallback data source
                _logger.LogWarning("Power prices sensor has no new data, using the fallback data source");

                LoadFallbackData().Wait();
            }
            else
            {
                // Set the prices for today and tomorrow
                PricesToday = new Dictionary<DateTime, double>();

                foreach (var price in electricityPriceInfo.PricesToday)
                {
                    PricesToday[price.GetTimeValue()] = GetAllInclusivePrice(price.Price);
                }

                PricesTomorrow = new Dictionary<DateTime, double>();

                foreach (var price in electricityPriceInfo.PricesTomorrow)
                {
                    PricesTomorrow[price.GetTimeValue()] = GetAllInclusivePrice(price.Price);
                }
            }
        }

        // At the end of successful price fetch (after setting PricesToday/PricesTomorrow):
        LastPricesUpdate = DateTime.Now;
        SavePricesToStateFile();
    }

    /// <summary>
    /// Gets the all-inclusive price using the specified raw price
    /// </summary>
    /// <param name="rawPrice">The raw price</param>
    /// <returns>The double</returns>
    private static double GetAllInclusivePrice(double rawPrice)
    {
        const double surcharge = 0.0248;
        const double tax = 0.1228;
        const double vat = 1.21;

        var result = rawPrice * vat + surcharge + tax;

        return Math.Round(result, 4);
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
    private void ParseHtmlToPrices(string? html)
    {
        var pricesToday = new Dictionary<DateTime, double>();
        var pricesTomorrow = new Dictionary<DateTime, double>();

        // Parse the HTML
        if (html == null) return;

        HtmlDocument document = new();
        document.LoadHtml(html);

        // Extract the table with the price information
        var nodes = document.DocumentNode.SelectNodes("//div[@class='row boxinner']");

        if (nodes == null) return;

        foreach (var node in nodes)
        {
            // Load the HTML of the row
            HtmlDocument nodeHtml = new();
            nodeHtml.LoadHtml($"<html>{node.OuterHtml}</html>");

            // Parse the time text
            var timeText = nodeHtml.DocumentNode.SelectSingleNode("//div[@class='col-6 col-md-2']").InnerText.Trim();
            var timeStart = timeText[..5];
            var day = timeText[11..];

            // Parse the price text
            HtmlDocument priceHtml = new();
            priceHtml.LoadHtml($"<html>{nodeHtml.DocumentNode.SelectSingleNode("//div[@class='collapse']").OuterHtml}</html>");
            var priceText = priceHtml.DocumentNode.SelectNodes("//strong").Last().InnerText.Trim();

            if (day == "Vandaag" && double.TryParse(priceText, NumberStyles.Any, CultureInfo.InvariantCulture, out var priceToday))
            {
                // Parse time and ensure it's in Dutch timezone context
                var timeOnly = DateTime.ParseExact(timeStart, "HH:mm", CultureInfo.InvariantCulture);
                var dutchDateTime = DateTime.SpecifyKind(DateTime.Today.Add(timeOnly.TimeOfDay), DateTimeKind.Unspecified);

                // Treat as Dutch time to maintain consistency with price data timezone
                pricesToday[dutchDateTime] = priceToday;
            }

            if (day == "Morgen" && double.TryParse(priceText, NumberStyles.Any, CultureInfo.InvariantCulture, out var priceTomorrow))
            {
                // Parse time and ensure it's in Dutch timezone context
                var timeOnly = DateTime.ParseExact(timeStart, "HH:mm", CultureInfo.InvariantCulture);
                var dutchDateTime = DateTime.SpecifyKind(DateTime.Today.AddDays(1).Add(timeOnly.TimeOfDay), DateTimeKind.Unspecified);

                // Treat as Dutch time to maintain consistency with price data timezone
                pricesTomorrow[dutchDateTime] = priceTomorrow;
            }
        }

        PricesToday = pricesToday;
        PricesTomorrow = pricesTomorrow;
        SavePricesToStateFile();
    }

    /// <summary>
    /// Gets the lowest price timeslot using the specified prices
    /// </summary>
    /// <param name="prices">The prices</param>
    /// <param name="windowHours">The window hours (can be fractional)</param>
    /// <returns>The date time start date time end</returns>
    public static (DateTime Start, DateTime End) GetLowestPriceTimeslot(IDictionary<DateTime, double> prices, double windowHours = 3.0)
    {
        if (prices == null || !prices.Any())
            throw new ArgumentException("Prices dictionary cannot be null or empty", nameof(prices));

        if (windowHours <= 0)
            throw new ArgumentException("Window hours must be greater than 0", nameof(windowHours));

        // Sort prices by time to maintain chronological order for window calculations
        var sortedByTime = prices.OrderBy(p => p.Key).ToList();

        // For sub-hour windows, find the single hour with the lowest price and create a window within it
        if (windowHours < 1.0)
        {
            var lowestPriceHour = sortedByTime.OrderBy(p => p.Value).ThenBy(p => p.Key).First();
            var windowMinutes = (int)Math.Ceiling(windowHours * 60);
            return (lowestPriceHour.Key, lowestPriceHour.Key.AddMinutes(windowMinutes - 1));
        }

        var windowHoursInt = (int)Math.Ceiling(windowHours);

        if (windowHoursInt >= sortedByTime.Count)
        {
            // If window is larger than available data, return the entire range
            return (sortedByTime.First().Key, sortedByTime.Last().Key.AddMinutes(59));
        }

        var bestSum = double.MaxValue;
        var bestStart = sortedByTime[0].Key;
        var bestEnd = sortedByTime[windowHoursInt - 1].Key;

        // Find the optimal consecutive window with the lowest total price
        for (var i = 0; i <= sortedByTime.Count - windowHoursInt; i++)
        {
            var currentSum = 0d;
            for (var j = 0; j < windowHoursInt; j++)
            {
                currentSum += sortedByTime[i + j].Value;
            }

            if (currentSum < bestSum)
            {
                bestSum = currentSum;
                bestStart = sortedByTime[i].Key;

                // For fractional hours, calculate the exact end time
                if (windowHours < windowHoursInt)
                {
                    var exactMinutes = (int)Math.Ceiling(windowHours * 60);
                    bestEnd = sortedByTime[i].Key.AddMinutes(exactMinutes - 1);
                }
                else
                {
                    bestEnd = sortedByTime[i + windowHoursInt - 1].Key.AddMinutes(59);
                }
            }
        }

        return (bestStart, bestEnd);
    }

    /// <summary>
    /// Finds the timeslot with the highest total price over a specified window of hours.
    /// </summary>
    /// <param name="prices">Dictionary of prices by DateTime.</param>
    /// <param name="windowHours">Number of consecutive hours in the window (can be fractional)</param>
    /// <returns>Tuple with start and end DateTime of the highest price window.</returns>
    public static (DateTime Start, DateTime End) GetHighestPriceTimeslot(IDictionary<DateTime, double> prices, double windowHours = 1.0)
    {
        if (prices == null || !prices.Any())
            throw new ArgumentException("Prices dictionary cannot be null or empty", nameof(prices));

        if (windowHours <= 0)
            throw new ArgumentException("Window hours must be greater than 0", nameof(windowHours));

        // Sort prices by time to maintain chronological order for window calculations
        var sortedByTime = prices.OrderBy(p => p.Key).ToList();

        // For sub-hour windows, find the single hour with the highest price and create a window within it
        if (windowHours < 1.0)
        {
            var highestPriceHour = sortedByTime.OrderByDescending(p => p.Value).ThenBy(p => p.Key).First();
            var windowMinutes = (int)Math.Ceiling(windowHours * 60);
            return (highestPriceHour.Key, highestPriceHour.Key.AddMinutes(windowMinutes - 1));
        }

        var windowHoursInt = (int)Math.Ceiling(windowHours);

        if (windowHoursInt >= sortedByTime.Count)
        {
            // If window is larger than available data, return the entire range
            return (sortedByTime.First().Key, sortedByTime.Last().Key.AddMinutes(59));
        }

        var bestSum = double.MinValue;
        var bestStart = sortedByTime[0].Key;
        var bestEnd = sortedByTime[windowHoursInt - 1].Key;

        // Find the optimal consecutive window with the highest total price
        for (var i = 0; i <= sortedByTime.Count - windowHoursInt; i++)
        {
            var currentSum = 0d;
            for (var j = 0; j < windowHoursInt; j++)
            {
                currentSum += sortedByTime[i + j].Value;
            }

            if (currentSum > bestSum)
            {
                bestSum = currentSum;
                bestStart = sortedByTime[i].Key;

                // For fractional hours, calculate the exact end time
                if (windowHours < windowHoursInt)
                {
                    var exactMinutes = (int)Math.Ceiling(windowHours * 60);
                    bestEnd = sortedByTime[i].Key.AddMinutes(exactMinutes - 1);
                }
                else
                {
                    bestEnd = sortedByTime[i + windowHoursInt - 1].Key.AddMinutes(59);
                }
            }
        }

        return (bestStart, bestEnd);
    }

    /// <summary>
    /// Gets the lowest night price (before 6 AM) from the given prices.
    /// </summary>
    public static KeyValuePair<DateTime, double> GetLowestNightPrice(IDictionary<DateTime, double> prices)
    {
        return prices.Where(p => p.Key.TimeOfDay < TimeSpan.FromHours(6))
                     .OrderBy(p => p.Value)
                     .ThenBy(p => p.Key)
                     .FirstOrDefault();
    }

    /// <summary>
    /// Gets the lowest day price (after 8 AM) from the given prices.
    /// </summary>
    public static KeyValuePair<DateTime, double> GetLowestDayPrice(IDictionary<DateTime, double> prices)
    {
        return prices.Where(p => p.Key.TimeOfDay > TimeSpan.FromHours(8))
                     .OrderBy(p => p.Value)
                     .ThenBy(p => p.Key)
                     .FirstOrDefault();
    }

    /// <summary>
    /// Gets the next night price (before 6 AM) from tomorrow's prices.
    /// </summary>
    public static KeyValuePair<DateTime, double> GetNextNightPrice(IDictionary<DateTime, double> pricesTomorrow)
    {
        return pricesTomorrow.Where(p => p.Key.TimeOfDay < TimeSpan.FromHours(6))
                             .OrderBy(p => p.Value)
                             .ThenBy(p => p.Key)
                             .FirstOrDefault();
    }
}
