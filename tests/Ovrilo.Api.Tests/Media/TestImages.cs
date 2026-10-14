namespace Ovrilo.Api.Tests.Media;

/// <summary>Tiny images generated with Pillow; sizes are noted per image.</summary>
internal static class TestImages
{
    /// <summary>3×2.</summary>
    public static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAMAAAACCAYAAACddGYaAAAAE0lEQVR4nGP8z8DwnwEKmBiQAAAvHwICobBLewAAAABJRU5ErkJggg==");

    /// <summary>5×4.</summary>
    public static readonly byte[] Jpeg = Convert.FromBase64String("/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAYEBQYFBAYGBQYHBwYIChAKCgkJChQODwwQFxQYGBcUFhYaHSUfGhsjHBYWICwgIyYnKSopGR8tMC0oMCUoKSj/2wBDAQcHBwoIChMKChMoGhYaKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCj/wAARCAAEAAUDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDn6KKK/YT8qP/Z");

    /// <summary>7×6, two frames.</summary>
    public static readonly byte[] AnimatedGif = Convert.FromBase64String("R0lGODlhBwAGAIEAAP8AAAAAAAAAAAAAACH/C05FVFNDQVBFMi4wAwEAAAAh+QQACgAAACwAAAAABwAGAAAIDQABCBxIsKDBgwgNBgQAIfkEAQoAAQAsAAAAAAcABgCBAP8AAAAAAAAAAAAACA0AAQgcSLCgwYMIDQYEADs=");

    /// <summary>9×8.</summary>
    public static readonly byte[] WebPLossy = Convert.FromBase64String("UklGRi4AAABXRUJQVlA4ICIAAABwAQCdASoJAAgAAUAmJZQCdAFAAAD+/DeBV/fU6D4r4AAA");

    /// <summary>11×10.</summary>
    public static readonly byte[] WebPLossless = Convert.FromBase64String("UklGRh4AAABXRUJQVlA4TBEAAAAvCkACAAdQiirUo/+BiOh/AAA=");

    /// <summary>7×6, two frames.</summary>
    public static readonly byte[] AnimatedWebP = Convert.FromBase64String("UklGRsoAAABXRUJQVlA4WAoAAAACAAAABgAABQAAQU5JTQYAAAAAAAAAAABBTk1GSgAAAAAAAAAAAAYAAAUAAGQAAAJWUDggMgAAADABAJ0BKgcABgABQCYloAADcAD+8ut///mwP/bz/wR6Af//0uD//pcH//S4P/SkAAAAQU5NRkwAAAAAAAAAAAAGAAAFAABkAAAAVlA4IDQAAAA0AQCdASoHAAYAAAAmJaAAA3AA/ukiH//3nz//ufP/+58/6M///yn7//I4//8jj/5QIAAA");

    /// <summary>5000×1.</summary>
    public static readonly byte[] WidePng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAE4gAAAABCAAAAAAXehtUAAAAHElEQVR4nO3BMQEAAADCoPVPbQo/oAAAAACAuwETiQABvmKQDwAAAABJRU5ErkJggg==");
}
