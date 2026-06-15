using System.Collections.Concurrent;
using Google.Protobuf;

namespace MyConnection;

public class ConnectionRegistry
{
    private readonly ConcurrentDictionary<string, ConnectionImplement> _connections = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _subjectSubscribers = new();
    private readonly ConcurrentDictionary<string, List<Action<IConnection, byte[]>>> _tcpSubscribers = new();
    private readonly ConcurrentDictionary<string, List<Action<IConnection, byte[]>>> _udpSubscribers = new();
    private readonly List<Action<IConnection>> _onConnectCallbacks = new();
    private readonly List<Action<IConnection>> _onDisconnectCallbacks = new();
    private readonly object _gate = new();
#pragma warning disable CS0649
    internal UdpSessionMap? _sessionMap;
#pragma warning restore CS0649

    public void Register(ConnectionImplement connection)
    {
        _connections[connection.Id] = connection;
        Action<IConnection>[] snapshot;
        lock (_gate)
        {
            snapshot = _onConnectCallbacks.ToArray();
        }
        foreach (var cb in snapshot)
        {
            cb(connection);
        }
    }

    public void Remove(string connectionId)
    {
        if (!_connections.TryRemove(connectionId, out var connection))
            return;
        _sessionMap?.Remove(connectionId);
        foreach (var (subject, dict) in _subjectSubscribers)
        {
            dict.TryRemove(connectionId, out _);
        }
        Action<IConnection>[] snapshot;
        lock (_gate)
        {
            snapshot = _onDisconnectCallbacks.ToArray();
        }
        foreach (var cb in snapshot)
        {
            cb(connection);
        }
    }

    public void BindUdp(string connectionId, string endpoint)
    {
        if (_connections.TryGetValue(connectionId, out var conn))
            conn.UdpAddress = endpoint;
    }

    public ConnectionImplement? GetById(string id)
    {
        _connections.TryGetValue(id, out var conn);
        return conn;
    }

    public IReadOnlyCollection<IConnection> GetAll()
    {
        return _connections.Values.ToList().AsReadOnly();
    }

    public void SubscribeConnection(string connectionId, string subject)
    {
        var dict = _subjectSubscribers.GetOrAdd(subject, _ => new ConcurrentDictionary<string, byte>());
        dict.TryAdd(connectionId, 0);
    }

    public void UnsubscribeConnection(string connectionId, string subject)
    {
        if (_subjectSubscribers.TryGetValue(subject, out var dict))
        {
            dict.TryRemove(connectionId, out _);
        }
    }

    public ISubscribe SubscribeTcpLocal<TData>(string subject, Action<IConnection, TData> callback)
    {
        Action<IConnection, byte[]> wrapped = (sender, rawPayload) =>
        {
            var data = ProtoSerializer.Deserialize<TData>(rawPayload);
            callback(sender, data);
        };
        var list = _tcpSubscribers.GetOrAdd(subject, _ => new List<Action<IConnection, byte[]>>());
        lock (list)
        {
            list.Add(wrapped);
        }
        return new UnsubscribeHandle(() =>
        {
            lock (list)
            {
                list.Remove(wrapped);
            }
        });
    }

    public ISubscribe SubscribeUdpLocal<TData>(string subject, Action<IConnection, TData> callback)
    {
        Action<IConnection, byte[]> wrapped = (sender, rawPayload) =>
        {
            var data = ProtoSerializer.Deserialize<TData>(rawPayload);
            callback(sender, data);
        };
        var list = _udpSubscribers.GetOrAdd(subject, _ => new List<Action<IConnection, byte[]>>());
        lock (list)
        {
            list.Add(wrapped);
        }
        return new UnsubscribeHandle(() =>
        {
            lock (list)
            {
                list.Remove(wrapped);
            }
        });
    }

    public ISubscribe SubscribeRawTcp(string subject, Action<IConnection, byte[]> callback)
    {
        var list = _tcpSubscribers.GetOrAdd(subject, _ => new List<Action<IConnection, byte[]>>());
        lock (list)
        {
            list.Add(callback);
        }
        return new UnsubscribeHandle(() =>
        {
            lock (list)
            {
                list.Remove(callback);
            }
        });
    }

    public ISubscribe SubscribeRawUdp(string subject, Action<IConnection, byte[]> callback)
    {
        var list = _udpSubscribers.GetOrAdd(subject, _ => new List<Action<IConnection, byte[]>>());
        lock (list)
        {
            list.Add(callback);
        }
        return new UnsubscribeHandle(() =>
        {
            lock (list)
            {
                list.Remove(callback);
            }
        });
    }

    public ISubscribe OnConnect(Action<IConnection> callback)
    {
        lock (_gate)
        {
            _onConnectCallbacks.Add(callback);
        }
        return new UnsubscribeHandle(() =>
        {
            lock (_gate)
            {
                _onConnectCallbacks.Remove(callback);
            }
        });
    }

    public ISubscribe OnDisconnect(Action<IConnection> callback)
    {
        lock (_gate)
        {
            _onDisconnectCallbacks.Add(callback);
        }
        return new UnsubscribeHandle(() =>
        {
            lock (_gate)
            {
                _onDisconnectCallbacks.Remove(callback);
            }
        });
    }

    public void Route(string senderConnectionId, string subject, byte[] payload, bool fromUdp = false)
    {
        var sender = GetById(senderConnectionId);
        if (sender is null) return;

        var subscribers = fromUdp ? _udpSubscribers : _tcpSubscribers;
        if (subscribers.TryGetValue(subject, out var list))
        {
            Action<IConnection, byte[]>[] snapshot;
            lock (list)
            {
                snapshot = list.ToArray();
            }
            foreach (var cb in snapshot)
            {
                cb(sender, payload);
            }
        }

        if (_subjectSubscribers.TryGetValue(subject, out var subs))
        {
            var envelope = new MessageEnvelope { Subject = subject, Payload = ByteString.CopyFrom(payload) };
            var bytes = envelope.ToByteArray();
            foreach (var (connId, _) in subs)
            {
                if (connId == senderConnectionId) continue;
                if (_connections.TryGetValue(connId, out var conn) && conn.Connected)
                {
                    _ = conn.SendAsync(bytes);
                }
            }
        }
    }

    public void Clear()
    {
        _connections.Clear();
        _subjectSubscribers.Clear();
        _tcpSubscribers.Clear();
        _udpSubscribers.Clear();
        lock (_gate)
        {
            _onConnectCallbacks.Clear();
            _onDisconnectCallbacks.Clear();
        }
    }

    private class UnsubscribeHandle : ISubscribe
    {
        private readonly Action _action;
        public UnsubscribeHandle(Action action) => _action = action;
        public void UnSubscribe() => _action();
    }
}
