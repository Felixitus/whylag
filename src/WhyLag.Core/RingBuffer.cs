namespace WhyLag.Core;

/// <summary>Fixed-capacity, thread-safe buffer that overwrites the oldest item.</summary>
public sealed class RingBuffer<T>
{
    private readonly T[] _items;
    private readonly Lock _lock = new();
    private int _start;
    private int _count;

    public RingBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _items = new T[capacity];
    }

    public int Capacity => _items.Length;

    public int Count
    {
        get { lock (_lock) return _count; }
    }

    public void Add(T item)
    {
        lock (_lock)
        {
            var index = (_start + _count) % _items.Length;
            _items[index] = item;
            if (_count < _items.Length) _count++;
            else _start = (_start + 1) % _items.Length;
        }
    }

    /// <summary>Copy of all items, oldest first.</summary>
    public List<T> Snapshot()
    {
        lock (_lock)
        {
            var result = new List<T>(_count);
            for (var i = 0; i < _count; i++) result.Add(_items[(_start + i) % _items.Length]);
            return result;
        }
    }

    public T? Latest()
    {
        lock (_lock) return _count == 0 ? default : _items[(_start + _count - 1) % _items.Length];
    }
}
