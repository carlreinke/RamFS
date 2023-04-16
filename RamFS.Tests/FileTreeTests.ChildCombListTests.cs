// Copyright 2023 Carl Reinke
//
// This file is part of a program that is licensed under the terms of the GNU
// General Public License Version 3 as published by the Free Software
// Foundation.

using ItsyBitsy.Xunit;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

public static partial class FileTreeTests
{
    public static class ChildCombListTests
    {
        private static readonly int _shift = (int)typeof(FileTree.ChildCombList).GetField(nameof(_shift), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).GetValue(null);

        private static readonly int _toothMaxLength = 1 << _shift;

        [Fact]
        public static void Capacity_Empty_ReturnsZero()
        {
            var instance = new FileTree.ChildCombList();

            Assert.Equal(0uL, instance.Capacity);
        }

        [Fact]
        public static void Length_Empty_ReturnsZero()
        {
            var instance = new FileTree.ChildCombList();

            Assert.Equal(0uL, instance.Length);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public static void Length_ChildAdded_Increases(int count)
        {
            var instance = new FileTree.ChildCombList();

            for (int i = 0; i < count; ++i)
                instance.Add(new FileTree.Child { Name = i.ToString("X8") }, ignoreCase: false);

            Assert.Equal((ulong)count, instance.Length);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public static void Length_ChildRemoved_Decreases(int count)
        {
            var instance = new FileTree.ChildCombList();

            for (int i = 0; i < count; ++i)
                instance.Add(new FileTree.Child { Name = i.ToString("X8") }, ignoreCase: false);
            for (int i = 0; i < count; ++i)
                instance.Remove(0, ignoreCase: false);

            Assert.Equal(0uL, instance.Length);
        }

        [Fact]
        public static void Find_Empty_ReturnsFalse()
        {
            var instance = new FileTree.ChildCombList();

            bool result = instance.Find(new StringSegment("a"), ignoreCase: false, out ulong index);

            Assert.False(result);
            Assert.Equal(default, index);
        }

        public static TheoryData<IEnumerable<string>, string, bool> Find_Tooth_Data = new()
        {
            { new[] { "b" }, "a", false },
            { new[] { "b" }, "b", true },
            { new[] { "b" }, "c", false },
            { new[] { "b", "d" }, "a", false },
            { new[] { "b", "d" }, "b", true },
            { new[] { "b", "d" }, "c", false },
            { new[] { "b", "d" }, "d", true },
            { new[] { "b", "d" }, "e", false },
            { new[] { "b", "d", "f" }, "a", false },
            { new[] { "b", "d", "f" }, "b", true },
            { new[] { "b", "d", "f" }, "c", false },
            { new[] { "b", "d", "f" }, "d", true },
            { new[] { "b", "d", "f" }, "e", false },
            { new[] { "b", "d", "f" }, "f", true },
            { new[] { "b", "d", "f" }, "g", false },
        };

        [Theory]
        [MemberData(nameof(Find_Tooth_Data))]
        public static void Find_Tooth_ReturnsExpectedResult(IEnumerable<string> names, string nameToFind, bool expectedResult)
        {
            var instance = new FileTree.ChildCombList();

            foreach (string name in names)
                instance.Add(new FileTree.Child() { Name = name }, ignoreCase: false);

            bool result = instance.Find(new StringSegment(nameToFind), ignoreCase: false, out ulong index);

            var expectedNames = names.ToList();
            for (int i = 0; i < expectedNames.Count; i += _toothMaxLength)
                expectedNames.Sort(i, Math.Min(expectedNames.Count - i, _toothMaxLength), StringComparer.Ordinal);
            ulong expectedIndex = expectedResult
                ? (ulong)expectedNames.FindIndex(x => StringComparer.Ordinal.Equals(x, nameToFind))
                : default;

            Assert.Equal(expectedResult, result);
            Assert.Equal(expectedIndex, index);
        }

        public static IEnumerable<(IEnumerable<string>, string, bool)> Find_Teeth_Data()
        {
            {
                var names = new List<string>();
                for (int i = 0; i < _toothMaxLength + 1; ++i)
                    names.Add((i * 2 + 1).ToString("X8"));

                yield return (names, names[0], true);
                yield return (names, names[1], true);
                yield return (names, names[_toothMaxLength - 2], true);
                yield return (names, names[_toothMaxLength - 1], true);
                yield return (names, names[_toothMaxLength], true);

                yield return (names, (0 * 2).ToString("X8"), false);
                yield return (names, (1 * 2).ToString("X8"), false);
                yield return (names, ((_toothMaxLength - 2) * 2).ToString("X8"), false);
                yield return (names, ((_toothMaxLength - 1) * 2).ToString("X8"), false);
                yield return (names, (_toothMaxLength * 2).ToString("X8"), false);
                yield return (names, ((_toothMaxLength + 1) * 2).ToString("X8"), false);
            }

            {
                var names = new List<string>();
                for (int i = 0; i < _toothMaxLength + 2; ++i)
                    names.Add((i * 2 + 1).ToString("X8"));

                yield return (names, names[_toothMaxLength], true);
                yield return (names, names[_toothMaxLength + 1], true);

                yield return (names, (_toothMaxLength * 2).ToString("X8"), false);
                yield return (names, ((_toothMaxLength + 1) * 2).ToString("X8"), false);
                yield return (names, ((_toothMaxLength + 2) * 2).ToString("X8"), false);
            }

            {
                var names = new List<string>();
                for (int i = 0; i < _toothMaxLength + 3; ++i)
                    names.Add((i * 2 + 1).ToString("X8"));

                yield return (names, names[_toothMaxLength], true);
                yield return (names, names[_toothMaxLength + 1], true);
                yield return (names, names[_toothMaxLength + 2], true);

                yield return (names, (_toothMaxLength * 2).ToString("X8"), false);
                yield return (names, ((_toothMaxLength + 1) * 2).ToString("X8"), false);
                yield return (names, ((_toothMaxLength + 2) * 2).ToString("X8"), false);
                yield return (names, ((_toothMaxLength + 3) * 2).ToString("X8"), false);
            }
        }

        [Theory]
        [TupleMemberData(nameof(Find_Teeth_Data))]
        public static void Find_Teeth_ReturnsExpectedResult(IEnumerable<string> names, string nameToFind, bool expectedResult)
        {
            var instance = new FileTree.ChildCombList();

            foreach (string name in names)
                instance.Add(new FileTree.Child() { Name = name }, ignoreCase: false);

            bool result = instance.Find(new StringSegment(nameToFind), ignoreCase: false, out ulong index);

            var expectedNames = names.ToList();
            for (int i = 0; i < expectedNames.Count; i += _toothMaxLength)
                expectedNames.Sort(i, Math.Min(expectedNames.Count - i, _toothMaxLength), StringComparer.Ordinal);
            int temp = expectedNames.FindIndex(x => StringComparer.Ordinal.Equals(x, nameToFind));
            ulong expectedIndex = expectedResult
                ? (ulong)expectedNames.FindIndex(x => StringComparer.Ordinal.Equals(x, nameToFind))
                : default;

            Assert.Equal(expectedResult, result);
            Assert.Equal(expectedIndex, index);
        }

        [Fact]
        public static void Find_Removed_ReturnsFalse()
        {
            var instance = new FileTree.ChildCombList();

            for (int i = 0; i < _toothMaxLength + 1; ++i)
                instance.Add(new FileTree.Child { Name = i.ToString("X8") }, ignoreCase: false);

            string name = instance[(ulong)_toothMaxLength].Name;

            instance.Remove((ulong)_toothMaxLength, ignoreCase: false);

            Assert.True(instance.Capacity > instance.Length);

            bool result = instance.Find(new StringSegment(name), ignoreCase: false, out ulong index);

            Assert.False(result);
            Assert.Equal(default, index);
        }

        [Fact]
        public static void Find_RandomNames_ReturnsExpectedResults()
        {
            var random = new Random(0);

            var names = new List<string>();
            for (int i = 0; i < _toothMaxLength * 4; ++i)
                names.Add(random.Next().ToString("X8"));

            var instance = new FileTree.ChildCombList();
            foreach (string name in names)
                instance.Add(new FileTree.Child() { Name = name }, ignoreCase: false);

            for (int i = 0; i < names.Count; ++i)
            {
                bool result = instance.Find(new StringSegment(names[i]), ignoreCase: false, out ulong index);

                string childName = instance[index].Name;

                Assert.True(result);
                Assert.Equal(names[i], childName);
            }
        }

        // TODO: Reorder

        public static TheoryData<IEnumerable<string>, string> Remove_Tooth_Data = new()
        {
            { new[] { "a" }, "a" },
            { new[] { "a", "b" }, "a" },
            { new[] { "a", "b" }, "b" },
            { new[] { "a", "b", "c" }, "a" },
            { new[] { "a", "b", "c" }, "b" },
            { new[] { "a", "b", "c" }, "c" },
        };

        [Theory]
        [MemberData(nameof(Remove_Tooth_Data))]
        public static void Remove_Tooth_EnumerationProducesExpectedItems(IEnumerable<string> names, string nameToRemove)
        {
            var instance = new FileTree.ChildCombList();

            foreach (string name in names)
                instance.Add(new FileTree.Child() { Name = name }, ignoreCase: false);

            if (instance.Find(new StringSegment(nameToRemove), ignoreCase: false, out ulong index))
                instance.Remove(index, ignoreCase: false);

            var expectedNames = names.ToList();
            expectedNames.Remove(nameToRemove);
            expectedNames.Sort(StringComparer.Ordinal);

            AssertNameEnumeration(expectedNames, instance);
        }

        public static IEnumerable<(IEnumerable<string>, string)> Remove_Teeth_Data()
        {
            // Replacement ordered at end of tooth.
            {
                var names = new List<string>();
                for (int i = 0; i < _toothMaxLength + 1; ++i)
                    names.Add(i.ToString("X8"));

                yield return (names, names[0]);
                yield return (names, names[1]);
                yield return (names, names[_toothMaxLength - 2]);
                yield return (names, names[_toothMaxLength - 1]);
            }

            // Replacement ordered at beginning of tooth.
            {
                var names = new List<string>();
                for (int i = 0; i < _toothMaxLength; ++i)
                    names.Add((i + 1).ToString("X8"));
                names.Add(0.ToString("X8"));

                yield return (names, names[0]);
                yield return (names, names[1]);
                yield return (names, names[_toothMaxLength - 2]);
                yield return (names, names[_toothMaxLength - 1]);
            }

            // Replacement ordered in middle of tooth.
            {
                var names = new List<string>();
                for (int i = 0; i < _toothMaxLength; ++i)
                    names.Add((i * 2).ToString("X8"));
                names.Add((_toothMaxLength / 2 + 1).ToString("X8"));

                yield return (names, names[0]);
                yield return (names, names[1]);
                yield return (names, names[_toothMaxLength - 2]);
                yield return (names, names[_toothMaxLength - 1]);
            }

            // Remove from first tooth; last tooth emptied.
            // (Would be the same as one of the tests above.)

            // Remove from first tooth; last tooth not emptied.
            {
                var names = new List<string>();
                for (int i = 0; i < _toothMaxLength + 2; ++i)
                    names.Add(i.ToString("X8"));

                yield return (names, names[_toothMaxLength - 1]);
            }

            // Remove from last tooth; last tooth emptied.
            {
                var names = new List<string>();
                for (int i = 0; i < _toothMaxLength + 1; ++i)
                    names.Add(i.ToString("X8"));

                yield return (names, names[_toothMaxLength]);
            }

            // Remove from last tooth; last tooth not emptied.
            {
                var names = new List<string>();
                for (int i = 0; i < _toothMaxLength + 3; ++i)
                    names.Add(i.ToString("X8"));

                yield return (names, names[_toothMaxLength]);
                yield return (names, names[_toothMaxLength + 1]);
                yield return (names, names[_toothMaxLength + 2]);
            }
        }

        [Theory]
        [TupleMemberData(nameof(Remove_Teeth_Data))]
        public static void Remove_Teeth_EnumerationProducesExpectedItems(IEnumerable<string> names, string nameToRemove)
        {
            var instance = new FileTree.ChildCombList();

            foreach (string name in names)
                instance.Add(new FileTree.Child() { Name = name }, ignoreCase: false);

            if (instance.Find(new StringSegment(nameToRemove), ignoreCase: false, out ulong index))
                instance.Remove(index, ignoreCase: false);

            var expectedNames = names.ToList();
            expectedNames.Remove(nameToRemove);
            expectedNames.Sort(StringComparer.Ordinal);

            AssertNameEnumeration(expectedNames, instance);
        }

        [Fact]
        public static void GetEnumeration_RandomNames_ProducesExpectedItems()
        {
            var random = new Random(0);

            var names = new List<string>();
            for (int i = 0; i < _toothMaxLength * 4; ++i)
                names.Add(random.Next().ToString("X8"));

            var instance = new FileTree.ChildCombList();
            foreach (string name in names)
                instance.Add(new FileTree.Child() { Name = name }, ignoreCase: false);

            names.Sort(StringComparer.Ordinal);

            AssertNameEnumeration(names, instance);
        }

        private static void AssertNameEnumeration(IEnumerable<string> expectedNames, FileTree.ChildCombList instance)
        {
            var actualNames = new List<string>();
            foreach (var child in instance)
                actualNames.Add(child.Name);
            actualNames.Sort(StringComparer.Ordinal);

            Assert.Equal(expectedNames, actualNames);

            actualNames.Clear();
            var enumerator = instance.GetEnumerator(null, ignoreCase: false);
            while (enumerator.MoveNext())
                actualNames.Add(enumerator.Current.Name);

            Assert.Equal(expectedNames, actualNames);
        }

        [Fact]
        public static void GetEnumeration_Marker_ProducesExpectedItems()
        {
            var random = new Random(0);

            var names = new List<string>();
            for (int i = 0; i < _toothMaxLength * 4; ++i)
                names.Add(random.Next().ToString("X8"));

            var instance = new FileTree.ChildCombList();
            foreach (string name in names)
                instance.Add(new FileTree.Child() { Name = name }, ignoreCase: false);

            names.Sort(StringComparer.Ordinal);

            for (int i = 0; i < names.Count; ++i)
            {
                string marker = names[i];

                var actualNames = new List<string>();
                var enumerator = instance.GetEnumerator(marker, ignoreCase: false);
                while (enumerator.MoveNext())
                    actualNames.Add(enumerator.Current.Name);

                Assert.Equal(names.Skip(i + 1), actualNames);
            }
        }
    }
}
