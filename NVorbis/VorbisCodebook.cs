﻿/****************************************************************************
 * NVorbis                                                                  *
 * Copyright (C) 2014, Andrew Ward <afward@gmail.com>                       *
 *                                                                          *
 * See COPYING for license terms (Ms-PL).                                   *
 *                                                                          *
 ***************************************************************************/
using System;
using System.Linq;
using System.IO;
using System.Collections.Generic;
using System.Collections;

namespace NVorbis
{
    class VorbisCodebook
    {
        internal int BookNum;
        internal int Dimensions;
        internal int Entries;
        internal int MapType;

        int[] Lengths;
        float[] LookupTable;

        HuffmanListNode PrefixOverflowTree;
        System.Collections.Generic.List<HuffmanListNode> PrefixList;
        int PrefixBitLength;
        int MaxBits;

        internal float this[int entry, int dim] => LookupTable[entry * Dimensions + dim];
            
        internal static VorbisCodebook Init(VorbisStreamDecoder vorbis, DataPacket packet, int number)
        {
            var temp = new VorbisCodebook(number);
            temp.Init(packet);
            return temp;
        }

        private VorbisCodebook(int bookNum)
        {
            BookNum = bookNum;
        }

        internal void Init(DataPacket packet)
        {
            // first, check the sync pattern
            ulong chkVal = packet.ReadBits(24);
            if (chkVal != 0x564342UL)
                throw new InvalidDataException();

            // get the counts
            Dimensions = (int)packet.ReadBits(16);
            Entries = (int)packet.ReadBits(24);
            
            // init the storage
            Lengths = new int[Entries];

            InitTree(packet);
            InitLookupTable(packet);
        }

        void InitTree(DataPacket packet)
        {
            bool sparse;
            int total = 0;

            if (packet.ReadBit())
            {
                // ordered
                var len = (int)packet.ReadBits(5) + 1;
                for (var i = 0; i < Entries; )
                {
                    int cnt = (int)packet.ReadBits(Utils.ilog(Entries - i));

                    while (--cnt >= 0)
                        Lengths[i++] = len;

                    len++;
                }
                total = 0;
                sparse = false;
            }
            else
            {
                // unordered
                sparse = packet.ReadBit();
                for (var i = 0; i < Entries; i++)
                {
                    if (!sparse || packet.ReadBit())
                    {
                        Lengths[i] = (int)packet.ReadBits(5) + 1;
                        total++;
                    }
                    else
                    {
                        // mark the entry as unused
                        Lengths[i] = -1;
                    }
                }
            }
            // figure out the maximum bit size; if all are unused, don't do anything else
            if ((MaxBits = Lengths.Max()) > -1)
            {
                int sortedCount = 0;
                int[] codewordLengths = null;
                if (sparse && total >= Entries >> 2)
                {
                    codewordLengths = new int[Entries];
                    Array.Copy(Lengths, codewordLengths, Entries);

                    sparse = false;
                }

                // compute size of sorted tables
                sortedCount = sparse ? total : 0;
                int sortedEntries = sortedCount;

                int[] values = null;
                int[] codewords = null;
                if (!sparse)
                {
                    codewords = new int[Entries];
                }
                else if (sortedEntries != 0)
                {
                    codewordLengths = new int[sortedEntries];
                    codewords = new int[sortedEntries];
                    values = new int[sortedEntries];
                }

                if (!ComputeCodewords(sparse, sortedEntries, codewords, codewordLengths, Lengths, Entries, values))
                    throw new InvalidDataException();

                IReadOnlyList<int> valueList;
                if (values != null)
                    valueList = values;
                else
                    valueList = FastRange.Get(0, codewords.Length);

                PrefixList = Huffman.BuildPrefixedLinkedList(
                    valueList, codewordLengths ?? Lengths, codewords, out PrefixBitLength, out PrefixOverflowTree);
            }
        }

        class FastRange : IReadOnlyList<int>
        {
            [ThreadStatic]
            private static FastRange _cachedRange;

            private int _start;

            public int this[int index]
            {
                get
                {
                    if (index > Count)
                        throw new ArgumentOutOfRangeException();
                    return _start + index;
                }
            }

            public int Count { get; private set; }

            private FastRange()
            {
            }

            public static FastRange Get(int start, int count)
            {
                if(_cachedRange == null)
                    _cachedRange = new FastRange();
                _cachedRange._start = start;
                _cachedRange.Count = count;
                return _cachedRange;
            }

            public IEnumerator<int> GetEnumerator()
            {
                throw new NotSupportedException();
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }
        }

        bool ComputeCodewords(
            bool sparse, int sortedEntries, int[] codewords,
            int[] codewordLengths, int[] lengths, int entries, int[] values)
        {
            int k, m = 0;

            for (k = 0; k < entries; ++k)
                if (lengths[k] > 0)
                    break;

            if (k == entries)
                return true;

            AddEntry(sparse, codewords, codewordLengths, 0, k, m++, lengths[k], values);

            uint[] available = new uint[32];
            for (int i = 1; i <= lengths[k]; ++i)
                available[i] = 1U << (32 - i);

            for (int i = k + 1; i < entries; ++i)
            {
                uint res;
                int z = lengths[i], y;
                if (z <= 0) continue;

                while (z > 0 && available[z] == 0) --z;
                if (z == 0) return false;
                res = available[z];
                available[z] = 0;
                AddEntry(sparse, codewords, codewordLengths, Utils.BitReverse(res), i, m++, lengths[i], values);

                if (z != lengths[i])
                {
                    for (y = lengths[i]; y > z; --y)
                        available[y] = res + (1U << (32 - y));
                }
            }

            return true;
        }

        void AddEntry(bool sparse, int[] codewords, int[] codewordLengths, uint huffCode, int symbol, int count, int len, int[] values)
        {
            if (sparse)
            {
                codewords[count] = (int)huffCode;
                codewordLengths[count] = len;
                values[count] = symbol;
            }
            else
                codewords[symbol] = (int)huffCode;
        }

        void InitLookupTable(DataPacket packet)
        {
            MapType = (int)packet.ReadBits(4);
            if (MapType == 0)
                return;

            float minValue = Utils.ConvertFromVorbisFloat32(packet.ReadUInt32());
            float deltaValue = Utils.ConvertFromVorbisFloat32(packet.ReadUInt32());
            int valueBits = (int)packet.ReadBits(4) + 1;
            bool sequence_p = packet.ReadBit();

            int lookupValueCount = Entries * Dimensions;
            float[] lookupTable = new float[lookupValueCount];
            if (MapType == 1)
                lookupValueCount = Lookup1_values();

            uint[] multiplicands = new uint[lookupValueCount];
            for (var i = 0; i < lookupValueCount; i++)
                multiplicands[i] = (uint)packet.ReadBits(valueBits);

            // now that we have the initial data read in, calculate the entry tree
            if (MapType == 1)
            {
                for (int idx = 0; idx < Entries; idx++)
                {
                    double last = 0.0;
                    int idxDiv = 1;
                    for (var i = 0; i < Dimensions; i++)
                    {
                        int moff = (idx / idxDiv) % lookupValueCount;
                        double value = multiplicands[moff] * deltaValue + minValue + last;
                        lookupTable[idx * Dimensions + i] = (float)value;

                        if (sequence_p)
                            last = value;

                        idxDiv *= lookupValueCount;
                    }
                }
            }
            else
            {
                for (int idx = 0; idx < Entries; idx++)
                {
                    double last = 0.0;
                    int moff = idx * Dimensions;
                    for (var i = 0; i < Dimensions; i++)
                    {
                        double value = multiplicands[moff] * deltaValue + minValue + last;
                        lookupTable[idx * Dimensions + i] = (float)value;

                        if (sequence_p)
                            last = value;

                        moff++;
                    }
                }
            }

            LookupTable = lookupTable;
        }

        int Lookup1_values()
        {
            int r = (int)Math.Floor(Math.Exp(Math.Log(Entries) / Dimensions));
            
            if (Math.Floor(Math.Pow(r + 1, Dimensions)) <= Entries)
                r++;
            
            return r;
        }

        internal int DecodeScalar(DataPacket packet)
        {
            int bits = (int)packet.TryPeekBits(PrefixBitLength, out int bitCnt);
            if (bitCnt == 0)
                return -1;

            // try to get the value from the prefix list...
            HuffmanListNode node = PrefixList[bits];
            if (node != null)
            {
                packet.SkipBits(node.Length);
                return node.Value;
            }

            // nope, not possible... run the tree
            bits = (int)packet.TryPeekBits(MaxBits, out bitCnt);

            node = PrefixOverflowTree;
            do
            {
                if (node.Bits == (bits & node.Mask))
                {
                    packet.SkipBits(node.Length);
                    return node.Value;
                }
            } while ((node = node.Next) != null);
            return -1;
        }
    }
}
