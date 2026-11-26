using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ThixGate.Core.Infrastructure;

namespace ThixGate.Sessions;

/// <summary>
/// Plays the sound of a remote desktop (16-bit PCM, any rate and channel count as the server sends it) on this computer: Windows through
/// waveOut, macOS through an AudioQueue - the system's own, no package. Pieces are queued as they come; when more than half a second waits,
/// new pieces are dropped, so the sound never lags behind the picture. A new rate or channel count opens the output again.
/// </summary>
internal sealed class AudioPlayer : IDisposable
{
    private const double MaxQueuedSeconds = 0.5;

    private readonly Lock _gate = new();
    private IOutput? _output;
    private (int Rate, int Channels) _format;
    private bool _failed;

    /// <summary>Plays one piece (any thread).</summary>
    public void Play(ReadOnlySpan<byte> pcm, int rate, int channels)
    {
        if (pcm.Length < 4 || rate <= 0 || channels is < 1 or > 8)
        {
            return;
        }

        lock (_gate)
        {
            if (_failed)
            {
                return;
            }

            try
            {
                if (_output is null || _format != (rate, channels))
                {
                    _output?.Dispose();
                    _output = null;
                    _format = (rate, channels);
                    _output = OperatingSystem.IsWindows() ? new WaveOut(rate, channels)
                        : OperatingSystem.IsMacOS() ? new AudioQueue(rate, channels)
                        : null;
                    if (_output is null)
                    {
                        _failed = true;
                        return;
                    }
                }

                var bytesPerSecond = rate * channels * 2;
                if (_output.QueuedBytes > bytesPerSecond * MaxQueuedSeconds)
                {
                    return;
                }

                _output.Write(pcm);
            }
            catch (Exception exception) when (exception is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException or ExternalException)
            {
                // No sound device, or the system refused: the session goes on without sound.
                _failed = true;
                Log.Warning($"Sound: {exception.Message}");
                _output?.Dispose();
                _output = null;
            }
        }
    }

    /// <summary>Stops at once (sound switched off): what waits is dropped.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _output?.Dispose();
            _output = null;
            _failed = false;
        }
    }

    public void Dispose() => Stop();

    private interface IOutput : IDisposable
    {
        long QueuedBytes { get; }

        void Write(ReadOnlySpan<byte> pcm);
    }

    // ================================================================= Windows: waveOut

    private sealed class WaveOut : IOutput
    {
        private const int WaveMapper = -1;
        private const uint WhdrDone = 0x1;

        private readonly IntPtr _device;
        private readonly List<IntPtr> _headers = [];

        public WaveOut(int rate, int channels)
        {
            var format = new WaveFormat
            {
                FormatTag = 1,
                Channels = (ushort)channels,
                SamplesPerSecond = (uint)rate,
                AverageBytesPerSecond = (uint)(rate * channels * 2),
                BlockAlign = (ushort)(channels * 2),
                BitsPerSample = 16,
                Size = 0,
            };
            var result = waveOutOpen(out _device, WaveMapper, ref format, IntPtr.Zero, IntPtr.Zero, 0);
            if (result != 0)
            {
                throw new InvalidOperationException($"waveOutOpen failed ({result}).");
            }
        }

        public long QueuedBytes
        {
            get
            {
                Reap();
                long queued = 0;
                foreach (var header in _headers)
                {
                    queued += Marshal.PtrToStructure<WaveHeader>(header).BufferLength;
                }

                return queued;
            }
        }

        public void Write(ReadOnlySpan<byte> pcm)
        {
            Reap();
            var data = Marshal.AllocHGlobal(pcm.Length);
            unsafe
            {
                pcm.CopyTo(new Span<byte>((void*)data, pcm.Length));
            }

            var header = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>());
            Marshal.StructureToPtr(new WaveHeader { Data = data, BufferLength = (uint)pcm.Length }, header, false);
            var size = (uint)Marshal.SizeOf<WaveHeader>();
            if (waveOutPrepareHeader(_device, header, size) != 0 || waveOutWrite(_device, header, size) != 0)
            {
                waveOutUnprepareHeader(_device, header, size);
                Marshal.FreeHGlobal(data);
                Marshal.FreeHGlobal(header);
                return;
            }

            _headers.Add(header);
        }

        public void Dispose()
        {
            waveOutReset(_device);
            foreach (var header in _headers)
            {
                Free(header);
            }

            _headers.Clear();
            waveOutClose(_device);
        }

        // Pieces that have been played give their memory back.
        private void Reap()
        {
            for (var index = _headers.Count - 1; index >= 0; index--)
            {
                if ((Marshal.PtrToStructure<WaveHeader>(_headers[index]).Flags & WhdrDone) != 0)
                {
                    Free(_headers[index]);
                    _headers.RemoveAt(index);
                }
            }
        }

        private void Free(IntPtr header)
        {
            var data = Marshal.PtrToStructure<WaveHeader>(header).Data;
            waveOutUnprepareHeader(_device, header, (uint)Marshal.SizeOf<WaveHeader>());
            Marshal.FreeHGlobal(data);
            Marshal.FreeHGlobal(header);
        }

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        private struct WaveFormat
        {
            public ushort FormatTag;
            public ushort Channels;
            public uint SamplesPerSecond;
            public uint AverageBytesPerSecond;
            public ushort BlockAlign;
            public ushort BitsPerSample;
            public ushort Size;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WaveHeader
        {
            public IntPtr Data;
            public uint BufferLength;
            public uint BytesRecorded;
            public IntPtr User;
            public uint Flags;
            public uint Loops;
            public IntPtr Next;
            public IntPtr Reserved;
        }

        [DllImport("winmm.dll")]
        private static extern int waveOutOpen(out IntPtr device, int deviceId, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);

        [DllImport("winmm.dll")]
        private static extern int waveOutPrepareHeader(IntPtr device, IntPtr header, uint size);

        [DllImport("winmm.dll")]
        private static extern int waveOutUnprepareHeader(IntPtr device, IntPtr header, uint size);

        [DllImport("winmm.dll")]
        private static extern int waveOutWrite(IntPtr device, IntPtr header, uint size);

        [DllImport("winmm.dll")]
        private static extern int waveOutReset(IntPtr device);

        [DllImport("winmm.dll")]
        private static extern int waveOutClose(IntPtr device);
    }

    // ================================================================= macOS: AudioQueue (AudioToolbox)

    private sealed unsafe class AudioQueue : IOutput
    {
        private const string Toolbox = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
        private const uint LinearPcm = 0x6C70636D; // 'lpcm'
        private const uint SignedPacked = 0x4 | 0x8;

        private static readonly Dictionary<IntPtr, AudioQueue> Queues = [];

        private readonly IntPtr _queue;
        private readonly Lock _buffers = new();
        private long _queued;
        private bool _started;

        public AudioQueue(int rate, int channels)
        {
            var format = new StreamDescription
            {
                SampleRate = rate,
                FormatId = LinearPcm,
                FormatFlags = SignedPacked,
                BytesPerPacket = (uint)(channels * 2),
                FramesPerPacket = 1,
                BytesPerFrame = (uint)(channels * 2),
                ChannelsPerFrame = (uint)channels,
                BitsPerChannel = 16,
            };
            var status = AudioQueueNewOutput(ref format, &Played, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out _queue);
            if (status != 0)
            {
                throw new InvalidOperationException($"AudioQueueNewOutput failed ({status}).");
            }

            lock (Queues)
            {
                Queues[_queue] = this;
            }
        }

        public long QueuedBytes => Interlocked.Read(ref _queued);

        public void Write(ReadOnlySpan<byte> pcm)
        {
            if (AudioQueueAllocateBuffer(_queue, (uint)pcm.Length, out var buffer) != 0)
            {
                return;
            }

            var header = (QueueBuffer*)buffer;
            pcm.CopyTo(new Span<byte>((void*)header->AudioData, pcm.Length));
            header->AudioDataByteSize = (uint)pcm.Length;
            Interlocked.Add(ref _queued, pcm.Length);
            if (AudioQueueEnqueueBuffer(_queue, buffer, 0, IntPtr.Zero) != 0)
            {
                Interlocked.Add(ref _queued, -pcm.Length);
                AudioQueueFreeBuffer(_queue, buffer);
                return;
            }

            if (!_started)
            {
                _started = AudioQueueStart(_queue, IntPtr.Zero) == 0;
            }
        }

        public void Dispose()
        {
            AudioQueueStop(_queue, true);
            AudioQueueDispose(_queue, true);
            lock (Queues)
            {
                Queues.Remove(_queue);
            }
        }

        // A buffer has been played: it goes back to the system.
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void Played(IntPtr user, IntPtr queue, IntPtr buffer)
        {
            AudioQueue? owner;
            lock (Queues)
            {
                Queues.TryGetValue(queue, out owner);
            }

            if (owner is not null)
            {
                Interlocked.Add(ref owner._queued, -((QueueBuffer*)buffer)->AudioDataByteSize);
            }

            AudioQueueFreeBuffer(queue, buffer);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StreamDescription
        {
            public double SampleRate;
            public uint FormatId;
            public uint FormatFlags;
            public uint BytesPerPacket;
            public uint FramesPerPacket;
            public uint BytesPerFrame;
            public uint ChannelsPerFrame;
            public uint BitsPerChannel;
            public uint Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct QueueBuffer
        {
            public uint AudioDataBytesCapacity;
            public IntPtr AudioData;
            public uint AudioDataByteSize;
        }

        [DllImport(Toolbox)]
        private static extern int AudioQueueNewOutput(ref StreamDescription format, delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void> callback, IntPtr user, IntPtr runLoop, IntPtr runLoopMode, uint flags, out IntPtr queue);

        [DllImport(Toolbox)]
        private static extern int AudioQueueAllocateBuffer(IntPtr queue, uint size, out IntPtr buffer);

        [DllImport(Toolbox)]
        private static extern int AudioQueueFreeBuffer(IntPtr queue, IntPtr buffer);

        [DllImport(Toolbox)]
        private static extern int AudioQueueEnqueueBuffer(IntPtr queue, IntPtr buffer, uint descriptions, IntPtr packetDescriptions);

        [DllImport(Toolbox)]
        private static extern int AudioQueueStart(IntPtr queue, IntPtr startTime);

        [DllImport(Toolbox)]
        private static extern int AudioQueueStop(IntPtr queue, bool immediate);

        [DllImport(Toolbox)]
        private static extern int AudioQueueDispose(IntPtr queue, bool immediate);
    }
}
