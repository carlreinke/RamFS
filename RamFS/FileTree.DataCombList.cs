// Copyright 2023 Carl Reinke
//
// This file is part of a program that is licensed under the terms of the GNU
// General Public License Version 3 as published by the Free Software
// Foundation.

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

internal sealed partial class FileTree
{
    [DebuggerDisplay("Length = {Length}")]
    public struct DataCombList
    {
        /// <summary>
        /// Determines the maximum number of bytes in each tooth of the comb.
        /// </summary>
        private const int _shift = 20;

        private const int _mask = (1 << _shift) - 1;

        private const int _toothMaxLength = 1 << _shift;

        private object? _items;

        private ulong _length;

        public ulong Length => _length;

        public static ulong GetRoundedLength(ulong length)
        {
            ulong roundedLength = (length + _mask) & ~(ulong)_mask;
            return roundedLength == 0 ? length : roundedLength;
        }

        /// <exception cref="OutOfMemoryException"/>
        public void SetLength(ulong length)
        {
            if (_length == length)
                return;

            ulong oldLength = _length;

            try
            {
                if (length == 0)
                {
                    if (_items is null)
                    {
                        Debug.Assert(false);
                    }
                    else if (_items is IntPtr tooth)
                    {
                        _items = null;
                        _length = 0;

                        Marshal.FreeHGlobal(tooth);
                    }
                    else
                    {
                        var oldTeeth = (IntPtr[])_items;

                        _items = null;
                        _length = 0;

                        for (int i = 0; i < oldTeeth.Length; i++)
                            Marshal.FreeHGlobal(oldTeeth[i]);
                    }
                    return;
                }

                // Use one tooth if possible.
                if (length <= _toothMaxLength)
                {
                    if (_items is null)
                    {
                        _items = Marshal.AllocHGlobal((IntPtr)length);
                        _length = length;
                    }
                    else if (_items is IntPtr tooth)
                    {
                        _items = Marshal.ReAllocHGlobal(tooth, (IntPtr)length);
                        _length = length;
                    }
                    else
                    {
                        var oldTeeth = (IntPtr[])_items;

                        _items = Marshal.ReAllocHGlobal(oldTeeth[0], (IntPtr)length);
                        _length = length;

                        for (int i = 1; i < oldTeeth.Length; ++i)
                            Marshal.FreeHGlobal(oldTeeth[i]);
                    }
                    return;
                }

                ulong newTeethLength = length >> _shift;
                ulong lastToothLength = length & _mask;
                if (lastToothLength == 0)
                    lastToothLength = _toothMaxLength;
                else
                    newTeethLength += 1;
                if (newTeethLength > int.MaxValue)
                    throw new OutOfMemoryException();

                IntPtr[] teeth;
                int oldTeethLength;
                if (_items is null)
                {
                    teeth = new IntPtr[newTeethLength];
                    _items = teeth;

                    oldTeethLength = 0;
                }
                else if (_items is IntPtr temp)
                {
                    var tooth = temp;

                    teeth = new IntPtr[newTeethLength];
                    teeth[0] = tooth;
                    _items = teeth;

                    teeth[0] = Marshal.ReAllocHGlobal(tooth, (IntPtr)_toothMaxLength);
                    _length = _toothMaxLength;

                    oldTeethLength = 1;
                }
                else
                {
                    var oldTeeth = (IntPtr[])_items;
                    teeth = oldTeeth;

                    // Discount missing teeth.
                    oldTeethLength = oldTeeth.Length;
                    while (oldTeethLength > 0 && oldTeeth[oldTeethLength - 1] == IntPtr.Zero)
                        oldTeethLength -= 1;

                    if (newTeethLength <= (ulong)oldTeethLength)
                    {
                        // Shrinking or only need to resize last tooth.

                        Array.Resize(ref teeth, (int)newTeethLength);

                        ref var lastTooth = ref teeth[teeth.Length - 1];
                        lastTooth = Marshal.ReAllocHGlobal(lastTooth, (IntPtr)lastToothLength);
                        _items = teeth;
                        _length = length;

                        for (int i = teeth.Length; i < oldTeethLength; ++i)
                            Marshal.FreeHGlobal(oldTeeth[i]);
                        return;
                    }

                    Array.Resize(ref teeth, (int)newTeethLength);
                    _items = teeth;

                    ref var oldLastTooth = ref teeth[oldTeethLength - 1];
                    oldLastTooth = Marshal.ReAllocHGlobal(oldLastTooth, (IntPtr)_toothMaxLength);
                    _length = (ulong)oldTeethLength << _shift;
                }

                Debug.Assert(teeth.Length > oldTeethLength);

                for (int i = oldTeethLength; ; ++i)
                {
                    if (i == teeth.Length - 1)
                    {
                        teeth[i] = Marshal.AllocHGlobal((IntPtr)lastToothLength);
                        _length = length;
                        break;
                    }
                    else
                    {
                        teeth[i] = Marshal.AllocHGlobal((IntPtr)_toothMaxLength);
                        _length = (ulong)(i + 1) << _shift;
                    }
                }
            }
            finally
            {
                if (_length > oldLength)
                    GC.AddMemoryPressure((long)(_length - oldLength));
                else
                    GC.RemoveMemoryPressure((long)(oldLength - _length));
            }
        }

        public void Read(ulong offset, IntPtr destination, uint length)
        {
            Debug.Assert(offset <= _length);
            Debug.Assert(length <= _length - offset);

            if (length == 0)
                return;

            if (_items is IntPtr[] teeth)
            {
                ulong teethIndex = offset >> _shift;
                ulong toothOffset = offset & _mask;
                var source = IntPtr.Add(teeth[teethIndex], (int)toothOffset);
                ulong copyLength = _toothMaxLength - toothOffset;
                while (true)
                {
                    if (copyLength > length)
                        copyLength = length;

                    CopyMemory(destination, source, (IntPtr)copyLength);
                    destination = IntPtr.Add(destination, (int)copyLength);
                    length -= (uint)copyLength;

                    if (length == 0)
                        break;

                    teethIndex += 1;
                    source = teeth[teethIndex];
                    copyLength = _toothMaxLength;
                }
            }
            else
            {
                var tooth = (IntPtr)_items!;
                var source = IntPtr.Add(tooth, (int)offset);
                CopyMemory(destination, source, (IntPtr)length);
            }
        }

        public void Write(ulong offset, IntPtr source, uint length)
        {
            Debug.Assert(offset <= _length);
            Debug.Assert(length <= _length - offset);

            if (length == 0)
                return;

            if (_items is IntPtr[] teeth)
            {
                ulong teethIndex = offset >> _shift;
                ulong toothOffset = offset & _mask;
                var destination = IntPtr.Add(teeth[teethIndex], (int)toothOffset);
                ulong copyLength = _toothMaxLength - toothOffset;
                while (true)
                {
                    if (copyLength > length)
                        copyLength = length;

                    CopyMemory(destination, source, (IntPtr)copyLength);
                    source = IntPtr.Add(source, (int)copyLength);
                    length -= (uint)copyLength;

                    if (length == 0)
                        break;

                    teethIndex += 1;
                    destination = teeth[teethIndex];
                    copyLength = _toothMaxLength;
                }
            }
            else
            {
                var tooth = (IntPtr)_items!;
                var destination = IntPtr.Add(tooth, (int)offset);
                CopyMemory(destination, source, (IntPtr)length);
            }
        }

        [DllImport("kernel32", SetLastError = false)]
        public static extern void CopyMemory(IntPtr destination, IntPtr source, IntPtr length);
    }
}
