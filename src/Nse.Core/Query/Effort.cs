using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace Nse.Query;

/// <summary>
/// A count of what answering from one model took: one for each record a query was handed, one for each lookup,
/// and one for each paragraph read or pattern run by a scan. No answer depends on it. It is kept so that a test
/// can hold an answer to work that grows as the answer does, and fail when a pass over the whole story finds its
/// way into a loop over the story. Time on a busy machine cannot be held to that; a count can.
/// </summary>
internal sealed class Effort
{
    public long Spent { get; private set; }

    public void Add(long units = 1) => Spent += units;
}

/// <summary>
/// Records of one kind in the order they entered the ledger, read-only. Each record handed out is counted.
/// It is a list in full so that the shortcuts LINQ takes on lists, such as a count without a pass, still hold.
/// </summary>
internal sealed class Counted<T>(List<T> items, Effort effort) : IList<T>, IReadOnlyList<T>
{
    public int Count => items.Count;

    public bool IsReadOnly => true;

    public T this[int index]
    {
        get
        {
            effort.Add();
            return items[index];
        }
        set => throw ReadOnly();
    }

    public IEnumerator<T> GetEnumerator()
    {
        foreach (var item in items)
        {
            effort.Add();
            yield return item;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Contains(T item)
    {
        effort.Add(items.Count);
        return items.Contains(item);
    }

    public int IndexOf(T item)
    {
        effort.Add(items.Count);
        return items.IndexOf(item);
    }

    public void CopyTo(T[] array, int arrayIndex)
    {
        effort.Add(items.Count);
        items.CopyTo(array, arrayIndex);
    }

    public void Add(T item) => throw ReadOnly();

    public void Clear() => throw ReadOnly();

    public void Insert(int index, T item) => throw ReadOnly();

    public bool Remove(T item) => throw ReadOnly();

    public void RemoveAt(int index) => throw ReadOnly();

    private static NotSupportedException ReadOnly() => new("The records of a story's state are read, never changed.");
}

/// <summary>Records by id, read-only. Each lookup is counted, and each record handed out by a pass over them all.</summary>
internal sealed class CountedMap<T>(Dictionary<string, T> items, Effort effort) : IReadOnlyDictionary<string, T>
{
    public int Count => items.Count;

    public T this[string key]
    {
        get
        {
            effort.Add();
            return items[key];
        }
    }

    public bool ContainsKey(string key)
    {
        effort.Add();
        return items.ContainsKey(key);
    }

    public bool TryGetValue(string key, [MaybeNullWhen(false)] out T value)
    {
        effort.Add();
        return items.TryGetValue(key, out value);
    }

    public IEnumerable<string> Keys
    {
        get
        {
            foreach (var key in items.Keys)
            {
                effort.Add();
                yield return key;
            }
        }
    }

    public IEnumerable<T> Values
    {
        get
        {
            foreach (var value in items.Values)
            {
                effort.Add();
                yield return value;
            }
        }
    }

    public IEnumerator<KeyValuePair<string, T>> GetEnumerator()
    {
        foreach (var pair in items)
        {
            effort.Add();
            yield return pair;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
