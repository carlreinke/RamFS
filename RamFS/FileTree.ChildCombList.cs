// Copyright 2023 Carl Reinke
//
// This file is part of a program that is licensed under the terms of the GNU
// General Public License Version 3 as published by the Free Software
// Foundation.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;

internal sealed partial class FileTree
{
    [DebuggerDisplay("Length = {Length}")]
    public struct ChildCombList : IEnumerable<Child>
    {
        /// <summary>
        /// Determines the maximum number of children in each tooth of the comb.
        /// </summary>
#if DEBUG
        private const int _shift = 2;
#else
        private const int _shift = 7;
#endif

        private const int _mask = (1 << _shift) - 1;

        private const int _toothMaxLength = 1 << _shift;

        private object? _items;

        private ulong _length;

        /// <summary>
        /// Gets the maximum number of children that can be stored without resizing.
        /// </summary>
        public ulong Capacity => CombList<Child>.GetLength(_shift, _items);

        /// <summary>
        /// Gets the number of children.
        /// </summary>
        public ulong Length => _length;

        /// <summary>
        /// Gets a reference to the child at the specified index.
        /// </summary>
        public ref Child this[ulong index] => ref CombList<Child>.GetItemRef(_shift, _items, index);

        /// <summary>
        /// Adds a child.
        /// </summary>
        /// <exception cref="OutOfMemoryException"/>
        public void Add(Child child, bool ignoreCase)
        {
            Debug.Assert(child.Name.Length > 0);

            if (_length == Capacity)
                CombList<Child>.SetLength(_shift, ref _items, _length + 1);

            Child[] tooth;
            ulong toothIndex;
            ulong toothLength;
            if (_items is Child[] temp)
            {
                tooth = temp;
                toothIndex = _length;
                toothLength = toothIndex + 1;
            }
            else
            {
                var teeth = (Child[][])_items!;
                ulong teethIndex = _length >> _shift;
                toothIndex = _length & _mask;
                toothLength = toothIndex + 1;
                tooth = teeth[teethIndex];
            }

            tooth[toothIndex] = child;

            _length += 1;

            Reorder(tooth, (int)toothIndex, (int)toothLength, ignoreCase);
        }

        /// <summary>
        /// Finds the index of a child by name.
        /// </summary>
        public bool Find(StringSegment name, bool ignoreCase, out ulong index)
        {
            Debug.Assert(name.Length > 0);

            if (_items == null)
            {
                // Not found.
            }
            else if (_items is Child[] temp)
            {
                var tooth = temp;
                int i = BinarySearch(tooth, 0, (int)_length, name, ignoreCase);
                if (i >= 0)
                {
                    index = (ulong)i;
                    return true;
                }
            }
            else
            {
                var teeth = (Child[][])_items;
                for (int j = 0; j < teeth.Length; ++j)
                {
                    ulong offset = (ulong)j << _shift;
                    if (offset >= _length)
                        break;
                    var tooth = teeth[j];
                    ulong toothLength = _length - offset;
                    if (toothLength > _toothMaxLength)
                        toothLength = _toothMaxLength;
                    int i = BinarySearch(tooth, 0, (int)toothLength, name, ignoreCase);
                    if (i >= 0)
                    {
                        index = offset + (ulong)i;
                        return true;
                    }
                }
            }

            index = default;
            return false;
        }

        /// <summary>
        /// Reorders the child at the specified index (because its name changed).
        /// </summary>
        public void Reorder(ulong index, bool ignoreCase)
        {
            Debug.Assert(index < _length);

            Child[] tooth;
            ulong toothIndex;
            ulong toothLength;
            if (_items is Child[] temp)
            {
                tooth = temp;
                toothIndex = index;
                toothLength = _length;
            }
            else
            {
                var teeth = (Child[][])_items!;
                ulong teethIndex = index >> _shift;
                toothIndex = index & _mask;
                toothLength = _length - (teethIndex << _shift);
                if (toothLength > _toothMaxLength)
                    toothLength = _toothMaxLength;
                tooth = teeth[teethIndex];
            }

            Reorder(tooth, (int)toothIndex, (int)toothLength, ignoreCase);
        }

        /// <summary>
        /// Removes the child at the specified index.
        /// </summary>
        public void Remove(ulong index, bool ignoreCase)
        {
            Debug.Assert(index < _length);

            _length -= 1;

            Child[] tooth;
            ulong toothIndex;
            ulong toothLength;
            Child[] lastChildTooth;
            ulong lastChildToothIndex;
            if (_items is Child[] temp)
            {
                tooth = temp;
                toothIndex = index;
                toothLength = _length;
                lastChildTooth = temp;
                lastChildToothIndex = _length;
            }
            else
            {
                var teeth = (Child[][])_items!;
                ulong teethIndex = index >> _shift;
                toothIndex = index & _mask;
                toothLength = _length - (teethIndex << _shift);
                if (toothLength > _toothMaxLength)
                    toothLength = _toothMaxLength;
                tooth = teeth[teethIndex];
                ulong lastChildTeethIndex = _length >> _shift;
                lastChildToothIndex = _length & _mask;
                lastChildTooth = teeth[lastChildTeethIndex];
            }

            ref var lastChild = ref lastChildTooth[lastChildToothIndex];
            tooth[toothIndex] = lastChild;
            lastChild = default;

            if (toothIndex != toothLength)
                Reorder(tooth, (int)toothIndex, (int)toothLength, ignoreCase);

            try
            {
                // Allow one excess child.
                if (Capacity - 1 > _length || _length == 0)
                    CombList<Child>.SetLength(_shift, ref _items, _length);
            }
            catch (OutOfMemoryException)
            {
                // Couldn't shrink.
            }
        }

        /// <summary>
        /// Returns an enumerator that yields the children in an arbitrary order.
        /// </summary>
        public Enumerator GetEnumerator() => new Enumerator(_items, _length);

        IEnumerator<Child> IEnumerable<Child>.GetEnumerator() => GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>
        /// Returns an enumerator that yields children, ordered by name, where the name is greater
        /// than the marker.
        /// </summary>
        public ListingEnumerator GetEnumerator(string? marker, bool ignoreCase) => new ListingEnumerator(_items, _length, marker, ignoreCase);

        /// <summary>
        /// Orders the sole unordered child at the specified index in the tooth.
        /// </summary>
        private static void Reorder(Child[] tooth, int index, int length, bool ignoreCase)
        {
            Debug.Assert(index < length);
            Debug.Assert(tooth.Length == length || tooth[length].Name == null);

            var comparer = ignoreCase
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;

            var child = tooth[index];
            string name = child.Name;

            if (index > 0 && comparer.Compare(name, tooth[index - 1].Name) < 0)
            {
                int newIndex = ~BinarySearch(tooth, 0, index - 1, name, comparer);
                Array.Copy(tooth, newIndex, tooth, newIndex + 1, index - newIndex);
                tooth[newIndex] = child;
            }
            else if (index < length - 1)
            {
                int newIndex = ~BinarySearch(tooth, index + 1, length - (index + 1), name, comparer) - 1;
                if (newIndex != index)
                {
                    Array.Copy(tooth, index + 1, tooth, index, newIndex - index);
                    tooth[newIndex] = child;
                }
            }

#if DEBUG
            // Check order.
            string leftName = tooth[0].Name;
            for (int i = 1; i < length; ++i)
            {
                string rightName = tooth[i].Name;
                Debug.Assert(comparer.Compare(leftName, rightName) < 0);
                leftName = rightName;
            }
#endif
        }

        private static int BinarySearch(Child[] tooth, int offset, int length, string name, StringComparer comparer)
        {
            int lower = offset;
            int upper = offset + length - 1;
            while (lower <= upper)
            {
                int middle = lower + (upper - lower >> 1);
                int result = comparer.Compare(name, tooth[middle].Name);
                if (result == 0)
                    return middle;
                if (result < 0)
                    upper = middle - 1;
                else
                    lower = middle + 1;
            }
            return ~lower;
        }

        private static int BinarySearch(Child[] tooth, int offset, int length, StringSegment name, bool ignoreCase)
        {
            int lower = offset;
            int upper = offset + length - 1;
            while (lower <= upper)
            {
                int middle = lower + (upper - lower >> 1);
                int result = name.CompareOrdinalTo(tooth[middle].Name, ignoreCase);
                if (result == 0)
                    return middle;
                if (result < 0)
                    upper = middle - 1;
                else
                    lower = middle + 1;
            }
            return ~lower;
        }

        /// <summary>
        /// Enumerates children in an arbitrary order.
        /// </summary>
        public struct Enumerator : IEnumerator<Child>
        {
            private readonly Child[][]? _teeth;

            private Child[]? _tooth;

            private int _teethIndex;

            private int _toothIndex;

            private ulong _length;

            internal Enumerator(object? items, ulong length)
            {
                if (items is null)
                {
                    _teeth = null;
                    _tooth = null;
                }
                else if (items is Child[] tooth)
                {
                    _teeth = null;
                    _tooth = tooth;
                }
                else
                {
                    _teeth = (Child[][])items;
                    _tooth = _teeth[0];
                }
                _teethIndex = 0;
                _toothIndex = -1;
                _length = length;
            }

            public Child Current => _tooth![_toothIndex];

            object IEnumerator.Current => Current;

            public void Dispose()
            {
            }

            public bool MoveNext()
            {
                if (_length == 0)
                    return false;

                _length -= 1;

                _toothIndex += 1;
                if (_toothIndex == _tooth!.Length)
                {
                    _teethIndex += 1;
                    _tooth = _teeth![_teethIndex];
                    _toothIndex = 0;
                }

                return true;
            }

            /// <exception cref="NotSupportedException"/>
            public void Reset() => throw new NotSupportedException();
        }

        /// <summary>
        /// Enumerates children, ordered by name, where the name is greater than the marker.
        /// </summary>
        public struct ListingEnumerator : IEnumerator<Child>
        {
            private readonly HeapItem[] _heap;

            private readonly StringComparer _comparer;

            private Child _current;

            internal ListingEnumerator(object? items, ulong length, string? marker, bool ignoreCase)
            {
                _comparer = ignoreCase
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal;

                // Create a heap item for each tooth.
                if (items is null)
                {
                    _heap = new HeapItem[0];
                }
                else if (items is Child[] temp)
                {
                    var tooth = temp;
                    _heap = new HeapItem[1];
                    int toothLength = (int)length;
                    _heap[0] = new HeapItem
                    {
                        Tooth = tooth,
                        Index = 0,
                        Length = toothLength,
                    };
                }
                else
                {
                    var teeth = (Child[][])items;
                    _heap = new HeapItem[teeth.Length];
                    for (int j = 0; j < teeth.Length; ++j)
                    {
                        ulong offset = (ulong)j << _shift;
                        if (offset >= length)
                            break;
                        var tooth = teeth[j];
                        ulong toothLength = length - offset;
                        if (toothLength > _toothMaxLength)
                            toothLength = _toothMaxLength;
                        _heap[j] = new HeapItem
                        {
                            Tooth = tooth,
                            Index = 0,
                            Length = (int)toothLength,
                        };
                    }
                }

                // Prepare heap items so that each starts with the child that
                // has a name that is greater than the marker.
                for (int j = 0; j < _heap.Length; ++j)
                {
                    ref HeapItem item = ref _heap[j];
                    if (marker != null)
                    {
                        int i = BinarySearch(item.Tooth, 0, item.Length, marker, _comparer);
                        item.Index = i >= 0 ? i + 1 : ~i;
                    }
                    item.Name = item.Index < item.Length
                        ? item.Tooth[item.Index].Name
                        : null;
                }

                _current = default;

                // Make the heap satisfy the invariant.
                for (int j = _heap.Length >> 1; j > 0; --j)
                    PushDown(j - 1);
            }

            public Child Current => _current;

            object IEnumerator.Current => Current;

            public void Dispose()
            {
            }

            public bool MoveNext()
            {
                if (_heap.Length == 0)
                    return false;

                ref var item = ref _heap[0];
                if (item.Name == null)
                    return false;

                _current = item.Tooth[item.Index];

                // Advance the top heap item to next child and ensure that the
                // heap satisfies the invariant.
                item.Index += 1;
                item.Name = item.Index < item.Length
                    ? item.Tooth[item.Index].Name
                    : null;
                PushDown(0);

                return true;
            }

            /// <exception cref="NotSupportedException"/>
            public void Reset() => throw new NotSupportedException();

            /// <summary>
            /// Pushes the heap item at the specified index down through the heap until its left and
            /// right items are not less than it.
            /// </summary>
            private void PushDown(int index)
            {
                var item = _heap[index];

                while (index < _heap.Length >> 1)
                {
                    HeapItem leftItem;

                    int rightIndex = (index + 1) << 1;
                    if (rightIndex < _heap.Length)
                    {
                        var rightItem = _heap[rightIndex];
                        if (IsLess(rightItem.Name, item.Name))
                        {
                            leftItem = _heap[rightIndex - 1];
                            if (IsLess(leftItem.Name, rightItem.Name))
                            {
                                // Left is least.
                                _heap[index] = leftItem;
                                index = rightIndex - 1;
                                continue;
                            }
                            else
                            {
                                // Right is least.
                                _heap[index] = rightItem;
                                index = rightIndex;
                                continue;
                            }
                        }
                    }

                    leftItem = _heap[rightIndex - 1];
                    if (IsLess(leftItem.Name, item.Name))
                    {
                        // Left is least.
                        _heap[index] = leftItem;
                        index = rightIndex - 1;
                        continue;
                    }

                    // Self is least.
                    break;
                }

                _heap[index] = item;
            }

            /// <summary>
            /// Determines whether left is less than right, with non-null being less than null.
            /// </summary>
            private bool IsLess(string? left, string? right)
            {
                return left != null && (right == null || _comparer.Compare(left, right) < 0);
            }

            private struct HeapItem
            {
                public string? Name;
                public Child[] Tooth;
                public int Index;
                public int Length;
            }
        }
    }
}
