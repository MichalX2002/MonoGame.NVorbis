﻿/****************************************************************************
 * NVorbis                                                                  *
 * Copyright (C) 2014, Andrew Ward <afward@gmail.com>                       *
 *                                                                          *
 * See COPYING for license terms (Ms-PL).                                   *
 *                                                                          *
 ***************************************************************************/
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;

namespace NVorbis
{
    public class VorbisReader : IDisposable
    {
        IContainerReader _containerReader;
        List<VorbisStreamDecoder> _decoders;
        List<int> _serials;

        private VorbisReader()
        {
            ClipSamples = true;

            _decoders = new List<VorbisStreamDecoder>();
            _serials = new List<int>();
        }

        public VorbisReader(string fileName) :
            this(File.Open(fileName, FileMode.Open, FileAccess.Read, FileShare.Read), leaveOpen: false)
        {
        }

        public VorbisReader(Stream stream, bool leaveOpen) : this()
        {
            // try Ogg first
            var oggContainer = new Ogg.OggContainerReader(stream, leaveOpen);
            if (!LoadContainer(oggContainer))
            {
                // oops, not Ogg!
                // we don't support any other container types yet, so error out
                // TODO: Add Matroska fallback
                oggContainer.Dispose();
                throw new InvalidDataException("Could not determine container type.");
            }
            _containerReader = oggContainer;

            if (_decoders.Count == 0)
                throw new InvalidDataException("No Vorbis data found.");
        }

        public VorbisReader(IContainerReader containerReader) : this()
        {
            if (!LoadContainer(containerReader))
                throw new InvalidDataException("Container did not initialize.");

            _containerReader = containerReader;

            if (_decoders.Count == 0)
                throw new InvalidDataException("No Vorbis data found.");
        }

        public VorbisReader(IPacketProvider packetProvider) : this()
        {
            var ea = new NewStreamEventArgs(packetProvider);
            NewStream(this, ea);
            if (ea.IgnoreStream)
                throw new InvalidDataException("No Vorbis data found.");
        }

        bool LoadContainer(IContainerReader containerReader)
        {
            containerReader.NewStream += NewStream;
            if (!containerReader.Init())
            {
                containerReader.NewStream -= NewStream;
                return false;
            }
            return true;
        }

        void NewStream(object sender, NewStreamEventArgs ea)
        {
            var packetProvider = ea.PacketProvider;
            var decoder = new VorbisStreamDecoder(packetProvider);
            if (decoder.TryInit())
            {
                _decoders.Add(decoder);
                _serials.Add(packetProvider.StreamSerial);
            }
            else
            {
                // This is almost certainly not a Vorbis stream
                ea.IgnoreStream = true;
            }
        }

        public void Dispose()
        {
            if (_decoders != null)
            {
                foreach (var decoder in _decoders)
                    decoder.Dispose();

                _decoders.Clear();
                _decoders = null;
            }

            if (_containerReader != null)
            {
                _containerReader.NewStream -= NewStream;
                _containerReader.Dispose();
                _containerReader = null;
            }
        }

        VorbisStreamDecoder ActiveDecoder
        {
            get
            {
                if (_decoders == null)
                    throw new ObjectDisposedException(nameof(VorbisReader));
                return _decoders[StreamIndex];
            }
        }

        #region Public Interface

        /// <summary>
        /// Gets the number of channels in the current selected Vorbis stream
        /// </summary>
        public int Channels => ActiveDecoder._channels;

        /// <summary>
        /// Gets the sample rate of the current selected Vorbis stream
        /// </summary>
        public int SampleRate => ActiveDecoder._sampleRate;

        /// <summary>
        /// Gets the encoder's upper bitrate of the current selected Vorbis stream
        /// </summary>
        public int UpperBitrate => ActiveDecoder._upperBitrate;

        /// <summary>
        /// Gets the encoder's nominal bitrate of the current selected Vorbis stream
        /// </summary>
        public int NominalBitrate => ActiveDecoder._nominalBitrate;

        /// <summary>
        /// Gets the encoder's lower bitrate of the current selected Vorbis stream
        /// </summary>
        public int LowerBitrate => ActiveDecoder._lowerBitrate;

        /// <summary>
        /// Gets the encoder's vendor string for the current selected Vorbis stream
        /// </summary>
        public string Vendor => ActiveDecoder._vendor;

        /// <summary>
        /// Gets the comments in the current selected Vorbis stream
        /// </summary>
        public string[] Comments => ActiveDecoder._comments;

        /// <summary>
        /// Gets whether the previous short sample count was due to a parameter change in the stream.
        /// </summary>
        public bool IsParameterChange => ActiveDecoder.IsParameterChange;

        /// <summary>
        /// Gets the number of bits read that are related to framing and transport alone
        /// </summary>
        public long ContainerOverheadBits => ActiveDecoder.ContainerBits;

        /// <summary>
        /// Gets or sets whether to automatically apply clipping to samples returned by <see cref="ReadSamples"/>.
        /// </summary>
        public bool ClipSamples { get; set; }

        /// <summary>
        /// Gets stats from each decoder stream available
        /// </summary>
        public IEnumerable<IVorbisStreamStatus> Stats => _decoders.Select(d => d).Cast<IVorbisStreamStatus>();

        /// <summary>
        /// Gets the currently-selected stream's index
        /// </summary>
        public int StreamIndex { get; private set; }

        /// <summary>
        /// Reads decoded samples from the current logical stream
        /// </summary>
        /// <param name="buffer">The buffer to write the samples to.</param>
        /// <returns>The number of samples written</returns>
        public int ReadSamples(Span<float> buffer)
        {
            int samples = ActiveDecoder.ReadSamples(buffer);

            if (ClipSamples)
            {
                VorbisStreamDecoder decoder = _decoders[StreamIndex];
                for (int i = 0; i < samples; i++)
                    buffer[i] = Utils.ClipValue(buffer[i], ref decoder._clipped);
            }

            return samples;
        }

        /// <summary>
        /// Clears the parameter change flag so further samples can be requested.
        /// </summary>
        public void ClearParameterChange()
        {
            ActiveDecoder.IsParameterChange = false;
        }

        /// <summary>
        /// Returns the number of logical streams found so far in the physical container
        /// </summary>
        public int StreamCount => _decoders.Count;

        /// <summary>
        /// Searches for the next stream in a concatenated file
        /// </summary>
        /// <returns><c>True</c> if a new stream was found, otherwise <c>false</c>.</returns>
        public bool FindNextStream()
        {
            if (_containerReader == null)
                return false;
            return _containerReader.FindNextStream();
        }

        /// <summary>
        /// Switches to an alternate logical stream.
        /// </summary>
        /// <param name="index">The logical stream index to switch to</param>
        /// <returns><c>True</c> if the properties of the logical stream differ from those of the one previously being decoded. Otherwise, <c>False</c>.</returns>
        public bool SwitchStreams(int index)
        {
            if (index < 0 || index >= StreamCount)
                throw new ArgumentOutOfRangeException(nameof(index));

            if (_decoders == null)
                throw new ObjectDisposedException(nameof(VorbisReader));

            if (StreamIndex == index)
                return false;

            var curDecoder = _decoders[StreamIndex];
            StreamIndex = index;
            var newDecoder = _decoders[StreamIndex];

            return curDecoder._channels != newDecoder._channels 
                || curDecoder._sampleRate != newDecoder._sampleRate;
        }

        /// <summary>
        /// Gets or Sets the current timestamp of the decoder.  Is the timestamp before the next sample to be decoded
        /// </summary>
        public TimeSpan DecodedTime
        {
            get => TimeSpan.FromSeconds((double)ActiveDecoder.CurrentPosition / SampleRate);
            set => ActiveDecoder.SeekTo((long)(value.TotalSeconds * SampleRate));

        }

        /// <summary>
        /// Gets or Sets the current position of the next sample to be decoded.
        /// </summary>
        public long DecodedPosition
        {
            get => ActiveDecoder.CurrentPosition;
            set => ActiveDecoder.SeekTo(value);
        }

        /// <summary>
        /// Gets the total length of the current logical stream.
        /// </summary>
        public TimeSpan TotalTime
        {
            get
            {
                VorbisStreamDecoder decoder = ActiveDecoder;
                if (decoder.CanSeek)
                    return TimeSpan.FromSeconds((double)decoder.GetLastGranulePos() / decoder._sampleRate);
                else
                    return TimeSpan.MaxValue;
            }
        }

        /// <summary>
        /// Gets the total amount of samples in the current logical stream.
        /// </summary>
        public long TotalSamples
        {
            get
            {
                VorbisStreamDecoder decoder = ActiveDecoder;
                if (decoder.CanSeek)
                    return decoder.GetLastGranulePos();
                else
                    return long.MaxValue;
            }
        }
        
        #endregion
    }
}
