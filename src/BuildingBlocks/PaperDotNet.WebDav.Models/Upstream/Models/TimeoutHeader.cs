#nullable enable // PaperDotNet: vendored files are generated code for the analyzers (ADR-0047).
// <copyright file="TimeoutHeader.cs" company="Fubar Development Junker">
// Copyright (c) Fubar Development Junker. All rights reserved.
// </copyright>

using FubarDev.WebDavServer.Properties;

namespace FubarDev.WebDavServer.Models
{
    /// <summary>
    /// The HTTP <c>Timeout</c> header.
    /// </summary>
    public class TimeoutHeader
    {
        private static readonly char[] _unitValueSplitChar = { '-' };

        /// <summary>
        /// Initializes a new instance of the <see cref="TimeoutHeader"/> class.
        /// </summary>
        /// <param name="values">The timeout values.</param>
        public TimeoutHeader(IReadOnlyCollection<TimeSpan> values)
        {
            Values = values;
        }

        /// <summary>
        /// Gets the timeout value for an infinite timeout.
        /// </summary>
        public static TimeSpan Infinite { get; } = TimeSpan.MaxValue;

        /// <summary>
        /// Gets the timeout values of the <c>Timeout</c> header.
        /// </summary>
        public IReadOnlyCollection<TimeSpan> Values { get; }

        /// <summary>
        /// Parses the header values to get a new instance of the <see cref="TimeoutHeader"/> class.
        /// </summary>
        /// <param name="args">The header values to parse.</param>
        /// <returns>The new instance of the <see cref="TimeoutHeader"/> class.</returns>
        public static TimeoutHeader Parse(IEnumerable<string> args)
        {
            // PaperDotNet: one header value may hold a list ("Infinite, Second-4100000000", as Windows sends it),
            // values may exceed Int32, and unknown units are ignored (RFC 4918 10.7).
            var timespans = new List<TimeSpan>();
            foreach (var arg in args.SelectMany(a => a.Split(',')).Select(a => a.Trim()).Where(a => a.Length != 0))
            {
                if (string.Equals(arg, "Infinite", StringComparison.OrdinalIgnoreCase))
                {
                    timespans.Add(Infinite);
                    continue;
                }

                var parts = arg.Split(_unitValueSplitChar, 2);
                if (parts.Length == 2
                    && string.Equals(parts[0].Trim(), "Second", StringComparison.OrdinalIgnoreCase)
                    && long.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                {
                    timespans.Add(TimeSpan.FromSeconds(Math.Min(seconds, uint.MaxValue)));
                }
            }

            return new TimeoutHeader(timespans);
        }

        /// <inheritdoc />
        public override string ToString()
        {
            var output = Values.Select(timeSpan => timeSpan == Infinite ? "Infinite" : $"Second-{timeSpan.TotalSeconds:F0}");
            return string.Join(",", output);
        }
    }
}
