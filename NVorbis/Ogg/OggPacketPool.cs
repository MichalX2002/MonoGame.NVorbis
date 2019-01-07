using System.Collections.Generic;

namespace NVorbis.Ogg
{
    internal static class OggPacketPool
    {
        private static object _mutex = new object();
        private static Stack<OggPacket> _pool = new Stack<OggPacket>();

        public static int MAX_PACKETS = 1024 * 128;

        public static OggPacket Rent(OggContainerReader reader, long streamOffset, int length)
        {
            lock (_mutex)
            {
                if (_pool.Count > 0)
                {
                    var packet = _pool.Pop();
                    packet.Set(reader, streamOffset, length);
                    return packet;
                }
                return new OggPacket(reader, streamOffset, length);
            }
        }

        public static void Return(OggPacket packet)
        {
            if (packet == null)
                return;
            
            lock (_mutex)
            {
                if (_pool.Count < MAX_PACKETS)
                    _pool.Push(packet);
            }
        }
    }
}