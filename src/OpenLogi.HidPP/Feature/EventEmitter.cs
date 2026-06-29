using System.Threading.Channels;

namespace OpenLogi.HidPP.Feature;

/// <summary>
/// A simple multi-subscriber event emitter. Ported from Rust <c>event::EventEmitter</c>.
/// Each <see cref="CreateReceiver"/> returns an independent unbounded channel.
///
/// Note: the Rust version prunes senders whose receiver count dropped to zero;
/// .NET channels don't expose subscriber liveness, so subscribers instead call
/// <see cref="RemoveReceiver"/> (or the agent holds the feature for its lifetime).
/// </summary>
public sealed class EventEmitter<T>
{
    private readonly object _lock = new();
    private readonly List<System.Threading.Channels.Channel<T>> _channels = [];

    /// <summary>Create a receiver; the returned reader yields every subsequently emitted event.</summary>
    public ChannelReader<T> CreateReceiver()
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<T>();
        lock (_lock) _channels.Add(channel);
        return channel.Reader;
    }

    /// <summary>Stop delivering to a reader previously obtained from <see cref="CreateReceiver"/>.</summary>
    public void RemoveReceiver(ChannelReader<T> reader)
    {
        lock (_lock)
        {
            var idx = _channels.FindIndex(c => ReferenceEquals(c.Reader, reader));
            if (idx >= 0)
            {
                _channels[idx].Writer.TryComplete();
                _channels.RemoveAt(idx);
            }
        }
    }

    /// <summary>Emit an event to all current receivers.</summary>
    public void Emit(T evt)
    {
        lock (_lock)
            foreach (var channel in _channels)
                channel.Writer.TryWrite(evt);
    }
}
