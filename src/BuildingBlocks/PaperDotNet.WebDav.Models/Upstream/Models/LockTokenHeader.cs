#nullable enable // PaperDotNet: vendored files are generated code for the analyzers (ADR-0047).
// <copyright file="LockTokenHeader.cs" company="Fubar Development Junker">
// Copyright (c) Fubar Development Junker. All rights reserved.
// </copyright>

using FubarDev.WebDavServer.Parsing;
using FubarDev.WebDavServer.Properties;

namespace FubarDev.WebDavServer.Models
{
    /// <summary>
    /// The <c>Lock-Token</c> header.
    /// </summary>
    public class LockTokenHeader
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="LockTokenHeader"/> class.
        /// </summary>
        /// <param name="stateToken">The lock token.</param>
        public LockTokenHeader(Uri stateToken)
        {
            StateToken = stateToken;
        }

        /// <summary>
        /// Gets the lock token.
        /// </summary>
        public Uri StateToken { get; }

        /// <summary>
        /// Parses the header string to get a new instance of the <see cref="LockTokenHeader"/> class.
        /// </summary>
        /// <param name="s">The header string to parse.</param>
        /// <returns>The new instance of the <see cref="LockTokenHeader"/> class.</returns>
        public static LockTokenHeader Parse(string s)
        {
            // PaperDotNet: HeaderParser replaces the Yoakke-generated parser.
            if (HeaderParser.TryParseCodedUrl(s, out var url))
            {
                return new LockTokenHeader(url);
            }

            throw new ArgumentException(
                string.Format(Resources.InvalidLockTokenFormat, s),
                nameof(s));
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return $"<{StateToken}>";
        }
    }
}
