/****************************************************************************
 * NVorbis                                                                  *
 * Copyright (C) 2014, Andrew Ward <afward@gmail.com>                       *
 *                                                                          *
 * See COPYING for license terms (Ms-PL).                                   *
 *                                                                          *
 ***************************************************************************/
using System;
using System.IO;
using System.Collections.Generic;
using System.Collections;
using HuffmanListNode = NVorbis.HuffmanPool.Node;

namespace NVorbis
{
    class VorbisCodebook : IDisposable
    {
        private bool _disposed;

        internal int BookNum;
        internal int Dimensions;
        internal int Entries;
        internal int MapType;

        int[] Lengths;
        float[] LookupTable;

        HuffmanListNode PrefixOverflowTree;
        public HuffmanListNode[] PrefixList;
        int PrefixBitLength;
        int MaxBits;

        internal float this[int entry, int dim] => LookupTable[entry * Dimensions + dim];

        private VorbisCodebook(int bookNum)
        {
            BookNum = bookNum;
        }

        internal static VorbisCodebook Create(VorbisStreamDecoder vorbis, DataPacket packet, int number)
        {
            var tmp = new VorbisCodebook(number);
            tmp.Init(packet);
            return tmp;
        }

        internal void Init(DataPacket packet)
        {
            // first, check the sync pattern
            ulong chkVal = packet.ReadUBits(24);
            if (chkVal != 0x564342UL)
                throw new InvalidDataException();

            // get the counts
            Dimensions = (int)packet.ReadUBits(16);
            Entries = (int)packet.ReadUBits(24);
            
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
                var len = (int)packet.ReadUBits(5) + 1;
                for (var i = 0; i < Entries; )
                {
                    int cnt = (int)packet.ReadUBits(Utils.ILog(Entries - i));

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
                        Lengths[i] = (int)packet.ReadUBits(5) + 1;
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
                Span<int> codewordLengths = stackalloc int[0];
                if (sparse && total >= Entries >> 2)
                {
                    codewordLengths = stackalloc int[Entries];
                    for (int i = 0; i < Entries; i++)
                        codewordLengths[i] = Lengths[i];

                    sparse = false;
                }

                // compute size of sorted tables
                int sortedEntries = sparse ? total : 0;

                Span<int> values = stackalloc int[0];
                Span<int> codewords = stackalloc int[0];
                if (!sparse)
                {
                    codewords = stackalloc int[Entries];
                }
                else if (sortedEntries != 0)
                {
                    codewordLengths = stackalloc int[sortedEntries];
                    codewords = stackalloc int[sortedEntries];
                    values = stackalloc int[sortedEntries];
                }

                if (!ComputeCodewords(sparse, sortedEntries, codewords, codewordLengths, Lengths, Entries, values))
                    throw new InvalidDataException();

                Span<int> valueList = stackalloc int[codewords.Length];
                if (values.Length != 0)
                    valueList = values;
                else
                {
                    for (int i = 0; i < codewords.Length; i++)
                        valueList[i] = i;
                }

                Span<int> lengthList = codewordLengths.Length > 0 ? codewordLengths : Lengths.AsSpan();
                PrefixList = Huffman.BuildPrefixedLinkedList(
                    valueList, lengthList, codewords, out PrefixBitLength, out PrefixOverflowTree);
            }
        }

        bool ComputeCodewords(
            bool sparse, int sortedEntries, Span<int> codewords,
            Span<int> codewordLengths, Span<int> lengths, int entries, Span<int> values)
        {
            int k;
            for (k = 0; k < entries; ++k)
                if (lengths[k] > 0)
                    break;

            if (k == entries)
                return true;

            int m = 0;
            AddEntry(sparse, codewords, codewordLengths, 0, k, m++, lengths[k], values);

            Span<uint> available = stackalloc uint[32];
            for (int i = 1; i <= lengths[k]; ++i)
                available[i] = 1U << (32 - i);

            for (int i = k + 1; i < entries; ++i)
            {
                int z = lengths[i], y;
                if (z <= 0)
                    continue;

                while (z > 0 && available[z] == 0)
                    --z;

                if (z == 0)
                    return false;

                uint res = available[z];
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

        void AddEntry(
            bool sparse, Span<int> codewords, Span<int> codewordLengths,
            uint huffCode, int symbol, int count, int len, Span<int> values)
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
            MapType = (int)packet.ReadUBits(4);
            if (MapType == 0)
                return;

            float minValue = Utils.ConvertFromVorbisFloat32(packet.ReadUInt32());
            float deltaValue = Utils.ConvertFromVorbisFloat32(packet.ReadUInt32());
            int valueBits = (int)packet.ReadUBits(4) + 1;
            bool sequence_p = packet.ReadBit();

            int lookupValueCount = Entries * Dimensions;
            LookupTable = new float[lookupValueCount];
            if (MapType == 1)
                lookupValueCount = Lookup1_values();

            Span<uint> multiplicands = stackalloc uint[lookupValueCount];
            for (var i = 0; i < lookupValueCount; i++)
                multiplicands[i] = (uint)packet.ReadUBits(valueBits);

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
                        LookupTable[idx * Dimensions + i] = (float)value;

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
                        LookupTable[idx * Dimensions + i] = (float)value;

                        if (sequence_p)
                            last = value;

                        moff++;
                    }
                }
            }
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
            int bits = (int)packet.TryPeekU64Bits(PrefixBitLength, out int bitCnt);
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
            bits = (int)packet.TryPeekU64Bits(MaxBits, out _);

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

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    HuffmanPool.Return(PrefixOverflowTree);
                    PrefixOverflowTree = null;

                    for (int i = 0; i < PrefixList.Length; i++)
                        HuffmanPool.Return(PrefixList[i]);
                    
                    Array.Clear(PrefixList, 0, PrefixList.Length);
                    PrefixList = null;

                    Lengths = null;
                    LookupTable = null;
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }
    }
}
