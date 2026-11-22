using ARPG.Net;

namespace ARPG.Persistence;

/// <summary>
/// Client-side autosave: writes the last server-sent character every
/// <see cref="Interval"/> seconds, on every map change and on level-up. The snapshot is
/// serialized on the calling (render) thread and written on a background task
/// (SaveManager guarantees one writer per file). UI-free so the headless test drives it.
/// </summary>
public sealed class Autosaver
{
    public const float Interval = 60f;

    private readonly GameClient _client;
    private float _timer;
    private int _lastLevel;
    private bool _pending;
    /// <summary>Why the last autosave ran ("interval", "map change", "level up").</summary>
    public string LastReason { get; private set; }
    public int SavesRequested { get; private set; }

    public Autosaver(GameClient client)
    {
        _client = client;
        // Events fire from inside the client's packet handling (render thread): mark
        // and save from Update, after the whole packet batch has been applied.
        client.MapChanged += () => Request("map change");
        client.CharacterUpdated += () =>
        {
            int level = _client.World.MyCharacter?.Level ?? 0;
            if (_lastLevel > 0 && level > _lastLevel) Request("level up");
            _lastLevel = level;
        };
    }

    private void Request(string reason)
    {
        _pending = true;
        LastReason = reason;
    }

    /// <summary>Call once per frame from the thread that owns the client.</summary>
    public void Update(float dt)
    {
        var character = _client.World.MyCharacter;
        if (_client.Status != ClientStatus.InGame || character == null) return;
        if (_lastLevel == 0) _lastLevel = character.Level;
        _timer += dt;
        if (_timer >= Interval) Request("interval");
        if (!_pending) return;
        _pending = false;
        _timer = 0;
        SavesRequested++;
        SaveManager.SaveCharacterInBackground(character);
    }
}
