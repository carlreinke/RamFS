// Copyright 2023 Carl Reinke
//
// This file is part of a program that is licensed under the terms of the GNU
// General Public License Version 3 as published by the Free Software
// Foundation.

using System;
using System.Diagnostics;

#pragma warning disable CS0660 // Type defines operator == or operator != but does not override Object.Equals(object o)
#pragma warning disable CS0661 // Type defines operator == or operator != but does not override Object.GetHashCode()
internal readonly struct StringSegment
#pragma warning restore CS0660 // Type defines operator == or operator != but does not override Object.Equals(object o)
#pragma warning restore CS0661 // Type defines operator == or operator != but does not override Object.GetHashCode()
{
    private readonly string _string;
    private readonly int _offset;
    private readonly int _length;

    public StringSegment(string str)
    {
        _string = str;
        _offset = 0;
        _length = str.Length;
    }

    public StringSegment(string str, int offset)
    {
        Debug.Assert(str.Length >= offset);

        _string = str;
        _offset = offset;
        _length = str.Length - offset;
    }

    public StringSegment(string str, int offset, int length)
    {
        Debug.Assert(str.Length - offset >= length);

        _string = str;
        _offset = offset;
        _length = length;
    }

    public int Length => _length;

    public static bool operator ==(StringSegment left, string right)
    {
        return left.EqualsOrdinal(right, ignoreCase: false);
    }

    public static bool operator !=(StringSegment left, string right)
    {
        return !left.EqualsOrdinal(right, ignoreCase: false);
    }

    public bool EqualsOrdinal(string other, bool ignoreCase)
    {
        return CompareOrdinalTo(other, ignoreCase) == 0;
    }

    public int CompareOrdinalTo(string other, bool ignoreCase)
    {
        int result = ignoreCase
            ? string.Compare(_string, _offset, other, 0, _length, StringComparison.OrdinalIgnoreCase)
            : string.CompareOrdinal(_string, _offset, other, 0, _length);
        // Only `_length` characters were compared, but `other` might be longer.
        return result == 0 && _length != other.Length ? -1 : result;
    }

    /// <exception cref="OutOfMemoryException"/>
    public override string ToString() => _string.Substring(_offset, _length);
}
