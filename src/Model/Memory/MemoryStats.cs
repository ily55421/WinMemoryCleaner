using System;

namespace WinMemoryCleaner
{
    /// <summary>
    /// Memory Stats
    /// </summary>
    public class MemoryStats
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="MemoryStats" /> class.
        /// </summary>
        /// <param name="free">The amount of memory currently available</param>
        /// <param name="total">The amount of actual memory</param>
        /// <param name="used">A number between 0 and 100 that specifies the approximate amount of memory that is in use</param>
        public MemoryStats(long free, long total, int? used = null)
        {
            Free = new MemorySize(free);
            Total = new MemorySize(total);

            var usedBytes = total >= free ? total - free : free - total;

            Used = new MemorySize(usedBytes);

            if (used == null)
                used = total > 0 ? (int)(usedBytes * 100L / total) : 0;

            // Guard against out-of-range values coming from the operating system
            used = Math.Max(0, Math.Min(100, (int)used));

            Free.Percentage = 100 - (int)used;
            Used.Percentage = (int)used;
        }

        /// <summary>
        /// Gets or sets the free memory.
        /// </summary>
        /// <value>
        /// The free memory.
        /// </value>
        public MemorySize Free { get; private set; }

        /// <summary>
        /// Gets or sets the total memory.
        /// </summary>
        /// <value>
        /// The total memory.
        /// </value>
        public MemorySize Total { get; private set; }

        /// <summary>
        /// Gets or sets the used memory.
        /// </summary>
        /// <value>
        /// The used memory.
        /// </value>
        public MemorySize Used { get; private set; }

        /// <summary>
        /// Converts to string.
        /// </summary>
        /// <returns>
        /// A <see cref="string" /> that represents this instance.
        /// </returns>
        public override string ToString()
        {
            return string.Format(Localizer.Culture, "({0:0.#} {1}) {2} | {3} - {4} | {5}", Total.Value, Total.Unit, Localizer.String.Used, Used, Localizer.String.Free, Free);
        }
    }
}
