using System.Collections.Concurrent;
using System.Net;

namespace MyConnection;

public class UdpSessionMap
{
    private readonly ConcurrentDictionary<string, string> _keyToConnection = new();
    private readonly ConcurrentDictionary<string, IPEndPoint> _connectionToEndpoint = new();
    private readonly ConcurrentDictionary<IPEndPoint, string> _endpointToConnection = new();

    public void RegisterKey(string key, string connectionId)
    {
        _keyToConnection[key] = connectionId;
    }

    public bool BindEndpoint(IPEndPoint ep, string key)
    {
        if (!_keyToConnection.TryGetValue(key, out var connectionId))
            return false;

        _connectionToEndpoint[connectionId] = ep;
        _endpointToConnection[ep] = connectionId;
        return true;
    }

    public bool IsAlreadyBound(string key)
    {
        if (!_keyToConnection.TryGetValue(key, out var connectionId))
            return false;
        return _connectionToEndpoint.ContainsKey(connectionId);
    }

    public string? GetConnectionId(IPEndPoint ep)
    {
        _endpointToConnection.TryGetValue(ep, out var connectionId);
        return connectionId;
    }

    public IPEndPoint? GetEndpoint(string connectionId)
    {
        _connectionToEndpoint.TryGetValue(connectionId, out var ep);
        return ep;
    }

    public void Invalidate(string connectionId)
    {
        if (_connectionToEndpoint.TryRemove(connectionId, out var ep))
        {
            _endpointToConnection.TryRemove(ep, out _);
        }
    }

    public void Remove(string connectionId)
    {
        Invalidate(connectionId);
        foreach (var kv in _keyToConnection)
        {
            if (kv.Value == connectionId)
                _keyToConnection.TryRemove(kv.Key, out _);
        }
    }

    public string GenerateKey()
    {
        return Guid.NewGuid().ToString("N").Substring(0, 16);
    }
}
