#nullable enable // PaperDotNet: vendored files are generated code for the analyzers (ADR-0047).
// <copyright file="WebDavRequestHeaders.cs" company="Fubar Development Junker">
// Copyright (c) Fubar Development Junker. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Xml;

using FubarDev.WebDavServer.Models;
using FubarDev.WebDavServer.Parsing;

using Microsoft.AspNetCore.Http;

namespace FubarDev.WebDavServer;

/// <summary>
/// Implementation of the <see cref="IWebDavRequestHeaders"/> interface.
/// </summary>
public class WebDavRequestHeaders : IWebDavRequestHeaders
{
    private static readonly string[] _empty = Array.Empty<string>();

    /// <summary>
    /// Initializes a new instance of the <see cref="WebDavRequestHeaders"/> class.
    /// </summary>
    /// <param name="headers">The headers to parse.</param>
    public WebDavRequestHeaders(IHeaderDictionary headers)
    {
        Headers = headers.ToDictionary(
            x => x.Key,
            x => (IReadOnlyList<string>)x.Value.ToList(),
            StringComparer.OrdinalIgnoreCase);
        // PaperDotNet: a malformed header is a 400 (not a 500); malformed conditional and Timeout headers are ignored.
        Depth = Required(() => ParseHeader("Depth", args => DepthHeader.Parse(args.Single())));
        Overwrite = Required(() => ParseValueHeader("Overwrite", args => OverwriteHeader.Parse(args.Single())));
        Range = Optional(() => ParseHeader("Range", RangeHeader.Parse));
        If = ParseHeaders("If", ParseIfHeader);
        IfMatch = Optional(() => ParseHeader("If-Match", IfMatchHeader.Parse));
        IfNoneMatch = Optional(() => ParseHeader("If-None-Match", IfNoneMatchHeader.Parse));
        IfModifiedSince = Optional(() => ParseHeader("If-Modified-Since", args => IfModifiedSinceHeader.Parse(args.Single())));
        IfUnmodifiedSince = Optional(() => ParseHeader("If-Unmodified-Since", args => IfUnmodifiedSinceHeader.Parse(args.Single())));
        Timeout = Optional(() => ParseHeader("Timeout", TimeoutHeader.Parse));
        ContentLength = Required(() => ParseValueHeader("Content-Length", args => (long?)XmlConvert.ToInt64(args.Single())));
    }

    /// <inheritdoc />
    public long? ContentLength { get; }

    /// <inheritdoc />
    public DepthHeader? Depth { get; }

    /// <inheritdoc />
    public bool? Overwrite { get; }

    /// <inheritdoc />
    public IReadOnlyList<IfHeader>? If { get; }

    /// <inheritdoc />
    public IfMatchHeader? IfMatch { get; }

    /// <inheritdoc />
    public IfNoneMatchHeader? IfNoneMatch { get; }

    /// <inheritdoc />
    public IfModifiedSinceHeader? IfModifiedSince { get; }

    /// <inheritdoc />
    public IfUnmodifiedSinceHeader? IfUnmodifiedSince { get; }

    /// <inheritdoc />
    public RangeHeader? Range { get; }

    /// <inheritdoc />
    public TimeoutHeader? Timeout { get; }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Headers { get; }

    /// <inheritdoc />
    public IReadOnlyCollection<string> this[string name]
    {
        get
        {
            if (Headers.TryGetValue(name, out var v))
            {
                return v;
            }

            return _empty;
        }
    }

    private static T? Required<T>(Func<T?> parse)
    {
        try
        {
            return parse();
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or OverflowException)
        {
            throw new WebDavException(WebDavStatusCode.BadRequest, ex.Message);
        }
    }

    private static T? Optional<T>(Func<T?> parse)
    {
        try
        {
            return parse();
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or OverflowException)
        {
            return default;
        }
    }

    private static IfHeader ParseIfHeader(string s)
    {
        // PaperDotNet: HeaderParser replaces the Yoakke-generated parser.
        if (!HeaderParser.TryParseIfHeader(s, out var header))
        {
            throw new WebDavException(WebDavStatusCode.BadRequest, "Invalid If header");
        }

        return header;
    }

    private T? ParseValueHeader<T>(string name, Func<IReadOnlyCollection<string>, T?> createFunc, T? defaultValue = default)
        where T : struct
    {
        if (Headers.TryGetValue(name, out var v))
        {
            if (v.Count != 0)
            {
                return createFunc(v);
            }
        }

        return defaultValue;
    }

    private T? ParseHeader<T>(string name, Func<IReadOnlyCollection<string>, T> createFunc, T? defaultValue = default)
        where T : class
    {
        if (Headers.TryGetValue(name, out var v))
        {
            if (v.Count != 0)
            {
                return createFunc(v);
            }
        }

        return defaultValue;
    }

    private IReadOnlyList<T>? ParseHeaders<T>(
        string name,
        Func<string, T> createFunc)
    {
        if (Headers.TryGetValue(name, out var v))
        {
            return v.Select(createFunc).ToImmutableList();
        }

        return null;
    }
}
