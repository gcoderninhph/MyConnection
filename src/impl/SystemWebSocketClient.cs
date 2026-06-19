using System.Net.WebSockets;

namespace MyConnection;

internal sealed class SystemWebSocketClient : IWebSocketClient
{
    private ClientWebSocket? _ws;
    private readonly Uri _uri;
    private readonly Dictionary<string, string> _headers;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _connectCts;
    private bool _onCloseFired;

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public event Action? OnOpen;
    public event Action<string>? OnError;
    public event Action<byte[]>? OnMessage;
    public event Action<int>? OnClose;

    public SystemWebSocketClient(Uri uri, Dictionary<string, string> headers)
    {
        _uri = uri;
        _headers = headers;
    }

    public async Task ConnectAsync()
    {
        _cts = new CancellationTokenSource();
        _connectCts = new CancellationTokenSource();
        _onCloseFired = false;

        _ws = new ClientWebSocket();
        foreach (var kv in _headers)
            _ws.Options.SetRequestHeader(kv.Key, kv.Value);

        try
        {
            await _ws.ConnectAsync(_uri, _connectCts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            OnError?.Invoke(ex.Message);
            return;
        }

        OnOpen?.Invoke();

        try
        {
            await ReceiveLoopAsync();
        }
        catch { }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[1024 * 64];
        try
        {
            while (_ws!.State == WebSocketState.Open)
            {
                if (_cts!.IsCancellationRequested) break;

                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        if (!_onCloseFired)
                        {
                            _onCloseFired = true;
                            OnClose?.Invoke((int)(result.CloseStatus ?? WebSocketCloseStatus.NormalClosure));
                        }
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                OnMessage?.Invoke(ms.ToArray());
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException)
        {
            if (!_onCloseFired)
            {
                _onCloseFired = true;
                OnClose?.Invoke(-1);
            }
        }
        catch (ObjectDisposedException)
        {
            if (!_onCloseFired)
            {
                _onCloseFired = true;
                OnClose?.Invoke(-1);
            }
        }
    }

    public async Task SendAsync(byte[] data)
    {
        if (_ws?.State == WebSocketState.Open)
            await _ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Binary, true, _cts?.Token ?? CancellationToken.None);
    }

    public async Task CloseAsync()
    {
        try
        {
            if (_ws?.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", closeCts.Token);
            }
        }
        catch { }

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        _ws?.Dispose();
        _ws = null;

        if (!_onCloseFired)
        {
            _onCloseFired = true;
            OnClose?.Invoke((int)WebSocketCloseStatus.NormalClosure);
        }
    }

    public void CancelConnection()
    {
        _connectCts?.Cancel();
    }
}
