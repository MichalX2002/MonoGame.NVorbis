/****************************************************************************
 * NVorbis                                                                  *
 * Copyright (C) 2014, Andrew Ward <afward@gmail.com>                       *
 *                                                                          *
 * See COPYING for license terms (Ms-PL).                                   *
 *                                                                          *
 ***************************************************************************/
using System;

namespace NVorbis.Ogg
{
    class OggPacket : DataPacket
    {
        long _offset;                       // 8
        int _length;                        // 4
        int _curOfs;                        // 4
        OggPacket _mergedPacket;               // IntPtr.Size
        OggContainerReader _containerReader;   // IntPtr.Size

        internal OggPacket Next { get; set; } // IntPtr.Size
        internal OggPacket Prev { get; set; } // IntPtr.Size

        internal bool IsContinued
        {
            get => GetFlag(PacketFlags.User1);
            set => SetFlag(PacketFlags.User1, value);
        }

        internal bool IsContinuation
        {
            get => GetFlag(PacketFlags.User2);
            set => SetFlag(PacketFlags.User2, value);
        }

        internal OggPacket(OggContainerReader containerReader, long streamOffset, int length) : base(length)
        {
            Set(containerReader, streamOffset, length);
        }

        internal void Set(OggContainerReader containerReader, long streamOffset, int length)
        {
            _mergedPacket = null;
            _containerReader = containerReader;

            Next = null;
            Prev = null;

            _offset = streamOffset;
            _length = length;
            _curOfs = 0;

            Set(length);
        }

        internal void MergeWith(DataPacket continuation)
        {
            if (!(continuation is OggPacket op))
                throw new ArgumentException("Incorrect packet type!");

            Length += continuation.Length;

            if (_mergedPacket == null)
                _mergedPacket = op;
            else
                _mergedPacket.MergeWith(continuation);

            // per the spec, a partial packet goes with the next page's granulepos. 
            // we'll go ahead and assign it to the next page as well
            PageGranulePosition = continuation.PageGranulePosition;
            PageSequenceNumber = continuation.PageSequenceNumber;
        }

        internal void Reset()
        {
            _curOfs = 0;
            ResetBitReader();

            if (_mergedPacket != null)
                _mergedPacket.Reset();
        }

        protected override int ReadNextByte()
        {
            if (_curOfs == _length)
            {
                if (_mergedPacket == null)
                    return -1;

                return _mergedPacket.ReadNextByte();
            }

            int b = _containerReader.PacketReadByte(_offset + _curOfs);
            if (b != -1)
                _curOfs++;

            return b;
        }

        public override void Done()
        {
            if (_mergedPacket != null)
                _mergedPacket.Done();
            else
                _containerReader.PacketDiscardThrough(_offset + _length);
        }
    }
}
