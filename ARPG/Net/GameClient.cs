using System.Numerics;
using ARPG.Core;
using ARPG.Data;
using ARPG.Items;
using ARPG.Sim;
using ARPG.Util;
using ARPG.World;
using LiteNetLib;
using LiteNetLib.Utils;

namespace ARPG.Net;

public enum ClientStatus
{
    Disconnected,
    Connecting,
    Joining,
    InGame,
}

/// <summary>
/// The game client: connects to a host (local or remote) by direct IP, sends input/requests,
/// and applies authoritative state from the server to its ClientWorld.
/// Contains no rendering/UI code — the UI layer reads from World and subscribes to events.
/// </summary>
public class GameClient
{
    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _net;
    private NetPeer _server;
    private readonly GameData _data;
    private float _stateTimer;

    public ClientWorld World { get; } = new();
    public ClientStatus Status { get; private set; } = ClientStatus.Disconnected;
    public string PlayerName { get; }
    public string LastDisconnectReason { get; private set; }
    public int PingMs => _server?.Ping ?? 0;

    /// <summary>Fired when the server confirms our join (map is ready afterwards).</summary>
    public event Action JoinedGame;
    /// <summary>Fired on any authoritative character update (inventory/skills/equipment).</summary>
    public event Action CharacterUpdated;
    /// <summary>Server info/error text for the HUD message line.</summary>
    public event Action<string> ServerMessageReceived;
    public event Action<string> Disconnected;

    private readonly CharacterData _initialCharacter;

    public GameClient(GameData data, string playerName, CharacterData savedCharacter)
    {
        _data = data;
        PlayerName = playerName;
        _initialCharacter = savedCharacter;
        _net = new NetManager(_listener) { AutoRecycle = true };

        _listener.PeerConnectedEvent += peer =>
        {
            _server = peer;
            Status = ClientStatus.Joining;
            var w = Packets.Make(PacketType.JoinRequest);
            w.Put(GameNetConfig.ProtocolVersion);
            w.Put(PlayerName);
            w.Put(_initialCharacter != null ? Json.SaveCompact(_initialCharacter) : "");
            peer.Send(w, DeliveryMethod.ReliableOrdered);
        };
        _listener.PeerDisconnectedEvent += (peer, info) =>
        {
            string reason = LastDisconnectReason ?? info.Reason switch
            {
                DisconnectReason.ConnectionFailed => "Could not reach the host.",
                DisconnectReason.Timeout => "Connection timed out.",
                DisconnectReason.ConnectionRejected => ReadRejectReason(info) ?? "Connection rejected by the host.",
                DisconnectReason.RemoteConnectionClose => "The host closed the connection.",
                _ => $"Disconnected ({info.Reason}).",
            };
            Status = ClientStatus.Disconnected;
            _server = null;
            Disconnected?.Invoke(reason);
        };
        _listener.NetworkReceiveEvent += (peer, reader, channel, method) =>
        {
            try { HandlePacket(reader); }
            catch (Exception e) { Console.WriteLine($"[Client] Bad packet: {e.Message}"); }
        };
    }

    private static string ReadRejectReason(DisconnectInfo info)
    {
        try
        {
            if (info.AdditionalData != null && !info.AdditionalData.EndOfData)
                return info.AdditionalData.GetString();
        }
        catch { /* no readable reason attached */ }
        return null;
    }

    public bool Connect(string host, int port, out string error)
    {
        error = null;
        if (!_net.Start())
        {
            error = "Could not start network client.";
            return false;
        }
        try
        {
            Status = ClientStatus.Connecting;
            _net.Connect(host, port, GameNetConfig.ConnectionKey);
            return true;
        }
        catch (Exception e)
        {
            Status = ClientStatus.Disconnected;
            error = $"Invalid address: {e.Message}";
            _net.Stop();
            return false;
        }
    }

    public void Disconnect()
    {
        LastDisconnectReason ??= "Left the game.";
        _net.Stop();
        Status = ClientStatus.Disconnected;
    }

    public void Update(float dt)
    {
        _net.PollEvents();
        if (Status != ClientStatus.InGame) return;

        World.Tick(dt);

        // Send local player state at 20 Hz (unreliable — newest wins).
        _stateTimer += dt;
        var me = World.Me;
        if (_stateTimer >= 0.05f && me != null && _server != null)
        {
            _stateTimer = 0;
            var w = Packets.Make(PacketType.PlayerState);
            w.PutVec2(me.Position);
            w.PutVec2(me.Facing);
            _server.Send(w, DeliveryMethod.Unreliable);
        }
    }

    // ------------------------------------------------------------------ requests

    private void Send(NetDataWriter w, DeliveryMethod method)
    {
        _server?.Send(w, method);
    }

    public void RequestUseSkill(string skillId, Vector2 target)
    {
        var w = Packets.Make(PacketType.UseSkill);
        w.Put(skillId);
        w.PutVec2(target);
        Send(w, DeliveryMethod.ReliableOrdered);
    }

    public void RequestPickup(Guid dropId)
    {
        var w = Packets.Make(PacketType.PickupRequest);
        w.PutGuid(dropId);
        Send(w, DeliveryMethod.ReliableOrdered);
    }

    public void RequestMoveItem(ItemLocation src, ItemLocation dst)
    {
        var w = Packets.Make(PacketType.MoveItemRequest);
        src.Write(w);
        dst.Write(w);
        Send(w, DeliveryMethod.ReliableOrdered);
    }

    public void RequestDropItem(Guid instanceId)
    {
        var w = Packets.Make(PacketType.DropItemRequest);
        w.PutGuid(instanceId);
        Send(w, DeliveryMethod.ReliableOrdered);
    }

    public void RequestLearnSkill(string skillId)
    {
        var w = Packets.Make(PacketType.LearnSkillRequest);
        w.Put(skillId);
        Send(w, DeliveryMethod.ReliableOrdered);
    }

    public void RequestAssignHotbar(int slot, string skillId)
    {
        var w = Packets.Make(PacketType.AssignHotbarRequest);
        w.Put(slot);
        w.Put(skillId ?? "");
        Send(w, DeliveryMethod.ReliableOrdered);
    }

    public void RequestApplyEnchant(Guid scrollInstanceId, Guid targetInstanceId)
    {
        var w = Packets.Make(PacketType.ApplyEnchantRequest);
        w.PutGuid(scrollInstanceId);
        w.PutGuid(targetInstanceId);
        Send(w, DeliveryMethod.ReliableOrdered);
    }

    public void RequestDodge(Vector2 direction)
    {
        var w = Packets.Make(PacketType.DodgeRequest);
        w.PutVec2(direction);
        Send(w, DeliveryMethod.ReliableOrdered);
    }

    public void SendDebugCommand(string cmd, string arg = "")
    {
        var w = Packets.Make(PacketType.DebugCommand);
        w.Put(cmd);
        w.Put(arg ?? "");
        Send(w, DeliveryMethod.ReliableOrdered);
    }

    // ------------------------------------------------------------------ inbound state

    private void HandlePacket(NetDataReader r)
    {
        var type = (PacketType)r.GetByte();
        switch (type)
        {
            case PacketType.JoinAccept:
            {
                World.MyPlayerId = r.GetInt();
                int mapSeed = r.GetInt();
                var pos = r.GetVec2();
                float hp = r.GetFloat();
                float maxHp = r.GetFloat();
                World.MyCharacter = Json.Load<CharacterData>(r.GetString());
                World.Map = new GameMap(mapSeed);
                World.Players[World.MyPlayerId] = new ClientPlayer
                {
                    Id = World.MyPlayerId,
                    Name = PlayerName,
                    Position = pos,
                    NetTarget = pos,
                    Health = hp,
                    MaxHealth = maxHp,
                    IsLocal = true,
                };
                World.RecomputeMyStats(_data);
                Status = ClientStatus.InGame;
                JoinedGame?.Invoke();
                break;
            }
            case PacketType.JoinDeny:
                LastDisconnectReason = r.GetString();
                break;

            case PacketType.PlayerJoined:
            {
                var p = new ClientPlayer { Id = r.GetInt(), Name = r.GetString() };
                p.Position = p.NetTarget = r.GetVec2();
                p.Health = r.GetFloat();
                p.MaxHealth = r.GetFloat();
                p.Alive = r.GetBool();
                if (p.Id != World.MyPlayerId)
                    World.Players[p.Id] = p;
                break;
            }
            case PacketType.PlayerLeft:
                World.Players.Remove(r.GetInt());
                break;

            case PacketType.PlayerStates:
            {
                int count = r.GetInt();
                for (int i = 0; i < count; i++)
                {
                    int id = r.GetInt();
                    var pos = r.GetVec2();
                    var facing = r.GetVec2();
                    if (id == World.MyPlayerId) continue; // local player is predicted locally
                    if (World.Players.TryGetValue(id, out var p))
                    {
                        p.NetTarget = pos;
                        p.Facing = facing;
                    }
                }
                break;
            }
            case PacketType.PlayerHealth:
            {
                int id = r.GetInt();
                float hp = r.GetFloat(), maxHp = r.GetFloat();
                if (World.Players.TryGetValue(id, out var p)) { p.Health = hp; p.MaxHealth = maxHp; }
                break;
            }
            case PacketType.PlayerDeath:
            {
                if (World.Players.TryGetValue(r.GetInt(), out var p)) { p.Alive = false; p.Health = 0; }
                break;
            }
            case PacketType.PlayerRespawn:
            {
                int id = r.GetInt();
                var pos = r.GetVec2();
                float hp = r.GetFloat(), maxHp = r.GetFloat();
                if (World.Players.TryGetValue(id, out var p))
                {
                    p.Alive = true;
                    p.Position = p.NetTarget = pos;
                    p.Health = hp;
                    p.MaxHealth = maxHp;
                }
                break;
            }

            case PacketType.EnemySpawn:
            {
                var e = new ClientEnemy { Id = r.GetInt(), TypeId = r.GetString() };
                e.Position = e.NetTarget = r.GetVec2();
                e.Health = r.GetFloat();
                e.MaxHealth = r.GetFloat();
                e.Def = _data.Enemies.GetValueOrDefault(e.TypeId);
                World.Enemies[e.Id] = e;
                break;
            }
            case PacketType.EnemyStates:
            {
                int count = r.GetInt();
                for (int i = 0; i < count; i++)
                {
                    int id = r.GetInt();
                    var pos = r.GetVec2();
                    byte state = r.GetByte();
                    if (World.Enemies.TryGetValue(id, out var e)) { e.NetTarget = pos; e.State = state; }
                }
                break;
            }
            case PacketType.EnemyHealth:
            {
                int id = r.GetInt();
                float hp = r.GetFloat();
                if (World.Enemies.TryGetValue(id, out var e)) e.Health = hp;
                break;
            }
            case PacketType.EnemyDeath:
            {
                int id = r.GetInt();
                var pos = r.GetVec2();
                World.Enemies.Remove(id);
                World.AddEffect(pos, 0.6f, 0.35f, "hit");
                break;
            }

            case PacketType.ProjectileSpawn:
            {
                var pr = new ClientProjectile { Id = r.GetInt(), FromPlayer = r.GetBool(), SkillId = r.GetString() };
                pr.Position = r.GetVec2();
                pr.Direction = r.GetVec2();
                pr.Speed = r.GetFloat();
                pr.MaxRange = r.GetFloat();
                World.Projectiles[pr.Id] = pr;
                break;
            }
            case PacketType.ProjectileDespawn:
            {
                int id = r.GetInt();
                var at = r.GetVec2();
                World.Projectiles.Remove(id);
                World.AddEffect(at, 0.4f, 0.2f, "hit");
                break;
            }

            case PacketType.WorldItemSpawn:
            {
                var drop = new ClientDrop { DropId = r.GetGuid(), Position = r.GetVec2() };
                bool isGold = r.GetBool();
                if (isGold) drop.GoldAmount = r.GetInt();
                else drop.Item = Json.Load<ItemInstance>(r.GetString());
                World.Drops[drop.DropId] = drop;
                break;
            }
            case PacketType.WorldItemRemove:
            {
                var dropId = r.GetGuid();
                r.GetInt(); // pickedUpBy (unused client-side beyond removal)
                World.Drops.Remove(dropId);
                break;
            }

            case PacketType.CharacterState:
            {
                World.MyCharacter = Json.Load<CharacterData>(r.GetString());
                World.RecomputeMyStats(_data);
                CharacterUpdated?.Invoke();
                break;
            }

            case PacketType.SkillEffect:
            {
                int playerId = r.GetInt();
                string skillId = r.GetString();
                // The server sends the exact computed impact point; visuals render there —
                // never at a client-side recomputation of the target.
                var effectPoint = r.GetVec2();
                var def = _data.Skills.GetValueOrDefault(skillId);
                if (def != null && World.Players.ContainsKey(playerId))
                {
                    switch (def.Archetype)
                    {
                        case Skills.SkillArchetype.MeleeStrike:
                            World.AddEffect(effectPoint, MathF.Max(0.8f, def.Radius), 0.18f, "melee");
                            break;
                        case Skills.SkillArchetype.MeleeSingle:
                        {
                            // Swipe arc around the caster, sweeping toward the impact point.
                            var caster = World.Players.GetValueOrDefault(playerId);
                            if (caster != null)
                            {
                                var dir = effectPoint - caster.Position;
                                World.Effects.Add(new ClientEffect
                                {
                                    Position = caster.Position,
                                    Radius = MathF.Max(1.1f, def.Range * 0.8f),
                                    TimeLeft = 0.22f,
                                    Duration = 0.22f,
                                    Kind = "swipe",
                                    Dir = dir.LengthSquared() > 0.001f ? Vector2.Normalize(dir) : caster.Facing,
                                });
                                World.AddEffect(effectPoint, 0.45f, 0.15f, "melee");
                            }
                            break;
                        }
                        case Skills.SkillArchetype.MeleeArea:
                            World.AddEffect(effectPoint, def.Radius, 0.3f, "slam");
                            break;
                        case Skills.SkillArchetype.AreaBurst:
                            World.AddEffect(effectPoint, def.Radius, 0.3f, "burst");
                            break;
                    }
                }
                break;
            }

            case PacketType.DodgeEvent:
            {
                int playerId = r.GetInt();
                r.GetVec2(); // direction (movement is predicted/synced separately)
                r.GetFloat(); // distance
                float duration = r.GetFloat();
                World.DodgeEventsSeen++;
                if (World.Players.TryGetValue(playerId, out var dodger))
                    dodger.DodgeTimeLeft = duration;
                break;
            }

            case PacketType.DamageEvent:
            {
                var fn = new FloatingNumber
                {
                    TargetIsPlayer = r.GetBool(),
                };
                int targetId = r.GetInt();
                fn.Amount = r.GetFloat();
                fn.Kind = r.GetByte();
                fn.Position = r.GetVec2();
                // Anchor to the entity's current client-side position when we know it.
                if (fn.TargetIsPlayer && World.Players.TryGetValue(targetId, out var tp))
                    fn.Position = tp.Position;
                else if (!fn.TargetIsPlayer && World.Enemies.TryGetValue(targetId, out var te))
                    fn.Position = te.Position;
                World.FloatingNumbers.Add(fn);
                break;
            }

            case PacketType.PlayerAppearance:
            {
                int playerId = r.GetInt();
                string weaponBaseId = r.GetString();
                if (World.Players.TryGetValue(playerId, out var p))
                    p.WeaponBaseId = string.IsNullOrEmpty(weaponBaseId) ? null : weaponBaseId;
                break;
            }

            case PacketType.ServerMessage:
                ServerMessageReceived?.Invoke(r.GetString());
                break;
        }
    }
}
