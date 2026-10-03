using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PaperDotNet.Jobs;
using PaperDotNet.Jobs.Features;

namespace PaperDotNet.UnitTests;

public sealed class JobsOptionsTests
{
    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-00:00:00.001")]
    [InlineData("00:00:00.0001")]
    [InlineData("49.17:02:47.295")]
    public async Task Invalid_keep_alive_intervals_are_rejected_at_startup(string interval)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Jobs:LiveEventsKeepAlive"] = interval });
        new JobsModule().AddServices(builder.Services, builder.Configuration);
        // Avoid starting unrelated workers; retain the real options startup validation.
        builder.Services.Remove(builder.Services.Single(d => d.ServiceType == typeof(IHostedService)
            && d.ImplementationType?.Name == "RecurringJobScheduler"));
        using var host = builder.Build();
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));
        Assert.Contains("Jobs:LiveEventsKeepAlive", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("00:00:00.001")]
    [InlineData("00:00:30")]
    [InlineData("49.17:02:47.294")]
    public void Supported_keep_alive_intervals_are_accepted(string interval)
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Jobs:LiveEventsKeepAlive"] = interval }).Build();
        services.AddSingleton<IConfiguration>(configuration);
        new JobsModule().AddServices(services, configuration);
        using var provider = services.BuildServiceProvider();
        Assert.Equal(TimeSpan.Parse(interval, System.Globalization.CultureInfo.InvariantCulture), provider.GetRequiredService<IOptions<JobsOptions>>().Value.LiveEventsKeepAlive);
    }
}
