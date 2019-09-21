/****************************************************************************
 * NVorbis                                                                  *
 * Copyright (C) 2014, Andrew Ward <afward@gmail.com>                       *
 *                                                                          *
 * See COPYING for license terms (Ms-PL).                                   *
 *                                                                          *
 ***************************************************************************/
using System;
using System.Collections.Generic;
using System.IO;

namespace NVorbis.Ogg
{
    /// <summary>
    /// Provides an <see cref="IContainerReader"/> implementation for basic Ogg files.
    /// </summary>
    public class OggContainerReader : IContainerReader
    {
        OggCrc _crc = new OggCrc();
        BufferedReadStream _stream;
        Dictionary<int, OggPacketReader> _packetReaders;
        List<int> _disposedStreamSerials;
        long _nextPageOffset;
        int _pageCount;

        byte[] _readBuffer; 

        long _containerBits, _wasteBits;

        /// <summary>
        /// Gets the list of stream serials found in the container so far.
        /// </summary>
        public int[] StreamSerials => System.Linq.Enumerable.ToArray(_packetReaders.Keys);

        /// <summary>
        /// Event raised when a new logical stream is found in the container.
        /// </summary>
        public event NewStreamDelegate NewStream;

        /// <summary>
        /// Creates a new instance with the specified file.
        /// </summary>
        /// <param name="path">The full path to the file.</param>
        public OggContainerReader(string path)
            : this(File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read), true)
        {
        }

        /// <summary>
        /// Creates a new instance with the specified stream.  Optionally sets to close the stream when disposed.
        /// </summary>
        /// <param name="stream">The stream to read.</param>
        /// <param name="leaveOpen"><c>false</c> to close the stream when <see cref="Dispose"/> is called, otherwise <c>true</c>.</param>
        public OggContainerReader(Stream stream, bool leaveOpen)
        {
            _packetReaders = new Dictionary<int, OggPacketReader>();
            _disposedStreamSerials = new List<int>();

            _stream = new BufferedReadStream(stream, leaveOpen);
            _readBuffer = BufferPool.Rent();
        }

        /// <summary>
        /// Initializes the container and finds the first stream.
        /// </summary>
        /// <returns><c>True</c> if a valid logical stream is found, otherwise <c>False</c>.</returns>
        public bool Init()
        {
            _stream.TakeLock();
            try
            {
                return GatherNextPage() != -1;
            }
            finally
            {
                _stream.ReleaseLock();
            }
        }

        /// <summary>
        /// Disposes this instance.
        /// </summary>
        public void Dispose()
        {
            // don't use _packetReaders directly since that'll change the enumeration...
            foreach (var streamSerial in StreamSerials)
            {
                _packetReaders[streamSerial].Dispose();
            }

            _nextPageOffset = 0L;
            _containerBits = 0L;
            _wasteBits = 0L;

            _stream.Dispose();

            BufferPool.Return(_readBuffer);
        }

        /// <summary>
        /// Gets the <see cref="IPacketProvider"/> instance for the specified stream serial.
        /// </summary>
        /// <param name="streamSerial">The stream serial to look for.</param>
        /// <returns>An <see cref="IPacketProvider"/> instance.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The specified stream serial was not found.</exception>
        public IPacketProvider GetStream(int streamSerial)
        {
            if (!_packetReaders.TryGetValue(streamSerial, out OggPacketReader provider))
                throw new ArgumentOutOfRangeException();
            return provider;
        }

        /// <summary>
        /// Finds the next new stream in the container.
        /// </summary>
        /// <returns><c>True</c> if a new stream was found, otherwise <c>False</c>.</returns>
        /// <exception cref="InvalidOperationException"><see cref="CanSeek"/> is <c>False</c>.</exception>
        public bool FindNextStream()
        {
            if (!CanSeek)
                throw new InvalidOperationException();

            // goes through all the pages until the serial count increases
            var cnt = _packetReaders.Count;
            while (_packetReaders.Count == cnt)
            {
                _stream.TakeLock();
                try
                {
                    // acquire & release the lock every pass so we don't block any longer than necessary
                    if (GatherNextPage() == -1)
                        break;
                }
                finally
                {
                    _stream.ReleaseLock();
                }
            }
            return cnt > _packetReaders.Count;
        }

        /// <summary>
        /// Gets the number of pages that have been read in the container.
        /// </summary>
        public int PagesRead => _pageCount;

        /// <summary>
        /// Retrieves the total number of pages in the container.
        /// </summary>
        /// <returns>The total number of pages.</returns>
        /// <exception cref="InvalidOperationException"><see cref="CanSeek"/> is <c>False</c>.</exception>
        public int GetTotalPageCount()
        {
            if (!CanSeek)
                throw new InvalidOperationException();

            // just read pages until we can't any more...
            while (true)
            {
                _stream.TakeLock();
                try
                {
                    // acquire & release the lock every pass so we don't block any longer than necessary
                    if (GatherNextPage() == -1)
                        break;
                }
                finally
                {
                    _stream.ReleaseLock();
                }
            }

            return _pageCount;
        }

        /// <summary>
        /// Gets whether the container supports seeking.
        /// </summary>
        public bool CanSeek => _stream.CanSeek;

        /// <summary>
        /// Gets the number of bits in the container that are not associated with a logical stream.
        /// </summary>
        public long WasteBits => _wasteBits;


        // private implmentation bits
        unsafe struct PageHeader
        {
            public int StreamSerial;
            public OggPageFlags Flags;
            public long GranulePosition;
            public int SequenceNumber;
            public long DataOffset;
            public bool LastPacketContinues;
            public bool IsResync;

            public fixed int PacketSizes[256];
            public int SegCount;
        }

        unsafe bool ReadPageHeader(long position, out PageHeader header)
        {
            header = default;

            // set the stream's position
            _stream.Seek(position, SeekOrigin.Begin);

            // header
            // NB: if the stream didn't have an EOS flag, this is the most likely spot for the EOF to be found...
            if (_stream.Read(_readBuffer, 0, 27) != 27)
                return false;

            // capture signature
            if (_readBuffer[0] != 0x4f || _readBuffer[1] != 0x67 || _readBuffer[2] != 0x67 || _readBuffer[3] != 0x53)
                return false;

            // check the stream version
            if (_readBuffer[4] != 0)
                return false;

            // start populating the header
            header = new PageHeader();

            // bit flags
            header.Flags = (OggPageFlags)_readBuffer[5];

            // granulePosition
            header.GranulePosition = BitConverter.ToInt64(_readBuffer, 6);

            // stream serial
            header.StreamSerial = BitConverter.ToInt32(_readBuffer, 14);

            // sequence number
            header.SequenceNumber = BitConverter.ToInt32(_readBuffer, 18);

            // save off the CRC
            var crc = BitConverter.ToUInt32(_readBuffer, 22);

            // start calculating the CRC value for this page
            _crc.Reset();
            for (int i = 0; i < 22; i++)
                _crc.Update(_readBuffer[i]);

            _crc.Update(0);
            _crc.Update(0);
            _crc.Update(0);
            _crc.Update(0);
            _crc.Update(_readBuffer[26]);

            // figure out the length of the page
            header.SegCount = _readBuffer[26];
            if (_stream.Read(_readBuffer, 0, header.SegCount) != header.SegCount)
                return false;

            int size = 0;
            int idx = 0;
            for (int i = 0; i < header.SegCount; i++)
            {
                var tmp = _readBuffer[i];
                _crc.Update(tmp);
                
                header.PacketSizes[idx] += tmp;

                if (tmp < 255)
                {
                    idx++;
                    header.LastPacketContinues = false;
                }
                else
                    header.LastPacketContinues = true;

                size += tmp;
            }
            header.DataOffset = position + 27 + header.SegCount;

            // now we have to go through every byte in the page
            if (_stream.Read(_readBuffer, 0, size) != size)
                return false;

            for (int i = 0; i < size; i++)
                _crc.Update(_readBuffer[i]);

            if (_crc.Test(crc))
            {
                _containerBits += 8 * (27 + header.SegCount);
                ++_pageCount;
                return true;
            }
            return false;
        }

        unsafe bool FindNextPageHeader(out PageHeader header)
        {
            long startPos = _nextPageOffset;
            bool isResync = false;

            while (!ReadPageHeader(startPos, out header))
            {
                isResync = true;
                _wasteBits += 8;
                _stream.Position = ++startPos;

                int count = 0;
                do
                {
                    int b = _stream.ReadByte();
                    if (b == 0x4f)
                    {
                        if (_stream.ReadByte() == 0x67 &&
                            _stream.ReadByte() == 0x67 &&
                            _stream.ReadByte() == 0x53)
                        {
                            // found it!
                            startPos += count;
                            break;
                        }
                        else
                            _stream.Seek(-3, SeekOrigin.Current);
                    }
                    else if (b == -1)
                        return false;

                    _wasteBits += 8;
                } while (++count < 65536); // we will only search through 64KB of data to find the next sync marker. if it can't be found, we have a badly corrupted stream.
                if (count == 65536)
                    return false;
            }
            header.IsResync = isResync;

            _nextPageOffset = header.DataOffset;
            for (int i = 0; i < header.SegCount; i++)
                _nextPageOffset += header.PacketSizes[i];

            return true;
        }

        unsafe bool AddPage(PageHeader hdr)
        {
            // get our packet reader (create one if we have to)
            if (!_packetReaders.TryGetValue(hdr.StreamSerial, out var packetReader))
                packetReader = new OggPacketReader(this, hdr.StreamSerial);

            // save off the container bits
            packetReader.ContainerBits += _containerBits;
            _containerBits = 0;

            // get our flags prepped
            bool isContinued = hdr.SegCount == 1 && hdr.LastPacketContinues;
            bool isContinuation = (hdr.Flags & OggPageFlags.ContinuesPacket) == OggPageFlags.ContinuesPacket;
            bool isEOS = false;
            bool isResync = hdr.IsResync;

            // add all the packets, making sure to update flags as needed
            long dataOffset = hdr.DataOffset;
            int count = hdr.SegCount;
            for (int i = 0; i < hdr.SegCount; i++)
            {
                int size = hdr.PacketSizes[i];
                var packet = OggPacketPool.Rent(this, dataOffset, size);
                packet.PageGranulePosition = hdr.GranulePosition;
                packet.IsEndOfStream = isEOS;
                packet.PageSequenceNumber = hdr.SequenceNumber;
                packet.IsContinued = isContinued;
                packet.IsContinuation = isContinuation;
                packet.IsResync = isResync;
                packetReader.AddPacket(packet);

                // update the offset into the stream for each packet
                dataOffset += size;

                // only the first packet in a page can be a continuation or resync
                isContinuation = false;
                isResync = false;

                // only the last packet in a page can be continued or flagged end of stream
                if (--count == 1)
                {
                    isContinued = hdr.LastPacketContinues;
                    isEOS = (hdr.Flags & OggPageFlags.EndOfStream) == OggPageFlags.EndOfStream;
                }
            }

            // if the packet reader list doesn't include the serial in question, add it to the list and indicate a new stream to the caller
            if (!_packetReaders.ContainsKey(hdr.StreamSerial))
            {
                _packetReaders.Add(hdr.StreamSerial, packetReader);
                return true;
            }
            else
            {
                // otherwise, indicate an existing stream to the caller
                return false;
            }
        }

        int GatherNextPage()
        {
            while (true)
            {
                // get our next header
                if(!FindNextPageHeader(out var hdr))
                    return -1;
                
                // if it's in a disposed stream, grab the next page instead
                if (_disposedStreamSerials.Contains(hdr.StreamSerial))
                    continue;
                
                // otherwise, add it
                if (AddPage(hdr))
                {
                    var callback = NewStream;
                    if (callback != null)
                    {
                        var ea = new NewStreamEventArgs(_packetReaders[hdr.StreamSerial]);
                        callback(this, ea);

                        if (ea.IgnoreStream)
                        {
                            _packetReaders[hdr.StreamSerial].Dispose();
                            continue;
                        }
                    }
                }
                return hdr.StreamSerial;
            }
        }

        // packet reader bits...
        internal void DisposePacketReader(OggPacketReader packetReader)
        {
            _disposedStreamSerials.Add(packetReader.StreamSerial);
            _packetReaders.Remove(packetReader.StreamSerial);
        }

        internal int PacketReadByte(long offset)
        {
            _stream.TakeLock();
            try
            {
                _stream.Position = offset;
                return _stream.ReadByte();
            }
            finally
            {
                _stream.ReleaseLock();
            }
        }

        internal void PacketDiscardThrough(long offset)
        {
            _stream.TakeLock();
            try
            {
                _stream.DiscardThrough(offset);
            }
            finally
            {
                _stream.ReleaseLock();
            }
        }

        internal void GatherNextPage(int streamSerial)
        {
            if (!_packetReaders.ContainsKey(streamSerial)) throw new ArgumentOutOfRangeException("streamSerial");

            int nextSerial;
            do
            {
                _stream.TakeLock();
                try
                {
                    if (_packetReaders[streamSerial].HasEndOfStream) break;

                    nextSerial = GatherNextPage();
                    if (nextSerial == -1)
                    {
                        foreach (var reader in _packetReaders)
                        {
                            if (!reader.Value.HasEndOfStream)
                            {
                                reader.Value.SetEndOfStream();
                            }
                        }
                        break;
                    }
                }
                finally
                {
                    _stream.ReleaseLock();
                }
            } while (nextSerial != streamSerial);
        }
    }
}
