// Ported from Queue.h of PianoBooster (C++). Copyright (c) 2008-2013, L. J. Barman. GPLv3.

namespace PianoBooster.Core;

/// <summary>
/// A circular buffer (First In First Out) of a fixed size, like the C++ CQueue. The items are
/// values; <see cref="IndexRef"/> and <see cref="SlotRef"/> give access to them in place
/// (the C++ code keeps pointers into the buffer).
/// </summary>
/// <remarks>
/// As in the C++ release build (where assert() does nothing), pushing into a full queue does
/// nothing, popping an empty queue returns the item at the tail, and an index out of range
/// gives the item at the head.
/// </remarks>
public sealed class CircularQueue<T> where T : struct
{
    private readonly T[] _buffer;
    private readonly int _size;
    private int _head;
    private int _tail;
    private int _count;

    public CircularQueue(int size)
    {
        _size = size;
        _buffer = new T[size];
        Clear();
    }

    public void Clear()
    {
        _count = _head = _tail = 0;
    }

    /// <summary>Pushes the item and returns the index of its place in the buffer
    /// (for <see cref="SlotRef"/>), or -1 if the queue is full.</summary>
    public int Push(in T item)
    {
        if (Space == 0)
            return -1;
        int slot = _head;
        _buffer[_head] = item;
        _head++;
        if (_head >= _size)
            _head = 0;
        _count++;
        return slot;
    }

    public T Pop()
    {
        if (Length == 0)
            return _buffer[_tail];
        T item = _buffer[_tail++];
        if (_tail >= _size)
            _tail = 0;
        _count--;
        return item;
    }

    /// <summary>The item at a place of the buffer returned by <see cref="Push"/>.</summary>
    public ref T SlotRef(int slot) => ref _buffer[slot];

    /// <summary>The item at an index from the tail (the oldest item) of the queue.</summary>
    public ref T IndexRef(int index)
    {
        if (index >= Length)
            return ref _buffer[_head];
        int offset = _tail + index;
        if (offset >= _size)
            offset -= _size;
        return ref _buffer[offset];
    }

    public T Index(int index) => IndexRef(index);

    public int Length => _count;
    public int Space => _size - _count;
}
