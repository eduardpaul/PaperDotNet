using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PaperDotNet.Abstractions;
using PaperDotNet.Ocr.Contracts;

namespace PaperDotNet.Ocr;

public sealed class OcrModule : IModule
{
    public string Name => "Ocr";
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        // Existing deployments retain their configured recognition engine; Ocr overrides legacy settings.
        services.AddOptions<OcrOptions>().Bind(configuration.GetSection("Documents")).Bind(configuration.GetSection(OcrOptions.Section));
        services.AddHttpClient(GlmOcr.HttpClientName, (sp, client) => client.Timeout = sp.GetRequiredService<IOptions<OcrOptions>>().Value.OcrTimeout);
        services.AddSingleton<PaddleOcr>();
        services.AddScoped<OcrEngine>();
        services.AddScoped<IOcrEngine>(sp => sp.GetRequiredService<OcrEngine>());
        services.AddScoped<IOcrService, ConfiguredOcrService>();
        services.AddSingleton<ITextDetector, PaddleTextDetector>();
        services.AddScoped<IWordLayoutDetector, TesseractWordDetector>();
    }
    public void MapEndpoints(IEndpointRouteBuilder endpoints) { }
}
