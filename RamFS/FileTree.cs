// Copyright 2023 Carl Reinke
//
// This file is part of a program that is licensed under the terms of the GNU
// General Public License Version 3 as published by the Free Software
// Foundation.

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

internal sealed partial class FileTree
{
    private const ulong _noParentNodeIndex = ~0uL;

    private const ulong _noFreeNodeIndex = 0uL;

    // Size of CombList arrays are omitted for simplicity.
    private const ulong _nodeSize =
        64 + // size of Node
        48;  // size of Node2

    // Size of CombList arrays are omitted for simplicity.
    private const ulong _childSize =
        16 + // size of Child
        26;  // size of String object header (Child.Name)

    private static readonly byte[] _empty = new byte[0];

    private readonly ulong _totalSize;

    private readonly bool _ignoreCase;

    // Held in read mode when reading/writing nodes.
    // Held in write mode when setting length of nodes.
    private readonly ReaderWriterLockSlim _nodesRefLock = new();

    private CombList<Node> _nodes;

    private CombList<Node2> _nodes2;

    private long _freeNodeIndex = (long)_noFreeNodeIndex;

    private long _freeSize;

    /// <exception cref="ArgumentException"/>
    public FileTree(ulong totalSize, bool caseSensitive, byte[] rootSecurity)
    {
        _totalSize = totalSize;
        _ignoreCase = !caseSensitive;

        _freeSize = (long)_totalSize;

        try
        {
            DecreaseFreeSize(_nodeSize);

            _nodes2.SetLength(1);
            _nodes.SetLength(1);

            ref var rootNode = ref _nodes[0];
            rootNode = new Node(FileAttributes.Directory);
            ulong now = (ulong)DateTime.UtcNow.ToFileTimeUtc();
            rootNode.CreationTime = now;
            rootNode.LastAccessTime = now;
            rootNode.LastWriteTime = now;
            rootNode.ChangeTime = now;

            SetSecurity(0, rootSecurity);
        }
        catch (FullException)
        {
            throw new ArgumentException("Insufficient size.", nameof(totalSize));
        }
        catch (OutOfMemoryException)
        {
#pragma warning disable Ex0100 // Member may throw undocumented exception
            throw;
#pragma warning restore Ex0100 // Member may throw undocumented exception
        }

        Debug.Assert(_nodes.GetEnumerator() != null);
        Debug.Assert(_nodes2.GetEnumerator() != null);
    }

    public ulong TotalSize => _totalSize;

    public bool CaseSensitive => !_ignoreCase;

    public ulong GetFreeSize() => (ulong)Interlocked.Read(ref _freeSize);

    /// <exception cref="FullException"/>
    /// <exception cref="OutOfMemoryException"/>
    /// <remarks>
    /// Mutually exclusive with the following methods:<br/>
    /// <see cref="Add(ulong, StringSegment, FileAttributes, uint, ulong, out ulong)"/>,<br/>
    /// <see cref="Find(ulong, StringSegment, out ulong, out string)"/>,<br/>
    /// <see cref="Move(ulong, StringSegment, StringSegment)"/>,<br/>
    /// <see cref="Move(ulong, StringSegment, ulong, StringSegment)"/>,<br/>
    /// <see cref="Remove(ulong, StringSegment)"/>,<br/>
    /// <see cref="RemoveChildren(ulong)"/>,<br/><!-- - - -->
    /// <see cref="HasChildren(ulong)"/>,<br/><!-- - - - - - - - -->
    /// <see cref="GetChildren(ulong)"/> (until enumeration is complete), and<br/>
    /// <see cref="GetChildren(ulong, string?)"/> (until enumeration is complete).
    /// </remarks>
    public bool Add(ulong parentNodeIndex, StringSegment name, FileAttributes fileAttributes, uint reparseTag, ulong times, out ulong nodeIndex)
    {
        using (_nodesRefLock.EnterUpgradeableReadScope())
        {
            if (_nodes2[parentNodeIndex].Children.Find(name, _ignoreCase, out _))
            {
                nodeIndex = default;
                return false;
            }

            DecreaseFreeSize(_childSize + (ulong)name.Length * 2);
            try
            {
                ref var node = ref AllocateNodeUnlocked(out ulong freeNodeIndex);
                try
                {
                    var child = new Child
                    {
                        Name = name.ToString(),
                        NodeIndex = freeNodeIndex,
                    };

                    // Allocating a node invalidates all other node references, so we can't reuse
                    // the same reference to the children when finding and adding a child.
                    _nodes2[parentNodeIndex].Children.Add(child, _ignoreCase);

                    node = new Node(fileAttributes);
                    node.ReparseTag = reparseTag;
                    Debug.Assert(node.FileSize == 0);
                    node.CreationTime = times;
                    node.LastAccessTime = times;
                    node.LastWriteTime = times;
                    node.ChangeTime = times;

                    if ((fileAttributes & FileAttributes.Directory) != 0)
                        node.ParentNodeIndex = parentNodeIndex;
                    else
                        node.LinkCount = 1;

                    Debug.Assert(node.OpenCount == 0);

#if SYNCHRONIZED
                    CheckNodes();
#endif

                    nodeIndex = freeNodeIndex;
                    return true;
                }
                catch
                {
                    FreeNodeUnlocked(freeNodeIndex, ref node);
                    throw;
                }
            }
            catch
            {
                IncreaseFreeSize(_childSize + (ulong)name.Length * 2);
                throw;
            }
        }
    }

    public bool Find(ulong parentNodeIndex, StringSegment name, out ulong nodeIndex, [MaybeNullWhen(false)] out string normalizedName)
    {
        using (_nodesRefLock.EnterReadScope())
        {
            ref var parentNode2 = ref _nodes2[parentNodeIndex];
            ref var children = ref parentNode2.Children;

            if (children.Find(name, _ignoreCase, out ulong childIndex))
            {
                ref var child = ref children[childIndex];

                nodeIndex = child.NodeIndex;
                normalizedName = child.Name;
                return true;
            }
            else
            {
                nodeIndex = default;
                normalizedName = null;
                return false;
            }
        }
    }

    /// <returns>The returned reference may or may not reflect subsequent changes to the node and is
    ///     invalid after the node is removed or is unlinked and closed.</returns>
    public ref readonly Node Get(ulong nodeIndex)
    {
        using (_nodesRefLock.EnterReadScope())
            return ref _nodes[nodeIndex];
    }

    /// <returns>The returned reference may or may not reflect subsequent changes to the node and is
    ///     invalid if the node is removed or is unlinked and closed.</returns>
    public ref readonly Node ResetAndGet(ulong nodeIndex, FileAttributes fileAttributes, uint reparseTag, ulong times)
    {
        using (_nodesRefLock.EnterReadScope())
        {
            ref var node = ref _nodes[nodeIndex];

            node.FileAttributes = fileAttributes;
            node.ReparseTag = reparseTag;
            node.FileSize = 0;
            node.CreationTime = times;
            node.LastAccessTime = times;
            node.LastWriteTime = times;
            node.ChangeTime = times;

            return ref node;
        }
    }

    /// <returns>The returned reference may or may not reflect subsequent changes to the node and is
    ///     invalid if the node is removed or is unlinked and closed.</returns>
    public ref readonly Node SetAndGet(ulong nodeIndex, FileAttributes fileAttributes, ulong creationTime, ulong lastAccessTime, ulong lastWriteTime, ulong changeTime)
    {
        using (_nodesRefLock.EnterReadScope())
        {
            ref var node = ref _nodes[nodeIndex];

            node.FileAttributes = fileAttributes;
            node.CreationTime = creationTime;
            node.LastAccessTime = lastAccessTime;
            node.LastWriteTime = lastWriteTime;
            node.ChangeTime = changeTime;

            return ref node;
        }
    }

    public void Set(ulong nodeIndex, FileAttributes fileAttributes, uint reparseTag)
    {
        using (_nodesRefLock.EnterReadScope())
        {
            ref var node = ref _nodes[nodeIndex];

            node.FileAttributes = fileAttributes;
            node.ReparseTag = reparseTag;
        }
    }

    /// <exception cref="FullException"/>
    /// <exception cref="OutOfMemoryException"/>
    public void Move(ulong parentNodeIndex, StringSegment srcName, StringSegment dstName)
    {
        using (_nodesRefLock.EnterReadScope())
        {
            ref var parentNode2 = ref _nodes2[parentNodeIndex];
            ref var children = ref parentNode2.Children;

            if (!children.Find(srcName, _ignoreCase, out ulong srcChildIndex))
                throw new UnreachableException();

            if (children.Find(dstName, _ignoreCase, out ulong dstChildIndex))
            {
                // Destination child exists.

                ref var dstChild = ref children[dstChildIndex];
                ulong dstNodeIndex = dstChild.NodeIndex;

                if (dstName != dstChild.Name)
                    dstChild.Name = dstName.ToString();

                if (srcChildIndex != dstChildIndex)
                {
                    // Destination child is not the same as source.  Replace the
                    // child; unlink the node.  Remove the source child.

                    ref var srcChild = ref children[srcChildIndex];
                    ulong srcNodeIndex = srcChild.NodeIndex;

                    dstChild.NodeIndex = srcNodeIndex;

                    UnlinkNodeUnlocked(dstNodeIndex, parentNodeIndex);

                    children.Remove(srcChildIndex, _ignoreCase);

                    IncreaseFreeSize(_childSize + (ulong)srcName.Length * 2);
                }
            }
            else
            {
                // Destination child does not exist.  Change the name of the
                // source child.

                DecreaseFreeSize((ulong)dstName.Length * 2);
                try
                {
                    string name = dstName.ToString();

                    ref var srcChild = ref children[srcChildIndex];

                    srcChild.Name = name;
                }
                catch
                {
                    IncreaseFreeSize((ulong)dstName.Length * 2);
                    throw;
                }

                IncreaseFreeSize((ulong)srcName.Length * 2);

                children.Reorder(srcChildIndex, _ignoreCase);
            }
        }
    }

    /// <exception cref="FullException"/>
    /// <exception cref="OutOfMemoryException"/>
    public void Move(ulong srcParentNodeIndex, StringSegment srcName, ulong dstParentNodeIndex, StringSegment dstName)
    {
        if (srcParentNodeIndex == dstParentNodeIndex)
        {
            Move(srcParentNodeIndex, srcName, dstName);
            return;
        }

        using (_nodesRefLock.EnterReadScope())
        {
            ref var srcParentNode2 = ref _nodes2[srcParentNodeIndex];
            ref var srcChildren = ref srcParentNode2.Children;

            if (!srcChildren.Find(srcName, _ignoreCase, out ulong srcChildIndex))
                throw new UnreachableException();

            ref var srcChild = ref srcChildren[srcChildIndex];
            ulong srcNodeIndex = srcChild.NodeIndex;

            ref var dstParentNode2 = ref _nodes2[dstParentNodeIndex];
            ref var dstChildren = ref dstParentNode2.Children;

            if (dstChildren.Find(dstName, _ignoreCase, out ulong dstChildIndex))
            {
                // Destination child exists.  Replace the child; unlink the node.

                ref var dstChild = ref dstChildren[dstChildIndex];
                ulong dstNodeIndex = dstChild.NodeIndex;

                if (dstName != dstChild.Name)
                    dstChild.Name = dstName.ToString();

                dstChild.NodeIndex = srcNodeIndex;

                UnlinkNodeUnlocked(dstNodeIndex, dstParentNodeIndex);
            }
            else
            {
                DecreaseFreeSize(_childSize + (ulong)dstName.Length * 2);
                try
                {
                    // Destination child does not exist.  Add it.

                    var dstChild = new Child
                    {
                        Name = dstName.ToString(),
                        NodeIndex = srcNodeIndex,
                    };

                    dstChildren.Add(dstChild, _ignoreCase);
                }
                catch
                {
                    IncreaseFreeSize(_childSize + (ulong)dstName.Length * 2);
                    throw;
                }
            }

            ref var node = ref _nodes[srcNodeIndex];

            if ((node.FileAttributes & FileAttributes.Directory) != 0)
                node.ParentNodeIndex = dstParentNodeIndex;

            // Remove the source child.

            srcChildren.Remove(srcChildIndex, _ignoreCase);

            IncreaseFreeSize(_childSize + (ulong)srcName.Length * 2);
        }
    }

    public void Remove(ulong parentNodeIndex, StringSegment name)
    {
        using (_nodesRefLock.EnterReadScope())
        {
            ref var parentNode2 = ref _nodes2[parentNodeIndex];
            ref var children = ref parentNode2.Children;

            if (!children.Find(name, _ignoreCase, out ulong childIndex))
                throw new UnreachableException();

            ref var child = ref children[childIndex];
            ulong nodeIndex = child.NodeIndex;

            children.Remove(childIndex, _ignoreCase);

            UnlinkNodeUnlocked(nodeIndex, parentNodeIndex);

            IncreaseFreeSize(_childSize + (ulong)name.Length * 2);
        }
    }

    public void RemoveChildren(ulong parentNodeIndex)
    {
        using (_nodesRefLock.EnterReadScope())
        {
            ref var parentNode2 = ref _nodes2[parentNodeIndex];

            RemoveChildNodesUnlocked(parentNodeIndex, ref parentNode2);
        }
    }

    /// <returns>The returned reference may or may not reflect subsequent changes to the node and is
    ///     invalid after the node is removed or is unlinked and closed.</returns>
    public ref readonly Node Open(ulong nodeIndex)
    {
        using (_nodesRefLock.EnterReadScope())
        {
            ref var node = ref _nodes[nodeIndex];

            long openCount = Interlocked.Increment(ref node.OpenCount);
            Debug.Assert(openCount != 0);

            return ref node;
        }
    }

    public void Close(ulong nodeIndex)
    {
        using (_nodesRefLock.EnterReadScope())
        {
            ref var node = ref _nodes[nodeIndex];

            long openCount = Interlocked.Decrement(ref node.OpenCount);
            Debug.Assert(openCount != -1);

            if (openCount == 0)
            {
                if ((node.FileAttributes & FileAttributes.Directory) != 0
                    ? node.ParentNodeIndex == _noParentNodeIndex
                    : node.LinkCount == 0)
                {
                    FreeNodeUnlocked(nodeIndex, ref node);
                }
            }
        }
    }

    public byte[] GetSecurity(ulong nodeIndex)
    {
        using (_nodesRefLock.EnterReadScope())
            return _nodes2[nodeIndex].SecurityDescriptor ?? _empty;
    }

    /// <exception cref="FullException"/>
    public void SetSecurity(ulong nodeIndex, byte[]? securityDescriptor)
    {
        using (_nodesRefLock.EnterReadScope())
        {
            ref var node2 = ref _nodes2[nodeIndex];

            SetSecurityUnlocked(ref node2, securityDescriptor);
        }
    }

    /// <exception cref="OutOfMemoryException"/>
    public delegate TResult NodeSecurityFunc<TArg, TResult>(ref byte[] securityDescriptor, TArg arg);

    /// <exception cref="FullException"/>
    /// <exception cref="OutOfMemoryException"/>
    public TResult ModifySecurity<TArg, TResult>(ulong nodeIndex, TArg arg, NodeSecurityFunc<TArg, TResult> func)
    {
        using (_nodesRefLock.EnterReadScope())
        {
            ref var node2 = ref _nodes2[nodeIndex];

            byte[] securityDescriptor = node2.SecurityDescriptor ?? _empty;

            var result = func(ref securityDescriptor, arg);

            if (securityDescriptor != node2.SecurityDescriptor)
                SetSecurityUnlocked(ref node2, securityDescriptor);

            return result;
        }
    }

    public byte[] GetExtraData(ulong nodeIndex)
    {
        using (_nodesRefLock.EnterReadScope())
            return _nodes2[nodeIndex].ExtraData ?? _empty;
    }

    /// <exception cref="FullException"/>
    public void SetExtraData(ulong nodeIndex, byte[]? data)
    {
        using (_nodesRefLock.EnterReadScope())
        {
            ref var node2 = ref _nodes2[nodeIndex];

            Debug.Assert(_nodesRefLock.IsReadLockHeld);

            DecreaseFreeSize((ulong)(data ?? _empty).Length);

            int oldExtraDataLength = (node2.ExtraData ?? _empty).Length;

            node2.ExtraData = data;

            IncreaseFreeSize((ulong)oldExtraDataLength);
        }
    }

    public ulong GetAllocationSize(ulong nodeIndex)
    {
        using (_nodesRefLock.EnterReadScope())
            return _nodes2[nodeIndex].Data.Length;
    }

    /// <exception cref="FullException"/>
    /// <exception cref="OutOfMemoryException"/>
    public void SetAllocationSize(ulong nodeIndex, ulong size)
    {
        using (_nodesRefLock.EnterReadScope())
        {
            ref var node2 = ref _nodes2[nodeIndex];

            SetAllocationSizeUnlocked(ref node2, size);

            ref var node = ref _nodes[nodeIndex];

            if (node.FileSize > size)
                node.FileSize = size;
        }
    }

    /// <exception cref="FullException"/>
    /// <exception cref="OutOfMemoryException"/>
    public void SetFileSize(ulong nodeIndex, ulong size)
    {
        using (_nodesRefLock.EnterReadScope())
        {
            ref var node2 = ref _nodes2[nodeIndex];

            if (node2.Data.Length < size)
                SetAllocationSizeUnlocked(ref node2, size);

            ref var node = ref _nodes[nodeIndex];

            node.FileSize = size;
        }
    }

    public uint ReadData(ulong nodeIndex, ulong offset, IntPtr destination, uint length)
    {
        using (_nodesRefLock.EnterReadScope())
        {
            ref var node = ref _nodes[nodeIndex];

            if (offset >= node.FileSize)
                return 0;

            ref var node2 = ref _nodes2[nodeIndex];

            // Ensure reading ends at end of file.
            if (length > node.FileSize - offset)
                length = (uint)(node.FileSize - offset);

            node2.Data.Read(offset, destination, length);
            return length;
        }
    }

    /// <exception cref="FullException"/>
    /// <exception cref="OutOfMemoryException"/>
    public uint WriteData(ulong nodeIndex, ulong offset, IntPtr source, uint length)
    {
        using (_nodesRefLock.EnterReadScope())
        {
            ref var node = ref _nodes[nodeIndex];
            ref var node2 = ref _nodes2[nodeIndex];

            // Ensure writing ends at ulong.MaxValue.
            if (length > ulong.MaxValue - offset)
                length = (uint)(ulong.MaxValue - offset);

            if (offset + length > node.FileSize)
            {
                ulong newFileSize = offset + length;

                ulong oldAllocatedSize = node2.Data.Length;

                if (newFileSize > oldAllocatedSize)
                {
                    ulong allocatedSize = DataCombList.GetRoundedLength(newFileSize);

                    try
                    {
                        DecreaseFreeSize(allocatedSize - oldAllocatedSize);
                    }
                    catch (FullException)
                    {
                        // Rounded length is too much; try actual length.
                        allocatedSize = newFileSize;

                    again:
                        try
                        {
                            DecreaseFreeSize(allocatedSize - oldAllocatedSize);
                        }
                        catch (FullException)
                        {
                            // Actual length is too much; try less.
                            allocatedSize = oldAllocatedSize + (allocatedSize - oldAllocatedSize) / 2;

                            if (allocatedSize > oldAllocatedSize)
                                goto again;

                            throw;
                        }
                    }
                    try
                    {
                        node2.Data.SetLength(allocatedSize);
                    }
                    catch (OutOfMemoryException)
                    {
                        // Resize may have partially succeeded.
                        ulong newAllocatedSize = node2.Data.Length;

                        if (newAllocatedSize == oldAllocatedSize)
                            throw;

                        IncreaseFreeSize(allocatedSize - newAllocatedSize);
                        allocatedSize = newAllocatedSize;
                    }
                    catch
                    {
                        throw new UnreachableException();
                    }

                    // Ensure file ends at end of allocation.  Ensure writing ends at end of file.
                    if (newFileSize > allocatedSize)
                    {
                        newFileSize = allocatedSize;
                        length = (uint)(newFileSize - offset);
                    }
                }

                node.FileSize = newFileSize;
            }

            node2.Data.Write(offset, source, length);
            return length;
        }
    }

    public bool HasChildren(ulong nodeIndex)
    {
        using (_nodesRefLock.EnterReadScope())
            return _nodes2[nodeIndex].Children.Length > 0;
    }

    public ChildCombList.Enumerator GetChildren(ulong nodeIndex)
    {
        using (_nodesRefLock.EnterReadScope())
            return _nodes2[nodeIndex].Children.GetEnumerator();
    }

    public ChildCombList.ListingEnumerator GetChildren(ulong nodeIndex, string? marker)
    {
        using (_nodesRefLock.EnterReadScope())
            return _nodes2[nodeIndex].Children.GetEnumerator(marker, _ignoreCase);
    }

    /// <exception cref="FullException"/>
    private void DecreaseFreeSize(ulong size)
    {
        while (true)
        {
            ulong freeSize = (ulong)Interlocked.Read(ref _freeSize);
            if (freeSize < size)
                throw new FullException();
            if (Interlocked.CompareExchange(ref _freeSize, (long)(freeSize - size), (long)freeSize) == (long)freeSize)
                break;
        }
    }

    private void IncreaseFreeSize(ulong size)
    {
        while (true)
        {
            ulong freeSize = (ulong)Interlocked.Read(ref _freeSize);
            Debug.Assert(freeSize + size >= freeSize && freeSize + size <= _totalSize);
            if (Interlocked.CompareExchange(ref _freeSize, (long)(freeSize + size), (long)freeSize) == (long)freeSize)
                break;
        }
    }

    /// <exception cref="FullException"/>
    /// <exception cref="OutOfMemoryException"/>
    private ref Node AllocateNodeUnlocked(out ulong nodeIndex)
    {
        Debug.Assert(_nodesRefLock.IsUpgradeableReadLockHeld);

    retry:
        ulong freeNodeIndex = (ulong)Interlocked.Read(ref _freeNodeIndex);

        if (freeNodeIndex != _noFreeNodeIndex)
        {
            ref var freeNode = ref _nodes[freeNodeIndex];

            if (Interlocked.CompareExchange(ref _freeNodeIndex, (long)freeNode.NextFreeNodeIndex, (long)freeNodeIndex) != (long)freeNodeIndex)
                goto retry;

            nodeIndex = freeNodeIndex;
            return ref freeNode;
        }
        else
        {
            DecreaseFreeSize(_nodeSize);
            try
            {
                using (_nodesRefLock.EnterWriteScope())
                {
                    ulong nodesLength = _nodes.Length;

                    freeNodeIndex = nodesLength;

                    _nodes2.SetLength(nodesLength + 1);
                    _nodes.SetLength(nodesLength + 1);
                }

                nodeIndex = freeNodeIndex;
                return ref _nodes[freeNodeIndex];
            }
            catch
            {
                IncreaseFreeSize(_nodeSize);
                throw;
            }
        }
    }

    private void RemoveChildNodesUnlocked(ulong parentNodeIndex, ref Node2 parentNode2)
    {
        Debug.Assert(_nodesRefLock.IsReadLockHeld);

        ref var children = ref parentNode2.Children;

        foreach (var child in children)
        {
            UnlinkNodeUnlocked(child.NodeIndex, parentNodeIndex);

            IncreaseFreeSize(_childSize + (ulong)child.Name.Length * 2);
        }

        children = default;
    }

    private void UnlinkNodeUnlocked(ulong nodeIndex, ulong parentNodeIndex)
    {
        Debug.Assert(_nodesRefLock.IsReadLockHeld);

        ref var node = ref _nodes[nodeIndex];

        if ((node.FileAttributes & FileAttributes.Directory) != 0)
        {
            Debug.Assert(node.ParentNodeIndex == parentNodeIndex);
            node.ParentNodeIndex = _noParentNodeIndex;

            // Defer removal if open.
            if (Interlocked.Read(ref node.OpenCount) != 0)
                return;
        }
        else
        {
            Debug.Assert(node.LinkCount > 0);
            node.LinkCount -= 1;

            // No removal if still linked.  Defer removal if open.
            if (node.LinkCount != 0 || Interlocked.Read(ref node.OpenCount) != 0)
                return;
        }

        FreeNodeUnlocked(nodeIndex, ref node);
    }

    private void FreeNodeUnlocked(ulong nodeIndex, ref Node node)
    {
        Debug.Assert(_nodesRefLock.IsReadLockHeld);

        ref var node2 = ref _nodes2[nodeIndex];

        RemoveChildNodesUnlocked(nodeIndex, ref node2);

        ulong securitySize = (ulong)(node2.SecurityDescriptor ?? _empty).Length;
        ulong extraDataSize = (ulong)(node2.ExtraData ?? _empty).Length;
        ulong dataSize = node2.Data.Length;

        node2.Free();

        node = default;

        IncreaseFreeSize(securitySize + extraDataSize + dataSize);

    retry:
        ulong freeNodeIndex = (ulong)Interlocked.Read(ref _freeNodeIndex);

        node.NextFreeNodeIndex = freeNodeIndex;

        if (Interlocked.CompareExchange(ref _freeNodeIndex, (long)nodeIndex, (long)freeNodeIndex) != (long)freeNodeIndex)
            goto retry;
    }

    /// <exception cref="FullException"/>
    private void SetSecurityUnlocked(ref Node2 node2, byte[]? securityDescriptor)
    {
        Debug.Assert(_nodesRefLock.IsReadLockHeld);

        DecreaseFreeSize((ulong)(securityDescriptor ?? _empty).Length);

        int oldSecurityLength = (node2.SecurityDescriptor ?? _empty).Length;

        node2.SecurityDescriptor = securityDescriptor;

        IncreaseFreeSize((ulong)oldSecurityLength);
    }

    /// <exception cref="FullException"/>
    /// <exception cref="OutOfMemoryException"/>
    private void SetAllocationSizeUnlocked(ref Node2 node2, ulong size)
    {
        Debug.Assert(_nodesRefLock.IsReadLockHeld);

        ulong oldSize = node2.Data.Length;

        if (size > oldSize)
            DecreaseFreeSize(size - oldSize);
        try
        {
            node2.Data.SetLength(size);
        }
        catch (OutOfMemoryException)
        {
            // Setting length may have partially succeeded.
            ulong newSize = node2.Data.Length;

            // Shrinking should never partially succeed.
            Debug.Assert(newSize >= oldSize);

            IncreaseFreeSize(size - newSize);
            throw;
        }
        catch
        {
            throw new UnreachableException();
        }

        if (oldSize > size)
            IncreaseFreeSize(oldSize - size);
    }

#if SYNCHRONIZED
    [Conditional("DEBUG")]
    private void CheckNodes()
    {
        ulong usedSize = _nodes.Length * _nodeSize;

        var usedNodeIndexes = new System.Collections.Generic.SortedSet<ulong>();

        var queue = new System.Collections.Generic.Queue<ulong>();

        if (usedNodeIndexes.Add(0))
            queue.Enqueue(0);

        while (queue.Count > 0)
        {
            ulong nodeIndex = queue.Dequeue();

            ref var node = ref _nodes[nodeIndex];

            Debug.Assert(node.FileAttributes != 0);

            ref var node2 = ref _nodes2[nodeIndex];

            foreach (var child in node2.Children)
            {
                ulong childNodeIndex = child.NodeIndex;
                string childName = child.Name;

                usedSize += _childSize + (ulong)childName.Length * 2;

                ref var childNode = ref _nodes[childNodeIndex];

                if ((childNode.FileAttributes & FileAttributes.Directory) != 0)
                    Debug.Assert(childNode.ParentNodeIndex == nodeIndex);

                if (usedNodeIndexes.Add(childNodeIndex))
                    queue.Enqueue(childNodeIndex);
            }
        }

        var freeNodeIndexes = new System.Collections.Generic.SortedSet<ulong>();

        ulong freeNodeIndex = (ulong)_freeNodeIndex;

        while (freeNodeIndex != _noFreeNodeIndex)
        {
            Debug.Assert(!usedNodeIndexes.Contains(freeNodeIndex));

            bool added = freeNodeIndexes.Add(freeNodeIndex);
            Debug.Assert(added);

            ref var node = ref _nodes[freeNodeIndex];

            Debug.Assert(node.FileAttributes == 0);

            freeNodeIndex = node.NextFreeNodeIndex;
        }

        ulong nodesLength = _nodes.Length;

        if ((ulong)usedNodeIndexes.Count + (ulong)freeNodeIndexes.Count < nodesLength)
            for (ulong i = 0; i < nodesLength; ++i)
                if (!usedNodeIndexes.Contains(i) && !freeNodeIndexes.Contains(i))
                    Debug.Assert(_nodes[i].OpenCount > 0);

        foreach (var node2 in _nodes2)
            usedSize += (ulong)(node2.SecurityDescriptor ?? _empty).Length +
                        (ulong)(node2.ExtraData ?? _empty).Length +
                        node2.Data.Length;

        Debug.Assert((ulong)_freeSize == _totalSize - usedSize);
    }
#endif

    [DebuggerDisplay("FileAttributes = {FileAttributes}")]
    public struct Node
    {
        public Node(FileAttributes fileAttributes)
        {
            _fileAttributes = FixNormal(fileAttributes);
            ReparseTag = default;
            _fileSize = default;
            CreationTime = default;
            LastAccessTime = default;
            LastWriteTime = default;
            ChangeTime = default;
            _union = default;
            OpenCount = default;
        }

        private FileAttributes _fileAttributes;
        public FileAttributes FileAttributes
        {
            get => _fileAttributes;
            set
            {
                Debug.Assert(_fileAttributes != 0);
                Debug.Assert(((_fileAttributes ^ value) & FileAttributes.Directory) == 0);
                _fileAttributes = FixNormal(value);
            }
        }
        public uint ReparseTag;
        private ulong _fileSize;
        public ulong FileSize
        {
            get => _fileSize;
            internal set => _fileSize = value;
        }
        public ulong CreationTime;
        public ulong LastAccessTime;
        public ulong LastWriteTime;
        public ulong ChangeTime;
        // Next free node index if the node is free.
        // Parent node index if the node is a directory.
        // Link count if node is a file.
        private ulong _union;
        internal ulong NextFreeNodeIndex
        {
            get
            {
                Debug.Assert(_fileAttributes == 0);
                return _union;
            }
            set
            {
                Debug.Assert(_fileAttributes == 0);
                _union = value;
            }
        }
        public ulong ParentNodeIndex
        {
            get
            {
                Debug.Assert((_fileAttributes & FileAttributes.Directory) != 0);
                return _union;
            }
            internal set
            {
                Debug.Assert((_fileAttributes & FileAttributes.Directory) != 0);
                _union = value;
            }
        }
        public ulong LinkCount
        {
            get
            {
                Debug.Assert((_fileAttributes & FileAttributes.Directory) == 0);
                return _union;
            }
            internal set
            {
                Debug.Assert((_fileAttributes & FileAttributes.Directory) == 0);
                _union = value;
            }
        }
        internal long OpenCount;

        private static FileAttributes FixNormal(FileAttributes fileAttributes)
        {
            fileAttributes &= ~FileAttributes.Normal;
            return fileAttributes == 0
                ? FileAttributes.Normal
                : fileAttributes;
        }
    }

    [DebuggerDisplay("Children.Length = {Children.Length}")]
    private struct Node2
    {
        public byte[]? SecurityDescriptor;
        public byte[]? ExtraData;
        public DataCombList Data;
        public ChildCombList Children;

        internal void Free()
        {
            try
            {
                Data.SetLength(0);
            }
            catch (OutOfMemoryException)
            {
                throw new UnreachableException();
            }

            this = default;
        }
    }

    [DebuggerDisplay("Name = {Name}")]
    public struct Child
    {
        public string Name;
        public ulong NodeIndex;
    }
}
