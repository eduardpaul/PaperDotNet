using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PaperDotNet.Ocr;
using PaperDotNet.Ocr.Contracts;

namespace PaperDotNet.UnitTests;

public sealed class OcrModuleTests
{
    [Fact]
    public void Host_ocr_is_available_without_enabling_storage_optimization()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        services.AddSingleton<IConfiguration>(configuration);
        new OcrModule().AddServices(services, configuration);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.Equal("paddleocr", provider.GetRequiredService<IOptions<OcrOptions>>().Value.Engine);
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IOcrService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IOcrEngine>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IWordLayoutDetector>());
        var detector = provider.GetRequiredService<ITextDetector>();
        Assert.Equal("PP-OCRv6_small_det", detector.Model);
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "models", detector.Model + ".onnx")));
    }

    [Fact]
    public void New_ocr_settings_override_legacy_recognition_settings()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Documents:Engine"] = "glm",
            ["Documents:TesseractPath"] = "legacy",
            ["Ocr:TesseractPath"] = "new",
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        new OcrModule().AddServices(services, configuration);
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<OcrOptions>>().Value;
        Assert.Equal("glm", options.Engine);
        Assert.Equal("new", options.TesseractPath);
        Assert.Equal("PP-OCRv6_small_det", provider.GetRequiredService<ITextDetector>().Model);
    }
}
