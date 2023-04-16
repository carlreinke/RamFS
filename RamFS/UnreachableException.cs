// Copyright 2023 Carl Reinke
//
// This file is part of a program that is licensed under the terms of the GNU
// General Public License Version 3 as published by the Free Software
// Foundation.

using System;

internal sealed class UnreachableException : Exception
{
    public UnreachableException()
        : base("The program executed an instruction that was thought to be unreachable.")
    {
    }
}
