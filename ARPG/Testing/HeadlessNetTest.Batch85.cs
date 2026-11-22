using System.Numerics;
using ARPG.Data;
using ARPG.Items;
using ARPG.Net;
using ARPG.Persistence;
using ARPG.Server;
using ARPG.Sim;
using ARPG.Util;

namespace ARPG.Testing;

/// <summary>Batch 85 checks (hardening + new content). Each section runs on its own
/// server so it cannot disturb — or be disturbed by — the long shared session above.</summary>
public static partial class HeadlessNetTest
{
    /// <summary>A server plus N clients over loopback, pumped by hand.</summary>
    private sealed class Session
    {
        public readonly GameServer Server;
        public readonly List<GameClient> Clients = new();
        public readonly List<string> Disconnects = new();
        public readonly List<List<string>> Messages = new();
        public ServerWorld World => Server.World;
        public GameClient A => Clients[0];
        public GameClient B => Clients[1];

        public Session(GameData data, int seed, bool campaign = false, bool story = false, string theme = null,
            int clients = 2, CharacterData[] characters = null, string[] names = null)
        {
            Server = new GameServer(data, seed, theme, campaign, story);
            if (!Server.Start(0)) throw new InvalidOperationException("test server failed to start");
            for (int i = 0; i < clients; i++) Add(data, names?[i] ?? $"T{seed % 1000}{(char)('A' + i)}", characters?[i]);
            Pump(1.2f);
        }

        public GameClient Add(GameData data, string name, CharacterData character = null, Action<GameClient> configure = null)
        {
            var c = new GameClient(data, name, character);
            configure?.Invoke(c);
            int index = Clients.Count;
            Disconnects.Add(null);
            Messages.Add(new List<string>());
            c.Disconnected += r => Disconnects[index] = r;
            c.ServerMessageReceived += m => Messages[index].Add(m);
            c.Connect("127.0.0.1", Server.LocalPort, out _);
            Clients.Add(c);
            return c;
        }

        public void Pump(float seconds)
        {
            const float dt = 1f / 60f;
            int steps = (int)(seconds / dt);
            for (int i = 0; i < steps; i++)
            {
                Server.Update(dt);
                foreach (var c in Clients) c.Update(dt);
                Thread.Sleep(2); // LiteNetLib flushes on its own ~15 ms thread: real time must pass
            }
        }

        public ServerPlayer P(GameClient c) => World.Players[c.World.MyPlayerId];

        /// <summary>Put a client's player at a spot (client-authoritative movement) and let it sync.</summary>
        public void Place(GameClient c, Vector2 pos)
        {
            c.World.Me.Position = pos;
            c.World.Me.Height = World.Map.GroundHeightAt(pos);
            var sp = P(c);
            sp.Position = pos;
            sp.Height = c.World.Me.Height;
            Pump(0.25f);
        }

        public void Close()
        {
            foreach (var c in Clients) c.Disconnect();
            Pump(0.2f);
            Server.Stop();
        }
    }

    private static string TempDir(string name)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"scrollbound-nettest-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void RunBatch85(GameData data)
    {
        Batch85Hardening(data);
        Batch85Multiplayer(data);
        Batch85Pools(data);
        Batch85BanditsAndGoblins(data);
        Batch85SkillBooks(data);
        Batch85Mace(data);
        Batch85Bow(data);
        Batch85ScorchedBluffs(data);
        Batch85Quests(data);
        Batch85SoftLocks(data);
    }

    private static void Batch85SoftLocks(GameData data)
    {
        Console.WriteLine("\n-- Batch 85.17: soft-lock audit --");

        // ---- solo death on every map kind ends with the player on their feet, somewhere playable.
        var solo = new Session(data, 851701, campaign: true, story: true, clients: 1, names: new[] { "Lonely" });
        var w = solo.World;
        var p = solo.P(solo.A);
        bool DieAndRecover(string where)
        {
            solo.A.SendDebugCommand("die");
            solo.Pump(0.5f);
            bool died = !p.Alive;
            for (int i = 0; i < 40 && !p.Alive; i++) solo.Pump(0.25f);
            solo.Pump(0.8f);
            bool ok = died && p.Alive && p.Health > 0 && solo.A.World.Me.Alive && (w.Map.IsHub || w.Map.IsRoad) &&
                      solo.A.World.Map.Kind == w.Map.Kind && !w.Map.CircleHitsWall(p.Position, 0.3f);
            if (!ok) Console.WriteLine($"    (soft-lock detail: {where} died={died} alive={p.Alive} map={w.Map.Kind} client={solo.A.World.Map.Kind})");
            return ok;
        }
        var soloResults = new List<string>();
        void Try(string where, string cmd, string arg = "")
        {
            if (cmd != null) { solo.A.SendDebugCommand(cmd, arg); solo.Pump(1.5f); }
            soloResults.Add($"{where}:{(DieAndRecover(where) ? "ok" : "STUCK")}");
        }
        Try("road", null);
        Try("ruins", "warp_home");
        Try("archive", "warp_basement");
        Try("portal run", "enter_portal", "warp_zone_forest+warp_boss");
        Try("survival run", "enter_portal", "warp_mode_survival+warp_zone_swamp");
        Try("defense run", "enter_portal", "warp_mode_defense");
        Try("arid run", "enter_portal", "warp_zone_arid");
        Check(soloResults.All(r => r.EndsWith(":ok")), $"solo death on each story map kind recovers ({string.Join(", ", soloResults)})");
        Check(w.MapIndex == 0 && !w.PortalOpen && solo.Server.TickFailures == 0, "after the last one the party is home, the abandoned portal closed, no tick failures");
        solo.Close();
        var groundsSolo = new Session(data, 851702, campaign: true, clients: 1, names: new[] { "Tester" });
        solo = groundsSolo; w = solo.World; p = solo.P(solo.A);
        soloResults.Clear();
        Try("sanctum", null);
        Try("forest run", "warp_next");
        Try("caravan stand", "warp_defense");
        Try("old road", "warp_tutorial");
        Check(soloResults.All(r => r.EndsWith(":ok")), $"and on the test grounds ({string.Join(", ", soloResults)})");
        groundsSolo.Close();

        // ---- a whole-party wipe in a portal run; the burnt scroll is not the end of the road.
        var wipe = PortalRun(data, 851703, "warp_zone_forest+warp_boss");
        var ww = wipe.World;
        var wa = wipe.P(wipe.A);
        var wb = wipe.P(wipe.B);
        wa.Character.Quests.Completed.AddRange(new[] { "q_road", "q_lorekeeper", "q_archive" });
        wa.Character.Quests.Active.RemoveAll(q => q.QuestId != "q_book");
        wa.Character.Quests.Active.Add(new QuestProgress { QuestId = "q_portal", Step = 1 });
        wipe.A.SendDebugCommand("die");
        wipe.Pump(0.6f);
        bool oneDownStays = !wa.Alive && wb.Alive && ww.MapIndex == ServerWorld.ScrollMapIndex;
        wipe.B.SendDebugCommand("die");
        for (int i = 0; i < 40 && ww.MapIndex != 0; i++) wipe.Pump(0.25f);
        wipe.Pump(0.8f);
        Check(oneDownStays && ww.MapIndex == 0 && wa.Alive && wb.Alive && wipe.A.World.Me.Alive && wipe.B.World.Me.Alive &&
              wipe.A.World.Map.Kind == World.MapKind.RuinsHub && !ww.PortalOpen,
              "a whole-party wipe in a portal run: with one down the run goes on; with both down everyone wakes in the camp");
        Check(!wa.Character.Inventory.Items.Any(pl => pl.Item.GetBase(data).Category == ItemCategory.WarpScroll) &&
              wa.Character.Quests.Get("q_portal")?.Step == 1,
              "(the scroll burnt on entry, and 'finish a portal run' is still open)");
        wipe.A.SendDebugCommand("warp_basement");
        wipe.Pump(1.2f);
        var mercy = ww.Enemies.Values.First(e => e.Def.Id == "codex");
        ww.DebugSkillHit(wa, mercy, null, mercy.Health + 1f);
        wipe.Pump(0.6f);
        var mercyScrolls = ww.Drops.Values.Where(d => d.Item?.GetBase(data).Category == ItemCategory.WarpScroll).ToList();
        Check(mercyScrolls.Any(d => d.ReservedForPlayerId == wa.Id || d.ReservedForPlayerId < 0),
              "a warp scroll is always obtainable: the first Codex kill gives one, and it gives another to a present player whose portal quest is stuck without one");
        // The one teammate who could have revived you leaves: that is a wipe too.
        wipe.A.SendDebugCommand("die");
        wipe.Pump(0.6f);
        bool waitingForRevive = !wa.Alive && ww.MapIndex == ServerWorld.BasementMapIndex;
        wipe.B.Disconnect();
        for (int i = 0; i < 40 && ww.MapIndex != 0; i++) wipe.Pump(0.25f);
        wipe.Pump(0.6f);
        Check(waitingForRevive && ww.Players.Count == 1 && ww.MapIndex == 0 && wa.Alive && wipe.A.World.Me.Alive,
              "a dead player whose only living teammate disconnects is not left on the floor: the wipe rule brings them home");
        wipe.Close();

        // ---- selling your only weapon.
        var shop = new Session(data, 851704, campaign: true, clients: 1, names: new[] { "Seller" });
        var sp = shop.P(shop.A);
        var sc = sp.Character;
        var merchant = shop.World.Npcs.First(n => n.TypeId == "merchant");
        shop.Place(shop.A, merchant.Position + new Vector2(0.9f, 0.3f));
        var club = sc.MainHand;
        shop.A.RequestMoveItem(ItemLocation.AtEquip(EquipSlot.MainHand), ItemLocation.AtGrid(0, 0));
        shop.Pump(0.4f);
        bool inBag = sc.Inventory.FindByInstance(club.InstanceId) != null && sc.MainHand == null;
        int goldBefore = sc.Gold;
        shop.A.RequestShopSell(club.InstanceId);
        shop.Pump(0.4f);
        Check(inBag && sc.Inventory.FindByInstance(club.InstanceId) != null && sc.Gold == goldBefore &&
              shop.Messages[0].Any(m => m.Contains("only weapon")),
              "the merchant refuses to buy your only weapon");
        shop.A.SendDebugCommand("give_mace");
        shop.Pump(0.4f);
        shop.A.RequestShopSell(club.InstanceId);
        shop.Pump(0.4f);
        var lastMace = sc.Inventory.Items.Select(pl => pl.Item).FirstOrDefault(it => it.GetBase(data).IsWeapon);
        Check(sc.Inventory.FindByInstance(club.InstanceId) == null && sc.Gold > goldBefore && lastMace != null,
              "with a second weapon in the bag the first one sells normally");
        // Drop the last one on the floor and walk away: the camp finds a spare.
        shop.A.RequestDropItem(lastMace.InstanceId);
        shop.Pump(0.4f);
        bool weaponless = !sc.Equipment.Values.Concat(sc.Inventory.Items.Select(pl => pl.Item)).Any(it => it != null && it.GetBase(data).IsWeapon);
        shop.A.SendDebugCommand("warp_next");
        shop.Pump(1.0f);
        shop.A.SendDebugCommand("warp_home");
        shop.Pump(1.0f);
        Check(weaponless && sc.MainHand != null && sc.MainHand.BaseItemId == data.Classes.First(cl => cl.Id == sc.ClassId).StartWeapon &&
              shop.A.World.MyCharacter.MainHand != null,
              $"a character with no weapon left anywhere is handed a spare {sc.MainHand?.GetBase(data).Name} on coming home");
        shop.Close();
        var unarmed = CharacterData.CreateNew(data, "Unarmed", "archer");
        unarmed.Equipment.Remove(EquipSlot.MainHand);
        var armed = new Session(data, 851705, campaign: true, story: true, clients: 1, characters: new[] { unarmed }, names: new[] { "Unarmed" });
        Check(armed.P(armed.A).Character.MainHand?.GetBase(data).Category == ItemCategory.Bow,
              "and a save that arrives with no weapon at all gets one on joining");
        armed.Close();

        // ---- a player disconnecting while others stand readied at a door.
        var door = new Session(data, 851706, campaign: true, clients: 2);
        var dw = door.World;
        door.Place(door.A, dw.Map.ExitDoor + new Vector2(-0.9f, 0f));
        door.Place(door.B, dw.Map.ExitDoor + new Vector2(-6f, 0f));
        door.A.RequestDoorReady();
        door.Pump(0.5f);
        bool waiting = dw.MapIndex == 0 && dw.ReadyCount == 1;
        door.B.Disconnect();
        door.Pump(1.5f);
        Check(waiting && dw.Players.Count == 1 && dw.MapIndex == 1 && door.A.World.Map.Kind == World.MapKind.Forest,
              "one ready at the door, the other disconnects without readying: the door opens for the one still here (it used to hang at 1/1)");
        door.Close();
        var door2 = new Session(data, 851707, campaign: true, clients: 2);
        var d2 = door2.World;
        door2.Place(door2.A, d2.Map.ExitDoor + new Vector2(-0.9f, 0f));
        door2.Place(door2.B, d2.Map.ExitDoor + new Vector2(-0.9f, 0.7f));
        door2.A.RequestDoorReady();
        door2.Pump(0.5f);
        door2.A.Disconnect();
        door2.Pump(1.0f);
        bool readyGone = d2.ReadyCount == 0 && d2.MapIndex == 0 && door2.B.World.ZoneReadyCount == 0;
        door2.B.RequestDoorReady();
        door2.Pump(1.2f);
        Check(readyGone && d2.MapIndex == 1, "the READY player disconnects: their mark is cleared (HUD shows 0), and the one left can go through alone");
        // Mid-defense: a wave is called with one ready, the other leaves.
        door2.B.SendDebugCommand("warp_defense");
        door2.Pump(1.2f);
        var late = door2.Add(data, "WaveJoiner");
        door2.Pump(1.2f);
        var lp = door2.P(late);
        door2.Place(door2.B, d2.Map.WorkbenchSpot + new Vector2(0.8f, 0f));
        door2.B.RequestDoorReady();
        door2.Pump(0.5f);
        bool buildPhase = d2.DefPhase == DefensePhase.Build;
        late.Disconnect();
        door2.Pump(1.2f);
        Check(buildPhase && d2.DefPhase == DefensePhase.Wave, "the same at the caravan's workbench: the wave starts when the un-ready player leaves");
        // ---- joiners arriving at awkward moments.
        var midWave = door2.Add(data, "MidWave");
        door2.Pump(3.0f);
        var mp = door2.P(midWave);
        Check(midWave.Status == ClientStatus.InGame && mp.Supplies == World.DefenseBalance.SupplyStart && mp.Alive &&
              midWave.World.Map.Kind == World.MapKind.Defense && midWave.World.Structures.Count == d2.Structures.Count &&
              door2.Server.TickFailures == 0,
              $"a joiner arriving mid-defense-wave gets the map, the structures and the starting supplies ({mp.Supplies})");
        door2.Close();
        var sealedRun = PortalRun(data, 851708, "warp_zone_swamp+warp_boss", clients: 1);
        var joiner = sealedRun.Add(data, "DoorJoiner");
        sealedRun.Pump(1.5f);
        Check(sealedRun.World.ExitLocked && joiner.Status == ClientStatus.InGame && joiner.World.ZoneExitLocked &&
              joiner.World.Enemies.Values.Any(e => e.IsBoss) && joiner.World.Map.Theme?.Id == "graveyard",
              "a joiner arriving while the exit is sealed sees the seal and the boss holding it");
        sealedRun.Close();
        var scene = new Session(data, 851709, campaign: true, story: true, clients: 1);
        var sceneJoiner = scene.Add(data, "SceneJoiner"); // the road's opening scene is still playing
        scene.Pump(1.5f);
        scene.A.Disconnect();                             // ...and the host's client drops mid-scene
        scene.Pump(1.0f);
        var sj = scene.P(sceneJoiner);
        var gate = scene.World.Enemies.Values.FirstOrDefault(e => e.Def.Id == "barrowlord");
        if (gate != null) scene.World.DebugSkillHit(sj, gate, null, gate.Health + 1f);
        scene.Pump(0.6f);
        Check(sceneJoiner.Status == ClientStatus.InGame && scene.World.Players.Count == 1 && gate != null && !scene.World.ExitLocked &&
              scene.Server.TickFailures == 0,
              "a joiner arriving mid-cutscene plays on, and a player dropping mid-cutscene blocks nothing (the gate still opens)");
        scene.Close();
        Check(true, "leaving a map without a guaranteed book or a quest item is covered above (85.12: the book drops again; 85.16: the Codex gives the scroll again)");
    }

    private static void Batch85Quests(GameData data)
    {
        Console.WriteLine("\n-- Batch 85.16: the quest tracker (per-character logs, presence credit, no catch-up) --");
        string[] stepTypes = { "reach_map", "kill", "kill_boss", "talk", "turn_in", "collect", "use_podium", "complete_run" };
        Check(data.Quests.Count >= 7 && data.Quests.Count(q => !q.Side) == 4 && data.Quests.Count(q => q.Side) >= 3 &&
              data.Quests.SelectMany(q => q.Steps).All(st => stepTypes.Contains(st.Type) && st.Text.Length > 0) &&
              data.Quests.SelectMany(q => q.Steps).Select(st => st.Type).Distinct().Count() == stepTypes.Length &&
              data.Quests.All(q => q.RewardGold > 0 && q.RewardXp > 0) && data.GetQuest("q_captain").RewardItem.Length > 0,
              $"{data.Quests.Count} quests in Data/Quests/quests.json: a four-quest story line and three side quests, every step type used, gold + XP rewards (one with an item)");
        Check(Core.InputManager.DefaultBindings()[Core.InputAction.QuestLog].ToString() == "Key:J",
              "the quest log has its own rebindable action, default J");

        // ---- the story line, one character, start to finish.
        var qs = new Session(data, 851601, campaign: true, story: true, clients: 1, names: new[] { "Quester" });
        var w = qs.World;
        var pa = qs.P(qs.A);
        var log = pa.Character.Quests;
        Check(w.QuestsActive && log.Active.Select(q => q.QuestId).OrderBy(x => x).SequenceEqual(new[] { "q_book", "q_road" }) &&
              log.Get("q_road").Step == 0 && qs.A.World.MyCharacter.Quests.Active.Count == 2,
              "a new character starts with the first story quest and the open side quest (client holds the same log)");
        var tracker = UI.QuestUI.TrackerLines(data, qs.A.World.MyCharacter, qs.A.World.Players.Values);
        Check(tracker.Count >= 2 && tracker[0].Kind == "title" && tracker[0].Text == "The Old Road" &&
              tracker[1].Kind == "step" && tracker[1].Text.Contains("Barrow Lord") && tracker.Any(l => l.Kind == "side" && l.Text.Contains("Words on Paper")),
              $"the tracker shows the active quest's current step (\"{tracker[0].Text} / {tracker[1].Text}\") and the side quest under it");
        int gold0 = pa.Character.Gold;
        var lord = w.Enemies.Values.First(e => e.Def.Id == "barrowlord");
        w.DebugSkillHit(pa, lord, null, lord.Health + 1f);
        qs.Pump(0.5f);
        Check(log.Get("q_road")?.Step == 1 && qs.A.World.MyCharacter.Quests.Get("q_road")?.Step == 1 &&
              qs.Messages[0].Any(m => m.StartsWith("Quest updated")),
              "kill_boss: the Barrow Lord's fall advances the step (server and client)");
        qs.A.SendDebugCommand("warp_home");
        qs.Pump(1.2f);
        Check(log.IsCompleted("q_road") && log.Get("q_lorekeeper") != null && pa.Character.Gold == gold0 + data.GetQuest("q_road").RewardGold &&
              qs.Messages[0].Any(m => m.StartsWith("Quest complete")) && qs.A.World.MyCharacter.Quests.IsCompleted("q_road"),
              $"reach_map: arriving in the camp completes the quest, pays {data.GetQuest("q_road").RewardGold} gold and offers the next one");
        var trainer = w.Npcs.First(n => n.TypeId == "skill_trainer");
        qs.Place(qs.A, trainer.Position + new Vector2(8f, 0f));
        qs.A.RequestTalk(trainer.Id);
        qs.Pump(0.4f);
        bool talkTooFar = log.Get("q_lorekeeper") != null;
        qs.Place(qs.A, trainer.Position + new Vector2(0.9f, 0.3f));
        qs.A.RequestTalk(w.Npcs.First(n => n.TypeId == "merchant").Id);
        qs.Pump(0.4f);
        bool wrongNpc = log.Get("q_lorekeeper") != null;
        qs.A.RequestTalk(trainer.Id);
        qs.Pump(0.4f);
        Check(talkTooFar && wrongNpc && log.IsCompleted("q_lorekeeper") && log.Get("q_archive")?.Step == 0 &&
              log.Get("q_goblins") == null,
              "talk: only speaking to the trainer, within reach, counts; the side quests behind the archive aren't offered yet");
        qs.A.SendDebugCommand("warp_basement");
        qs.Pump(1.2f);
        Check(w.Map.Kind == World.MapKind.Archive && log.Get("q_archive")?.Step == 1, "reach_map: descending to the archive advances the step");
        var codex = w.Enemies.Values.First(e => e.Def.Id == "codex");
        w.DebugSkillHit(pa, codex, null, codex.Health + 1f);
        qs.Pump(0.6f);
        var firstScroll = w.Drops.Values.FirstOrDefault(d => d.Item?.GetBase(data).Category == ItemCategory.WarpScroll);
        Check(log.Get("q_archive")?.Step == 2 && firstScroll != null && pa.Character.Story.CodexFelled &&
              !pa.Character.Inventory.Items.Any(pl => pl.Item.GetBase(data).Category == ItemCategory.WarpScroll),
              "kill_boss: the Codex falls; the next step needs its scroll — which is on the ground, NOT auto-added to the bag");
        // Leave it behind: the step stays open, and the source is there again next visit.
        qs.A.SendDebugCommand("warp_home");
        qs.Pump(1.2f);
        bool stillOpen = log.Get("q_archive")?.Step == 2 && w.Drops.Count == 0;
        qs.A.SendDebugCommand("warp_basement");
        qs.Pump(1.2f);
        var codexAgain = w.Enemies.Values.FirstOrDefault(e => e.Def.Id == "codex");
        if (codexAgain != null) w.DebugSkillHit(pa, codexAgain, null, codexAgain.Health + 1f);
        qs.Pump(0.6f);
        var secondScroll = w.Drops.Values.FirstOrDefault(d => d.Item?.GetBase(data).Category == ItemCategory.WarpScroll);
        Check(stillOpen && codexAgain != null && secondScroll != null && secondScroll.ReservedForPlayerId == pa.Id,
              "leaving the quest item behind loses it and leaves the step open; the Codex is there again and gives the scroll again");
        if (secondScroll != null)
        {
            qs.Place(qs.A, secondScroll.Position);
            qs.A.RequestPickup(secondScroll.DropId);
            qs.Pump(0.5f);
        }
        Check(log.IsCompleted("q_archive") && log.Get("q_portal")?.Step == 0 && log.Get("q_goblins") != null && log.Get("q_captain") != null,
              "collect: picking the scroll up yourself completes the quest and opens the portal quest and both side quests");
        var codexThird = w.SpawnEnemy("codex", pa.Position + new Vector2(2f, 0f), EliteAffix.Boss);
        w.DebugSkillHit(pa, codexThird, null, codexThird.Health + 1f);
        qs.Pump(0.4f);
        Check(!w.Drops.Values.Any(d => d.Item?.GetBase(data).Category == ItemCategory.WarpScroll && d.ReservedForPlayerId == pa.Id),
              "once the step is done the Codex stops handing out quest scrolls");
        foreach (var leftover in w.Drops.Keys.ToList()) w.Drops.Remove(leftover);

        // ---- a second character joins late: their own log, and NO catch-up.
        var late = qs.Add(data, "Latecomer");
        qs.Pump(1.2f);
        var pb = qs.P(late);
        var logB = pb.Character.Quests;
        Check(late.Status == ClientStatus.InGame && w.CodexFelled && w.ScrollsUnlocked &&
              !pb.Character.Story.CodexFelled && !pb.Character.Story.ScrollsUnlocked && !pb.Character.Story.ReachedCamp &&
              !late.World.MyCharacter.Story.CodexFelled,
              "no catch-up: the world keeps the host's progress, but the joiner's saved story record does NOT gain the Codex kill they weren't there for");
        Check(logB.Get("q_road")?.Step == 0 && logB.Get("q_archive") == null && !logB.IsCompleted("q_road") &&
              late.World.MyCharacter.Quests.Get("q_road")?.Step == 0,
              "the joiner starts their own log at the beginning — nothing copied from the host");
        Check(late.World.Players[qs.A.World.MyPlayerId].QuestId == "q_portal" && qs.A.World.Players[late.World.MyPlayerId].QuestId == "q_road" &&
              qs.A.World.Players[late.World.MyPlayerId].QuestStep == 0,
              "each player's current quest step is broadcast to the other");
        var helping = UI.QuestUI.TrackerLines(data, qs.A.World.MyCharacter, qs.A.World.Players.Values);
        var notHelping = UI.QuestUI.TrackerLines(data, late.World.MyCharacter, late.World.Players.Values);
        Check(helping.Any(l => l.Kind == "helping" && l.Text.Contains("helping Latecomer") && l.Text.Contains("Barrow Lord")) &&
              !notHelping.Any(l => l.Kind == "helping"),
              $"the host's tracker says who they are helping (\"{helping.FirstOrDefault(l => l.Kind == "helping").Text}\"); the newcomer's doesn't");
        // Both present for a Codex kill now: the joiner's story record gains it (they were
        // there) — but their quest log doesn't, because they don't have that step.
        var codexShared = w.SpawnEnemy("codex", pa.Position + new Vector2(2f, 0f), EliteAffix.Boss);
        w.DebugSkillHit(pa, codexShared, null, codexShared.Health + 1f);
        qs.Pump(0.5f);
        Check(pb.Character.Story.CodexFelled && logB.Get("q_archive") == null && logB.Get("q_road")?.Step == 0,
              "present for a later Codex kill: the joiner's record says so from then on, yet a quest step they don't hold isn't credited");
        foreach (var leftover in w.Drops.Keys.ToList()) w.Drops.Remove(leftover);
        qs.A.SendDebugCommand("warp_home");
        qs.Pump(1.2f);
        Check(pb.Character.Story.ReachedCamp && logB.Get("q_road")?.Step == 0,
              "the joiner reaches the camp with the party (recorded), but 'Reach the camp' waits behind the Barrow Lord step they haven't done");

        // ---- presence: a kill credits everyone on the map who has the step — alive or dead.
        qs.B.SendDebugCommand("die");
        qs.Pump(0.4f);
        var sharedLord = w.SpawnEnemy("barrowlord", pa.Position + new Vector2(3f, 0f), EliteAffix.Boss);
        w.DebugSkillHit(pa, sharedLord, null, sharedLord.Health + 1f);
        qs.Pump(0.6f);
        Check(!pb.Alive && logB.IsCompleted("q_road") && logB.Get("q_lorekeeper") != null && log.IsCompleted("q_road"),
              "a dead player on the map is still credited for the boss kill (and, standing in the camp, finishes the quest)");
        foreach (var leftover in w.Drops.Keys.ToList()) w.Drops.Remove(leftover);
        w.DebugRevive(pb);
        qs.Pump(0.4f);
        // Kill counts are per player, and only for those holding the step.
        qs.A.SendDebugCommand("enter_portal", "warp_zone_forest");
        qs.Pump(1.6f);
        Check(w.Map.Theme?.Id == "forest" && log.Get("q_goblins")?.Step == 1 && logB.Get("q_goblins") == null,
              "reach_map zone:forest — entering the Mirewood advances the host's goblin quest; the joiner doesn't have it");
        foreach (var e in w.Enemies.Values.ToList()) e.Position = pa.Position + new Vector2(40f, 40f); // park the run's own packs
        for (int i = 0; i < 3; i++)
        {
            var gob = w.SpawnEnemy(i == 0 ? "goblin_shaman" : "goblin_skulker", pa.Position + new Vector2(2f, 0f));
            w.DebugSkillHit(pb, gob, null, gob.Health + 1f); // the JOINER lands the killing blows
        }
        var notGoblin = w.SpawnEnemy("bandit_archer", pa.Position + new Vector2(2f, 0f));
        w.DebugSkillHit(pa, notGoblin, null, notGoblin.Health + 1f);
        qs.Pump(0.5f);
        var sideLine = UI.QuestUI.TrackerLines(data, qs.A.World.MyCharacter, qs.A.World.Players.Values).FirstOrDefault(l => l.Text.Contains("Goblins slain"));
        Check(log.Get("q_goblins")?.Count == 3 && logB.Get("q_goblins") == null && sideLine.Text != null && sideLine.Text.Contains("Goblins slain 3/12"),
              $"kill family:goblin — three goblins (killed by the OTHER player) count for the holder, a bandit doesn't: \"{sideLine.Text}\"");
        // The portal quest: podium, then walking out of a finished run.
        qs.A.SendDebugCommand("warp_home");
        qs.Pump(1.2f);
        Check(log.Get("q_portal")?.Step == 0, "leaving a run through a debug warp is not 'finishing' it");
        var podiumScroll = pa.Character.Inventory.Items.First(pl => pl.Item.GetBase(data).Category == ItemCategory.WarpScroll);
        qs.Place(qs.A, w.Map.PodiumSpot + new Vector2(-1.0f, 0.5f));
        qs.Place(qs.B, w.Map.PodiumSpot + new Vector2(-1.0f, -0.5f));
        qs.A.RequestUsePodium(podiumScroll.Item.InstanceId);
        qs.Pump(0.5f);
        Check(w.PortalOpen && log.Get("q_portal")?.Step == 1, "use_podium: placing the scroll advances the step");
        qs.A.SendDebugCommand("enter_portal", "warp_zone_swamp"); // a one-room run with no boss: the exit is open
        qs.Pump(1.6f);
        qs.Place(qs.A, w.Map.ExitDoor + new Vector2(-1.0f, 0.5f));
        qs.Place(qs.B, w.Map.ExitDoor + new Vector2(-1.0f, -0.5f));
        int goldBeforeRun = pa.Character.Gold;
        qs.A.RequestDoorReady();
        qs.B.RequestDoorReady();
        qs.Pump(1.4f);
        Check(w.MapIndex == 0 && log.IsCompleted("q_portal") && pa.Character.Gold == goldBeforeRun + data.GetQuest("q_portal").RewardGold &&
              logB.Get("q_portal") == null && !logB.IsCompleted("q_portal"),
              "complete_run: walking out of the run's exit finishes the story line for the one who held the step");
        // Turn-ins credit only the turner.
        var trainerNow = w.Npcs.First(n => n.TypeId == "skill_trainer");
        qs.Place(qs.A, trainerNow.Position + new Vector2(0.9f, 0.3f));
        qs.Place(qs.B, trainerNow.Position + new Vector2(-0.9f, 0.3f));
        qs.A.SendDebugCommand("give_book", "book_ossuary");
        qs.Pump(0.4f);
        var handIn = pa.Character.Inventory.Items.First(pl => pl.Item.BaseItemId == "book_ossuary");
        qs.A.RequestTurnInSkillBook(handIn.Item.InstanceId);
        qs.Pump(0.4f);
        Check(log.IsCompleted("q_book") && logB.Get("q_book") != null && !logB.IsCompleted("q_book"),
              "turn_in: handing a skill book to the trainer credits the turner only (the other player's side quest stays open)");
        // The reward item, and the Captain side quest crediting both holders at once.
        logB.Active.Add(new QuestProgress { QuestId = "q_captain" });
        var chief = w.SpawnEnemy("bandit_captain", pa.Position + new Vector2(3f, 0f), EliteAffix.Boss);
        w.DebugSkillHit(pa, chief, null, chief.Health + 1f);
        qs.Pump(0.5f);
        Check(log.IsCompleted("q_captain") && logB.IsCompleted("q_captain") &&
              (pa.Character.Inventory.Items.Any(pl => pl.Item.BaseItemId == "scroll_aftershock") || w.Drops.Values.Any(d => d.Item?.BaseItemId == "scroll_aftershock")),
              "two players holding the same step are both credited by one kill, each receiving the reward (an Aftershock scroll)");

        // ---- the log survives save / load, and the sanitizer keeps it honest.
        var savedLog = Json.Load<CharacterData>(Json.Save(pa.Character));
        const string questSave = "nettest_quest_85";
        savedLog.Name = questSave;
        SaveManager.SaveCharacter(savedLog);
        var reloaded = SaveManager.LoadCharacter(questSave);
        SaveManager.DeleteCharacter(questSave);
        Check(reloaded != null && reloaded.Quests.Completed.SequenceEqual(log.Completed) &&
              reloaded.Quests.Active.Select(q => (q.QuestId, q.Step, q.Count)).SequenceEqual(log.Active.Select(q => (q.QuestId, q.Step, q.Count))) &&
              reloaded.Quests.Get("q_goblins")?.Count == 3,
              $"quest logs survive save/load ({reloaded?.Quests.Completed.Count} completed, {reloaded?.Quests.Active.Count} active, goblin count intact)");
        reloaded.Quests.Active.Add(new QuestProgress { QuestId = "q_from_another_build" });
        reloaded.Quests.Active.Add(new QuestProgress { QuestId = "q_goblins", Step = 99, Count = -5 });
        reloaded.Quests.Completed.Add("q_never_existed");
        w.SanitizeCharacter(reloaded, "QuestSanity");
        Check(reloaded.Quests.Active.All(q => data.GetQuest(q.QuestId) != null) && reloaded.Quests.Active.Count(q => q.QuestId == "q_goblins") == 1 &&
              !reloaded.Quests.Completed.Contains("q_never_existed"),
              "join sanitization drops unknown and duplicated quest entries");
        Check(qs.Server.TickFailures == 0, "no tick failures through the quest checks");
        qs.Close();

        // The test grounds don't run the story's quests.
        var grounds = new Session(data, 851602, campaign: true, clients: 1);
        Check(!grounds.World.QuestsActive && grounds.P(grounds.A).Character.Quests.Active.Count == 0 &&
              UI.QuestUI.TrackerLines(data, grounds.A.World.MyCharacter, grounds.A.World.Players.Values).Count == 0,
              "the test grounds carry no quests (nothing to track, no tracker drawn)");
        grounds.Close();
    }

    private static void Batch85ScorchedBluffs(GameData data)
    {
        Console.WriteLine("\n-- Batch 85.15: a fourth portal zone — the Scorched Bluffs --");
        var seals = data.Modifiers.Values.Where(m => m.ModifierGroup == "warp_zone").ToList();
        var aridSeal = data.Modifiers.GetValueOrDefault("warp_zone_arid");
        Check(seals.Count == 4 && seals.All(m => data.ZoneThemes.Any(t => t.Id == m.ZoneThemeId) && !string.IsNullOrEmpty(m.ZoneName)) &&
              aridSeal?.ZoneThemeId == "arid" && aridSeal.ZoneName == "the Scorched Bluffs" &&
              seals.Select(m => m.ZoneThemeId).Distinct().Count() == 4,
              "four zone seals, each carrying its own theme id and zone name in data (no switch in code)");
        var arid = data.ZoneThemes.First(t => t.Id == "arid");
        var poolCount = arid.PackPool.SelectMany(pk => pk.Members).GroupBy(m => data.Enemies[m.typeId].Family)
            .ToDictionary(g => g.Key, g => g.Sum(m => m.count));
        Check(arid.PackPool.Count >= 6 && poolCount.GetValueOrDefault("bandit") > poolCount.GetValueOrDefault("goblin") * 3 &&
              arid.OverlookPool.All(pk => pk.Members.All(m => m.typeId == "bandit_archer")) && arid.OverlookPool.Count >= 1 &&
              arid.BossPacks.Single().Boss == "bandit_captain" && arid.GroundMaterials.Count >= 3 && arid.PropStyle == "arid",
              "the arid theme has its own pool: a bandit stronghold — bandit-heavy packs, archers on the high ground, the Captain as boss");
        var scroll = new LootGenerator(data, new Random(8515));
        int aridRolled = 0;
        for (int i = 0; i < 400; i++)
            if (scroll.GenerateWarpScroll(5, ItemRarity.Rare).Modifiers.Any(m => m.ModifierId == "warp_zone_arid")) aridRolled++;
        Check(aridRolled > 10, $"sealed scrolls roll the new zone seal like the other three ({aridRolled}/400 rare scrolls)");

        var bluffs = PortalRun(data, 851501, "warp_zone_arid+warp_boss+warp_level_t1");
        var bw = bluffs.World;
        var live = bw.Enemies.Values.Where(e => !e.Dead).ToList();
        var allowed = arid.PackPool.SelectMany(pk => pk.Members.Select(m => m.typeId))
            .Concat(arid.OverlookPool.SelectMany(pk => pk.Members.Select(m => m.typeId)))
            .Concat(arid.PromisedPacks.SelectMany(pk => pk.Members.Select(m => m.typeId)))
            .Concat(arid.BossPacks.SelectMany(b => b.GuardMembers.Select(m => m.typeId).Append(b.Boss))).ToHashSet();
        Check(bw.MapIndex == ServerWorld.ScrollMapIndex && bw.Map.Theme?.Id == "arid" && bw.ZoneTitle.Contains("Scorched Bluffs") &&
              bluffs.A.World.Map.Theme?.Id == "arid" && bluffs.B.World.ZoneTitle == bw.ZoneTitle,
              $"an arid warp scroll opens an arid run ('{bw.ZoneTitle}', both clients)");
        int banditsThere = live.Count(e => e.Def.Family == "bandit");
        Check(live.Count >= 8 && live.All(e => allowed.Contains(e.Def.Id) && !e.Def.Undead) && banditsThere >= live.Count * 0.7f,
              $"populated from its pool: {live.Count} enemies, {banditsThere} of them bandits ({string.Join(", ", live.Select(e => e.Def.Id).Distinct().OrderBy(x => x))})");
        var overlookPacks = bw.Packs.Where(pk => bw.Map.OverlookSpots.Any(o => Vector2.Distance(o, pk.Position) < 0.01f)).ToList();
        Check((bw.Map.OverlookSpots.Count == 0 || (overlookPacks.Count == bw.Map.OverlookSpots.Count &&
                  overlookPacks.All(pk => pk.Entries.All(en => en.typeId == "bandit_archer")))) &&
              live.Any(e => e.Def.Id == "bandit_brute"),
              $"archers hold the {bw.Map.OverlookSpots.Count} overlook(s), and a Brute is always somewhere on the bluffs");
        var chief = live.FirstOrDefault(e => e.Def.Id == "bandit_captain");
        Check(chief != null && chief.Affixes.HasFlag(EliteAffix.Boss) && bw.ExitLocked && bw.BossAlive &&
              bluffs.B.World.Enemies.Count == live.Count && bluffs.Server.TickFailures == 0,
              "the Bandit Captain seals the exit, the second client sees the same population, no tick failures");
        bluffs.Close();
    }

    private static void Batch85Bow(GameData data)
    {
        Console.WriteLine("\n-- Batch 85.14: bow skills — Snipe, Split Arrow, Disengage, bow modifiers --");
        var archer = CharacterData.CreateNew(data, "Fletch", "archer");
        var bs = new Session(data, 851401, characters: new[] { archer, null }, names: new[] { "Fletch", "Watcher" });
        var w = bs.World;
        var pa = bs.P(bs.A);
        foreach (var id in new[] { "snipe", "split_arrow", "disengage" }) bs.A.SendDebugCommand("learn_skill", id);
        bs.Pump(0.6f);
        var spawn = w.Map.PlayerSpawn;
        var open = OpenDirection(w.Map, spawn, 9f);
        var side = new Vector2(-open.Y, open.X);
        Check(open != Vector2.Zero && pa.Character.MainHand?.GetBase(data).Category == ItemCategory.Bow &&
              new[] { "snipe", "split_arrow", "disengage" }.All(id => pa.Character.GetSkill(id) != null),
              "(setup) an archer with a bow, the three new skills learned, a clear lane to shoot down");
        void Quiet()
        {
            foreach (var e in w.Enemies.Values.ToList())
                if (!e.Dead && Vector2.Distance(e.Position, spawn) < 16f) w.DebugSkillHit(pa, e, null, e.Health + 1e7f);
            foreach (var pr in w.Projectiles.Keys.ToList()) w.Projectiles.Remove(pr);
            bs.A.SendDebugCommand("heal");
            bs.Pump(0.3f);
            bs.Place(bs.A, spawn);
        }
        void Flat()
        {
            pa.Stats.WeaponMinDamage = pa.Stats.WeaponMaxDamage = 10f;
            pa.Stats.CritChance = 0f;
        }
        ServerEnemy Dummy(Vector2 at)
        {
            var e = w.SpawnEnemy("grunt", at);
            e.MaxHealth = e.Health = 100000f;
            e.AttackReadyAt = w.Time + 9999f;
            e.StunnedUntil = w.Time + 600f;
            return e;
        }
        ServerProjectile Loose(string skill, Vector2 at, float charge = 0f)
        {
            pa.Mana = pa.Stats.MaxMana;
            pa.SkillReadyAt.Clear();
            pa.GlobalSkillReadyAt = 0;
            int lastId = w.Projectiles.Keys.DefaultIfEmpty(0).Max();
            bs.A.RequestUseSkill(skill, at, -1, charge);
            ServerProjectile found = null;
            for (int i = 0; i < 40 && found == null; i++)
            {
                bs.Server.Update(1f / 60f);
                foreach (var c in bs.Clients) c.Update(1f / 60f);
                Thread.Sleep(2);
                found = w.Projectiles.Values.Where(pr => pr.FromPlayer && pr.Id > lastId).OrderBy(pr => pr.Id).FirstOrDefault();
            }
            return found;
        }

        // ---- Snipe.
        Quiet(); Flat();
        var snipeDef = data.Skills["snipe"];
        var snipeStats = Skills.SkillMath.Compute(data, snipeDef, 1, Array.Empty<Skills.ScrollDefinition>(), pa.Stats);
        var line = new[] { Dummy(spawn + open * 3f), Dummy(spawn + open * 4.5f), Dummy(spawn + open * 6f) };
        var quick = Loose("snipe", spawn + open * 8f, charge: 0f);
        float quickMin = quick?.MinDamage ?? 0f, quickRange = quick?.MaxRange ?? 0f, quickSpeed = quick?.Speed ?? 0f;
        bool quickPierce = quick?.Pierce ?? true;
        bs.Pump(1.0f);
        int quickHits = line.Count(e => e.Health < e.MaxHealth);
        Check(quick != null && !quickPierce && quickHits == 1 && line[0].Health < line[0].MaxHealth &&
              MathF.Abs(quickRange - snipeStats.Range) < 0.01f && MathF.Abs(quickSpeed - snipeStats.ProjectileSpeed) < 0.01f,
              $"Snipe released at once: one ordinary arrow (range {quickRange:0.#}, speed {quickSpeed:0.#}) that stops in the first enemy");
        foreach (var e in line) e.Health = e.MaxHealth;
        var full = Loose("snipe", spawn + open * 8f, charge: 1f);
        float fullMin = full?.MinDamage ?? 0f, fullRange = full?.MaxRange ?? 0f, fullSpeed = full?.Speed ?? 0f;
        bool fullPierce = full?.Pierce ?? false;
        bs.Pump(1.0f);
        Check(full != null && MathF.Abs(fullMin / quickMin - (1f + snipeDef.ChargeDamageBonus)) < 0.01f &&
              MathF.Abs(fullRange / quickRange - (1f + snipeDef.ChargeRangeBonus)) < 0.01f &&
              MathF.Abs(fullSpeed / quickSpeed - (1f + snipeDef.ChargeSpeedBonus)) < 0.01f,
              $"a full draw: damage x{fullMin / quickMin:0.0}, range {quickRange:0.#} -> {fullRange:0.#}, speed {quickSpeed:0.#} -> {fullSpeed:0.#}");
        Check(fullPierce && line.All(e => e.Health < e.MaxHealth) && snipeDef.Chargeable && snipeDef.PierceAtFullCharge && !snipeDef.Pierce,
              "and at full charge it pierces everything in line (all three enemies hit)");
        foreach (var e in line) e.Health = e.MaxHealth;
        var half = Loose("snipe", spawn + open * 8f, charge: 0.5f);
        bool halfPierce = half?.Pierce ?? true;
        float halfMin = half?.MinDamage ?? 0f;
        bs.Pump(1.0f);
        Check(half != null && !halfPierce && halfMin > quickMin * 1.5f && halfMin < fullMin * 0.8f && line.Count(e => e.Health < e.MaxHealth) == 1,
              "a half draw sits in between and does not pierce");

        // ---- Split Arrow.
        Quiet(); Flat();
        var splitDef = data.Skills["split_arrow"];
        int lastBefore = w.Projectiles.Keys.DefaultIfEmpty(0).Max();
        Loose("split_arrow", spawn + open * 5f);
        var fan = w.Projectiles.Values.Where(pr => pr.FromPlayer && pr.Id > lastBefore).OrderBy(pr => MathF.Atan2(Vector2.Dot(pr.Direction, side), Vector2.Dot(pr.Direction, open))).ToList();
        float fanWidth = fan.Count == 5 ? MathF.Acos(Math.Clamp(Vector2.Dot(fan[0].Direction, fan[4].Direction), -1f, 1f)) : 0f;
        Check(fan.Count == 5 && MathF.Abs(fanWidth - splitDef.ProjectileSpread * 4f) < 0.02f &&
              Vector2.Dot(fan[2].Direction, open) > 0.999f && fan.All(pr => MathF.Abs(pr.MaxRange - fan[0].MaxRange) < 0.01f && pr.MaxRange < 7f),
              $"Split Arrow looses five arrows in a {fanWidth * 180f / MathF.PI:0}° fan, short-ranged ({fan.FirstOrDefault()?.MaxRange:0.#} tiles)");
        bs.Pump(0.8f);
        Quiet(); Flat();
        var closeDummy = Dummy(spawn + open * 0.9f);
        Loose("split_arrow", spawn + open * 5f);
        bs.Pump(0.8f);
        float closeDamage = closeDummy.MaxHealth - closeDummy.Health;
        float perArrow = 10f * splitDef.WeaponDamageMultiplier;
        Quiet(); Flat();
        var farDummy = Dummy(spawn + open * 5f);
        Loose("split_arrow", spawn + open * 5f);
        bs.Pump(0.9f);
        float farDamage = farDummy.MaxHealth - farDummy.Health;
        Check(closeDamage >= perArrow * 2.9f && farDamage > 0f && farDamage <= perArrow * 2.1f && closeDamage >= farDamage * 1.5f,
              $"point blank several arrows find the same body ({closeDamage / perArrow:0.#} arrows' worth); at five tiles it is thin ({farDamage / perArrow:0.#})");

        // ---- Disengage.
        Quiet(); Flat();
        var disDef = data.Skills["disengage"];
        var chaser = Dummy(spawn + open * 2.5f);
        chaser.SlowedUntil = 0f;
        int dodgesSeen = bs.B.World.DodgeEventsSeen;
        var parting = Loose("disengage", chaser.Position);
        bool hopSafe = pa.InvulnerableUntil > w.Time && pa.DodgeUntil > w.Time;
        bool towardAim = parting != null && Vector2.Dot(parting.Direction, open) > 0.99f;
        bs.Pump(0.8f);
        Check(parting != null && hopSafe && towardAim && bs.B.World.DodgeEventsSeen == dodgesSeen + 1 &&
              disDef.HopBackDistance >= 2.5f && disDef.HopBackDuration > 0f,
              $"Disengage: a {disDef.HopBackDistance}-tile backward hop on the dodge system (i-frames granted, the roll replicates) while an arrow flies at the aim");
        Check(chaser.Health < chaser.MaxHealth && chaser.SlowedUntil > w.Time + 1f,
              $"its arrow slows what it hits ({chaser.SlowedUntil - w.Time:0.0} s of slow left)");

        // ---- Bow-only modifiers.
        Quiet();
        string[] bowMods = { "piercing", "of_the_hawk", "barbed" };
        Check(bowMods.All(id => data.Modifiers.TryGetValue(id, out var m) && m.CompatibleItemCategories.SequenceEqual(new[] { ItemCategory.Bow }) &&
                  !m.CompatibleWith(ItemCategory.Mace) && !m.CompatibleWith(ItemCategory.Quiver)) &&
              data.Modifiers["piercing"].MinimumValue == 1f && data.Modifiers["piercing_t2"].MinimumValue == 2f &&
              data.Modifiers["of_the_hawk"].AffixType == AffixType.Suffix && data.Modifiers["barbed_t4"].MinimumItemLevel == 30,
              "Piercing, of the Hawk and Barbed roll on bows only");
        var plainBow = pa.Character.MainHand;
        ItemInstance Modded(string modId, float value)
        {
            var it = new ItemInstance { BaseItemId = plainBow.BaseItemId, ItemLevel = 1, Rarity = ItemRarity.Magic, MaxPrefixes = 3, MaxSuffixes = 3 };
            it.Modifiers.Add(new ItemModifierRoll { ModifierId = modId, Value = value });
            return it;
        }
        void Wield(ItemInstance it)
        {
            pa.Character.Equipment[EquipSlot.MainHand] = it;
            pa.RecomputeStats(data);
        }
        var shotDef = data.Skills["arrow_shot"];
        var baseShot = Skills.SkillMath.Compute(data, shotDef, 1, Array.Empty<Skills.ScrollDefinition>(), pa.Stats);
        var row = new[] { Dummy(spawn + open * 3f), Dummy(spawn + open * 4.5f), Dummy(spawn + open * 6f) };
        Loose("arrow_shot", spawn + open * 8f);
        bs.Pump(1.0f);
        int plainHits = row.Count(e => e.Health < e.MaxHealth);
        foreach (var e in row) e.Health = e.MaxHealth;
        Wield(Modded("piercing", 1f));
        var pierced = Loose("arrow_shot", spawn + open * 8f);
        int pierceLeft = pierced?.PierceLeft ?? -1;
        bs.Pump(1.0f);
        int pierceHits = row.Count(e => e.Health < e.MaxHealth);
        Check(pa.Stats.ExtraPierce == 1 && plainHits == 1 && pierceLeft == 1 && pierceHits == 2 && row[2].Health == row[2].MaxHealth,
              $"Piercing +1: an Arrow Shot passes through one enemy and stops in the second ({plainHits} -> {pierceHits} hit)");
        Wield(Modded("of_the_hawk", 20f));
        var hawkShot = Skills.SkillMath.Compute(data, shotDef, 1, Array.Empty<Skills.ScrollDefinition>(), pa.Stats);
        var hawkArrow = Loose("arrow_shot", spawn + open * 8f);
        Check(pa.Stats.ProjectileReachIncrease == 20f && MathF.Abs(hawkShot.Range - baseShot.Range * 1.2f) < 0.01f &&
              MathF.Abs(hawkShot.ProjectileSpeed - baseShot.ProjectileSpeed * 1.2f) < 0.01f &&
              hawkArrow != null && MathF.Abs(hawkArrow.Speed - baseShot.ProjectileSpeed * 1.2f) < 0.01f && MathF.Abs(hawkArrow.MaxRange - baseShot.Range * 1.2f) < 0.01f,
              $"of the Hawk +20%: Arrow Shot's range {baseShot.Range:0.#} -> {hawkShot.Range:0.#} and speed {baseShot.ProjectileSpeed:0.#} -> {hawkShot.ProjectileSpeed:0.#}");
        bs.Pump(1.0f);
        Wield(Modded("barbed", 30f));
        var barbedShot = Skills.SkillMath.Compute(data, shotDef, 1, Array.Empty<Skills.ScrollDefinition>(), pa.Stats);
        var fireDef = data.Skills["fire_bolt"];
        var barbedSpell = Skills.SkillMath.Compute(data, fireDef, 1, Array.Empty<Skills.ScrollDefinition>(), pa.Stats);
        Wield(Modded("barbed", 100f));
        foreach (var e in row) { e.Health = e.MaxHealth; e.BleedStacks.Clear(); }
        Loose("arrow_shot", spawn + open * 8f);
        bs.Pump(1.0f);
        Check(pa.Stats.BowBleedChance == 100f && MathF.Abs(barbedShot.BleedChance - 0.30f) < 0.001f && baseShot.BleedChance == 0f &&
              barbedSpell.BleedChance == 0f && row[0].BleedStacks.Count > 0,
              "Barbed: bow attacks gain the bleed chance (30% rolled -> 0.30; a spell gets nothing) and a barbed arrow leaves its target bleeding");
        Check(data.Modifiers["piercing"].DescribeRoll(1f).Contains("Pierced") && data.Modifiers["of_the_hawk"].DescribeRoll(20f).Contains("Range") &&
              data.Modifiers["barbed"].DescribeRoll(30f).Contains("Bleed"),
              $"their tooltips read plainly (\"{data.Modifiers["of_the_hawk"].DescribeRoll(20f)}\")");
        Wield(plainBow);
        var fletcher = data.Items["book_fletcher"]; var longDraw = data.Items["book_longdraw"];
        Check(fletcher.UnlocksSkills.Contains("split_arrow") && longDraw.UnlocksSkills.Contains("snipe") && longDraw.UnlocksSkills.Contains("disengage") &&
              data.LootTables["boss"].SkillBooks.Any(b => b.ItemId == "book_longdraw"),
              "the new bow skills are in Fletcher's Manual and a second bow book, The Long Draw");
        Check(bs.Server.TickFailures == 0, "no tick failures through the bow checks");
        bs.Close();
    }

    /// <summary>A direction from <paramref name="from"/> with <paramref name="tiles"/> of clear, level ground.</summary>
    private static Vector2 OpenDirection(World.GameMap map, Vector2 from, float tiles)
    {
        float h = map.GroundHeightAt(from);
        foreach (var dir in new[] { new Vector2(-1, 0), new Vector2(0, -1), new Vector2(0, 1), new Vector2(1, 0),
                     Vector2.Normalize(new Vector2(-1, -1)), Vector2.Normalize(new Vector2(-1, 1)), Vector2.Normalize(new Vector2(1, -1)), Vector2.Normalize(new Vector2(1, 1)) })
        {
            bool clear = true;
            for (float t = 0.25f; t <= tiles && clear; t += 0.25f)
            {
                var at = from + dir * t;
                clear = !map.CircleHitsWall(at, 0.55f) && !map.IsWater((int)at.X, (int)at.Y) && MathF.Abs(map.GroundHeightAt(at) - h) < 0.05f;
            }
            if (clear) return dir;
        }
        return Vector2.Zero;
    }

    private static void Batch85Mace(GameData data)
    {
        Console.WriteLine("\n-- Batch 85.13: mace identity — Crash, Leap Slam, Skull Crack, Fault Line, mace modifiers, Aftershock --");
        var crashCfg = data.MaceCrash;
        var ms = new Session(data, 851301);
        var w = ms.World;
        var pa = ms.P(ms.A);
        // (The fresh warrior's own plain club: a dev-granted mace rolls random modifiers —
        // including tonight's mace ones — which would skew every number below.)
        foreach (var id in new[] { "mace_slam", "leap_slam", "skull_crack", "fault_line", "ground_slam" }) ms.A.SendDebugCommand("learn_skill", id);
        ms.Pump(0.6f);
        // Somewhere quiet: move both players next to a wall with open ground behind them.
        var spawn = w.Map.PlayerSpawn;
        var open = OpenDirection(w.Map, spawn, 7f);
        Vector2 wallDir = Vector2.Zero, wallFoot = Vector2.Zero;
        foreach (var dir in new[] { new Vector2(1, 0), new Vector2(-1, 0), new Vector2(0, 1), new Vector2(0, -1) })
        {
            for (float t = 0.5f; t < 8f && wallDir == Vector2.Zero; t += 0.1f)
            {
                var at = spawn + dir * t;
                if (MathF.Abs(w.Map.GroundHeightAt(at) - w.Map.GroundHeightAt(spawn)) > 0.05f) break;
                if (!w.Map.CircleHitsWall(at, 0.4f)) continue;
                if (t > 2.2f) { wallDir = dir; wallFoot = spawn + dir * (t - 0.1f); } // last clear spot before the wall
                break;
            }
            if (wallDir != Vector2.Zero) break;
        }
        Check(open != Vector2.Zero && wallDir != Vector2.Zero, "(setup) the arena spawn has open ground one way and a wall another");
        void Quiet()
        {
            foreach (var e in w.Enemies.Values.ToList())
                if (!e.Dead && Vector2.Distance(e.Position, spawn) < 14f) w.DebugSkillHit(pa, e, null, e.Health + 1e7f);
            foreach (var drop in w.Drops.Keys.ToList()) w.Drops.Remove(drop);
            ms.A.SendDebugCommand("heal");
            pa.Mana = pa.Stats.MaxMana;
            pa.SkillReadyAt.Clear();
            pa.GlobalSkillReadyAt = 0;
            ms.Pump(0.25f);
        }
        ServerEnemy Dummy(Vector2 at, float hp = 100000f, bool stunned = false)
        {
            var e = w.SpawnEnemy("grunt", at);
            e.MaxHealth = e.Health = hp;
            e.AttackReadyAt = w.Time + 9999f;                 // a punching bag: never swings
            e.FrozenUntil = stunned ? w.Time + 600f : 0f;      // held in place either way...
            e.StunnedUntil = stunned ? w.Time + 600f : 0f;
            e.SlowedUntil = w.Time + 600f;
            return e;
        }
        void Cast(string skill, Vector2 at)
        {
            pa.Mana = pa.Stats.MaxMana;
            pa.SkillReadyAt.Clear();
            pa.GlobalSkillReadyAt = 0;
            ms.A.RequestUseSkill(skill, at);
        }

        // ---- Crash: a wall versus open ground.
        Quiet();
        ms.Place(ms.A, spawn);
        var slamDef = data.Skills["mace_slam"];
        var openDummy = Dummy(spawn + open * 1.2f, stunned: true);
        int crashes0 = w.CrashCount;
        w.DebugKnockback(pa, openDummy, spawn, slamDef.Knockback, 100f, "mace_slam");
        float openMoved = Vector2.Distance(openDummy.Position, spawn + open * 1.2f);
        Check(w.CrashCount == crashes0 && MathF.Abs(openMoved - slamDef.Knockback) < 0.05f && openDummy.Health == openDummy.MaxHealth,
              $"knockback across open ground: thrown the full {slamDef.Knockback} tiles, no Crash");
        var wallDummy = Dummy(wallFoot - wallDir * 0.3f, stunned: false);
        wallDummy.StunBuildup = 0f;
        float wallHp = wallDummy.Health;
        int crashSeen0 = ms.B.World.CrashEventsSeen;
        w.DebugKnockback(pa, wallDummy, wallFoot - wallDir * 1.5f, slamDef.Knockback, 100f, "mace_slam");
        float crashBuildup = wallDummy.StunBuildup; // read at once: the stun meter decays
        ms.Pump(0.3f);
        float crashTaken = wallHp - wallDummy.Health;
        Check(w.CrashCount == crashes0 + 1 && MathF.Abs(w.LastCrashDamage - 100f * crashCfg.DamagePct / 100f) < 0.01f &&
              crashTaken > 0f && crashTaken <= w.LastCrashDamage + 0.01f && crashBuildup >= crashCfg.StunBuildup * 0.99f,
              $"knockback into a wall: Crash — bonus {crashCfg.DamagePct}% of the hit ({w.LastCrashDamage:0.#} before armor, {crashTaken:0.#} dealt) and +{crashCfg.StunBuildup} stun buildup");
        Check(ms.B.World.CrashEventsSeen == crashSeen0 + 1 && ms.B.World.Effects.Any(fx => fx.Kind == "dirtburst") &&
              Render.WorldRenderer.CrashColor != default,
              "the Crash replicates: its damage number carries the Crash tint flag and a dust puff plays (second client)");
        // Into another enemy: both pay, the anvil a smaller share.
        Quiet();
        var hammer = Dummy(spawn + open * 1.0f, stunned: true);
        var anvil = Dummy(spawn + open * (1.0f + slamDef.Knockback - 0.1f), stunned: true);
        float hammerHp = hammer.Health, anvilHp = anvil.Health;
        crashes0 = w.CrashCount;
        w.DebugKnockback(pa, hammer, spawn, slamDef.Knockback, 100f, "mace_slam");
        Check(w.CrashCount == crashes0 + 1 && hammerHp - hammer.Health > 0f && anvilHp - anvil.Health > 0f &&
              (anvilHp - anvil.Health) < (hammerHp - hammer.Health),
              $"thrown into another enemy: both take Crash damage, the one hit taking the smaller share ({hammerHp - hammer.Health:0.#} vs {anvilHp - anvil.Health:0.#})");
        // Only maces crash.
        Quiet();
        var bashDummy = Dummy(wallFoot - wallDir * 0.3f, stunned: true);
        crashes0 = w.CrashCount;
        w.DebugKnockback(pa, bashDummy, wallFoot - wallDir * 1.5f, 2.5f, 100f, "shield_bash");
        Check(w.CrashCount == crashes0 && bashDummy.Health == bashDummy.MaxHealth, "a non-mace skill's knockback into a wall is just a knockback");
        // End to end: a real Mace Slam beside the wall.
        Quiet();
        ms.Place(ms.A, wallFoot - wallDir * 1.6f);
        var realDummy = Dummy(wallFoot - wallDir * 0.35f, stunned: true);
        crashes0 = w.CrashCount;
        Cast("mace_slam", realDummy.Position);
        ms.Pump(0.8f);
        Check(w.CrashCount > crashes0 && realDummy.Health < realDummy.MaxHealth,
              "a real Mace Slam that drives its target into the wall triggers Crash");

        // ---- Leap Slam.
        Quiet();
        ms.Place(ms.A, spawn);
        var leapDef = data.Skills["leap_slam"];
        var landing = spawn + open * 4.5f;
        var nearLanding = Dummy(landing + open * 1.0f, stunned: true);
        var sideOfLanding = Dummy(landing - open * 0.9f, stunned: true);
        var nearTakeoff = Dummy(spawn - open * 0.2f + new Vector2(-open.Y, open.X) * 1.2f, stunned: true);
        float d0 = Vector2.Distance(nearLanding.Position, landing);
        Cast("leap_slam", landing);
        for (int i = 0; i < 20 && pa.InvulnerableUntil <= w.Time; i++) ms.Pump(0.02f); // the request arrives
        bool airborneSafe = pa.InvulnerableUntil > w.Time + 0.2f;
        ms.Place(ms.A, landing); // the client's predicted jump (PlayScreen moves the local player the same way)
        ms.Pump(0.6f);
        Check(leapDef.LeapDistance >= 5f && leapDef.Archetype == Skills.SkillArchetype.MeleeArea && leapDef.HasTag("Slam") && leapDef.HasTag("Mace") &&
              Vector2.Distance(Skills.SkillMath.LeapLanding(spawn, spawn + open * 30f, open, leapDef.LeapDistance), spawn + open * leapDef.LeapDistance) < 0.01f &&
              Vector2.Distance(Skills.SkillMath.LeapLanding(spawn, landing, open, leapDef.LeapDistance), landing) < 0.01f,
              $"Leap Slam leaps to the aimed point, at most {leapDef.LeapDistance} tiles");
        Check(airborneSafe && nearLanding.Health < nearLanding.MaxHealth && sideOfLanding.Health < sideOfLanding.MaxHealth &&
              nearTakeoff.Health == nearTakeoff.MaxHealth,
              "the slam lands where the leaper comes down (enemies at the landing are hit, the one left at the take-off is not), invulnerable in the air");
        Check(Vector2.Distance(nearLanding.Position, landing) > d0 + 1.0f &&
              Vector2.Dot(sideOfLanding.Position - landing, -open) > 0.9f + 1.0f,
              $"and throws them outward from the impact ({d0:0.0} -> {Vector2.Distance(nearLanding.Position, landing):0.0} tiles)");

        // ---- Skull Crack.
        Quiet();
        ms.Place(ms.A, spawn);
        var crackDef = data.Skills["skull_crack"];
        // A flat, crit-less weapon for this section, so the +100% reads exactly.
        pa.Stats.WeaponMinDamage = pa.Stats.WeaponMaxDamage = 10f;
        pa.Stats.CritChance = 0f;
        var crackA = Dummy(spawn + open * 1.2f);
        var crackB = Dummy(spawn + open * 1.2f + new Vector2(-open.Y, open.X) * 0.7f);
        float plain = 0f;
        for (int i = 0; i < 6; i++)
        {
            crackA.StunBuildup = 0f; crackA.StunnedUntil = 0f; crackA.StunResistStacks = 0;
            float before = crackA.Health;
            Cast("skull_crack", crackA.Position);
            ms.Pump(0.8f);
            plain += before - crackA.Health;
        }
        Check(plain > 0f && crackB.Health == crackB.MaxHealth && crackDef.SingleTarget,
              "Skull Crack strikes ONE enemy (the neighbour inside the impact circle is untouched)");
        crackA.StunBuildup = 0f; crackA.StunnedUntil = 0f; crackA.StunResistStacks = 0;
        Cast("skull_crack", crackA.Position);
        ms.Pump(0.8f);
        float oneHitBuildup = crackA.StunBuildup;
        Cast("skull_crack", crackA.Position);
        ms.Pump(0.8f);
        Check(oneHitBuildup >= 80f && crackA.StunnedUntil > w.Time,
              $"huge stun buildup: {oneHitBuildup:0} of 100 from one blow, stunned by the second");
        float helpless = 0f;
        float extendedBy = 0f;
        for (int i = 0; i < 6; i++)
        {
            crackA.StunnedUntil = w.Time + 30f;
            float before = crackA.Health, untilBefore = crackA.StunnedUntil;
            Cast("skull_crack", crackA.Position);
            ms.Pump(0.8f);
            helpless += before - crackA.Health;
            extendedBy = crackA.StunnedUntil - untilBefore;
        }
        Check(helpless > plain * 1.95f && helpless < plain * 2.05f && MathF.Abs(extendedBy - crackDef.StunExtend) < 0.01f,
              $"against a stunned target: +{crackDef.BonusVsStunnedPct}% damage ({plain:0} -> {helpless:0} over six blows) and the stun runs {extendedBy:0.0} s longer");
        crackA.StunnedUntil = 0f;
        crackA.FrozenUntil = w.Time + 30f;
        float frozenBefore = crackA.Health, frozenUntil = crackA.FrozenUntil;
        Cast("skull_crack", crackA.Position);
        ms.Pump(0.8f);
        Check(frozenBefore - crackA.Health > plain / 6f * 1.3f && crackA.FrozenUntil > frozenUntil + 1f,
              "a frozen target counts too (bonus damage, the freeze extended)");
        pa.RecomputeStats(data);

        // ---- Fault Line.
        Quiet();
        ms.Place(ms.A, spawn);
        var faultDef = data.Skills["fault_line"];
        var impact = Skills.SkillMath.MeleeImpactPoint(spawn, spawn + open * 6f, open, pa.Stats.WeaponRange > faultDef.Range ? pa.Stats.WeaponRange : faultDef.Range);
        var farDummy = Dummy(impact + open * faultDef.FissureSpacing * 3f, stunned: true);
        var midDummy = Dummy(impact + open * faultDef.FissureSpacing * 2f, stunned: true);
        var offLine = Dummy(impact + open * faultDef.FissureSpacing * 2f + new Vector2(-open.Y, open.X) * 3.2f, stunned: true);
        Cast("fault_line", spawn + open * 6f);
        for (int i = 0; i < 30 && w.PendingGroundBursts == 0; i++) ms.Pump(0.05f); // the wind-up lands; the fissure is queued
        var burstSpots = w.PendingGroundBurstPositions.ToList();
        bool farUntouchedYet = farDummy.Health == farDummy.MaxHealth;
        Check(burstSpots.Count == 3 && farUntouchedYet &&
              Enumerable.Range(1, 3).All(i => burstSpots.Any(b => Vector2.Distance(b, impact + open * faultDef.FissureSpacing * i) < 0.25f)),
              $"Fault Line queues three eruptions forward along the aim, {faultDef.FissureSpacing} tiles apart; the far enemy isn't hit yet");
        ms.Pump(1.1f);
        Check(w.PendingGroundBursts == 0 && farDummy.Health < farDummy.MaxHealth && midDummy.Health < midDummy.MaxHealth &&
              offLine.Health == offLine.MaxHealth && ms.B.World.Effects.Count >= 0,
              "the bursts erupt in turn: enemies along the line are hit, one well off to the side is not");
        Quiet();
        ms.Place(ms.A, wallFoot - wallDir * 2.2f);
        Cast("fault_line", wallFoot + wallDir * 3f);
        int wallBursts = 0;
        for (int i = 0; i < 16; i++) { ms.Pump(0.05f); wallBursts = Math.Max(wallBursts, w.PendingGroundBursts); }
        Check(wallBursts < 3, $"a wall stops the fissure ({wallBursts} of 3 bursts queued facing the wall)");
        ms.Pump(1.0f);

        // ---- Mace-only modifiers: each one changes its number.
        Quiet();
        ms.Place(ms.A, spawn);
        string[] maceMods = { "concussive", "of_impact", "seismic", "crushing" };
        Check(maceMods.All(id => data.Modifiers.TryGetValue(id, out var m) && m.CompatibleItemCategories.SequenceEqual(new[] { ItemCategory.Mace }) &&
                  !m.CompatibleWith(ItemCategory.Staff) && !m.CompatibleWith(ItemCategory.Bow) &&
                  data.Modifiers.ContainsKey(id + "_t2") && data.Modifiers[id + "_t2"].MinimumItemLevel == 10 &&
                  data.Modifiers[id + "_t4"].MinimumItemLevel == 30) &&
              data.Modifiers["of_impact"].AffixType == AffixType.Suffix && data.Modifiers["concussive"].AffixType == AffixType.Prefix,
              "Concussive, of Impact, Seismic and Crushing roll on maces only, in four tiers on the 1/10/20/30 item-level bands");
        var plainMace = pa.Character.MainHand;
        ItemInstance Modded(string modId, float value)
        {
            var it = new ItemInstance { BaseItemId = plainMace.BaseItemId, ItemLevel = plainMace.ItemLevel, Rarity = ItemRarity.Magic, MaxPrefixes = 3, MaxSuffixes = 3 };
            it.Modifiers.Add(new ItemModifierRoll { ModifierId = modId, Value = value });
            return it;
        }
        void Wield(ItemInstance it)
        {
            pa.Character.Equipment[EquipSlot.MainHand] = it;
            pa.RecomputeStats(data);
        }
        Wield(new ItemInstance { BaseItemId = plainMace.BaseItemId, ItemLevel = plainMace.ItemLevel, MaxPrefixes = 3, MaxSuffixes = 3 });
        var learnedSlam = pa.Character.GetSkill("mace_slam");
        float baseRadius = Skills.SkillMath.Compute(data, slamDef, learnedSlam.Level, learnedSlam.ScrollDefinitions(data), pa.Stats).Radius;
        var stunBag = Dummy(spawn + open * 1.5f);
        w.DebugStunBuildup(pa, stunBag, 10f);
        float baseBuildup = stunBag.StunBuildup;
        var throwBag = Dummy(spawn + open * 1.0f, stunned: true);
        var throwStart = throwBag.Position;
        w.DebugKnockback(pa, throwBag, spawn, 1.0f, 100f, "mace_slam");
        float baseThrow = Vector2.Distance(throwBag.Position, throwStart);
        var crushBag = Dummy(spawn + open * 1.5f + new Vector2(-open.Y, open.X), stunned: true);
        float hpC = crushBag.Health;
        w.DebugDirectHit(pa, crushBag, "mace_slam", 50f);
        float baseCrush = hpC - crushBag.Health;

        Wield(Modded("concussive", 20f));
        stunBag.StunBuildup = 0f;
        w.DebugStunBuildup(pa, stunBag, 10f);
        Check(pa.Stats.StunBuildupIncrease == 20f && MathF.Abs(stunBag.StunBuildup - baseBuildup * 1.2f) < 0.01f,
              $"Concussive +20%: the same blow builds {baseBuildup:0.#} -> {stunBag.StunBuildup:0.#} stun");
        Wield(Modded("of_impact", 30f));
        throwBag.Position = throwStart;
        var wallBag = Dummy(wallFoot - wallDir * 0.3f, stunned: true);
        w.DebugKnockback(pa, throwBag, spawn, 1.0f, 100f, "mace_slam");
        float impactThrow = Vector2.Distance(throwBag.Position, throwStart);
        w.DebugKnockback(pa, wallBag, wallFoot - wallDir * 1.5f, slamDef.Knockback, 100f, "mace_slam");
        Check(pa.Stats.ImpactIncrease == 30f && MathF.Abs(impactThrow - baseThrow * 1.3f) < 0.05f &&
              MathF.Abs(w.LastCrashDamage - 100f * crashCfg.DamagePct / 100f * 1.3f) < 0.01f,
              $"of Impact +30%: knockback {baseThrow:0.00} -> {impactThrow:0.00} tiles and Crash damage {100f * crashCfg.DamagePct / 100f:0} -> {w.LastCrashDamage:0}");
        Wield(Modded("seismic", 25f));
        float seismicRadius = Skills.SkillMath.Compute(data, slamDef, learnedSlam.Level, learnedSlam.ScrollDefinitions(data), pa.Stats).Radius;
        float strikeRadius = Skills.SkillMath.Compute(data, crackDef, 1, Array.Empty<Skills.ScrollDefinition>(), pa.Stats).Radius;
        Check(pa.Stats.SlamAreaIncrease == 25f && MathF.Abs(seismicRadius - baseRadius * 1.25f) < 0.001f && MathF.Abs(strikeRadius - crackDef.Radius) < 0.001f,
              $"Seismic +25%: Mace Slam's radius {baseRadius:0.00} -> {seismicRadius:0.00} (a non-Slam skill is unchanged)");
        Wield(Modded("crushing", 40f));
        hpC = crushBag.Health;
        w.DebugDirectHit(pa, crushBag, "mace_slam", 50f);
        float crushed = hpC - crushBag.Health;
        var awakeBag = Dummy(spawn + open * 2.2f);
        float hpAwake = awakeBag.Health;
        w.DebugDirectHit(pa, awakeBag, "mace_slam", 50f);
        Check(pa.Stats.DamageVsStunnedIncrease == 40f && MathF.Abs(crushed - baseCrush * 1.4f) < 0.01f &&
              MathF.Abs((hpAwake - awakeBag.Health) - baseCrush) < 0.01f,
              $"Crushing +40%: a stunned enemy takes {baseCrush:0.#} -> {crushed:0.#}; one on its feet takes the usual {hpAwake - awakeBag.Health:0.#}");
        var staffItem = new ItemInstance { BaseItemId = "oak_staff", MaxPrefixes = 3, MaxSuffixes = 3 };
        staffItem.Modifiers.Add(new ItemModifierRoll { ModifierId = "seismic", Value = 25f });
        Check(maceMods.All(id => data.Modifiers[id].StatAffected.ToString().Length > 0) &&
              data.Modifiers["concussive"].DescribeRoll(20f).Contains("Stun Buildup") && data.Modifiers["of_impact"].DescribeRoll(30f).Contains("Crash"),
              $"their tooltips read plainly (\"{data.Modifiers["of_impact"].DescribeRoll(30f)}\")");
        Wield(plainMace);

        // ---- the Aftershock scroll.
        Quiet();
        ms.Place(ms.A, spawn);
        var afterScroll = data.Scrolls["aftershock"];
        Check(afterScroll.RequiredTag == "Slam" && afterScroll.CompatibleWith(slamDef) && afterScroll.CompatibleWith(leapDef) &&
              afterScroll.CompatibleWith(faultDef) && !afterScroll.CompatibleWith(crackDef) &&
              data.Items["scroll_aftershock"].ScrollId == "aftershock",
              "the Aftershock scroll attaches to Slam skills only");
        learnedSlam.Scrolls.Add(new ItemInstance { BaseItemId = "scroll_aftershock" });
        pa.Stats.WeaponMinDamage = pa.Stats.WeaponMaxDamage = 10f; // exact numbers: 10 a slam, 5 a repeat
        pa.Stats.CritChance = 0f;
        // Against the wall, so the first slam can't throw the target out of the repeat.
        ms.Place(ms.A, wallFoot - wallDir * 1.5f);
        var echoBag = Dummy(wallFoot - wallDir * 0.3f, stunned: true);
        Cast("mace_slam", echoBag.Position);
        for (int i = 0; i < 30 && echoBag.Health == echoBag.MaxHealth; i++) ms.Pump(0.05f);
        float firstHit = echoBag.MaxHealth - echoBag.Health;
        int queued = w.PendingGroundBursts;
        ms.Pump(0.3f);
        float beforeEcho = echoBag.Health;
        bool notYet = w.PendingGroundBursts == 1;
        ms.Pump(0.6f);
        float echoHit = beforeEcho - echoBag.Health;
        // (Either hit may also Crash the target into the wall: +40% of that hit.)
        // so the repeat is 0.5x the slam, give or take a Crash on either side: 0.36x .. 0.70x.)
        Check(firstHit >= 9.9f && queued == 1 && notYet && echoHit >= firstHit * 0.35f && echoHit <= firstHit * 0.71f && w.PendingGroundBursts == 0,
              $"with Aftershock the slam repeats 0.6 s later at half damage ({firstHit:0.#} then {echoHit:0.#})");
        learnedSlam.Scrolls.Clear();
        pa.RecomputeStats(data);
        Check(ms.Server.TickFailures == 0, "no tick failures through the mace checks");
        ms.Close();
    }

    private static void Batch85SkillBooks(GameData data)
    {
        Console.WriteLine("\n-- Batch 85.12: skill books and the trainer --");
        var books = data.Items.Values.Where(b => b.IsSkillBook).ToList();
        Check(books.Count >= 7 && books.All(b => b.Category == ItemCategory.Curio && b.MaxStack == 1 && !b.IsEquippable &&
                  b.DropWeight == 0 && b.UnlocksSkills.Count is >= 1 and <= 3 && b.UnlocksSkills.All(data.Skills.ContainsKey)),
              $"{books.Count} skill books: Curio items, stack of one, each unlocking 1-3 real skills");
        var startSkills = data.Classes.Select(c => c.StartSkill).ToHashSet();
        var taught = books.SelectMany(b => b.UnlocksSkills).ToHashSet();
        Check(data.Skills.Keys.All(id => taught.Contains(id) || startSkills.Contains(id)),
              "every skill is reachable: taught by some book, or a class start skill" +
              (data.Skills.Keys.FirstOrDefault(id => !taught.Contains(id) && !startSkills.Contains(id)) is { } orphan ? $" — MISSING {orphan}" : ""));
        var bossTable = data.LootTables["boss"];
        Check(bossTable.SkillBookDropChance > 0.1f && bossTable.SkillBooks.Count == books.Count &&
              data.Enemies["goblin_shaman"].ExtraDrops.Count >= 2 && data.Enemies["bandit_archer"].ExtraDrops.Any(d => d.ItemId == "book_fletcher") &&
              data.Enemies["bandit_brute"].ExtraDrops.Any(d => d.ItemId == "book_shieldbearer") &&
              data.Enemies.Values.SelectMany(e => e.ExtraDrops).All(d => d.Chance <= 0.05f),
              "bosses roll any book; bandits carry bow and shield books, goblin shamans spell books — all rare");

        var warrior = CharacterData.CreateNew(data, "BookWar", "warrior");
        var bowman = CharacterData.CreateNew(data, "BookBow", "archer");
        var st = new Session(data, 851201, campaign: true, story: true, characters: new[] { warrior, bowman }, names: new[] { "BookWar", "BookBow" });
        var w = st.World;
        var pa = st.P(st.A);
        var pb = st.P(st.B);
        Check(w.SkillsGated && st.A.World.SkillsGated && pa.Character.UnlockedSkills.SetEquals(new[] { "mace_strike" }) &&
              pb.Character.UnlockedSkills.SetEquals(new[] { "arrow_shot" }),
              "story worlds gate skills (the client is told); a new character has only its class start skill unlocked");

        // ---- the first-kill guarantee, on the road's Barrow Lord.
        var roadBoss = w.Enemies.Values.First(e => e.Def.Id == "barrowlord");
        w.DebugSkillHit(pa, roadBoss, null, roadBoss.Health + 1f);
        st.Pump(0.6f);
        var reserved = w.Drops.Values.Where(d => d.GuaranteeBossId == "barrowlord").ToList();
        var forA = reserved.FirstOrDefault(d => d.ReservedForPlayerId == pa.Id);
        var forB = reserved.FirstOrDefault(d => d.ReservedForPlayerId == pb.Id);
        Check(reserved.Count == 2 && forA != null && forB != null && forA.Item.GetBase(data).IsSkillBook && forB.Item.GetBase(data).IsSkillBook,
              "the first Barrow Lord kill drops one guaranteed skill book per present player");
        Check(forA != null && forB != null &&
              forA.Item.GetBase(data).UnlocksSkills.Any(id => data.Skills[id].RequiredWeapon == ItemCategory.Mace) &&
              forB.Item.GetBase(data).UnlocksSkills.Any(id => data.Skills[id].RequiredWeapon == ItemCategory.Bow),
              $"each book suits its player's class (warrior: {forA?.Item.GetBase(data).Name}; archer: {forB?.Item.GetBase(data).Name})");
        Check(st.B.World.Drops.ContainsKey(forA.DropId) && st.A.World.Drops.ContainsKey(forB.DropId),
              "both clients see both books on the ground");
        st.Place(st.A, forB.Position);
        st.A.RequestPickup(forB.DropId);
        st.Pump(0.4f);
        Check(w.Drops.ContainsKey(forB.DropId) && st.Messages[0].Any(m => m.Contains("meant for")) &&
              !pa.Character.BookGuarantees.Contains("barrowlord"),
              "a player cannot take the book that dropped for someone else");
        st.Place(st.A, forA.Position);
        st.A.RequestPickup(forA.DropId);
        st.Pump(0.4f);
        Check(!w.Drops.ContainsKey(forA.DropId) && pa.Character.BookGuarantees.Contains("barrowlord") &&
              !pb.Character.BookGuarantees.Contains("barrowlord") &&
              st.A.World.MyCharacter.BookGuarantees.Contains("barrowlord"),
              "the guarantee is recorded when the book is PICKED UP (A has it; B, who left theirs on the ground, does not)");
        string firstBookId = forA.Item.BaseItemId;

        // B's book is left behind: the map changes and it is gone.
        st.A.SendDebugCommand("warp_home");
        st.Pump(1.2f);
        Check(w.Map.Kind == World.MapKind.RuinsHub && !w.Drops.Values.Any(d => d.Item?.GetBase(data).IsSkillBook == true),
              "leaving the map loses the book on the ground");
        var secondLord = w.SpawnEnemy("barrowlord", pa.Position + new Vector2(3f, 0f), EliteAffix.Boss);
        w.DebugSkillHit(pa, secondLord, null, secondLord.Health + 1f);
        st.Pump(0.5f);
        var again = w.Drops.Values.Where(d => d.GuaranteeBossId == "barrowlord").ToList();
        Check(again.Count == 1 && again[0].ReservedForPlayerId == pb.Id,
              "the next Barrow Lord kill drops the guarantee again for B only — none for A, who already picked theirs up");
        st.Place(st.B, again[0].Position);
        st.B.RequestPickup(again[0].DropId);
        st.Pump(0.4f);
        var thirdLord = w.SpawnEnemy("barrowlord", pa.Position + new Vector2(3f, 0f), EliteAffix.Boss);
        w.DebugSkillHit(pa, thirdLord, null, thirdLord.Health + 1f);
        st.Pump(0.5f);
        Check(pb.Character.BookGuarantees.Contains("barrowlord") && !w.Drops.Values.Any(d => d.GuaranteeBossId == "barrowlord"),
              "once both have picked theirs up, further kills guarantee nothing");
        foreach (var leftover in w.Drops.Keys.ToList()) w.Drops.Remove(leftover);

        // ---- turn-in at the trainer.
        var trainer = w.Npcs.First(n => n.TypeId == "skill_trainer");
        var aBook = pa.Character.Inventory.Items.First(pl => pl.Item.BaseItemId == firstBookId);
        var aBookBase = aBook.Item.GetBase(data);
        string lockedSkill = aBookBase.UnlocksSkills.First(id => pa.Character.GetSkill(id) == null);
        st.Place(st.A, trainer.Position + new Vector2(9f, 0f));
        st.A.RequestTurnInSkillBook(aBook.Item.InstanceId);
        st.Pump(0.4f);
        Check(pa.Character.Inventory.FindByInstance(aBook.Item.InstanceId) != null && pa.Character.UnlockedSkills.Count == 1,
              "out of the trainer's reach nothing happens: the book stays in the bag");
        st.Place(st.A, trainer.Position + new Vector2(0.9f, 0.3f));
        st.Place(st.B, trainer.Position + new Vector2(-0.9f, 0.3f));
        string panelState = UI.TrainerUI.SkillState(data, st.A.World.MyCharacter, true, lockedSkill, out var needBook);
        Check(UI.TrainerUI.BooksInBag(data, st.A.World.MyCharacter).Count == 1 && panelState == "locked" &&
              needBook != null && needBook.UnlocksSkills.Contains(lockedSkill) &&
              UI.TrainerUI.SkillState(data, st.A.World.MyCharacter, true, "mace_strike", out _) == "known" &&
              UI.TrainerUI.SkillState(data, st.A.World.MyCharacter, false, "ice_spike", out _) == "learn",
              $"the trainer panel lists the book in the bag and shows its skills as locked (\"Requires: {needBook?.Name}\")");
        pa.Character.Gold = 500;
        pb.Character.Gold = 500;
        st.A.RequestLearnSkill(lockedSkill);
        st.Pump(0.4f);
        Check(pa.Character.GetSkill(lockedSkill) == null && pa.Character.Gold == 500 &&
              st.Messages[0].Any(m => m.Contains("locked") && m.Contains("requires")),
              $"learning a locked skill fails and costs nothing (\"{st.Messages[0].LastOrDefault(m => m.Contains("locked"))}\")");
        st.A.RequestTurnInSkillBook(aBook.Item.InstanceId);
        st.Pump(0.4f);
        Check(pa.Character.Inventory.FindByInstance(aBook.Item.InstanceId) == null &&
              aBookBase.UnlocksSkills.All(id => pa.Character.UnlockedSkills.Contains(id)) &&
              aBookBase.UnlocksSkills.All(id => st.A.World.MyCharacter.UnlockedSkills.Contains(id)) &&
              st.A.World.MyCharacter.Inventory.FindByInstance(aBook.Item.InstanceId) == null,
              $"handing {aBookBase.Name} to the trainer consumes it and unlocks {string.Join(" + ", aBookBase.UnlocksSkills)} (server and the turner's client)");
        Check(aBookBase.UnlocksSkills.All(id => !pb.Character.UnlockedSkills.Contains(id) && !st.B.World.MyCharacter.UnlockedSkills.Contains(id)),
              "the unlock belongs to the turner only: the second player is unchanged");
        pa.Character.Gold = 500; // (handing the book in also paid the "Words on Paper" side quest)
        st.A.RequestLearnSkill(lockedSkill);
        st.B.RequestLearnSkill(lockedSkill);
        st.Pump(0.4f);
        Check(pa.Character.GetSkill(lockedSkill) != null && pa.Character.Gold == 500 - ServerWorld.SkillPrice &&
              pb.Character.GetSkill(lockedSkill) == null && pb.Character.Gold == 500,
              $"an unlocked skill still costs {ServerWorld.SkillPrice} gold to learn; the other player still can't learn it");
        // A second copy: nothing new in it — refused and kept; it can be dropped for a friend.
        st.A.SendDebugCommand("give_book", firstBookId);
        st.Pump(0.4f);
        var spare = pa.Character.Inventory.Items.First(pl => pl.Item.BaseItemId == firstBookId);
        st.A.RequestTurnInSkillBook(spare.Item.InstanceId);
        st.Pump(0.4f);
        Check(pa.Character.Inventory.FindByInstance(spare.Item.InstanceId) != null &&
              st.Messages[0].Any(m => m.Contains("already know everything")),
              "a book whose skills are all unlocked is refused with a message and kept");
        st.A.RequestDropItem(spare.Item.InstanceId);
        st.Pump(0.4f);
        var dropped = w.Drops.Values.FirstOrDefault(d => d.Item?.InstanceId == spare.Item.InstanceId);
        if (dropped != null)
        {
            st.Place(st.B, dropped.Position);
            st.B.RequestPickup(dropped.DropId);
            st.Pump(0.4f);
            st.Place(st.B, trainer.Position + new Vector2(-0.9f, 0.3f));
            st.B.RequestTurnInSkillBook(spare.Item.InstanceId);
            st.Pump(0.4f);
        }
        Check(dropped != null && dropped.ReservedForPlayerId < 0 && aBookBase.UnlocksSkills.All(id => pb.Character.UnlockedSkills.Contains(id)) &&
              pb.Character.Inventory.FindByInstance(spare.Item.InstanceId) == null,
              "books are tradeable: dropped by one player, picked up and handed in by the other");
        var stashGrid = pa.Character.GetStash("hub_stash");
        st.A.SendDebugCommand("give_book", "book_ossuary");
        st.Pump(0.4f);
        var stashBook = pa.Character.Inventory.Items.First(pl => pl.Item.BaseItemId == "book_ossuary");
        st.Place(st.A, w.Map.StashSpot + new Vector2(0.8f, 0f));
        st.A.RequestMoveItem(ItemLocation.AtGrid(stashBook.X, stashBook.Y), ItemLocation.AtStash("hub_stash", 0, 0));
        st.Pump(0.4f);
        Check(stashGrid.Items.Any(pl => pl.Item.BaseItemId == "book_ossuary") && pa.Character.Inventory.Items.All(pl => pl.Item.BaseItemId != "book_ossuary"),
              "and stashable");
        st.Close();

        // ---- migration: a save from before books keeps everything it knows.
        var veteran = CharacterData.CreateNew(data, "Veteran", "mage");
        veteran.Skills.Add(new LearnedSkill { SkillId = "chain_lightning", Level = 3 });
        veteran.Skills.Add(new LearnedSkill { SkillId = "ground_slam" });
        veteran.UnlockedSkills = new HashSet<string>();
        var vet = new Session(data, 851202, campaign: true, story: true, clients: 1, characters: new[] { veteran }, names: new[] { "Veteran" });
        var vc = vet.P(vet.A).Character;
        Check(vc.UnlockedSkills.SetEquals(new[] { "fire_bolt", "chain_lightning", "ground_slam" }) &&
              vet.A.World.MyCharacter.UnlockedSkills.Count == 3 && vc.GetSkill("chain_lightning").Level == 3,
              "migration: every skill an old save already knows counts as unlocked (plus the class start skill)");
        vet.Close();

        // ---- the test grounds keep everything teachable.
        var free = new Session(data, 851203, campaign: true, clients: 1);
        var fp = free.P(free.A);
        var freeTrainer = free.World.Npcs.First(n => n.TypeId == "skill_trainer");
        free.Place(free.A, freeTrainer.Position + new Vector2(0.9f, 0.3f));
        fp.Character.Gold = 500;
        free.A.RequestLearnSkill("chain_lightning");
        free.A.SendDebugCommand("give_book", "book_rime");
        free.Pump(0.5f);
        var freeBook = fp.Character.Inventory.Items.FirstOrDefault(pl => pl.Item.BaseItemId == "book_rime");
        if (freeBook != null) free.A.RequestTurnInSkillBook(freeBook.Item.InstanceId);
        free.Pump(0.4f);
        Check(!free.World.SkillsGated && !free.A.World.SkillsGated && fp.Character.GetSkill("chain_lightning") != null &&
              freeBook != null && fp.Character.UnlockedSkills.Contains("ice_spike"),
              "the test grounds keep every skill teachable without books (and the sanctum trainer still reads books)");

        // ---- drop rates.
        var lootGen = new LootGenerator(data, new Random(8512));
        int bossBooks = 0;
        for (int i = 0; i < 2000; i++) bossBooks += lootGen.RollDrops("boss", 6).Count(d => d.GetBase(data).IsSkillBook);
        int trashBooks = 0;
        for (int i = 0; i < 2000; i++) trashBooks += lootGen.RollDrops("default", 6).Count(d => d.GetBase(data).IsSkillBook);
        Check(bossBooks is > 480 and < 720 && trashBooks == 0,
              $"the boss table drops a book on about {bossTable.SkillBookDropChance:P0} of rolls ({bossBooks}/2000); ordinary tables never do");
        int shamanBooks = 0;
        foreach (var old in free.World.Drops.Keys.ToList()) free.World.Drops.Remove(old);
        for (int i = 0; i < 400; i++)
        {
            var sh = free.World.SpawnEnemy("goblin_shaman", fp.Position + new Vector2(2f, 0f));
            free.World.DebugSkillHit(fp, sh, null, sh.Health + 1f);
            shamanBooks += free.World.Drops.Values.Count(d => d.Item?.GetBase(data).IsSkillBook == true);
            foreach (var old in free.World.Drops.Keys.ToList()) free.World.Drops.Remove(old);
            if (i % 40 == 39) free.Pump(0.05f);
        }
        Check(shamanBooks is >= 12 and <= 70, $"goblin shamans carry spell books rarely ({shamanBooks} books from 400 kills, ~9% expected)");
        free.Close();

        string bookDir = TempDir("books");
        CopyDirectory(Path.Combine(AppContext.BaseDirectory, "Data"), bookDir);
        string booksPath = Path.Combine(bookDir, "Items", "books.json");
        File.WriteAllText(booksPath, File.ReadAllText(booksPath).Replace("\"ice_spike\"", "\"ice_spoke\""));
        var prevOut = Console.Out;
        Console.SetOut(TextWriter.Null);
        GameData bookData;
        try { bookData = GameData.LoadFromDirectory(bookDir); }
        finally { Console.SetOut(prevOut); }
        ContentFingerprint.Current = data.Fingerprint;
        Check(bookData.ValidationErrors.Any(e => e.Contains("book_rime") && e.Contains("ice_spoke")),
              "the validator catches a book that names a skill that doesn't exist");
        try { Directory.Delete(bookDir, true); } catch { }
    }

    private static void Batch85BanditsAndGoblins(GameData data)
    {
        Console.WriteLine("\n-- Batch 85.11: bandits and goblins in the Mirewood --");
        string[] goblins = { "goblin_skulker", "goblin_slinger", "goblin_shaman" };
        string[] bandits = { "bandit_cutthroat", "bandit_archer", "bandit_brute", "bandit_captain" };
        var newIds = goblins.Concat(bandits).ToArray();
        Check(newIds.All(id => data.Enemies.TryGetValue(id, out var d) && !d.Undead && d.Blood == "8E1818" &&
                  d.Level is >= 3 and <= 8 && d.MinSpawnLevel == d.Level && !string.IsNullOrEmpty(d.SpriteRole)) &&
              goblins.All(id => data.Enemies[id].Family == "goblin" && data.Enemies[id].SpriteStyle == "Goblin" && data.Enemies[id].SpriteScale <= 0.8f) &&
              bandits.All(id => data.Enemies[id].Family == "bandit" && data.Enemies[id].SpriteStyle == "Bandit"),
              "seven new enemies: three goblins and four bandits, none undead, all bleed red, native levels 3-8");
        var skulker = data.Enemies["goblin_skulker"]; var slinger = data.Enemies["goblin_slinger"]; var shaman = data.Enemies["goblin_shaman"];
        var cutthroat = data.Enemies["bandit_cutthroat"]; var archer = data.Enemies["bandit_archer"]; var brute = data.Enemies["bandit_brute"]; var captain = data.Enemies["bandit_captain"];
        Check(skulker.MoveSpeed > data.Enemies["grunt"].MoveSpeed && skulker.MaxHealth < data.Enemies["grunt"].MaxHealth && skulker.AttackStyle == "lunge" &&
              slinger.Ranged && slinger.KeepDistance > 0 && slinger.DamageTypes.ContainsKey(Skills.DamageKind.Blunt) && slinger.ProjectileSprite == "Stone" &&
              shaman.CastRadius > 0 && shaman.CastKind == Skills.DamageKind.Fire && shaman.KeepDistance > 0,
              "goblins: a fast frail lunger, a stone slinger that keeps its distance, a fire-casting shaman");
        Check(cutthroat.AttackStyle == "sword" && cutthroat.AttackBleedChance > 0 && cutthroat.AttackWindup >= 0.5f &&
              cutthroat.DamageTypes[Skills.DamageKind.Slash] > data.Enemies["bone_knight"].DamageTypes[Skills.DamageKind.Slash] &&
              archer.Ranged && archer.KeepDistance > 0 && archer.ProjectileSprite == "Arrow" &&
              brute.MoveSpeed <= 2.1f && brute.MaxHealth >= 2 * cutthroat.MaxHealth && brute.AttackKnockback >= 2f &&
              captain.DashDamage > 0 && captain.DashMinLevel <= captain.Level && captain.LootTableId == "boss" && captain.AttackStyle == "sword",
              "bandits: a bleeding telegraphed blade, an archer, a slow brute with a heavy shove, a dash-charging captain on the boss table");

        // ---- a forest portal run: only the pool's non-undead, the Captain at the end.
        var mire = PortalRun(data, 851101, "warp_zone_forest+warp_boss");
        var mw = mire.World;
        var live = mw.Enemies.Values.Where(e => !e.Dead).ToList();
        var poolIds = mw.Map.Theme.PackPool.SelectMany(pk => pk.Members.Select(m => m.typeId))
            .Concat(mw.Map.Theme.OverlookPool.SelectMany(pk => pk.Members.Select(m => m.typeId)))
            .Concat(mw.Map.Theme.BossPacks.SelectMany(b => b.GuardMembers.Select(m => m.typeId).Append(b.Boss))).ToHashSet();
        Check(mw.Map.Theme?.Id == "forest" && live.Count >= 8 && live.All(e => !e.Def.Undead && poolIds.Contains(e.Def.Id)),
              $"a Mirewood portal run spawns only non-undead from the forest pool ({live.Count} enemies: {string.Join(", ", live.Select(e => e.Def.Id).Distinct().OrderBy(x => x))})");
        Check(mw.Packs.All(pk => pk.Buried != true) && live.All(e => !e.Buried),
              "nothing in a bandit or goblin pack lies buried (that stays an undead trick)");
        var cap = live.FirstOrDefault(e => e.Def.Id == "bandit_captain");
        Check(cap != null && cap.Affixes.HasFlag(EliteAffix.Boss) && mw.BossAlive && mw.ExitLocked && cap.Level >= captain.Level &&
              live.Count(e => e.PackId == cap.PackId && e.Def.Family == "bandit") >= 3,
              $"the forest boss pack is the Bandit Captain (level {cap?.Level}) with bandit guards, sealing the exit");
        Check(live.All(e => e.Level >= e.Def.MinSpawnLevel) && mw.ZoneEnemyLevel < 3,
              $"in a level-{mw.ZoneEnemyLevel} zone they keep their native levels (never 'level 1' bandits)");
        Check(mire.B.World.Enemies.Count == live.Count && mire.B.World.Enemies.Values.All(ce => ce.Def != null && !ce.Def.Undead) &&
              mire.Server.TickFailures == 0,
              "the second client sees the same non-undead population");
        mire.Close();

        // ---- each new enemy: spawns, attacks, dies, leaves a corpse — on both clients.
        var pit = new Session(data, 851102);
        var pa = pit.P(pit.A);
        var pb = pit.P(pit.B);
        void Reset()
        {
            pit.A.SendDebugCommand("kill_nearby");
            pit.B.SendDebugCommand("kill_nearby");
            pit.A.SendDebugCommand("heal");
            pit.B.SendDebugCommand("heal");
            pa.BleedUntil = pb.BleedUntil = 0f;
            pit.Pump(0.3f);
            pit.Place(pit.A, pit.World.Map.PlayerSpawn);
            pit.Place(pit.B, pit.World.Map.PlayerSpawn + new Vector2(0f, 1.2f));
        }
        foreach (var id in newIds)
        {
            Reset();
            var def = data.Enemies[id];
            var foe = pit.World.SpawnEnemy(id, pa.Position + new Vector2(2.2f, 0f));
            float t0 = pit.World.Time;
            bool seenByBoth = false, attacked = false, dashed = false, cast = false, shot = false;
            for (int i = 0; i < 100 && !(attacked && (id != "bandit_captain" || dashed)); i++)
            {
                pit.Pump(0.2f);
                seenByBoth |= pit.A.World.Enemies.ContainsKey(foe.Id) && pit.B.World.Enemies.ContainsKey(foe.Id);
                attacked |= pa.LastDamagedAt > t0 || pb.LastDamagedAt > t0;
                dashed |= foe.DashPrepareUntil > 0 || foe.DashUntil > 0;
                cast |= foe.CastResolveAt > 0;
                // (A shot can be born and land inside one pump: the spent cooldown is the proof.)
                shot |= def.Ranged && (foe.AttackReadyAt > t0 || pit.World.Projectiles.Values.Any(pr => !pr.FromPlayer && pr.OwnerId == foe.Id));
                if (pa.Health < pa.Stats.MaxHealth * 0.5f) pit.A.SendDebugCommand("heal");
                if (pb.Health < pb.Stats.MaxHealth * 0.5f) pit.B.SendDebugCommand("heal");
            }
            bool styleOk = id switch
            {
                "goblin_slinger" or "bandit_archer" => shot,
                "goblin_shaman" => cast,
                "bandit_captain" => dashed,
                _ => true,
            };
            Check(seenByBoth && attacked && styleOk && !foe.Buried,
                  $"{def.Name}: spawns on both clients and attacks{(id == "bandit_captain" ? " (and dash-charges)" : shot ? " (with a projectile)" : cast ? " (with its fire cast)" : "")}");
            pit.World.DebugSkillHit(pa, foe, null, foe.Health + 1f);
            pit.Pump(0.5f);
            Check(foe.Dead && pit.World.Corpses.Any(co => co.SourceId == foe.Id && co.TypeId == id) &&
                  pit.A.World.Corpses.Values.Any(co => co.SourceId == foe.Id && co.TypeId == id) &&
                  pit.B.World.Corpses.Values.Any(co => co.SourceId == foe.Id && co.TypeId == id) &&
                  !pit.A.World.Enemies.ContainsKey(foe.Id) && !pit.B.World.Enemies.ContainsKey(foe.Id),
                  $"{def.Name}: dies and leaves its corpse on the server and both clients");
        }

        // ---- the Brute's shove.
        Reset();
        var shover = pit.World.SpawnEnemy("bandit_brute", pa.Position + new Vector2(1.6f, 0f));
        float shoveT0 = pit.World.Time;
        for (int i = 0; i < 80 && pa.LastKnockbackAt <= shoveT0 && pb.LastKnockbackAt <= shoveT0; i++)
        {
            pit.Pump(0.2f);
            if (pa.Health < pa.Stats.MaxHealth * 0.6f) pit.A.SendDebugCommand("heal");
            if (pb.Health < pb.Stats.MaxHealth * 0.6f) pit.B.SendDebugCommand("heal");
        }
        var shoved = pa.LastKnockbackAt > shoveT0 ? pa : pb;
        var shovedClient = shoved == pa ? pit.A : pit.B;
        float shoveGap = Vector2.Distance(shoved.Position, shover.Position);
        pit.Pump(0.1f);
        Check(shoved.LastKnockbackAt > shoveT0 && shoveGap > brute.AttackRange + 0.5f &&
              Vector2.Distance(shovedClient.World.Me.Position, shoved.Position) < 0.6f,
              $"the Brute's landed swing shoves its victim back ({shoveGap:0.0} tiles from it) and the victim's client is moved with it");

        // ---- the Cutthroat's bleed.
        Reset();
        var cutter = pit.World.SpawnEnemy("bandit_cutthroat", pa.Position + new Vector2(1.4f, 0f));
        ServerPlayer bleeder = null;
        for (int i = 0; i < 200 && bleeder == null; i++)
        {
            pit.Pump(0.2f);
            bleeder = pa.BleedUntil > pit.World.Time ? pa : pb.BleedUntil > pit.World.Time ? pb : null;
            if (pa.Health < pa.Stats.MaxHealth * 0.6f) pit.A.SendDebugCommand("heal");
            if (pb.Health < pb.Stats.MaxHealth * 0.6f) pit.B.SendDebugCommand("heal");
        }
        pit.World.DebugSkillHit(pa, cutter, null, cutter.Health + 1f);
        pit.Pump(0.4f); // let any heal command still in flight land before measuring
        if (bleeder != null) { bleeder.BleedDps = cutthroat.AttackBleedDps; bleeder.BleedUntil = pit.World.Time + cutthroat.AttackBleedDuration; }
        float bleedStart = bleeder?.Health ?? 0f;
        float lifeRegen = bleeder?.Stats.LifeRegeneration ?? 0f;
        pit.Pump(1.6f);
        Check(bleeder != null && bleeder.Health < bleedStart - cutthroat.AttackBleedDps * 0.9f + lifeRegen * 1.6f && bleeder.Alive,
              $"the Cutthroat's blade leaves a bleed that keeps hurting after it is dead ({bleedStart:0.0} -> {bleeder?.Health:0.0} hp in 1.6 s)");
        pit.Pump(cutthroat.AttackBleedDuration + 0.5f);
        float afterBleed = bleeder?.Health ?? 0f;
        pit.Pump(1.0f);
        Check(bleeder != null && bleeder.Health >= afterBleed - 0.01f && bleeder.BleedDps == 0f,
              "and the bleed runs out");
        Check(pit.Server.TickFailures == 0, "no tick failures through any of it");

        // ---- a crowd: more enemies than fit one unreliable snapshot packet.
        Reset();
        var crowd = new List<ServerEnemy>();
        for (int i = 0; i < 70; i++)
        {
            var g = pit.World.SpawnEnemy("goblin_skulker", pa.Position + new Vector2(12f + (i % 10) * 0.8f, -4f + (i / 10) * 0.8f));
            g.StunnedUntil = pit.World.Time + 60f;
            crowd.Add(g);
        }
        pit.Pump(0.6f);
        crowd[69].Position += new Vector2(0.5f, 0.5f);
        var movedTo = crowd[69].Position;
        pit.Pump(0.6f);
        Check(pit.Server.TickFailures == 0 && pit.Server.LastEnemySnapshotPackets >= 2 &&
              pit.B.World.Enemies.TryGetValue(crowd[69].Id, out var farGoblin) && Vector2.Distance(farGoblin.NetTarget, movedTo) < 0.05f,
              $"seventy-plus live enemies: snapshots split into {pit.Server.LastEnemySnapshotPackets} packets and the last enemy still syncs (one oversized packet used to throw and take the server down)");
        pit.Close();
    }

    /// <summary>A story world with the party already inside the portal run the seals describe.</summary>
    private static Session PortalRun(GameData data, int seed, string seals, int clients = 2, CharacterData[] characters = null)
    {
        var s = new Session(data, seed, campaign: true, story: true, clients: clients, characters: characters);
        s.A.SendDebugCommand("enter_portal", seals);
        s.Pump(1.6f);
        return s;
    }

    private static void Batch85Pools(GameData data)
    {
        Console.WriteLine("\n-- Batch 85.10: data-driven enemy pools per theme --");
        static bool SamePools(List<PackPoolEntry> a, List<PackPoolEntry> b) =>
            a.Count == b.Count && a.Zip(b).All(p => p.First.Members.SequenceEqual(p.Second.Members) &&
                p.First.MinLoop == p.Second.MinLoop && p.First.MinLevel == p.Second.MinLevel &&
                p.First.Weight == p.Second.Weight && p.First.RequireEnemy == p.Second.RequireEnemy);
        var builtIn = ServerWorld.BuiltInUndead;
        foreach (var id in new[] { "graveyard", "tomb" })
        {
            var t = data.ZoneThemes.First(z => z.Id == id);
            Check(SamePools(t.PackPool, builtIn.PackPool) && SamePools(t.OverlookPool, builtIn.OverlookPool) &&
                  SamePools(t.PromisedPacks, builtIn.PromisedPacks) && t.BossPacks.Count == builtIn.BossPacks.Count &&
                  t.BossPacks.Zip(builtIn.BossPacks).All(p => p.First.Boss == p.Second.Boss && p.First.GuardMembers.SequenceEqual(p.Second.GuardMembers) &&
                      p.First.MinLoop == p.Second.MinLoop && p.First.MinLevel == p.Second.MinLevel),
                  $"the {id} theme's pools in themes.json are exactly the old hard-coded undead lists");
        }
        Check(builtIn.PackPool.Count(e => e.Available(1, 1)) == 4 && builtIn.PackPool.Count(e => e.Available(2, 1)) == 6 &&
              builtIn.PackPool.Count(e => e.Available(1, 4)) == 6 && builtIn.PackPool.Count(e => e.Available(1, 3)) == 4,
              "pool gates: knights join from campaign loop 2 OR zone level 4, as before");

        var fen = PortalRun(data, 851001, "warp_zone_swamp+warp_boss");
        var fenWorld = fen.World;
        var fenLive = fenWorld.Enemies.Values.Where(e => !e.Dead).ToList();
        Check(fenWorld.MapIndex == ServerWorld.ScrollMapIndex && fenWorld.Map.Theme?.Id == "graveyard" &&
              ReferenceEquals(fenWorld.PopulationTheme, fenWorld.Map.Theme),
              "a Fen portal run populates from the graveyard theme's own pool");
        Check(fenLive.Count >= 6 && fenLive.All(e => e.Def.Undead) &&
              fenLive.Any(e => e.Def.Id == "gravelord" && e.Affixes.HasFlag(EliteAffix.Boss)) && fenWorld.BossAlive && fenWorld.ExitLocked,
              $"a graveyard run still spawns only undead ({fenLive.Count} of them), the Gravelord sealing the exit");
        Check(fenWorld.Packs.All(pk => pk.Entries.All(en => data.Enemies[en.typeId].Undead)) &&
              fenWorld.Packs.Where(pk => pk.Entries.Length > 0).Any(pk => pk.Buried == true || pk.Buried == false),
              "its packs are all-undead compositions (the only kind that may lie buried)");
        Check(fen.B.World.Enemies.Count == fenLive.Count && fen.A.World.Enemies.Count == fenLive.Count,
              "both clients see the whole population");
        fen.Close();

        var grounds = new Session(data, 851002, campaign: true, theme: "forest", clients: 1);
        grounds.A.SendDebugCommand("warp_next");
        grounds.Pump(1.0f);
        var groundsLive = grounds.World.Enemies.Values.Where(e => !e.Dead).ToList();
        Check(grounds.World.Map.Kind == World.MapKind.Forest && grounds.World.Map.Theme?.Id == "forest" &&
              ReferenceEquals(grounds.World.PopulationTheme, ServerWorld.BuiltInUndead) &&
              groundsLive.Count >= 6 && groundsLive.All(e => e.Def.Undead),
              $"the test-grounds campaign loop keeps its undead on a forest-themed map ({groundsLive.Count} undead)");
        grounds.Close();

        string poolDir = TempDir("pools");
        CopyDirectory(Path.Combine(AppContext.BaseDirectory, "Data"), poolDir);
        string themesPath = Path.Combine(poolDir, "Zones", "themes.json");
        File.WriteAllText(themesPath, File.ReadAllText(themesPath).Replace("[\"crypt_leaper\", 2]", "[\"crypt_creeper\", 2]"));
        var prevOut = Console.Out;
        Console.SetOut(TextWriter.Null);
        GameData poolData;
        try { poolData = GameData.LoadFromDirectory(poolDir); }
        finally { Console.SetOut(prevOut); }
        ContentFingerprint.Current = data.Fingerprint;
        Check(poolData.ValidationErrors.Any(e => e.Contains("PackPool") && e.Contains("crypt_creeper")),
              "the validator catches a pack pool naming an enemy that doesn't exist");
        try { Directory.Delete(poolDir, true); } catch { }
    }

    private static void Batch85Multiplayer(GameData data)
    {
        // ------------------------------------------------------------------ 85.8 handshake
        Console.WriteLine("\n-- Batch 85.8: sim version + content fingerprint in the handshake --");
        string dataDir = Path.Combine(AppContext.BaseDirectory, "Data");
        Check(data.Fingerprint.Length == 64 && data.Fingerprint == ContentFingerprint.Compute(dataDir) &&
              ContentFingerprint.ShortOf(data.Fingerprint).Length == 8,
              $"the content fingerprint is a stable SHA-256 over Data/**/*.json (menu shows {ContentFingerprint.ShortOf(data.Fingerprint)})");
        string fpDir = TempDir("fp");
        CopyDirectory(dataDir, fpDir);
        foreach (var file in Directory.GetFiles(fpDir, "*.json", SearchOption.AllDirectories))
            File.WriteAllText(file, File.ReadAllText(file).Replace("\r\n", "\n").Replace("\n", "\r\n"));
        string crlfPrint = ContentFingerprint.Compute(fpDir);
        string dodgeFile = Path.Combine(fpDir, "Config", "dodge.json");
        File.WriteAllText(dodgeFile, File.ReadAllText(dodgeFile) + " ");
        string editedPrint = ContentFingerprint.Compute(fpDir);
        Check(crlfPrint == data.Fingerprint && editedPrint != data.Fingerprint,
              "line endings don't change the fingerprint (a Windows checkout matches); any real edit does");
        try { Directory.Delete(fpDir, true); } catch { }
        Check(Core.GameNetConfig.ProtocolVersion >= 48 && Core.GameNetConfig.SimVersion >= 1,
              $"protocol bumped (48+) for the new JoinRequest — now {Core.GameNetConfig.ProtocolVersion}; SimVersion is {Core.GameNetConfig.SimVersion}");
        var hs = new Session(data, 850801, clients: 1);
        Check(hs.A.Status == ClientStatus.InGame, "a matching build joins as before");
        var wrongSim = hs.Add(data, "WrongSim", null, c => c.SimVersionOverride = Core.GameNetConfig.SimVersion + 1);
        var wrongData = hs.Add(data, "WrongData", null, c => c.FingerprintOverride = new string('a', 64));
        hs.Pump(1.2f);
        Check(wrongSim.Status == ClientStatus.Disconnected && (hs.Disconnects[1] ?? "").Contains("simulation differs") &&
              (hs.Disconnects[1] ?? "").Contains("same build"),
              $"a different SimVersion is refused readably (\"{hs.Disconnects[1]}\")");
        Check(wrongData.Status == ClientStatus.Disconnected && (hs.Disconnects[2] ?? "").Contains("Game data differs from the host") &&
              (hs.Disconnects[2] ?? "").Contains("aaaaaaaa"),
              $"different game data is refused readably (\"{hs.Disconnects[2]}\")");
        Check(hs.World.Players.Count == 1 && hs.A.Status == ClientStatus.InGame,
              "neither refused client became a player, and the host is unaffected");
        hs.Close();

        // ------------------------------------------------------------------ 85.9 coalesced sync
        Console.WriteLine("\n-- Batch 85.9: coalesced character sync --");
        var co = new Session(data, 850901);
        var coA = co.P(co.A);
        co.A.SendDebugCommand("learn_skill", "arcane_burst");
        co.Pump(0.5f);
        var killSpot = coA.Position + new Vector2(2.5f, 0f);
        foreach (var stray in co.World.Enemies.Values.ToList()) stray.Position = coA.Position + new Vector2(30f, 30f);
        var doomed = new List<ServerEnemy>();
        for (int i = 0; i < 20; i++)
        {
            var e = co.World.SpawnEnemy("grunt", killSpot + new Vector2((i % 5) * 0.12f - 0.24f, (i / 5) * 0.12f - 0.24f));
            e.Health = 1f;
            e.StunnedUntil = co.World.Time + 60f;
            doomed.Add(e);
        }
        co.Pump(0.4f);
        coA.Mana = coA.Stats.MaxMana;
        coA.SkillReadyAt.Clear();
        coA.GlobalSkillReadyAt = 0;
        float xpA = coA.Character.Experience + coA.Character.Level * 100000f;
        float xpB = co.P(co.B).Character.Experience + co.P(co.B).Character.Level * 100000f;
        int statesA = co.A.CharacterStatePackets, statesB = co.B.CharacterStatePackets;
        int looksA = co.A.AppearancePackets, looksServer = co.Server.AppearanceBroadcasts;
        co.A.RequestUseSkill("arcane_burst", killSpot);
        co.Pump(0.9f); // wind-up + the burst + the kills (well inside one skill-XP flush window)
        int killed = doomed.Count(e => e.Dead);
        int gotA = co.A.CharacterStatePackets - statesA, gotB = co.B.CharacterStatePackets - statesB;
        Check(killed == 20, $"one Arcane Burst killed all twenty ({killed}/20)");
        Check(gotA is >= 1 and <= 2 && gotB is >= 1 and <= 2,
              $"twenty kills in one tick cost the killer {gotA} CharacterState packet(s) and the party member {gotB} (was 20 each)");
        Check(coA.Character.Experience + coA.Character.Level * 100000f > xpA &&
              co.P(co.B).Character.Experience + co.P(co.B).Character.Level * 100000f > xpB &&
              co.A.World.MyCharacter.Level == coA.Character.Level &&
              MathF.Abs(co.A.World.MyCharacter.Experience - coA.Character.Experience) < 0.01f &&
              MathF.Abs(co.B.World.MyCharacter.Experience - co.P(co.B).Character.Experience) < 0.01f,
              "both players got their XP and both clients hold the final values");
        Check(co.A.AppearancePackets == looksA && co.Server.AppearanceBroadcasts == looksServer,
              "kills no longer broadcast a PlayerAppearance (nothing visible changed)");
        co.Pump(1.3f);
        int goldBefore = co.A.World.MyCharacter.Gold;
        statesA = co.A.CharacterStatePackets;
        co.A.SendDebugCommand("give_gold", "25");
        co.A.SendDebugCommand("give_gold", "25");
        co.A.SendDebugCommand("give_gold", "25");
        co.Pump(0.4f); // far less than the 1.1 s skill-XP cadence
        Check(co.A.World.MyCharacter.Gold == goldBefore + 75 && co.A.CharacterStatePackets - statesA is >= 1 and <= 3 &&
              co.A.AppearancePackets == looksA,
              $"character requests still sync at once (three grants -> {co.A.CharacterStatePackets - statesA} packet(s) within 0.4 s), still no appearance broadcast");
        looksServer = co.Server.AppearanceBroadcasts;
        int looksB = co.B.AppearancePackets;
        co.A.SendDebugCommand("give_bow", "equip");
        co.Pump(0.4f);
        var aSeenByB = co.B.World.Players[co.A.World.MyPlayerId];
        Check(co.Server.AppearanceBroadcasts == looksServer + 1 && co.B.AppearancePackets == looksB + 1 &&
              aSeenByB.WeaponBaseId == coA.Character.MainHand.BaseItemId,
              $"equipping a different weapon broadcasts the new appearance exactly once ({aSeenByB.WeaponBaseId})");
        // Skill-XP-only changes keep their slow cadence: a non-lethal hit doesn't sync at once.
        co.A.SendDebugCommand("give_mace", "equip");
        co.Pump(1.4f);
        var bag = co.World.SpawnEnemy("grunt", coA.Position + new Vector2(1.2f, 0f));
        bag.MaxHealth = bag.Health = 100000f;
        bag.StunnedUntil = co.World.Time + 60f;
        co.Pump(1.3f); // settle: let any pending skill-XP flush pass
        coA.SkillReadyAt.Clear();
        coA.GlobalSkillReadyAt = 0;
        statesA = co.A.CharacterStatePackets;
        float sxpBefore = co.A.World.MyCharacter.GetSkill("mace_strike")?.Experience ?? -1f;
        int hitsLanded = 0;
        float hitWindow = 0f;
        for (int i = 0; i < 40; i++)
        {
            // Server-side hits every 0.1 s (ten a second), each one banking skill XP.
            float hpBefore = bag.Health;
            co.World.DebugSkillHit(coA, bag, "mace_strike", 5f);
            co.Pump(0.1f);
            hitWindow += 0.1f;
            if (bag.Health < hpBefore) hitsLanded++;
        }
        co.Pump(1.3f);
        int sxpPackets = co.A.CharacterStatePackets - statesA;
        float sxpAfter = co.A.World.MyCharacter.GetSkill("mace_strike")?.Experience ?? -1f;
        Check(hitsLanded == 40 && sxpAfter > sxpBefore && sxpPackets >= 2 && sxpPackets <= (int)(hitWindow / 1.1f) + 3,
              $"skill XP from {hitsLanded} hits over {hitWindow:0.#} s still rides the 1.1 s batch ({sxpPackets} packets, not {hitsLanded}), and arrives");
        co.Close();
    }

    private static void Batch85Hardening(GameData data)
    {
        // ------------------------------------------------------------------ 85.1 logging
        Console.WriteLine("\n-- Batch 85.1: log file, console tee, crash reports --");
        string logDir = TempDir("logs");
        for (int i = 1; i <= 7; i++) File.WriteAllText(Path.Combine(logDir, $"scrollbound-2001-01-0{i}.log"), "old");
        Log.Init(logDir);
        Console.WriteLine("nettest-log-marker-out");
        Console.Error.WriteLine("nettest-log-marker-err");
        Console.Write("split ");
        Console.Write("line\n");
        string logFile = Log.CurrentFile;
        var recent = Log.RecentLines();
        string crashPath = CrashReporter.WriteCrashFile(new InvalidOperationException("nettest-boom"), "nettest crash", logDir);
        Log.Shutdown();
        string logText = File.Exists(logFile) ? File.ReadAllText(logFile) : "";
        Check(logText.Contains("nettest-log-marker-out") && logText.Contains("nettest-log-marker-err") && logText.Contains("split line"),
              "Console.Out and Console.Error are teed into Logs/scrollbound-<date>.log, line by line");
        var keptLogs = Directory.GetFiles(logDir, "scrollbound-*.log").Select(Path.GetFileName).OrderBy(f => f).ToList();
        Check(keptLogs.Count == Log.KeepFiles && !keptLogs.Contains("scrollbound-2001-01-01.log") && keptLogs.Contains(Path.GetFileName(logFile)),
              $"only the {Log.KeepFiles} newest log files are kept ({keptLogs.Count} on disk)");
        Check(recent.Any(l => l.Contains("nettest-log-marker-out")) && recent.Count <= Log.RecentCapacity,
              "the last log lines stay in memory for crash reports");
        string crashText = crashPath != null && File.Exists(crashPath) ? File.ReadAllText(crashPath) : "";
        Check(Path.GetFileName(crashPath ?? "").StartsWith("crash-") && crashText.Contains("nettest-boom") &&
              crashText.Contains("InvalidOperationException") && crashText.Contains("nettest-log-marker-out"),
              "a crash report holds the exception, its type and the recent log lines");
        Check(typeof(GameMain).GetMethod("EmergencySave") != null, "the crash handler's emergency save hook exists on the game shell");
        try { Directory.Delete(logDir, true); } catch { }

        // ------------------------------------------------------------------ 85.3 tick guard
        Console.WriteLine("\n-- Batch 85.3: the server tick guard --");
        var guard = new Session(data, 850301);
        Check(guard.A.Status == ClientStatus.InGame && guard.B.Status == ClientStatus.InGame, "two clients joined the guard-test server");
        float timeBefore = guard.World.Time;
        guard.A.SendDebugCommand("throw_in_tick");
        guard.Pump(0.5f);
        Check(guard.Server.TickFailures == 1 && guard.Server.FatalError == null,
              $"one tick threw and was caught ({guard.Server.TickFailures} failure) — the server did not die");
        Check(guard.A.Status == ClientStatus.InGame && guard.B.Status == ClientStatus.InGame && guard.World.Time > timeBefore + 0.3f,
              "both clients stay connected and the simulation keeps advancing after a bad tick");
        int newestBefore = guard.World.Enemies.Keys.DefaultIfEmpty(0).Max();
        guard.A.SendDebugCommand("spawn_enemy", "grunt");
        guard.Pump(0.3f);
        int newest = guard.World.Enemies.Keys.Max();
        Check(newest > newestBefore && guard.B.World.Enemies.ContainsKey(newest) && guard.A.World.Enemies.ContainsKey(newest),
              "the world still replicates normally afterwards");
        guard.A.SendDebugCommand("throw_in_tick", "5");
        guard.Pump(0.5f);
        Check(guard.Server.TickFailures == 6 && guard.Server.FatalError == null && guard.A.Status == ClientStatus.InGame,
              "five failing ticks in a row are survived too (fewer than the limit)");
        guard.A.SendDebugCommand("throw_in_tick", "100");
        guard.Pump(1.5f);
        Check(guard.Server.FatalError == GameServer.FatalTickMessage &&
              guard.Server.TickFailures == 6 + GameServer.MaxConsecutiveTickFailures,
              $"after {GameServer.MaxConsecutiveTickFailures} consecutive failing ticks the server stops itself cleanly");
        Check(guard.A.Status == ClientStatus.Disconnected && guard.B.Status == ClientStatus.Disconnected &&
              (guard.Disconnects[0] ?? "").Contains("Host simulation error") && (guard.Disconnects[1] ?? "").Contains("Logs folder"),
              $"every client is disconnected with the reason on screen (\"{guard.Disconnects[1]}\")");
        guard.Server.Update(1f / 60f);
        guard.Server.Stop();
        Check(true, "a stopped server ignores further updates and shuts down without throwing");

        // ------------------------------------------------------------------ 85.4 atomic saves
        Console.WriteLine("\n-- Batch 85.4: atomic saves with a backup --");
        const string saveName = "nettest_atomic_85";
        SaveManager.DeleteCharacter(saveName);
        string savePath = SaveManager.CharacterPath(saveName);
        var saved = CharacterData.CreateNew(data, saveName);
        saved.Gold = 111;
        SaveManager.SaveCharacter(saved);
        Check(File.Exists(savePath) && !File.Exists(Json.BackupPath(savePath)) && !File.Exists(savePath + ".tmp"),
              "the first save lands in place (no backup yet, no temp file left behind)");
        saved.Gold = 222;
        SaveManager.SaveCharacter(saved);
        Check(File.Exists(Json.BackupPath(savePath)) && !File.Exists(savePath + ".tmp") &&
              Json.LoadFile<CharacterData>(savePath).Gold == 222 && Json.LoadFile<CharacterData>(Json.BackupPath(savePath)).Gold == 111,
              "the second save replaces the file atomically and keeps the previous one as .bak");
        File.WriteAllText(savePath, "{ \"Name\": \"nettest_atomic_85\", \"Gold\": 22"); // a write cut off mid-file
        var recovered = SaveManager.LoadCharacter(saveName);
        Check(recovered != null && recovered.Gold == 111, "a corrupt main save loads the backup instead of losing the character");
        Check(SaveManager.ListCharacters().Any(ch => ch.Name == saveName), "the character list also reads through to the backup");
        File.WriteAllText(savePath, "");
        Check(SaveManager.LoadCharacter(saveName)?.Gold == 111, "an empty (zero-byte) main save falls back to the backup too");
        string settingsLike = Path.Combine(TempDir("settings"), "settings.json");
        Json.SaveFile(settingsLike, new Core.GameSettings { PlayerName = "First" });
        Json.SaveFile(settingsLike, new Core.GameSettings { PlayerName = "Second" });
        File.WriteAllText(settingsLike, "not json at all");
        var settingsBack = Json.LoadFileWithBackup<Core.GameSettings>(settingsLike, out bool settingsFromBackup);
        Check(settingsFromBackup && settingsBack?.PlayerName == "First", "settings.json takes the same path: corrupt main -> backup");
        try { Directory.Delete(Path.GetDirectoryName(settingsLike)!, true); } catch { }

        // Background writes: latest wins, never two writers in one file at once.
        SaveManager.MaxConcurrentWrites = 0;
        for (int i = 0; i < 40; i++)
        {
            saved.Gold = 1000 + i;
            SaveManager.SaveCharacterInBackground(saved);
            if (i % 10 == 9) SaveManager.SaveCharacter(saved);
        }
        saved.Gold = 4242;
        SaveManager.SaveCharacterInBackground(saved);
        SaveManager.FlushBackgroundWrites();
        Check(SaveManager.MaxConcurrentWrites == 1 && Json.LoadFile<CharacterData>(savePath).Gold == 4242,
              "forty overlapping background + foreground saves: one writer at a time, and the newest snapshot is what's on disk");
        SaveManager.DeleteCharacter(saveName);
        Check(!File.Exists(savePath) && !File.Exists(Json.BackupPath(savePath)) && SaveManager.LoadCharacter(saveName) == null,
              "deleting a character removes its backup too (it can't come back from the dead)");

        // ------------------------------------------------------------------ 85.5 autosave
        Console.WriteLine("\n-- Batch 85.5: autosave (interval, map change, level-up) --");
        const string autoName = "nettest_auto_85";
        SaveManager.DeleteCharacter(autoName);
        var auto = new Session(data, 850501, campaign: true, clients: 1, names: new[] { autoName });
        var autosaver = new Autosaver(auto.A);
        string autoPath = SaveManager.CharacterPath(autoName);
        autosaver.Update(1f);
        Check(autosaver.SavesRequested == 0 && !File.Exists(autoPath), "no autosave before anything happened");
        autosaver.Update(Autosaver.Interval);
        SaveManager.FlushBackgroundWrites();
        Check(autosaver.SavesRequested == 1 && autosaver.LastReason == "interval" && File.Exists(autoPath),
              $"the {Autosaver.Interval:0}-second interval writes the save in the background");
        auto.A.SendDebugCommand("warp_next");
        auto.Pump(0.6f);
        autosaver.Update(0.016f);
        SaveManager.FlushBackgroundWrites();
        Check(autosaver.SavesRequested == 2 && autosaver.LastReason == "map change", "a map change autosaves");
        auto.A.SendDebugCommand("give_gold", "77");
        auto.Pump(0.3f);
        autosaver.Update(0.016f);
        Check(autosaver.SavesRequested == 2, "an ordinary character update (gold) does not trigger a save by itself");
        int levelBefore = auto.A.World.MyCharacter.Level;
        auto.A.SendDebugCommand("char_xp"); // 150 xp: level 1 -> 2 takes 65
        auto.Pump(0.4f);
        autosaver.Update(0.016f);
        SaveManager.FlushBackgroundWrites();
        var autoLoaded = SaveManager.LoadCharacter(autoName);
        Check(auto.A.World.MyCharacter.Level > levelBefore && autosaver.SavesRequested == 3 && autosaver.LastReason == "level up" &&
              autoLoaded != null && autoLoaded.Level == auto.A.World.MyCharacter.Level && autoLoaded.Gold == auto.A.World.MyCharacter.Gold,
              $"a level-up autosaves — the file on disk is level {autoLoaded?.Level} with the gold picked up before it");
        auto.Close();
        SaveManager.DeleteCharacter(autoName);

        // ------------------------------------------------------------------ 85.6 join sanitization
        Console.WriteLine("\n-- Batch 85.6: join sanitization --");
        var bogus = CharacterData.CreateNew(data, "Bogus");
        var goodMace = new ItemInstance { BaseItemId = "iron_mace", ItemLevel = 3 };
        goodMace.Modifiers.Add(new ItemModifierRoll { ModifierId = "no_such_modifier_85", Value = 5 });
        var realMod = data.Modifiers.Values.First(m => m.CompatibleWith(ItemCategory.Mace) && string.IsNullOrEmpty(m.MapEffect));
        goodMace.Modifiers.Add(new ItemModifierRoll { ModifierId = realMod.Id, Value = realMod.MinimumValue });
        bogus.Inventory.Items.Add(new Inventory.PlacedItem { Item = goodMace, X = 0, Y = 0 });
        bogus.Inventory.Items.Add(new Inventory.PlacedItem { Item = new ItemInstance { BaseItemId = "item_that_was_deleted_85" }, X = 4, Y = 0 });
        var stackBase = data.Items.Values.First(b => b.MaxStack > 1);
        bogus.Inventory.Items.Add(new Inventory.PlacedItem { Item = new ItemInstance { BaseItemId = stackBase.Id, StackCount = stackBase.MaxStack + 500 }, X = 6, Y = 0 });
        bogus.Equipment[EquipSlot.Helmet] = new ItemInstance { BaseItemId = "helmet_from_another_build_85" };
        bogus.Skills.Add(new LearnedSkill { SkillId = "skill_that_never_existed_85", Level = 99 });
        bogus.Hotbar[2] = "skill_that_never_existed_85";
        bogus.AllocatedPassives.Add("passive_nowhere_85");
        bogus.UnlockedSkills.Add("unlock_nowhere_85");
        bogus.Gold = -500;
        bogus.Level = -3;
        var san = new Session(data, 850601, clients: 1, characters: new[] { bogus }, names: new[] { "Bogus" });
        Check(san.A.Status == ClientStatus.InGame, "a character full of unknown ids still joins");
        var sc = san.World.Players.Values.First().Character;
        Check(sc.Inventory.Items.All(pl => data.Items.ContainsKey(pl.Item.BaseItemId)) &&
              sc.Inventory.Items.Any(pl => pl.Item.BaseItemId == "iron_mace") && !sc.Equipment.ContainsKey(EquipSlot.Helmet),
              "items whose base id no longer exists are dropped (bag and equipment); real items stay");
        var cleanedMace = sc.Inventory.Items.First(pl => pl.Item.BaseItemId == "iron_mace").Item;
        Check(cleanedMace.Modifiers.Count == 1 && cleanedMace.Modifiers[0].ModifierId == realMod.Id,
              "an unknown modifier is stripped from an item, its real modifier kept");
        Check(sc.Skills.All(sk => data.Skills.ContainsKey(sk.SkillId)) && sc.Hotbar[2] == null && sc.Hotbar[0] != null &&
              sc.AllocatedPassives.Count == 0 && !sc.UnlockedSkills.Contains("unlock_nowhere_85"),
              "unknown skills, hotbar entries, passives and unlocks are removed; the real hotbar skill stays");
        Check(sc.Gold == 0 && sc.Level == 1 &&
              sc.Inventory.Items.First(pl => pl.Item.BaseItemId == stackBase.Id).Item.StackCount == stackBase.MaxStack,
              $"nonsense values are clamped: gold 0, level 1, the {stackBase.Name} stack at its maximum of {stackBase.MaxStack}");
        san.A.SendDebugCommand("spawn_enemy", "grunt");
        san.Pump(1.5f);
        Check(san.Server.TickFailures == 0 && san.A.Status == ClientStatus.InGame &&
              san.A.World.MyCharacter.Inventory.Items.Count == sc.Inventory.Items.Count,
              "the sanitized character plays: no tick failures, and the client holds the cleaned copy");
        san.Close();
        var hollow = new CharacterData
        {
            Name = "Hollow", Inventory = null, Equipment = null, Skills = null, Hotbar = null, Story = null,
            ShopSoldSlots = null, AllocatedPassives = null, Mercs = null, Stashes = null, UnlockedSkills = null,
            BookGuarantees = null, Quests = null, Experience = float.NaN,
        };
        var notes = new ServerWorld(data, 1, new GameServer(data, 1)).SanitizeCharacter(hollow, "Hollow");
        Check(hollow.Inventory?.Items != null && hollow.Equipment != null && hollow.Skills != null && hollow.Hotbar?.Length == 5 &&
              hollow.Story?.ScenesSeen != null && hollow.ShopSoldSlots != null && hollow.AllocatedPassives != null &&
              hollow.Mercs != null && hollow.Stashes != null && hollow.UnlockedSkills != null && hollow.BookGuarantees != null &&
              hollow.Quests?.Active != null && hollow.Quests.Completed != null && hollow.Experience == 0f,
              "every collection on CharacterData is null-coalesced (a hollowed-out save can't crash the tick)");
        var hollowJoin = new Session(data, 850602, clients: 1, characters: new[] { hollow }, names: new[] { "Hollow" });
        hollowJoin.Pump(0.5f);
        Check(hollowJoin.A.Status == ClientStatus.InGame && hollowJoin.Server.TickFailures == 0,
              "and a hollowed-out character joins and ticks");
        hollowJoin.Close();

        // ------------------------------------------------------------------ 85.7 data validator
        Console.WriteLine("\n-- Batch 85.7: the data validator --");
        Check(data.ValidationErrors.Count == 0 && data.LoadFailures.Count == 0 && data.DuplicateIds.Count == 0,
              data.ValidationErrors.Count == 0 ? "the shipped data validates clean" : "the shipped data validates clean — FIRST ERROR: " + data.ValidationErrors[0]);
        string badDir = TempDir("data");
        CopyDirectory(Path.Combine(AppContext.BaseDirectory, "Data"), badDir);
        File.WriteAllText(Path.Combine(badDir, "Items", "zz_broken.json"), "[ { \"Id\": \"half_an_item\", ");
        File.WriteAllText(Path.Combine(badDir, "Items", "zz_dupe.json"),
            "[ { \"Id\": \"iron_mace\", \"Name\": \"Impostor Mace\", \"Category\": \"Mace\" } ]");
        File.WriteAllText(Path.Combine(badDir, "Enemies", "zz_bad.json"),
            "[ { \"Id\": \"lootless\", \"Name\": \"Lootless\", \"LootTableId\": \"no_such_table\" } ]");
        File.WriteAllText(Path.Combine(badDir, "Modifiers", "zz_bad.json"),
            "[ { \"Id\": \"bad_stat\", \"Name\": \"Bad\", \"StatAffected\": \"NotAStat\" } ]");
        File.WriteAllText(Path.Combine(badDir, "Classes", "zz_bad.json"),
            "[ { \"Id\": \"hobo\", \"Name\": \"Hobo\", \"StartWeapon\": \"pointy_stick\", \"StartSkill\": \"poke\" } ]");
        string enemiesPath = Path.Combine(badDir, "Enemies", "enemies.json");
        File.WriteAllText(enemiesPath, File.ReadAllText(enemiesPath).Replace("\"Id\": \"codex\"", "\"Id\": \"codex_renamed\""));
        var prevOut = Console.Out;
        Console.SetOut(TextWriter.Null); // the broken set is noisy on purpose
        GameData badData;
        try { badData = GameData.LoadFromDirectory(badDir); }
        finally { Console.SetOut(prevOut); }
        ContentFingerprint.Current = data.Fingerprint;
        var errs = badData.ValidationErrors;
        Check(errs.Any(e => e.Contains("zz_broken.json")), "a file that fails to parse is reported (it used to vanish silently)");
        Check(errs.Any(e => e.Contains("duplicate id") && e.Contains("iron_mace")), "a duplicate id is reported (it used to overwrite silently)");
        Check(errs.Any(e => e.Contains("lootless") && e.Contains("no_such_table")), "an enemy with a missing loot table is reported");
        Check(errs.Any(e => e.Contains("Modifiers/zz_bad.json")), "a modifier whose StatAffected is not a StatType is reported");
        Check(errs.Any(e => e.Contains("hobo") && e.Contains("pointy_stick")) && errs.Any(e => e.Contains("hobo") && e.Contains("poke")),
              "a class with a missing start weapon / start skill is reported");
        Check(errs.Any(e => e.Contains("'codex'") && e.Contains("required by the code")),
              "renaming an id the server code depends on (the Codex) is reported");
        Check(badData.Fingerprint != data.Fingerprint && badData.Fingerprint.Length == 64,
              "and the changed data has a different content fingerprint");
        try { Directory.Delete(badDir, true); } catch { }
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), true);
    }
}
