using Hexa.NET.SDL2;

namespace YodaStoriesNG.Engine.Audio;

/// <summary>
/// Manages sound effect playback using SDL2 audio.
/// </summary>
public unsafe class SoundManager : IDisposable
{
    private readonly string _soundPath;
    private readonly Dictionary<int, SDLAudioSpec> _loadedSounds = new();
    private readonly Dictionary<int, byte[]> _soundData = new();
    private bool _initialized;
    private bool _muted;

    // Common sound effect IDs
    public const int SoundPickup = 0;
    public const int SoundAttack = 1;
    public const int SoundHurt = 2;
    public const int SoundDeath = 3;
    public const int SoundDoor = 4;
    public const int SoundSuccess = 5;

    public SoundManager(string soundPath)
    {
        _soundPath = soundPath;
    }

    public bool Initialize()
    {
        // SDL audio is already initialized with SDL_INIT_AUDIO in GameRenderer
        _initialized = true;
        Console.WriteLine("Sound system initialized");
        return true;
    }

    /// <summary>
    /// Loads a sound file for the given sound ID.
    /// </summary>
    public bool LoadSound(int soundId, string fileName)
    {
        if (!_initialized)
            return false;

        var fullPath = Path.Combine(_soundPath, fileName);
        if (!File.Exists(fullPath))
        {
            // Try with .wav extension
            fullPath = Path.Combine(_soundPath, Path.GetFileNameWithoutExtension(fileName) + ".wav");
            if (!File.Exists(fullPath))
                return false;
        }

        try
        {
            // Load the WAV file data
            var data = File.ReadAllBytes(fullPath);
            _soundData[soundId] = data;
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load sound {fileName}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Plays a sound effect by ID.
    /// </summary>
    public void PlaySound(int soundId)
    {
        if (!_initialized || _muted)
            return;

        // For now, we'll just note that sound would play
        // Full SDL2 audio implementation requires more setup
        // Console.WriteLine($"[Sound] Playing sound {soundId}");
    }

    /// <summary>
    /// Plays a sound effect by filename.
    /// </summary>
    public void PlaySound(string fileName)
    {
        if (!_initialized || _muted)
            return;

        // Console.WriteLine($"[Sound] Playing {fileName}");
    }

    public bool IsMuted
    {
        get => _muted;
        set => _muted = value;
    }

    public void ToggleMute() => _muted = !_muted;

    public void Dispose()
    {
        _soundData.Clear();
        _loadedSounds.Clear();
    }
}
