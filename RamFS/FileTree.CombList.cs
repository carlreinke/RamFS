// Copyright 2023 Carl Reinke
//
// This file is part of a program that is licensed under the terms of the GNU
// General Public License Version 3 as published by the Free Software
// Foundation.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;

internal sealed partial class FileTree
{
    [DebuggerDisplay("Length = {Length}")]
    public struct CombList<T> : IEnumerable<T>
        where T : struct
    {
        /// <summary>
        /// Determines the maximum number of items in each tooth of the comb.
        /// </summary>
#if DEBUG
        private const int _shift = 2;
#else
        private const int _shift = 9;
#endif

        private const int _mask = (1 << _shift) - 1;

        private object? _items;

        public ulong Length => GetLength(_shift, _items);

        public ref T this[ulong index] => ref GetItemRef(_shift, _items, index);

        public static ulong GetRoundedLength(ulong length)
        {
            return GetRoundedLength(_mask, length);
        }

        /// <exception cref="OutOfMemoryException"/>
        public void SetLength(ulong length) => SetLength(_shift, ref _items, length);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ulong GetLength(int shift, object? items)
        {
            if (items is null)
            {
                return 0;
            }
            else if (items is T[] tooth)
            {
                return (ulong)tooth.Length;
            }
            else
            {
                var teeth = (T[][])items;
                return ((ulong)(teeth.Length - 1) << shift) +
                       (ulong)teeth[teeth.Length - 1].Length;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ref T GetItemRef(int shift, object? items, ulong index)
        {
            if (items is T[] tooth)
            {
                return ref tooth[index];
            }
            else
            {
                var teeth = (T[][])items!;
                return ref teeth[index >> shift][index & (ulong)((1 << shift) - 1)];
            }
        }

        internal static ulong GetRoundedLength(ulong mask, ulong length)
        {
            ulong roundedLength = (length + mask) & ~mask;
            return roundedLength == 0 ? length : roundedLength;
        }

        /// <exception cref="OutOfMemoryException"/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void SetLength(int shift, ref object? items, ulong length)
        {
            if (length == 0)
            {
                items = null;
                Debug.Assert(GetLength(shift, items) == length);
                return;
            }

            int toothMaxLength = 1 << shift;

            // Use one tooth if possible.
            if (length <= (ulong)toothMaxLength)
            {
                if (items is null)
                {
                    items = new T[length];
                    Debug.Assert(GetLength(shift, items) == length);
                }
                else
                {
                    T[] tooth = items as T[] ?? ((T[][])items)[0];
                    Array.Resize(ref tooth, (int)length);

                    items = tooth;
                    Debug.Assert(GetLength(shift, items) == length);
                }
                return;
            }

            ulong mask = (1u << shift) - 1;

            ulong newTeethLength = length >> shift;
            int lastToothLength = (int)(length & mask);
            if (lastToothLength == 0)
                lastToothLength = toothMaxLength;
            else
                newTeethLength += 1;
            if (newTeethLength > int.MaxValue)
                throw new OutOfMemoryException();

            T[][] teeth;
            int oldTeethLength;
            if (items is null)
            {
                teeth = new T[(int)newTeethLength][];

                oldTeethLength = 0;
            }
            else if (items is T[] tooth)
            {
                teeth = new T[(int)newTeethLength][];
                Array.Resize(ref tooth, toothMaxLength);
                teeth[0] = tooth;

                oldTeethLength = 1;
            }
            else
            {
                var oldTeeth = (T[][])items;
                teeth = oldTeeth;

                if (newTeethLength <= (ulong)oldTeeth.Length)
                {
                    // Shrinking or only need to resize last tooth.

                    Array.Resize(ref teeth, (int)newTeethLength);

                    ref var lastTooth = ref teeth[teeth.Length - 1];
                    Array.Resize(ref lastTooth, lastToothLength);

                    items = teeth;
                    Debug.Assert(GetLength(shift, items) == length);
                    return;
                }

                Array.Resize(ref teeth, (int)newTeethLength);

                ref var oldLastTooth = ref teeth[oldTeeth.Length - 1];
                Array.Resize(ref oldLastTooth, toothMaxLength);

                oldTeethLength = oldTeeth.Length;
            }

            Debug.Assert(teeth.Length > oldTeethLength);

            for (int i = oldTeethLength; ; ++i)
            {
                if (i == teeth.Length - 1)
                {
                    teeth[i] = new T[lastToothLength];
                    break;
                }
                else
                {
                    teeth[i] = new T[toothMaxLength];
                }
            }

            items = teeth;
            Debug.Assert(GetLength(shift, items) == length);
        }

        public IEnumerator<T> GetEnumerator() => new Enumerator(_items);

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public struct Enumerator : IEnumerator<T>
        {
            private readonly T[][]? _teeth;

            private T[]? _tooth;

            private int _teethIndex;

            private int _toothIndex;

            internal Enumerator(object? items)
            {
                if (items is null)
                {
                    _teeth = null;
                    _tooth = null;
                    _teethIndex = -1;
                }
                else if (items is T[] tooth)
                {
                    _teeth = null;
                    _tooth = tooth;
                    _teethIndex = 0;
                }
                else
                {
                    _teeth = (T[][])items;
                    _tooth = null;
                    _teethIndex = -1;
                }
                _toothIndex = -1;
            }

            public T Current => _tooth![_toothIndex];

            object IEnumerator.Current => Current;

            public void Dispose()
            {
            }

            public bool MoveNext()
            {
                _toothIndex += 1;
                if (_tooth == null || _toothIndex == _tooth.Length)
                {
                    _teethIndex += 1;
                    if (_teeth == null || _teethIndex == _teeth.Length)
                    {
                        _tooth = null;
                        return false;
                    }

                    _tooth = _teeth[_teethIndex];
                    _toothIndex = 0;
                }

                return true;
            }

            /// <exception cref="NotSupportedException"/>
            public void Reset() => throw new NotSupportedException();
        }
    }
}
