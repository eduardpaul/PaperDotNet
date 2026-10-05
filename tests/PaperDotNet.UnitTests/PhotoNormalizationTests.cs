using System.Text;
using System.Buffers.Binary;
using PaperDotNet.Documents.Features.StorageOptimization;
using PaperDotNet.Documents.Features.PhotoToDocument;
using SkiaSharp;

namespace PaperDotNet.UnitTests;

public sealed class PhotoNormalizationTests
{
    [Fact]
    public void Single_frame_animated_webp_is_rejected()
    {
        var directory = Directory.CreateTempSubdirectory("pdn_animated_webp_");
        try
        {
            using var bitmap = new SKBitmap(20, 10);
            bitmap.Erase(SKColors.White);
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Webp, 90);
            var frame = encoded.ToArray()[12..];
            using var chunks = new MemoryStream();
            using (var writer = new BinaryWriter(chunks, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write("VP8X"u8);
                writer.Write(10);
                writer.Write(new byte[] { 2, 0, 0, 0, 19, 0, 0, 9, 0, 0 });
                writer.Write("ANIM"u8);
                writer.Write(6);
                writer.Write(new byte[6]);
                writer.Write("ANMF"u8);
                writer.Write(16 + frame.Length);
                writer.Write(new byte[] { 0, 0, 0, 0, 0, 0, 19, 0, 0, 9, 0, 0, 10, 0, 0, 0 });
                writer.Write(frame);
            }
            var source = Path.Combine(directory.FullName, "animated.webp");
            using (var file = File.Create(source))
            using (var writer = new BinaryWriter(file))
            {
                writer.Write("RIFF"u8);
                writer.Write(4 + (int)chunks.Length);
                writer.Write("WEBP"u8);
                writer.Write(chunks.ToArray());
            }
            using var codec = SKCodec.Create(source);
            Assert.NotNull(codec);
            Assert.Equal(1, codec.FrameCount);
            var error = Assert.Throws<InvalidOperationException>(() => ComposePhotosActivity.Normalize(source, Path.Combine(directory.FullName, "page.png")));
            Assert.Contains("Animated", error.Message, StringComparison.Ordinal);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Theory]
    [InlineData(SKEncodedImageFormat.Png)]
    [InlineData(SKEncodedImageFormat.Webp)]
    public void Static_images_are_flattened_to_white(SKEncodedImageFormat format)
    {
        var directory = Directory.CreateTempSubdirectory("pdn_static_photo_");
        try
        {
            using var bitmap = new SKBitmap(20, 10);
            bitmap.Erase(SKColors.Transparent);
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(format, 100);
            var source = Path.Combine(directory.FullName, "source");
            File.WriteAllBytes(source, encoded.ToArray());
            var target = Path.Combine(directory.FullName, "page.png");
            ComposePhotosActivity.Normalize(source, target);
            using var result = SKBitmap.Decode(target);
            Assert.Equal(SKColors.White, result.GetPixel(5, 5));
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public void Animated_png_is_rejected_even_when_decoder_exposes_only_the_default_image()
    {
        var directory = Directory.CreateTempSubdirectory("pdn_animated_photo_");
        try
        {
            using var bitmap = new SKBitmap(20, 10);
            bitmap.Erase(SKColors.White);
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            var png = encoded.ToArray();
            var chunk = new byte[20];
            BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(0, 4), 8);
            "acTL"u8.CopyTo(chunk.AsSpan(4));
            BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8, 4), 2);
            uint crc = uint.MaxValue;
            foreach (var value in chunk.AsSpan(4, 12))
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320);
            }
            BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(16), ~crc);
            var source = Path.Combine(directory.FullName, "animated.png");
            using (var file = File.Create(source))
            {
                file.Write(png.AsSpan(0, 33));
                file.Write(chunk);
                file.Write(png.AsSpan(33));
            }
            var error = Assert.Throws<InvalidOperationException>(() => ComposePhotosActivity.Normalize(source, Path.Combine(directory.FullName, "page.png")));
            Assert.Contains("Animated", error.Message, StringComparison.Ordinal);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Theory]
    [InlineData(1, 100, 60, "red")]
    [InlineData(2, 100, 60, "blue")]
    [InlineData(3, 100, 60, "yellow")]
    [InlineData(4, 100, 60, "green")]
    [InlineData(5, 60, 100, "red")]
    [InlineData(6, 60, 100, "green")]
    [InlineData(7, 60, 100, "yellow")]
    [InlineData(8, 60, 100, "blue")]
    public void Jpeg_exif_orientation_is_applied_before_ocr(int orientation, int width, int height, string corner)
    {
        var directory = Directory.CreateTempSubdirectory("pdn_orientation_");
        try
        {
            using var bitmap = new SKBitmap(100, 60);
            using var canvas = new SKCanvas(bitmap);
            using var paint = new SKPaint();
            foreach (var (x, y, color) in new[] { (0, 0, SKColors.Red), (50, 0, SKColors.Blue), (0, 30, SKColors.Lime), (50, 30, SKColors.Yellow) })
            {
                paint.Color = color;
                canvas.DrawRect(x, y, 50, 30, paint);
            }
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 100);
            var jpeg = encoded.ToArray();
            using var payload = new MemoryStream();
            using (var writer = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write("Exif\0\0"u8);
                writer.Write("II"u8);
                writer.Write((ushort)42);
                writer.Write(8);
                writer.Write((ushort)1);
                writer.Write((ushort)0x112);
                writer.Write((ushort)3);
                writer.Write(1);
                writer.Write((ushort)orientation);
                writer.Write((ushort)0);
                writer.Write(0);
            }
            var source = Path.Combine(directory.FullName, "photo.jpg");
            using (var file = File.Create(source))
            {
                file.Write(jpeg.AsSpan(0, 2));
                file.Write(new byte[] { 255, 225, 0, 34 });
                file.Write(payload.ToArray());
                file.Write(jpeg.AsSpan(2));
            }
            var target = Path.Combine(directory.FullName, "page.png");
            ComposePhotosActivity.Normalize(source, target);
            using var result = SKBitmap.Decode(target);
            Assert.Equal(width, result.Width);
            Assert.Equal(height, result.Height);
            var expected = corner switch { "red" => SKColors.Red, "blue" => SKColors.Blue, "green" => SKColors.Lime, _ => SKColors.Yellow };
            var actual = result.GetPixel(5, 5);
            Assert.InRange(Math.Abs(expected.Red - actual.Red), 0, 3);
            Assert.InRange(Math.Abs(expected.Green - actual.Green), 0, 3);
            Assert.InRange(Math.Abs(expected.Blue - actual.Blue), 0, 3);
        }
        finally { directory.Delete(recursive: true); }
    }
}
