// -----------------------------------------------------------------------------
// Utility methods for HomeAssistantGenerated
// -----------------------------------------------------------------------------

namespace NetDaemonApps.Helpers;

/// <summary>
/// The utils class provides helper methods for value comparison.
/// </summary>
public static class Utils
{
    /// <summary>
    /// Determines whether two double values are equal within a specified tolerance.
    /// </summary>
    /// <param name="value1">The first value.</param>
    /// <param name="value2">The second value.</param>
    /// <param name="tolerance">The tolerance for equality.</param>
    /// <returns>True if the values are equal within the tolerance; otherwise, false.</returns>
    public static bool AreDoublesEqual(double? value1, double? value2, double tolerance = 1e-10)
    {
        // Return false if either value is null
        if (value1 == null || value2 == null)
        {
            return false;
        }

        // Compare the absolute difference to the tolerance
        return Math.Abs(value1.Value - value2.Value) < tolerance;
    }
}
