// Copyright 2023 Carl Reinke
//
// This file is part of a program that is licensed under the terms of the GNU
// General Public License Version 3 as published by the Free Software
// Foundation.

using System;

using SystemFileAttributes = System.IO.FileAttributes;

// https://learn.microsoft.com/en-us/windows/win32/fileio/file-attribute-constants
// https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-fscc/ca28ec38-f155-4768-81d6-4bfeb8586fc9
[Flags]
internal enum FileAttributes : uint
{
    None = 0,

    ReadOnly = SystemFileAttributes.ReadOnly,
    Hidden = SystemFileAttributes.Hidden,
    System = SystemFileAttributes.System,

    Directory = SystemFileAttributes.Directory,
    Archive = SystemFileAttributes.Archive,
    Device = SystemFileAttributes.Device,
    Normal = SystemFileAttributes.Normal,

    Temporary = SystemFileAttributes.Temporary,
    SparseFile = SystemFileAttributes.SparseFile,
    ReparsePoint = SystemFileAttributes.ReparsePoint,
    Compressed = SystemFileAttributes.Compressed,

    Offline = SystemFileAttributes.Offline,
    NotContentIndexed = SystemFileAttributes.NotContentIndexed,
    Encrypted = SystemFileAttributes.Encrypted,
    IntegrityStream = SystemFileAttributes.IntegrityStream,

    Virtual = 0x00010000,
    NoScrubData = SystemFileAttributes.NoScrubData,
    EA = 0x00040000,
    Pinned = 0x00080000,

    Unpinned = 0x00100000,
}
