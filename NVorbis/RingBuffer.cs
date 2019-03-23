/****************************************************************************
 * NVorbis                                                                  *
 * Copyright (C) 2014, Andrew Ward <afward@gmail.com>                       *
 *                                                                          *
 * See COPYING for license terms (Ms-PL).                                   *
 *                                                                          *
 ***************************************************************************/
using System;

namespace NVorbis
{
    class RingBuffer
    {
        float[] _buffer;
        int _start;
        int _end;
        int _bufLen;
        int _channels;

        internal RingBuffer(int size, int channels)
        {
            _buffer = new float[size];
            _start = _end = 0;
            _bufLen = size;
            _channels = channels;
        }

        internal void EnsureSize(int size)
        {
            // because _end == _start signifies no data, and _end is always 1 more than the data we have, we must make the buffer {channels} entries bigger than requested
            size += _channels;

            if (_bufLen < size)
            {
                var tmp = new float[size];
                Array.Copy(_buffer, _start, tmp, 0, _bufLen - _start);
                if (_end < _start)
                    Array.Copy(_buffer, 0, tmp, _bufLen - _start, _end);

                var end = Length;
                _start = 0;
                _end = end;
                _buffer = tmp;

                _bufLen = size;
            }
        }

        internal void CopyTo(Span<float> buffer)
        {
            int start = _start;
            RemoveItems(buffer.Length);

            // this is used to pull data out of the buffer, so we'll update the start position too...
            int len = (_end - start + _bufLen) % _bufLen;
            if (buffer.Length > len)
                throw new ArgumentException(nameof(buffer));

            int cnt = Math.Min(buffer.Length, _bufLen - start);
            _buffer.AsSpan(start, cnt).CopyTo(buffer);

            if (cnt < buffer.Length)
                _buffer.AsSpan(0, buffer.Length - cnt).CopyTo(buffer.Slice(cnt));
        }

        internal void RemoveItems(int count)
        {
            int cnt = (count + _start) % _bufLen;
            if (_end > _start)
            {
                if (cnt > _end || cnt < _start)
                    throw new ArgumentOutOfRangeException();
            }
            else
            {
                // wrap-around
                if (cnt < _start && cnt > _end)
                    throw new ArgumentOutOfRangeException();
            }

            _start = cnt;
        }

        internal void Clear()
        {
            _start = _end = 0;
        }

        internal int Length
        {
            get
            {
                int tmp = _end - _start;
                if (tmp < 0)
                    tmp += _bufLen;
                return tmp;
            }
        }

        internal void Write(int channel, int index, int start, int switchPoint, int end, float[] pcm, float[] window)
        {
            // this is the index of the first sample to merge
            int idx = (index + start) * _channels + channel + _start;
            while (idx >= _bufLen)
                idx -= _bufLen;

            // blech...  gotta fix the first packet's pointers
            if (idx < 0)
            {
                start -= index;
                idx = channel;
            }

            // go through and do the overlap
            for (; idx < _bufLen && start < switchPoint; idx += _channels, ++start)
                _buffer[idx] += pcm[start] * window[start];

            if (idx >= _bufLen)
            {
                idx -= _bufLen;
                for (; start < switchPoint; idx += _channels, ++start)
                    _buffer[idx] += pcm[start] * window[start];
            }

            // go through and write the rest
            for (; idx < _bufLen && start < end; idx += _channels, ++start)
                _buffer[idx] = pcm[start] * window[start];

            if (idx >= _bufLen)
            {
                idx -= _bufLen;
                for (; start < end; idx += _channels, ++start)
                    _buffer[idx] = pcm[start] * window[start];
            }

            // finally, make sure the buffer end is set correctly
            _end = idx;
        }
    }
}
