﻿using System.ComponentModel;
using SharpAudio.Codec.FFMPEG;
using System;
using System.IO;
using System.Threading.Tasks;
using SharpAudio.SpectrumAnalysis;
using System.Numerics;

namespace SharpAudio.Codec
{

    public sealed class SoundStream : IDisposable, INotifyPropertyChanged
    {
        private Decoder _decoder;
        private byte[] _silence;
        private AudioBuffer _buffer;
        private byte[] _data;
        private readonly TimeSpan SampleQuantum = TimeSpan.FromSeconds(0.2);
        private readonly TimeSpan SampleWait = TimeSpan.FromMilliseconds(1);
        private bool _hasSpectrumData;
        private byte[] _latestSample;
        private object _latesSampleLock = new object();
        public event EventHandler<double[,]> FFTDataReady;
        protected const double MinDbValue = -90;
        protected const double MaxDbValue = 0;
        protected const double DbScale = MaxDbValue - MinDbValue;
        private readonly int fftLength = 512;
        private readonly int binaryExp;


        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// The audio format of this stream
        /// </summary>
        public AudioFormat Format => _decoder.Format;

        /// <summary>
        /// The metadata of the decoded data;
        /// </summary>
        // public AudioMetadata Metadata => _decoder.Metadata;

        /// <summary>
        /// The underlying source
        /// </summary>
        // public AudioSource Source { get; private set; }

        /// <summary>
        /// Wether or not the audio is finished
        /// </summary>
        public bool IsPlaying => _state == SoundStreamState.Playing;

        /// <summary>
        /// Wether or not the audio is streamed
        /// </summary>
        public bool IsStreamed { get; }

        private SoundSink _backend;

        // private AudioEngine _engine;

        /// <summary>
        /// The volume of the source
        /// </summary>
        public float Volume
        {
            get => _backend?.Source.Volume ?? 0;
            set => _backend.Source.Volume = value;
        }

        /// <summary>
        /// Duration when provided by the decoder. Otherwise 0
        /// </summary>
        public TimeSpan Duration => _decoder.Duration;

        /// <summary>
        /// Current position inside the stream
        /// </summary>
        public TimeSpan Position => _decoder.Position;

        private volatile SoundStreamState _state = SoundStreamState.PreparePlay;
        private volatile bool hasDecodedSamples = false;

        public SoundStreamState State => _state;

        public void TrySeek(TimeSpan seek)
        {
            _backend.ClearBuffers();
            _decoder.TrySeek(seek);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SoundStream"/> class.
        /// </summary>
        /// <param name="stream">The sound stream.</param>
        /// <param name="engine">The audio engine</param>
        public SoundStream(Stream stream, SoundSink backend)
        {
            if (stream == null)
                throw new ArgumentNullException("Stream cannot be null!");

            binaryExp = (int)Math.Log(fftLength, 2.0);

            IsStreamed = !stream.CanSeek;

            _backend = backend;

            _decoder = new FFmpegDecoder(stream);

            Task.Factory.StartNew(MainLoop, TaskCreationOptions.LongRunning | TaskCreationOptions.AttachedToParent);
        }

        private double[,] FFT2Double(Complex[,] fftResults, int ch, int fftLength)
        {
            // Only return the N/2 bins since that's the nyquist limit.
            var n = fftLength / 2;
            var processedFFT = new double[ch, n];

            for (int c = 0; c < ch; c++)
                for (int i = 0; i < n; i++)
                {
                    var complex = fftResults[c, i];

                    var magnitude = complex.Magnitude;
                    if (magnitude == 0)
                    {
                        continue;
                    }

                    // decibel
                    var result = (((20 * Math.Log10(magnitude)) - MinDbValue) / DbScale) * 1;

                    // normalised decibel
                    //var result = (((10 * Math.Log10((complex.Real * complex.Real) + (complex.Imaginary * complex.Imaginary))) - MinDbValue) / DbScale) * 1;

                    // linear
                    //var result = (magnitude * 9) * 1;

                    // sqrt                
                    //var result = ((Math.Sqrt(magnitude)) * 2) * 1;

                    processedFFT[c, i] = Math.Max(0, result);
                }

            return processedFFT;
        }

        private async Task SpectrumLoop()
        {
            // Assuming 16 bit PCM, Little-endian, Variable Channels.
            var totalCh = _decoder.Format.Channels;
            var specSamples = fftLength * totalCh * sizeof(short);
            var curChByteRaw = 0;
            var tempBuf = new byte[specSamples];
            var samplesDouble = new double[totalCh, fftLength];
            var channelCounters = new int[totalCh];
            var complexSamples = new Complex[totalCh, fftLength];
            var shortDivisor = (double)short.MaxValue;
            var cachedWindowVal = new double[fftLength];

            for (int i = 0; i < fftLength; i++)
            {
                cachedWindowVal[i] = FastFourierTransform.HammingWindow(i, fftLength);
            }

            do
            {
                await Task.Delay(SampleWait);

                if (_state == SoundStreamState.Paused || FFTDataReady is null) continue;

                bool gotData = false;

                lock (_latesSampleLock)
                {
                    if (_hasSpectrumData)
                    {
                        _hasSpectrumData = false;

                        if (_latestSample.Length < tempBuf.Length)
                        {
                            Array.Clear(tempBuf, 0, tempBuf.Length);
                            Buffer.BlockCopy(_latestSample, 0, tempBuf, 0, _latestSample.Length);
                        }
                        else
                            tempBuf = _latestSample;

                        gotData = true;
                    }
                }

                if (!gotData)
                {
                    continue;
                }

                var rawSamplesShort = tempBuf.AsMemory().AsShorts().Slice(0, fftLength * totalCh);

                // Channel de-interleaving
                for (int i = 0; i < rawSamplesShort.Length; i++)
                {
                    samplesDouble[curChByteRaw, channelCounters[curChByteRaw]] = rawSamplesShort.Span[i] / shortDivisor;
                    channelCounters[curChByteRaw]++;
                    curChByteRaw++;
                    curChByteRaw %= totalCh;
                }

                Array.Clear(channelCounters, 0, channelCounters.Length);

                // Process FFT for each channel.
                for (int curCh = 0; curCh < totalCh; curCh++)
                {
                    for (int i = 0; i < fftLength; i++)
                    {
                        var windowed_sample = samplesDouble[curCh, i] * cachedWindowVal[i];
                        complexSamples[curCh, i] = new Complex(windowed_sample, 0);
                    }

                    FastFourierTransform.ProcessFFT(true, binaryExp, complexSamples, curCh);
                }

                FFTDataReady?.Invoke(this, FFT2Double(complexSamples, totalCh, fftLength));

                Array.Clear(samplesDouble, 0, samplesDouble.Length);

            } while (_state != SoundStreamState.Stop);
        }

        /// <summary>
        /// Start playing the soundstream 
        /// </summary>
        public void PlayPause()
        {
            switch (_state)
            {
                case SoundStreamState.Idle:
                    _state = SoundStreamState.PreparePlay;
                    break;

                case SoundStreamState.PreparePlay:
                case SoundStreamState.Playing:
                    _state = SoundStreamState.Paused;
                    break;

                case SoundStreamState.Paused:
                    _state = SoundStreamState.Playing;
                    break;
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));
        }

        private async Task MainLoop()
        {
            do
            {
                await Task.Delay(SampleWait);

                switch (_state)
                {
                    case SoundStreamState.PreparePlay:

                        _state = SoundStreamState.Paused;

                        _ = Task.Factory.StartNew(SpectrumLoop, TaskCreationOptions.LongRunning | TaskCreationOptions.AttachedToParent);

                        break;

                    case SoundStreamState.Playing:

                        if (_decoder.IsFinished)
                        {
                            _state = SoundStreamState.Stop;
                        }

                        if (_backend.NeedsNewSample)
                        {
                            var res = _decoder.GetSamples(SampleQuantum, ref _data);

                            if (res == 0)
                                continue;

                            lock (_latesSampleLock)
                            {
                                _latestSample = _data;
                                _hasSpectrumData = true;
                            }

                            _backend.Send(_data);
                        }


                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Position)));
                        break;

                    case SoundStreamState.Paused:
                        break;
                }


            } while (_state != SoundStreamState.Stop);

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Position)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));
        }

        /// <summary>
        /// Stop the soundstream
        /// </summary>
        public void Stop()
        {
            _state = SoundStreamState.Stop;
        }

        public void Dispose()
        {
            _state = SoundStreamState.Stop;
            FFTDataReady = null;
            _decoder?.Dispose();
            _buffer?.Dispose();
        }
    }
}
