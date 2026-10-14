using System.Text;

using Ovrilo.Modules.Media.Images;

namespace Ovrilo.Api.Tests.Media;

public sealed class ImageInspectorTests
{
    public static TheoryData<string, string, int, int> Images() => new()
    {
        { nameof(TestImages.Png), ImageInspector.Png, 3, 2 },
        { nameof(TestImages.Jpeg), ImageInspector.Jpeg, 5, 4 },
        { nameof(TestImages.AnimatedGif), ImageInspector.Gif, 7, 6 },
        { nameof(TestImages.WebPLossy), ImageInspector.WebP, 9, 8 },
        { nameof(TestImages.WebPLossless), ImageInspector.WebP, 11, 10 },
        { nameof(TestImages.AnimatedWebP), ImageInspector.WebP, 7, 6 },
        { nameof(TestImages.WidePng), ImageInspector.Png, 5000, 1 },
    };

    [Theory]
    [MemberData(nameof(Images))]
    public void DetectsFormatAndSize(string image, string contentType, int width, int height)
    {
        var bytes = (byte[])typeof(TestImages).GetField(image)!.GetValue(null)!;

        Assert.Equal(new ImageInfo(contentType, width, height), ImageInspector.Inspect(bytes));
    }

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>")]
    [InlineData("<!doctype html><script>alert(1)</script>")]
    [InlineData("MZ\u0090\u0000 not an image")]
    [InlineData("")]
    public void RejectsNonImages(string content) =>
        Assert.Null(ImageInspector.Inspect(Encoding.UTF8.GetBytes(content)));

    [Fact]
    public void RejectsTruncatedHeaders()
    {
        Assert.Null(ImageInspector.Inspect(TestImages.Png.AsSpan(0, 20)));
        Assert.Null(ImageInspector.Inspect(TestImages.AnimatedGif.AsSpan(0, 8)));
        Assert.Null(ImageInspector.Inspect(TestImages.Jpeg.AsSpan(0, 30)));
    }
}
