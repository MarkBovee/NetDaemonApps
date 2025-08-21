namespace NetDaemonApps.Models.Battery
{
    using System.Diagnostics;

    /// <summary>
    /// Represents the dynamic parameters for saving the battery schedule.
    /// </summary>
    public class BatteryScheduleParameters
    {
        /// <summary>
        /// Gets or sets the value of the comm address.
        /// </summary>
        public string CommAddress { get; set; }

        /// <summary>
        /// Gets or sets the value of the component id.
        /// </summary>
        public string ComponentId { get; set; }

        /// <summary>
        /// Gets or sets the value of the transfer id.
        /// </summary>
        public string TransferId { get; set; }

        /// <summary>
        /// Gets or sets the value of the value.
        /// </summary>
        public string Value { get; set; }

        /// <summary>
        /// Describes whether this instance equals.
        /// </summary>
        /// <param name="obj">The obj.</param>
        /// <returns>The bool.</returns>
        public override bool Equals(object? obj)
        {
            return obj is BatteryScheduleParameters other && Compare(other);
        }

        /// <summary>
        /// Compares this instance with another and returns a list of mismatched fields.
        /// </summary>
        /// <param name="other">The other BatteryScheduleParameters.</param>
        /// <returns>List of mismatched field names and values.</returns>
        private bool Compare(BatteryScheduleParameters other)
        {
            var isEqual = true;

            if (CommAddress != other.CommAddress)
            {
                Debug.WriteLine($"CommAddress mismatch: expected '{other.CommAddress}', got '{CommAddress}'");
                isEqual = false;
            }

            if (ComponentId != other.ComponentId)
            {
                Debug.WriteLine($"ComponentId mismatch: expected '{other.ComponentId}', got '{ComponentId}'");
                isEqual = false;
            }

            if (TransferId != other.TransferId)
            {
                Debug.WriteLine($"TransferId mismatch: expected '{other.TransferId}', got '{TransferId}'");
                isEqual = false;
            }

            if (Value != other.Value)
            {
                Debug.WriteLine($"Value mismatch: expected '{other.Value}', got '{Value}'");
                isEqual = false;
            }

            return isEqual;
        }

        /// <summary>
        /// Gets the hash code.
        /// </summary>
        /// <returns>The int.</returns>
        public override int GetHashCode()
        {
            return HashCode.Combine(CommAddress, ComponentId, TransferId, Value);
        }
    }
}
