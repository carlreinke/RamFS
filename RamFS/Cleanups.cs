// Copyright 2023 Carl Reinke
//
// This file is part of a program that is licensed under the terms of the GNU
// General Public License Version 3 as published by the Free Software
// Foundation.

using Fsp;
using System;

[Flags]
internal enum Cleanups : uint
{
    None = 0,
    Delete = FileSystemBase.CleanupDelete,
    SetAllocationSize = FileSystemBase.CleanupSetAllocationSize,
    SetArchiveBit = FileSystemBase.CleanupSetArchiveBit,
    SetLastAccessTime = FileSystemBase.CleanupSetLastAccessTime,
    SetLastWriteTime = FileSystemBase.CleanupSetLastWriteTime,
    SetChangeTime = FileSystemBase.CleanupSetChangeTime,
}
