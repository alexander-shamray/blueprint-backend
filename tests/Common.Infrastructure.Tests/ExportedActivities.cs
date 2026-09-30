using System.Collections;
using System.Diagnostics;

namespace Common.Infrastructure.Tests;

/// <summary>A thread-safe exporter sink, since the Redis instrumentation writes from its flush timer.</summary>
internal sealed class ExportedActivities : ICollection<Activity>
{
    private readonly List<Activity> _items = [];
    private readonly Lock _lock = new();

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _items.Count;
            }
        }
    }

    public bool IsReadOnly => false;

    public void Add(Activity item)
    {
        lock (_lock)
        {
            _items.Add(item);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _items.Clear();
        }
    }

    public bool Contains(Activity item)
    {
        lock (_lock)
        {
            return _items.Contains(item);
        }
    }

    public void CopyTo(Activity[] array, int arrayIndex)
    {
        lock (_lock)
        {
            _items.CopyTo(array, arrayIndex);
        }
    }

    public bool Remove(Activity item)
    {
        lock (_lock)
        {
            return _items.Remove(item);
        }
    }

    /// <summary>Enumerates a snapshot, so a flush mid-assertion cannot invalidate it.</summary>
    public IEnumerator<Activity> GetEnumerator()
    {
        lock (_lock)
        {
            Activity[] snapshot = [.. _items];
            return ((IEnumerable<Activity>)snapshot).GetEnumerator();
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
