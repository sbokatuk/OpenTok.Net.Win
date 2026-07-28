using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using OpenTok;

namespace OpenTok.Net.Win.Rendering;

/// <summary>
/// An <see cref="IVideoRenderer"/> that draws into a WinUI <see cref="WriteableBitmap"/>.
/// </summary>
/// <remarks>
/// <para>
/// The gap this fills: <c>OpenTok.Client</c> ships <c>WPFVideoRenderer</c> and
/// <c>WinFormsVideoRenderer</c>, and neither works in WinUI 3 — which is what .NET MAUI uses on
/// Windows. Its <c>netstandard2.0</c> asset, the one a modern .NET app resolves, contains no
/// renderer at all. So a WinUI or MAUI app can connect, publish and subscribe out of the box and
/// still have nowhere to put the picture.
/// </para>
/// <para>
/// Hand an instance to <c>Publisher.Builder.Renderer</c> or <c>Subscriber.Builder.Renderer</c>, and
/// bind <see cref="Source"/> to an <c>Image</c> — or just use <see cref="OpenTokVideoView"/>, which
/// does both.
/// </para>
/// <para>
/// Threading: <see cref="RenderFrame"/> is called on an SDK thread, and both
/// <see cref="WriteableBitmap"/> and its pixel buffer are UI-thread affine. So the conversion runs
/// on the calling thread — where the frame is still valid — and only the finished BGRA bytes are
/// marshalled. See <see cref="RenderFrame"/> for why that split matters.
/// </para>
/// </remarks>
public sealed class OpenTokVideoRenderer : IVideoRenderer, IDisposable
{
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly object _gate = new();

    // Converted BGRA, double-buffered. The SDK thread writes _bgra; the UI thread reads _front.
    // Both are reused across frames: at 720p30 a fresh allocation per frame is 110 MB/s of garbage.
    //
    // Two buffers rather than one because the swap has to happen under the lock while the write to
    // the bitmap does not. Holding the lock across that write would stall the capture thread on the
    // UI thread's progress; dropping the lock without swapping would let the next frame overwrite
    // the buffer midway through the write and tear the picture across two frames.
    private byte[] _bgra = [];
    private byte[] _front = [];
    private int _width;
    private int _height;

    // Scratch I420 planes, allocated only for the frame formats that need converting first. Stays
    // empty for the I420 frames that are the normal case.
    private byte[] _i420 = [];

    private bool _frameReady;
    private bool _renderQueued;
    private bool _disposed;

    /// <summary>Creates a renderer that marshals to the given dispatcher queue.</summary>
    /// <param name="dispatcherQueue">
    /// The UI thread's queue — <c>DispatcherQueue.GetForCurrentThread()</c> from the thread that
    /// owns the <c>Image</c> this will feed.
    /// </param>
    public OpenTokVideoRenderer(DispatcherQueue dispatcherQueue)
    {
        ArgumentNullException.ThrowIfNull(dispatcherQueue);
        _dispatcherQueue = dispatcherQueue;
    }

    /// <summary>
    /// The bitmap to display, or <see langword="null"/> until the first frame arrives. UI thread only.
    /// </summary>
    /// <remarks>
    /// Replaced, not resized, when the stream's dimensions change — <see cref="WriteableBitmap"/>
    /// fixes its size at construction. Watch <see cref="SourceChanged"/> rather than caching this.
    /// </remarks>
    public WriteableBitmap? Source { get; private set; }

    /// <summary>Raised on the UI thread when <see cref="Source"/> is created or replaced.</summary>
    public event EventHandler? SourceChanged;

    /// <summary>
    /// Called by the SDK when a frame is available. Converts it and schedules a repaint.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The conversion deliberately happens here, on the SDK's thread, rather than being deferred to
    /// the UI thread with the frame in hand. A <c>VideoFrame</c>'s plane pointers are only valid
    /// for the duration of this call — the SDK reclaims the buffer on return — so passing the frame
    /// to another thread reads freed memory, intermittently, under load.
    /// </para>
    /// <para>
    /// Repaints are coalesced rather than queued: if the UI thread has not yet drawn the previous
    /// frame, this one overwrites it and no second callback is scheduled. Under load that drops
    /// frames, which is the right failure — the alternative is an unbounded queue of stale frames
    /// and video that falls further behind the longer the call lasts.
    /// </para>
    /// </remarks>
    /// <param name="frame">The frame to render. Not retained beyond this call.</param>
    public void RenderFrame(VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (_disposed)
        {
            return;
        }

        var width = frame.Width;
        var height = frame.Height;

        if (width <= 0 || height <= 0)
        {
            return;
        }

        lock (_gate)
        {
            EnsureBuffers(width, height);
            Convert(frame, width, height);
            _frameReady = true;

            if (_renderQueued)
            {
                return;
            }

            _renderQueued = true;
        }

        if (!_dispatcherQueue.TryEnqueue(Present))
        {
            // The UI thread is gone — during shutdown, or if the view was torn down while a frame
            // was in flight. Clear the flag so a later frame is not blocked by a callback that will
            // never run.
            lock (_gate)
            {
                _renderQueued = false;
            }
        }
    }

    private void Convert(VideoFrame frame, int width, int height)
    {
        var chromaWidth = (width + 1) / 2;
        var chromaHeight = (height + 1) / 2;

        if (frame.PixelFormat == PixelFormat.FormatYuv420p && frame.NumberOfPlanes >= 3)
        {
            ConvertPlanes(
                Plane(frame, 0), frame.GetPlaneStride(0),
                Plane(frame, 1), frame.GetPlaneStride(1),
                Plane(frame, 2), frame.GetPlaneStride(2),
                width, height, frame.IsMirrored);

            return;
        }

        // Anything else — NV12 from some hardware capture paths, BGRA from a screen source — goes
        // through the SDK's own converter first. ConvertInPlace is documented to support exactly
        // one destination format, YUV420p, which is precisely the one wanted here.
        var lumaSize = width * height;
        var chromaSize = chromaWidth * chromaHeight;
        var required = lumaSize + (chromaSize * 2);

        if (_i420.Length < required)
        {
            _i420 = new byte[required];
        }

        ConvertViaSdk(frame, width, height, chromaWidth, lumaSize, chromaSize);
    }

    private unsafe void ConvertViaSdk(
        VideoFrame frame, int width, int height, int chromaWidth, int lumaSize, int chromaSize)
    {
        fixed (byte* buffer = _i420)
        {
            var planes = new[]
            {
                (IntPtr)buffer,
                (IntPtr)(buffer + lumaSize),
                (IntPtr)(buffer + lumaSize + chromaSize),
            };

            var strides = new[] { width, chromaWidth, chromaWidth };

            frame.ConvertInPlace(PixelFormat.FormatYuv420p, planes, strides);

            ConvertPlanes(
                new ReadOnlySpan<byte>(buffer, lumaSize), width,
                new ReadOnlySpan<byte>(buffer + lumaSize, chromaSize), chromaWidth,
                new ReadOnlySpan<byte>(buffer + lumaSize + chromaSize, chromaSize), chromaWidth,
                width, height, frame.IsMirrored);
        }
    }

    private void ConvertPlanes(
        ReadOnlySpan<byte> y, int yStride,
        ReadOnlySpan<byte> u, int uStride,
        ReadOnlySpan<byte> v, int vStride,
        int width, int height, bool mirrored) =>
        I420Converter.ToBgra8(
            y, yStride, u, uStride, v, vStride,
            width, height, _bgra, width * I420Converter.BytesPerPixel, mirrored);

    private static unsafe ReadOnlySpan<byte> Plane(VideoFrame frame, int index) =>
        new((void*)frame.GetPlane(index), frame.GetPlaneSize(index));

    private void EnsureBuffers(int width, int height)
    {
        if (_width == width && _height == height && _bgra.Length > 0)
        {
            return;
        }

        _width = width;
        _height = height;

        var length = I420Converter.MinimumDestinationLength(width, height);
        _bgra = new byte[length];
        _front = new byte[length];

        // Not resized here: WriteableBitmap's dimensions are fixed at construction and it has to be
        // built on the UI thread. Present notices the mismatch and replaces it.
    }

    private void Present()
    {
        byte[] pixels;
        int width, height, length;

        lock (_gate)
        {
            _renderQueued = false;

            if (_disposed || !_frameReady)
            {
                return;
            }

            _frameReady = false;
            width = _width;
            height = _height;
            length = I420Converter.MinimumDestinationLength(width, height);

            // Swapped, not copied. Both buffers are the same size, so this hands the finished frame
            // to the UI thread and gives the capture thread the older buffer to fill next — no
            // allocation, and the lock is held only for two reference assignments.
            (_bgra, _front) = (_front, _bgra);
            pixels = _front;
        }

        if (Source is null || Source.PixelWidth != width || Source.PixelHeight != height)
        {
            Source = new WriteableBitmap(width, height);
            SourceChanged?.Invoke(this, EventArgs.Empty);
        }

        using (var stream = Source.PixelBuffer.AsStream())
        {
            stream.Write(pixels, 0, length);
        }

        Source.Invalidate();
    }

    /// <summary>Stops rendering. Safe to call from any thread, and more than once.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _frameReady = false;

            // All three, not just the one being written. At 1080p these are 8 MB each, and a
            // renderer is disposed per subscriber — a multiparty call that leaked the front buffer
            // of every tile that left would grow for the length of the call.
            _bgra = [];
            _front = [];
            _i420 = [];

            // _width/_height reset so a renderer that somehow sees another frame rebuilds its
            // buffers rather than writing into the empty arrays above.
            _width = 0;
            _height = 0;
        }
    }
}
