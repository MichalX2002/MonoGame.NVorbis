using System;
using System.Collections.Generic;

namespace NVorbis
{
    internal static class BufferPool
    {
        private static object _mutex = new object();
        private static Stack<byte[]> _pool = new Stack<byte[]>();

        public static int MAX_BUFFERS = 16;
        public static int BUFFER_SIZE = 1024 * 80;

        public static byte[] Rent()
        {
            lock (_mutex)
            {
                if (_pool.Count > 0)
                    return _pool.Pop();
                return new byte[BUFFER_SIZE];
            }
        }

        public static void Return(byte[] buffer)
        {
            if (buffer == null)
                return;

            if (buffer.Length != BUFFER_SIZE)
                throw new ArgumentException(
                    "The length must match this pool's constant per-buffer size.");
            
            lock (_mutex)
            {
                if (_pool.Count < MAX_BUFFERS)
                    _pool.Push(buffer);
            }
        }
    }
}