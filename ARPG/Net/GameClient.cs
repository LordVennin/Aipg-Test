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
    /// <summary>The merchant's stock for the local player (on shop open and after buys).</summary>
    public event Action<int, List<ClientShopEntry>> ShopStockReceived;

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
            w.Put(me.Height);
            _server.Send(w, DeliveryMethod.Unreliable);
        }
    }

    // ------------------------------------------------------------------ requests

    private void Send(NetDataWriter w, DeliveryMethod method)
    {
        _server?.Send(w, method);
    }

    public void RequestUseSkill(string skillId, Vector2 target, int targetEnemyId = -1, float charge = 0f)
    {
        PredictOwnCast(skillId, target);
        var w = Packets.Make(PacketType.UseSkill);
        w.Put(skillId);
        w.PutVec2(target);
        w.Put(targetEnemyId);
        w.Put(charge);
        Send(w, DeliveryMethod.ReliableOrdered);
    }

    // ------------------------------------------------------------------ cast prediction
    //
    // Attacks are server-authoritative, so without prediction a remote player's swing
    // only starts once the server's SkillEffect broadcast makes the ROUND TRIP back —
    // at 150 ping that's ~170ms of dead time after every click, while movement (client-
    // predicted) feels instant. These helpers give the CASTING client instant cosmetic
    // feedback: the swing/wind-up animation starts on click and projectile skills loose
    // a local ghost bolt immediately. The server still decides all damage, hits and
    // what everyone else sees; a rejected cast just shows a swing that does nothing.

    /// <summary>Recently predicted swings per skill: the server's own SkillEffect echo
    /// for these is suppressed so the animation doesn't play twice.</summary>
    private readonly Dictionary<string, long> _predictedSwings = new();
    private int _nextGhostId = -1;

    private void PredictOwnCast(string skillId, Vector2 target)
    {
        if (Status != ClientStatus.InGame) return;
        var me = World.Me;
        var def = _data.Skills.GetValueOrDefault(skillId);
        if (me == null || def == null || def.Archetype == Skills.SkillArchetype.Summon) return;
        if (World.MyCharacter?.GetSkill(skillId) == null) return;

        var aim = target - me.Position;
        var dir = aim.LengthSquared() > 0.001f ? Vector2.Normalize(aim) : me.Facing;

        // Swing / wind-up animation, mirroring the SkillEffect handler's rules.
        if (def.Archetype is Skills.SkillArchetype.MeleeStrike or Skills.SkillArchetype.MeleeSingle &&
            !def.RequiresShield)
        {
            me.SwingTotal = def.WindupTime > 0 ? def.WindupTime + 0.12f : ClientPlayer.SwingDuration;
            me.SwingTimeLeft = me.SwingTotal;
            me.SwingKind = (byte)(def.Tags?.Contains("Slam") == true ? 1 : 0);
            me.SwingDir = dir;
            _predictedSwings[skillId] = Environment.TickCount64;
        }

        // Ghost projectile: flies from the click; the authoritative spawn adopts its
        // progress on arrival. A short range cap makes rejected casts fizzle quietly.
        // Casts the server will obviously refuse (not enough mana) spawn NO ghost —
        // a bolt sailing through enemies with no server hit behind it reads as a bug.
        if (def.Archetype == Skills.SkillArchetype.Projectile)
        {
            if (def.ManaCost > 0 && me.Mana < def.ManaCost - 0.5f) return;
            var ghost = new ClientProjectile
            {
                Id = _nextGhostId--,
                FromPlayer = true,
                Ghost = true,
                SkillId = skillId,
                Position = me.Position + dir * 0.3f,
                Height = me.Height,
                Direction = dir,
                Speed = def.ProjectileSpeed,
                MaxRange = MathF.Min(def.Range, def.ProjectileSpeed * 0.8f),
            };
            World.Projectiles[ghost.Id] = ghost;
        }
    }

    /// <summary>True (and consumed) when this skill's swing was predicted moments ago —
    /// the server echo of our own cast must not restart the animation.</summary>
    private bool ConsumePredictedSwing(string skillId)
    {
        if (_predictedSwings.TryGetValue(skillId, out long at) &&
            Environment.TickCount64 - at < 900)
        {
            _predictedSwings.Remove(skillId);
            return true;
        }
        return false;
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

    public void RequestShopOpen(int npcId)
    {
        var w = Packets.Make(PacketType.ShopOpenRequest);
        w.Put(npcId);
        Send(w, DeliveryMethod.ReliableOrdered);
    }

    public void RequestShopBuy(int npcId, int slot)
    {
        var w = Packets.Make(PacketType.ShopBuyRequest);
        w.Put(npcId);
        w.Put(slot);
        Send(w, DeliveryMethod.ReliableOrdered);
    }

    public void RequestShopSell(Guid itemInstanceId)
    {
        var w = Packets.Make(PacketType.ShopSellRequest);
        w.PutGuid(itemInstanceId);
        Send(w, DeliveryMethod.ReliableOrdered);
    }

    public void RequestLevelSkill(string skillId)
    {
        var w = Packets.Make(PacketType.LevelSkillRequest);
        w.Put(skillId);
        Send(w, DeliveryMethod.ReliableOrdered);
    }

    public void RequestSummonAdjust(string skillId, int delta)
    {
        var w = Packets.Make(PacketType.SummonAdjustRequest);
        w.Put(skillId);
        w.Put(delta);
        Send(w, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>Rally ONE summon skill's pack (empty skillId = all of them).</summary>
    public void RequestSummonRally(string skillId, bool hasPoint, Vector2 point)
    {
        var w = Packets.Make(PacketType.SummonRallyRequest);
        w.Put(skillId ?? "");
        w.Put(hasPoint);
        w.PutVec2(point);
        Send(w, DeliveryMethod.ReliableOrdered);
    }

    public void RequestAllocatePassive(string nodeId)
    {
        var w = Packets.Make(PacketType.AllocatePassiveRequest);
        w.Put(nodeId);
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
                string zoneThemeId = r.GetString();
                var pos = r.GetVec2();
                float joinHeight = r.GetFloat();
                float hp = r.GetFloat();
                float maxHp = r.GetFloat();
                float mana = r.GetFloat();
                World.MyCharacter = Json.Load<CharacterData>(r.GetString());
                World.Map = new GameMap(mapSeed,
                    _data.ZoneThemes.FirstOrDefault(t => t.Id == zoneThemeId) ?? _data.ZoneThemes.FirstOrDefault());
                World.Players[World.MyPlayerId] = new ClientPlayer
                {
                    Id = World.MyPlayerId,
                    Name = PlayerName,
                    Position = pos,
                    NetTarget = pos,
                    Health = hp,
                    MaxHealth = maxHp,
                    Mana = mana,
                    IsLocal = true,
                    Height = joinHeight,
                    NetTargetHeight = joinHeight,
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
                p.Height = p.NetTargetHeight = r.GetFloat();
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
                    float height = r.GetFloat();
                    byte pFlags = r.GetByte();
                    if (World.Players.TryGetValue(id, out var p))
                    {
                        p.DebuffFlags = pFlags; // ailments apply to the local player too
                        if (id == World.MyPlayerId) continue; // position is predicted locally
                        p.NetTarget = pos;
                        p.Facing = facing;
                        p.NetTargetHeight = height;
                    }
                }
                break;
            }
            case PacketType.PlayerHealth:
            {
                int id = r.GetInt();
                float hp = r.GetFloat(), maxHp = r.GetFloat(), mana = r.GetFloat();
                float es = r.GetFloat(), maxEs = r.GetFloat();
                if (World.Players.TryGetValue(id, out var p))
                {
                    p.Health = hp;
                    p.MaxHealth = maxHp;
                    p.Mana = mana;
                    p.EnergyShield = es;
                    p.MaxEnergyShield = maxEs;
                }
                break;
            }

            case PacketType.EnemySlam:
            {
                var at = r.GetVec2();
                float radius = r.GetFloat();
                float height = r.GetFloat();
                World.AddEffect(at, radius, 0.45f, "slam", height);
                break;
            }
            case PacketType.EnemyAttack:
            {
                int id = r.GetInt();
                byte phase = r.GetByte();
                var dir = r.GetVec2();
                if (World.Enemies.TryGetValue(id, out var e))
                {
                    e.AttackAnimPhase = phase;
                    e.AttackDir = dir;
                    e.AttackAnimAtMs = Environment.TickCount64;
                    if (MathF.Abs(dir.X) > 0.05f) e.FacingLeft = dir.X < 0; // face the swing
                }
                break;
            }
            case PacketType.SummonAttack:
            {
                int id = r.GetInt();
                var dir = r.GetVec2();
                if (World.Summons.TryGetValue(id, out var su))
                {
                    su.AttackDir = dir;
                    su.AttackAnimAtMs = Environment.TickCount64;
                    if (MathF.Abs(dir.X) > 0.05f) su.FacingLeft = dir.X < 0; // face the shot/swing
                }
                break;
            }
            case PacketType.NpcInfo:
            {
                var npc = new ClientNpc { Id = r.GetInt(), TypeId = r.GetString() };
                npc.Position = r.GetVec2();
                npc.Height = r.GetFloat();
                npc.Name = _data.Npcs.GetValueOrDefault(npc.TypeId)?.Name ?? npc.TypeId;
                World.Npcs[npc.Id] = npc;
                break;
            }
            case PacketType.ShopStock:
            {
                int npcId = r.GetInt();
                int count = r.GetInt();
                var stock = new List<ClientShopEntry>(count);
                for (int i = 0; i < count; i++)
                    stock.Add(new ClientShopEntry
                    {
                        Slot = r.GetInt(),
                        Price = r.GetInt(),
                        Sold = r.GetBool(),
                        Item = Json.Load<ItemInstance>(r.GetString()),
                    });
                ShopStockReceived?.Invoke(npcId, stock);
                break;
            }
            case PacketType.SummonSpawn:
            {
                var summon = new ClientSummon { Id = r.GetInt(), OwnerId = r.GetInt(), SkillId = r.GetString() };
                summon.Position = r.GetVec2();
                summon.NetTarget = summon.Position;
                summon.Height = r.GetFloat();
                summon.NetTargetHeight = summon.Height;
                summon.MaxHealth = r.GetFloat();
                summon.Health = r.GetFloat();
                World.Summons[summon.Id] = summon;
                break;
            }
            case PacketType.SummonStates:
            {
                int count = r.GetInt();
                for (int i = 0; i < count; i++)
                {
                    int id = r.GetInt();
                    var pos = r.GetVec2();
                    float height = r.GetFloat();
                    float hp = r.GetFloat();
                    if (World.Summons.TryGetValue(id, out var summon))
                    {
                        summon.NetTarget = pos;
                        summon.NetTargetHeight = height;
                        summon.Health = hp;
                    }
                }
                break;
            }
            case PacketType.SummonDespawn:
            {
                World.Summons.Remove(r.GetInt());
                break;
            }
            case PacketType.WorldEffect:
            {
                string wfKind = r.GetString();
                var wfPos = r.GetVec2();
                float wfRadius = r.GetFloat();
                float wfDuration = r.GetFloat();
                float wfHeight = r.GetFloat();
                World.AddEffect(wfPos, wfRadius, wfDuration, wfKind, wfHeight);
                break;
            }
            case PacketType.ChainEffect:
            {
                r.GetString(); // skillId (unused for now — one chain visual)
                float chainHeight = r.GetFloat();
                int pointCount = r.GetByte();
                var points = new List<System.Numerics.Vector2>(pointCount);
                for (int i = 0; i < pointCount; i++) points.Add(r.GetVec2());
                World.Effects.Add(new ClientEffect
                {
                    Kind = "chain",
                    Height = chainHeight,
                    Points = points,
                    Position = points.Count > 0 ? points[0] : default,
                    TimeLeft = 0.45f,
                    Duration = 0.45f,
                });
                break;
            }

            case PacketType.PlayerPings:
            {
                int count = r.GetInt();
                for (int i = 0; i < count; i++)
                {
                    int id = r.GetInt();
                    int ping = r.GetShort();
                    if (World.Players.TryGetValue(id, out var p)) p.PingMs = ping;
                }
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
                float respawnHeight = r.GetFloat();
                float hp = r.GetFloat(), maxHp = r.GetFloat();
                float respawnMana = r.GetFloat();
                if (World.Players.TryGetValue(id, out var p))
                {
                    p.Alive = true;
                    p.Position = p.NetTarget = pos;
                    p.Height = p.NetTargetHeight = respawnHeight;
                    p.Health = hp;
                    p.MaxHealth = maxHp;
                    p.Mana = respawnMana;
                }
                break;
            }

            case PacketType.EnemySpawn:
            {
                var e = new ClientEnemy { Id = r.GetInt(), TypeId = r.GetString() };
                e.Position = e.NetTarget = r.GetVec2();
                e.Height = e.NetTargetHeight = r.GetFloat();
                e.Health = r.GetFloat();
                e.MaxHealth = r.GetFloat();
                e.EliteFlags = r.GetByte();
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
                    byte debuffs = r.GetByte();
                    float height = r.GetFloat();
                    if (World.Enemies.TryGetValue(id, out var e))
                    { e.NetTarget = pos; e.State = state; e.DebuffFlags = debuffs; e.NetTargetHeight = height; }
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
                float deathHeight = World.Enemies.TryGetValue(id, out var dying) ? dying.Height : 0f;
                World.Enemies.Remove(id);
                World.AddEffect(pos, 0.6f, 0.35f, "hit", deathHeight);
                break;
            }

            case PacketType.ProjectileSpawn:
            {
                var pr = new ClientProjectile { Id = r.GetInt(), FromPlayer = r.GetBool(), SkillId = r.GetString() };
                pr.Position = r.GetVec2();
                pr.Height = r.GetFloat();
                pr.Direction = r.GetVec2();
                pr.Speed = r.GetFloat();
                pr.MaxRange = r.GetFloat();
                pr.HeightStep = r.GetFloat();
                pr.SpriteOverride = r.GetString();
                if (pr.SpriteOverride.Length == 0) pr.SpriteOverride = null;
                // Adopt a matching ghost from our own cast prediction: keep the ghost's
                // flight progress so the bolt doesn't snap backwards on confirmation.
                if (pr.FromPlayer && World.Me is { } ghostOwner &&
                    Vector2.Distance(pr.Position, ghostOwner.Position) < 2.5f)
                {
                    var ghost = World.Projectiles.Values.FirstOrDefault(g =>
                        g.Ghost && g.SkillId == pr.SkillId);
                    if (ghost != null)
                    {
                        World.Projectiles.Remove(ghost.Id);
                        pr.Position += pr.Direction * ghost.Traveled;
                        pr.Height += pr.HeightStep * ghost.Traveled;
                        pr.Traveled = ghost.Traveled;
                    }
                }
                World.Projectiles[pr.Id] = pr;
                break;
            }
            case PacketType.ProjectileDespawn:
            {
                int id = r.GetInt();
                var at = r.GetVec2();
                float impactHeight = World.Projectiles.TryGetValue(id, out var gone) ? gone.Height : 0f;
                World.Projectiles.Remove(id);
                World.AddEffect(at, 0.4f, 0.2f, "hit", impactHeight);
                break;
            }

            case PacketType.WorldItemSpawn:
            {
                var drop = new ClientDrop { DropId = r.GetGuid(), Position = r.GetVec2() };
                drop.Height = r.GetFloat();
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
                float effectHeight = r.GetFloat();
                // phase 0 = instant cast, 1 = wind-up start (animation only),
                // 2 = wind-up landing (impact at the REAL point — the caster may have
                // moved during the wind-up, so this point supersedes the cast point).
                byte phase = r.GetByte();
                var def = _data.Skills.GetValueOrDefault(skillId);
                if (def != null && World.Players.ContainsKey(playerId))
                {
                    // Melee strikes play a real weapon-swing animation on the caster's
                    // held weapon (replacing the old abstract swipe arc). Shield skills
                    // don't swing — Shield Bash is a forward shove, not a swipe.
                    bool isSlam = def.Tags?.Contains("Slam") == true;
                    // Our own casts already played their swing at click time (prediction);
                    // the server echo must not restart the animation mid-motion.
                    bool ownPredicted = playerId == World.MyPlayerId && ConsumePredictedSwing(skillId);
                    if (phase != 2 && !ownPredicted &&
                        def.Archetype is Skills.SkillArchetype.MeleeStrike or Skills.SkillArchetype.MeleeSingle &&
                        !def.RequiresShield &&
                        World.Players.GetValueOrDefault(playerId) is { } swingCaster)
                    {
                        var swingDir = effectPoint - swingCaster.Position;
                        swingCaster.SwingTotal = phase == 1
                            ? def.WindupTime + 0.12f   // raise during the wind-up, land with the hit
                            : ClientPlayer.SwingDuration;
                        swingCaster.SwingTimeLeft = swingCaster.SwingTotal;
                        swingCaster.SwingKind = (byte)(isSlam ? 1 : 0);
                        swingCaster.SwingDir = swingDir.LengthSquared() > 0.001f
                            ? Vector2.Normalize(swingDir)
                            : swingCaster.Facing;
                    }

                    if (phase != 1) // impact visuals come with the landing, never the wind-up
                        switch (def.Archetype)
                        {
                            case Skills.SkillArchetype.MeleeStrike:
                                // Slam skills leave a ground impact that fades; plain
                                // strikes are just the weapon swing.
                                if (isSlam)
                                {
                                    World.AddEffect(effectPoint, MathF.Max(0.8f, def.Radius),
                                        0.45f, "impact", effectHeight);
                                    World.AddEffect(effectPoint, MathF.Max(0.8f, def.Radius),
                                        0.9f, "debris", effectHeight);
                                }
                                break;
                            // MeleeSingle: the weapon swing itself is the visual — no impact circle.
                            case Skills.SkillArchetype.MeleeArea:
                                World.AddEffect(effectPoint, def.Radius, 0.3f, "slam", effectHeight);
                                break;
                            case Skills.SkillArchetype.AreaBurst:
                                World.AddEffect(effectPoint, def.Radius, 0.3f, "burst", effectHeight);
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
                fn.Blocked = r.GetBool();
                if (fn.Blocked) World.BlockedEventsSeen++;
                // Anchor to the entity's current client-side position when we know it.
                if (fn.TargetIsPlayer && World.Players.TryGetValue(targetId, out var tp))
                { fn.Position = tp.Position; fn.Height = tp.Height; }
                else if (!fn.TargetIsPlayer && World.Enemies.TryGetValue(targetId, out var te))
                { fn.Position = te.Position; fn.Height = te.Height; }
                World.FloatingNumbers.Add(fn);
                break;
            }

            case PacketType.PlayerAppearance:
            {
                int playerId = r.GetInt();
                string weaponBaseId = r.GetString();
                string offHandBaseId = r.GetString();
                if (World.Players.TryGetValue(playerId, out var p))
                {
                    p.WeaponBaseId = string.IsNullOrEmpty(weaponBaseId) ? null : weaponBaseId;
                    p.OffHandBaseId = string.IsNullOrEmpty(offHandBaseId) ? null : offHandBaseId;
                }
                break;
            }

            case PacketType.ServerMessage:
                ServerMessageReceived?.Invoke(r.GetString());
                break;
        }
    }
}
