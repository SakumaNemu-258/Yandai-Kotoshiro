namespace Kotoshiro;

public static class Branding
{
    public const string DisplayName = "言代Kotoshiro";
    public const string Identity = "Kotoshiro";
    private static readonly Lazy<Icon> ApplicationIconAsset = new(() =>
    {
        using var stream = typeof(Branding).Assembly.GetManifestResourceStream("Kotoshiro.Assets.AppIcon.ico")
            ?? throw new InvalidOperationException("Missing application icon resource.");
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    });
    private sealed record HeaderArtwork(Bitmap Image, Rectangle SourceBounds);
    private static readonly Lazy<HeaderArtwork> BlackMarkAsset = new(() => LoadHeaderMark("Kotoshiro.Assets.HeaderMark.Black.png"));
    private static readonly Lazy<HeaderArtwork> WhiteMarkAsset = new(() => LoadHeaderMark("Kotoshiro.Assets.HeaderMark.White.png"));
    public static Icon ApplicationIcon => ApplicationIconAsset.Value;
    private static HeaderArtwork CurrentHeaderMark => Theme.IsDark ? WhiteMarkAsset.Value : BlackMarkAsset.Value;
    public static Image HeaderIcon => CurrentHeaderMark.Image;
    public static Rectangle HeaderIconSourceBounds => CurrentHeaderMark.SourceBounds;
    private static HeaderArtwork LoadHeaderMark(string name)
    {
        var image = LoadIconBitmap(name);
        int left = image.Width, top = image.Height, right = -1, bottom = -1;
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
                if (image.GetPixel(x, y).A != 0)
                {
                    left = Math.Min(left, x); top = Math.Min(top, y);
                    right = Math.Max(right, x); bottom = Math.Max(bottom, y);
                }
        if (right < left) { image.Dispose(); throw new InvalidOperationException("Header mark has no visible artwork."); }
        var source = Rectangle.FromLTRB(Math.Max(0, left - 2), Math.Max(0, top - 2), Math.Min(image.Width, right + 3), Math.Min(image.Height, bottom + 3));
        return new HeaderArtwork(image, source);
    }
    private static Bitmap LoadIconBitmap(string name)
    {
        using var stream = typeof(Branding).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("Missing header icon resource.");
        using var image = Image.FromStream(stream);
        return new Bitmap(image);
    }

    public static string ResolveDataRoot(string localAppData)
    {
        string current = Path.Combine(Path.GetFullPath(localAppData), Identity);
        // Reuse old installations without moving their encrypted settings or login files.
        string legacy = Path.Combine(Path.GetFullPath(localAppData), "TingyiLive");
        return !Directory.Exists(current) && Directory.Exists(legacy) ? legacy : current;
    }

    public static string ModelRuntimeDirectory(string modelFolder)
    {
        if (string.IsNullOrWhiteSpace(modelFolder)) return "";
        string folder = Path.GetFullPath(modelFolder);
        // Retain installed GPU components and caches in existing model folders.
        string legacy = Path.Combine(folder, ".tingyi");
        return Directory.Exists(legacy) ? legacy : Path.Combine(folder, ".kotoshiro");
    }
}
