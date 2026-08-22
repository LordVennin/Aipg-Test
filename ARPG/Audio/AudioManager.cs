using Microsoft.Xna.Framework.Audio;
using ARPG.Util;

namespace ARPG.Audio;

/// <summary>One entry in Data/Sounds/sounds.json — the game's sound vocabulary.
/// Every entry works with NO file present: the Synth field names a procedural
/// placeholder that is generated at runtime (same philosophy as the sprites).
/// Dropping a WAV into Assets/Sounds and naming it in File replaces the
/// placeholder with zero code changes.</summary>
public class SoundDef
{
    public string Id { get; set; }
    /// <summary>WAV file name under Assets/Sounds (44.1kHz 16-bit PCM). Optional.</summary>
    public string File { get; set; }
    /// <summary>Procedural placeholder kind (see AudioManager.Synthesize).</summary>
    public string Synth { get; set; }
    public float Volume { get; set; } = 1f;
    /// <summary>Random pitch spread per play (0.06 = ±6%) so repeats don't machine-gun.</summary>
    public float PitchVariance { get; set; } = 0.06f;
    /// <summary>Minimum ms between plays of this id (spam throttle).</summary>
    public int CooldownMs { get; set; } = 50;
}

/// <summary>
/// The whole audio stack: a JSON sound registry, WAV loading with procedural
/// synthesized placeholders as fallback, distance/pan attenuation against the local
/// player, per-id spam throttles and a master volume. Fully inert until Initialize()
/// succeeds — headless tests and machines without an audio device never notice it.
/// </summary>
public static class AudioManager
{
    private static bool _enabled;
    private static float _volume = 0.8f;
    private static Dictionary<string, SoundDef> _defs = new();
    private static readonly Dictionary<string, SoundEffect> _loaded = new();
    private static readonly Dictionary<string, long> _lastPlay = new();
    private static readonly Random _rng = new(12345);

    /// <summary>Set by the play screen: where the local player stands (attenuation).</summary>
    public static Func<System.Numerics.Vector2?> ListenerPos;

    public const int SampleRate = 22050;
    /// <summary>World distance at which a positioned sound fades to nothing.</summary>
    public const float HearingRange = 16f;

    public static bool Enabled => _enabled;

    public static void Initialize()
    {
        try
        {
            _defs = LoadRegistry(Path.Combine(AppContext.BaseDirectory, "Data", "Sounds", "sounds.json"))
                .ToDictionary(d => d.Id);
            // Probe the audio device with a tiny silent buffer — no device = stay silent.
            using (var probe = new SoundEffect(new byte[64], SampleRate, AudioChannels.Mono))
                probe.CreateInstance().Dispose();
            _enabled = true;
            Console.WriteLine($"[Audio] {_defs.Count} sound defs loaded; device ready.");
        }
        catch (Exception e)
        {
            _enabled = false;
            Console.WriteLine($"[Audio] Disabled (no device or bad registry): {e.Message}");
        }
    }

    /// <summary>Pure registry load — usable headless (tests validate the manifest).</summary>
    public static List<SoundDef> LoadRegistry(string path) =>
        Json.LoadFile<List<SoundDef>>(path) ?? new List<SoundDef>();

    public static void SetVolume(float v) => _volume = Math.Clamp(v, 0f, 1f);

    /// <summary>Play the first KNOWN id among the candidates, unpositioned (UI, self).</summary>
    public static void PlayUi(params string[] candidates) => PlayInternal(null, candidates);

    /// <summary>Play the first known id among the candidates at a world position —
    /// volume falls off with distance from the local player, pan follows screen x.</summary>
    public static void PlayWorld(System.Numerics.Vector2 at, params string[] candidates) =>
        PlayInternal(at, candidates);

    private static void PlayInternal(System.Numerics.Vector2? at, string[] candidates)
    {
        if (!_enabled || _volume <= 0f) return;
        SoundDef def = null;
        foreach (var id in candidates)
            if (id != null && _defs.TryGetValue(id, out def)) break;
        if (def == null) return;

        long now = Environment.TickCount64;
        if (_lastPlay.TryGetValue(def.Id, out long last) && now - last < def.CooldownMs) return;
        _lastPlay[def.Id] = now;

        float vol = _volume * def.Volume;
        float pan = 0f;
        if (at is { } pos && ListenerPos?.Invoke() is { } lp)
        {
            float dist = System.Numerics.Vector2.Distance(pos, lp);
            if (dist > HearingRange) return;
            vol *= 1f - dist / HearingRange;
            // Screen-space x of the offset (isometric): pan follows where the sound is.
            pan = Math.Clamp(((pos.X - lp.X) - (pos.Y - lp.Y)) / 12f, -1f, 1f) * 0.7f;
        }
        if (vol <= 0.01f) return;

        try
        {
            var fx = Resolve(def);
            float pitch = def.PitchVariance <= 0 ? 0f
                : ((float)_rng.NextDouble() * 2f - 1f) * def.PitchVariance;
            fx?.Play(Math.Clamp(vol, 0f, 1f), Math.Clamp(pitch, -1f, 1f), pan);
        }
        catch { /* a failed play must never take the game down */ }
    }

    /// <summary>WAV from Assets/Sounds when present, else the synthesized placeholder.</summary>
    private static SoundEffect Resolve(SoundDef def)
    {
        if (_loaded.TryGetValue(def.Id, out var cached)) return cached;
        SoundEffect fx = null;
        if (!string.IsNullOrEmpty(def.File))
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds", def.File);
            if (System.IO.File.Exists(path))
            {
                try
                {
                    using var stream = System.IO.File.OpenRead(path);
                    fx = SoundEffect.FromStream(stream);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[Audio] '{def.File}' unreadable ({e.Message}); using placeholder.");
                }
            }
        }
        fx ??= Synthesize(def.Synth, def.Id);
        _loaded[def.Id] = fx;
        return fx;
    }

    // ------------------------------------------------------------ procedural placeholders

    /// <summary>Tiny synthesized stand-in sounds, deterministic per id — enough to hear
    /// every hook and tune volumes before the real recordings arrive.</summary>
    public static readonly string[] SynthKinds =
    {
        "ui", "click", "swish", "thump", "slam", "bolt", "arrow", "hit", "hurt", "die",
        "coins", "pickup", "chest", "door", "levelup", "sip", "revive", "warn", "block",
    };

    private static SoundEffect Synthesize(string kind, string seedId)
    {
        var rng = new Random(seedId?.GetHashCode() ?? 1);
        float Noise() => (float)rng.NextDouble() * 2f - 1f;
        float[] samples = kind switch
        {
            "click" or "ui" => Render(0.03f, t => Noise() * Env(t, 0.03f, 24f)),
            "swish" => Render(0.14f, t => Noise() * MathF.Sin(t / 0.14f * MathF.PI) * 0.7f),
            "thump" => Render(0.1f, t => MathF.Sin(Tau * (85f - 300f * t) * t) * Env(t, 0.1f, 14f)),
            "slam" => Render(0.24f, t => (MathF.Sin(Tau * (70f - 120f * t) * t) + Noise() * 0.35f) * Env(t, 0.24f, 9f)),
            "bolt" => Render(0.2f, t => Square(Tau * (620f - 1800f * t) * t) * 0.4f * Env(t, 0.2f, 7f)),
            "arrow" => Render(0.09f, t => Noise() * MathF.Sin(t / 0.09f * MathF.PI) * 0.55f),
            "hit" => Render(0.05f, t => (Noise() * 0.6f + MathF.Sin(Tau * 180f * t)) * Env(t, 0.05f, 24f)),
            "hurt" => Render(0.12f, t => Saw(Tau * (230f - 500f * t) * t) * 0.55f * Env(t, 0.12f, 10f)),
            "die" => Render(0.32f, t => (MathF.Sin(Tau * (150f - 280f * t) * t) + Noise() * 0.4f) * Env(t, 0.32f, 7f)),
            "coins" => Render(0.16f, t => (Ping(t, 0f, 1250f) + Ping(t, 0.07f, 1680f)) * 0.5f),
            "pickup" => Render(0.09f, t => Ping(t, 0f, 920f) * 0.55f),
            "chest" => Render(0.18f, t => (Ping(t, 0f, 150f) + Ping(t, 0.09f, 112f)) * 0.8f),
            "door" => Render(0.28f, t => (MathF.Sin(Tau * (90f - 110f * t) * t) + Noise() * 0.25f) * Env(t, 0.28f, 6f)),
            "levelup" => Render(0.34f, t => (Ping(t, 0f, 440f) + Ping(t, 0.1f, 554f) + Ping(t, 0.2f, 659f)) * 0.6f),
            "sip" => Render(0.09f, t => MathF.Sin(Tau * (480f - 1600f * t) * t) * 0.45f * Env(t, 0.09f, 12f)),
            "revive" => Render(0.24f, t => MathF.Sin(Tau * (300f + 1600f * t) * t) * 0.4f * Env(t, 0.24f, 4f)),
            "warn" => Render(0.16f, t => Square(Tau * 320f * t) * 0.22f * (MathF.Sin(Tau * 18f * t) > 0 ? 1f : 0.2f)),
            "block" => Render(0.06f, t => (MathF.Sin(Tau * 820f * t) * 0.7f + Noise() * 0.3f) * Env(t, 0.06f, 26f)),
            _ => Render(0.05f, t => Noise() * Env(t, 0.05f, 20f)),
        };
        // Soft clip, convert to 16-bit PCM little-endian.
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            float s = MathF.Tanh(samples[i] * 0.9f);
            short v = (short)(s * short.MaxValue * 0.82f);
            bytes[i * 2] = (byte)(v & 0xff);
            bytes[i * 2 + 1] = (byte)((v >> 8) & 0xff);
        }
        return new SoundEffect(bytes, SampleRate, AudioChannels.Mono);
    }

    private const float Tau = MathF.PI * 2f;
    private static float Square(float phase) => MathF.Sin(phase) >= 0 ? 1f : -1f;
    private static float Saw(float phase) => 2f * (phase / Tau - MathF.Floor(phase / Tau + 0.5f));
    /// <summary>Exponential decay envelope with a 3ms anti-click attack.</summary>
    private static float Env(float t, float len, float rate) =>
        MathF.Min(1f, t / 0.003f) * MathF.Exp(-t * rate) * (t < len ? 1f : 0f);
    private static float Ping(float t, float start, float freq)
    {
        float lt = t - start;
        return lt < 0 ? 0f : MathF.Sin(Tau * freq * lt) * Env(lt, 0.12f, 22f);
    }

    private static float[] Render(float seconds, Func<float, float> wave)
    {
        int n = (int)(seconds * SampleRate);
        var s = new float[n];
        for (int i = 0; i < n; i++) s[i] = wave(i / (float)SampleRate);
        return s;
    }
}
