namespace NetDaemonApps.Models.EnergyPrices
{
    using System;
    using System.Collections.Generic;

    using Enums;

    public interface IPriceHelper
    {
        IDictionary<DateTime, double>? PricesToday { get; }
        IDictionary<DateTime, double>? PricesTomorrow { get; }
        double PriceThreshold { get; }
        double? CurrentPrice { get; }
        Level EnergyPriceLevel { get; }
    }
}

