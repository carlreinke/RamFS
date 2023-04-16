// Copyright 2023 Carl Reinke
//
// This file is part of a program that is licensed under the terms of the GNU
// General Public License Version 3 as published by the Free Software
// Foundation.

using Fsp;
using System;

// https://learn.microsoft.com/en-us/windows/win32/api/winternl/nf-winternl-ntcreatefile
[Flags]
internal enum CreateOptions : uint
{
    None = 0,

    DirectoryFile = FileSystemBase.FILE_DIRECTORY_FILE,
    WriteThrough = FileSystemBase.FILE_WRITE_THROUGH,
    SequentialOnly = FileSystemBase.FILE_SEQUENTIAL_ONLY,
    NoIntermediateBuffering = FileSystemBase.FILE_NO_INTERMEDIATE_BUFFERING,

    SynchronousIOAlert = FileSystemBase.FILE_SYNCHRONOUS_IO_ALERT,
    SynchronousIONonAlert = FileSystemBase.FILE_SYNCHRONOUS_IO_NONALERT,
    NonDirectoryFile = FileSystemBase.FILE_NON_DIRECTORY_FILE,
    CreateTreeConnection = FileSystemBase.FILE_CREATE_TREE_CONNECTION,  // Should never be seen.

    CompleteIfOpLocked = FileSystemBase.FILE_COMPLETE_IF_OPLOCKED,
    NoEAKnowledge = FileSystemBase.FILE_NO_EA_KNOWLEDGE,
    OpenRemoteInstance = FileSystemBase.FILE_OPEN_REMOTE_INSTANCE,
    RandomAccess = FileSystemBase.FILE_RANDOM_ACCESS,

    DeleteOnClose = FileSystemBase.FILE_DELETE_ON_CLOSE,
    OpenByFileId = FileSystemBase.FILE_OPEN_BY_FILE_ID,
    OpenForBackupIntent = FileSystemBase.FILE_OPEN_FOR_BACKUP_INTENT,
    NoCompression = FileSystemBase.FILE_NO_COMPRESSION,

    OpenRequiringOpLock = FileSystemBase.FILE_OPEN_REQUIRING_OPLOCK,
    DisallowExclusive = 0x00020000,
    SessionAware = 0x00040000,

    ReserveOpFilter = FileSystemBase.FILE_RESERVE_OPFILTER,
    OpenReparsePoint = FileSystemBase.FILE_OPEN_REPARSE_POINT,
    OpenNoRecall = FileSystemBase.FILE_OPEN_NO_RECALL,
    OpenForFreeSpaceQuery = FileSystemBase.FILE_OPEN_FOR_FREE_SPACE_QUERY,
}
