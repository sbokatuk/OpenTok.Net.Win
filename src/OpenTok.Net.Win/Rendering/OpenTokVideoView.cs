using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace OpenTok.Net.Win.Rendering;

/// <summary>
/// A WinUI control that displays one OpenTok video stream.
/// </summary>
/// <remarks>
/// <para>
/// The whole of the usual setup:
/// </para>
/// <code>
/// var view = new OpenTokVideoView();
/// var publisher = new Publisher.Builder(Context.Instance) { Renderer = view.Renderer }.Build();
/// </code>
/// <para>
/// Built in code rather than as a templated control with a <c>generic.xaml</c>. There is exactly
/// one element in it, an <c>Image</c>, so a template would add a resource dictionary, a
/// <c>DefaultStyleKey</c> and a part-name contract without making anything more customisable —
/// <see cref="Stretch"/> is the only knob a template would have exposed.
/// </para>
/// </remarks>
public sealed class OpenTokVideoView : Grid, IDisposable
{
    private readonly Image _image;
    private bool _disposed;

    /// <summary>Creates a view bound to the current thread's dispatcher queue.</summary>
    /// <exception cref="InvalidOperationException">Constructed off a UI thread.</exception>
    public OpenTokVideoView()
    {
        var dispatcherQueue = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException(
                $"{nameof(OpenTokVideoView)} must be created on a UI thread — the thread it runs " +
                "on becomes the one its bitmap is updated from.");

        _image = new Image { Stretch = Stretch.Uniform };
        Children.Add(_image);

        Renderer = new OpenTokVideoRenderer(dispatcherQueue);
        Renderer.SourceChanged += OnSourceChanged;
    }

    /// <summary>
    /// The renderer to hand to <c>Publisher.Builder.Renderer</c> or <c>Subscriber.Builder.Renderer</c>.
    /// </summary>
    public OpenTokVideoRenderer Renderer { get; }

    /// <summary>
    /// How the video fills the control. <see cref="Stretch.Uniform"/> by default — letterboxed,
    /// never distorted.
    /// </summary>
    /// <remarks>
    /// <see cref="Stretch.UniformToFill"/> is the other sensible choice: it fills the control and
    /// crops instead, which is usually what a small self-view tile wants.
    /// </remarks>
    public Stretch Stretch
    {
        get => _image.Stretch;
        set => _image.Stretch = value;
    }

    private void OnSourceChanged(object? sender, EventArgs e) => _image.Source = Renderer.Source;

    /// <summary>Releases the renderer. Detach the view from its publisher or subscriber first.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Renderer.SourceChanged -= OnSourceChanged;
        Renderer.Dispose();
        _image.Source = null;
    }
}
