using Microsoft.Extensions.Configuration;
using PaperDotNet.Documents.Contracts;
using PaperDotNet.StorageOptimization;
using SkiaSharp;

namespace PaperDotNet.UnitTests;

public sealed class StorageOptimizationTests
{
    [Fact]
    public async Task Animated_png_is_retained_without_ocr_or_still_image_encoding()
    {
        // Two independently encoded one-pixel RGBA frames (red and blue), with valid APNG sequence and CRCs.
        const string apng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAACGFjVEwAAAACAAAAAPONk3AAAAAaZmNUTAAAAAAAAAABAAAAAQAAAAAAAAAAAAEACgAAWn8w0AAAAA1JREFUeJxj+M/A8B8ABQAB/4mZPR0AAAAaZmNUTAAAAAEAAAABAAAAAQAAAAAAAAAAAAEACgAAwQzaBAAAABFmZEFUAAAAAnicY2Bg+P8fAAMCAf/1e6XXAAAAAElFTkSuQmCC";
        await using var content = new MemoryStream(Convert.FromBase64String(apng));
        using var gate = new ImageOptimizationGate();
        var adapter = new ImageOptimizationAdapter(new ConfigurationBuilder().Build(), gate);
        await using var result = await adapter.OptimizeAsync(content, "eng", new(), TestContext.Current.CancellationToken);
        Assert.Null(result.Content);
        Assert.Contains("Animated", result.SkipReason!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(6, 1)]
    [InlineData(24, 0.5)]
    [InlineData(1000, 0.05)]
    public void Scale_respects_target_minimum_and_no_upscaling(double textHeight, double expected) =>
        Assert.Equal(expected, ImageOptimizationAdapter.ScaleForHeight(textHeight, new()));

    [Fact]
    public void Words_do_not_fall_through_to_rows_and_noise_is_filtered_before_projection()
    {
        const string tsv = "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n"
            + "4\t1\t1\t1\t1\t0\t0\t0\t400\t99\t-1\t\n"
            + "5\t1\t1\t1\t1\t1\t0\t0\t50\t20\t90\tTOTAL\n"
            + "5\t1\t1\t1\t1\t2\t0\t0\t50\t10\t70\t6.19\n"
            + "5\t1\t1\t1\t1\t3\t0\t0\t3\t1\t90\t.\n"
            + "5\t1\t1\t1\t1\t4\t0\t0\t50\t5\t49\tnoise\n";
        Assert.Equal([40.0, 20.0], ImageOptimizationAdapter.WordHeights(tsv, 0.5, 50));
    }

    [Fact]
    public void Percentiles_interpolate_and_do_not_take_the_absolute_minimum()
    {
        Assert.Equal(15, ImageOptimizationAdapter.Percentile([10, 110], 5));
        Assert.Equal(10, ImageOptimizationAdapter.Percentile([10], 5));
        Assert.Equal(110, ImageOptimizationAdapter.Percentile([10, 110], 100));
    }

    [Theory]
    [InlineData(1, 2, 3, 1)]
    [InlineData(2, 2, 3, 2)]
    [InlineData(3, 2, 3, 4)]
    [InlineData(4, 2, 3, 3)]
    [InlineData(5, 3, 2, 1)]
    [InlineData(6, 3, 2, 2)]
    [InlineData(7, 3, 2, 4)]
    [InlineData(8, 3, 2, 3)]
    public void All_exif_orientations_preserve_pixels_including_mirrors(int origin, int width, int height, int expectedCorner)
    {
        using var image = new SKBitmap(2, 3);
        image.Erase(SKColors.White);
        image.SetPixel(0, 0, SKColors.Red);
        image.SetPixel(1, 0, SKColors.Green);
        image.SetPixel(0, 2, SKColors.Blue);
        image.SetPixel(1, 2, SKColors.Yellow);
        using var upright = ImageOptimizationAdapter.Orient(image, (SKEncodedOrigin)origin);
        Assert.Equal(width, upright.Width);
        Assert.Equal(height, upright.Height);
        var corners = new[] { upright.GetPixel(0, 0), upright.GetPixel(width - 1, 0), upright.GetPixel(0, height - 1), upright.GetPixel(width - 1, height - 1) };
        Assert.Equal(SKColors.Red, corners[expectedCorner - 1]);
    }

    [Theory]
    [InlineData(0, 50, 5, 2600, 0.05, 80)]
    [InlineData(12, 101, 5, 2600, 0.05, 80)]
    [InlineData(12, 50, -1, 2600, 0.05, 80)]
    [InlineData(12, 50, 5, 10, 0.05, 80)]
    [InlineData(12, 50, 5, 2600, 0, 80)]
    [InlineData(12, 50, 5, 2600, 0.05, 101)]
    public void Invalid_tuning_is_rejected(double target, double confidence, double percentile, int analysis, double scale, int quality) =>
        Assert.Throws<InvalidOperationException>(() => ImageOptimizationAdapter.Validate(new(target, confidence, percentile, analysis, scale, quality)));

    [Fact]
    public async Task Malformed_images_and_pixel_bombs_fail_before_ocr()
    {
        using var gate = new ImageOptimizationGate();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["StorageOptimization:MaxPixels"] = "10" }).Build();
        var adapter = new ImageOptimizationAdapter(config, gate);
        await using var broken = new MemoryStream("broken image"u8.ToArray());
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.OptimizeAsync(broken, "eng", new(), TestContext.Current.CancellationToken));
        using var bitmap = new SKBitmap(4, 4);
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        await using var pixels = new MemoryStream(png.ToArray());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.OptimizeAsync(pixels, "eng", new(), TestContext.Current.CancellationToken));
        Assert.Contains("pixels", error.Message, StringComparison.Ordinal);
    }
}
