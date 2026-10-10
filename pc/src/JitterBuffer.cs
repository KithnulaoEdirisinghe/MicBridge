using System;

namespace MicBridge
{
    /// <summary>
    /// Holds a little 16-bit mono audio between the network thread (Write) and the sound card thread (Read).
    /// It absorbs Wi-Fi timing jitter and quietly speeds up or slows down playback by a fraction of a
    /// percent so the phone's clock and the PC's clock never drift apart. Nothing here allocates.
    /// </summary>
    sealed unsafe class JitterBuffer
    {
        const int Capacity = 32768;          // samples (0.68 s at 48 kHz, 64 KB)
        const int Mask = Capacity - 1;
        const int FadeLen = 96;              // 2 ms ramp used wherever audio starts or stops
        const double MaxAdjust = 0.01;       // never bend the speed by more than 1 %

        readonly object _lock = new object();
        readonly short[] _buf = new short[Capacity];
        int _read, _count;

        int _rate = 48000;
        int _target = 2880;                  // samples we like to keep in reserve after each read
        int _max = 7680;                     // reserve beyond which we skip ahead instead of gliding
        int _deadband = 144;

        bool _buffering = true;
        bool _fadeNextWrite;
        int _fadeIn;
        double _frac;                        // sub-sample read position, 0 <= _frac < 1
        double _adjust;                      // current speed offset (0 = exactly real time)
        double _average;                     // smoothed reserve
        float _gain = 1f;

        int _peak, _underruns, _resyncs;

        public int Underruns { get { return _underruns; } }
        public int Resyncs { get { return _resyncs; } }
        public int FillMs { get { return _count * 1000 / _rate; } }
        public float Gain { set { _gain = value; } }

        /// <summary>Highest output level (0..32767) since the previous call.</summary>
        public int TakePeak()
        {
            int p = _peak;
            _peak = 0;
            return p;
        }

        public void Configure(int sampleRate, int bufferMs)
        {
            lock (_lock)
            {
                _rate = sampleRate;
                SetTargets(bufferMs);
                _read = 0;
                _count = 0;
                _buffering = true;
                _fadeNextWrite = false;
                _underruns = 0;
                _resyncs = 0;
                _peak = 0;
            }
        }

        public void SetBufferMs(int bufferMs)
        {
            lock (_lock)
            {
                SetTargets(bufferMs);
                _buffering = true;           // re-centre on the new latency straight away
            }
        }

        void SetTargets(int bufferMs)
        {
            _target = _rate * bufferMs / 1000;
            _max = _target * 2 + _rate * 40 / 1000;
            int limit = Capacity - _rate / 8;
            if (_max > limit) _max = limit;
            if (_target > _max / 2) _target = _max / 2;
            _deadband = _rate * 3 / 1000;
        }

        /// <summary>Appends 16-bit little-endian samples straight from a network packet.</summary>
        public void Write(byte[] source, int offset, int samples)
        {
            if (samples <= 0) return;
            lock (_lock)
            {
                if (samples > Capacity) { offset += (samples - Capacity) * 2; samples = Capacity; }
                int free = Capacity - _count;
                if (samples > free)
                {
                    // Reader has stalled: keep the newest audio.
                    int drop = samples - free;
                    _read = (_read + drop) & Mask;
                    _count -= drop;
                    _resyncs++;
                }
                int w = (_read + _count) & Mask;
                int first = Capacity - w;
                if (first > samples) first = samples;
                Buffer.BlockCopy(source, offset, _buf, w * 2, first * 2);
                if (first < samples) Buffer.BlockCopy(source, offset + first * 2, _buf, 0, (samples - first) * 2);
                if (_fadeNextWrite)
                {
                    _fadeNextWrite = false;
                    int n = samples < FadeLen ? samples : FadeLen;
                    for (int i = 0; i < n; i++)
                    {
                        int k = (w + i) & Mask;
                        _buf[k] = (short)(_buf[k] * i / n);
                    }
                }
                _count += samples;
            }
        }

        /// <summary>Stands in for lost packets: fades the tail out, inserts silence, fades the next packet in.</summary>
        public void WriteGap(int samples)
        {
            if (samples <= 0) return;
            lock (_lock)
            {
                int end = _read + _count;
                int n = _count < FadeLen ? _count : FadeLen;
                for (int i = 0; i < n; i++)
                {
                    int k = (end - 1 - i) & Mask;
                    _buf[k] = (short)(_buf[k] * i / n);
                }
                int free = Capacity - _count;
                if (samples > free) samples = free;
                for (int i = 0; i < samples; i++) _buf[(end + i) & Mask] = 0;
                _count += samples;
                _fadeNextWrite = true;
            }
        }

        /// <summary>Fills <paramref name="frames"/> frames for the sound card (same sample on every channel).</summary>
        public void Read(short* dst, int frames, int channels)
        {
            if (frames <= 0) return;
            int peak = 0;
            lock (_lock)
            {
                int need = frames + (frames >> 6) + 2;   // worst case consumed at +1 %, plus the look-ahead sample
                int fill = _count;

                if (_buffering)
                {
                    if (fill < _target + need) { Silence(dst, frames * channels); return; }
                    Skip(fill - (_target + need));       // start exactly at the latency that was asked for
                    fill = _count;
                    Restart(fill - frames);
                    _buffering = false;
                }
                else if (fill < need)
                {
                    // Ran dry: play out what is left with a fade, then wait for the reserve to rebuild.
                    int n = fill < frames ? fill : frames;
                    int ramp = n < FadeLen ? n : FadeLen;
                    float g = _gain;
                    for (int i = 0; i < n; i++)
                    {
                        int v = Clip((int)(_buf[(_read + i) & Mask] * g));
                        int left = n - 1 - i;
                        if (left < ramp) v = v * left / ramp;
                        for (int c = 0; c < channels; c++) *dst++ = (short)v;
                    }
                    Silence(dst, (frames - n) * channels);
                    _read = (_read + fill) & Mask;
                    _count = 0;
                    _buffering = true;
                    _underruns++;
                    return;
                }
                else if (fill - frames > _max)
                {
                    // Far too much queued (e.g. the PC stalled): jump back to the wanted latency.
                    Skip(fill - (_target + need));
                    fill = _count;
                    Restart(fill - frames);
                    _resyncs++;
                }

                // Speed control: glide towards the target reserve.
                double k = frames / (double)_rate;       // one-second smoothing
                if (k > 1) k = 1;
                _average += ((fill - frames) - _average) * k;
                double error = _average - _target;
                double adjust = _adjust;
                bool settle = false;
                if (error > _deadband || error < -_deadband)
                {
                    adjust = error / (_rate * 4.0);
                    if (adjust > MaxAdjust) adjust = MaxAdjust; else if (adjust < -MaxAdjust) adjust = -MaxAdjust;
                }
                else if (_frac == 0)
                {
                    adjust = 0;
                }
                else
                {
                    // On target but between two samples: keep gliding until we land on one, then play 1:1.
                    adjust = adjust < 0 ? -0.0005 : 0.0005;
                    settle = true;
                }

                int r = _read;
                double frac = _frac;
                float gain = _gain;
                int fade = _fadeIn;
                short[] buf = _buf;

                if (adjust == 0 && frac == 0)
                {
                    for (int i = 0; i < frames; i++)
                    {
                        int v = Clip((int)(buf[r & Mask] * gain));
                        r++;
                        if (fade > 0) { v = v * (FadeLen - fade) / FadeLen; fade--; }
                        int a = v < 0 ? -v : v;
                        if (a > peak) peak = a;
                        *dst++ = (short)v;
                        if (channels == 2) *dst++ = (short)v;
                    }
                }
                else
                {
                    for (int i = 0; i < frames; i++)
                    {
                        int s0 = buf[r & Mask], s1 = buf[(r + 1) & Mask];
                        int v = Clip((int)((s0 + (s1 - s0) * frac) * gain));
                        if (fade > 0) { v = v * (FadeLen - fade) / FadeLen; fade--; }
                        int a = v < 0 ? -v : v;
                        if (a > peak) peak = a;
                        *dst++ = (short)v;
                        if (channels == 2) *dst++ = (short)v;

                        frac += adjust;
                        if (frac >= 1.0)
                        {
                            r += 2;                       // skipped one sample
                            if (settle) { frac = 0; adjust = 0; settle = false; } else frac -= 1.0;
                        }
                        else if (frac <= 0.0 && adjust < 0)
                        {
                            if (settle) { r++; frac = 0; adjust = 0; settle = false; }
                            else if (frac < 0.0) frac += 1.0;   // repeated one sample (r stays)
                            else r++;
                        }
                        else
                        {
                            r++;
                        }
                    }
                }

                int used = r - _read;
                _read = r & Mask;
                _count -= used;
                _frac = frac;
                _adjust = adjust;
                _fadeIn = fade;
            }
            if (peak > _peak) _peak = peak;
        }

        void Skip(int samples)
        {
            if (samples <= 0) return;
            _read = (_read + samples) & Mask;
            _count -= samples;
        }

        void Restart(int reserve)
        {
            _average = reserve;
            _frac = 0;
            _adjust = 0;
            _fadeIn = FadeLen;
        }

        static int Clip(int v)
        {
            return v > 32767 ? 32767 : (v < -32768 ? -32768 : v);
        }

        static void Silence(short* dst, int count)
        {
            for (int i = 0; i < count; i++) dst[i] = 0;
        }
    }
}
