using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FocusFence.Helpers;

/// <summary>
/// Generates a subtle film-grain noise texture to overlay on Zone backgrounds.
/// Per PART 2A: eliminates the "plastic" feel of flat semitransparent surfaces.
/// 
/// Usage: NoiseTextureHelper.CreateNoiseBrush() returns an ImageBrush 
/// that tiles a 256×256 random gray-noise pattern at Opacity=0.025.
/// </summary>
public static class NoiseTextureHelper
{
    private static ImageBrush? _cachedBrush;

    /// <summary>
    /// Returns a tiling ImageBrush with noise texture.
    /// Cached — only generates once per app lifetime.
    /// </summary>
    public static ImageBrush CreateNoiseBrush(double opacity = 0.025)
    {
        _cachedBrush ??= BuildBrush();
        return new ImageBrush
        {
            ImageSource = _cachedBrush.ImageSource,
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 256, 256),
            ViewportUnits = BrushMappingMode.Absolute,
            Opacity = opacity
        };
    }

    /// <summary>
    /// Returns the raw noise BitmapSource (256×256, Gray8).
    /// </summary>
    public static BitmapSource GenerateNoiseBitmap(int size = 256)
    {
        var wb = new WriteableBitmap(size, size, 96, 96, PixelFormats.Gray8, null);
        var pixels = new byte[size * size];
        var rng = new Random();
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = (byte)rng.Next(100, 156); // mid-gray noise
        wb.WritePixels(new Int32Rect(0, 0, size, size), pixels, size, 0);
        wb.Freeze(); // freeze for cross-thread use
        return wb;
    }

    private static ImageBrush BuildBrush()
    {
        var bitmap = GenerateNoiseBitmap();
        return new ImageBrush
        {
            ImageSource = bitmap,
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 256, 256),
            ViewportUnits = BrushMappingMode.Absolute,
            Opacity = 0.025
        };
    }
}
