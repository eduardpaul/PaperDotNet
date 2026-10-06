#nullable enable // PaperDotNet: vendored files are generated code for the analyzers (ADR-0047).
// <copyright file="WebDavContextAccessor.cs" company="Fubar Development Junker">
// Copyright (c) Fubar Development Junker. All rights reserved.
// </copyright>

using FubarDev.WebDavServer.AspNetCore.Contexts;

using Microsoft.AspNetCore.Http;

namespace FubarDev.WebDavServer.AspNetCore
{
    // PaperDotNet: always the escaped context (the litmus-only unescaped variant is not vendored).
    internal class WebDavContextAccessor : GenericWebDavContextAccessor<EscapedWebDavContext>
    {
        public WebDavContextAccessor(IHttpContextAccessor httpContextAccessor)
            : base(httpContextAccessor)
        {
        }
    }
}
