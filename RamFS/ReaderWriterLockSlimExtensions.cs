// Copyright 2023 Carl Reinke
//
// This file is part of a program that is licensed under the terms of the GNU
// General Public License Version 3 as published by the Free Software
// Foundation.

using System;
using System.Threading;

internal static class ReaderWriterLockSlimExtensions
{
    /// <exception cref="ObjectDisposedException"/>
    public static ReadScope EnterReadScope(this ReaderWriterLockSlim @lock)
    {
        return new ReadScope(@lock);
    }

    /// <exception cref="ObjectDisposedException"/>
    public static UpgradeableReadScope EnterUpgradeableReadScope(this ReaderWriterLockSlim @lock)
    {
        return new UpgradeableReadScope(@lock);
    }

    /// <exception cref="ObjectDisposedException"/>
    public static WriteScope EnterWriteScope(this ReaderWriterLockSlim @lock)
    {
        return new WriteScope(@lock);
    }

    public readonly struct ReadScope : IDisposable
    {
        private readonly ReaderWriterLockSlim _lock;

        /// <exception cref="ObjectDisposedException"/>
        /// <exception cref="LockRecursionException"/>
        internal ReadScope(ReaderWriterLockSlim @lock)
        {
            _lock = @lock;
            _lock.EnterReadLock();
        }

        /// <exception cref="SynchronizationLockException"/>
#pragma warning disable Ex0200 // Member is documented as throwing exception not documented on member in base or interface type
        public void Dispose()
#pragma warning restore Ex0200 // Member is documented as throwing exception not documented on member in base or interface type
        {
            _lock.ExitReadLock();
        }
    }

    public readonly struct UpgradeableReadScope : IDisposable
    {
        private readonly ReaderWriterLockSlim _lock;

        /// <exception cref="ObjectDisposedException"/>
        /// <exception cref="LockRecursionException"/>
        internal UpgradeableReadScope(ReaderWriterLockSlim @lock)
        {
            _lock = @lock;
            _lock.EnterUpgradeableReadLock();
        }

        /// <exception cref="SynchronizationLockException"/>
#pragma warning disable Ex0200 // Member is documented as throwing exception not documented on member in base or interface type
        public void Dispose()
#pragma warning restore Ex0200 // Member is documented as throwing exception not documented on member in base or interface type
        {
            _lock.ExitUpgradeableReadLock();
        }
    }

    public readonly struct WriteScope : IDisposable
    {
        private readonly ReaderWriterLockSlim _lock;

        /// <exception cref="ObjectDisposedException"/>
        /// <exception cref="LockRecursionException"/>
        internal WriteScope(ReaderWriterLockSlim @lock)
        {
            _lock = @lock;
            _lock.EnterWriteLock();
        }

        /// <exception cref="SynchronizationLockException"/>
#pragma warning disable Ex0200 // Member is documented as throwing exception not documented on member in base or interface type
        public void Dispose()
#pragma warning restore Ex0200 // Member is documented as throwing exception not documented on member in base or interface type
        {
            _lock.ExitWriteLock();
        }
    }
}
