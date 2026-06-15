using System.Collections.Concurrent;

namespace MyConnection;

public class SubjectDispatcher
{
    private readonly ConcurrentDictionary<string, List<Action<byte[]>>> _subscribers = new();

    public ISubscribe Subscribe<TData>(string subject, Action<TData> callback)
    {
        Action<byte[]> wrapped = rawPayload =>
        {
            var data = ProtoSerializer.Deserialize<TData>(rawPayload);
            callback(data);
        };
        var list = _subscribers.GetOrAdd(subject, _ => new List<Action<byte[]>>());
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

    public void Dispatch(string subject, byte[] payload)
    {
        if (!_subscribers.TryGetValue(subject, out var list))
            return;
        Action<byte[]>[] snapshot;
        lock (list)
        {
            snapshot = list.ToArray();
        }
        foreach (var cb in snapshot)
        {
            cb(payload);
        }
    }

    public void Clear()
    {
        _subscribers.Clear();
    }

    private class UnsubscribeHandle : ISubscribe
    {
        private readonly Action _action;
        public UnsubscribeHandle(Action action) => _action = action;
        public void UnSubscribe() => _action();
    }
}
