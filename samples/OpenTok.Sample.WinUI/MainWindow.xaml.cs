using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenTok;
using OpenTok.Net.Win;
using OpenTok.Net.Win.Rendering;

namespace OpenTok.Sample.WinUI;

/// <summary>
/// Connect, publish, and subscribe to the first stream that arrives — the smallest thing that shows
/// the renderer actually rendering.
/// </summary>
/// <remarks>
/// The part worth copying is the <see cref="OpenTokDispatcher"/> handed to <c>Context</c>. Without
/// it the SDK raises its events on its own threads, and every handler below — all of which touch
/// XAML — would throw <c>RPC_E_WRONG_THREAD</c>.
/// </remarks>
public sealed partial class MainWindow : Window
{
    private readonly OpenTokVideoView _publisherView;
    private readonly OpenTokVideoView _subscriberView;

    private Context? _context;
    private Session? _session;
    private Publisher? _publisher;
    private Subscriber? _subscriber;

    public MainWindow()
    {
        InitializeComponent();

        _publisherView = new OpenTokVideoView { Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill };
        _subscriberView = new OpenTokVideoView();

        Grid.SetColumn(_publisherView, 0);
        Grid.SetColumn(_subscriberView, 1);
        VideoGrid.Children.Add(_publisherView);
        VideoGrid.Children.Add(_subscriberView);

        Closed += (_, _) => Teardown();
    }

    private void OnConnectClick(object sender, RoutedEventArgs e)
    {
        if (_session is not null)
        {
            Teardown();
            StatusText.Text = "Not connected";
            ConnectButton.Content = "Connect";
            return;
        }

        var apiKey = ApiKeyBox.Text.Trim();
        var sessionId = SessionIdBox.Text.Trim();
        var token = TokenBox.Text.Trim();

        if (apiKey.Length == 0 || sessionId.Length == 0 || token.Length == 0)
        {
            StatusText.Text = "API key, session ID and token are all required.";
            return;
        }

        // Every OpenTok event below arrives on this thread because of this one argument.
        _context = new Context(new OpenTokDispatcher(DispatcherQueue.GetForCurrentThread()));

        _session = new Session.Builder(_context, apiKey, sessionId).Build();
        _session.Connected += OnConnected;
        _session.Disconnected += (_, _) => StatusText.Text = "Disconnected";
        _session.Error += (_, args) => StatusText.Text = $"Session error: {args.ErrorCode} {args.ErrorDescription}";
        _session.StreamReceived += OnStreamReceived;
        _session.StreamDropped += OnStreamDropped;

        StatusText.Text = "Connecting...";
        ConnectButton.Content = "Disconnect";
        _session.Connect(token);
    }

    private void OnConnected(object? sender, EventArgs e)
    {
        StatusText.Text = "Connected — publishing";

        _publisher = new Publisher.Builder(_context!)
        {
            Renderer = _publisherView.Renderer,
            Name = "WinUI sample",
        }.Build();

        _session!.Publish(_publisher);
    }

    private void OnStreamReceived(object? sender, Session.StreamEventArgs e)
    {
        // One tile in this sample, so later streams are ignored rather than replacing the first.
        if (_subscriber is not null)
        {
            return;
        }

        _subscriber = new Subscriber.Builder(_context!, e.Stream)
        {
            Renderer = _subscriberView.Renderer,
        }.Build();

        _session!.Subscribe(_subscriber);
        StatusText.Text = $"Subscribed to {e.Stream.Name ?? e.Stream.Id}";
    }

    private void OnStreamDropped(object? sender, Session.StreamEventArgs e)
    {
        if (_subscriber is null || _subscriber.Stream.Id != e.Stream.Id)
        {
            return;
        }

        _session!.Unsubscribe(_subscriber);
        _subscriber.Dispose();
        _subscriber = null;
        StatusText.Text = "Remote stream ended";
    }

    private void Teardown()
    {
        // Order matters: unsubscribe and unpublish before disconnecting, and dispose the Context
        // last — it owns the resources the other three are built on.
        if (_session is not null)
        {
            if (_subscriber is not null)
            {
                _session.Unsubscribe(_subscriber);
                _subscriber.Dispose();
                _subscriber = null;
            }

            if (_publisher is not null)
            {
                _session.Unpublish(_publisher);
                _publisher.Dispose();
                _publisher = null;
            }

            _session.Disconnect();
            _session.Dispose();
            _session = null;
        }

        _context?.Dispose();
        _context = null;
    }
}
