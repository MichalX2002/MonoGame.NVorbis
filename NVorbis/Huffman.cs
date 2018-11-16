/****************************************************************************
 * NVorbis                                                                  *
 * Copyright (C) 2014, Andrew Ward <afward@gmail.com>                       *
 *                                                                          *
 * See COPYING for license terms (Ms-PL).                                   *
 *                                                                          *
 ***************************************************************************/
using System;
using System.Collections.Generic;

namespace NVorbis
{
    static class Huffman
    {
        const int MAX_TABLE_BITS = 10;
        
        static internal List<HuffmanListNode> BuildPrefixedLinkedList(
            int[] values, int[] lengthList, int[] codeList,
            out int tableBits, out HuffmanListNode firstOverflowNode)
        {
            HuffmanListNode[] list = new HuffmanListNode[lengthList.Length];

            int maxLen = 0;
            for (int i = 0; i < lengthList.Length; i++)
            {
                int nodeLength = lengthList[i] <= 0 ? 99999 : lengthList[i];
                int mask = (1 << lengthList[i]) - 1;
                list[i] = new HuffmanListNode(values[i], nodeLength, codeList[i], mask);

                if (lengthList[i] > 0 && maxLen < lengthList[i])
                    maxLen = lengthList[i];
            }
        
            tableBits = maxLen > MAX_TABLE_BITS ? MAX_TABLE_BITS : maxLen;
            var prefixList = new List<HuffmanListNode>(1 << tableBits);

            Array.Sort(list, 0, lengthList.Length);
            firstOverflowNode = null;
            for (int i = 0; i < lengthList.Length && list[i].Length < 99999; i++)
            {
                if (firstOverflowNode == null)
                {
                    int itemBits = list[i].Length;
                    if (itemBits > tableBits)
                    {
                        firstOverflowNode = list[i];
                    }
                    else
                    {
                        int maxVal = 1 << (tableBits - itemBits);
                        HuffmanListNode item = list[i];
                        for (int j = 0; j < maxVal; j++)
                        {
                            int idx = (j << itemBits) | item.Bits;
                            while (prefixList.Count <= idx)
                                prefixList.Add(null);

                            prefixList[idx] = item;
                        }
                    }
                }
                else
                    list[i - 1].Next = list[i];
            }

            while (prefixList.Count < 1 << tableBits)
                prefixList.Add(null);

            return prefixList;
        }
    }

    internal class HuffmanListNode : IComparable<HuffmanListNode>
    {
        public int Value;
        public int Length;
        public int Bits;
        public int Mask;

        public HuffmanListNode Next;

        public HuffmanListNode(int value, int length, int bits, int mask)
        {
            Value = value;
            Length = length;
            Bits = bits;
            Mask = mask;
        }

        public int CompareTo(HuffmanListNode other)
        {
            int len = Length - other.Length;
            if (len == 0)
                return Bits - other.Bits;

            return len;
        }
    }
}
