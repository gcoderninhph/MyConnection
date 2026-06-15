using Google.Protobuf;
using NativeWebSocket;

namespace MyConnection;

public class ClientImplement : ClientAbstract
{
    private WebSocket? _ws;
    private Task? _connectTask;
    private readonly SubjectDispatcher _dispatcher = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly List<Action> _onDisconnectCallbacks = new();
    private readonly object _gate = new();

    protected override async Task ConnectWebSocket(string token, string websocketServer)
    {
        var uri = new Uri(
            websocketServer.Contains("://") ? websocketServer : "ws://" + websocketServer);

        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer " + token
        };
        _ws = new WebSocket(uri.ToString(), headers);

        var connectTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _ws.OnOpen += () =>
        {
            connectTcs.TrySetResult(true);
        };

        _ws.OnError += (err) =>
        {
            if (!connectTcs.Task.IsCompletedSuccessfully)
                connectTcs.TrySetException(new ConnectionFailedException(err ?? "Unknown connection error"));
        };

        _ws.OnMessage += OnMessage;

        _ws.OnClose += (code) =>
        {
            if (!connectTcs.Task.IsCompletedSuccessfully)
            {
                connectTcs.TrySetException(new ConnectionFailedException($"WebSocket closed with code {code}"));
                return;
            }

            Action[] snapshot;
            lock (_gate) { snapshot = _onDisconnectCallbacks.ToArray(); }
            foreach (var cb in snapshot) cb();
        };

        _connectTask = _ws.Connect();

        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(10));
        var completedTask = await Task.WhenAny(connectTcs.Task, timeoutTask);
        if (completedTask == timeoutTask)
        {
            _ws.CancelConnection();
            throw new ConnectionFailedException("Connection timed out after 10 seconds");
        }
        await completedTask;
    }

    private void OnMessage(byte[] data)
    {
        try
        {
            var envelope = MessageEnvelope.Parser.ParseFrom(data);
            _dispatcher.Dispatch(envelope.Subject, envelope.Payload.ToByteArray());
        }
        catch { }
    }

    protected override void NotifyConnectUdp(string token, string udpServer)
    {
    }

    protected override void AutoPingWebSocketAndUdpThread()
    {
    }

    public override async Task DisconnectAsync()
    {
        if (_ws != null)
        {
            try
            {
                await _ws.Close();
            }
            catch { }
        }

        if (_connectTask != null)
        {
            try { await _connectTask; } catch { }
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _sendLock.Dispose();
    }

    public override ISubscribe OnDisconnect(Action onDisconnect)
    {
        lock (_gate) { _onDisconnectCallbacks.Add(onDisconnect); }
        return new UnsubscribeHandle(() =>
        {
            lock (_gate) { _onDisconnectCallbacks.Remove(onDisconnect); }
        });
    }

    public override async void SendOnTcp<TData>(string subject, TData data)
    {
        var payload = ProtoSerializer.Serialize(data);
        var envelope = new MessageEnvelope
        {
            Subject = subject,
            Payload = ByteString.CopyFrom(payload)
        };
        await _sendLock.WaitAsync();
        try
        {
            await _ws!.Send(envelope.ToByteArray());
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public override void SendOnUdp<TData>(string subject, TData data)
        => throw new NotImplementedException("UDP not implemented yet");

    public override ISubscribe SubscribeTcp<TData>(string subject, Action<TData> data)
        => _dispatcher.Subscribe(subject, data);

    public override ISubscribe SubscribeUdp<TData>(string subject, Action<TData> data)
        => throw new NotImplementedException("UDP not implemented yet");

    private class UnsubscribeHandle : ISubscribe
    {
        private readonly Action _action;
        public UnsubscribeHandle(Action action) => _action = action;
        public void UnSubscribe() => _action();
    }
}
