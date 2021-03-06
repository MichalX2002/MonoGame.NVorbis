﻿/****************************************************************************
 * NVorbis                                                                  *
 * Copyright (C) 2014, Andrew Ward <afward@gmail.com>                       *
 *                                                                          *
 * See COPYING for license terms (Ms-PL).                                   *
 *                                                                          *
 ***************************************************************************/
using System;
using System.Collections.Generic;
using HuffmanNode = NVorbis.HuffmanPool.Node;

namespace NVorbis
{
    static class Huffman
    {
        const int MAX_TABLE_BITS = 10;
        
        static internal HuffmanNode[] BuildPrefixedLinkedList(
            Span<int> values, Span<int> lengthList, Span<int> codeList,
            out int tableBits, out HuffmanNode firstOverflowNode)
        {
            var list = new HuffmanNode[lengthList.Length];

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
            var prefixList = new HuffmanNode[1 << tableBits];

            Array.Sort(list, 0, lengthList.Length);
            firstOverflowNode = null;

            for (int i = 0; i < lengthList.Length && list[i].Length < 99999; i++)
            {
                if (firstOverflowNode == null)
                {
                    HuffmanNode node = list[i];
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
        private static Stack<HuffmanNode> _pool = new Stack<HuffmanNode>();

        public const int MAX_NODES = 1024 * 32;

        public static HuffmanNode Rent(int value, int length, int bits, int mask)
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
            return new HuffmanNode(value, length, bits, mask);
        }

        public static void Return(HuffmanNode node)
        {
            if (node == null)
                return;

            if (node.IsInUse)
            {
                node.IsInUse = false;

                lock (_mutex)
                {
                    Return(node.Next);
                    node.Next = null;

                    if (_pool.Count < MAX_NODES)
                        _pool.Push(node);
                }
            }
        }

        internal class Node : IComparable<HuffmanNode>
        {
            internal bool IsInUse;

            public int Value;
            public int Length;
            public int Bits;
            public int Mask;

            public bool IsLinked;
            public HuffmanNode Next;

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

            public int CompareTo(HuffmanNode other)
            {
                int len = Length - other.Length;
                if (len == 0)
                    return Bits - other.Bits;
                return len;
            }
        }
    }
}
