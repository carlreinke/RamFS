// Copyright 2023 Carl Reinke
//
// This file is part of a program that is licensed under the terms of the GNU
// General Public License Version 3 as published by the Free Software
// Foundation.

// https://learn.microsoft.com/en-us/windows/win32/api/winternl/nf-winternl-ntcreatefile
internal enum CreateDisposition : uint
{
    Supersede = 0,
    Open = 1,
    Create = 2,
    OpenIf = 3,
    Overwrite = 4,
    OverwriteIf = 5,
}
