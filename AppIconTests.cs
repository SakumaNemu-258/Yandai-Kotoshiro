using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Kotoshiro;

// Acceptance checks against the user's supplied artwork, including the native PE
// resources Windows Explorer reads rather than just the managed Form.Icon property.
internal static class AppIconTests
{
    private const string IconHash = "B3BE6538FDEAAF3F32A521E17B7831AE21230B4FA30C78390D6B8C85095480CD";
    private const string DarkImageHash = "31A8FED304F9464FA314E1C7C0DA7A539AB830E9A52BD4D682E4467BE064CE6F";
    private const string LightImageHash = "E4E76682CAFA12509397801D08C0E3347558828E72A60E16933B1C95A9FE5902";
    private const string BlackMarkHash = "7C1346ABA1ABAE36C7DB26D07CD9ABC4E1ABDBE8775B795A55CE49661B747699";
    private const string WhiteMarkHash = "5C5BE436545714DC5D9D4E1DA80F20499378CEAEF89881929A114005A8E2BD98";
    private static readonly int[] RequiredSizes = [16, 24, 32, 48, 64, 128, 256];
    private readonly record struct Frame(int Width, int Height, int Depth, byte[] Bytes);

    internal static List<string> Run()
    {
        byte[] source = Resource("Assets.AppIcon.ico");
        Check(Hash(source) == IconHash, "embedded ICO matches the white application icon exported from the supplied vector artwork");
        var frames = ParseFrames(source);
        Check(frames.Select(frame => frame.Width).SequenceEqual(RequiredSizes), "ICO contains the required 16/24/32/48/64/128/256 pixel frames");
        foreach (var frame in frames)
        {
            Check(frame.Width == frame.Height && frame.Depth == 32, "each ICO frame is square and 32-bit");
            Check(frame.Bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "each ICO frame is lossless PNG");
            using var stream = new MemoryStream(frame.Bytes);
            using var image = Image.FromStream(stream);
            Check(image.Width == frame.Width && image.Height == frame.Height, "decoded icon frame matches its directory size");
        }
        var results = new List<string>
        {
            "PASS: embedded Windows ICO matches the white application icon, with all seven 32-bit PNG resolutions (16 through 256 pixels)"
        };
        foreach (var asset in new[] { ("Assets.AppIcon.png", DarkImageHash), ("Assets.AppIcon.Light.png", LightImageHash) })
        {
            byte[] bytes = Resource(asset.Item1);
            Check(Hash(bytes) == asset.Item2, "embedded full icon artwork preserves the user's original bytes");
            using var stream = new MemoryStream(bytes);
            using var image = new Bitmap(stream);
            Check(image.Size == new Size(1024, 1024), "full icon artwork keeps its original resolution");
            int transparent = 0, visible = 0;
            for (int x = 0; x < image.Width; x++)
                for (int y = 0; y < image.Height; y++)
                {
                    byte alpha = image.GetPixel(x, y).A;
                    if (alpha == 0) transparent++;
                    if (alpha == 255) visible++;
                }
            Check(transparent == 42277 && visible > 1_000_000, "full icon artwork retains transparent rounded corners and visible artwork");
        }
        results.Add("PASS: both embedded full icon PNG variants preserve the user's source SHA256, 1024 pixel resolution, and transparent corners");
        VerifyTransparentMarks(results);
        return results;
    }

    private static void VerifyTransparentMarks(List<string> results)
    {
        byte[] blackBytes = Resource("Assets.HeaderMark.Black.png"), whiteBytes = Resource("Assets.HeaderMark.White.png");
        Check(Hash(blackBytes) == BlackMarkHash && Hash(whiteBytes) == WhiteMarkHash, "transparent header marks preserve the user's original source bytes");
        using var blackStream = new MemoryStream(blackBytes);
        using var whiteStream = new MemoryStream(whiteBytes);
        using var darkIconStream = new MemoryStream(Resource("Assets.AppIcon.png"));
        using var lightIconStream = new MemoryStream(Resource("Assets.AppIcon.Light.png"));
        using var black = new Bitmap(blackStream);
        using var white = new Bitmap(whiteStream);
        using var darkIcon = new Bitmap(darkIconStream);
        using var lightIcon = new Bitmap(lightIconStream);
        Check(black.Size == new Size(1024, 1024) && white.Size == black.Size, "transparent marks preserve the original artwork's canvas dimensions");
        int transparent = 0, visible = 0, opaque = 0;
        int left = black.Width, top = black.Height, right = -1, bottom = -1;
        for (int x = 0; x < black.Width; x++)
            for (int y = 0; y < black.Height; y++)
            {
                Color blackPixel = black.GetPixel(x, y), whitePixel = white.GetPixel(x, y);
                Check(blackPixel.A == whitePixel.A, "black and white marks have exactly the same alpha geometry");
                bool fullBlackStroke = lightIcon.GetPixel(x, y).ToArgb() == unchecked((int)0xFF111113);
                bool fullWhiteStroke = darkIcon.GetPixel(x, y).ToArgb() == unchecked((int)0xFFFFFFFF);
                Check(fullBlackStroke == (blackPixel.A == 255) && fullWhiteStroke == (whitePixel.A == 255),
                    "transparent marks retain the original full icon's fully opaque stroke geometry without deformation");
                if (blackPixel.A == 0) { transparent++; continue; }
                visible++;
                left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y);
                if (blackPixel.A == 255)
                {
                    opaque++;
                    Check(blackPixel.ToArgb() == unchecked((int)0xFF111113) && whitePixel.ToArgb() == unchecked((int)0xFFFFFFFF),
                        "fully opaque mark pixels use only the supplied black and white foreground colors");
                }
            }
        Check(transparent == 845080 && transparent > black.Width * black.Height * .8 && visible == 203496 && opaque == 201224,
            "header marks contain strokes on a mostly transparent canvas, with no opaque background tile");
        Check(Rectangle.FromLTRB(left, top, right + 1, bottom + 1) == new Rectangle(196, 228, 632, 568),
            "transparent marks preserve the original stroke bounding box and aspect ratio");
        results.Add("PASS: transparent black and white header marks are byte-identical to the supplied sources; over 80% of pixels are fully transparent, with no background tile");
        results.Add("PASS: both header marks share identical alpha geometry, match the original icon's opaque strokes, use only black/white foreground colors, and retain their 632x568 artwork bounds");
    }

    internal static List<string> VerifyPublishedExecutable(string path)
    {
        var sourceFrames = ParseFrames(Resource("Assets.AppIcon.ico"));
        IntPtr module = LoadLibraryExW(Path.GetFullPath(path), IntPtr.Zero, 0x00000002);
        Check(module != IntPtr.Zero, "published executable can be opened for Windows resources");
        try
        {
            var groups = new List<List<Frame>>();
            Exception? callbackError = null;
            EnumResourceNameProc callback = (loaded, type, name, parameter) =>
            {
                try
                {
                    byte[] group = NativeResource(loaded, type, name);
                    Check(group.Length >= 6 && Read16(group, 0) == 0 && Read16(group, 2) == 1, "PE group icon header is valid");
                    int count = Read16(group, 4);
                    Check(group.Length >= 6 + count * 14, "PE group icon directory is complete");
                    var frames = new List<Frame>();
                    for (int i = 0; i < count; i++)
                    {
                        int entry = 6 + i * 14;
                        int width = group[entry] == 0 ? 256 : group[entry];
                        int height = group[entry + 1] == 0 ? 256 : group[entry + 1];
                        byte[] frame = NativeResource(loaded, new IntPtr(3), new IntPtr(Read16(group, entry + 12)));
                        Check(frame.Length == Read32(group, entry + 8), "PE icon size matches its group directory");
                        frames.Add(new(width, height, Read16(group, entry + 6), frame));
                    }
                    groups.Add(frames);
                    return true;
                }
                catch (Exception error) { callbackError = error; return false; }
            };
            bool enumerated = EnumResourceNamesW(module, new IntPtr(14), callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            if (callbackError != null) throw callbackError;
            Check(enumerated && groups.Count > 0, "published executable contains a native icon group");
            var first = groups[0];
            Check(first.Count == sourceFrames.Count, "Windows' first executable icon group contains every supplied size");
            for (int i = 0; i < first.Count; i++)
            {
                Check(first[i].Width == sourceFrames[i].Width && first[i].Height == sourceFrames[i].Height && first[i].Depth == sourceFrames[i].Depth,
                    "native executable icon frame dimensions and depth match the white application ICO");
                Check(first[i].Bytes.AsSpan().SequenceEqual(sourceFrames[i].Bytes), "native executable icon frame is byte-identical to the white application ICO PNG");
            }
            return [$"PASS: published EXE's first RT_GROUP_ICON and all {first.Count} RT_ICON frames are byte-identical to the white application ICO; Windows Explorer reads this native resource"];
        }
        finally { FreeLibrary(module); }
    }

    internal static string VerifyForPreview(Form form)
    {
        Check(form.IsHandleCreated && form.Icon != null, "preview form has a window handle and an application icon");
        var registered = new[]
        {
            ("large", SendMessageW(form.Handle, 0x007F, new IntPtr(1), IntPtr.Zero)),
            ("small", SendMessageW(form.Handle, 0x007F, IntPtr.Zero, IntPtr.Zero))
        };
        byte[] source = Resource("Assets.AppIcon.ico");
        foreach (var item in registered)
        {
            Check(item.Item2 != IntPtr.Zero, $"{form.GetType().Name} registers its native {item.Item1} icon");
            // The window owns these handles. FromHandle wrappers never destroy them.
            using var nativeIcon = Icon.FromHandle(item.Item2);
            using var actual = nativeIcon.ToBitmap();
            using var stream = new MemoryStream(source);
            using var expectedIcon = new Icon(stream, actual.Size);
            using var expected = expectedIcon.ToBitmap();
            Check(actual.Size == expected.Size, "native window icon has the expected size");
            for (int x = 0; x < actual.Width; x++)
                for (int y = 0; y < actual.Height; y++)
                    Check(actual.GetPixel(x, y).ToArgb() == expected.GetPixel(x, y).ToArgb(), "native window icon pixels match the white application icon");
        }
        return $"PASS: {form.GetType().Name} WM_GETICON returns both large and small native icons whose rendered pixels match the white application ICO";
    }

    private static byte[] Resource(string suffix)
    {
        Assembly assembly = typeof(AppIconTests).Assembly;
        string name = assembly.GetManifestResourceNames().Single(resource => resource.EndsWith(suffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var bytes = new MemoryStream(); stream.CopyTo(bytes); return bytes.ToArray();
    }

    private static List<Frame> ParseFrames(byte[] bytes)
    {
        Check(bytes.Length >= 6 && Read16(bytes, 0) == 0 && Read16(bytes, 2) == 1, "ICO header is valid");
        int count = Read16(bytes, 4);
        Check(bytes.Length >= 6 + count * 16, "ICO directory is complete");
        var frames = new List<Frame>();
        for (int i = 0; i < count; i++)
        {
            int entry = 6 + i * 16;
            int width = bytes[entry] == 0 ? 256 : bytes[entry];
            int height = bytes[entry + 1] == 0 ? 256 : bytes[entry + 1];
            int length = checked((int)Read32(bytes, entry + 8)), offset = checked((int)Read32(bytes, entry + 12));
            Check(offset >= 6 + count * 16 && length > 8 && offset <= bytes.Length - length, "ICO frame is inside the resource");
            frames.Add(new(width, height, Read16(bytes, entry + 6), bytes.AsSpan(offset, length).ToArray()));
        }
        return frames;
    }

    private static byte[] NativeResource(IntPtr module, IntPtr type, IntPtr name)
    {
        IntPtr found = FindResourceW(module, name, type);
        Check(found != IntPtr.Zero, "native icon resource exists");
        int length = checked((int)SizeofResource(module, found));
        IntPtr address = LockResource(LoadResource(module, found));
        Check(address != IntPtr.Zero && length > 0, "native icon resource can be loaded");
        byte[] bytes = new byte[length]; Marshal.Copy(address, bytes, 0, length); return bytes;
    }

    private static ushort Read16(byte[] bytes, int offset) => BitConverter.ToUInt16(bytes, offset);
    private static uint Read32(byte[] bytes, int offset) => BitConverter.ToUInt32(bytes, offset);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("FAIL: " + message); }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate bool EnumResourceNameProc(IntPtr module, IntPtr type, IntPtr name, IntPtr parameter);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FreeLibrary(IntPtr module);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool EnumResourceNamesW(IntPtr module, IntPtr type, EnumResourceNameProc callback, IntPtr parameter);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr FindResourceW(IntPtr module, IntPtr name, IntPtr type);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint SizeofResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")] private static extern IntPtr LockResource(IntPtr resource);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageW(IntPtr handle, int message, IntPtr wparam, IntPtr lparam);
}
