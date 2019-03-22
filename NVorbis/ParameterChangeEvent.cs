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
    /// <summary>
    /// Event data for when a logical stream has a parameter change.
    /// </summary>
    [Serializable]
    public readonly struct ParameterChangeEvent
    {
        /// <summary>
        /// Gets the first packet after the parameter change.
        /// This would typically be the parameters packet.
        /// </summary>
        public DataPacket FirstPacket { get; }

        /// <summary>
        /// Creates a new instance of <see cref="ParameterChangeEvent"/>.
        /// </summary>
        /// <param name="firstPacket">The first packet after the parameter change.</param>
        public ParameterChangeEvent(DataPacket firstPacket)
        {
            FirstPacket = firstPacket;
        }
    }
}
