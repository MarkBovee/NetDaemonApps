namespace HomeAssistantGenerated.helpers;

/// <summary>
/// The utils class
/// </summary>

public static class Utils
{
    /// <summary>
    /// Describes whether are doubles equal
    /// </summary>
    /// <param name="value1">The value</param>
    /// <param name="value2">The value</param>
    /// <param name="tolerance">The tolerance</param>
    /// <returns>The bool</returns>
    public static bool AreDoublesEqual(double? value1, double? value2, double tolerance = 1e-10)
    {
        if (value1 == null || value2 == null)
        {
            return false;
        }
        
        return Math.Abs(value1.Value - value2.Value) < tolerance;
    }
}