// Copyright 2023 Carl Reinke
//
// This file is part of a program that is licensed under the terms of the GNU
// General Public License Version 3 as published by the Free Software
// Foundation.

using Fsp;
using Fsp.Interop;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.AccessControl;
using System.Text;

// [MS-FSA]: File System Algorithms
// https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-fsa/860b1516-c452-47b4-bdbc-625d344e2041
// [MS-FSCC]: File System Control Codes
// https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-fscc/efbfe127-73ad-4140-9967-ec6500e66d5e

internal sealed class RamFS : FileSystemBase
{
    private const string _defaultRootSddl = "O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;WD)";

    private const FileAttributes _supportedAttributes =
        FileAttributes.ReadOnly |
        FileAttributes.Hidden |
        FileAttributes.System |
        FileAttributes.Directory |
        FileAttributes.Archive |
        FileAttributes.Temporary |
        FileAttributes.NotContentIndexed;

    private readonly FileTree _fileTree;

    private readonly string _fileSystemName;

    private string _volumeLabel = "RAMFS";

    /// <exception cref="ArgumentException"/>
    public RamFS(ulong size, bool caseSensitive, string? fileSystemName, string? volumeLabel, GenericSecurityDescriptor? rootSecurityDescriptor)
    {
        _fileSystemName = fileSystemName ?? "RAMFS";
        _volumeLabel = volumeLabel ?? "RAM";

        if (rootSecurityDescriptor != null)
        {
            if (rootSecurityDescriptor.Owner == null || rootSecurityDescriptor.Group == null)
                throw new ArgumentException("Invalid security descriptor.", nameof(rootSecurityDescriptor));
        }
        else
        {
            rootSecurityDescriptor = new RawSecurityDescriptor(_defaultRootSddl);
        }

        byte[] rootSecurity = new byte[rootSecurityDescriptor.BinaryLength];
        rootSecurityDescriptor.GetBinaryForm(rootSecurity, 0);

        _fileTree = new FileTree(size, caseSensitive, rootSecurity);
    }

    public override int Init(object hostObj)
    {
        var host = (FileSystemHost)hostObj;
        host.CaseSensitiveSearch = _fileTree.CaseSensitive;
        host.CasePreservedNames = true;
        host.UnicodeOnDisk = true;
        host.PersistentAcls = true;
        host.ReparsePoints = true;
        host.ReparsePointsAccessCheck = false;
        host.NamedStreams = false;
        host.ExtendedAttributes = false;
        host.PassQueryDirectoryPattern = false;
        host.PassQueryDirectoryFileName = false;
        host.FlushAndPurgeOnCleanup = false;
        host.DeviceControl = false;
        host.WslFeatures = false;  // Requires support for extended attributes.
        host.SupportsPosixUnlinkRename = true;
        host.VolumeSerialNumber = (uint)Process.GetCurrentProcess().Id;
        host.SectorSize = 512;
        host.FileSystemName = _fileSystemName;
        host.MaxComponentLength = 255;
        host.SectorsPerAllocationUnit = 1;
        host.VolumeCreationTime = (ulong)DateTime.UtcNow.ToFileTimeUtc();

        return Log(STATUS_SUCCESS, null);
    }

    public override int GetVolumeInfo(
        out VolumeInfo volumeInfo)
    {
        ulong totalSize = _fileTree.TotalSize;
        ulong freeSize = _fileTree.GetFreeSize();

        volumeInfo = new VolumeInfo
        {
            TotalSize = totalSize,
            FreeSize = freeSize,
        };
        volumeInfo.SetVolumeLabel(_volumeLabel);
        return Log(STATUS_SUCCESS, null);
    }

    public override int SetVolumeLabel(
        string volumeLabel,
        out VolumeInfo volumeInfo)
    {
        _volumeLabel = volumeLabel;

        ulong totalSize = _fileTree.TotalSize;
        ulong freeSize = _fileTree.GetFreeSize();

        volumeInfo = new VolumeInfo
        {
            TotalSize = totalSize,
            FreeSize = freeSize,
        };
        volumeInfo.SetVolumeLabel(_volumeLabel);
        return Log(STATUS_SUCCESS, null);
    }

    public override int GetReparsePointByName(
        string fileName,
        bool isDirectory,
        ref byte[]? reparseData)
    {
        int result = Find(fileName, out _, out ulong nodeIndex);
        if (result != STATUS_SUCCESS)
            goto fail;

        ref readonly var fileTreeNode = ref _fileTree.Get(nodeIndex);

        if ((fileTreeNode.FileAttributes & FileAttributes.ReparsePoint) == 0)
        {
            result = STATUS_NOT_A_REPARSE_POINT;
            goto fail;
        }

        if ((fileTreeNode.FileAttributes & FileAttributes.Directory) != 0)
            Debug.Assert(!_fileTree.HasChildren(nodeIndex));

        reparseData = _fileTree.GetExtraData(nodeIndex);

        return Log(STATUS_SUCCESS, null, fileName);

    fail:
        Debug.Assert(result != STATUS_SUCCESS);
        reparseData = default;
        return Log(result, null, fileName);
    }

    public override int GetSecurityByName(
        string fileName,
        out uint fileAttributesValue, // or reparsePointIndex
        ref byte[]? securityDescriptor)
    {
        int result = Find(fileName, out _, out ulong nodeIndex);
        if (result != STATUS_SUCCESS)
        {
            if (result == STATUS_DIRECTORY_IS_A_REPARSE_POINT)
                if (FindReparsePoint(fileName, out fileAttributesValue))
                    return Log(STATUS_REPARSE, null, fileName);

            goto fail;
        }

        ref readonly var fileTreeNode = ref _fileTree.Get(nodeIndex);

        fileAttributesValue = (uint)fileTreeNode.FileAttributes;
        securityDescriptor = _fileTree.GetSecurity(nodeIndex);
        return Log(STATUS_SUCCESS, null, fileName);

    fail:
        Debug.Assert(result != STATUS_SUCCESS);
        fileAttributesValue = default;
        return Log(result, null, fileName);
    }

    public override int CreateEx(
        string fileName,
        uint createOptionsValue,
        uint grantedAccess,
        uint fileAttributesValue,
        byte[] securityDescriptor,
        ulong allocationSize,
        IntPtr extraBuffer,
        uint extraLength,
        bool extraBufferIsReparsePoint,
        out object? fileNodeObj,
        out object? fileDescObj,
        out FileInfo fileInfo,
        out string? normalizedName)
    {
        var createDisposition = (CreateDisposition)(createOptionsValue >> 24);
        var createOptions = (CreateOptions)(createOptionsValue & 0xFFFFFF);
        var fileAttributes = (FileAttributes)fileAttributesValue;
        Debug.Assert((fileAttributes & FileAttributes.Directory) == 0 || allocationSize == 0);
        Debug.Assert(extraBuffer == IntPtr.Zero || extraBufferIsReparsePoint);
        Debug.Assert(extraLength == 0 || extraBufferIsReparsePoint);

        fileAttributes &= _supportedAttributes;

        int result;

        if ((createOptions & CreateOptions.DirectoryFile) != 0 &&
            (fileAttributes & FileAttributes.Directory) == 0)
        {
            result = STATUS_INVALID_PARAMETER;
            goto fail;
        }

        if ((createOptions & CreateOptions.NonDirectoryFile) != 0 &&
            (fileAttributes & FileAttributes.Directory) != 0)
        {
            result = STATUS_INVALID_PARAMETER;
            goto fail;
        }

        if ((fileAttributes & FileAttributes.Directory) != 0 && allocationSize != 0)
        {
            result = STATUS_INVALID_PARAMETER;
            goto fail;
        }

        if ((fileAttributes & FileAttributes.Directory) == 0)
            fileAttributes |= FileAttributes.Archive;

        byte[]? reparseData;
        uint reparseTag;
        if (extraBuffer != IntPtr.Zero && extraBufferIsReparsePoint)
        {
            fileAttributes |= FileAttributes.ReparsePoint;

            try
            {
                reparseData = MakeReparsePoint(extraBuffer, extraLength);
            }
            catch (OutOfMemoryException)
            {
                result = STATUS_INSUFFICIENT_RESOURCES;
                goto fail;
            }

            reparseTag = GetReparseTag(reparseData);
        }
        else
        {
            reparseData = null;
            reparseTag = 0;
        }

        result = FindParent(fileName, out ulong parentNodeIndex, out int nameOffset);
        if (result != STATUS_SUCCESS)
            goto fail;

        if (nameOffset == fileName.Length)
        {
            Debug.Assert(parentNodeIndex == 0);
            result = STATUS_OBJECT_NAME_COLLISION;
            goto fail;
        }

        var name = new StringSegment(fileName, nameOffset);

        ulong now = (ulong)DateTime.UtcNow.ToFileTimeUtc();

        ulong nodeIndex;
        try
        {
            if (!_fileTree.Add(parentNodeIndex, name, fileAttributes, reparseTag, now, out nodeIndex))
            {
                result = STATUS_OBJECT_NAME_COLLISION;
                goto fail;
            }
        }
        catch (FileTree.FullException)
        {
            result = STATUS_DISK_FULL;
            goto fail;
        }
        catch (OutOfMemoryException)
        {
            result = STATUS_INSUFFICIENT_RESOURCES;
            goto fail;
        }

        try
        {
            _fileTree.SetSecurity(nodeIndex, securityDescriptor);
        }
        catch (FileTree.FullException)
        {
            _fileTree.Remove(parentNodeIndex, name);

            result = STATUS_DISK_FULL;
            goto fail;
        }

        try
        {
            if (extraBufferIsReparsePoint)
                _fileTree.SetExtraData(nodeIndex, reparseData);
        }
        catch (FileTree.FullException)
        {
            _fileTree.Remove(parentNodeIndex, name);

            result = STATUS_DISK_FULL;
            goto fail;
        }

        try
        {
            _fileTree.SetAllocationSize(nodeIndex, allocationSize);
        }
        catch (FileTree.FullException)
        {
            _fileTree.Remove(parentNodeIndex, name);

            result = STATUS_DISK_FULL;
            goto fail;
        }
        catch (OutOfMemoryException)
        {
            _fileTree.Remove(parentNodeIndex, name);

            result = STATUS_INSUFFICIENT_RESOURCES;
            goto fail;
        }

        ref readonly var fileTreeNode = ref _fileTree.Open(nodeIndex);

        var fileDesc = new FileDesc(nodeIndex);

        fileNodeObj = null;
        fileDescObj = fileDesc;
        fileInfo = ToFileInfo(nodeIndex, in fileTreeNode, allocationSize, eaSize: 0);
        normalizedName = Normalize(fileName);
        return Log(STATUS_SUCCESS, fileDesc, fileName);

    fail:
        Debug.Assert(result != STATUS_SUCCESS);
        fileNodeObj = default;
        fileDescObj = default;
        fileInfo = default;
        normalizedName = default;
        return Log(result, null, fileName);
    }

    public override int Open(
        string fileName,
        uint createOptionsValue,
        uint grantedAccess,
        out object? fileNodeObj,
        out object? fileDescObj,
        out FileInfo fileInfo,
        out string? normalizedName)
    {
        var createDisposition = (CreateDisposition)(createOptionsValue >> 24);
        var createOptions = (CreateOptions)(createOptionsValue & 0xFFFFFF);

        int result = Find(fileName, out _, out ulong nodeIndex);
        if (result != STATUS_SUCCESS)
            goto fail;

        ref readonly var fileTreeNode = ref _fileTree.Open(nodeIndex);

        if ((createOptions & CreateOptions.DirectoryFile) != 0 &&
            (fileTreeNode.FileAttributes & FileAttributes.Directory) == 0)
        {
            _fileTree.Close(nodeIndex);

            result = STATUS_NOT_A_DIRECTORY;
            goto fail;
        }

        if ((createOptions & CreateOptions.NonDirectoryFile) != 0 &&
            (fileTreeNode.FileAttributes & FileAttributes.Directory) != 0)
        {
            _fileTree.Close(nodeIndex);

            result = STATUS_FILE_IS_A_DIRECTORY;
            goto fail;
        }

        // TODO: FILE_NO_EA_KNOWLEDGE

        var fileDesc = new FileDesc(nodeIndex);

        ulong allocationSize = _fileTree.GetAllocationSize(nodeIndex);

        fileNodeObj = null;
        fileDescObj = fileDesc;
        fileInfo = ToFileInfo(nodeIndex, in fileTreeNode, allocationSize, eaSize: 0);
        normalizedName = Normalize(fileName);
        return Log(STATUS_SUCCESS, fileDesc, fileName);

    fail:
        Debug.Assert(result != STATUS_SUCCESS);
        fileNodeObj = default;
        fileDescObj = default;
        fileInfo = default;
        normalizedName = default;
        return Log(result, null, fileName);
    }

    public override void Close(
        object fileNodeObj,
        object fileDescObj)
    {
        Debug.Assert(fileNodeObj is null);
        var fileDesc = (FileDesc)fileDescObj;

        _fileTree.Close(fileDesc.NodeIndex);

        Log(fileDesc);
    }

    public override int CanDelete(
        object fileNodeObj,
        object fileDescObj,
        string fileName)
    {
        Debug.Assert(fileNodeObj is null);
        var fileDesc = (FileDesc)fileDescObj;

        ref readonly var fileTreeNode = ref _fileTree.Get(fileDesc.NodeIndex);

        if ((fileTreeNode.FileAttributes & FileAttributes.Directory) != 0)
        {
            // Root directory cannot be deleted.
            if (fileTreeNode.ParentNodeIndex == fileDesc.NodeIndex)
                return Log(STATUS_ACCESS_DENIED, fileDesc, fileName);

            if (_fileTree.HasChildren(fileDesc.NodeIndex))
                return Log(STATUS_DIRECTORY_NOT_EMPTY, fileDesc, fileName);
        }

        return Log(STATUS_SUCCESS, fileDesc, fileName);
    }

    public override void Cleanup(
        object fileNodeObj,
        object fileDescObj,
        string fileName,
        uint flags)
    {
        Debug.Assert(fileNodeObj is null);
        var fileDesc = (FileDesc)fileDescObj;
        var cleanups = (Cleanups)flags;

        ref readonly var fileTreeNode = ref _fileTree.Get(fileDesc.NodeIndex);

        if ((flags & CleanupSetAllocationSize) != 0)
        {
            try
            {
                Debug.Assert((fileTreeNode.FileAttributes & FileAttributes.Directory) == 0);

                _fileTree.SetAllocationSize(fileDesc.NodeIndex, fileTreeNode.FileSize);
            }
            catch (FileTree.FullException)
            {
                // The file allocation size is not growing.
                throw new UnreachableException();
            }
            catch (OutOfMemoryException)
            {
                // Ignore.  Reporting an error is not possible.
            }
        }

        var newFileAttributes = fileTreeNode.FileAttributes;
        ulong newCreationTime = fileTreeNode.CreationTime;
        ulong newLastAccessTime = fileTreeNode.LastAccessTime;
        ulong newLastWriteTime = fileTreeNode.LastWriteTime;
        ulong newChangeTime = fileTreeNode.ChangeTime;

        if ((cleanups & Cleanups.SetArchiveBit) != 0)
        {
            Debug.Assert((newFileAttributes & FileAttributes.Directory) == 0);

            newFileAttributes |= FileAttributes.Archive;
        }

        if ((cleanups & (Cleanups.SetLastAccessTime | Cleanups.SetLastWriteTime | Cleanups.SetChangeTime)) != 0)
        {
            ulong now = (ulong)DateTime.UtcNow.ToFileTimeUtc();

            if ((cleanups & Cleanups.SetLastAccessTime) != 0)
                newLastAccessTime = now;

            if ((cleanups & Cleanups.SetLastWriteTime) != 0)
                newLastWriteTime = now;

            if ((cleanups & Cleanups.SetChangeTime) != 0)
                newChangeTime = now;
        }

        ref readonly var fileTreeNode2 = ref _fileTree.SetAndGet(fileDesc.NodeIndex, newFileAttributes, newCreationTime, newLastAccessTime, newLastWriteTime, newChangeTime);
        Debug.Assert(Unsafe2.AreSame(fileTreeNode, fileTreeNode2));

        if ((cleanups & Cleanups.Delete) != 0)
        {
            // Root directory or non-empty directory cannot be deleted.
            if ((fileTreeNode.FileAttributes & FileAttributes.Directory) == 0 ||
                (fileTreeNode.ParentNodeIndex != fileDesc.NodeIndex &&
                 !_fileTree.HasChildren(fileDesc.NodeIndex)))
            {
                int result = FindParent(fileName, out ulong parentNodeIndex, out int nameOffset);
                if (result == STATUS_SUCCESS)
                {
                    var name = new StringSegment(fileName, nameOffset);

                    _fileTree.Remove(parentNodeIndex, name);
                }
                else
                {
                    Debugger.Break();
                }
            }
        }

        Log(fileDesc, fileName);
    }

    public override int DeleteReparsePoint(
        object fileNodeObj,
        object fileDescObj,
        string fileName,
        byte[] reparseData)
    {
        Debug.Assert(fileNodeObj is null);
        var fileDesc = (FileDesc)fileDescObj;

        Debug.Assert(reparseData != null);

        ref readonly var fileTreeNode = ref _fileTree.Get(fileDesc.NodeIndex);

        if ((fileTreeNode.FileAttributes & FileAttributes.ReparsePoint) == 0)
            return Log(STATUS_NOT_A_REPARSE_POINT, fileDesc, fileName);

        byte[] oldReparseData = _fileTree.GetExtraData(fileDesc.NodeIndex);

        int result = CanReplaceReparsePoint(oldReparseData, reparseData);
        if (result != STATUS_SUCCESS)
            return Log(result, fileDesc, fileName);

        try
        {
            _fileTree.SetExtraData(fileDesc.NodeIndex, null);
        }
        catch (FileTree.FullException)
        {
            throw new UnreachableException();
        }

        var fileAttributes = fileTreeNode.FileAttributes & ~FileAttributes.ReparsePoint;
        _fileTree.Set(fileDesc.NodeIndex, fileAttributes, 0);

        return Log(STATUS_SUCCESS, fileDesc, fileName);
    }

    public override int Flush(
        object fileNodeObj,
        object fileDescObj,
        out FileInfo fileInfo)
    {
        Debug.Assert(fileNodeObj is null);
        var fileDesc = (FileDesc)fileDescObj;

        ref readonly var fileTreeNode = ref _fileTree.Get(fileDesc.NodeIndex);

        ulong allocationSize = _fileTree.GetAllocationSize(fileDesc.NodeIndex);

        fileInfo = ToFileInfo(fileDesc.NodeIndex, in fileTreeNode, allocationSize, eaSize: 0);
        return Log(STATUS_SUCCESS, fileDesc);
    }

    public override int GetDirInfoByName(
        object fileNodeObj,
        object fileDescObj,
        string fileName,
        out string? normalizedName,
        out FileInfo fileInfo)
    {
        Debug.Assert(fileNodeObj is null);
        var fileDesc = (FileDesc)fileDescObj;

        ref readonly var fileTreeNode = ref _fileTree.Get(fileDesc.NodeIndex);

        int result;

        if ((fileTreeNode.FileAttributes & FileAttributes.Directory) == 0)
        {
            result = STATUS_NOT_A_DIRECTORY;
            goto fail;
        }

        var name = new StringSegment(fileName);

        if (!_fileTree.Find(fileDesc.NodeIndex, name, out ulong childNodeIndex, out normalizedName))
        {
            result = STATUS_OBJECT_NAME_NOT_FOUND;
            goto fail;
        }

        ref readonly var childFileTreeNode = ref _fileTree.Get(childNodeIndex);

        ulong childAllocationSize = _fileTree.GetAllocationSize(childNodeIndex);

        fileInfo = ToFileInfo(childNodeIndex, in childFileTreeNode, childAllocationSize, eaSize: 0);
        return Log(STATUS_SUCCESS, fileDesc, fileName);

    fail:
        normalizedName = default;
        fileInfo = default;
        return Log(result, fileDesc, fileName);
    }

    // GetEa base implementation calls GetEaEntry

    public override bool GetEaEntry(
        object fileNodeObj,
        object fileDescObj,
        ref object context,
        out string eaName,
        out byte[] eaValue,
        out bool needEa)
    {
        Debug.Assert(fileNodeObj is null);
        var fileDesc = (FileDesc)fileDescObj;

        // TODO
        Debugger.Break();

        return base.GetEaEntry(fileNodeObj, fileDescObj, ref context, out eaName, out eaValue, out needEa);
    }

    public override int GetFileInfo(
        object fileNodeObj,
        object fileDescObj,
        out FileInfo fileInfo)
    {
        Debug.Assert(fileNodeObj is null);
        var fileDesc = (FileDesc)fileDescObj;

        ref readonly var fileTreeNode = ref _fileTree.Get(fileDesc.NodeIndex);

        ulong allocationSize = _fileTree.GetAllocationSize(fileDesc.NodeIndex);

        fileInfo = ToFileInfo(fileDesc.NodeIndex, in fileTreeNode, allocationSize, eaSize: 0);
        return Log(STATUS_SUCCESS, fileDesc);
    }

    public override int GetReparsePoint(
        object fileNodeObj,
        object fileDescObj,
        string fileName,
        ref byte[]? reparseData)
    {
        Debug.Assert(fileNodeObj is null);
        var fileDesc = (FileDesc)fileDescObj;

        ref readonly var fileTreeNode = ref _fileTree.Get(fileDesc.NodeIndex);

        int result;

        if ((fileTreeNode.FileAttributes & FileAttributes.ReparsePoint) == 0)
        {
            result = STATUS_NOT_A_REPARSE_POINT;
            goto fail;
        }

        if ((fileTreeNode.FileAttributes & FileAttributes.Directory) != 0)
            Debug.Assert(!_fileTree.HasChildren(fileDesc.NodeIndex));

        reparseData = _fileTree.GetExtraData(fileDesc.NodeIndex);

        return Log(STATUS_SUCCESS, null, fileName);

    fail:
        Debug.Assert(result != STATUS_SUCCESS);
        reparseData = default;
        return Log(result, null, fileName);
    }

    // GetStreamInfo base implementation calls GetStreamEntry

    public override bool GetStreamEntry(
        object fileNodeObj,
        object fileDescObj,
        ref object context,
        out string streamName,
        out ulong streamSize,
        out ulong streamAllocationSize)
    {
        Debug.Assert(fileNodeObj is null);
        var fileDesc = (FileDesc)fileDescObj;

        // TODO
        Debugger.Break();

        return base.GetStreamEntry(fileNodeObj, fileDescObj, ref context, out streamName, out streamSize, out streamAllocationSize);
    }

    public override int GetSecurity(
        object fileNodeObj,
        object fileDescObj,
        ref byte[] securityDescriptor)
    {
        Debug.Assert(fileNodeObj is null);
        var fileDesc = (FileDesc)fileDescObj;

        securityDescriptor = _fileTree.GetSecurity(fileDesc.NodeIndex);
        return Log(STATUS_SUCCESS, fileDesc);
    }

    public override int OverwriteEx(
        object fileNodeObj,
        object fileDescObj,
        uint fileAttributesValue,
        bool replaceFileAttributes,
        ulong allocationSize,
        IntPtr ea,
        uint eaLength,
        out FileInfo fileInfo)
    {
        Debug.Assert(fileNodeObj is null);
        var fileDesc = (FileDesc)fileDescObj;
        var fileAttributes = (FileAttributes)fileAttributesValue;
        Debug.Assert(ea == IntPtr.Zero);
        Debug.Assert(eaLength == 0);

        fileAttributes &= _supportedAttributes;

        ref readonly var fileTreeNode = ref _fileTree.Get(fileDesc.NodeIndex);

        if (!replaceFileAttributes)
            fileAttributes |= fileTreeNode.FileAttributes;

        fileAttributes |= FileAttributes.Archive;

        if ((fileAttributes & FileAttributes.Directory) != 0)
            throw new UnreachableException();

        int result;

        if ((fileTreeNode.FileAttributes & FileAttributes.Directory) != 0)
        {
            result = STATUS_FILE_IS_A_DIRECTORY;
            goto fail;
        }

        try
        {
            _fileTree.SetAllocationSize(fileDesc.NodeIndex, allocationSize);
        }
        catch (FileTree.FullException)
        {
            result = STATUS_DISK_FULL;
            goto fail;
        }
        catch (OutOfMemoryException)
        {
            result = STATUS_INSUFFICIENT_RESOURCES;
            goto fail;
        }

        _fileTree.RemoveChildren(fileDesc.NodeIndex);

        ulong now = (ulong)DateTime.UtcNow.ToFileTimeUtc();

        ref readonly var fileTreeNode2 = ref _fileTree.ResetAndGet(fileDesc.NodeIndex, fileAttributes, 0, now);
        Debug.Assert(Unsafe2.AreSame(fileTreeNode, fileTreeNode2));

        fileInfo = ToFileInfo(fileDesc.NodeIndex, in fileTreeNode2, allocationSize, eaSize: 0);
        return Log(STATUS_SUCCESS, fileDesc);

    fail:
        fileInfo = default;
        return Log(result, fileDesc);
    }

    public override int Read(
        object fileNodeObj,
        object fileDescObj,
        IntPtr buffer,
        ulong offset,
        uint length,
        out uint bytesTransferred)
    {
        Debug.Assert(fileNodeObj is null);
        var fileDesc = (FileDesc)fileDescObj;

        ref readonly var fileTreeNode = ref _fileTree.Get(fileDesc.NodeIndex);

        int result;

        if ((fileTreeNode.FileAttributes & FileAttributes.Directory) != 0)
        {
            result = STATUS_FILE_IS_A_DIRECTORY;
            goto fail;
        }

        if (offset >= fileTreeNode.FileSize)
        {
            result = STATUS_END_OF_FILE;
            goto fail;
        }

        bytesTransferred = _fileTree.ReadData(fileDesc.NodeIndex, offset, buffer, length);

        return Log(STATUS_SUCCESS, fileDesc);

    fail:
        bytesTransferred = default;
        return Log(result, fileDesc);
    }

    // ReadDirectory base implementation calls ReadDirectoryEntry

    public override bool ReadDirectoryEntry(
        object fileNodeObj,
        object fileDescObj,
        string? pattern,
        string? marker,
        ref object? context,
        out string? fileName,
        out FileInfo fileInfo)
    {
        Debug.Assert(fileNodeObj is null);
        var fileDesc = (FileDesc)fileDescObj;

        IEnumerator<FileTree.Child> enumerator;
        if (context is null)
        {
            ref readonly var fileTreeNode = ref _fileTree.Get(fileDesc.NodeIndex);

            if ((fileTreeNode.FileAttributes & FileAttributes.Directory) == 0)
            {
                fileName = default;
                fileInfo = default;
                return false;
            }

            enumerator = _fileTree.GetChildren(fileDesc.NodeIndex, marker == "." || marker == ".." ? null : marker);

            // Root directory does not have "." and "..".
            if (fileTreeNode.ParentNodeIndex != fileDesc.NodeIndex)
                enumerator = AddDotAndDotDot(marker, fileDesc.NodeIndex, fileTreeNode.ParentNodeIndex, enumerator);

            context = enumerator;
        }
        else
        {
            enumerator = (IEnumerator<FileTree.Child>)context;
        }

        if (enumerator.MoveNext())
        {
            var child = enumerator.Current;

            ref readonly var childFileTreeNode = ref _fileTree.Get(child.NodeIndex);

            ulong childAllocationSize = _fileTree.GetAllocationSize(fileDesc.NodeIndex);

            fileName = child.Name;
            fileInfo = ToFileInfo(child.NodeIndex, in childFileTreeNode, childAllocationSize, eaSize: 0);
            return true;
        }
        else
        {
            fileName = default;
            fileInfo = default;
            return false;
        }

        static IEnumerator<FileTree.Child> AddDotAndDotDot(string? marker, ulong nodeIndex, ulong parentNodeIndex, IEnumerator<FileTree.Child> enumerator)
        {
            if (marker == null)
                yield return new FileTree.Child { Name = ".", NodeIndex = nodeIndex };
            if (marker == null || marker == ".")
                yield return new FileTree.Child { Name = "..", NodeIndex = parentNodeIndex };
            while (enumerator.MoveNext())
                yield return enumerator.Current;
        }
    }

    public override int Rename(
        object fileNodeObj,
        object fileDescObj,
        string fileName,
        string newFileName,
        bool replaceIfExists)
    {
        Debug.Assert(fileNodeObj is null);
        var fileDesc = (FileDesc)fileDescObj;

        int result = FindParent(fileName, out ulong parentNodeIndex, out int nameOffset);
        if (result != STATUS_SUCCESS)
            return Log(result, fileDesc, fileName);

        if (nameOffset == fileName.Length)
        {
            Debug.Assert(false);
            return Log(STATUS_INVALID_PARAMETER, fileDesc, fileName);
        }

        var name = new StringSegment(fileName, nameOffset);

        result = FindParent(newFileName, out ulong newParentNodeIndex, out int newNameOffset);
        if (result != STATUS_SUCCESS)
            return Log(result, fileDesc, fileName);

        if (newNameOffset == newFileName.Length)
        {
            Debug.Assert(false);
            return Log(STATUS_INVALID_PARAMETER, fileDesc, fileName);
        }

        var newName = new StringSegment(newFileName, newNameOffset);

        if (_fileTree.Find(newParentNodeIndex, newName, out ulong existingNodeIndex, out string? normalizedExistingName))
        {
            if (newParentNodeIndex == parentNodeIndex && name == normalizedExistingName)
            {
                // The source and the destination are the same.  The
                // normalized name may need to be updated; if not, the
                // rename is a no-op.
                try
                {
                    _fileTree.Move(newParentNodeIndex, name, newName);
                }
                catch (FileTree.FullException)
                {
                    // Existing child is being replaced, so used size should not
                    // increase.
                    throw new UnreachableException();
                }
                catch (OutOfMemoryException)
                {
                    return Log(STATUS_INSUFFICIENT_RESOURCES, fileDesc, fileName);
                }
            }
            else
            {
                if (!replaceIfExists)
                    return Log(STATUS_OBJECT_NAME_COLLISION, fileDesc, fileName);

                ref readonly var existingFileTreeNode = ref _fileTree.Get(existingNodeIndex);

                if ((existingFileTreeNode.FileAttributes & FileAttributes.Directory) != 0)
                    return Log(STATUS_ACCESS_DENIED, fileDesc, fileName);

                try
                {
                    _fileTree.Move(parentNodeIndex, name, newParentNodeIndex, newName);
                }
                catch (FileTree.FullException)
                {
                    // Existing child is being replaced, so used size should not
                    // increase.
                    throw new UnreachableException();
                }
                catch (OutOfMemoryException)
                {
                    return Log(STATUS_INSUFFICIENT_RESOURCES, fileDesc, fileName);
                }
            }
        }
        else
        {
            try
            {
                _fileTree.Move(parentNodeIndex, name, newParentNodeIndex, newName);
            }
            catch (FileTree.FullException)
            {
                return Log(STATUS_DISK_FULL, fileDesc, fileName);
            }
            catch (OutOfMemoryException)
            {
                return Log(STATUS_INSUFFICIENT_RESOURCES, fileDesc, fileName);
            }
        }

        return Log(STATUS_SUCCESS, fileDesc, fileName);
    }

    // ResolveReparsePoints base implementation calls GetReparsePointByName

    // SetDelete base implementation calls CanDelete

    public override int SetBasicInfo(
        object fileNodeObj,
        object fileDescObj,
        uint fileAttributesValue,
        ulong creationTime,
        ulong lastAccessTime,
        ulong lastWriteTime,
        ulong changeTime,
        out FileInfo fileInfo)
    {
        Debug.Assert(fileNodeObj is null);
        var fileDesc = (FileDesc)fileDescObj;
        var fileAttributes = (FileAttributes)fileAttributesValue;

        if (fileAttributesValue != ~0u)
            fileAttributes &= _supportedAttributes;

        ref readonly var fileTreeNode = ref _fileTree.Get(fileDesc.NodeIndex);

        var newFileAttributes = fileTreeNode.FileAttributes;
        ulong newCreationTime = fileTreeNode.CreationTime;
        ulong newLastAccessTime = fileTreeNode.LastAccessTime;
        ulong newLastWriteTime = fileTreeNode.LastWriteTime;
        ulong newChangeTime = fileTreeNode.ChangeTime;

        int result;

        if (fileAttributesValue != ~0u)
        {
            if (((fileTreeNode.FileAttributes ^ fileAttributes) & FileAttributes.Directory) != 0)
            {
                result = STATUS_ACCESS_DENIED;
                goto fail;
            }

            newFileAttributes = fileAttributes;
        }

        if (creationTime != 0)
            newCreationTime = creationTime;

        if (lastAccessTime != 0)
            newLastAccessTime = lastAccessTime;

        if (lastWriteTime != 0)
            newLastWriteTime = lastWriteTime;

        if (changeTime != 0)
            newChangeTime = changeTime;

        ref readonly var fileTreeNode2 = ref _fileTree.SetAndGet(fileDesc.NodeIndex, newFileAttributes, newCreationTime, newLastAccessTime, newLastWriteTime, newChangeTime);
        Debug.Assert(Unsafe2.AreSame(fileTreeNode, fileTreeNode2));

        ulong allocationSize = _fileTree.GetAllocationSize(fileDesc.NodeIndex);

        fileInfo = ToFileInfo(fileDesc.NodeIndex, in fileTreeNode2, allocationSize, eaSize: 0);
        return Log(STATUS_SUCCESS, fileDesc);

    fail:
        fileInfo = default;
        return Log(result, fileDesc);
    }

    // SetEa base implementation calls SetEaEntry

    public override int SetEaEntry(
        object fileNodeObj,
        object fileDescObj,
        ref object context,
        string eaName,
        byte[] eaValue,
        bool needEa)
    {
        Debug.Assert(fileNodeObj is null);
        var fileDesc = (FileDesc)fileDescObj;

        // TODO
        Debugger.Break();

        return Log(base.SetEaEntry(fileNodeObj, fileDescObj, ref context, eaName, eaValue, needEa), fileDesc);
    }

    public override int SetFileSize(
        object fileNodeObj,
        object fileDescObj,
        ulong newSize,
        bool setAllocationSize,
        out FileInfo fileInfo)
    {
        Debug.Assert(fileNodeObj is null);
        var fileDesc = (FileDesc)fileDescObj;

        ref readonly var fileTreeNode = ref _fileTree.Get(fileDesc.NodeIndex);

        int result;

        if ((fileTreeNode.FileAttributes & FileAttributes.Directory) != 0)
        {
            result = STATUS_FILE_IS_A_DIRECTORY;
            goto fail;
        }

        ulong allocationSize;

        if (setAllocationSize)
        {
            try
            {
                _fileTree.SetAllocationSize(fileDesc.NodeIndex, newSize);
            }
            catch (FileTree.FullException)
            {
                result = STATUS_DISK_FULL;
                goto fail;
            }
            catch (OutOfMemoryException)
            {
                result = STATUS_INSUFFICIENT_RESOURCES;
                goto fail;
            }

            allocationSize = newSize;
        }
        else
        {
            try
            {
                _fileTree.SetFileSize(fileDesc.NodeIndex, newSize);
            }
            catch (FileTree.FullException)
            {
                result = STATUS_DISK_FULL;
                goto fail;
            }
            catch (OutOfMemoryException)
            {
                result = STATUS_INSUFFICIENT_RESOURCES;
                goto fail;
            }

            allocationSize = _fileTree.GetAllocationSize(fileDesc.NodeIndex);
        }

        ref readonly var fileTreeNode2 = ref _fileTree.Get(fileDesc.NodeIndex);
        Debug.Assert(Unsafe2.AreSame(fileTreeNode, fileTreeNode2));

        fileInfo = ToFileInfo(fileDesc.NodeIndex, in fileTreeNode, allocationSize, eaSize: 0);
        return Log(STATUS_SUCCESS, fileDesc);

    fail:
        fileInfo = default;
        return Log(result, fileDesc);
    }

    public override int SetReparsePoint(
        object fileNodeObj,
        object fileDescObj,
        string fileName,
        byte[] reparseData)
    {
        Debug.Assert(fileNodeObj is null);
        var fileDesc = (FileDesc)fileDescObj;
        Debug.Assert(reparseData != null);

        ref readonly var fileTreeNode = ref _fileTree.Get(fileDesc.NodeIndex);

        if ((fileTreeNode.FileAttributes & FileAttributes.ReparsePoint) != 0)
        {
            byte[] oldReparseData = _fileTree.GetExtraData(fileDesc.NodeIndex);

            int result = CanReplaceReparsePoint(oldReparseData, reparseData);
            if (result != STATUS_SUCCESS)
                return Log(result, fileDesc, fileName);
        }
        else if ((fileTreeNode.FileAttributes & FileAttributes.Directory) != 0)
        {
            if (_fileTree.HasChildren(fileDesc.NodeIndex))
                return Log(STATUS_DIRECTORY_NOT_EMPTY, fileDesc, fileName);
        }

        try
        {
            _fileTree.SetExtraData(fileDesc.NodeIndex, reparseData);
        }
        catch (FileTree.FullException)
        {
            return Log(STATUS_DISK_FULL, fileDesc);
        }

        var fileAttributes = fileTreeNode.FileAttributes | FileAttributes.ReparsePoint;
        _fileTree.Set(fileDesc.NodeIndex, fileAttributes, GetReparseTag(reparseData));

        return Log(STATUS_SUCCESS, fileDesc, fileName);
    }

    public override int SetSecurity(
        object fileNodeObj,
        object fileDescObj,
        AccessControlSections sections,
        byte[] securityDescriptor)
    {
        Debug.Assert(fileNodeObj is null);
        var fileDesc = (FileDesc)fileDescObj;

        int result;

        try
        {
            result = _fileTree.ModifySecurity(fileDesc.NodeIndex, (sections, securityDescriptor), static (ref byte[] securityDescriptor, (AccessControlSections Sections, byte[] ModificationDescriptor) arg) =>
            {
                return ModifySecurityDescriptorEx(securityDescriptor, arg.Sections, arg.ModificationDescriptor, ref securityDescriptor);
            });
        }
        catch (FileTree.FullException)
        {
            return Log(STATUS_DISK_FULL, fileDesc);
        }
        catch (OutOfMemoryException)
        {
            return Log(STATUS_INSUFFICIENT_RESOURCES, fileDesc);
        }

        return Log(result, fileDesc);
    }

    public override int Write(
        object fileNodeObj,
        object fileDescObj,
        IntPtr buffer,
        ulong offset,
        uint length,
        bool writeToEndOfFile,
        bool constrainedIo,
        out uint bytesTransferred,
        out FileInfo fileInfo)
    {
        Debug.Assert(fileNodeObj is null);
        var fileDesc = (FileDesc)fileDescObj;

        ref readonly var fileTreeNode = ref _fileTree.Get(fileDesc.NodeIndex);

        int result;

        if ((fileTreeNode.FileAttributes & FileAttributes.Directory) != 0)
        {
            result = STATUS_FILE_IS_A_DIRECTORY;
            goto fail;
        }

        if (constrainedIo)
        {
            if (offset >= fileTreeNode.FileSize)
                length = 0;
            else if (length > fileTreeNode.FileSize - offset)
                length = (uint)(fileTreeNode.FileSize - offset);
        }
        else if (writeToEndOfFile)
        {
            offset = fileTreeNode.FileSize;
        }

        try
        {
            bytesTransferred = _fileTree.WriteData(fileDesc.NodeIndex, offset, buffer, length);
        }
        catch (FileTree.FullException)
        {
            result = STATUS_DISK_FULL;
            goto fail;
        }
        catch (OutOfMemoryException)
        {
            result = STATUS_INSUFFICIENT_RESOURCES;
            goto fail;
        }

        ulong allocationSize = _fileTree.GetAllocationSize(fileDesc.NodeIndex);

        fileInfo = ToFileInfo(fileDesc.NodeIndex, in fileTreeNode, allocationSize, eaSize: 0);
        return Log(STATUS_SUCCESS, fileDesc);

    fail:
        bytesTransferred = default;
        fileInfo = default;
        return Log(result, fileDesc);
    }

    private static void Log(in FileDesc? fileDesc, string? fileName = null, [CallerMemberName] string memberName = "")
    {
#if LOG
        string counter = fileDesc?.Counter.ToString("X8") ?? "--------";
        Console.Error.WriteLine($"-------- {counter} {memberName} {fileName}");
#endif
    }

    private static int Log(int status, in FileDesc? fileDesc, string? fileName = null, [CallerMemberName] string memberName = "")
    {
#if LOG
        string counter = fileDesc?.Counter.ToString("X8") ?? "--------";
        Console.Error.WriteLine($"{status:X8} {counter} {memberName} {fileName}");
#endif
        return status;
    }

    private static FileInfo ToFileInfo(ulong nodeIndex, in FileTree.Node fileTreeNode, ulong allocationSize, uint eaSize)
    {
        return new FileInfo
        {
            FileAttributes = (uint)fileTreeNode.FileAttributes,
            ReparseTag = fileTreeNode.ReparseTag,
            AllocationSize = allocationSize,
            FileSize = fileTreeNode.FileSize,
            CreationTime = fileTreeNode.CreationTime,
            LastAccessTime = fileTreeNode.LastAccessTime,
            LastWriteTime = fileTreeNode.LastWriteTime,
            ChangeTime = fileTreeNode.ChangeTime,
            IndexNumber = nodeIndex,
            HardLinks = 0,  // Not implemented in WinFSP.
            EaSize = eaSize,
        };
    }

    private int FindParent(string fileName, out ulong parentNodeIndex, out int nameOffset)
    {
        Debug.Assert(fileName.Length > 0 && fileName[0] == '\\');

        ulong nodeIndex = 0;

        int i = 1;
        for (int j = i; j < fileName.Length; ++j)
        {
            char c = fileName[j];
            if (c != '\\')
            {
                Debug.Assert(c >= ' ' && c != ':');
                continue;
            }

            var name = new StringSegment(fileName, i, j - i);

            if (!_fileTree.Find(nodeIndex, name, out ulong childNodeIndex, out _))
            {
                ref readonly var parentFileTreeNode = ref _fileTree.Get(nodeIndex);

                if ((parentFileTreeNode.FileAttributes & FileAttributes.Directory) != 0 &&
                    (parentFileTreeNode.FileAttributes & FileAttributes.ReparsePoint) != 0)
                {
                    parentNodeIndex = nodeIndex;
                    nameOffset = i;
                    return STATUS_DIRECTORY_IS_A_REPARSE_POINT;
                }

                parentNodeIndex = default;
                nameOffset = default;
                return STATUS_OBJECT_PATH_NOT_FOUND;
            }

            nodeIndex = childNodeIndex;

            i = j + 1;
        }

        ref readonly var fileTreeNode = ref _fileTree.Get(nodeIndex);

        if ((fileTreeNode.FileAttributes & FileAttributes.Directory) == 0)
        {
            parentNodeIndex = default;
            nameOffset = default;
            return STATUS_OBJECT_PATH_NOT_FOUND;
        }

        if ((fileTreeNode.FileAttributes & FileAttributes.ReparsePoint) != 0)
        {
            parentNodeIndex = nodeIndex;
            nameOffset = i;
            return STATUS_DIRECTORY_IS_A_REPARSE_POINT;
        }

        parentNodeIndex = nodeIndex;
        nameOffset = i;
        return STATUS_SUCCESS;
    }

    private int Find(string fileName, out ulong parentNodeIndex, out ulong nodeIndex)
    {
        int result = FindParent(fileName, out parentNodeIndex, out int nameOffset);
        if (result != STATUS_SUCCESS)
        {
            nodeIndex = default;
            return result;
        }

        if (nameOffset == fileName.Length)
        {
            Debug.Assert(fileName.Length == 1);
            nodeIndex = parentNodeIndex;
            return result;
        }

        var name = new StringSegment(fileName, nameOffset);

        return _fileTree.Find(parentNodeIndex, name, out nodeIndex, out _)
            ? STATUS_SUCCESS
            : STATUS_OBJECT_NAME_NOT_FOUND;
    }

    private string? Normalize(string fileName)
    {
        Debug.Assert(fileName.Length > 0 && fileName[0] == '\\');

        if (_fileTree.CaseSensitive)
            return null;

        StringBuilder? builder = null;

        ulong nodeIndex = 0;

        int i = 1;
        for (int j = i; i < fileName.Length; ++j)
        {
            if (j < fileName.Length)
            {
                char c = fileName[j];
                if (c != '\\')
                {
                    Debug.Assert(c >= ' ' && c != ':');
                    continue;
                }
            }

            var name = new StringSegment(fileName, i, j - i);

            if (!_fileTree.Find(nodeIndex, name, out nodeIndex, out string? normalizedName))
                throw new UnreachableException();

            if (builder != null)
            {
                builder.Append('\\').Append(normalizedName);
            }
            else if (name != normalizedName)
            {
                builder = new StringBuilder(fileName.Length);
                builder.Append(fileName, 0, i).Append(normalizedName);
            }

            i = j + 1;
        }

        return builder != null
            ? builder.ToString()
            : fileName;
    }

    [DebuggerDisplay("NodeIndex = {NodeIndex}")]
    private readonly struct FileDesc
    {
#if LOG
        private static int _counter = -1;
#endif

        public readonly ulong NodeIndex;

#if LOG
        public readonly uint Counter;
#endif

        public FileDesc(ulong nodeIndex)
        {
            NodeIndex = nodeIndex;
#if LOG
            Counter = (uint)System.Threading.Interlocked.Increment(ref _counter);
#endif
        }
    }
}
