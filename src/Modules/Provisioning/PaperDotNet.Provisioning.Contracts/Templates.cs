using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace PaperDotNet.Provisioning.Contracts;

/// <summary>The template XML namespace and schema version (PRV-03).</summary>
public static class TemplateXml
{
    public const string NamespaceUri = "urn:paperdotnet:template:1";
    public const string SchemaVersion = "1.0";

    public static readonly XNamespace Ns = NamespaceUri;

    /// <summary>A name in the template namespace (built-in sections); extensions use their own namespace.</summary>
    public static XName Name(string localName) => Ns + localName;
}

/// <summary>What a template describes: a whole tenant or one workspace (with what it depends on).</summary>
public enum TemplateScope
{
    Tenant = 0,
    Workspace = 1,
}

/// <summary>The element a section belongs to: the template root, a <c>Workspace</c> or a <c>List</c>.</summary>
public enum TemplateLevel
{
    Tenant = 0,
    Workspace = 1,
    List = 2,
}

public enum TemplateChangeAction
{
    Create = 0,
    Update = 1,
}

/// <summary>A change an apply makes (or would make, in a dry run).</summary>
public sealed record TemplateChange(TemplateChangeAction Action, string Kind, string Name, string? Detail = null);

/// <summary>Kinds used for changes and cross-section references.</summary>
public static class TemplateKinds
{
    public const string Extension = "extension";
    public const string Group = "group";
    public const string Role = "role";
    public const string User = "user";
    public const string TermGroup = "termGroup";

    /// <summary>Key: <c>Group/Set</c>.</summary>
    public const string TermSet = "termSet";

    public const string Term = "term";

    /// <summary>Key: the content type name.</summary>
    public const string ContentType = "contentType";

    public const string Workspace = "workspace";

    /// <summary>Key: <c>Workspace/List</c>.</summary>
    public const string List = "list";

    public const string View = "view";
    public const string Member = "member";
    public const string Permissions = "permissions";
    public const string Settings = "settings";
}

/// <summary>
/// State of one export or apply, shared by all handlers: the current workspace and list, the
/// ids registered by earlier sections (resolved or planned), the recorded changes and warnings.
/// </summary>
public sealed class TemplateContext(TemplateScope scope, bool dryRun)
{
    private readonly List<TemplateChange> _changes = [];
    private readonly List<string> _warnings = [];
    private readonly Dictionary<(string Kind, string Key), Guid> _ids = [];
    private readonly HashSet<(string Kind, string Key)> _required = [];
    private readonly List<Func<CancellationToken, Task>> _deferred = [];

    public TemplateScope Scope => scope;

    /// <summary>Apply only: record and validate, but write nothing.</summary>
    public bool DryRun => dryRun;

    /// <summary>The current workspace (workspace and list levels).</summary>
    public Guid? WorkspaceId { get; set; }

    public string? WorkspaceName { get; set; }

    /// <summary>The current list (list level).</summary>
    public Guid? ListId { get; set; }

    public string? ListName { get; set; }

    /// <summary>
    /// The current workspace or list does not exist yet (dry run): its id is a placeholder and
    /// nothing is stored under it, so every section below it is new.
    /// </summary>
    public bool IsPlanned { get; set; }

    /// <summary>Free state for handlers of one module (key it with the module name).</summary>
    public IDictionary<string, object> Items { get; } = new Dictionary<string, object>(StringComparer.Ordinal);

    public IReadOnlyList<TemplateChange> Changes => _changes;

    public IReadOnlyList<string> Warnings => _warnings;

    public void Created(string kind, string name, string? detail = null) => _changes.Add(new(TemplateChangeAction.Create, kind, name, detail));

    public void Updated(string kind, string name, string? detail = null) => _changes.Add(new(TemplateChangeAction.Update, kind, name, detail));

    /// <summary>A problem that does not stop the apply (e.g. a user that does not exist on the target).</summary>
    public void Warn(string message, XObject? source = null) => _warnings.Add(TemplateException.Describe(message, source));

    /// <summary>Makes an object's id (existing or planned) known to later sections.</summary>
    public void Register(string kind, string key, Guid id) => _ids[(kind, key)] = id;

    public Guid? Resolve(string kind, string key) => _ids.TryGetValue((kind, key), out var id) ? id : null;

    /// <summary>Export: marks a tenant-level object that the exported workspace depends on.</summary>
    public void Require(string kind, string key) => _required.Add((kind, key));

    /// <summary>Export: whether a tenant-level object belongs in the template (all of them for a tenant template).</summary>
    public bool Includes(string kind, string key) => Scope == TemplateScope.Tenant || _required.Contains((kind, key));

    /// <summary>Apply: work to run after all sections (e.g. lookup fields whose list comes later).</summary>
    public void Defer(Func<CancellationToken, Task> action) => _deferred.Add(action);

    /// <summary>Runs the deferred work (called once by the provisioning engine).</summary>
    public async Task RunDeferredAsync(CancellationToken cancellationToken)
    {
        for (var i = 0; i < _deferred.Count; i++)
        {
            await _deferred[i](cancellationToken);
        }

        _deferred.Clear();
    }
}

/// <summary>
/// A template section (PRV-05): one element at one level, owned by one module or extension. Built-in
/// sections use the template namespace; extensions use their own (validated laxly). Export returns the
/// section for the current tenant, workspace or list (null when there is nothing to export); apply
/// creates what is missing and updates what differs, writing nothing when <see cref="TemplateContext.DryRun"/>.
/// Throw <see cref="TemplateException"/> for problems that must stop the apply.
/// </summary>
public interface ITemplateHandler
{
    XName Element { get; }

    TemplateLevel Level { get; }

    /// <summary>Order within the level (lower first). Built-in tenant sections use 100–400; extensions should use 1000 or more.</summary>
    int Order { get; }

    Task<XElement?> ExportAsync(TemplateContext context, CancellationToken cancellationToken);

    Task ApplyAsync(XElement section, TemplateContext context, CancellationToken cancellationToken);
}

/// <summary>
/// The <c>Workspace</c> and <c>List</c> elements (implemented by the Workspaces and Lists modules):
/// enumerates, exports and finds or creates them, setting the current workspace or list on the context.
/// </summary>
public interface ITemplateContainer
{
    /// <summary><see cref="TemplateLevel.Workspace"/> or <see cref="TemplateLevel.List"/>.</summary>
    TemplateLevel Level { get; }

    /// <summary>Export: the workspaces of the template (all shared ones, or the context's) or the lists of the context's workspace.</summary>
    Task<IReadOnlyList<Guid>> ListAsync(TemplateContext context, CancellationToken cancellationToken);

    /// <summary>Export: the element with its attributes and own children; sets the context's current workspace or list.</summary>
    Task<XElement> ExportAsync(Guid id, TemplateContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Apply: finds (by name) or creates the workspace or list and updates it; sets the context's current
    /// id, name and <see cref="TemplateContext.IsPlanned"/>. A workspace id already on the context is the target.
    /// </summary>
    Task ApplyAsync(XElement element, TemplateContext context, CancellationToken cancellationToken);
}

/// <summary>A template problem that stops the apply; carries the line number when known.</summary>
public sealed class TemplateException(string message, XObject? source = null) : Exception(Describe(message, source))
{
    public int? LineNumber { get; } = source is IXmlLineInfo { } info && info.HasLineInfo() ? info.LineNumber : null;

    public static string Describe(string message, XObject? source) =>
        source is IXmlLineInfo info && info.HasLineInfo()
            ? string.Create(CultureInfo.InvariantCulture, $"Line {info.LineNumber}: {message}")
            : message;
}

/// <summary>Attribute helpers for handlers (invariant culture, XML Schema boolean forms).</summary>
public static class TemplateElementExtensions
{
    public static string? Attr(this XElement element, string name) => (string?)element.Attribute(name);

    public static string RequiredAttr(this XElement element, string name) =>
        element.Attr(name) is { Length: > 0 } value ? value : throw new TemplateException($"{element.Name.LocalName}: attribute {name} is required.", element);

    public static bool BoolAttr(this XElement element, string name, bool fallback) =>
        element.Attr(name) is { } value ? XmlConvert.ToBoolean(value) : fallback;

    public static int? IntAttr(this XElement element, string name) =>
        element.Attr(name) is { } value ? XmlConvert.ToInt32(value) : null;

    public static decimal? DecimalAttr(this XElement element, string name) =>
        element.Attr(name) is { } value ? XmlConvert.ToDecimal(value) : null;

    public static TEnum EnumAttr<TEnum>(this XElement element, string name, TEnum fallback)
        where TEnum : struct, Enum
    {
        if (element.Attr(name) is not { } value)
        {
            return fallback;
        }

        return Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) && !int.TryParse(value, out _)
            ? parsed
            : throw new TemplateException($"{element.Name.LocalName}: '{value}' is not a valid {name}.", element);
    }

    /// <summary>Sets an attribute in its XML form; null (and false when <paramref name="omitDefault"/>) leaves it out.</summary>
    public static XElement With(this XElement element, string name, object? value, bool omitDefault = false)
    {
        var text = value switch
        {
            null => null,
            bool b => omitDefault && !b ? null : XmlConvert.ToString(b),
            int i => XmlConvert.ToString(i),
            decimal d => XmlConvert.ToString(d),
            Enum e => e.ToString(),
            string s => s,
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };
        if (text is not null)
        {
            element.SetAttributeValue(name, text);
        }

        return element;
    }
}
