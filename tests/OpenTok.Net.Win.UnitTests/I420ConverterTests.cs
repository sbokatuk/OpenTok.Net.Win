using OpenTok.Net.Win.Rendering;
using Xunit;

namespace OpenTok.Net.Win.UnitTests;

/// <summary>
/// Checks the I420 to BGRA8 conversion against values worked out independently of the code.
/// </summary>
/// <remarks>
/// The expectations here are BT.601 <em>limited range</em> — Y in 16-235 — because that is what
/// WebRTC delivers. The failure this guards against is not a crash: full-range coefficients produce
/// a picture that is merely a bit flat, which is exactly the kind of wrong that ships.
/// </remarks>
public class I420ConverterTests
{
    private const int B = 0, G = 1, R = 2, A = 3;

    [Theory]
    // Y=16 is limited-range black, not Y=0. Neutral chroma is 128 on both planes.
    [InlineData(16, 128, 128, 0, 0, 0)]
    // Y=235 is limited-range white.
    [InlineData(235, 128, 128, 255, 255, 255)]
    // The three primaries, at their standard BT.601 limited-range encodings.
    //
    // Green decodes to a blue channel of 1 rather than 0, and that is correct rather than a
    // tolerance being papered over: the fixed-point coefficients are an approximation and are not
    // an exact inverse of the encoder, so a primary can land one step off. Pinned to the value the
    // arithmetic actually produces, so that a real change to the coefficients still fails here.
    [InlineData(81, 90, 240, 255, 0, 0)]
    [InlineData(145, 54, 34, 0, 255, 1)]
    [InlineData(41, 240, 110, 0, 0, 255)]
    public void Converts_a_uniform_frame_to_its_known_colour(
        byte y, byte u, byte v, byte expectedRed, byte expectedGreen, byte expectedBlue)
    {
        const int width = 4, height = 4;
        var destination = Convert(Uniform(y, u, v, width, height), width, height);

        for (var pixel = 0; pixel < width * height; pixel++)
        {
            var offset = pixel * 4;
            Assert.Equal(expectedBlue, destination[offset + B]);
            Assert.Equal(expectedGreen, destination[offset + G]);
            Assert.Equal(expectedRed, destination[offset + R]);
            Assert.Equal(255, destination[offset + A]);
        }
    }

    [Fact]
    public void Writes_an_opaque_alpha_channel()
    {
        // WriteableBitmap treats its buffer as premultiplied BGRA. A zero alpha here renders as a
        // fully transparent video view — a black rectangle in most layouts, which reads as "the
        // stream never arrived" rather than as a rendering bug.
        const int width = 8, height = 8;
        var destination = Convert(Uniform(120, 100, 150, width, height), width, height);

        for (var pixel = 0; pixel < width * height; pixel++)
        {
            Assert.Equal(255, destination[(pixel * 4) + A]);
        }
    }

    [Fact]
    public void Reads_each_row_at_its_stride_rather_than_its_width()
    {
        // Padded planes are the normal case from WebRTC, not an edge case. A converter that assumes
        // stride == width drifts by the padding on every row and produces a diagonally skewed
        // picture.
        const int width = 3, height = 2;
        const int yStride = 16, chromaStride = 9;

        var y = new byte[yStride * height];
        var u = new byte[chromaStride * 1];
        var v = new byte[chromaStride * 1];

        // Row 0 black, row 1 white — only distinguishable if the stride is honoured.
        y.AsSpan(0, width).Fill(16);
        y.AsSpan(yStride, width).Fill(235);
        u.AsSpan().Fill(128);
        v.AsSpan().Fill(128);

        // Fill the padding with a value that would be visible if it were read as pixel data.
        for (var i = width; i < yStride; i++)
        {
            y[i] = 200;
        }

        var destination = new byte[I420Converter.MinimumDestinationLength(width, height)];
        I420Converter.ToBgra8(y, yStride, u, chromaStride, v, chromaStride,
            width, height, destination, width * 4, mirrored: false);

        for (var column = 0; column < width; column++)
        {
            Assert.Equal(0, destination[(column * 4) + R]);
            Assert.Equal(255, destination[(((width) + column) * 4) + R]);
        }
    }

    [Fact]
    public void Mirrors_each_row_when_the_frame_says_it_is_mirrored()
    {
        // A local camera preview reports IsMirrored so the user sees themselves as in a mirror.
        const int width = 4, height = 2;
        var frame = Uniform(16, 128, 128, width, height);

        // Make the leftmost column white so the flip is observable.
        for (var row = 0; row < height; row++)
        {
            frame.Y[row * width] = 235;
        }

        var normal = Convert(frame, width, height, mirrored: false);
        var mirrored = Convert(frame, width, height, mirrored: true);

        for (var row = 0; row < height; row++)
        {
            var rowStart = row * width * 4;
            Assert.Equal(255, normal[rowStart + R]);
            Assert.Equal(0, normal[rowStart + ((width - 1) * 4) + R]);

            Assert.Equal(0, mirrored[rowStart + R]);
            Assert.Equal(255, mirrored[rowStart + ((width - 1) * 4) + R]);
        }
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(5, 1)]
    [InlineData(1, 5)]
    [InlineData(7, 9)]
    public void Handles_odd_dimensions_without_reading_past_a_chroma_plane(int width, int height)
    {
        // Chroma planes are ceil(w/2) x ceil(h/2). Truncating that division leaves the last column
        // and row of an odd-sized frame reading out of bounds — which throws here rather than
        // silently returning garbage, but only if the rounding is right.
        var destination = Convert(Uniform(150, 110, 130, width, height), width, height);

        Assert.Equal(I420Converter.MinimumDestinationLength(width, height), destination.Length);
        Assert.All(Enumerable.Range(0, width * height), pixel =>
            Assert.Equal(255, destination[(pixel * 4) + A]));
    }

    [Fact]
    public void Rejects_a_destination_that_is_too_small()
    {
        const int width = 16, height = 16;
        var frame = Uniform(16, 128, 128, width, height);
        var tooSmall = new byte[I420Converter.MinimumDestinationLength(width, height) - 1];

        var error = Assert.Throws<ArgumentException>(() =>
            I420Converter.ToBgra8(frame.Y, width, frame.U, (width + 1) / 2, frame.V, (width + 1) / 2,
                width, height, tooSmall, width * 4, mirrored: false));

        Assert.Contains("destination", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_a_stride_shorter_than_its_row()
    {
        const int width = 16, height = 16;
        var frame = Uniform(16, 128, 128, width, height);
        var destination = new byte[I420Converter.MinimumDestinationLength(width, height)];

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            I420Converter.ToBgra8(frame.Y, width - 1, frame.U, (width + 1) / 2, frame.V, (width + 1) / 2,
                width, height, destination, width * 4, mirrored: false));
    }

    [Fact]
    public void Accepts_a_final_row_that_is_not_padded_out_to_a_full_stride()
    {
        // A plane whose last row stops at its width rather than its stride is legal and common.
        // Demanding stride * height would reject frames that are perfectly well formed.
        const int width = 4, height = 3, stride = 12;

        var y = new byte[((height - 1) * stride) + width];
        var chromaStride = 6;
        var chromaHeight = (height + 1) / 2;
        var u = new byte[((chromaHeight - 1) * chromaStride) + ((width + 1) / 2)];
        var v = new byte[u.Length];

        y.AsSpan().Fill(16);
        u.AsSpan().Fill(128);
        v.AsSpan().Fill(128);

        var destination = new byte[I420Converter.MinimumDestinationLength(width, height)];

        I420Converter.ToBgra8(y, stride, u, chromaStride, v, chromaStride,
            width, height, destination, width * 4, mirrored: false);

        Assert.Equal(255, destination[A]);
    }

    private sealed record Frame(byte[] Y, byte[] U, byte[] V);

    private static Frame Uniform(byte y, byte u, byte v, int width, int height)
    {
        var chromaWidth = (width + 1) / 2;
        var chromaHeight = (height + 1) / 2;

        var luma = new byte[width * height];
        var cb = new byte[chromaWidth * chromaHeight];
        var cr = new byte[chromaWidth * chromaHeight];

        luma.AsSpan().Fill(y);
        cb.AsSpan().Fill(u);
        cr.AsSpan().Fill(v);

        return new Frame(luma, cb, cr);
    }

    private static byte[] Convert(Frame frame, int width, int height, bool mirrored = false)
    {
        var chromaWidth = (width + 1) / 2;
        var destination = new byte[I420Converter.MinimumDestinationLength(width, height)];

        I420Converter.ToBgra8(
            frame.Y, width, frame.U, chromaWidth, frame.V, chromaWidth,
            width, height, destination, width * 4, mirrored);

        return destination;
    }
}
