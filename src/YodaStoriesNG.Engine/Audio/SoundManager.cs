using Hexa.NET.SDL2;

namespace YodaStoriesNG.Engine.Audio;

/// <summary>
/// Sound effects, played the way the original's WaveMix did: several at once, mixed.
///
/// Every clip is converted once at load to 16-bit mono at <see cref="Rate"/>, so mixing is
/// just adding samples. Each frame <see cref="Update"/> tops SDL's audio queue up to
/// <see cref="QueueAhead"/> samples from whatever voices are playing - push, not SDL's
/// callback, so nothing runs on SDL's audio thread and there is no delegate to keep alive.
///
/// Sound IDs are indices into the data file's SNDS table; see WebFun's variant/*/sounds.ts.
/// Music is MIDI, which SDL2 cannot play, so only effects are handled here.
/// </summary>
public unsafe class SoundManager : IDisposable
{
    // Well-known IDs, the same in both games (WebFun variant/*/sounds.ts).
    public const int Hurt = 4;
    public const int NoGo = 6;

    private const int Rate = 22050;
    private const ushort AudioS16Sys = 0x8010;  // AUDIO_S16LSB; every platform we ship is little-endian
    private const int QueueAhead = Rate / 15;   // ~66ms: survives a slow frame, short enough to feel instant

    private readonly string _dataPath;
    private readonly Dictionary<int, short[]> _clips = new();
    private readonly List<Voice> _voices = new();
    private uint _device;

    private sealed class Voice
    {
        public required short[] Clip;
        public int Position;
    }

    public SoundManager(string dataPath)
    {
        _dataPath = dataPath;
    }

    public bool IsMuted { get; set; }

    public void ToggleMute() => IsMuted = !IsMuted;

    public bool Initialize()
    {
        var want = new SDLAudioSpec { Freq = Rate, Format = AudioS16Sys, Channels = 1, Samples = 512 };
        // allowed_changes = 0: SDL converts to whatever the hardware wants behind the queue.
        _device = SDL.OpenAudioDevice((byte*)null, 0, &want, (SDLAudioSpec*)null, 0);
        if (_device == 0)
        {
            Console.WriteLine($"No audio device, playing silent: {SDL.GetErrorS()}");
            return false;
        }

        SDL.PauseAudioDevice(_device, 0);
        return true;
    }

    /// <summary>
    /// Loads SNDS entry <paramref name="soundId"/>. Yoda names a bare "hurt.wav" that lives in
    /// sfx/; Indiana Jones names a full path from its developers' machine
    /// ("\INDY\MASTER\INDYHURT.WAV") for a file sitting next to the data file. So take the
    /// last path component and look for it, case-insensitively, in both places.
    /// </summary>
    public bool LoadSound(int soundId, string fileName)
    {
        if (_device == 0)
            return false;

        var name = fileName.Split('\\', '/').Last();
        var path = Data.GameDataFile.Find(_dataPath, name);
        if (path == null)
        {
            var sfx = Directory.Exists(_dataPath)
                ? Directory.EnumerateDirectories(_dataPath)
                    .FirstOrDefault(d => Path.GetFileName(d).Equals("sfx", StringComparison.OrdinalIgnoreCase))
                : null;
            path = sfx != null ? Data.GameDataFile.Find(sfx, name) : null;
        }

        if (path == null || !path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            return false;

        var clip = Decode(path);
        if (clip == null)
            return false;

        _clips[soundId] = clip;
        return true;
    }

    private static short[]? Decode(string path)
    {
        SDLAudioSpec spec;
        byte* wav;
        uint length;
        SDLAudioSpec* loaded = SDL.LoadWAVRW(SDL.RWFromFile(path, "rb"), 1, &spec, &wav, &length);
        if (loaded == null)
        {
            Console.WriteLine($"Could not load {Path.GetFileName(path)}: {SDL.GetErrorS()}");
            return null;
        }

        try
        {
            SDLAudioCVT cvt;
            if (SDL.BuildAudioCVT(&cvt, spec.Format, spec.Channels, spec.Freq, AudioS16Sys, 1, Rate) < 0)
                return null;

            // ConvertAudio works in place and may need up to len_mult times the input.
            var buffer = new byte[length * Math.Max(1, cvt.LenMult)];
            new ReadOnlySpan<byte>(wav, (int)length).CopyTo(buffer);

            fixed (byte* p = buffer)
            {
                cvt.Buf = p;
                cvt.Len = (int)length;
                if (cvt.Needed != 0 && SDL.ConvertAudio(&cvt) < 0)
                    return null;

                int bytes = cvt.Needed != 0 ? cvt.LenCvt : (int)length;
                return new ReadOnlySpan<short>(p, bytes / 2).ToArray();
            }
        }
        finally
        {
            SDL.FreeWAV(wav);
        }
    }

    /// <summary>Starts SNDS entry <paramref name="soundId"/>. Unknown or unloaded IDs are silent.</summary>
    public void PlaySound(int soundId)
    {
        if (IsMuted || soundId < 0 || !_clips.TryGetValue(soundId, out var clip))
            return;

        _voices.Add(new Voice { Clip = clip });
    }

    /// <summary>Stop active effects, including audio already queued to SDL.</summary>
    public void StopAllSounds()
    {
        _voices.Clear();
        if (_device != 0) SDL.ClearQueuedAudio(_device);
    }

    /// <summary>Mixes live voices into the device queue. Call once per frame.</summary>
    public void Update()
    {
        if (_device == 0 || _voices.Count == 0)
            return;

        int queued = (int)SDL.GetQueuedAudioSize(_device) / 2;
        int needed = QueueAhead - queued;
        if (needed <= 0)
            return;

        var mix = new int[needed];
        foreach (var voice in _voices)
        {
            int count = Math.Min(needed, voice.Clip.Length - voice.Position);
            for (int i = 0; i < count; i++)
                mix[i] += voice.Clip[voice.Position + i];
            voice.Position += count;
        }
        _voices.RemoveAll(v => v.Position >= v.Clip.Length);

        var samples = new short[needed];
        for (int i = 0; i < needed; i++)
            samples[i] = (short)Math.Clamp(mix[i], short.MinValue, short.MaxValue);

        fixed (short* p = samples)
            SDL.QueueAudio(_device, p, (uint)(needed * 2));
    }

    public void Dispose()
    {
        if (_device != 0)
            SDL.CloseAudioDevice(_device);
        _device = 0;
        _voices.Clear();
        _clips.Clear();
    }
}
