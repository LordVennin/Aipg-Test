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
        Check(Core.GameNetConfig.ProtocolVersion == 48 && Core.GameNetConfig.SimVersion >= 1,
              $"protocol bumped to 48 for the new JoinRequest; SimVersion is {Core.GameNetConfig.SimVersion}");
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
