// Copyright 2023 Carl Reinke
//
// This file is part of a program that is licensed under the terms of the GNU
// General Public License Version 3 as published by the Free Software
// Foundation.

using System.Runtime.CompilerServices;

internal static class Unsafe2
{
    public static bool AreSame<T>(in T left, in T right)
    {
        return Unsafe.AreSame(ref Unsafe.AsRef(left), ref Unsafe.AsRef(right));
    }
}
