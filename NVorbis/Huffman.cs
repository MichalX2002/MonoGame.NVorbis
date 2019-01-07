/****************************************************************************
 * NVorbis                                                                  *
 * Copyright (C) 2014, Andrew Ward <afward@gmail.com>                       *
 *                                                                          *
 * See COPYING for license terms (Ms-PL).                                   *
 *                                                                          *
 ***************************************************************************/
using System;
using System.Collections.Generic;
using HuffmanListNode = NVorbis.HuffmanPool.Node;

namespace NVorbis
{
    static class Huffman
    {
        const int MAX_TABLE_BITS = 10;
        
        static internal HuffmanListNode[] BuildPrefixedLinkedList(
            IReadOnlyList<int> values, int[] lengthList, int[] codeList,
            out int tableBits, out HuffmanListNode firstOverflowNode)
        {
            var list = new HuffmanListNode[lengthList.Length];

            int maxLen = 0;
            for (int i = 0; i < lengthList.Length; i++)
            {
                int nodeLength = lengthList[i] <= 0 ? 99999 : lengthList[i];
                int mask = (1 << lengthList[i]) - 1;
                list[i] = HuffmanPool.Rent(values[i], nodeLength, codeList[i], mask);

                if (lengthList[i] > 0 && maxLen < lengthList[i])
                    maxLen = lengthList[i];
            }
        
            tableBits = maxLen > MAX_TABLE_BITS ? MAX_TABLE_BITS : maxLen;
            var prefixList = new HuffmanListNode[1 << tableBits];

            Array.Sort(list, 0, lengthList.Length);
            firstOverflowNode = null;

            for (int i = 0; i < lengthList.Length && list[i].Length < 99999; i++)
            {
                if (firstOverflowNode == null)
                {
                    HuffmanListNode node = list[i];
                    node.IsLinked = true;
                    
                    if (node.Length > tableBits)
                    {
                        firstOverflowNode = node;
                    }
                    else
                    {
                        int maxVal = 1 << (tableBits - node.Length);
                        for (int j = 0; j < maxVal; j++)
                        {
                            int idx = (j << node.Length) | node.Bits;
                            prefixList[idx] = node;
                        }
                    }
                }
                else
                {
                    list[i - 1].Next = list[i];
                    list[i].IsLinked = true;
                }
            }

            foreach (var node in list)
            {
                if (node != null && !node.IsLinked)
                    HuffmanPool.Return(node);
            }

            return prefixList;
        }
    }

    internal static class HuffmanPool
    {
        private static object _mutex = new object();
        private static Stack<Node> _pool = new Stack<Node>();

        public const int MAX_NODES = 1024 * 128;

        public static Node Rent(int value, int length, int bits, int mask)
        {
            lock (_mutex)
            {
                if (_pool.Count > 0)
                {
                    var item = _pool.Pop();
                    item.Set(value, length, bits, mask);
                    return item;
                }
            }
            return new HuffmanListNode(value, length, bits, mask);
        }

        public static void Return(Node node)
        {
            if (node == null)
                return;

            if (node.IsInUse)
            {
                node.IsInUse = false;

                Return(node.Next);
                node.Next = null;

                lock (_mutex)
                {
                    if (_pool.Count < MAX_NODES)
                        _pool.Push(node);
                }
            }
        }

        internal class Node : IComparable<Node>
        {
            internal bool IsInUse;

            public int Value;
            public int Length;
            public int Bits;
            public int Mask;

            public bool IsLinked;
            public Node Next;

            internal Node(int value, int length, int bits, int mask)
            {
                Set(value, length, bits, mask);
            }

            public void Set(int value, int length, int bits, int mask)
            {
                Value = value;
                Length = length;
                Bits = bits;
                Mask = mask;
                IsInUse = true;
                IsLinked = false;
            }

            public int CompareTo(Node other)
            {
                int len = Length - other.Length;
                if (len == 0)
                    return Bits - other.Bits;
                return len;
            }
        }
    }
}
