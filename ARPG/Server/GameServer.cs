using System.Net;
using System.Net.Sockets;
using System.Numerics;
using ARPG.Core;
using ARPG.Data;
using ARPG.Net;
using ARPG.Sim;
using ARPG.Util;
using LiteNetLib;
using LiteNetLib.Utils;

namespace ARPG.Server;

/// <summary>
/// The network host. Owns the authoritative ServerWorld and translates simulation events
/// into packets. The hosting player connects to this like any other client (via loopback),
/// so single player, hosting and joining all share one gameplay path.
/// </summary>
public class GameServer : IServerEvents
{
    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _net;
    public ServerWorld World { get; private set; }
    public GameData Data { get; }
    public int MapSeed { get; }
    public int LocalPort => _net.LocalPort;

    private readonly Dictionary<NetPeer, int> _peerToPlayer = new();
    private readonly Dictionary<int, NetPeer> _playerToPeer = new();
    private int _nextPlayerId = 1;
    private float _playerStateTimer, _enemyStateTimer, _pingTimer;

    // Dedicated simulation thread (see StartLoop). ALL server state — ServerWorld, the
    // peer maps, LiteNetLib polling — is touched exclusively from that thread once it
    // starts; everything else talks to the server through the network path.
    private Thread _thread;
    private volatile bool _running;

    /// <summary>Fixed simulation rate of the dedicated server thread.</summary>
    public const float TickRate = 60f;

    public GameServer(GameData data, int mapSeed)
    {
        Data = data;
        MapSeed = mapSeed;
        World = new ServerWorld(data, mapSeed, this);
        _net = new NetManager(_listener) { AutoRecycle = true };

        _listener.ConnectionRequestEvent += request =>
        {
            if (_peerToPlayer.Count >= GameNetConfig.MaxPlayers)
                request.Reject(MakeDenyData("Server is full."));
            else
                request.AcceptIfKey(GameNetConfig.ConnectionKey);
        };
        _listener.PeerConnectedEvent += peer =>
            Console.WriteLine($"[Server] Peer connected: {peer.Address}");
        _listener.PeerDisconnectedEvent += (peer, info) => OnPeerDisconnected(peer, info);
        _listener.NetworkReceiveEvent += (peer, reader, channel, method) =>
        {
            try { HandlePacket(peer, reader); }
            catch (Exception e) { Console.WriteLine($"[Server] Bad packet from {peer.Address}: {e.Message}"); }
        };
    }

    private static NetDataWriter MakeDenyData(string reason)
    {
        var w = new NetDataWriter();
        w.Put(reason);
        return w;
    }

    /// <summary>Listen on all interfaces (0.0.0.0) so LAN / ZeroTier / Meshnet peers can connect.
    /// Port 0 lets the OS choose (used for single player loopback hosting).</summary>
    public bool Start(int port)
    {
        bool ok = _net.Start(IPAddress.Any, IPAddress.IPv6Any, port);
        if (ok) Console.WriteLine($"[Server] Listening on 0.0.0.0:{_net.LocalPort}");
        return ok;
    }

    /// <summary>
    /// Run the simulation on its own dedicated thread with a stable fixed timestep,
    /// decoupled from the render loop: frame hitches on the host no longer stall the
    /// world, and remote players tick at a steady rate regardless of the host's FPS.
    /// The hosting player's own client keeps talking to it over loopback UDP like any
    /// remote client. Call after Start(). Idempotent.
    /// </summary>
    public void StartLoop()
    {
        if (_thread != null) return;
        _running = true;
        _thread = new Thread(RunLoop) { IsBackground = true, Name = "Scrollbound Server" };
        _thread.Start();
    }

    /// <summary>Fixed-timestep loop (accumulator pattern): late wakeups run catch-up ticks
    /// of exactly 1/TickRate each, capped so a long stall can't spiral.</summary>
    private void RunLoop()
    {
        const double step = 1.0 / TickRate;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        double previous = clock.Elapsed.TotalSeconds;
        double accumulator = 0;
        while (_running)
        {
            double now = clock.Elapsed.TotalSeconds;
            accumulator += now - previous;
            previous = now;
            if (accumulator > 0.25) accumulator = 0.25; // stall guard
            while (accumulator >= step && _running)
            {
                Update((float)step);
                accumulator -= step;
            }
            Thread.Sleep(1);
        }
    }

    /// <summary>Stops the simulation thread (if any), then the network. Safe to call from
    /// the game thread and safe to call more than once.</summary>
    public void Stop()
    {
        _running = false;
        if (_thread != null && _thread != Thread.CurrentThread)
            _thread.Join(1000);
        _thread = null;
        _net.Stop();
        _peerToPlayer.Clear();
        _playerToPeer.Clear();
    }

    /// <summary>One simulation step: network in, world tick, snapshots out. Public so the
    /// headless test harness (and a future standalone dedicated-server executable) can
    /// drive the loop itself instead of using StartLoop's thread.</summary>
    public void Update(float dt)
    {
        _net.PollEvents();
        World.Tick(dt);

        _playerStateTimer += dt;
        if (_playerStateTimer >= 0.05f) // 20 Hz
        {
            _playerStateTimer = 0;
            BroadcastPlayerStates();
        }
        _enemyStateTimer += dt;
        if (_enemyStateTimer >= 0.1f) // 10 Hz
        {
            _enemyStateTimer = 0;
            BroadcastEnemyStates();
        }
        _pingTimer += dt;
        if (_pingTimer >= 2f)
        {
            _pingTimer = 0;
            BroadcastPings();
        }
    }

    /// <summary>Round-trip pings for the HUD player list, measured by LiteNetLib per peer.</summary>
    private void BroadcastPings()
    {
        if (_peerToPlayer.Count == 0) return;
        var w = Packets.Make(PacketType.PlayerPings);
        w.Put(_peerToPlayer.Count);
        foreach (var (peer, playerId) in _peerToPlayer)
        {
            w.Put(playerId);
            w.Put((short)peer.Ping);
        }
        Broadcast(w, DeliveryMethod.Unreliable);
    }

    // ------------------------------------------------------------------ inbound

    private void HandlePacket(NetPeer peer, NetDataReader r)
    {
        var type = (PacketType)r.GetByte();

        if (type == PacketType.JoinRequest)
        {
            HandleJoin(peer, r);
            return;
        }

        if (!_peerToPlayer.TryGetValue(peer, out int playerId)) return;

        switch (type)
        {
            case PacketType.PlayerState:
            {
                var pos = r.GetVec2();
                var facing = r.GetVec2();
                float height = r.GetFloat();
                World.UpdatePlayerState(playerId, pos, facing, height);
                break;
            }
            case PacketType.UseSkill:
                World.UseSkill(playerId, r.GetString(), r.GetVec2());
                break;
            case PacketType.PickupRequest:
                World.RequestPickup(playerId, r.GetGuid());
                break;
            case PacketType.MoveItemRequest:
                World.MoveItem(playerId, ItemLocation.Read(r), ItemLocation.Read(r));
                break;
            case PacketType.DropItemRequest:
                World.DropItem(playerId, r.GetGuid());
                break;
            case PacketType.LearnSkillRequest:
                World.LearnSkill(playerId, r.GetString());
                break;
            case PacketType.AssignHotbarRequest:
                World.AssignHotbar(playerId, r.GetInt(), r.GetString());
                break;
            case PacketType.DebugCommand:
                World.DebugCommand(playerId, r.GetString(), r.GetString());
                break;
            case PacketType.DodgeRequest:
                World.RequestDodge(playerId, r.GetVec2());
                break;
            case PacketType.ApplyEnchantRequest:
                World.ApplyEnchant(playerId, r.GetGuid(), r.GetGuid());
                break;
        }
    }

    private void HandleJoin(NetPeer peer, NetDataReader r)
    {
        int version = r.GetInt();
        string name = r.GetString();
        string characterJson = r.GetString();

        if (version != GameNetConfig.ProtocolVersion)
        {
            var deny = Packets.Make(PacketType.JoinDeny);
            deny.Put($"Version mismatch (server {GameNetConfig.ProtocolVersion}, you {version}).");
            peer.Send(deny, DeliveryMethod.ReliableOrdered);
            peer.Disconnect();
            return;
        }

        CharacterData character = null;
        if (!string.IsNullOrEmpty(characterJson))
        {
            try { character = Json.Load<CharacterData>(characterJson); }
            catch (Exception e) { Console.WriteLine($"[Server] Invalid character from {name}: {e.Message}"); }
        }

        int playerId = _nextPlayerId++;
        var player = World.AddPlayer(playerId, string.IsNullOrWhiteSpace(name) ? $"Player{playerId}" : name, character);
        _peerToPlayer[peer] = playerId;
        _playerToPeer[playerId] = peer;

        // Accept: id, map seed, authoritative character state.
        var accept = Packets.Make(PacketType.JoinAccept);
        accept.Put(playerId);
        accept.Put(MapSeed);
        accept.PutVec2(player.Position);
        accept.Put(player.Height);
        accept.Put(player.Health);
        accept.Put(player.Stats.MaxHealth);
        accept.Put(player.Mana);
        accept.Put(Json.SaveCompact(player.Character));
        peer.Send(accept, DeliveryMethod.ReliableOrdered);

        // Existing world snapshot for the new player.
        foreach (var other in World.Players.Values)
        {
            if (other.Id == playerId) continue;
            peer.Send(PlayerJoinedPacket(other), DeliveryMethod.ReliableOrdered);
        }
        foreach (var enemy in World.Enemies.Values.Where(e => !e.Dead))
            peer.Send(EnemySpawnPacket(enemy), DeliveryMethod.ReliableOrdered);
        foreach (var drop in World.Drops.Values)
            peer.Send(WorldItemSpawnPacket(drop), DeliveryMethod.ReliableOrdered);

        // Announce to everyone else.
        BroadcastExcept(peer, PlayerJoinedPacket(player), DeliveryMethod.ReliableOrdered);

        // Held-weapon appearances: existing players' to the newcomer, the newcomer's to all.
        foreach (var other in World.Players.Values)
            if (other.Id != playerId)
                peer.Send(AppearancePacket(other), DeliveryMethod.ReliableOrdered);
        Broadcast(AppearancePacket(player), DeliveryMethod.ReliableOrdered);
        Console.WriteLine($"[Server] {player.Name} joined as player {playerId}");
    }

    private void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
    {
        if (!_peerToPlayer.TryGetValue(peer, out int playerId)) return;
        _peerToPlayer.Remove(peer);
        _playerToPeer.Remove(playerId);
        World.RemovePlayer(playerId);
        var w = Packets.Make(PacketType.PlayerLeft);
        w.Put(playerId);
        Broadcast(w, DeliveryMethod.ReliableOrdered);
        Console.WriteLine($"[Server] Player {playerId} disconnected ({info.Reason})");
    }

    // ------------------------------------------------------------------ outbound

    private void Broadcast(NetDataWriter w, DeliveryMethod method)
    {
        foreach (var peer in _peerToPlayer.Keys)
            peer.Send(w, method);
    }

    private void BroadcastExcept(NetPeer except, NetDataWriter w, DeliveryMethod method)
    {
        foreach (var peer in _peerToPlayer.Keys)
            if (peer != except)
                peer.Send(w, method);
    }

    private void SendTo(int playerId, NetDataWriter w, DeliveryMethod method)
    {
        if (_playerToPeer.TryGetValue(playerId, out var peer))
            peer.Send(w, method);
    }

    private NetDataWriter PlayerJoinedPacket(ServerPlayer p)
    {
        var w = Packets.Make(PacketType.PlayerJoined);
        w.Put(p.Id);
        w.Put(p.Name);
        w.PutVec2(p.Position);
        w.Put(p.Height);
        w.Put(p.Health);
        w.Put(p.Stats.MaxHealth);
        w.Put(p.Alive);
        return w;
    }

    private NetDataWriter EnemySpawnPacket(ServerEnemy e)
    {
        var w = Packets.Make(PacketType.EnemySpawn);
        w.Put(e.Id);
        w.Put(e.Def.Id);
        w.PutVec2(e.Position);
        w.Put(e.Height);
        w.Put(e.Health);
        w.Put(e.Def.MaxHealth);
        return w;
    }

    private NetDataWriter WorldItemSpawnPacket(WorldItem item)
    {
        var w = Packets.Make(PacketType.WorldItemSpawn);
        w.PutGuid(item.DropId);
        w.PutVec2(item.Position);
        w.Put(item.Height);
        w.Put(item.IsGold);
        if (item.IsGold) w.Put(item.GoldAmount);
        else w.Put(Json.SaveCompact(item.Item));
        return w;
    }

    private void BroadcastPlayerStates()
    {
        if (_peerToPlayer.Count == 0) return;
        var w = Packets.Make(PacketType.PlayerStates);
        w.Put(World.Players.Count);
        foreach (var p in World.Players.Values)
        {
            w.Put(p.Id);
            w.PutVec2(p.Position);
            w.PutVec2(p.Facing);
            w.Put(p.Height);
        }
        Broadcast(w, DeliveryMethod.Unreliable);
    }

    private void BroadcastEnemyStates()
    {
        if (_peerToPlayer.Count == 0) return;
        var alive = World.Enemies.Values.Where(e => !e.Dead).ToList();
        var w = Packets.Make(PacketType.EnemyStates);
        w.Put(alive.Count);
        foreach (var e in alive)
        {
            w.Put(e.Id);
            w.PutVec2(e.Position);
            w.Put((byte)e.State);
            // Debuff bitmask so clients can render per-debuff indicators.
            byte debuffs = 0;
            if (World.Time < e.StunnedUntil) debuffs |= EnemyDebuffs.Stunned;
            if (e.BurnTimeLeft > 0) debuffs |= EnemyDebuffs.Burning;
            w.Put(debuffs);
            w.Put(e.Height);
        }
        Broadcast(w, DeliveryMethod.Unreliable);
    }

    // ------------------------------------------------------------------ IServerEvents

    public void EnemySpawned(ServerEnemy e) => Broadcast(EnemySpawnPacket(e), DeliveryMethod.ReliableOrdered);

    public void EnemyHealthChanged(ServerEnemy e)
    {
        var w = Packets.Make(PacketType.EnemyHealth);
        w.Put(e.Id);
        w.Put(e.Health);
        Broadcast(w, DeliveryMethod.ReliableOrdered);
    }

    public void EnemyDied(ServerEnemy e)
    {
        var w = Packets.Make(PacketType.EnemyDeath);
        w.Put(e.Id);
        w.PutVec2(e.Position);
        Broadcast(w, DeliveryMethod.ReliableOrdered);
    }

    public void PlayerHealthChanged(ServerPlayer p)
    {
        var w = Packets.Make(PacketType.PlayerHealth);
        w.Put(p.Id);
        w.Put(p.Health);
        w.Put(p.Stats.MaxHealth);
        w.Put(p.Mana);
        Broadcast(w, DeliveryMethod.ReliableOrdered);
    }

    public void PlayerDied(ServerPlayer p)
    {
        var w = Packets.Make(PacketType.PlayerDeath);
        w.Put(p.Id);
        Broadcast(w, DeliveryMethod.ReliableOrdered);
    }

    public void PlayerRespawned(ServerPlayer p)
    {
        var w = Packets.Make(PacketType.PlayerRespawn);
        w.Put(p.Id);
        w.PutVec2(p.Position);
        w.Put(p.Height);
        w.Put(p.Health);
        w.Put(p.Stats.MaxHealth);
        w.Put(p.Mana);
        Broadcast(w, DeliveryMethod.ReliableOrdered);
    }

    public void ProjectileSpawned(ServerProjectile p)
    {
        var w = Packets.Make(PacketType.ProjectileSpawn);
        w.Put(p.Id);
        w.Put(p.FromPlayer);
        w.Put(p.SkillId ?? "");
        w.PutVec2(p.Position);
        w.Put(p.Height);
        w.PutVec2(p.Direction);
        w.Put(p.Speed);
        w.Put(p.MaxRange);
        Broadcast(w, DeliveryMethod.ReliableOrdered);
    }

    public void ProjectileDespawned(ServerProjectile p, Vector2 at)
    {
        var w = Packets.Make(PacketType.ProjectileDespawn);
        w.Put(p.Id);
        w.PutVec2(at);
        Broadcast(w, DeliveryMethod.ReliableOrdered);
    }

    public void WorldItemSpawned(WorldItem item) =>
        Broadcast(WorldItemSpawnPacket(item), DeliveryMethod.ReliableOrdered);

    public void WorldItemRemoved(WorldItem item, int pickedUpByPlayerId)
    {
        var w = Packets.Make(PacketType.WorldItemRemove);
        w.PutGuid(item.DropId);
        w.Put(pickedUpByPlayerId);
        Broadcast(w, DeliveryMethod.ReliableOrdered);
    }

    public void CharacterChanged(ServerPlayer p)
    {
        var w = Packets.Make(PacketType.CharacterState);
        w.Put(Json.SaveCompact(p.Character));
        SendTo(p.Id, w, DeliveryMethod.ReliableOrdered);
        // Equipment may have changed what the player is visibly holding.
        Broadcast(AppearancePacket(p), DeliveryMethod.ReliableOrdered);
    }

    private NetDataWriter AppearancePacket(ServerPlayer p)
    {
        var w = Packets.Make(PacketType.PlayerAppearance);
        w.Put(p.Id);
        w.Put(p.Character.MainHand?.BaseItemId ?? "");
        w.Put(p.Character.OffHand?.BaseItemId ?? "");
        return w;
    }

    public void SkillUsed(ServerPlayer p, string skillId, Vector2 effectPoint)
    {
        var w = Packets.Make(PacketType.SkillEffect);
        w.Put(p.Id);
        w.Put(skillId);
        w.PutVec2(effectPoint);
        w.Put(p.Height);
        Broadcast(w, DeliveryMethod.ReliableOrdered);
    }

    public void PlayerDodged(ServerPlayer p, Vector2 direction, float distance, float duration)
    {
        var w = Packets.Make(PacketType.DodgeEvent);
        w.Put(p.Id);
        w.PutVec2(direction);
        w.Put(distance);
        w.Put(duration);
        Broadcast(w, DeliveryMethod.ReliableOrdered);
    }

    public void DamageDealt(bool targetIsPlayer, int targetId, float amount, Skills.DamageKind kind, Vector2 position, bool blocked = false)
    {
        var w = Packets.Make(PacketType.DamageEvent);
        w.Put(targetIsPlayer);
        w.Put(targetId);
        w.Put(amount);
        w.Put((byte)kind);
        w.PutVec2(position);
        w.Put(blocked);
        Broadcast(w, DeliveryMethod.ReliableOrdered);
    }

    public void ChainEffect(string skillId, List<Vector2> points, float height)
    {
        var w = Packets.Make(PacketType.ChainEffect);
        w.Put(skillId);
        w.Put(height);
        w.Put((byte)points.Count);
        foreach (var pt in points) w.PutVec2(pt);
        Broadcast(w, DeliveryMethod.ReliableOrdered);
    }

    public void MessageFor(ServerPlayer p, string text)
    {
        var w = Packets.Make(PacketType.ServerMessage);
        w.Put(text);
        SendTo(p.Id, w, DeliveryMethod.ReliableOrdered);
    }
}
