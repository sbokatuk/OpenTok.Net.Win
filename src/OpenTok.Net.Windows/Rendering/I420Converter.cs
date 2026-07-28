namespace OpenTok.Net.Windows.Rendering;

/// <summary>
/// Converts I420 (planar YUV 4:2:0) video frames to BGRA8, the format a WinUI
/// <c>WriteableBitmap</c> expects.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the conversion cannot be delegated. <c>VideoFrame.ConvertInPlace</c> looks
/// like the obvious answer, but its own documentation says it "currently only works with
/// PixelFormat.FormatYuv420p" as the <em>destination</em> — so it converts to I420, not from it.
/// The Vonage Windows SDK's WPF and WinForms renderers do this step internally and are not
/// reusable from WinUI, which is the whole reason this package exists.
/// </para>
/// <para>
/// Deliberately free of every WinUI, Windows and OpenTok type — it takes spans and integers and
/// nothing else. That is not incidental tidiness: it lets
/// <c>tests/OpenTok.Net.Windows.UnitTests</c> compile this exact source file into a plain
/// <c>net9.0</c> assembly and test it on any operating system. The rest of this package cannot be
/// built or run outside Windows, so this is the one place where the arithmetic that is easiest to
/// get wrong can actually be verified against known values.
/// </para>
/// <para>
/// BT.601 limited range ("video range", Y in 16-235, chroma in 16-240), which is what WebRTC
/// delivers. Using full-range coefficients here is the classic mistake and does not look obviously
/// broken — it produces slightly washed-out video that survives review.
/// </para>
/// </remarks>
public static class I420Converter
{
    /// <summary>Bytes per pixel in the BGRA8 destination.</summary>
    public const int BytesPerPixel = 4;

    /// <summary>
    /// Converts one I420 frame to BGRA8.
    /// </summary>
    /// <param name="y">The luma plane.</param>
    /// <param name="yStride">Bytes per row in <paramref name="y"/>. May exceed <paramref name="width"/>.</param>
    /// <param name="u">The Cb (blue-difference) chroma plane, at half resolution in both axes.</param>
    /// <param name="uStride">Bytes per row in <paramref name="u"/>.</param>
    /// <param name="v">The Cr (red-difference) chroma plane, at half resolution in both axes.</param>
    /// <param name="vStride">Bytes per row in <paramref name="v"/>.</param>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="destination">The BGRA8 destination, at least <paramref name="destinationStride"/> * <paramref name="height"/> bytes.</param>
    /// <param name="destinationStride">Bytes per row in <paramref name="destination"/>.</param>
    /// <param name="mirrored">
    /// When true, each row is written right to left. Set from <c>VideoFrame.IsMirrored</c>, which a
    /// local camera preview normally reports true so the user sees themselves as in a mirror.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">A dimension or stride is not usable.</exception>
    /// <exception cref="ArgumentException">A plane or the destination is too small for the stated dimensions.</exception>
    public static void ToBgra8(
        ReadOnlySpan<byte> y, int yStride,
        ReadOnlySpan<byte> u, int uStride,
        ReadOnlySpan<byte> v, int vStride,
        int width, int height,
        Span<byte> destination, int destinationStride,
        bool mirrored)
    {
        // Rounded up, not down: a 1x1 frame still has one chroma sample, and the last column of an
        // odd-width frame still needs one to read. Truncating division here reads out of bounds on
        // every odd dimension, which is rare enough in practice to ship broken.
        var chromaWidth = (width + 1) / 2;
        var chromaHeight = (height + 1) / 2;

        Validate(
            y.Length, yStride, u.Length, uStride, v.Length, vStride,
            width, height, chromaWidth, chromaHeight,
            destination.Length, destinationStride);

        for (var row = 0; row < height; row++)
        {
            var lumaRow = y.Slice(row * yStride, width);

            // Integer halving, unlike the width rounding above: rows 0 and 1 both read chroma row
            // 0. That is what 4:2:0 subsampling means vertically.
            var chromaOffset = (row / 2);
            var cbRow = u.Slice(chromaOffset * uStride, chromaWidth);
            var crRow = v.Slice(chromaOffset * vStride, chromaWidth);

            var target = destination.Slice(row * destinationStride, width * BytesPerPixel);

            for (var column = 0; column < width; column++)
            {
                var luma = 298 * (lumaRow[column] - 16);
                var cb = cbRow[column / 2] - 128;
                var cr = crRow[column / 2] - 128;

                // +128 before the >>8 is rounding, not a magic number: it turns a truncating shift
                // into a nearest-integer one.
                var red = (luma + (409 * cr) + 128) >> 8;
                var green = (luma - (100 * cb) - (208 * cr) + 128) >> 8;
                var blue = (luma + (516 * cb) + 128) >> 8;

                var offset = (mirrored ? width - 1 - column : column) * BytesPerPixel;

                target[offset] = Clamp(blue);
                target[offset + 1] = Clamp(green);
                target[offset + 2] = Clamp(red);

                // Opaque. WriteableBitmap treats its buffer as premultiplied BGRA, and at alpha 255
                // premultiplication is the identity, so no scaling of the colour channels is needed.
                target[offset + 3] = 255;
            }
        }
    }

    /// <summary>
    /// The smallest destination buffer <see cref="ToBgra8"/> can write a frame of this size into,
    /// assuming a tightly packed stride.
    /// </summary>
    public static int MinimumDestinationLength(int width, int height) =>
        checked(width * BytesPerPixel * height);

    private static byte Clamp(int value) =>
        value < 0 ? (byte)0 : value > 255 ? (byte)255 : (byte)value;

    private static void Validate(
        int yLength, int yStride, int uLength, int uStride, int vLength, int vStride,
        int width, int height, int chromaWidth, int chromaHeight,
        int destinationLength, int destinationStride)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        // A stride shorter than its row means the caller has mixed up a plane's dimensions, and
        // every read after the first row would silently land on the wrong pixels rather than fail.
        ArgumentOutOfRangeException.ThrowIfLessThan(yStride, width);
        ArgumentOutOfRangeException.ThrowIfLessThan(uStride, chromaWidth);
        ArgumentOutOfRangeException.ThrowIfLessThan(vStride, chromaWidth);
        ArgumentOutOfRangeException.ThrowIfLessThan(destinationStride, width * BytesPerPixel);

        // The final row only needs its own width, not a full stride — a plane whose last row is not
        // padded out is legal and common, and demanding stride * height would reject valid frames.
        RequirePlane(yLength, yStride, height, width, "luma");
        RequirePlane(uLength, uStride, chromaHeight, chromaWidth, "Cb chroma");
        RequirePlane(vLength, vStride, chromaHeight, chromaWidth, "Cr chroma");
        RequirePlane(destinationLength, destinationStride, height, width * BytesPerPixel, "destination");
    }

    private static void RequirePlane(int length, int stride, int rows, int lastRowBytes, string name)
    {
        var required = ((rows - 1) * stride) + lastRowBytes;
        if (length < required)
        {
            throw new ArgumentException(
                $"The {name} plane is {length} bytes; {rows} rows at a stride of {stride} need {required}.",
                name);
        }
    }
}
