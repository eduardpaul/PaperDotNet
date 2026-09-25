using System.Text;
using PaperDotNet.Extensions;
using PaperDotNet.Extensions.Generated;
using PaperDotNet.Samples.Invoices;

namespace PaperDotNet.UnitTests;

public sealed class ExtensionTests
{
    private static ExtensionManifest Parse(string json) => ExtensionManifest.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void Source_generator_finds_referenced_extensions()
    {
        var extensions = ReferencedExtensions.Create();

        Assert.Contains(extensions, e => e is InvoicesExtension);
    }

    [Fact]
    public void Sample_manifest_is_valid()
    {
        using var stream = typeof(InvoicesExtension).Assembly.GetManifestResourceStream(ExtensionSdk.ManifestResource)!;
        var manifest = ExtensionManifest.Parse(stream);

        Assert.Empty(manifest.Validate(ExtensionSdk.Version));
        Assert.Equal(3, manifest.Settings.Count);
    }

    [Fact]
    public void Invalid_manifests_report_every_problem()
    {
        var manifest = Parse("""
            {
              "id": "Invoices", "name": "", "version": "1.0", "sdkVersion": "2.0",
              "scopes": [{ "name": "other.read", "description": "x" }],
              "settings": [{ "name": "Mode", "type": "choice" }, { "name": "limit", "type": "number", "default": "high" }]
            }
            """);

        var errors = manifest.Validate(new Version(1, 0));

        Assert.Contains(errors, e => e.StartsWith("id", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("name", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("version", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("sdkVersion", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("scope 'other.read'", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("camelCase", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("need choices", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("default must be a number", StringComparison.Ordinal));
    }

    [Fact]
    public void Newer_minor_sdk_versions_are_rejected_older_ones_accepted()
    {
        var manifest = Parse("""{ "id": "acme.x", "name": "X", "version": "1.0.0", "sdkVersion": "1.3" }""");

        Assert.NotEmpty(manifest.Validate(new Version(1, 2)));
        Assert.Empty(manifest.Validate(new Version(1, 4)));
    }

    [Theory]
    [InlineData("DE89 3704 0044 0532 0130 00", true)]
    [InlineData("GB82WEST12345698765432", true)]
    [InlineData("DE89370400440532013001", false)]
    public void Sample_iban_field_checks_the_checksum(string iban, bool valid) =>
        Assert.Equal(valid, IbanFieldType.IsValid(iban.Replace(" ", string.Empty, StringComparison.Ordinal)));
}

public sealed class MutatorRegistrationTests
{
    private sealed class Noop : PaperDotNet.Lists.Contracts.IItemMutator;

    [Fact]
    public void Mutators_filter_by_content_type_list_and_template()
    {
        var options = new ItemMutatorOptions();
        options.ContentTypes.Add("Invoice");
        options.ListTemplates.Add("samples.invoices.invoices");
        var mutator = new PaperDotNet.ExtensionHost.Runtime.GatedItemMutator("samples.invoices", options, new Noop(), null!);
        var scope = new PaperDotNet.Lists.Contracts.ItemEventScope(Guid.NewGuid(), Guid.NewGuid(), "Invoices", Guid.NewGuid(), false)
        {
            ContentTypeName = "invoice",
            ListTemplate = "samples.invoices.invoices",
        };

        Assert.True(mutator.AppliesTo(scope));
        Assert.False(mutator.AppliesTo(scope with { ListTemplate = "tasks" }));
        Assert.False(mutator.AppliesTo(scope with { ContentTypeName = "Quote" }));
        Assert.False(mutator.AppliesTo(scope with { IsFolder = true }));
    }
}
