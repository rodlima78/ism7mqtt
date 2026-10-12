using System;

namespace ism7mqtt.HomeAssistant
{
    /// <summary>
    /// The Home Assistant state class of a numeric parameter that cannot be derived from its unit alone.
    /// </summary>
    public static class HaStateClass
    {
        /// <summary>
        /// Energy values. A running total, or the total of the current day, month or year, only grows until its
        /// period starts again, which <c>total_increasing</c> reads as a reset. The total of a period that has
        /// ended (Vortag, Vorjahr, Vorvorjahr; VT and VJ in the abbreviated names) is a fixed reference value, not
        /// a counter: it gets no state class, because Home Assistant rejects <c>measurement</c> for energy and
        /// would book the step to the next period's total as consumption.
        /// </summary>
        public static string ForEnergy(string name)
        {
            var lower = name.ToLowerInvariant();
            if (lower.Contains("vortag") || lower.Contains("vorjahr") ||
                lower.Contains(" vt ") || lower.Contains(" vj ") ||
                lower.EndsWith(" vt") || lower.EndsWith(" vj"))
            {
                return null;
            }
            return "total_increasing";
        }

        /// <summary>
        /// Parameters without a unit whose values are still numbers to plot: valve openings are measurements,
        /// start and power-on counts only grow.
        /// </summary>
        public static string ForUnitless(string name)
        {
            switch (name)
            {
                case "EEV HZ":
                case "EEV K":
                    return "measurement";
                case "Verdichterstarts":
                case "Anzahl Netz Ein":
                    return "total_increasing";
                default:
                    return null;
            }
        }
    }
}
