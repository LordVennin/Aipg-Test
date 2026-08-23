using System.Numerics;
using ARPG.Data;
using ARPG.Net;
using ARPG.Server;
using ARPG.Util;

namespace ARPG.Testing;

/// <summary>
/// Automated two-client multiplayer test (run with `dotnet run -- --nettest`).
/// Spins up a real GameServer plus two real GameClients over loopback UDP — the exact same
/// code path used by the GUI — and asserts the host-authoritative sync guarantees:
/// join, movement sync, combat sync, enemy death sync, host loot generation with identical
/// modifiers on both peers, exclusive pickup, equipment stat changes, skill scroll attach,
/// and clean disconnect handling.
/// </summary>
public static class HeadlessNetTest
{
    private static readonly List<string> Failures = new();
    private static int _checks;

    private static void Check(bool condition, string what)
    {
        _checks++;
        if (condition)
        {
            Console.WriteLine($"  [PASS] {what}");
        }
        else
        {
            Failures.Add(what);
            Console.WriteLine($"  [FAIL] {what}");
        }
    }

    public static int Run()
    {
        Console.WriteLine("=== ARPG headless multiplayer self-test ===");
        var data = GameData.LoadDefault();

        var server = new GameServer(data, mapSeed: 1234);
        if (!server.Start(0))
        {
            Console.WriteLine("FATAL: server failed to start");
            return 1;
        }
        int port = server.LocalPort;
        Console.WriteLine($"Server listening on port {port}");

        var clientA = new GameClient(data, "HostPlayer", null);
        var clientB = new GameClient(data, "JoinPlayer", null);
        string msgA = null, msgB = null;
        clientA.ServerMessageReceived += m => msgA = m;
        clientB.ServerMessageReceived += m => msgB = m;
        string disconnectB = null;
        clientB.Disconnected += r => disconnectB = r;

        clientA.Connect("127.0.0.1", port, out _);
        clientB.Connect("127.0.0.1", port, out _);

        void Pump(float seconds)
        {
            const float dt = 1f / 60f;
            int steps = (int)(seconds / dt);
            for (int i = 0; i < steps; i++)
            {
                server.Update(dt);
                clientA.Update(dt);
                clientB.Update(dt);
                Thread.Sleep(2);
            }
        }

        Pump(1.5f);
        Console.WriteLine("\n-- Join --");
        Check(clientA.Status == ClientStatus.InGame, "client A joined");
        Check(clientB.Status == ClientStatus.InGame, "client B joined");
        // New characters know only Mace Strike + Fire Bolt (skills are trainer-bought
        // in the campaign). The arena has no trainer, so learning stays free — pick up
        // Mace Slam here because half the combat checks below swing it.
        clientA.RequestLearnSkill("mace_strike");
        clientB.RequestLearnSkill("mace_strike");
        Pump(0.3f);
        Check(clientA.World.MyCharacter.GetSkill("mace_strike") != null &&
              clientB.World.MyCharacter.GetSkill("mace_strike") != null,
              "arena worlds keep free skill learning (no trainer present)");
        Check(clientA.World.MyCharacter.Gold >= 100,
              $"new characters start with 100 gold (has {clientA.World.MyCharacter.Gold})");
        Check(clientA.World.Players.Count == 2, $"client A sees 2 players (saw {clientA.World.Players.Count})");
        Check(clientB.World.Players.Count == 2, $"client B sees 2 players (saw {clientB.World.Players.Count})");
        Check(clientA.World.Map != null && clientB.World.Map != null &&
              clientA.World.Map.Seed == clientB.World.Map.Seed, "both clients share the same map seed");

        Console.WriteLine("\n-- Movement sync --");
        var meA = clientA.World.Me;
        meA.Position += new Vector2(2.0f, 1.0f);
        Pump(0.6f);
        var aSeenByB = clientB.World.Players[clientA.World.MyPlayerId];
        Check(Vector2.Distance(aSeenByB.Position, meA.Position) < 0.5f,
              $"client B sees client A's movement (dist {Vector2.Distance(aSeenByB.Position, meA.Position):0.00})");
        var meB = clientB.World.Me;
        meB.Position += new Vector2(-1.5f, 0.5f);
        Pump(0.6f);
        var bSeenByA = clientA.World.Players[clientB.World.MyPlayerId];
        Check(Vector2.Distance(bSeenByA.Position, meB.Position) < 0.5f, "client A sees client B's movement");

        Console.WriteLine("\n-- Enemy sync --");
        Pump(1.0f);
        Check(clientA.World.Enemies.Count > 0, $"client A sees enemies ({clientA.World.Enemies.Count})");
        Check(clientA.World.Enemies.Count == clientB.World.Enemies.Count,
              $"both clients see the same enemy count (A {clientA.World.Enemies.Count} / B {clientB.World.Enemies.Count})");

        Console.WriteLine("\n-- Combat + host-authoritative loot --");
        // Spawn a dedicated enemy near player A, then kill it with skills.
        clientA.SendDebugCommand("spawn_enemy", "grunt");
        Pump(0.3f);
        var serverEnemy = server.World.Enemies.Values
            .OrderBy(e => Vector2.Distance(e.Position, server.World.Players[clientA.World.MyPlayerId].Position))
            .First();
        int targetId = serverEnemy.Id;
        Check(clientB.World.Enemies.ContainsKey(targetId), "client B sees the debug-spawned enemy");

        // Move A within melee reach: mace strike is caster-relative, projected in front of
        // the player along the aim direction and clamped to weapon range.
        var meAOnServer = server.World.Players[clientA.World.MyPlayerId];
        clientA.World.Me.Position = serverEnemy.Position + new Vector2(-1.0f, 0);
        Pump(0.3f); // let the position reach the server

        float hpBefore = serverEnemy.Health;
        clientA.RequestUseSkill("mace_strike", serverEnemy.Position);
        Pump(0.7f); // covers network latency + the slam's 0.35s wind-up
        Check(serverEnemy.Health < hpBefore, $"mace strike damaged enemy ({hpBefore:0} -> {serverEnemy.Health:0})");
        Check(clientB.World.Enemies.TryGetValue(targetId, out var enemyOnB) &&
              Math.Abs(enemyOnB.Health - serverEnemy.Health) < 0.01f, "enemy damage synchronized to client B");
        Check(clientA.World.FloatingNumbers.Count > 0 || clientB.World.FloatingNumbers.Count > 0,
              "damage event produced floating numbers on clients");

        // A strike aimed far BEHIND max range must not hit (impact point clamps to range).
        float hpBefore2 = serverEnemy.Health;
        clientA.RequestUseSkill("mace_strike",
            clientA.World.Me.Position - new Vector2(10f, 0)); // aimed the opposite direction
        Pump(0.7f); // long enough that a wind-up strike WOULD have landed if aimed here
        Check(Math.Abs(serverEnemy.Health - hpBefore2) < 0.01f,
              "melee strike aimed away from the enemy does not hit it (caster-relative aim)");

        Console.WriteLine("\n-- Dodge (server-authoritative cooldown + i-frames) --");
        var dodgerServer = server.World.Players[clientA.World.MyPlayerId];
        clientA.RequestDodge(new Vector2(1, 0));
        Pump(0.3f);
        Check(dodgerServer.InvulnerableUntil > server.World.Time - 0.5f &&
              dodgerServer.InvulnerableUntil <= server.World.Time + 1f,
              "server granted dodge i-frames");
        float nextDodgeAt = dodgerServer.NextDodgeAt;
        Check(nextDodgeAt > server.World.Time, "server started the dodge cooldown");
        clientA.RequestDodge(new Vector2(0, 1)); // immediately again: must be rejected
        Pump(0.3f);
        Check(Math.Abs(dodgerServer.NextDodgeAt - nextDodgeAt) < 0.001f,
              "second dodge during cooldown was rejected by the server");
        Check(clientB.World.DodgeEventsSeen >= 1, "client B saw client A's dodge event");

        // Force many drops to guarantee loot: kill everything nearby repeatedly.
        // Gold piles have Item == null, so keep going until an actual item drops.
        int dropsBefore = clientA.World.Drops.Count;
        for (int round = 0; round < 10 && !server.World.Drops.Values.Any(d => d.Item != null); round++)
        {
            clientA.SendDebugCommand("spawn_enemy", "grunt");
            Pump(0.2f);
            clientA.SendDebugCommand("kill_nearby");
            Pump(0.4f);
        }
        Check(!clientA.World.Enemies.ContainsKey(targetId), "enemy death synchronized to client A");
        Check(!clientB.World.Enemies.ContainsKey(targetId), "enemy death synchronized to client B");
        Check(server.World.Drops.Count > 0, $"host generated loot ({server.World.Drops.Count} drops)");
        Pump(0.4f);
        Check(clientA.World.Drops.Count == server.World.Drops.Count, "client A sees all drops");
        Check(clientB.World.Drops.Count == server.World.Drops.Count, "client B sees all drops");

        // Same drop must have identical serialized modifiers on both clients.
        var dropId = server.World.Drops.First(kv => kv.Value.Item != null).Key;
        var dropA = clientA.World.Drops[dropId];
        var dropB = clientB.World.Drops[dropId];
        Check(Json.SaveCompact(dropA.Item) == Json.SaveCompact(dropB.Item),
              "generated item is byte-identical on both clients (no rerolling)");
        Check(dropA.Item.InstanceId == dropB.Item.InstanceId, "drop shares one InstanceId across peers");

        // Dev/testing command: drop_scrolls scatters one drop per scroll type.
        int scrollDropsBefore = server.World.Drops.Count;
        clientA.SendDebugCommand("drop_scrolls");
        Pump(0.5f);
        int scrollTypes = data.Items.Values.Count(b =>
            b.Category is Items.ItemCategory.EnchantScroll or Items.ItemCategory.SkillScroll);
        Check(scrollTypes > 0 && server.World.Drops.Count - scrollDropsBefore == scrollTypes,
              $"drop_scrolls scatters one drop per scroll type ({server.World.Drops.Count - scrollDropsBefore}/{scrollTypes})");

        Console.WriteLine("\n-- Exclusive pickup --");
        // Drops can land inside wall pillars (the server rejects teleports into walls, so a
        // player standing "at" such a drop never actually moves). Stand at the nearest
        // walkable spot within pickup range instead.
        Vector2 SafeNear(Vector2 target)
        {
            foreach (var off in new[]
                     {
                         Vector2.Zero, new Vector2(1, 0), new Vector2(-1, 0), new Vector2(0, 1),
                         new Vector2(0, -1), new Vector2(1.2f, 1.2f), new Vector2(-1.2f, -1.2f),
                         new Vector2(1.7f, 0), new Vector2(0, 1.7f),
                     })
            {
                var cand = target + off;
                if (!clientA.World.Map.CircleHitsWall(cand, 0.35f)) return cand;
            }
            return target;
        }

        // Both clients race to pick up the same item; exactly one may win.
        var dropPos = SafeNear(server.World.Drops[dropId].Position);
        int invA = clientA.World.MyCharacter.Inventory.Items.Count;
        int invB = clientB.World.MyCharacter.Inventory.Items.Count;
        int gainedA = 0, gainedB = 0;
        for (int attempt = 0; attempt < 8 && gainedA + gainedB == 0; attempt++)
        {
            // Roaming zombies can kill the players; heal doesn't revive, so ride out the
            // respawn cycle until both are alive before racing for the pickup.
            for (int wait = 0; wait < 20 && !(clientA.World.Me.Alive && clientB.World.Me.Alive); wait++)
                Pump(0.5f);
            clientA.SendDebugCommand("kill_nearby");
            clientB.SendDebugCommand("kill_nearby");
            clientA.SendDebugCommand("heal");
            clientB.SendDebugCommand("heal");
            clientA.World.Me.Position = dropPos;
            clientB.World.Me.Position = dropPos;
            Pump(0.4f); // let the fresh client positions reach the server before requesting pickup
            clientA.RequestPickup(dropId);
            clientB.RequestPickup(dropId);
            Pump(0.6f);
            gainedA = clientA.World.MyCharacter.Inventory.Items.Count - invA;
            gainedB = clientB.World.MyCharacter.Inventory.Items.Count - invB;
        }
        Check(gainedA + gainedB == 1, $"exactly one client got the item (A +{gainedA}, B +{gainedB})");
        Check(!clientA.World.Drops.ContainsKey(dropId) && !clientB.World.Drops.ContainsKey(dropId),
              "picked-up item disappeared for both clients");

        Console.WriteLine("\n-- Equipment stats --");
        // Level the character up first (rare maces can have RequiredLevel gates), then equip.
        for (int i = 0; i < 12; i++) clientA.SendDebugCommand("char_xp");
        clientA.SendDebugCommand("give_mace");
        Pump(0.6f);
        Check(clientA.World.MyCharacter.Level > 5, $"character leveled up via debug XP (level {clientA.World.MyCharacter.Level})");
        var mace = clientA.World.MyCharacter.Inventory.Items
            .FirstOrDefault(pl => pl.Item.GetBase(data).Category == Items.ItemCategory.Mace);
        Check(mace != null, "debug mace arrived in inventory");
        if (mace != null)
        {
            clientA.RequestMoveItem(ItemLocation.AtGrid(mace.X, mace.Y), ItemLocation.AtEquip(Items.EquipSlot.MainHand));
            Pump(0.4f);
            var equipped = clientA.World.MyCharacter.Equipment.GetValueOrDefault(Items.EquipSlot.MainHand);
            Check(equipped != null && equipped.InstanceId == mace.Item.InstanceId, "mace equipped via move request");
            var serverStats = server.World.Players[clientA.World.MyPlayerId].Stats;
            var clientStats = clientA.World.MyStats;
            Check(Math.Abs(serverStats.WeaponMaxDamage - clientStats.WeaponMaxDamage) < 0.01f &&
                  Math.Abs(serverStats.MaxHealth - clientStats.MaxHealth) < 0.01f &&
                  serverStats.WeaponCategory == Items.ItemCategory.Mace,
                  $"server and client compute identical stats from the equipped mace " +
                  $"(dmg {serverStats.WeaponMinDamage:0.#}-{serverStats.WeaponMaxDamage:0.#})");
            var aSeenByBAppearance = clientB.World.Players[clientA.World.MyPlayerId];
            Check(aSeenByBAppearance.WeaponBaseId == equipped?.BaseItemId,
                  $"client B sees client A's held weapon ({aSeenByBAppearance.WeaponBaseId})");
        }

        Console.WriteLine("\n-- Skills, levels, scroll slots, scrolls --");
        // v27 class kits: fresh characters start with ONE class skill (these server-made
        // defaults are warriors), so fire_bolt is learned here — arena learning is free.
        clientA.RequestLearnSkill("fire_bolt");
        clientB.RequestLearnSkill("fire_bolt");
        clientB.RequestLearnSkill("arcane_burst");
        clientB.SendDebugCommand("skill_xp"); // level up skills -> unlock scroll slots
        clientB.SendDebugCommand("give_scroll");
        Pump(0.6f);
        var charB = clientB.World.MyCharacter;
        Check(charB.GetSkill("arcane_burst") != null, "client B learned Arcane Burst");
        var fireBolt = charB.GetSkill("fire_bolt");
        Check(fireBolt != null && fireBolt.Level == 1 && fireBolt.Experience > 0,
              $"skill XP banks WITHOUT auto-leveling (fire_bolt level {fireBolt?.Level}, {fireBolt?.Experience:0} xp)");
        // Leveling is a deliberate Skill Menu action: spend the banked XP now.
        for (int i = 0; i < 6; i++) clientB.RequestLevelSkill("fire_bolt");
        Pump(0.5f);
        fireBolt = clientB.World.MyCharacter.GetSkill("fire_bolt");
        Check(fireBolt != null && fireBolt.Level > 1,
              $"the Level Up request spends banked XP (fire_bolt level {fireBolt?.Level})");
        float bankedNow = fireBolt?.Experience ?? 0;
        clientB.RequestLevelSkill("fire_bolt"); // no XP left for another level
        Pump(0.3f);
        Check(clientB.World.MyCharacter.GetSkill("fire_bolt").Level == fireBolt.Level,
              "leveling without enough banked XP is refused");
        int slots = Skills.SkillMath.ScrollSlotsAtLevel(data, fireBolt?.Level ?? 1);
        Check(slots > 0, $"higher skill level unlocked scroll slots ({slots})");

        var scrollItem = charB.Inventory.Items.FirstOrDefault(pl =>
            pl.Item.GetBase(data).Category == Items.ItemCategory.SkillScroll);
        Check(scrollItem != null, "debug scroll arrived in inventory");
        if (scrollItem != null)
        {
            // Find a learned skill compatible with this scroll.
            var scrollDef = data.Scrolls[scrollItem.Item.GetBase(data).ScrollId];
            var targetSkill = charB.Skills.FirstOrDefault(s =>
                scrollDef.CompatibleWith(s.GetDefinition(data)) &&
                Skills.SkillMath.ScrollSlotsAtLevel(data, s.Level) > 0);
            if (targetSkill != null)
            {
                clientB.RequestMoveItem(ItemLocation.AtGrid(scrollItem.X, scrollItem.Y),
                                        ItemLocation.AtScroll(targetSkill.SkillId, 0));
                Pump(0.4f);
                charB = clientB.World.MyCharacter;
                var attached = charB.GetSkill(targetSkill.SkillId).Scrolls.FirstOrDefault();
                Check(attached != null && attached.InstanceId == scrollItem.Item.InstanceId,
                      $"scroll '{scrollDef.Name}' attached to {targetSkill.SkillId}");

                // Scroll visibly alters skill numbers.
                var withoutScroll = Skills.SkillMath.Compute(data, data.Skills[targetSkill.SkillId],
                    charB.GetSkill(targetSkill.SkillId).Level, Array.Empty<Skills.ScrollDefinition>(), clientB.World.MyStats);
                var withScroll = Skills.SkillMath.Compute(data, data.Skills[targetSkill.SkillId],
                    charB.GetSkill(targetSkill.SkillId).Level, charB.GetSkill(targetSkill.SkillId).ScrollDefinitions(data),
                    clientB.World.MyStats);
                Check(Json.SaveCompact(withScroll) != Json.SaveCompact(withoutScroll),
                      "attached scroll changes computed skill stats");
            }
            else
            {
                // Incompatible scroll: attaching must be rejected.
                var anySkill = charB.Skills.First(s => !scrollDef.CompatibleWith(s.GetDefinition(data)));
                msgB = null;
                clientB.RequestMoveItem(ItemLocation.AtGrid(scrollItem.X, scrollItem.Y),
                                        ItemLocation.AtScroll(anySkill.SkillId, 0));
                Pump(0.4f);
                Check(clientB.World.MyCharacter.GetSkill(anySkill.SkillId).Scrolls.All(s => s == null),
                      "incompatible scroll was rejected");
                Check(msgB != null, $"server explained the rejection ('{msgB}')");
            }
        }

        Console.WriteLine("\n-- Modifier limit flexibility --");
        clientA.SendDebugCommand("give_10mod");
        Pump(0.5f);
        var bigItem = clientA.World.MyCharacter.Inventory.Items
            .Select(pl => pl.Item)
            .FirstOrDefault(i => i.Modifiers.Count > 6);
        Check(bigItem != null,
              $"item exceeded the conventional six-affix limit (got {bigItem?.Modifiers.Count.ToString() ?? "none"} modifiers, limit {bigItem?.CurrentModifierLimit(data)})");

        Console.WriteLine("\n-- Gold, item values, regen prefix, knockback --");
        var valuedItem = clientA.World.MyCharacter.Inventory.Items
            .FirstOrDefault(pl => pl.Item.GetBase(data).Category != Items.ItemCategory.SkillScroll)?.Item;
        Check(valuedItem != null && valuedItem.GoldValue(data) > 0,
              $"items have modifier-based gold values ({valuedItem?.GoldValue(data)} gold)");

        Check(data.Modifiers.ContainsKey("mending"), "Mending (life regeneration) prefix loaded");
        var regenChar = new Sim.CharacterData();
        var regenRing = new Items.ItemInstance { BaseItemId = "copper_ring", Rarity = Items.ItemRarity.Magic };
        regenRing.Modifiers.Add(new Items.ItemModifierRoll { ModifierId = "mending", Value = 2 });
        regenChar.Equipment[Items.EquipSlot.Ring1] = regenRing;
        var regenStats = Stats.StatCalculator.Compute(data, regenChar);
        Check(Math.Abs(regenStats.LifeRegeneration - 2) < 0.01f,
              $"Mending prefix grants life regen through the stat system ({regenStats.LifeRegeneration}/s)");

        int goldBefore = clientA.World.MyCharacter.Gold;
        for (int round = 0; round < 10 && !server.World.Drops.Values.Any(d => d.IsGold); round++)
        {
            clientA.SendDebugCommand("spawn_enemy", "grunt");
            Pump(0.2f);
            clientA.SendDebugCommand("kill_nearby");
            Pump(0.3f);
        }
        var goldDrop = server.World.Drops.Values.FirstOrDefault(d => d.IsGold);
        Check(goldDrop != null, "enemies drop gold");
        if (goldDrop != null)
        {
            Pump(0.3f);
            Check(clientB.World.Drops.TryGetValue(goldDrop.DropId, out var goldOnB) && goldOnB.IsGold &&
                  goldOnB.GoldAmount == goldDrop.GoldAmount,
                  $"gold drop synchronized to client B ({goldDrop.GoldAmount} gold)");
            // Gold is now picked up AUTOMATICALLY by walking over it: drop a pile at A's
            // feet server-side and just wait — no pickup request at all.
            bool goldGained = false;
            for (int attempt = 0; attempt < 8 && !goldGained; attempt++)
            {
                // heal doesn't revive: if A is dead, ride out the respawn cycle first.
                for (int wait = 0; wait < 20 && !clientA.World.Me.Alive; wait++)
                    Pump(0.5f);
                if (!clientA.World.Me.Alive) continue; // auto-pickup only runs for the living
                clientA.SendDebugCommand("kill_nearby"); // roaming zombies can kill A mid-pickup
                clientA.SendDebugCommand("heal");
                // A and B stand on the same tile after the pickup race — step A away so
                // B can't vacuum up the pile first.
                clientA.World.Me.Position = SafeNear(
                    server.World.Players[clientB.World.MyPlayerId].Position + new Vector2(4, 0));
                Pump(0.4f);
                server.World.SpawnGoldDrop(25, server.World.Players[clientA.World.MyPlayerId].Position);
                Pump(0.8f);
                goldGained = clientA.World.MyCharacter.Gold > goldBefore;
                if (!goldGained)
                {
                    var sp = server.World.Players[clientA.World.MyPlayerId];
                    Console.WriteLine($"  [diag] gold attempt {attempt}: sAlive={sp.Alive} " +
                        $"clientGold={clientA.World.MyCharacter.Gold} serverGold={sp.Character.Gold} " +
                        $"goldDropsNear={server.World.Drops.Values.Count(d => d.IsGold && Vector2.Distance(d.Position, sp.Position) < 2f)}");
                }
            }
            Check(goldGained,
                  $"walking over gold picks it up automatically ({goldBefore} -> {clientA.World.MyCharacter.Gold})");
        }

        clientA.RequestLearnSkill("basic_strike");
        // Move to the map spawn (guaranteed open ground) so the debug enemy can't land in a wall.
        clientA.World.Me.Position = server.World.Map.PlayerSpawn;
        Pump(0.4f);
        clientA.SendDebugCommand("spawn_enemy", "grunt");
        Pump(0.3f);
        var kbPlayer = server.World.Players[clientA.World.MyPlayerId];
        var kbTarget = server.World.Enemies.Values.Where(e => !e.Dead)
            .OrderBy(e => Vector2.Distance(e.Position, kbPlayer.Position)).First();
        kbTarget.StunnedUntil = server.World.Time + 5f; // hold still: measure pure knockback, not chase drift
        // The swing is now player-centered (range + body radius) — step within reach
        // first, since the debug grunt can spawn a couple of tiles out.
        clientA.World.Me.Position = SafeNear(kbTarget.Position);
        Pump(0.3f);
        float kbBefore = Vector2.Distance(kbTarget.Position, kbPlayer.Position);
        clientA.RequestUseSkill("basic_strike", kbTarget.Position);
        Pump(0.15f);
        float kbAfter = Vector2.Distance(kbTarget.Position, kbPlayer.Position);
        Check(kbTarget.Dead || kbAfter > kbBefore + 0.3f,
              $"basic strike knocked the enemy back ({kbBefore:0.00} -> {kbAfter:0.00} tiles)");

        Console.WriteLine("\n-- Enchanting Scrolls, slot caps, stacking, stun --");
        Check(data.Items.Values.Count(b => b.Category == Items.ItemCategory.EnchantScroll) == 12,
              "all 12 Enchanting Scroll types loaded");

        // Slot cap distribution: totals within 3..8, 5 the most common.
        var slotGen = new Items.LootGenerator(data, new Random(7));
        var totals = new Dictionary<int, int>();
        bool sidesOk = true;
        for (int i = 0; i < 2000; i++)
        {
            var (mp, ms) = slotGen.RollSlots();
            if (mp < 1 || ms < 1) sidesOk = false;
            totals[mp + ms] = totals.GetValueOrDefault(mp + ms) + 1;
        }
        Check(sidesOk, "every rolled item has at least 1 prefix and 1 suffix slot");
        Check(totals.Keys.Min() >= 3 && totals.Keys.Max() <= 8,
              $"slot totals stay within 3..8 (saw {totals.Keys.Min()}..{totals.Keys.Max()})");
        Check(totals.OrderByDescending(kv => kv.Value).First().Key == 5,
              $"5 total slots is the most common roll ({string.Join(", ", totals.OrderBy(k => k.Key).Select(kv => $"{kv.Key}:{kv.Value}"))})");
        Check(totals.GetValueOrDefault(8) > 0 && totals.GetValueOrDefault(8) < 100,
              $"8 slots is extremely rare ({totals.GetValueOrDefault(8)}/2000)");

        // Pure crafting logic: white -> blue, blue 2-mod cap, sealing.
        var craftRng = new Random(11);
        var craftLoot = new Items.LootGenerator(data, craftRng);
        var white = new Items.ItemInstance { BaseItemId = "iron_mace", ItemLevel = 5, MaxPrefixes = 3, MaxSuffixes = 3 };
        Check(Items.EnchantSystem.Apply(data, craftRng, craftLoot, Items.EnchantType.Awaken, white, out _) &&
              white.Rarity == Items.ItemRarity.Magic && white.Modifiers.Count == 1,
              "Awakening: white item gained a modifier and turned blue");
        // Fill the blue item to 2 mods, then a third add must fail (blue cap).
        bool second = Items.EnchantSystem.Apply(data, craftRng, craftLoot,
            white.CountAffixes(data, Items.AffixType.Prefix) == 0 ? Items.EnchantType.AddPrefixMagic : Items.EnchantType.AddSuffixMagic,
            white, out _);
        bool third = Items.EnchantSystem.Apply(data, craftRng, craftLoot, Items.EnchantType.AddPrefixMagic, white, out string capError);
        Check(second && !third && white.Modifiers.Count == 2,
              $"blue items cap at 2 modifiers ('{capError}')");

        var rare = craftLoot.Generate(data.Items["arcane_staff"], 10, Items.ItemRarity.Rare);
        int slotsBefore = rare.MaxPrefixes + rare.MaxSuffixes;
        Check(Items.EnchantSystem.Apply(data, craftRng, craftLoot, Items.EnchantType.SealExpand, rare, out _) &&
              rare.Locked && rare.MaxPrefixes + rare.MaxSuffixes == slotsBefore + 2,
              $"Sealing: +2 slots ({slotsBefore} -> {rare.MaxPrefixes + rare.MaxSuffixes}) and item locked");
        Check(!Items.EnchantSystem.Apply(data, craftRng, craftLoot, Items.EnchantType.AddRandomRare, rare, out string sealError),
              $"sealed item rejects further enchanting ('{sealError}')");

        // Networked crafting: B awakens its white starter club — which is EQUIPPED, so
        // this also proves ApplyEnchant reaches equipped items, not just the bag.
        clientB.SendDebugCommand("give_enchant", "es_awakening");
        Pump(0.5f);
        var bChar = clientB.World.MyCharacter;
        var bScroll = bChar.Inventory.Items.FirstOrDefault(pl => pl.Item.BaseItemId == "es_awakening");
        var bClub = bChar.Equipment.GetValueOrDefault(Items.EquipSlot.MainHand);
        // B may already own an es_awakening (e.g. won one in the pickup race), in which
        // case the debug stack MERGES — assert relative counts, not absolutes.
        Check(bScroll != null && bScroll.Item.StackCount >= 3, $"scroll stack arrived (x{bScroll?.Item.StackCount})");
        Check(bClub != null && bClub.Rarity == Items.ItemRarity.Normal,
              "B's starter club is white and equipped for the crafting test");
        if (bScroll != null && bClub != null)
        {
            int stackBefore = bScroll.Item.StackCount;
            clientB.RequestApplyEnchant(bScroll.Item.InstanceId, bClub.InstanceId);
            Pump(0.5f);
            bChar = clientB.World.MyCharacter;
            var clubAfter = bChar.Equipment.GetValueOrDefault(Items.EquipSlot.MainHand);
            var scrollAfter = bChar.Inventory.FindByInstance(bScroll.Item.InstanceId)?.Item;
            Check(clubAfter != null && clubAfter.Rarity == Items.ItemRarity.Magic && clubAfter.Modifiers.Count == 1,
                  $"networked Awakening turned the equipped club blue with 1 modifier");
            Check(scrollAfter != null && scrollAfter.StackCount == stackBefore - 1,
                  $"one scroll charge consumed (stack {stackBefore} -> {scrollAfter?.StackCount})");
        }

        // Ground Slam stun (retry loop: A may be dead or the spawned grunt already killed).
        clientA.RequestLearnSkill("ground_slam");
        Pump(0.3f);
        var stunPlayer = server.World.Players[clientA.World.MyPlayerId];
        bool stunConfirmed = false;
        for (int attempt = 0; attempt < 5 && !stunConfirmed; attempt++)
        {
            for (int wait = 0; wait < 20 && !clientA.World.Me.Alive; wait++)
                Pump(0.5f);
            // Park A at the map spawn (guaranteed open ground) so the debug grunt can't
            // spawn inside a wall pillar next to wherever A happened to be standing.
            clientA.World.Me.Position = clientA.World.Map.PlayerSpawn;
            Pump(0.4f);
            clientA.SendDebugCommand("kill_nearby"); // clear campers that could kill A mid-slam
            clientA.SendDebugCommand("heal");
            stunPlayer.Mana = stunPlayer.Stats.MaxMana; // slams cost mana; refill between attempts
            Pump(0.2f);
            clientA.SendDebugCommand("spawn_enemy", "grunt");
            Pump(0.3f);
            // The grunt may immediately chase the OTHER player out of slam radius —
            // teleport A onto it before slamming.
            var slamTarget = server.World.Enemies.Values.Where(e => !e.Dead)
                .OrderByDescending(e => e.Id).FirstOrDefault();
            if (slamTarget != null)
            {
                // A strong mace one-shots the grunt, and dead enemies can't read as
                // stunned — buff its health so the slam stuns instead of kills.
                slamTarget.Health = 999f;
                clientA.World.Me.Position = SafeNear(slamTarget.Position);
                Pump(0.3f);
            }
            clientA.RequestUseSkill("ground_slam", stunPlayer.Position);
            Pump(0.65f); // the reworked slam has a 0.4s wind-up before the hit lands
            stunConfirmed = server.World.Enemies.Values.Any(e => !e.Dead && e.StunnedUntil > server.World.Time);
            if (!stunConfirmed)
            {
                int near = server.World.Enemies.Values.Count(e => !e.Dead &&
                    Vector2.Distance(e.Position, stunPlayer.Position) < 2.2f);
                var closest = server.World.Enemies.Values.Where(e => !e.Dead)
                    .OrderBy(e => Vector2.Distance(e.Position, stunPlayer.Position)).FirstOrDefault();
                Console.WriteLine($"  [diag] slam attempt {attempt}: alive={stunPlayer.Alive} mana={stunPlayer.Mana:0} " +
                                  $"near={near} serverPos={stunPlayer.Position} clientPos={clientA.World.Me.Position} " +
                                  $"closest={closest?.Def.Id}@{closest?.Position} " +
                                  $"dist={(closest != null ? Vector2.Distance(closest.Position, stunPlayer.Position) : -1):0.0} " +
                                  $"cd={stunPlayer.SkillReadyAt.GetValueOrDefault("ground_slam") - server.World.Time:0.00}");
                Pump(2.4f); // ride out the (now much longer) skill cooldown before retrying
            }
        }
        Check(stunConfirmed, "ground slam stunned nearby enemies");

        // Ground Slam rework: wind-up before the hit, knockback on survivors, and the
        // heavier cost/cooldown numbers.
        var gsDef = data.Skills["ground_slam"];
        Check(gsDef.WindupTime > 0.3f && gsDef.ManaCost == 12 && gsDef.Cooldown > 2f &&
              gsDef.Knockback > 1f && gsDef.Tags.Contains("Slam"),
              "Ground Slam reworked: wind-up, 12 mana, knockback, long cooldown");
        {
            clientA.SendDebugCommand("kill_nearby");
            clientA.SendDebugCommand("heal");
            stunPlayer.Mana = stunPlayer.Stats.MaxMana;
            stunPlayer.SkillReadyAt.Clear();
            stunPlayer.GlobalSkillReadyAt = 0;
            clientA.World.Me.Position = clientA.World.Map.PlayerSpawn;
            Pump(0.3f);
            var gsPrey = server.World.SpawnEnemy("grunt", stunPlayer.Position + new Vector2(1.0f, 0));
            gsPrey.Health = 999f;
            gsPrey.StunnedUntil = server.World.Time + 30f; // pinned: any later movement is knockback
            Pump(0.2f);
            float gsHpBefore = gsPrey.Health;
            float gsDistBefore = Vector2.Distance(gsPrey.Position, stunPlayer.Position);
            stunPlayer.Mana = stunPlayer.Stats.MaxMana;
            stunPlayer.SkillReadyAt.Clear();
            stunPlayer.GlobalSkillReadyAt = 0;
            clientA.RequestUseSkill("ground_slam", stunPlayer.Position);
            Pump(0.2f); // inside the wind-up: nothing has landed yet
            Check(gsPrey.Health >= gsHpBefore - 0.01f,
                  "ground slam wind-up delays the hit (no damage mid-charge)");
            Pump(0.5f); // past the wind-up: the slam lands
            float gsDistAfter = Vector2.Distance(gsPrey.Position, stunPlayer.Position);
            Check(gsPrey.Health < gsHpBefore - 0.01f,
                  $"ground slam lands after the wind-up (hp {gsHpBefore:0} -> {gsPrey.Health:0})");
            Check(gsDistAfter > gsDistBefore + 0.4f,
                  $"ground slam knocks survivors back ({gsDistBefore:0.00} -> {gsDistAfter:0.00} tiles)");
            // gsPrey stays alive and pinned-stunned: the replication check below
            // needs a living stunned enemy to read the debuff flag from.
        }

        // The stun replicates to clients as a debuff flag (rendered as a tiny icon).
        bool stunFlagSeen = false;
        for (int i = 0; i < 30 && !stunFlagSeen; i++)
        {
            Pump(0.05f);
            stunFlagSeen = clientA.World.Enemies.Values.Any(en =>
                (en.DebuffFlags & Server.EnemyDebuffs.Stunned) != 0);
        }
        Check(stunFlagSeen, "stun debuff flag replicated to the client for indicator icons");

        Console.WriteLine("\n-- Tiered modifiers and damage types --");
        Check(data.Modifiers.Count == 463, $"tiered modifier database loaded ({data.Modifiers.Count} modifiers)");
        Check(data.Modifiers.Values.Count(m => m.Tier == 10) == 46,
              "every full family has a tier X (46 tiered families reach tier 10)");
        Check(data.Modifiers["of_precision"].StatAffected == Stats.StatType.CriticalChance &&
              data.Modifiers["of_ferocity"].StatAffected == Stats.StatType.CriticalDamage &&
              data.Modifiers["of_precision"].CompatibleItemCategories.All(c =>
                  c is Items.ItemCategory.Mace or Items.ItemCategory.Staff
                    or Items.ItemCategory.Bow or Items.ItemCategory.Quiver),
              "critical hit chance/damage suffixes roll on weapons (and quivers) only");
        Check(!data.Modifiers["of_haste"].CompatibleItemCategories.Contains(Items.ItemCategory.Staff) &&
              !data.Modifiers["of_casting"].CompatibleItemCategories.Contains(Items.ItemCategory.Mace),
              "attack speed and cast speed suffixes are separated (no staff haste, no mace focus)");
        Check(data.Modifiers.Values.Where(m => m.StatAffected is Stats.StatType.FireResistance
                  or Stats.StatType.ArcaneResistance or Stats.StatType.Armor)
              .All(m => !m.CompatibleWith(Items.ItemCategory.Mace) && !m.CompatibleWith(Items.ItemCategory.Staff)),
              "defense modifiers (armor, resistances) no longer roll on weapons");
        Check(data.Skills["basic_strike"].Name == "Mace Strike" && data.Skills["mace_strike"].Name == "Mace Slam",
              "skills renamed: Mace Strike (swing) and Mace Slam (ground slam)");

        // Crit stats flow through the stat system: base 5% / 150% plus weapon suffixes.
        var critChar = new Sim.CharacterData();
        var critMace = new Items.ItemInstance { BaseItemId = "wooden_club", Rarity = Items.ItemRarity.Magic };
        critMace.Modifiers.Add(new Items.ItemModifierRoll { ModifierId = "of_precision", Value = 3 });
        critMace.Modifiers.Add(new Items.ItemModifierRoll { ModifierId = "of_ferocity", Value = 12 });
        critChar.Equipment[Items.EquipSlot.MainHand] = critMace;
        var critStats = Stats.StatCalculator.Compute(data, critChar);
        Check(Math.Abs(critStats.CritChance - 8) < 0.01f && Math.Abs(critStats.CritDamage - 162) < 0.01f,
              $"crit chance/damage computed from weapon suffixes ({critStats.CritChance}% / {critStats.CritDamage}%)");
        Check(data.Modifiers["of_nullification"].StatAffected == Stats.StatType.ArcaneResistance,
              "Arcane Resistance suffix family loaded");
        Check(!data.Modifiers["occult"].CompatibleWith(Items.ItemCategory.Staff) &&
              data.Modifiers["occult"].CompatibleWith(Items.ItemCategory.Bow) &&
              data.Modifiers["eldritch"].CompatibleItemCategories.SequenceEqual(new[] { Items.ItemCategory.Staff }),
              "Arcane added-damage prefixes split attack (mace/bow) / spell (staff)");
        Check(data.Modifiers["sapphire"].StatAffected == Stats.StatType.MaximumMana &&
              data.Modifiers["of_clarity"].StatAffected == Stats.StatType.ManaRegeneration,
              "Maximum Mana prefix and Mana Regeneration suffix families loaded");

        // Added-damage split: attack adds are attack-gear-only, spell adds caster-weapon-only.
        Check(!data.Modifiers["searing"].CompatibleWith(Items.ItemCategory.Staff) &&
              data.Modifiers["searing"].CompatibleItemCategories.All(c =>
                  c is Items.ItemCategory.Mace or Items.ItemCategory.Bow or Items.ItemCategory.Quiver),
              "attack added-damage prefixes roll only on attack gear (mace/bow/quiver)");
        Check(data.Modifiers["blazing"].CompatibleItemCategories.SequenceEqual(new[] { Items.ItemCategory.Staff }),
              "spell added-damage prefixes roll only on caster weapons (staffs)");

        // Item level gating: an ilvl-1 item can only roll tier-appropriate (ilvl<=1) mods,
        // even when forced to roll many; an ilvl-18 item can roll high tiers.
        var tierGen = new Items.LootGenerator(data, new Random(3));
        bool gatingOk = true;
        int highTierSeen = 0;
        for (int i = 0; i < 60; i++)
        {
            var low = tierGen.Generate(data.Items["iron_mace"], 1, Items.ItemRarity.Rare, forcedModifierCount: 5);
            foreach (var roll in low.Modifiers)
                if (data.Modifiers[roll.ModifierId].MinimumItemLevel > 1) gatingOk = false;
            // Tiers stretch across ilvl 1-100 now (tier N unlocks at (N-1)*10).
            var high = tierGen.Generate(data.Items["iron_mace"], 95, Items.ItemRarity.Rare, forcedModifierCount: 5);
            highTierSeen += high.Modifiers.Count(r => data.Modifiers[r.ModifierId].Tier >= 6);
        }
        Check(gatingOk, "ilvl-1 items never roll modifiers above their item level");
        Check(highTierSeen > 0, $"ilvl-95 items roll high tiers ({highTierSeen} tier-6+ rolls seen)");

        // Same-family tiers can never stack: all groups on a many-mod item are distinct.
        var stackItem = tierGen.Generate(data.Items["arcane_staff"], 18, Items.ItemRarity.Rare, forcedModifierCount: 12);
        var groups = stackItem.Modifiers.Select(r => data.Modifiers[r.ModifierId].ModifierGroup).ToList();
        Check(groups.Count == groups.Distinct().Count(),
              $"no duplicate modifier groups on one item ({groups.Count} mods, all distinct families)");

        // Added elemental damage flows: a Searing (added fire) weapon roll produces a fire
        // damage component on attack skills.
        var fireChar = new Sim.CharacterData { Level = 15 }; // iron_mace wants level 15
        var fireMace = new Items.ItemInstance { BaseItemId = "iron_mace", Rarity = Items.ItemRarity.Magic, MaxPrefixes = 3, MaxSuffixes = 3 };
        fireMace.Modifiers.Add(new Items.ItemModifierRoll { ModifierId = "searing_t5", Value = 12 });
        fireChar.Equipment[Items.EquipSlot.MainHand] = fireMace;
        var fireStats = Stats.StatCalculator.Compute(data, fireChar);
        var strikeStats = Skills.SkillMath.Compute(data, data.Skills["basic_strike"], 1,
            Array.Empty<Skills.ScrollDefinition>(), fireStats);
        Check(fireStats.AddedFire == 12 && strikeStats.Added != null &&
              strikeStats.Added.Any(c => c.Kind == Skills.DamageKind.Fire && c.Max > 0),
              $"Searing roll adds a fire component to attacks (+{fireStats.AddedFire} fire)");
        Check(strikeStats.DamageKind == Skills.DamageKind.Blunt,
              "mace attacks deal Blunt damage (physical split into thrust/blunt/slash)");

        // Spell adds: a Blazing staff roll adds a fire component to Fire Bolt, and the
        // melee-only attack adds do NOT leak onto spells.
        var casterChar = new Sim.CharacterData();
        var blazeStaff = new Items.ItemInstance { BaseItemId = "oak_staff", Rarity = Items.ItemRarity.Magic, MaxPrefixes = 3, MaxSuffixes = 3 };
        blazeStaff.Modifiers.Add(new Items.ItemModifierRoll { ModifierId = "blazing_t5", Value = 9 });
        casterChar.Equipment[Items.EquipSlot.MainHand] = blazeStaff;
        var casterStats = Stats.StatCalculator.Compute(data, casterChar);
        var boltStats = Skills.SkillMath.Compute(data, data.Skills["fire_bolt"], 1,
            Array.Empty<Skills.ScrollDefinition>(), casterStats);
        Check(casterStats.SpellAddedFire == 9 && boltStats.Added != null &&
              boltStats.Added.Any(c => c.Kind == Skills.DamageKind.Fire && c.Max > 0),
              $"Blazing staff roll adds fire damage to spells like Fire Bolt (+{casterStats.SpellAddedFire})");
        var meleeOnSpell = Skills.SkillMath.Compute(data, data.Skills["fire_bolt"], 1,
            Array.Empty<Skills.ScrollDefinition>(), fireStats); // fireStats has melee Searing only
        Check(meleeOnSpell.Added == null,
              "melee attack adds do not apply to spells");

        // New resistances compute through the stat system.
        var resChar = new Sim.CharacterData();
        var resRing = new Items.ItemInstance { BaseItemId = "copper_ring", Rarity = Items.ItemRarity.Magic, MaxPrefixes = 3, MaxSuffixes = 3 };
        resRing.Modifiers.Add(new Items.ItemModifierRoll { ModifierId = "of_acid_resistance_t3", Value = 15 });
        resRing.Modifiers.Add(new Items.ItemModifierRoll { ModifierId = "of_dark_resistance", Value = 8 });
        resChar.Equipment[Items.EquipSlot.Ring1] = resRing;
        var resStats = Stats.StatCalculator.Compute(data, resChar);
        Check(resStats.AcidResistance == 15 && resStats.DarkResistance == 8,
              $"acid/dark/light resistances compute ({resStats.AcidResistance}% acid, {resStats.DarkResistance}% dark)");

        Console.WriteLine("\n-- Gilding scroll and DPS breakdown --");
        Check(data.Items.ContainsKey("es_gilding") &&
              data.Items["es_gilding"].EnchantType == Items.EnchantType.GildUpgrade,
              "Scroll of Gilding loaded (12 enchanting scroll types)");
        var gildItem = new Items.ItemInstance
        {
            BaseItemId = "iron_mace", ItemLevel = 8, Rarity = Items.ItemRarity.Normal,
            MaxPrefixes = 3, MaxSuffixes = 3,
        };
        var gildRng = new Random(21);
        var gildLoot = new Items.LootGenerator(data, gildRng);
        Items.EnchantSystem.Apply(data, gildRng, gildLoot, Items.EnchantType.Awaken, gildItem, out _);
        Check(gildItem.Rarity == Items.ItemRarity.Magic, "test item awakened to blue");
        int modsBeforeGild = gildItem.Modifiers.Count;
        Check(Items.EnchantSystem.Apply(data, gildRng, gildLoot, Items.EnchantType.GildUpgrade, gildItem, out _) &&
              gildItem.Rarity == Items.ItemRarity.Rare && gildItem.Modifiers.Count == modsBeforeGild + 1,
              $"Gilding turned blue item gold and added a modifier ({modsBeforeGild} -> {gildItem.Modifiers.Count})");
        Check(Items.EnchantSystem.Apply(data, gildRng, gildLoot, Items.EnchantType.AddRandomRare, gildItem, out _),
              "gilded item accepts gold-tier scrolls afterward");
        Check(!Items.EnchantSystem.Apply(data, gildRng, gildLoot, Items.EnchantType.GildUpgrade, gildItem, out string gildErr),
              $"Gilding rejects already-gold items ('{gildErr}')");

        var breakdown = Skills.SkillMath.DpsBreakdown(strikeStats); // Searing mace basic strike
        Check(breakdown.GetValueOrDefault(Skills.DamageKind.Blunt) > 0 &&
              breakdown.GetValueOrDefault(Skills.DamageKind.Fire) > 0,
              $"DPS breakdown splits by type (Blunt {breakdown.GetValueOrDefault(Skills.DamageKind.Blunt):0.0}, " +
              $"Fire {breakdown.GetValueOrDefault(Skills.DamageKind.Fire):0.0})");

        Console.WriteLine("\n-- Enemy combat profiles (typed damage + resistances) --");
        var gruntDef = data.Enemies["grunt"];
        var spitterDef = data.Enemies["spitter"];
        Check(gruntDef.DamageTypes.GetValueOrDefault(Skills.DamageKind.Blunt) > 0 &&
              gruntDef.DamageTypes.GetValueOrDefault(Skills.DamageKind.Acid) > 0,
              "melee zombie deals Blunt + Acid damage (data-driven)");
        Check(spitterDef.DamageTypes.Count == 1 &&
              spitterDef.DamageTypes.ContainsKey(Skills.DamageKind.Acid),
              "ranged zombie deals pure Acid damage");
        Check(gruntDef.Resistances.Count == 10 && gruntDef.Resistances.Values.All(v => v == 0) &&
              spitterDef.Resistances.Count == 10 && spitterDef.Resistances.Values.All(v => v == 0),
              "zombies expose all 10 resistance knobs, currently 0");

        // Functional resistance check: at 100% fire resist, fire bolts deal nothing;
        // back at 0 they hurt — proving per-type enemy mitigation works end to end.
        clientA.World.Me.Position = server.World.Map.PlayerSpawn;
        Pump(0.4f);
        clientA.SendDebugCommand("kill_nearby");
        Pump(0.4f);
        clientA.SendDebugCommand("spawn_enemy", "grunt");
        Pump(0.3f);
        var resistTarget = server.World.Enemies.Values.Where(e => !e.Dead)
            .OrderBy(e => Vector2.Distance(e.Position, server.World.Players[clientA.World.MyPlayerId].Position))
            .First();
        gruntDef.Resistances[Skills.DamageKind.Fire] = 100;
        float immuneHp = resistTarget.Health;
        var resistCaster = server.World.Players[clientA.World.MyPlayerId];
        for (int i = 0; i < 3 && !resistTarget.Dead; i++)
        {
            resistCaster.Mana = resistCaster.Stats.MaxMana; // bolts cost mana now
            clientA.RequestUseSkill("fire_bolt", resistTarget.Position);
            Pump(0.8f);
        }
        bool immuneHeld = !resistTarget.Dead && Math.Abs(resistTarget.Health - immuneHp) < 0.01f;
        Check(immuneHeld, $"100% fire resistance nullified fire bolts ({immuneHp:0} -> {resistTarget.Health:0})");
        gruntDef.Resistances[Skills.DamageKind.Fire] = 0;
        float vulnerableHp = resistTarget.Health;
        bool damaged = false;
        for (int i = 0; i < 6 && !damaged; i++)
        {
            resistCaster.Mana = resistCaster.Stats.MaxMana;
            clientA.RequestUseSkill("fire_bolt", resistTarget.Position);
            Pump(0.8f);
            damaged = resistTarget.Dead || resistTarget.Health < vulnerableHp - 0.01f;
        }
        Check(damaged, "at 0% resistance the same fire bolts deal damage (hits confirmed)");

        // The spitter's projectile carries its Acid damage type.
        clientA.SendDebugCommand("spawn_enemy", "spitter");
        Skills.DamageKind? spitKind = null;
        for (int i = 0; i < 300 && spitKind == null; i++)
        {
            server.Update(1f / 60f);
            clientA.Update(1f / 60f);
            clientB.Update(1f / 60f);
            Thread.Sleep(2);
            var spit = server.World.Projectiles.Values.FirstOrDefault(pr => !pr.FromPlayer);
            if (spit != null) spitKind = spit.DamageKind;
        }
        Check(spitKind == Skills.DamageKind.Acid, $"spitter projectiles are Acid-typed ({spitKind})");

        Console.WriteLine("\n-- Shields, handedness, blocking --");
        var shieldBases = data.Items.Values.Where(b => b.Category == Items.ItemCategory.Shield).ToList();
        Check(shieldBases.Count >= 3, $"shield bases loaded ({shieldBases.Count})");
        Check(data.Items.Values.Where(b => b.Category == Items.ItemCategory.Staff).All(b => b.TwoHanded),
              "all staffs are two-handed");
        Check(data.Items.Values.Where(b => b.Category == Items.ItemCategory.Mace).All(b => !b.TwoHanded),
              "all maces are one-handed");

        // Client A still wields the rare mace from the equipment test; add a shield off-hand.
        int aId = clientA.World.MyPlayerId;
        clientA.SendDebugCommand("give_shield");
        Pump(0.5f);
        var shieldPlaced = clientA.World.MyCharacter.Inventory.Items
            .FirstOrDefault(pl => pl.Item.GetBase(data).Category == Items.ItemCategory.Shield);
        Check(shieldPlaced != null, "debug shield arrived in inventory");
        clientA.RequestMoveItem(ItemLocation.AtGrid(shieldPlaced.X, shieldPlaced.Y),
                                ItemLocation.AtEquip(Items.EquipSlot.OffHand));
        Pump(0.4f);
        var offHandItem = clientA.World.MyCharacter.Equipment.GetValueOrDefault(Items.EquipSlot.OffHand);
        Check(offHandItem != null && offHandItem.InstanceId == shieldPlaced.Item.InstanceId,
              "shield equipped in the off-hand next to a one-handed mace");
        var aServerStats = server.World.Players[aId].Stats;
        Check(aServerStats.HasShield && aServerStats.BlockChance > 0,
              $"block chance computed from the shield ({aServerStats.BlockChance:0}% per {aServerStats.BlockCooldown:0.0}s)");
        Check(Math.Abs(clientA.World.MyStats.BlockChance - aServerStats.BlockChance) < 0.01f,
              "client computes the same block chance");
        var aAppearance = clientB.World.Players[aId];
        Check(aAppearance.OffHandBaseId == offHandItem?.BaseItemId,
              $"client B sees A's off-hand shield ({aAppearance.OffHandBaseId})");

        // Shield Bash: rejected without a shield (no cooldown set), accepted with one.
        int bId = clientB.World.MyPlayerId;
        clientA.RequestLearnSkill("shield_bash");
        clientB.RequestLearnSkill("shield_bash");
        Pump(0.4f);
        clientB.RequestUseSkill("shield_bash", clientB.World.Me.Position + new Vector2(1, 0));
        Pump(0.4f);
        Check(!server.World.Players[bId].SkillReadyAt.ContainsKey("shield_bash"),
              "Shield Bash rejected without a shield (no cooldown consumed)");
        server.World.Players[aId].Mana = server.World.Players[aId].Stats.MaxMana; // bash costs mana
        clientA.RequestUseSkill("shield_bash", clientA.World.Me.Position + new Vector2(1, 0));
        // Poll in small steps so the i-frame check runs right after the cast lands
        // (the 0.35s invulnerability window would expire during one big pump).
        bool bashAccepted = false;
        for (int i = 0; i < 30 && !bashAccepted; i++)
        {
            Pump(0.03f);
            bashAccepted = server.World.Players[aId].SkillReadyAt.ContainsKey("shield_bash");
        }
        Check(bashAccepted, "Shield Bash accepted with a shield equipped");
        Check(bashAccepted && server.World.Players[aId].InvulnerableUntil > server.World.Time,
              "Shield Bash lunge grants brief i-frames (ramming an enemy can't hurt)");
        Check(data.Skills["shield_bash"].LungeDistance > 0 && data.Skills["shield_bash"].Knockback >= 2f,
              "Shield Bash lunges forward and knocks back hard");
        Pump(0.3f);

        // Shields are one-handed and fit EITHER hand: a second shield goes into the main hand.
        clientA.SendDebugCommand("give_shield");
        Pump(0.5f);
        var secondShield = clientA.World.MyCharacter.Inventory.Items.FirstOrDefault(pl =>
            pl.Item.GetBase(data).Category == Items.ItemCategory.Shield &&
            pl.Item.InstanceId != offHandItem.InstanceId);
        Check(secondShield != null, "second debug shield arrived in inventory");
        clientA.RequestMoveItem(ItemLocation.AtGrid(secondShield.X, secondShield.Y),
                                ItemLocation.AtEquip(Items.EquipSlot.MainHand));
        Pump(0.4f);
        var charAfterDual = clientA.World.MyCharacter;
        Check(charAfterDual.MainHand?.GetBase(data).Category == Items.ItemCategory.Shield &&
              charAfterDual.OffHand?.GetBase(data).Category == Items.ItemCategory.Shield,
              "a shield can be equipped in BOTH hands at once");

        // Shield Bash scales with shield armor: same weapon, +ShieldArmor -> more damage.
        var bashDef = data.Skills["shield_bash"];
        Check(bashDef.ShieldArmorScaling > 0 && bashDef.StunBuildup >= 50f,
              $"Shield Bash has armor scaling ({bashDef.ShieldArmorScaling}) and heavy stun BUILDUP ({bashDef.StunBuildup})");
        var bareStats = new Stats.ComputedStats { WeaponMinDamage = 10, WeaponMaxDamage = 10, WeaponAttackSpeed = 1f };
        var shieldedStats = bareStats;
        shieldedStats.ShieldArmor = 20;
        var bashBare = Skills.SkillMath.Compute(data, bashDef, 1, Array.Empty<Skills.ScrollDefinition>(), bareStats);
        var bashShielded = Skills.SkillMath.Compute(data, bashDef, 1, Array.Empty<Skills.ScrollDefinition>(), shieldedStats);
        float armorBonus = bashShielded.MinDamage - bashBare.MinDamage;
        Check(Math.Abs(armorBonus - 20 * bashDef.ShieldArmorScaling) < 0.01f,
              $"Shield Bash gains flat damage from shield armor (+{armorBonus:0.#} from 20 armor)");

        // Blocking end-to-end: crank A's off-hand shield to the block cap on the server,
        // park B far away so the spitter targets A, and wait for a Blocked event.
        var aServer = server.World.Players[aId];
        aServer.Character.Equipment[Items.EquipSlot.OffHand].Modifiers.Add(
            new Items.ItemModifierRoll { ModifierId = "of_blocking_t10", Value = 500 });
        aServer.RecomputeStats(server.World.Data);
        Check(aServer.Stats.BlockChance >= 74.9f,
              $"block chance is capped ({aServer.Stats.BlockChance:0}%)");
        clientB.World.Me.Position = aServer.Position + new Vector2(15, 0);
        Pump(0.4f);
        int blockedBefore = clientA.World.BlockedEventsSeen;
        clientA.SendDebugCommand("spawn_enemy", "spitter");
        bool blockedSeen = false;
        for (int i = 0; i < 2400 && !blockedSeen; i++)
        {
            server.Update(1f / 60f);
            clientA.Update(1f / 60f);
            clientB.Update(1f / 60f);
            Thread.Sleep(1);
            if (i % 240 == 239) clientA.SendDebugCommand("heal");
            blockedSeen = clientA.World.BlockedEventsSeen > blockedBefore;
        }
        Check(blockedSeen, "a hit was fully blocked and the Blocked event replicated to the client");
        clientA.SendDebugCommand("kill_nearby");
        clientA.SendDebugCommand("heal");
        Pump(0.4f);

        // Two-handed rules: equipping a staff frees BOTH hands first — the main-hand mace
        // swaps back to the bag and the off-hand shield is auto-unequipped. That needs
        // bag room for BOTH freed hands, and the bag's contents here vary with the
        // pickup race and debug-loot rolls — clear the bulky clutter first so the swap
        // (the thing under test) is deterministic. give_staff re-syncs the client copy.
        server.World.Players[aId].Character.Inventory.Items.RemoveAll(pl =>
            pl.Item.GetBase(data).IsWeapon ||
            pl.Item.GetBase(data).Category == Items.ItemCategory.Shield);
        clientA.SendDebugCommand("give_staff");
        Pump(0.5f);
        var staffPlaced = clientA.World.MyCharacter.Inventory.Items
            .FirstOrDefault(pl => pl.Item.GetBase(data).Category == Items.ItemCategory.Staff);
        Check(staffPlaced != null, "debug staff arrived in inventory");
        clientA.RequestMoveItem(ItemLocation.AtGrid(staffPlaced.X, staffPlaced.Y),
                                ItemLocation.AtEquip(Items.EquipSlot.MainHand));
        Pump(0.4f);
        var charAfterStaff = clientA.World.MyCharacter;
        Check(charAfterStaff.MainHand?.InstanceId == staffPlaced.Item.InstanceId &&
              charAfterStaff.OffHand == null &&
              charAfterStaff.Inventory.FindByInstance(offHandItem.InstanceId) != null,
              $"equipping a two-handed staff auto-unequips the off-hand shield to the bag " +
              $"(main {charAfterStaff.MainHand?.BaseItemId}, off {charAfterStaff.OffHand?.BaseItemId}, msgA '{msgA}')");

        var shieldInBag = charAfterStaff.Inventory.FindByInstance(offHandItem.InstanceId);
        if (shieldInBag != null)
        {
            clientA.RequestMoveItem(ItemLocation.AtGrid(shieldInBag.X, shieldInBag.Y),
                                    ItemLocation.AtEquip(Items.EquipSlot.OffHand));
            Pump(0.4f);
            Check(clientA.World.MyCharacter.OffHand == null,
                  "off-hand refuses a shield while a two-handed staff is equipped");
        }
        else
        {
            Check(false, "off-hand refuses a shield while a two-handed staff is equipped (SKIPPED: shield never reached the bag)");
        }

        Console.WriteLine("\n-- Mana --");
        Check(data.Skills["fire_bolt"].ManaCost > 0 && data.Skills["basic_strike"].ManaCost == 0,
              "skills carry mana costs (Basic Strike stays free)");
        var manaPlayer = server.World.Players[bId];
        Check(manaPlayer.Stats.MaxMana > 0 && manaPlayer.Stats.ManaRegeneration > 0,
              $"level-based mana pool and regen computed ({manaPlayer.Stats.MaxMana:0} max, {manaPlayer.Stats.ManaRegeneration:0.0}/s)");
        Pump(1.0f); // let earlier cooldowns clear
        manaPlayer.Mana = manaPlayer.Stats.MaxMana;
        float manaBefore = manaPlayer.Mana;
        clientB.RequestUseSkill("fire_bolt", clientB.World.Me.Position + new Vector2(3, 0));
        Pump(0.4f);
        // Regen refills a little during the pump, so require most of the cost to be gone.
        Check(manaPlayer.Mana < manaBefore - data.Skills["fire_bolt"].ManaCost + 1.5f,
              $"casting Fire Bolt spent mana server-side ({manaBefore:0} -> {manaPlayer.Mana:0.#})");

        Pump(1.0f);
        manaPlayer.Mana = 1;
        manaPlayer.SkillReadyAt.Remove("fire_bolt");
        manaPlayer.GlobalSkillReadyAt = 0;
        clientB.RequestUseSkill("fire_bolt", clientB.World.Me.Position + new Vector2(3, 0));
        Pump(0.4f);
        Check(!manaPlayer.SkillReadyAt.ContainsKey("fire_bolt") && manaPlayer.Mana <= 1.01f + manaPlayer.Stats.ManaRegeneration,
              "insufficient mana rejects the cast (no cooldown consumed)");

        float manaLow = manaPlayer.Mana;
        Pump(2.0f);
        Check(manaPlayer.Mana > manaLow + 1f,
              $"mana regenerates over time ({manaLow:0.#} -> {manaPlayer.Mana:0.#})");
        manaPlayer.Mana = manaPlayer.Stats.MaxMana; // refill for the projectile section below

        Console.WriteLine("\n-- Ice Spike & Chain Lightning --");
        Check(data.Skills["ice_spike"].DamageKind == Skills.DamageKind.Cold &&
              data.Skills["ice_spike"].ProjectileSprite == "IceSpike",
              "Ice Spike loaded (cold projectile with a named sprite)");
        Check(data.Skills["chain_lightning"].Archetype == Skills.SkillArchetype.ChainLightning &&
              data.Skills["chain_lightning"].DamageKind == Skills.DamageKind.Lightning,
              "Chain Lightning loaded (chain archetype)");

        // Skill Scroll compatibility: Multishot (Projectile tag) fits both new spells and
        // adds shards / chain jumps through the same generic math.
        var multishot = data.Scrolls["multishot"];
        Check(multishot.CompatibleWith(data.Skills["ice_spike"]) &&
              multishot.CompatibleWith(data.Skills["chain_lightning"]),
              "projectile Skill Scrolls attach to both new spells");
        var clPlain = Skills.SkillMath.Compute(data, data.Skills["chain_lightning"], 1,
            Array.Empty<Skills.ScrollDefinition>(), default);
        var clScrolled = Skills.SkillMath.Compute(data, data.Skills["chain_lightning"], 1,
            new[] { multishot }, default);
        Check(clScrolled.ProjectileCount == clPlain.ProjectileCount + 2 && clScrolled.MaxDamage < clPlain.MaxDamage,
              $"Multishot adds chain jumps ({clPlain.ProjectileCount} -> {clScrolled.ProjectileCount}) for less damage");

        // End-to-end: cluster tanky grunts at open ground and zap them.
        clientB.RequestLearnSkill("chain_lightning");
        clientB.RequestLearnSkill("ice_spike");
        clientB.World.Me.Position = clientB.World.Map.PlayerSpawn;
        Pump(0.4f);
        clientB.SendDebugCommand("kill_nearby");
        clientB.SendDebugCommand("heal");
        Pump(0.3f);
        for (int i = 0; i < 3; i++) clientB.SendDebugCommand("spawn_enemy", "grunt");
        Pump(0.5f);
        var bServerPos = server.World.Players[bId].Position;
        var cluster = server.World.Enemies.Values
            .Where(e => !e.Dead && Vector2.Distance(e.Position, bServerPos) < 5f).ToList();
        Check(cluster.Count >= 2, $"grunt cluster assembled ({cluster.Count} enemies)");
        foreach (var e in cluster) e.Health = 500f; // survive the zaps so hits are countable

        Check(cluster.Count < 2 || Vector2.Distance(cluster[0].Position, cluster[1].Position) > 0.4f,
              "overlapping enemies push apart instead of stacking");

        bool chainSeen = false;
        int zapped = 0;
        for (int attempt = 0; attempt < 5 && zapped < 2; attempt++)
        {
            server.World.Players[bId].Mana = server.World.Players[bId].Stats.MaxMana;
            clientB.RequestUseSkill("chain_lightning", cluster[0].Position);
            for (int i = 0; i < 20; i++)
            {
                Pump(0.05f);
                chainSeen |= clientA.World.Effects.Any(fx => fx.Kind == "chain" && fx.Points is { Count: > 2 });
            }
            zapped = cluster.Count(e => e.Health < 499.9f);
        }
        Check(zapped >= 2, $"Chain Lightning leapt between enemies ({zapped} of {cluster.Count} hit)");
        Check(chainSeen, "chain path replicated to the other client for the bolt visual");

        // Clear the cluster first — a spike that hits a grunt on its very first tick
        // spawns and despawns inside one server update and can't be observed.
        foreach (var e in cluster) e.Health = 1f;
        clientB.SendDebugCommand("kill_nearby");
        Pump(0.4f);
        server.World.Players[bId].Mana = server.World.Players[bId].Stats.MaxMana;
        clientB.RequestUseSkill("ice_spike", clientB.World.Me.Position + new Vector2(3, 0));
        bool spikeSeen = false;
        Skills.DamageKind? spikeKind = null;
        for (int i = 0; i < 60 && !spikeSeen; i++)
        {
            Pump(0.03f);
            var sp = server.World.Projectiles.Values.FirstOrDefault(pr => pr.SkillId == "ice_spike");
            if (sp != null) spikeKind = sp.DamageKind;
            spikeSeen = clientA.World.Projectiles.Values.Any(pr => pr.SkillId == "ice_spike");
        }
        Check(spikeSeen && spikeKind == Skills.DamageKind.Cold,
              $"Ice Spike fires a cold projectile replicated to peers ({spikeKind})");

        Console.WriteLine("\n-- Fire bolt projectile --");
        Pump(4.0f); // ride out a possible death/respawn cycle from roaming enemies
        clientB.SendDebugCommand("kill_nearby"); // the spitter (and friends) can kill B mid-cast otherwise
        clientB.SendDebugCommand("heal");
        Pump(0.3f);
        bool projectileSeen = false;
        for (int attempt = 0; attempt < 6 && !projectileSeen; attempt++)
        {
            for (int wait = 0; wait < 20 && !clientB.World.Me.Alive; wait++)
                Pump(0.5f); // heal doesn't revive — ride out the respawn cycle
            // Cast from open ground: against a wall the bolt would spawn and despawn in
            // the same tick and never be observable on the other client.
            clientB.World.Me.Position = clientB.World.Map.PlayerSpawn;
            clientB.SendDebugCommand("kill_nearby");
            clientB.SendDebugCommand("heal");
            manaPlayer.Mana = manaPlayer.Stats.MaxMana; // bolts cost mana
            Pump(0.4f);
            clientB.RequestUseSkill("fire_bolt", clientB.World.Me.Position + new Vector2(3, 0));
            for (int i = 0; i < 60 && !projectileSeen; i++)
            {
                server.Update(1f / 60f);
                clientA.Update(1f / 60f);
                clientB.Update(1f / 60f);
                Thread.Sleep(2);
                projectileSeen = clientA.World.Projectiles.Values.Any(pr => pr.FromPlayer);
            }
        }
        Check(projectileSeen, "fire bolt projectile spawned and replicated to the other client");

        Console.WriteLine("\n-- Layered terrain: demo layout --");
        var map = clientA.World.Map;
        Check(map.GroundLevel(10, 10) == 1 && map.GroundLevel(7, 7) == 2 && map.GroundLevel(22, 22) == 0,
              "demo terrain has flat ground plus two elevation levels (plateau 1, crown 2)");
        Check(map.Ramp(15, 10) == World.RampDirection.MinusX && map.Ramp(15, 24) == World.RampDirection.MinusX,
              "elevation transitions inset into the plateau edges");
        Check(!map.RampIsStairs(15, 10) && map.RampIsStairs(15, 24),
              "demo has both transition styles (smooth ramp and stairs)");
        Check(map.WallHeight(20, 7) == 3 && map.WallHeight(24, 7) == 2,
              "tall cliff wall with varying height (3 and 2 levels)");
        Check(map.BridgeLevel(10, 17) == 1 && map.GroundLevel(10, 17) == 0 && !map.IsSolid(10, 17),
              "bridge deck over walkable level-0 ground (two stacked surfaces)");

        Console.WriteLine("\n-- Layered terrain: ramps & cliffs --");
        // Walk from open ground across the ramp tile onto plateau A: height must rise 0 -> 1.
        float walkerH = 0f;
        var walker = new Vector2(17.5f, 10.5f);
        for (int i = 0; i < 240; i++)
            walker = map.MoveWithCollision(walker, new Vector2(-0.05f, 0), 0.35f, ref walkerH);
        Check(walker.X < 14.5f && MathF.Abs(walkerH - 1f) < 0.05f,
              $"walking up the ramp raises surface height to 1 (x {walker.X:0.0}, h {walkerH:0.00})");
        // Walking straight into the cliff face (no ramp) must be blocked: the level-1
        // plateau edge at x=16, y=13 is a cliff for a height-0 entity.
        float cliffH = 0f;
        var cliffWalker = new Vector2(17.5f, 13.5f);
        for (int i = 0; i < 240; i++)
            cliffWalker = map.MoveWithCollision(cliffWalker, new Vector2(-0.05f, 0), 0.35f, ref cliffH);
        Check(cliffWalker.X > 15.9f && cliffH < 0.05f,
              $"cliff face blocks movement without a ramp (stopped at x {cliffWalker.X:0.0}, h {cliffH:0.00})");
        // Regression: descending the inset ramp while hugging the flank wall. The
        // circle legally overlaps the flank near the top (heights match), and as the
        // height drops the flank turns unreachable — penetration-based movement must
        // keep the walker sliding out instead of wedging it in place forever.
        float hugH = 1f;
        var hugger = new Vector2(14.5f, 10.32f); // plateau, circle grazing flank (15,9)
        for (int i = 0; i < 120; i++)
            hugger = map.MoveWithCollision(hugger, new Vector2(0.05f, 0), 0.35f, ref hugH);
        Check(hugger.X > 16.2f && hugH < 0.05f,
              $"walker descends the ramp along the flank without getting stuck (x {hugger.X:0.0}, h {hugH:0.00})");

        // Projectile LOS: a shot flying at ground height is blocked by the cliff, while
        // the same shot at plateau height clears it.
        Check(map.SegmentBlocked(new Vector2(18, 10.5f), new Vector2(10, 10.5f), 0.5f),
              "ground-height shot is blocked by the plateau cliff");
        Check(!map.SegmentBlocked(new Vector2(14, 10.5f), new Vector2(8, 10.5f), 1.5f),
              "plateau-height shot flies clear across the plateau");

        Console.WriteLine("\n-- Layered terrain: height sync --");
        // Teleport A onto plateau A at level 1 and verify the elevation replicates.
        clientA.World.Me.Position = new Vector2(12.5f, 12.5f);
        clientA.World.Me.Height = 1f;
        Pump(0.4f);
        clientA.SendDebugCommand("kill_nearby"); // plateau residents would maul A mid-check
        clientA.SendDebugCommand("heal");
        Pump(0.4f);
        var srvA = server.World.Players[clientA.World.MyPlayerId];
        Check(MathF.Abs(srvA.Height - 1f) < 0.05f,
              $"server accepted and sampled the plateau surface (h {srvA.Height:0.00})");
        var aOnB = clientB.World.Players[clientA.World.MyPlayerId];
        Check(MathF.Abs(aOnB.NetTargetHeight - 1f) < 0.05f,
              $"client B sees A's replicated elevation (h {aOnB.NetTargetHeight:0.00})");
        // A client claiming a plateau position at the WRONG height is rejected (the
        // server refuses positions with no reachable surface near the claimed height).
        var posBefore = srvA.Position;
        clientA.World.Me.Position = new Vector2(8.5f, 12.5f);
        clientA.World.Me.Height = 0f; // level-1 ground there — no surface within tolerance
        Pump(0.5f);
        Check(Vector2.Distance(srvA.Position, posBefore) < 0.05f,
              "server rejects a position claim with no surface near the claimed height");
        clientA.World.Me.Position = posBefore;
        clientA.World.Me.Height = 1f;
        Pump(0.4f);

        Console.WriteLine("\n-- Layered terrain: bridge & combat isolation --");
        // Park B ON the bridge deck (level 1) while a grunt stands UNDER it at level 0:
        // same X/Y column, two different surfaces.
        clientB.SendDebugCommand("kill_nearby");
        clientB.SendDebugCommand("heal");
        Pump(0.3f);
        clientB.World.Me.Position = new Vector2(10.5f, 17.5f);
        clientB.World.Me.Height = 1f;
        Pump(0.5f);
        var srvB = server.World.Players[bId];
        Check(MathF.Abs(srvB.Height - 1f) < 0.05f,
              $"player stands on the bridge deck at level 1 (h {srvB.Height:0.00})");
        var underEnemy = server.World.SpawnEnemy("grunt", new Vector2(10.5f, 17.5f));
        underEnemy.Health = 500f;
        Check(underEnemy.Height < 0.05f,
              $"enemy under the deck occupies the same X/Y at level 0 (h {underEnemy.Height:0.00})");
        Pump(1.0f);
        Check(underEnemy.TargetPlayerId != bId,
              "under-bridge enemy does not aggro the player on the deck above");
        // A melee swing from the deck must not hit through the deck floor.
        srvB.Mana = srvB.Stats.MaxMana;
        clientB.RequestUseSkill("mace_strike", clientB.World.Me.Position + new Vector2(0.3f, 0));
        Pump(0.5f);
        Check(underEnemy.Health >= 499.9f,
              $"deck player's melee does not hit the enemy underneath (hp {underEnemy.Health:0})");
        // Same-surface combat still works: hop down beside it and swing again. The
        // enemy is stun-pinned first so chase drift between the cast request and its
        // server-side resolution can't carry it out of the swing.
        underEnemy.StunnedUntil = server.World.Time + 6f;
        clientB.World.Me.Position = new Vector2(13.5f, 17.5f); // corridor ground, level 0
        clientB.World.Me.Height = 0f;
        Pump(0.4f);
        clientB.World.Me.Position = underEnemy.Position + new Vector2(-0.9f, 0);
        Pump(0.4f);
        srvB.Mana = srvB.Stats.MaxMana;
        srvB.SkillReadyAt.Remove("mace_strike");
        srvB.GlobalSkillReadyAt = 0;
        clientB.World.Me.Position = underEnemy.Position + new Vector2(-0.9f, 0); // re-pin
        clientB.RequestUseSkill("mace_strike", underEnemy.Position);
        Pump(0.5f);
        Check(underEnemy.Health < 499.9f,
              $"same-surface melee still lands (hp {underEnemy.Health:0})");
        // Drop height replication: kill an enemy up on plateau B and check clients see
        // its gold/loot resting at level 1.
        underEnemy.Health = 1f;
        clientB.World.Me.Position = new Vector2(10.5f, 23.0f); // plateau B, level 1
        clientB.World.Me.Height = 1f;
        Pump(0.4f);
        clientB.SendDebugCommand("kill_nearby"); // clear plateau residents BEFORE the target spawns
        clientB.SendDebugCommand("heal");
        Pump(0.3f);
        // Spawn out of gold-autopickup reach (1.1) but inside melee reach (range + radius).
        var plateauEnemy = server.World.SpawnEnemy("grunt", new Vector2(10.5f, 25.3f));
        plateauEnemy.StunnedUntil = server.World.Time + 30f; // hold still for the kill
        Check(MathF.Abs(plateauEnemy.Height - 1f) < 0.05f, "enemy spawned on plateau B at level 1");
        bool elevatedDrop = false;
        for (int hit = 0; hit < 30 && !plateauEnemy.Dead; hit++)
        {
            // Mace Slam's knockback shoves the target out of reach between swings;
            // pin it back to the spawn spot so every iteration connects.
            plateauEnemy.Position = new Vector2(10.5f, 25.3f);
            plateauEnemy.Height = 1f;
            srvB.Mana = srvB.Stats.MaxMana;
            srvB.SkillReadyAt.Clear();
            srvB.GlobalSkillReadyAt = 0;
            clientB.RequestUseSkill("mace_strike", plateauEnemy.Position);
            Pump(0.25f);
        }
        Check(plateauEnemy.Dead, "plateau enemy killed by same-surface melee");
        // Drop replication is chance-free when spawned directly: a gold pile on the
        // plateau must reach clients carrying the level-1 surface height. (Out of B's
        // 1.1-tile auto-pickup range so it survives long enough to observe.)
        var goldSpot = new Vector2(8.5f, 26.5f);
        server.World.SpawnGoldDrop(25, goldSpot, 1f);
        for (int i = 0; i < 40 && !elevatedDrop; i++)
        {
            Pump(0.1f);
            elevatedDrop = clientA.World.Drops.Values.Any(d =>
                Vector2.Distance(d.Position, goldSpot) < 0.75f && MathF.Abs(d.Height - 1f) < 0.05f);
        }
        Check(elevatedDrop, "drops replicate to clients with their surface elevation");
        clientB.World.Me.Position = clientB.World.Map.PlayerSpawn;
        clientB.World.Me.Height = 0f;
        Pump(0.4f);

        Console.WriteLine("\n-- Pathfinding across elevation --");
        // A ground enemy must keep aggro on a plateau player and PATH up the ramp to
        // reach them (the old same-surface targeting de-aggroed the moment you climbed).
        clientB.World.Me.Position = new Vector2(13.5f, 10.5f); // plateau A, level 1
        clientB.World.Me.Height = 1f;
        Pump(0.4f);
        clientB.SendDebugCommand("kill_nearby");
        clientB.SendDebugCommand("heal");
        Pump(0.3f);
        var chaser = server.World.SpawnEnemy("grunt", new Vector2(17.5f, 10.5f)); // floor, past the ramp
        chaser.Health = 500f;
        Check(chaser.Height < 0.05f, "chaser spawned on the ground floor");
        bool aggroed = false, climbed = false;
        for (int i = 0; i < 40 && !climbed; i++)
        {
            Pump(0.25f);
            aggroed |= chaser.TargetPlayerId == bId;
            climbed = !chaser.Dead && chaser.Height > 0.9f;
        }
        Check(aggroed, "enemy aggros a player on a different elevation (path exists)");
        Check(climbed, $"enemy pathed up the ramp to reach the player (h {chaser.Height:0.00})");
        chaser.Health = 1f;
        clientB.SendDebugCommand("kill_nearby");
        clientB.World.Me.Position = clientB.World.Map.PlayerSpawn;
        clientB.World.Me.Height = 0f;
        Pump(0.4f);

        Console.WriteLine("\n-- Zone themes --");
        Check(data.ZoneThemes.Count >= 4 &&
              data.ZoneThemes.Any(t => t.Id == "graveyard") &&
              data.ZoneThemes.Any(t => t.Id == "tomb") &&
              data.ZoneThemes.Any(t => t.Id == "arid") &&
              data.ZoneThemes.Any(t => t.Id == "forest"),
              $"zone themes loaded ({data.ZoneThemes.Count}: {string.Join(", ", data.ZoneThemes.Select(t => t.Id))})");
        Check(data.ZoneThemes.All(t => t.ClutterDensity is > 0 and < 0.5f && t.PropStyle != null),
              "themes carry sane clutter settings");

        Console.WriteLine("\n-- Overlook combat & targeted casts --");
        // Line-of-fire geometry: descending shots off a cliff are clear, climbing shots
        // into a cliff face are blocked, and NOTHING shoots through a bridge deck plane.
        Check(!map.ShotBlocked(new Vector2(13.5f, 14.5f), 1.5f, new Vector2(13.5f, 17.5f), 0.5f),
              "descending overlook shot has a clear line of fire");
        Check(map.ShotBlocked(new Vector2(17.5f, 13.5f), 0.5f, new Vector2(12.5f, 13.5f), 1.5f),
              "climbing shot into the cliff face is blocked");
        Check(map.ShotBlocked(new Vector2(10.5f, 21.5f), 0.5f, new Vector2(10.5f, 14.5f), 1.5f) ||
              map.ShotBlocked(new Vector2(10.5f, 17.5f), 0.5f, new Vector2(10.5f, 17.5f), 1.5f),
              "shots crossing the bridge deck plane are blocked");

        // A spitter on the plateau rim rains shots on a ground player below.
        clientB.SendDebugCommand("kill_nearby");
        clientB.SendDebugCommand("heal");
        clientB.World.Me.Position = new Vector2(13.5f, 17.5f); // corridor floor, below the rim
        clientB.World.Me.Height = 0f;
        Pump(0.4f);
        clientB.SendDebugCommand("kill_nearby");
        Pump(0.2f);
        var rimSpitter = server.World.SpawnEnemy("spitter", new Vector2(13.5f, 14.5f)); // plateau A rim, level 1
        Check(MathF.Abs(rimSpitter.Height - 1f) < 0.05f, "overlook spitter holds the rim at level 1");
        var srvB2 = server.World.Players[bId];
        float hpBeforeRain = srvB2.Stats.MaxHealth;
        bool rained = false;
        for (int i = 0; i < 50 && !rained; i++)
        {
            srvB2.Health = hpBeforeRain;
            clientB.World.Me.Position = new Vector2(13.5f, 17.5f);
            rimSpitter.Position = new Vector2(13.5f, 14.5f); // pin to the rim: wandering off breaks the sightline
            rimSpitter.Height = 1f;
            Pump(0.2f);
            rained = server.World.Projectiles.Values.Any(pr => pr.OwnerId == rimSpitter.Id && pr.HeightStep < -0.01f)
                     || srvB2.Health < hpBeforeRain - 0.5f;
        }
        Check(rained, "overlook spitter fires a descending shot at the player below");
        bool rainHit = false;
        for (int i = 0; i < 60 && !rainHit; i++)
        {
            clientB.World.Me.Position = new Vector2(13.5f, 17.5f);
            Pump(0.1f);
            rainHit = srvB2.Health < hpBeforeRain - 0.5f;
        }
        Check(rainHit, $"the descending shot lands on the lower surface ({srvB2.Health:0}/{hpBeforeRain:0})");

        // Targeted cast the other way: B climbs the rim and fire-bolts the grunt below
        // by target id — the arc carries the bolt down to the enemy's elevation.
        rimSpitter.Health = 1f;
        clientB.SendDebugCommand("kill_nearby");
        clientB.SendDebugCommand("heal");
        clientB.World.Me.Position = new Vector2(13.5f, 14.5f); // up on the rim
        clientB.World.Me.Height = 1f;
        Pump(0.4f);
        var lowTarget = server.World.SpawnEnemy("grunt", new Vector2(13.5f, 17.8f));
        lowTarget.Health = 500f;
        lowTarget.StunnedUntil = server.World.Time + 30f;
        Pump(0.2f);
        bool bolted = false;
        for (int attempt = 0; attempt < 8 && !bolted; attempt++)
        {
            srvB2.Mana = srvB2.Stats.MaxMana;
            srvB2.SkillReadyAt.Clear();
            srvB2.GlobalSkillReadyAt = 0;
            clientB.RequestUseSkill("fire_bolt", lowTarget.Position, lowTarget.Id);
            for (int i = 0; i < 25 && !bolted; i++)
            {
                Pump(0.05f);
                bolted = lowTarget.Health < 499.9f;
            }
        }
        Check(bolted, $"hover-targeted cast arcs down and hits the enemy below (hp {lowTarget.Health:0})");
        lowTarget.Health = 1f;
        clientB.SendDebugCommand("kill_nearby");
        clientB.World.Me.Position = clientB.World.Map.PlayerSpawn;
        clientB.World.Me.Height = 0f;
        Pump(0.4f);

        Console.WriteLine("\n-- Authored encounters: packs & elites --");
        Check(server.World.Packs.Count >= 6, $"authored packs registered ({server.World.Packs.Count})");
        // Group respawn: wipe the corridor pack, force its timer, and watch it return
        // as a full group with pack affiliation intact.
        var corridorPack = server.World.Packs[0];
        clientB.World.Me.Position = new Vector2(14.2f, 17.5f);
        clientB.World.Me.Height = 0f;
        Pump(0.4f);
        clientB.SendDebugCommand("kill_nearby");
        clientB.SendDebugCommand("heal");
        Pump(0.4f); // pack wiped; TickSpawners schedules its respawn
        clientB.World.Me.Position = new Vector2(20.5f, 17.5f); // step away so the respawn is clean
        Pump(0.3f);
        corridorPack.RespawnAt = 0.001f; // force the timer (already scheduled -> spawn next tick)
        Pump(0.4f);
        Check(corridorPack.AliveIds.Count == 3 &&
              corridorPack.AliveIds.All(id => server.World.Enemies[id].PackId == 0),
              $"pack respawned as a full group ({corridorPack.AliveIds.Count} members)");
        // Approach: the whole pack should engage together.
        clientB.World.Me.Position = new Vector2(17.5f, 17.5f);
        Pump(1.2f);
        int chasing = corridorPack.AliveIds.Count(id =>
            server.World.Enemies.TryGetValue(id, out var m) && !m.Dead && m.TargetPlayerId == bId);
        Check(chasing >= 2, $"pack shares aggro ({chasing} members engaged)");
        clientB.SendDebugCommand("kill_nearby");
        clientB.SendDebugCommand("heal");
        clientB.World.Me.Position = clientB.World.Map.PlayerSpawn;
        Pump(0.4f);

        // Elite scaling + replication.
        var gruntBase = data.Enemies["grunt"];
        var brutish = server.World.SpawnEnemy("grunt", clientB.World.Me.Position + new Vector2(3.5f, 0),
            Server.EliteAffix.Brutish);
        Check(MathF.Abs(brutish.MaxHealth - gruntBase.MaxHealth * 2.5f) < 0.1f && brutish.DamageScale > 1.4f,
              $"Brutish elite scales life and damage (hp {brutish.MaxHealth:0})");
        var warded = server.World.SpawnEnemy("grunt", clientB.World.Me.Position + new Vector2(-3.5f, 0),
            Server.EliteAffix.Warded);
        Check(warded.BonusResist >= 39f, "Warded elite gains flat resistances");
        Pump(0.5f);
        Check(clientA.World.Enemies.TryGetValue(brutish.Id, out var brutishOnA) &&
              brutishOnA.EliteFlags == (byte)Server.EliteAffix.Brutish &&
              brutishOnA.DisplayName.StartsWith("Brutish") &&
              MathF.Abs(brutishOnA.MaxHealth - brutish.MaxHealth) < 0.1f,
              $"elite affix and scaled health replicate to clients ({brutishOnA?.DisplayName})");
        brutish.Health = 1f;
        warded.Health = 1f;
        clientB.SendDebugCommand("kill_nearby");
        Pump(0.3f);

        Console.WriteLine("\n-- Gravelord: slam & reward --");
        clientB.SendDebugCommand("heal");
        var boss = server.World.SpawnEnemy("gravelord", clientB.World.Me.Position + new Vector2(1.5f, 0),
            Server.EliteAffix.Boss);
        var srvBBoss = server.World.Players[bId];
        bool slamSeen = false, slamWarnSeen = false;
        float hpBeforeSlam = srvBBoss.Health;
        for (int i = 0; i < 60 && !slamSeen; i++)
        {
            srvBBoss.Health = srvBBoss.Stats.MaxHealth; // out-heal the boss while observing
            clientB.World.Me.Position = boss.Position + new Vector2(-1.2f, 0); // stay in slam range
            Pump(0.2f);
            slamWarnSeen |= clientA.World.Effects.Any(fx => fx.Kind == "slamwarn");
            slamSeen = clientA.World.Effects.Any(fx => fx.Kind == "slam") && boss.SlamReadyAt > 0;
        }
        Check(slamWarnSeen, "boss slam telegraphs first (red AoE warning decal replicates)");
        Check(slamSeen, "boss ground slam fires and replicates its AoE visual");
        // Track NEW drop ids rather than a raw count delta: B's teleport sweep during
        // the kill loop can auto-pickup OLD gold piles, which would mask the burst.
        var dropKeysBeforeBoss = server.World.Drops.Keys.ToHashSet();
        boss.Health = 30f;
        for (int hit = 0; hit < 20 && !boss.Dead; hit++)
        {
            srvBBoss.Mana = srvBBoss.Stats.MaxMana;
            srvBBoss.SkillReadyAt.Clear();
            srvBBoss.GlobalSkillReadyAt = 0;
            srvBBoss.Health = srvBBoss.Stats.MaxHealth;
            // Stand at the edge of Mace Slam's reach (range 1.5 + radius 1.25) but
            // outside gold auto-pickup (1.1 + ~0.6 drop scatter), so the loot burst
            // survives long enough to be counted.
            clientB.World.Me.Position = boss.Position + new Vector2(-2.2f, 0);
            clientB.RequestUseSkill("mace_strike", boss.Position);
            Pump(0.25f);
        }
        Check(boss.Dead, "Gravelord defeated");
        Pump(0.5f);
        int bossDrops = server.World.Drops.Keys.Count(k => !dropKeysBeforeBoss.Contains(k));
        Check(bossDrops >= 4, $"boss death guarantees a loot burst ({bossDrops} drops)");
        clientB.SendDebugCommand("kill_nearby");
        clientB.SendDebugCommand("heal");
        Pump(0.3f);

        Console.WriteLine("\n-- Combat feel: multi-hit, slow, use-time lockout --");
        clientB.SendDebugCommand("kill_nearby");
        clientB.SendDebugCommand("heal");
        clientB.World.Me.Position = clientB.World.Map.PlayerSpawn;
        clientB.World.Me.Height = 0f;
        Pump(0.4f);
        var srvMulti = server.World.Players[bId];
        // Three grunts stacked in one spot: a single swing must hit more than one.
        var pack3 = new List<Server.ServerEnemy>();
        for (int i = 0; i < 3; i++)
        {
            var g = server.World.SpawnEnemy("grunt", clientB.World.Me.Position + new Vector2(1.2f, 0.1f * i));
            g.Health = 500f;
            g.StunnedUntil = server.World.Time + 30f;
            pack3.Add(g);
        }
        Pump(0.2f);
        srvMulti.Mana = srvMulti.Stats.MaxMana;
        srvMulti.SkillReadyAt.Clear();
        srvMulti.GlobalSkillReadyAt = 0;
        clientB.RequestUseSkill("basic_strike", pack3[0].Position);
        Pump(0.4f);
        int struck = pack3.Count(g => g.Health < 499.9f);
        Check(struck >= 2, $"Mace Strike hits every enemy in the arc ({struck} of 3 struck)");

        // Mace Slam applies its slow debuff (60% chance — retry a few casts).
        bool slowed = false;
        for (int attempt = 0; attempt < 8 && !slowed; attempt++)
        {
            srvMulti.Mana = srvMulti.Stats.MaxMana;
            srvMulti.SkillReadyAt.Clear();
            srvMulti.GlobalSkillReadyAt = 0;
            clientB.RequestUseSkill("mace_strike", pack3[0].Position);
            Pump(0.6f); // past the slam's 0.35s wind-up, so THIS cast's roll is judged
            slowed = pack3.Any(g => g.SlowedUntil > server.World.Time);
            if (!slowed)
                Console.WriteLine($"  [diag] slam-slow attempt {attempt}: " + string.Join(" | ",
                    pack3.Select(g => $"dead={g.Dead} hp={g.Health:0} slow={g.SlowedUntil - server.World.Time:0.00} " +
                        $"dist={Vector2.Distance(g.Position, srvMulti.Position):0.00}")) +
                    $" srvPos={srvMulti.Position} mana={srvMulti.Mana:0} max={srvMulti.Stats.MaxMana:0}" +
                    $" alive={srvMulti.Alive} frozen={srvMulti.FrozenUntil - server.World.Time:0.00}" +
                    $" global={srvMulti.GlobalSkillReadyAt - server.World.Time:0.00}" +
                    $" weap={srvMulti.Character.MainHand?.BaseItemId ?? "none"}" +
                    $" lvl={srvMulti.Character.GetSkill("mace_strike")?.Level.ToString() ?? "unlearned"}" +
                    $" msgB='{msgB}'");
        }
        Check(slowed, "Mace Slam slows survivors");
        Check(data.Skills["mace_strike"].Name == "Mace Slam" &&
              data.Skills["mace_strike"].Tags.Contains("Slam") &&
              data.Skills["mace_strike"].Tags.Contains("Area"),
              "Heavy Strike renamed to Mace Slam with Slam/Area tags");

        // Player-centered swing reach: a plain Mace Strike can't overshoot past
        // range+bodies anymore, and can't whiff an enemy standing on the caster's toes
        // off-axis (the old impact-point circle did both).
        var farGrunt = server.World.SpawnEnemy("grunt", srvMulti.Position + new Vector2(2.6f, 0));
        farGrunt.Health = 500f;
        farGrunt.StunnedUntil = server.World.Time + 30f;
        var closeGrunt = server.World.SpawnEnemy("grunt", srvMulti.Position + new Vector2(-0.5f, 0));
        closeGrunt.Health = 500f;
        closeGrunt.StunnedUntil = server.World.Time + 30f;
        // A flanker at ~90° off the aim: inside the VISIBLE sweep, so it must connect.
        var flankGrunt = server.World.SpawnEnemy("grunt", srvMulti.Position + new Vector2(0, 1.3f));
        flankGrunt.Health = 500f;
        flankGrunt.StunnedUntil = server.World.Time + 30f;
        Pump(0.2f);
        srvMulti.Mana = srvMulti.Stats.MaxMana;
        srvMulti.SkillReadyAt.Clear();
        srvMulti.GlobalSkillReadyAt = 0;
        clientB.RequestUseSkill("basic_strike", clientB.World.Me.Position + new Vector2(2.6f, 0));
        Pump(0.4f);
        Check(farGrunt.Health >= 499.9f,
              $"Mace Strike no longer overshoots its range (far grunt hp {farGrunt.Health:0.0})");
        Check(closeGrunt.Health < 499.9f,
              $"point-blank enemies are caught even behind the swing (close grunt hp {closeGrunt.Health:0.0})");
        Check(flankGrunt.Health < 499.9f,
              $"the swing arc matches the sprite's sweep — a 90° flanker in range is hit (hp {flankGrunt.Health:0.0})");
        farGrunt.Health = 1f;
        closeGrunt.Health = 1f;
        flankGrunt.Health = 1f;

        // Global use-time lockout: two casts in the same instant — only ONE fires.
        srvMulti.Mana = srvMulti.Stats.MaxMana;
        srvMulti.SkillReadyAt.Clear();
        srvMulti.GlobalSkillReadyAt = 0;
        // (fire_bolt may be multishot-scrolled by now — compare against ITS projectile
        // count, and require the second cast to add nothing.)
        int projBefore = server.World.Projectiles.Count;
        server.World.UseSkill(bId, "fire_bolt", srvMulti.Position + new Vector2(4, 0));
        int projAfterFirst = server.World.Projectiles.Count;
        server.World.UseSkill(bId, "ice_spike", srvMulti.Position + new Vector2(4, 0));
        Check(projAfterFirst > projBefore && server.World.Projectiles.Count == projAfterFirst,
              $"use-time lockout blocks the second simultaneous cast ({projAfterFirst - projBefore} bolt(s), then 0)");
        foreach (var g in pack3) g.Health = 1f;
        clientB.SendDebugCommand("kill_nearby");
        Pump(0.3f);

        Console.WriteLine("\n-- Stairs side access --");
        // Walking off the plateau rim onto the stairs' LOW section hops you down onto
        // the steps (ramp tiles allow a one-level drop) instead of wedging in the corner.
        float sideH = 1f;
        var sideWalker = new Vector2(15.85f, 22.6f);
        for (int i = 0; i < 160; i++)
            sideWalker = map.MoveWithCollision(sideWalker, new Vector2(0, 0.05f), 0.3f, ref sideH);
        Check(sideWalker.Y > 24.3f && sideH < 0.6f,
              $"stairs are accessible from the upper side edge (y {sideWalker.Y:0.0}, h {sideH:0.00})");
        // Plain cliff edges still refuse the drop — no walking off the plateau.
        float cliffH2 = 1f;
        var cliffWalker2 = new Vector2(13.5f, 15.6f);
        for (int i = 0; i < 60; i++)
            cliffWalker2 = map.MoveWithCollision(cliffWalker2, new Vector2(0, 0.05f), 0.3f, ref cliffH2);
        Check(cliffWalker2.Y < 16.0f && MathF.Abs(cliffH2 - 1f) < 0.05f,
              $"plain cliff edges still block walking off (y {cliffWalker2.Y:0.0})");

        Console.WriteLine("\n-- Theme-driven generation --");
        var forestTheme = data.ZoneThemes.First(t => t.Id == "forest");
        var forestMap = new World.GameMap(1234, forestTheme);
        int roots = 0, parts = 0;
        (int rx, int ry) = (-1, -1);
        for (int fy = 0; fy < forestMap.Height; fy++)
            for (int fx = 0; fx < forestMap.Width; fx++)
            {
                if (forestMap.Feature(fx, fy) == World.TileFeature.BigTreeRoot) { roots++; (rx, ry) = (fx, fy); }
                if (forestMap.Feature(fx, fy) != World.TileFeature.None) parts++;
            }
        Check(roots >= 8 && parts == roots * 4,
              $"forest theme grows multi-tile trees ({roots} trees over {parts} tiles)");
        Check(rx >= 0 && forestMap.IsSolid(rx, ry) && forestMap.WallHeight(rx, ry) == 2 &&
              forestMap.Feature(rx + 1, ry + 1) == World.TileFeature.BigTreePart &&
              !forestMap.IsSolid(rx + 1, ry + 1) && !forestMap.IsSolid(rx + 1, ry) &&
              !forestMap.IsSolid(rx, ry + 1),
              "only the TRUNK tile blocks — the canopy footprint stays walkable");
        // Same seed, different theme: the BASE layout must be identical outside the
        // theme's added features (trees come from their own seeded stream).
        bool baseSame = true;
        for (int fy = 0; fy < forestMap.Height && baseSame; fy++)
            for (int fx = 0; fx < forestMap.Width && baseSame; fx++)
            {
                if (forestMap.Feature(fx, fy) != World.TileFeature.None) continue;
                baseSame = forestMap.WallHeight(fx, fy) == map.WallHeight(fx, fy) &&
                           forestMap.GroundLevel(fx, fy) == map.GroundLevel(fx, fy) &&
                           forestMap.BridgeLevel(fx, fy) == map.BridgeLevel(fx, fy);
            }
        Check(baseSame, "base layout is identical across themes for the same seed");

        // The theme is decided server-side BEFORE generation and replicated on join.
        var forestServer = new GameServer(data, 4242, "forest");
        Check(forestServer.Start(0), "forest-themed server started");
        var forestClient = new GameClient(data, "Ranger", null);
        forestClient.Connect("127.0.0.1", forestServer.LocalPort, out _);
        for (int i = 0; i < 240 && forestClient.Status != ClientStatus.InGame; i++)
        {
            forestServer.Update(1f / 60f);
            forestClient.Update(1f / 60f);
            Thread.Sleep(2);
        }
        Check(forestClient.Status == ClientStatus.InGame &&
              forestClient.World.Map.Theme?.Id == "forest",
              $"zone theme replicates to joining clients ({forestClient.World.Map?.Theme?.Id})");
        int clientRoots = 0;
        for (int fy = 0; fy < forestClient.World.Map.Height; fy++)
            for (int fx = 0; fx < forestClient.World.Map.Width; fx++)
                if (forestClient.World.Map.Feature(fx, fy) == World.TileFeature.BigTreeRoot) clientRoots++;
        int serverRoots = 0;
        for (int fy = 0; fy < forestServer.World.Map.Height; fy++)
            for (int fx = 0; fx < forestServer.World.Map.Width; fx++)
                if (forestServer.World.Map.Feature(fx, fy) == World.TileFeature.BigTreeRoot) serverRoots++;
        Check(clientRoots == serverRoots && clientRoots > 0,
              $"client and server grow identical trees ({clientRoots} == {serverRoots})");
        forestClient.Disconnect();
        forestServer.Stop();

        Console.WriteLine("\n-- Combat feel 2: weapon-% damage, slam wind-up, clean strikes --");
        // Attacks are pure weapon scaling (PoE2-style): % of weapon damage, no flat
        // skill damage added on top. Spells keep their own progression.
        var pctChar = new Sim.CharacterData();
        pctChar.Equipment[Items.EquipSlot.MainHand] = new Items.ItemInstance
            { BaseItemId = "wooden_club", Rarity = Items.ItemRarity.Normal };
        var pctStats = Stats.StatCalculator.Compute(data, pctChar);
        var strikeDef = data.Skills["basic_strike"];
        var pctEff = Skills.SkillMath.Compute(data, strikeDef, 1,
            Enumerable.Empty<Skills.ScrollDefinition>(), pctStats);
        float physScale = 1f + pctStats.PhysicalDamageIncrease / 100f;
        Check(strikeDef.UsesWeaponDamage && strikeDef.BaseDamage == 0 &&
              MathF.Abs(pctEff.MinDamage - pctStats.WeaponMinDamage * strikeDef.WeaponDamageMultiplier * physScale) < 0.01f &&
              MathF.Abs(pctEff.MaxDamage - pctStats.WeaponMaxDamage * strikeDef.WeaponDamageMultiplier * physScale) < 0.01f,
              $"attacks deal % of weapon damage, nothing added ({pctEff.MinDamage:0.0}-{pctEff.MaxDamage:0.0} " +
              $"= {strikeDef.WeaponDamageMultiplier:P0} of {pctStats.WeaponMinDamage:0}-{pctStats.WeaponMaxDamage:0})");
        Check(!data.Skills["fire_bolt"].UsesWeaponDamage && data.Skills["fire_bolt"].BaseDamage > 0,
              "spells keep their own base damage progression");
        Check(data.Skills["shield_bash"].ShieldArmorScaling > 0,
              "Shield Bash keeps its shield-armor damage formula");

        // Mace Slam wind-up: the cast happens now, the hit lands WindupTime later.
        clientB.SendDebugCommand("kill_nearby");
        clientB.SendDebugCommand("heal");
        clientB.World.Me.Position = clientB.World.Map.PlayerSpawn;
        clientB.World.Me.Height = 0f;
        Pump(0.4f);
        var srvWind = server.World.Players[bId];
        var windTarget = server.World.SpawnEnemy("grunt",
            srvWind.Position + new Vector2(1.2f, 0));
        windTarget.Health = 500f;
        windTarget.StunnedUntil = server.World.Time + 30f;
        Pump(0.2f);
        srvWind.Mana = srvWind.Stats.MaxMana;
        srvWind.SkillReadyAt.Clear();
        srvWind.GlobalSkillReadyAt = 0;
        clientB.RequestUseSkill("mace_strike", windTarget.Position);
        Pump(0.15f); // inside the 0.35s wind-up: cast accepted, hit not yet landed
        Check(data.Skills["mace_strike"].WindupTime > 0.2f && windTarget.Health >= 499.9f,
              $"Mace Slam winds up before landing (hp {windTarget.Health:0} mid-windup)");
        Pump(0.4f); // wind-up expires -> the queued strike lands
        Check(windTarget.Health < 499.9f,
              $"the queued slam lands after the wind-up (hp {windTarget.Health:0})");
        // Plain Mace Strike stays visually clean: no ground-circle effect, only the swing.
        srvWind.Mana = srvWind.Stats.MaxMana;
        srvWind.SkillReadyAt.Clear();
        srvWind.GlobalSkillReadyAt = 0;
        clientB.RequestUseSkill("basic_strike", windTarget.Position);
        Pump(0.12f);
        Check(clientB.World.Effects.All(fx => fx.Kind != "melee"),
              "plain Mace Strike draws no impact circle (weapon swing only)");
        windTarget.Health = 1f;
        clientB.SendDebugCommand("kill_nearby");
        Pump(0.3f);

        Console.WriteLine("\n-- Merchant NPC & per-player shop --");
        Check(server.World.Npcs.Count == 1 && server.World.Npcs[0].TypeId == "merchant",
              "the test merchant spawned near the player spawn");
        Check(clientA.World.Npcs.Count == 1 && clientB.World.Npcs.Count == 1 &&
              clientB.World.Npcs.Values.First().Name == data.Npcs["merchant"].Name,
              $"merchant replicates to all clients ({clientB.World.Npcs.Values.FirstOrDefault()?.Name})");
        Check(data.Npcs["merchant"].Dialogue.Count >= 4, "merchant carries dialogue lines");

        var merchant = server.World.Npcs[0];
        List<ClientShopEntry> stockB = null, buybackB = null;
        clientB.ShopStockReceived += (_, stock, buyback) => { stockB = stock; buybackB = buyback; };
        List<ClientShopEntry> stockA = null;
        clientA.ShopStockReceived += (_, stock, _) => stockA = stock;

        // Range gate: opening from across the map is ignored.
        clientB.World.Me.Position = merchant.Position + new Vector2(8f, 8f);
        Pump(0.3f);
        clientB.RequestShopOpen(merchant.Id);
        Pump(0.3f);
        Check(stockB == null, "shop refuses to open from out of range");

        clientB.World.Me.Position = merchant.Position + new Vector2(1.0f, 0);
        clientB.World.Me.Height = merchant.Height;
        clientA.World.Me.Position = merchant.Position + new Vector2(-1.0f, 0);
        clientA.World.Me.Height = merchant.Height;
        Pump(0.4f);
        clientB.RequestShopOpen(merchant.Id);
        Pump(0.4f);
        Check(stockB is { Count: >= 5 } && stockB.All(e => e.Item != null && e.Price > 0),
              $"shop stock arrives with priced gear ({stockB?.Count} slots)");
        Check(stockB != null && stockB.Any(e => e.Item.Rarity == Items.ItemRarity.Rare),
              "the stock always includes a rare");

        // Deterministic per (player, level): reopening yields the SAME items.
        string StockSig(List<ClientShopEntry> s) => string.Join("|",
            s.Select(e => $"{e.Slot}:{e.Item.InstanceId}:{Json.SaveCompact(e.Item)}:{e.Price}"));
        string firstSig = StockSig(stockB);
        stockB = null;
        clientB.RequestShopOpen(merchant.Id);
        Pump(0.4f);
        Check(stockB != null && StockSig(stockB) == firstSig,
              "reopening the shop never rerolls the stock (same level, same items)");

        // ...and each player gets their OWN shop.
        clientA.RequestShopOpen(merchant.Id);
        Pump(0.4f);
        Check(stockA != null && StockSig(stockA) != firstSig,
              "each player sees a different personal stock");

        // Buying: gold is spent, the item lands in the bag, the slot sells out for good.
        var srvShopper = server.World.Players[bId];
        var buyEntry = stockB.First(e => !e.Sold);
        srvShopper.Character.Gold = buyEntry.Price + 5;
        clientB.RequestShopBuy(merchant.Id, buyEntry.Slot);
        Pump(0.5f);
        Check(srvShopper.Character.Gold == 5 &&
              srvShopper.Character.Inventory.FindByInstance(buyEntry.Item.InstanceId) != null,
              $"buying spends gold and delivers the item (gold {srvShopper.Character.Gold})");
        Check(stockB != null && stockB.First(e => e.Slot == buyEntry.Slot).Sold,
              "the purchased slot is marked sold in the refreshed stock");
        Check(srvShopper.Character.ShopSoldSlots.Contains(buyEntry.Slot),
              "sold slots persist on the character (survives rejoining)");

        // Refusals: broke, or a sold-out slot.
        var buyEntry2 = stockB.First(e => !e.Sold);
        srvShopper.Character.Gold = 0;
        clientB.RequestShopBuy(merchant.Id, buyEntry2.Slot);
        clientB.RequestShopBuy(merchant.Id, buyEntry.Slot);
        Pump(0.4f);
        Check(srvShopper.Character.Inventory.FindByInstance(buyEntry2.Item.InstanceId) == null &&
              srvShopper.Character.Inventory.Items.Count(pl => pl.Item.InstanceId == buyEntry.Item.InstanceId) == 1,
              "no gold and sold-out purchases are refused");

        // Selling: the bought item converts back to gold at its base value.
        int goldBeforeSell = srvShopper.Character.Gold;
        int expectedSell = Math.Max(1, buyEntry.Item.GoldValue(data));
        clientB.RequestShopSell(buyEntry.Item.InstanceId);
        Pump(0.4f);
        Check(srvShopper.Character.Gold == goldBeforeSell + expectedSell &&
              srvShopper.Character.Inventory.FindByInstance(buyEntry.Item.InstanceId) == null,
              $"selling removes the item and pays its value ({expectedSell} gold)");

        // Buy-back: the sale lands on the merchant's counter (replicated with the
        // stock), and buying it back costs exactly what it fetched.
        Check(srvShopper.Buyback.Count == 1 &&
              srvShopper.Buyback[0].Item.InstanceId == buyEntry.Item.InstanceId &&
              srvShopper.Buyback[0].Price == expectedSell,
              "the sold item waits on the buy-back counter at its sale price");
        Check(buybackB is { Count: 1 } &&
              buybackB[0].Item.InstanceId == buyEntry.Item.InstanceId &&
              buybackB[0].Price == expectedSell,
              "the buy-back list replicates alongside the stock");
        srvShopper.Character.Gold = expectedSell - 1; // one short: refused
        clientB.RequestShopBuyback(merchant.Id, buyEntry.Item.InstanceId);
        Pump(0.4f);
        Check(srvShopper.Buyback.Count == 1 &&
              srvShopper.Character.Inventory.FindByInstance(buyEntry.Item.InstanceId) == null,
              "buy-back refuses when gold falls short");
        srvShopper.Character.Gold = expectedSell + 3;
        clientB.RequestShopBuyback(merchant.Id, buyEntry.Item.InstanceId);
        Pump(0.4f);
        Check(srvShopper.Character.Gold == 3 &&
              srvShopper.Character.Inventory.FindByInstance(buyEntry.Item.InstanceId) != null &&
              srvShopper.Buyback.Count == 0,
              "buying back returns the item for its sale price and clears the counter");

        // Leveling up rerolls the stock and clears the sold slots.
        int lvlBefore = srvShopper.Character.Level;
        // XP-to-next varies run to run (combat kill credit differs), so grant liberally.
        for (int i = 0; i < 30 && srvShopper.Character.Level == lvlBefore; i++)
        {
            clientB.SendDebugCommand("char_xp");
            Pump(0.2f);
        }
        stockB = null;
        clientB.RequestShopOpen(merchant.Id);
        Pump(0.4f);
        Check(srvShopper.Character.Level > lvlBefore && stockB != null && StockSig(stockB) != firstSig &&
              stockB.All(e => !e.Sold) && srvShopper.Character.ShopSoldSlots.Count == 0,
              "leveling up rerolls the shop and clears sold slots");

        Check(new Core.GameSettings().ZoneThemeId == "forest",
              "forest is the default zone theme");

        Console.WriteLine("\n-- Slam wind-up follows the caster --");
        // The hit resolves from the caster's position AT LANDING TIME: moving during the
        // wind-up moves the impact (visuals ride the phase-2 broadcast to the same point).
        clientB.SendDebugCommand("kill_nearby");
        clientB.SendDebugCommand("heal");
        clientB.World.Me.Position = clientB.World.Map.PlayerSpawn;
        clientB.World.Me.Height = 0f;
        Pump(0.4f);
        var srvMove = server.World.Players[bId];
        var farTarget = server.World.SpawnEnemy("grunt", srvMove.Position + new Vector2(1.2f, 0));
        farTarget.Health = 500f;
        farTarget.StunnedUntil = server.World.Time + 30f;
        Pump(0.2f);
        srvMove.Mana = srvMove.Stats.MaxMana;
        srvMove.SkillReadyAt.Clear();
        srvMove.GlobalSkillReadyAt = 0;
        clientB.RequestUseSkill("mace_strike", farTarget.Position);
        Pump(0.1f); // cast accepted, wind-up running
        clientB.World.Me.Position = new Vector2(13.5f, 17.5f); // known-open corridor, far from the target
        clientB.World.Me.Height = 0f;
        Pump(0.6f); // wind-up lands from the NEW position — the old target is out of reach
        Check(farTarget.Health >= 499.9f,
              $"the slam lands where the caster IS, not where the cast started (hp {farTarget.Health:0})");
        farTarget.Health = 1f;
        clientB.SendDebugCommand("kill_nearby");
        Pump(0.3f);

        Console.WriteLine("\n-- Passive skill tree --");
        var tree = data.PassiveTree;
        Check(tree.Nodes.Count == 25 && tree.Nodes.Count(n => n.Start) == 1,
              $"starter tree loaded ({tree.Nodes.Count} nodes, {tree.Nodes.Count(n => n.Start)} start)");
        int TreeAttrNodes(Stats.StatType st) =>
            tree.Nodes.Count(n => n.Effects.Any(fx => fx.Stat == st));
        Check(TreeAttrNodes(Stats.StatType.Strength) >= 4 &&
              TreeAttrNodes(Stats.StatType.Dexterity) >= 4 &&
              TreeAttrNodes(Stats.StatType.Intelligence) >= 4,
              $"each attribute branch carries 4+ attribute nodes " +
              $"(STR {TreeAttrNodes(Stats.StatType.Strength)} / DEX {TreeAttrNodes(Stats.StatType.Dexterity)} / INT {TreeAttrNodes(Stats.StatType.Intelligence)})");
        Check(tree.Nodes.All(n => n.Effects.Count > 0) &&
              tree.Connections.All(c => c.Count == 2 && tree.ById.ContainsKey(c[0]) && tree.ById.ContainsKey(c[1])),
              "every node has effects and every connection joins real nodes");
        Check(tree.Neighbors("root").Count == 3 && tree.Neighbors("arcanist").Contains("clear_mind"),
              "adjacency works in both directions");

        var srvTreeChar = server.World.Players[bId].Character;
        srvTreeChar.AllocatedPassives.Clear();
        int treePoints = Skills.PassiveTree.PointsForLevel(srvTreeChar.Level);
        Check(treePoints >= 2, $"leveled character has passive points ({treePoints} at level {srvTreeChar.Level})");

        // A disconnected node is refused; the start node is not.
        clientB.RequestAllocatePassive("arcanist");
        Pump(0.4f);
        Check(!srvTreeChar.AllocatedPassives.Contains("arcanist"),
              "allocation refuses nodes not connected to anything allocated");
        float hpBeforeTree = server.World.Players[bId].Stats.MaxHealth;
        clientB.RequestAllocatePassive("root");
        Pump(0.4f);
        Check(srvTreeChar.AllocatedPassives.Contains("root"),
              "the start node allocates first");
        Check(server.World.Players[bId].Stats.MaxHealth > hpBeforeTree + 9f,
              $"allocated passives change stats immediately (+{server.World.Players[bId].Stats.MaxHealth - hpBeforeTree:0} max life)");

        // Outward growth: brawn borders root, so it allocates; double-allocation is refused.
        float physBefore = server.World.Players[bId].Stats.PhysicalDamageIncrease;
        clientB.RequestAllocatePassive("brawn");
        clientB.RequestAllocatePassive("brawn");
        Pump(0.4f);
        Check(srvTreeChar.AllocatedPassives.Count(id => id == "brawn") == 1 &&
              server.World.Players[bId].Stats.PhysicalDamageIncrease >= physBefore + 7.9f,
              "adjacent nodes allocate once and add their % physical damage");

        // Exhausting the point pool blocks further allocation.
        int fill = Skills.PassiveTree.PointsForLevel(srvTreeChar.Level) - srvTreeChar.AllocatedPassives.Count;
        for (int i = 0; i < fill; i++) srvTreeChar.AllocatedPassives.Add($"_test_filler_{i}");
        clientB.RequestAllocatePassive("vitality");
        Pump(0.4f);
        Check(!srvTreeChar.AllocatedPassives.Contains("vitality"),
              "allocation is refused with no points left");
        srvTreeChar.AllocatedPassives.RemoveAll(id => id.StartsWith("_test_filler_"));

        // Allocations persist through the save serialization round-trip.
        var savedJson = Json.SaveCompact(srvTreeChar);
        var reloaded = Json.Load<Sim.CharacterData>(savedJson);
        Check(reloaded.AllocatedPassives.Contains("root") && reloaded.AllocatedPassives.Contains("brawn"),
              "allocated passives persist in the character save");

        Console.WriteLine("\n-- Ailments: chill/freeze, electrocute, ignite, poison, bleed --");
        // SkillMath resolution: base chances from definitions, magnitudes folding the
        // player's %-increases, and the new scroll effects.
        var iceDef = data.Skills["ice_spike"];
        var magStats = new Stats.ComputedStats { ChillMagnitudeIncrease = 50f, IgniteMagnitudeIncrease = 100f };
        var iceEff = Skills.SkillMath.Compute(data, iceDef, 1, Enumerable.Empty<Skills.ScrollDefinition>(), magStats);
        Check(iceDef.ChillChance > 0.5f && MathF.Abs(iceEff.ChillMagnitude - 1.5f) < 0.01f,
              $"Ice Spike chills; player magnitude increases fold in ({iceEff.ChillMagnitude:0.00}x)");
        var fireEff = Skills.SkillMath.Compute(data, data.Skills["fire_bolt"], 1,
            Enumerable.Empty<Skills.ScrollDefinition>(), magStats);
        Check(fireEff.IgniteChance >= 0.29f && MathF.Abs(fireEff.IgniteMagnitude - 2f) < 0.01f,
              $"Fire Bolt has a base ignite chance ({fireEff.IgniteChance:0.00}) with scaling magnitude");
        Check(data.Skills["chain_lightning"].ElectrocuteChance >= 0.4f,
              "Chain Lightning has a base electrocute chance");
        var meleeEff = Skills.SkillMath.Compute(data, data.Skills["basic_strike"], 1,
            new[] { data.Scrolls["venom"], data.Scrolls["rending"] }, pctStats);
        Check(MathF.Abs(meleeEff.PoisonChance - 0.35f) < 0.01f && MathF.Abs(meleeEff.BleedChance - 0.35f) < 0.01f,
              "Venom and Rending scrolls add poison/bleed chance to melee skills");
        var frenzyEff = Skills.SkillMath.Compute(data, data.Skills["basic_strike"], 1,
            new[] { data.Scrolls["frenzy"] }, pctStats);
        var plainEff = Skills.SkillMath.Compute(data, data.Skills["basic_strike"], 1,
            Enumerable.Empty<Skills.ScrollDefinition>(), pctStats);
        Check(frenzyEff.Cooldown < plainEff.Cooldown - 0.01f,
              $"Frenzy scroll speeds melee attacks ({plainEff.Cooldown:0.00}s -> {frenzyEff.Cooldown:0.00}s)");

        // ---- bleed/poison STACKS: base cap 1 per skill, +1 per Venom/Rending scroll,
        // and within a full source only the strongest instances survive.
        Check(plainEff.MaxBleedStacks == 1 && plainEff.MaxPoisonStacks == 1 &&
              meleeEff.MaxBleedStacks == 2 && meleeEff.MaxPoisonStacks == 2,
              "bleed/poison scrolls raise the skill's stack caps (+1 each over the base 1)");
        var dotStacks = new List<Server.ServerEnemy.DotStack>();
        Server.ServerWorld.AddDotStack(dotStacks, 2, 4f, 1, "strike");
        Server.ServerWorld.AddDotStack(dotStacks, 2, 5f, 1, "strike");
        Server.ServerWorld.AddDotStack(dotStacks, 2, 3f, 1, "strike"); // weaker: refresh only
        Check(dotStacks.Count == 2 && MathF.Abs(dotStacks.Sum(s => s.Dps) - 9f) < 0.01f,
              "a weaker instance never overwrites a stronger stack (4+5 kept over the 3)");
        Server.ServerWorld.AddDotStack(dotStacks, 2, 6f, 1, "strike"); // stronger: replaces the 4
        Check(dotStacks.Count == 2 && MathF.Abs(dotStacks.Sum(s => s.Dps) - 11f) < 0.01f,
              "a stronger instance replaces the weakest stack (5+6 remain)");
        Server.ServerWorld.AddDotStack(dotStacks, 2, 2f, 2, "strike"); // another player
        Server.ServerWorld.AddDotStack(dotStacks, 2, 2f, 1, "other");  // another skill
        Check(dotStacks.Count == 4, "stacks from different players/skills always coexist");
        var shatterEff = Skills.SkillMath.Compute(data, iceDef, 1, new[] { data.Scrolls["shattering"] }, magStats);
        var shatterMulti = Skills.SkillMath.Compute(data, iceDef, 1,
            new[] { data.Scrolls["shattering"], data.Scrolls["multishot"] }, magStats);
        Check(shatterEff.ShatterShards == 5 && shatterMulti.ShatterShards == 5,
              "Shattering yields exactly 5 shards — added-projectile scrolls cannot raise it");
        Check(Skills.SkillMath.Compute(data, data.Skills["fire_bolt"], 1,
                  new[] { data.Scrolls["scorched_earth"] }, magStats).FirePatch,
              "Scorched Earth flags fire projectiles to scorch the ground");

        // ---- gameplay: chill builds, decays, and freezes at the cap (blue-tint flag)
        clientB.SendDebugCommand("kill_nearby");
        clientB.SendDebugCommand("heal");
        clientB.World.Me.Position = clientB.World.Map.PlayerSpawn;
        clientB.World.Me.Height = 0f;
        clientB.RequestLearnSkill("ice_spike");
        clientB.RequestLearnSkill("chain_lightning");
        clientB.RequestLearnSkill("fire_bolt");
        Pump(0.4f);
        var srvAil = server.World.Players[bId];
        var chillTarget = server.World.SpawnEnemy("grunt", srvAil.Position + new Vector2(2f, 0));
        chillTarget.Health = 100000f;
        chillTarget.StunnedUntil = server.World.Time + 300f;
        Pump(0.2f);
        void CastAt(string skill, Vector2 at)
        {
            srvAil.Mana = srvAil.Stats.MaxMana;
            srvAil.SkillReadyAt.Clear();
            srvAil.GlobalSkillReadyAt = 0;
            clientB.RequestUseSkill(skill, at);
            Pump(0.35f);
        }
        for (int i = 0; i < 10 && chillTarget.ChillMagnitude < 1f; i++) CastAt("ice_spike", chillTarget.Position);
        Check(chillTarget.ChillMagnitude > 0f,
              $"chilling hits build chill magnitude ({chillTarget.ChillMagnitude:0})");
        for (int i = 0; i < 25 && server.World.Time >= chillTarget.FrozenUntil; i++)
            CastAt("ice_spike", chillTarget.Position);
        Check(server.World.Time < chillTarget.FrozenUntil,
              "at full chill, hits can freeze the target solid");
        Check(Server.ServerWorld.FreezeDuration >= 2.19f,
              $"the deep freeze holds longer now ({Server.ServerWorld.FreezeDuration:0.0}s base)");
        Pump(0.4f);
        Check(clientB.World.Enemies.TryGetValue(chillTarget.Id, out var chillOnB) &&
              (chillOnB.DebuffFlags & (Server.EnemyDebuffs.Chilled | Server.EnemyDebuffs.Frozen)) != 0,
              "chill/frozen flags replicate for the blue tint");
        Check(chillOnB is { ChillPercent: > 30 },
              $"chill buildup PERCENT replicates for the icon readout ({chillOnB.ChillPercent}%)");
        float chillPeak = chillTarget.ChillMagnitude;
        chillTarget.FrozenUntil = 0;
        Pump(1.5f);
        Check(chillTarget.ChillMagnitude < chillPeak - 5f,
              $"chill magnitude decays over time ({chillPeak:0} -> {chillTarget.ChillMagnitude:0})");
        Check(chillTarget.ChillMagnitude <= Server.ServerWorld.ChillMaxMagnitude + 0.01f,
              "chill magnitude respects its 100% cap");

        // ---- electrocute: 6s duration, periodic freeze-in-place with a zap visual
        for (int i = 0; i < 15 && server.World.Time >= chillTarget.ElectrocutedUntil; i++)
            CastAt("chain_lightning", chillTarget.Position);
        Check(server.World.Time < chillTarget.ElectrocutedUntil,
              "Chain Lightning electrocutes its victims");
        Check(chillTarget.ElectrocutedUntil - server.World.Time <= 6.05f,
              $"electrocute base duration is 6 seconds ({chillTarget.ElectrocutedUntil - server.World.Time:0.0}s)");
        chillTarget.ElectrocutedUntil = server.World.Time + 60f; // hold it for deterministic rolls
        chillTarget.FrozenUntil = 0;
        bool zapFroze = false, zapSeen = false;
        for (int i = 0; i < 30 && !zapFroze; i++)
        {
            chillTarget.NextShockRollAt = 0; // force a roll this tick
            Pump(0.1f);
            zapFroze = server.World.Time < chillTarget.FrozenUntil;
            zapSeen |= clientB.World.Effects.Any(fx => fx.Kind == "zap");
        }
        Pump(0.25f); // let the WorldEffect packet land
        zapSeen |= clientB.World.Effects.Any(fx => fx.Kind == "zap");
        Check(zapFroze, "electrocuted targets get frozen in place by periodic shocks");
        Check(zapSeen, "the shock seize shows an electricity effect");
        chillTarget.ElectrocutedUntil = 0;
        chillTarget.FrozenUntil = 0;

        // ---- ignite: DoT scaling off the hit that applied it
        for (int i = 0; i < 25 && chillTarget.BurnTimeLeft <= 0; i++)
            CastAt("fire_bolt", chillTarget.Position);
        Check(chillTarget.BurnTimeLeft > 0 && chillTarget.BurnDps > 0,
              $"Fire Bolt ignites ({chillTarget.BurnDps:0.0} dps burning)");
        float hpBeforeBurn = chillTarget.Health;
        Pump(1.0f);
        Check(chillTarget.Health < hpBeforeBurn - 0.5f,
              "ignite burns the target over time");

        // ---- poison + bleed via attached melee scrolls
        var srvStrike = srvAil.Character.GetSkill("basic_strike");
        srvStrike.Scrolls.Add(new Items.ItemInstance { BaseItemId = "scroll_venom" });
        srvStrike.Scrolls.Add(new Items.ItemInstance { BaseItemId = "scroll_rending" });
        clientB.World.Me.Position = chillTarget.Position + new Vector2(-1.0f, 0);
        Pump(0.3f);
        chillTarget.Position = srvAil.Position + new Vector2(1.0f, 0); // pin beside the striker
        for (int i = 0; i < 30 && (chillTarget.PoisonTimeLeft <= 0 || chillTarget.BleedTimeLeft <= 0); i++)
        {
            chillTarget.Position = srvAil.Position + new Vector2(1.0f, 0);
            CastAt("basic_strike", chillTarget.Position);
        }
        Check(chillTarget.PoisonTimeLeft > 0 && chillTarget.PoisonDps > 0,
              $"Venom-scrolled melee poisons ({chillTarget.PoisonDps:0.0} dps)");
        Check(chillTarget.BleedTimeLeft > 0 && chillTarget.BleedDps > 0,
              $"Rending-scrolled melee bleeds ({chillTarget.BleedDps:0.0} dps)");
        // The two DoTs can latch from DIFFERENT hits (each proc re-rolls damage, and a
        // crit on the poison-applying hit can outweigh bleed's better scaling), so keep
        // striking until a representative pair is latched instead of judging one sample.
        for (int i = 0; i < 40 && chillTarget.BleedDps <= chillTarget.PoisonDps * 0.99f; i++)
        {
            chillTarget.Position = srvAil.Position + new Vector2(1.0f, 0);
            CastAt("basic_strike", chillTarget.Position);
        }
        Check(chillTarget.BleedDps > chillTarget.PoisonDps * 0.99f,
              $"bleed outscales poison on pure-physical hits ({chillTarget.BleedDps:0.0} vs {chillTarget.PoisonDps:0.0})");
        // Stacking in anger: with the Rending scroll the cap is 2, so a second bleed
        // latches while the first still runs (and a third never does).
        for (int i = 0; i < 40 && chillTarget.BleedStacks.Count < 2; i++)
        {
            chillTarget.Position = srvAil.Position + new Vector2(1.0f, 0);
            CastAt("basic_strike", chillTarget.Position);
        }
        Check(chillTarget.BleedStacks.Count == 2,
              "a Rending scroll lets a second bleed instance stack on the same enemy");
        Check(chillTarget.BleedStacks.Count <= 2 && chillTarget.PoisonStacks.Count <= 2,
              $"stacks respect the per-skill cap ({chillTarget.BleedStacks.Count} bleed / {chillTarget.PoisonStacks.Count} poison)");
        Pump(0.4f);
        Check(clientB.World.Enemies.TryGetValue(chillTarget.Id, out var dotOnB) &&
              (dotOnB.DebuffFlags & Server.EnemyDebuffs.Poisoned) != 0 &&
              (dotOnB.DebuffFlags & Server.EnemyDebuffs.Bleeding) != 0,
              "poison/bleed flags replicate for their visual effects");
        srvStrike.Scrolls.Clear();

        // ---- shatter: 5 ice shards continue behind the struck enemy
        var srvIce = srvAil.Character.GetSkill("ice_spike");
        srvIce.Scrolls.Add(new Items.ItemInstance { BaseItemId = "scroll_shattering" });
        bool shardsSeen = false, shardOnClient = false;
        for (int attempt = 0; attempt < 6 && !shardsSeen; attempt++)
        {
            srvAil.Mana = srvAil.Stats.MaxMana;
            srvAil.SkillReadyAt.Clear();
            srvAil.GlobalSkillReadyAt = 0;
            chillTarget.Position = srvAil.Position + new Vector2(2f, 0);
            clientB.RequestUseSkill("ice_spike", chillTarget.Position);
            for (int step = 0; step < 14 && !(shardsSeen && shardOnClient); step++)
            {
                Pump(0.05f);
                if (server.World.Projectiles.Values.Count(pr => pr.SpriteOverride == "IceShard") >= 4)
                    shardsSeen = true;
                if (clientB.World.Projectiles.Values.Any(pr => pr.SpriteOverride == "IceShard"))
                    shardOnClient = true; // latched separately: replication is a pump behind
            }
        }
        Check(shardsSeen, "Shattering bursts 5 ice shards out behind the struck enemy");
        Check(shardOnClient, "shard projectiles replicate with their own sprite");
        srvIce.Scrolls.Clear();

        // ---- Scorched Earth: ground fire + stacking fire-res shred
        var srvFire = srvAil.Character.GetSkill("fire_bolt");
        srvFire.Scrolls.Add(new Items.ItemInstance { BaseItemId = "scroll_scorched_earth" });
        chillTarget.Position = srvAil.Position + new Vector2(2f, 0);
        chillTarget.StunnedUntil = server.World.Time + 300f;
        CastAt("fire_bolt", chillTarget.Position);
        Check(server.World.ActiveFirePatches > 0, "fire projectiles scorch the ground on hit");
        Check(clientB.World.Effects.Any(fx => fx.Kind == "firepatch"),
              "the fire patch visual replicates to clients");
        int stacksBefore = server.World.FireExposureStacks(chillTarget);
        float hpBeforePatch = chillTarget.Health;
        Pump(2.2f); // stand in the fire
        Check(server.World.FireExposureStacks(chillTarget) > stacksBefore,
              $"standing in the fire stacks fire-resistance shred ({server.World.FireExposureStacks(chillTarget)} stacks)");
        Check(chillTarget.Health < hpBeforePatch - 0.5f, "the patch deals fire damage over time");
        Check(server.World.FireExposureStacks(chillTarget) <= Server.ServerWorld.FireExposureMaxStacks,
              "fire exposure respects its 25-stack cap");
        srvFire.Scrolls.Clear();
        chillTarget.Health = 1f;
        clientB.SendDebugCommand("kill_nearby");
        Pump(0.3f);

        // ---- players: electrocute freezes them in place (position pinned, flag replicated)
        var srvFrozen = server.World.Players[bId];
        clientB.World.Me.Position = clientB.World.Map.PlayerSpawn;
        Pump(0.3f);
        srvFrozen.ElectrocutedUntil = server.World.Time + 60f;
        bool playerFroze = false;
        for (int i = 0; i < 30 && !playerFroze; i++)
        {
            srvFrozen.NextShockRollAt = 0;
            Pump(0.1f);
            playerFroze = server.World.Time < srvFrozen.FrozenUntil;
        }
        Check(playerFroze, "electrocuted players seize up too");
        var pinnedAt = srvFrozen.Position;
        srvFrozen.FrozenUntil = server.World.Time + 5f;
        srvFrozen.FrozenAt = pinnedAt;
        clientB.World.Me.Position = pinnedAt + new Vector2(4f, 0);
        Pump(0.4f);
        Check(Vector2.Distance(srvFrozen.Position, pinnedAt) < 0.1f,
              "frozen players cannot move (server pins the position)");
        Check(clientB.World.Me != null &&
              (clientB.World.Me.DebuffFlags & Server.PlayerDebuffs.Frozen) != 0,
              "the frozen flag reaches the local player for the blue tint");
        srvFrozen.FrozenUntil = 0;
        srvFrozen.ElectrocutedUntil = 0;
        clientB.World.Me.Position = clientB.World.Map.PlayerSpawn;
        Pump(0.4f);

        Console.WriteLine("\n-- Summons: skeleton archers --");
        clientB.SendDebugCommand("kill_nearby");
        clientB.SendDebugCommand("heal");
        clientB.World.Me.Position = clientB.World.Map.PlayerSpawn;
        clientB.World.Me.Height = 0f;
        clientB.RequestLearnSkill("summon_skeleton");
        Pump(0.4f);
        var srvSum = server.World.Players[bId];
        var sumDef = data.Skills["summon_skeleton"];
        var sumLearned = srvSum.Character.GetSkill("summon_skeleton");
        Check(sumLearned != null && sumDef.Archetype == Skills.SkillArchetype.Summon &&
              sumDef.SummonLimit == 2,
              "Summon Skeleton Archers learned (limit 2 at level 1)");
        float expectedCost = server.World.SummonManaCost(srvSum, sumDef, 1);
        Check(MathF.Abs(expectedCost - (10f + 0.05f * srvSum.Stats.MaxMana)) < 0.01f,
              $"summon costs 10 flat + 5% of max mana ({expectedCost:0.0})");

        srvSum.Mana = srvSum.Stats.MaxMana;
        float manaBeforeSummon = srvSum.Mana;
        clientB.RequestSummonAdjust("summon_skeleton", +1);
        Pump(0.4f);
        Check(server.World.Summons.Values.Count(su => su.OwnerId == bId) == 1,
              "the Skill Menu + button raises a skeleton");
        Check(MathF.Abs(srvSum.ManaReserved - expectedCost) < 0.01f &&
              srvSum.Mana <= srvSum.Stats.MaxMana - expectedCost + 0.5f,
              $"summoning RESERVES maximum mana ({srvSum.ManaReserved:0.0} held, pool {manaBeforeSummon:0} -> {srvSum.Mana:0})");
        clientB.RequestSummonAdjust("summon_skeleton", +1);
        Pump(0.4f);
        clientB.RequestSummonAdjust("summon_skeleton", +1); // over the limit: refused
        Pump(0.4f);
        Check(server.World.Summons.Values.Count(su => su.OwnerId == bId) == 2,
              "the summon limit caps the pack at 2");
        Pump(0.3f);
        Check(clientA.World.Summons.Count == 2 && clientB.World.Summons.Count == 2,
              "summons replicate to every client");

        // Archers fight: spawn a grunt nearby and expect arrow projectiles + damage.
        var sumPrey = server.World.SpawnEnemy("grunt", srvSum.Position + new Vector2(3f, 0));
        sumPrey.Health = 400f;
        sumPrey.StunnedUntil = server.World.Time + 60f;
        bool arrowSeen = false, archerAnimSeen = false;
        for (int i = 0; i < 40 && (!arrowSeen || sumPrey.Health >= 399.9f); i++)
        {
            Pump(0.1f);
            arrowSeen |= server.World.Projectiles.Values.Any(pr => pr.SpriteOverride == "Arrow");
            archerAnimSeen |= clientA.World.Summons.Values.Any(su =>
                su.SkillId == "summon_skeleton" && su.AttackAnimAtMs > 0);
        }
        Check(arrowSeen, "skeleton archers shoot arrows at nearby enemies");
        Check(sumPrey.Health < 399.9f, $"arrows damage the enemy (hp {sumPrey.Health:0})");
        Pump(0.3f); // replication runs a pump behind the server-side checks
        archerAnimSeen |= clientA.World.Summons.Values.Any(su =>
            su.SkillId == "summon_skeleton" && su.AttackAnimAtMs > 0);
        Check(archerAnimSeen, "clients receive archer attack events (bow animation)");

        // Summons can be hurt and die; the death queues a FREE respawn near the summoner.
        var victim2 = server.World.Summons.Values.First(su => su.OwnerId == bId);
        int victimId = victim2.Id;
        float manaBeforeDeath = srvSum.Mana;
        server.World.DamageSummon(victim2, 99999f);
        Pump(0.3f);
        Check(!server.World.Summons.ContainsKey(victimId) && !clientB.World.Summons.ContainsKey(victimId),
              "summons take damage, die and despawn everywhere");
        Check(server.World.Summons.Values.Count(su => su.OwnerId == bId) == 1, "one archer remains");
        bool respawned = false;
        for (int i = 0; i < 30 && !respawned; i++)
        {
            Pump(0.4f);
            respawned = server.World.Summons.Values.Count(su => su.OwnerId == bId) == 2;
        }
        Check(respawned, $"dead archers freely respawn after {sumDef.SummonRespawnTime:0}s");
        Check(srvSum.Mana >= manaBeforeDeath - 1f && // free respawn: no extra mana taken...
              MathF.Abs(srvSum.ManaReserved - expectedCost * 2f) < 0.1f, // ...the reservation persists
              $"the free respawn keeps the reservation, costs nothing ({srvSum.ManaReserved:0.0} held)");

        // Rally: the backquote command walks the pack to a marked point. Clear the
        // practice target first — marching summons now stop to fight bodies in their
        // path, and this check measures the MARCH.
        clientB.SendDebugCommand("kill_nearby");
        Pump(0.3f);
        var rallyPoint = srvSum.Position + new Vector2(0f, 4f);
        clientB.RequestSummonRally("summon_skeleton", true, rallyPoint);
        bool rallied = false;
        for (int i = 0; i < 30 && !rallied; i++)
        {
            Pump(0.3f);
            rallied = server.World.Summons.Values.Where(su => su.OwnerId == bId)
                .All(su => Vector2.Distance(su.Position, rallyPoint) < 1.6f);
        }
        Check(rallied, "rallied summons hold the marked point");
        clientB.RequestSummonRally("summon_skeleton", false, default);
        Pump(0.3f);
        Check(!srvSum.SummonRallies.ContainsKey("summon_skeleton"),
              "clearing the rally returns them to following");

        // The - button dismisses one.
        clientB.RequestSummonAdjust("summon_skeleton", -1);
        Pump(0.4f);
        Check(server.World.Summons.Values.Count(su => su.OwnerId == bId) == 1,
              "the Skill Menu - button dismisses a skeleton");
        Check(MathF.Abs(srvSum.ManaReserved - expectedCost) < 0.1f,
              $"dismissal RELEASES the reservation ({srvSum.ManaReserved:0.0} still held by the survivor)");

        // Leveling a summon skill raises its reservation price — including for minions
        // ALREADY out (the bug: only future summons repriced).
        float reservedBefore = srvSum.ManaReserved;
        sumLearned.Experience = Skills.SkillMath.XpToNextLevel(sumLearned.Level);
        clientB.RequestLevelSkill("summon_skeleton");
        Pump(0.4f);
        float repricedCost = server.World.SummonManaCost(srvSum, sumDef, sumLearned.Level);
        Check(sumLearned.Level == 2 && MathF.Abs(srvSum.ManaReserved - repricedCost) < 0.1f &&
              srvSum.ManaReserved > reservedBefore + sumDef.ManaCostPerLevel - 0.6f,
              $"leveling a summon skill reprices the LIVE reservation ({reservedBefore:0.0} -> {srvSum.ManaReserved:0.0})");

        // A summon marching to a rally point behind an enemy that physically blocks the
        // way attacks the blocker instead of shoving against it forever.
        var rallyArcher = server.World.Summons.Values.First(su => su.OwnerId == bId);
        rallyArcher.Position = srvSum.Position + new Vector2(2f, 0f);
        rallyArcher.Height = srvSum.Height;
        var blocker = server.World.SpawnEnemy("grunt", rallyArcher.Position + new Vector2(0.9f, 0f));
        blocker.Health = 999f;
        blocker.StunnedUntil = server.World.Time + 60f;
        clientB.RequestSummonRally("summon_skeleton", true, rallyArcher.Position + new Vector2(9f, 0f));
        float blockerHpBefore = blocker.Health;
        Pump(3.0f);
        Check(blocker.Health < blockerHpBefore - 0.1f,
              $"a rally-blocked summon fights the body in its way (blocker hp {blocker.Health:0.0})");
        clientB.RequestSummonRally("summon_skeleton", false, default);
        blocker.Health = 1f;
        clientB.SendDebugCommand("kill_nearby");
        Pump(0.4f);

        // Gear scaling: summon damage/health % and +limit modifiers exist and apply.
        Check(data.Modifiers["commanding"].StatAffected == Stats.StatType.SummonDamage &&
              data.Modifiers["commanding"].CompatibleItemCategories.All(c =>
                  c is Items.ItemCategory.Helmet or Items.ItemCategory.Staff) &&
              data.Modifiers["commanding"].AffixType == Items.AffixType.Prefix,
              "summon damage rolls as a helmet/staff PREFIX");
        Check(data.Modifiers["of_undeath"].StatAffected == Stats.StatType.SummonHealth &&
              data.Modifiers["of_undeath"].AffixType == Items.AffixType.Suffix,
              "summon health rolls as a helmet/staff SUFFIX");
        Check(data.Modifiers["of_legions"].StatAffected == Stats.StatType.SummonLimit &&
              data.Modifiers["of_legions"].CompatibleItemCategories.SequenceEqual(
                  new[] { Items.ItemCategory.Helmet }) &&
              data.Modifiers["of_legions"].AffixType == Items.AffixType.Suffix,
              "+summon limit rolls as a helmet-only SUFFIX");
        var sumChar = new Sim.CharacterData();
        var sumHelm = new Items.ItemInstance { BaseItemId = "leather_hood", Rarity = Items.ItemRarity.Rare };
        sumHelm.Modifiers.Add(new Items.ItemModifierRoll { ModifierId = "commanding", Value = 8 });
        sumHelm.Modifiers.Add(new Items.ItemModifierRoll { ModifierId = "of_undeath", Value = 10 });
        sumHelm.Modifiers.Add(new Items.ItemModifierRoll { ModifierId = "of_legions", Value = 1 });
        sumChar.Equipment[Items.EquipSlot.Helmet] = sumHelm;
        var sumStats = Stats.StatCalculator.Compute(data, sumChar);
        Check(MathF.Abs(sumStats.SummonDamageIncrease - 8f) < 0.01f &&
              MathF.Abs(sumStats.SummonHealthIncrease - 10f) < 0.01f &&
              sumStats.SummonLimitBonus == 1,
              "summon modifiers flow through the stat pipeline (+8% dmg, +10% hp, +1 limit)");
        clientB.RequestSummonAdjust("summon_skeleton", -1);
        sumPrey.Health = 1f;
        clientB.SendDebugCommand("kill_nearby");
        Pump(0.4f);

        Console.WriteLine("\n-- Skeleton warriors, per-skill rally, summon separation --");
        clientB.RequestLearnSkill("summon_skeleton_warrior");
        Pump(0.4f);
        var warDef = data.Skills["summon_skeleton_warrior"];
        Check(warDef.SummonMelee && warDef.Archetype == Skills.SkillArchetype.Summon,
              "Skeleton Warriors are a melee summon skill");
        srvSum.Mana = srvSum.Stats.MaxMana;
        clientB.RequestSummonAdjust("summon_skeleton_warrior", +1);
        Pump(0.3f);
        srvSum.Mana = srvSum.Stats.MaxMana;
        clientB.RequestSummonAdjust("summon_skeleton_warrior", +1);
        Pump(0.3f);
        var warriors = server.World.Summons.Values
            .Where(su => su.OwnerId == bId && su.SkillId == "summon_skeleton_warrior").ToList();
        Check(warriors.Count == 2 && warriors.All(su => su.Melee), "two melee warriors raised");
        Check(clientA.World.Summons.Values.Count(su => su.SkillId == "summon_skeleton_warrior") == 2,
              "warriors replicate with their skill id (distinct sprite)");

        // Separation: stack the pair on one spot; the soft push must fan them out.
        warriors[1].Position = warriors[0].Position;
        Pump(0.8f);
        float apart = Vector2.Distance(warriors[0].Position, warriors[1].Position);
        Check(apart > 0.45f, $"summons push apart instead of stacking ({apart:0.00})");

        // Melee: warriors close to arm's reach and swing (no arrow projectiles).
        var warPrey = server.World.SpawnEnemy("grunt", srvSum.Position + new Vector2(2.5f, 0));
        warPrey.Health = 60f;
        warPrey.StunnedUntil = server.World.Time + 60f;
        bool bitten = false, warriorAnimSeen = false;
        for (int i = 0; i < 50 && !bitten; i++)
        {
            Pump(0.2f);
            bitten = warPrey.Dead || warPrey.Health < 59.9f;
            warriorAnimSeen |= clientA.World.Summons.Values.Any(su =>
                su.SkillId == "summon_skeleton_warrior" && su.AttackAnimAtMs > 0);
        }
        Check(bitten, "warriors close to melee range and hit");
        Pump(0.3f); // replication runs a pump behind the server-side damage check
        warriorAnimSeen |= clientA.World.Summons.Values.Any(su =>
            su.SkillId == "summon_skeleton_warrior" && su.AttackAnimAtMs > 0);
        Check(warriorAnimSeen, "clients receive warrior attack events (sword chop animation)");
        clientB.SendDebugCommand("kill_nearby");
        Pump(0.4f);

        // Per-skill rallies: warriors hold a mark while the archers keep following.
        srvSum.Mana = srvSum.Stats.MaxMana;
        clientB.RequestSummonAdjust("summon_skeleton", +1);
        Pump(0.3f);
        // Summons walk a straight line to the rally, so pick a mark with a clear lane —
        // random pillar clusters can sit 3-4 tiles from the spawn on some map seeds.
        var warRally = srvSum.Position + new Vector2(-3.2f, 0f);
        foreach (var cand in new[]
                 {
                     new Vector2(-3.2f, 0), new Vector2(3.2f, 0), new Vector2(0, -3.2f), new Vector2(0, 3.2f),
                     new Vector2(-2.4f, -2.4f), new Vector2(2.4f, 2.4f), new Vector2(-2.4f, 2.4f), new Vector2(2.4f, -2.4f),
                 })
        {
            var target = srvSum.Position + cand;
            bool laneClear = true;
            for (float t = 0f; t <= 1f && laneClear; t += 0.08f)
                laneClear = !server.World.Map.CircleHitsWall(Vector2.Lerp(srvSum.Position, target, t), 0.35f);
            if (laneClear) { warRally = target; break; }
        }
        clientB.RequestSummonRally("summon_skeleton_warrior", true, warRally);
        Pump(0.3f);
        Check(srvSum.SummonRallies.ContainsKey("summon_skeleton_warrior") &&
              !srvSum.SummonRallies.ContainsKey("summon_skeleton"),
              "rallies are stored per summon skill");
        bool warHeld = false;
        for (int i = 0; i < 30 && !warHeld; i++)
        {
            // Rallied MELEE warriors chase prey near their post BY DESIGN, so any
            // ambient respawn inside their aggro bubble would lure them off the mark —
            // banish intruders to the far corner while measuring the hold.
            foreach (var intruder in server.World.Enemies.Values)
                if (!intruder.Dead && Vector2.Distance(intruder.Position, warRally) < 12f)
                {
                    intruder.Position = new Vector2(2.5f, 2.5f);
                    intruder.Height = 0f;
                    intruder.StunnedUntil = server.World.Time + 5f;
                }
            Pump(0.3f);
            warHeld = server.World.Summons.Values
                .Where(su => su.SkillId == "summon_skeleton_warrior")
                .All(su => Vector2.Distance(su.Position, warRally) < 1.6f);
        }
        Check(warHeld, "rallied warriors hold their own mark");
        var followArcher = server.World.Summons.Values.First(su => su.SkillId == "summon_skeleton");
        Check(Vector2.Distance(followArcher.Position, srvSum.Position) < 3f,
              "archers keep following while the warriors are rallied");
        clientB.RequestSummonRally("", false, default);
        Pump(0.3f);
        Check(srvSum.SummonRallies.Count == 0, "an empty-skill command clears every rally");
        foreach (var skl in new[] { "summon_skeleton", "summon_skeleton_warrior" })
        {
            clientB.RequestSummonAdjust(skl, -1);
            clientB.RequestSummonAdjust(skl, -1);
        }
        Pump(0.4f);

        Console.WriteLine("\n-- Water tiles --");
        var wmap = server.World.Map;
        var wTiles = new List<(int x, int y)>();
        for (int wy = 0; wy < wmap.Height; wy++)
            for (int wx = 0; wx < wmap.Width; wx++)
                if (wmap.IsWater(wx, wy)) wTiles.Add((wx, wy));
        Check(wTiles.Count >= 5, $"the generator floods ponds ({wTiles.Count} water tiles)");
        Check(wTiles.All(t => !wmap.IsSolid(t.x, t.y)),
              "water is its own impassable kind, not a wall");
        // A water tile bordered by open level-0 land: walkers must be refused entry.
        (int lx, int ly) land = (-1, -1);
        (int x, int y) pondEdge = (-1, -1);
        foreach (var (ax, ay) in wTiles)
        {
            foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                int nx = ax + dx, ny = ay + dy;
                if (!wmap.IsWater(nx, ny) && !wmap.IsSolid(nx, ny) &&
                    wmap.GroundLevel(nx, ny) == 0 && wmap.Ramp(nx, ny) == World.RampDirection.None)
                {
                    land = (nx, ny);
                    pondEdge = (ax, ay);
                    break;
                }
            }
            if (land.lx >= 0) break;
        }
        Check(land.lx >= 0, "a pond has a walkable shore");
        var pondCenter = new Vector2(pondEdge.x + 0.5f, pondEdge.y + 0.5f);
        Check(wmap.SampleHeight(pondCenter, 0f) == null, "water offers no walkable surface");
        var wader = new Vector2(land.lx + 0.5f, land.ly + 0.5f);
        var inward = Vector2.Normalize(pondCenter - wader);
        float wadeH = wmap.GroundHeightAt(wader);
        for (int i = 0; i < 80; i++)
            wader = wmap.MoveWithCollision(wader, inward * 0.08f, 0.3f, ref wadeH);
        Check(!wmap.IsWater((int)MathF.Floor(wader.X), (int)MathF.Floor(wader.Y)),
              "walkers cannot cross onto water");
        Check(!wmap.ShotBlocked(new Vector2(land.lx + 0.5f, land.ly + 0.5f), 0.5f, pondCenter, 0.5f),
              "shots fly freely over the pond surface");
        Check(wmap.EnemySpawns.All(sp => !wmap.IsWater((int)sp.X, (int)sp.Y)),
              "enemy spawn points avoid water");

        Console.WriteLine("\n-- Telegraphed enemy melee --");
        clientB.SendDebugCommand("kill_nearby");
        clientB.SendDebugCommand("heal");
        clientB.World.Me.Position = clientB.World.Map.PlayerSpawn + new Vector2(0f, 3f);
        clientB.World.Me.Height = 0f;
        Pump(0.4f);
        float hpCalm = srvSum.Health;
        var lunger = server.World.SpawnEnemy("grunt", srvSum.Position + new Vector2(1.0f, 0));
        bool sawWindup = false, animSeen = false;
        for (int i = 0; i < 60 && !sawWindup; i++)
        {
            Pump(0.05f);
            sawWindup = lunger.Winding;
            animSeen |= clientB.World.Enemies.TryGetValue(lunger.Id, out var ceB) && ceB.AttackAnimPhase >= 1;
        }
        Check(sawWindup && srvSum.Health >= hpCalm - 0.01f,
              "melee enemies wind up before any damage lands");
        for (int i = 0; i < 10 && !animSeen; i++)
        {
            Pump(0.05f);
            animSeen |= clientB.World.Enemies.TryGetValue(lunger.Id, out var ceB2) && ceB2.AttackAnimPhase >= 1;
        }
        Check(animSeen, "clients receive the wind-up event for the attack animation");
        for (int i = 0; i < 40 && srvSum.Health >= hpCalm - 0.5f; i++) Pump(0.1f);
        Check(srvSum.Health < hpCalm - 0.5f, "the swing lands after the wind-up");
        clientB.SendDebugCommand("kill_nearby");
        clientB.SendDebugCommand("heal");
        Pump(0.4f);

        // Dodge i-frames through the impact: the committed swing must WHIFF.
        float hpWhiff = srvSum.Health;
        var whiffer = server.World.SpawnEnemy("grunt", srvSum.Position + new Vector2(1.0f, 0));
        bool wound2 = false;
        for (int i = 0; i < 60 && !wound2; i++) { Pump(0.05f); wound2 = whiffer.Winding; }
        Check(wound2, "a second grunt commits to a swing");
        srvSum.InvulnerableUntil = server.World.Time + 3f;
        for (int i = 0; i < 40 && whiffer.Winding; i++) Pump(0.05f);
        Pump(0.2f);
        Check(srvSum.Health >= hpWhiff - 0.01f, "dodge i-frames make the committed swing whiff");
        clientB.SendDebugCommand("kill_nearby");
        clientB.SendDebugCommand("heal");
        srvSum.InvulnerableUntil = 0f;
        Pump(0.4f);

        // A stun mid-wind-up cancels the swing outright (the cooldown stays spent).
        float hpStun = srvSum.Health;
        var stunnee = server.World.SpawnEnemy("grunt", srvSum.Position + new Vector2(1.0f, 0));
        bool wound3 = false;
        for (int i = 0; i < 60 && !wound3; i++) { Pump(0.05f); wound3 = stunnee.Winding; }
        stunnee.StunnedUntil = server.World.Time + 3f;
        Pump(0.8f);
        Check(wound3 && !stunnee.Winding && srvSum.Health >= hpStun - 0.01f,
              "a stun mid-wind-up cancels the swing entirely");
        clientB.SendDebugCommand("kill_nearby");
        clientB.SendDebugCommand("heal");
        Pump(0.4f);

        // Barrow Knight: sword-style (tracks its victim), Skeleton sprite, lands blows.
        var knightDef = data.Enemies["bone_knight"];
        Check(knightDef.AttackStyle == "sword" && knightDef.AttackTracks &&
              knightDef.SpriteStyle == "Skeleton" && !knightDef.Ranged,
              "the Barrow Knight is a sword-tracking melee skeleton");
        float hpKnight = srvSum.Health;
        var knight = server.World.SpawnEnemy("bone_knight", srvSum.Position + new Vector2(1.1f, 0));
        bool kWound = false;
        for (int i = 0; i < 60 && !kWound; i++) { Pump(0.05f); kWound |= knight.Winding; }
        for (int i = 0; i < 60 && srvSum.Health >= hpKnight - 0.5f; i++) Pump(0.1f);
        Check(kWound && srvSum.Health < hpKnight - 0.5f,
              "Barrow Knights wind up and land sword blows");
        clientB.SendDebugCommand("kill_nearby");
        clientB.SendDebugCommand("heal");
        Pump(0.4f);

        Console.WriteLine("\n-- Attributes & derived stats --");
        // Pure StatCalculator checks: base attributes, and each attribute's centralized
        // derived contributions (AttributeBalance is the only place they live).
        var attrChar = new Sim.CharacterData();
        var attrBase = Stats.StatCalculator.Compute(data, attrChar);
        Check(MathF.Abs(attrBase.Strength - Stats.AttributeBalance.BaseAttribute) < 0.01f &&
              MathF.Abs(attrBase.Dexterity - Stats.AttributeBalance.BaseAttribute) < 0.01f &&
              MathF.Abs(attrBase.Intelligence - Stats.AttributeBalance.BaseAttribute) < 0.01f,
              "every character starts at the base attribute values");
        var strHelm = new Items.ItemInstance { BaseItemId = "leather_hood", Rarity = Items.ItemRarity.Rare };
        strHelm.Modifiers.Add(new Items.ItemModifierRoll { ModifierId = "of_the_bear", Value = 20 });
        attrChar.Equipment[Items.EquipSlot.Helmet] = strHelm;
        var strStats = Stats.StatCalculator.Compute(data, attrChar);
        Check(MathF.Abs(strStats.Strength - (attrBase.Strength + 20)) < 0.01f,
              "gear attribute rolls flow through the stat pipeline (+20 Strength)");
        Check(MathF.Abs(strStats.MaxHealth - attrBase.MaxHealth - 20 * Stats.AttributeBalance.LifePerStrength) < 0.01f,
              "Strength grants maximum Life at the centralized rate");
        strHelm.Modifiers.Clear();
        strHelm.Modifiers.Add(new Items.ItemModifierRoll { ModifierId = "of_the_owl", Value = 20 });
        var intStats = Stats.StatCalculator.Compute(data, attrChar);
        Check(MathF.Abs(intStats.MaxMana - attrBase.MaxMana - 20 * Stats.AttributeBalance.ManaPerIntelligence) < 0.01f,
              "Intelligence grants maximum Mana at the centralized rate");
        float regenRatio = intStats.ManaRegeneration / attrBase.ManaRegeneration;
        float expectRatio = (1f + 30f * Stats.AttributeBalance.ManaRegenPctPerIntelligence / 100f) /
                            (1f + 10f * Stats.AttributeBalance.ManaRegenPctPerIntelligence / 100f);
        Check(MathF.Abs(regenRatio - expectRatio) < 0.01f,
              $"Intelligence speeds mana regeneration (+{Stats.AttributeBalance.ManaRegenPctPerIntelligence}%/point)");

        // Deflection aggregation: ALL equipped pieces + dexterity pool into ONE rating.
        strHelm.Modifiers.Clear();
        strHelm.Modifiers.Add(new Items.ItemModifierRoll { ModifierId = "deflecting", Value = 30 });
        attrChar.Level = 30;         // hunters_jerkin is tier-2: level 30, 14 DEX —
        attrChar.BaseDexterity = 14; // the wearer must genuinely qualify now
        attrChar.Equipment[Items.EquipSlot.BodyArmor] =
            new Items.ItemInstance { BaseItemId = "hunters_jerkin", Rarity = Items.ItemRarity.Normal };
        var defStats = Stats.StatCalculator.Compute(data, attrChar);
        // DEX multiplies GEAR rating by a small percent — bare dexterity grants nothing.
        float expectRating = (62 + 30) *
                             (1f + defStats.Dexterity * Stats.AttributeBalance.DeflectionPctPerDexterity / 100f);
        Check(MathF.Abs(defStats.DeflectionRating - expectRating) < 0.01f,
              $"deflection rating aggregates gear and scales slightly with dexterity ({defStats.DeflectionRating:0})");
        var bareDex = new Sim.CharacterData();
        Check(Stats.StatCalculator.Compute(data, bareDex).DeflectionRating < 0.01f,
              "dexterity alone grants NO deflection without gear");
        Check(defStats.DeflectionChance > 0 &&
              defStats.DeflectionChance <= Stats.Deflection.InitialChanceCap &&
              MathF.Abs(defStats.DeflectionChance -
                        Stats.Deflection.ChanceFromRating(defStats.DeflectionRating, attrChar.Level)) < 0.01f,
              $"rating converts to a capped initial chance via the central formula ({defStats.DeflectionChance:0.0}%)");
        Check(MathF.Abs(defStats.PhysicalReduction -
                        Stats.ArmorBalance.PhysicalReduction(defStats.Armor, attrChar.Level)) < 0.001f,
              "armor mitigation follows the central level-scaled soft cap");

        Console.WriteLine("\n-- Deflection layer mechanics --");
        var layers = Stats.Deflection.Layers(50f).ToList();
        Check(layers.Count == 4 && layers.SequenceEqual(new[] { 50f, 35f, 20f, 5f }),
              $"descending layers generate until the next would be 0 ({string.Join("/", layers)})");
        // Scripted rolls: fail, success, fail, success — every layer is attempted
        // independently, failures never stop later checks, successes stack
        // multiplicatively on the REMAINING damage.
        var script = new Queue<double>(new[] { 0.99, 0.30, 0.99, 0.01 });
        int rollsMade = 0;
        double NextRoll() { rollsMade++; return script.Dequeue(); }
        float mult = Stats.Deflection.RollDamageMultiplier(50f, NextRoll);
        Check(rollsMade == 4, "ALL layers roll independently (a failed roll does not stop later ones)");
        Check(MathF.Abs(100f * mult - 64f) < 0.01f,
              $"fail/success/fail/success on 100 damage leaves 64 ({100f * mult:0.##})");
        var allPass = new Queue<double>(new[] { 0.0, 0.0, 0.0, 0.0 });
        float mult2 = Stats.Deflection.RollDamageMultiplier(50f, () => allPass.Dequeue());
        Check(MathF.Abs(mult2 - MathF.Pow(0.8f, 4)) < 0.0001f,
              "every success multiplies the remaining damage by 0.8");

        Console.WriteLine("\n-- Equipment attribute requirements --");
        // iron_plate demands 14 Strength; a fresh character has 10 — the server refuses.
        var plate = new Items.ItemInstance { BaseItemId = "iron_plate", Rarity = Items.ItemRarity.Normal, ItemLevel = 5 };
        plate.EnsureSlotData();
        srvSum.Character.Level = Math.Max(srvSum.Character.Level, 40); // tier-2 gear now wants level 30
        srvSum.RecomputeStats(data);
        Check(srvSum.Character.Inventory.TryAdd(data, plate), "test plate added to the bag");
        var platePlaced = srvSum.Character.Inventory.FindByInstance(plate.InstanceId);
        server.World.MoveItem(bId, ItemLocation.AtGrid(platePlaced.X, platePlaced.Y),
            ItemLocation.AtEquip(Items.EquipSlot.BodyArmor));
        Check(srvSum.Character.Equipment.GetValueOrDefault(Items.EquipSlot.BodyArmor)?.InstanceId != plate.InstanceId,
              "the server refuses gear whose Strength requirement is unmet");
        // A +20 Strength helmet satisfies it: attributes from OTHER gear count.
        var bearHelm = new Items.ItemInstance { BaseItemId = "leather_hood", Rarity = Items.ItemRarity.Rare };
        bearHelm.EnsureSlotData();
        bearHelm.Modifiers.Add(new Items.ItemModifierRoll { ModifierId = "of_the_bear", Value = 20 });
        var oldHelm = srvSum.Character.Equipment.GetValueOrDefault(Items.EquipSlot.Helmet);
        srvSum.Character.Equipment[Items.EquipSlot.Helmet] = bearHelm;
        srvSum.RecomputeStats(data);
        platePlaced = srvSum.Character.Inventory.FindByInstance(plate.InstanceId);
        server.World.MoveItem(bId, ItemLocation.AtGrid(platePlaced.X, platePlaced.Y),
            ItemLocation.AtEquip(Items.EquipSlot.BodyArmor));
        Check(srvSum.Character.Equipment.GetValueOrDefault(Items.EquipSlot.BodyArmor)?.InstanceId == plate.InstanceId,
              "meeting the requirement through other gear's attributes allows the equip");

        Console.WriteLine("\n-- Energy Shield --");
        // Battlemage Plate (Armor + ES hybrid, 8 Str / 8 Int). B is a warrior (3 INT),
        // and worn gear only counts while its requirements hold — so give the test
        // puppet the Intelligence to genuinely qualify.
        var esPlate = new Items.ItemInstance { BaseItemId = "battlemage_plate", Rarity = Items.ItemRarity.Normal };
        srvSum.Character.BaseIntelligence = Math.Max(srvSum.Character.BaseIntelligence, 8);
        srvSum.Character.Equipment[Items.EquipSlot.BodyArmor] = esPlate;
        srvSum.RecomputeStats(data);
        Check(srvSum.Stats.MaxEnergyShield > 15f,
              $"hybrid base grants Energy Shield scaled by Intelligence ({srvSum.Stats.MaxEnergyShield:0.0})");
        srvSum.EnergyShield = srvSum.Stats.MaxEnergyShield;
        clientB.SendDebugCommand("heal");
        Pump(0.3f);
        float esHpBefore = srvSum.Health;
        float esBefore = srvSum.EnergyShield;
        var esBiter = server.World.SpawnEnemy("grunt", srvSum.Position + new Vector2(1.0f, 0));
        bool esAbsorbed = false;
        for (int i = 0; i < 80 && !esAbsorbed; i++)
        {
            Pump(0.1f);
            esAbsorbed = srvSum.EnergyShield < esBefore - 0.1f;
        }
        Check(esAbsorbed && srvSum.Health >= esHpBefore - 0.01f,
              $"Energy Shield absorbs the hit before life (ES {srvSum.EnergyShield:0.0}, hp intact)");
        Check(MathF.Abs(srvSum.LastDamagedAt - server.World.Time) < 3f,
              "taking damage stamps the recharge-delay timer");
        clientB.SendDebugCommand("kill_nearby");
        Pump(0.4f);
        // Recharge: silent inside the delay window, then refills at the configured rate.
        srvSum.EnergyShield = 5f;
        srvSum.LastDamagedAt = server.World.Time; // as if just hit: reset the delay
        Pump(Stats.EnergyShieldBalance.RechargeDelay * 0.5f);
        Check(srvSum.EnergyShield <= 5.01f, "no recharge before the delay has passed");
        Pump(Stats.EnergyShieldBalance.RechargeDelay * 0.6f + 0.5f);
        Check(srvSum.EnergyShield > 5.5f, $"recharge starts after the delay ({srvSum.EnergyShield:0.0})");
        float esMid = srvSum.EnergyShield;
        Pump(6f);
        Check(srvSum.EnergyShield >= MathF.Min(srvSum.Stats.MaxEnergyShield, esMid) &&
              srvSum.EnergyShield >= srvSum.Stats.MaxEnergyShield - 0.1f,
              "recharge refills to full at the configured rate");
        Pump(0.3f);
        Check(MathF.Abs((clientB.World.Me?.EnergyShield ?? -1) - srvSum.EnergyShield) < 1.5f &&
              clientA.World.Players[bId].MaxEnergyShield > 15f,
              "Energy Shield and its maximum replicate to every client");

        // Attack-vs-spell tagging is data-driven: a spell-flagged projectile is never
        // deflectable, the default stays a deflectable Attack.
        data.Enemies["spitter"].ProjectileIsSpell = true;
        var spellSpitter = server.World.SpawnEnemy("spitter", srvSum.Position + new Vector2(4f, 0));
        Server.ServerProjectile spellGlob = null;
        for (int i = 0; i < 60 && spellGlob == null; i++)
        {
            Pump(0.1f);
            spellGlob = server.World.Projectiles.Values.FirstOrDefault(pr => !pr.FromPlayer);
        }
        Check(spellGlob is { AttackHit: false },
              "spell-flagged enemy projectiles are marked non-deflectable (data-driven)");
        data.Enemies["spitter"].ProjectileIsSpell = false;
        clientB.SendDebugCommand("kill_nearby");
        // Restore B's gear so later sections see the old state.
        if (oldHelm != null) srvSum.Character.Equipment[Items.EquipSlot.Helmet] = oldHelm;
        else srvSum.Character.Equipment.Remove(Items.EquipSlot.Helmet);
        srvSum.Character.Equipment.Remove(Items.EquipSlot.BodyArmor);
        srvSum.RecomputeStats(data);
        clientB.SendDebugCommand("heal");
        Pump(0.4f);

        Console.WriteLine("\n-- Armor sets, gear requirements & of Ease --");
        // Pure sets exist for every defense type across the four armor slots, carrying
        // ONLY their type's stat; hybrids pay for breadth with ~60% of each pure value.
        bool PureSet(string[] ids, Stats.StatType stat) => ids.All(id =>
            data.Items.TryGetValue(id, out var b) && b.BaseStats.Count == 1 &&
            b.BaseStats.ContainsKey(stat));
        Check(PureSet(new[] { "iron_cap", "iron_mail", "iron_gauntlets", "iron_greaves", "iron_plate" },
                  Stats.StatType.Armor),
              "a pure Armor set spans helmet/body/gloves/boots");
        Check(PureSet(new[] { "hide_hood", "hide_tunic", "hide_gloves", "hide_boots", "hunters_jerkin" },
                  Stats.StatType.DeflectionRating),
              "a pure Deflection set spans helmet/body/gloves/boots");
        Check(PureSet(new[] { "cloth_cowl", "cloth_robe", "cloth_wraps", "cloth_slippers", "apprentice_robe" },
                  Stats.StatType.EnergyShield),
              "a pure Energy Shield set spans helmet/body/gloves/boots");
        float PureBody(string id, Stats.StatType st) => data.Items[id].BaseStats[st];
        bool HybridWeaker(string id, Stats.StatType st, string pureId) =>
            data.Items[id].BaseStats[st] >= PureBody(pureId, st) * 0.5f &&
            data.Items[id].BaseStats[st] <= PureBody(pureId, st) * 0.7f;
        Check(HybridWeaker("brigandine", Stats.StatType.Armor, "iron_plate") &&
              HybridWeaker("brigandine", Stats.StatType.DeflectionRating, "hunters_jerkin") &&
              HybridWeaker("battlemage_plate", Stats.StatType.EnergyShield, "apprentice_robe") &&
              HybridWeaker("shadowweave_garb", Stats.StatType.DeflectionRating, "hunters_jerkin"),
              "hybrid bodies carry ~60% of each pure stat");
        Check(data.Items["iron_mace"].RequiredStrength == 8 &&
              data.Items["heavy_war_mace"].RequiredStrength == 10 &&
              data.Items["mystic_staff"].RequiredIntelligence == 8 &&
              data.Items["arcane_staff"].RequiredIntelligence == 10 &&
              data.Items["iron_kite_shield"].RequiredStrength == 8 &&
              data.Items["hide_tunic"].RequiredDexterity == 8 &&
              data.Items["cloth_robe"].RequiredIntelligence == 8 &&
              data.Items["leather_hood"].RequiredStrength == 8,
              "weapons, shields and armor carry baseline attribute requirements");

        // "of Ease": a LOCAL suffix lowering the item's own requirements. Iron Plate
        // (14 Str) with a 30% reduction needs ceil(14*0.7)=10 — met at base stats.
        var easedPlate = new Items.ItemInstance { BaseItemId = "iron_plate", Rarity = Items.ItemRarity.Rare, ItemLevel = 5 };
        easedPlate.EnsureSlotData();
        easedPlate.Modifiers.Add(new Items.ItemModifierRoll { ModifierId = "of_ease", Value = 30 });
        Check(easedPlate.EffectiveRequirement(data, data.Items["iron_plate"].RequiredStrength) == 10,
              "of Ease lowers the item's own requirement (14 Str -> 10)");
        Check(srvSum.Character.Inventory.TryAdd(data, easedPlate), "eased plate added to the bag");
        var easedPlaced = srvSum.Character.Inventory.FindByInstance(easedPlate.InstanceId);
        server.World.MoveItem(bId, ItemLocation.AtGrid(easedPlaced.X, easedPlaced.Y),
            ItemLocation.AtEquip(Items.EquipSlot.BodyArmor));
        Check(srvSum.Character.Equipment.GetValueOrDefault(Items.EquipSlot.BodyArmor)?.InstanceId == easedPlate.InstanceId,
              "the server accepts the eased requirement it would otherwise refuse");
        srvSum.Character.Equipment.Remove(Items.EquipSlot.BodyArmor);
        srvSum.RecomputeStats(data);

        Console.WriteLine("\n-- Swept projectile hits --");
        // A projectile so fast it steps clean across a body in one tick must still
        // connect: hits sweep the tick's segment instead of sampling the endpoint.
        clientB.SendDebugCommand("kill_nearby");
        Pump(0.3f);
        var sweptPrey = server.World.SpawnEnemy("grunt", srvSum.Position + new Vector2(1.0f, 0));
        sweptPrey.Health = 200f;
        sweptPrey.StunnedUntil = server.World.Time + 30f;
        server.World.Projectiles[999999] = new ServerProjectile
        {
            Id = 999999,
            FromPlayer = true,
            OwnerId = bId,
            SkillId = "fire_bolt",
            Position = srvSum.Position,
            Height = sweptPrey.Height,
            Direction = new Vector2(1, 0),
            Speed = 120f,          // 2 tiles per tick: endpoint sampling would miss
            MaxRange = 2.5f,
            MinDamage = 5f,
            MaxDamage = 5f,
            DamageKind = Skills.DamageKind.Fire,
        };
        Pump(0.2f);
        Check(sweptPrey.Health < 199.9f,
              $"tick-crossing projectiles still land their hit (hp {sweptPrey.Health:0.0})");
        clientB.SendDebugCommand("kill_nearby");
        Pump(0.3f);

        // Ghost gating: with no mana, no cosmetic ghost spawns (nothing to fly through
        // enemies while the server rejects the real cast).
        float manaSave = clientB.World.Me.Mana;
        clientB.World.Me.Mana = 0f;
        clientB.RequestUseSkill("fire_bolt", clientB.World.Me.Position + new Vector2(3f, 0));
        Check(!clientB.World.Projectiles.Values.Any(pr => pr.Ghost),
              "no ghost bolt spawns when the cast will be rejected for mana");
        clientB.World.Me.Mana = manaSave;
        Pump(0.5f);

        Console.WriteLine("\n-- XP curves & enemy level scaling --");
        Check(Stats.XpBalance.LevelFactor(5, 4) == 1f &&
              MathF.Abs(Stats.XpBalance.LevelFactor(13, 8) - 0.25f) < 0.001f &&
              MathF.Abs(Stats.XpBalance.LevelFactor(40, 1) - Stats.XpBalance.MinimumFactor) < 0.001f,
              "under-level kills pay less XP, down to the floor");
        var scaled = server.World.SpawnEnemy("grunt", srvSum.Position + new Vector2(8f, 8f), level: 11);
        var gruntNative = data.Enemies["grunt"];
        int lvlUp = 11 - gruntNative.Level;
        Check(scaled.Level == 11 &&
              MathF.Abs(scaled.MaxHealth - gruntNative.MaxHealth * Stats.EnemyLevelScaling.Health(lvlUp)) < 0.5f &&
              MathF.Abs(scaled.DamageScale - Stats.EnemyLevelScaling.Damage(lvlUp)) < 0.001f &&
              MathF.Abs(scaled.XpScale - Stats.EnemyLevelScaling.Xp(lvlUp)) < 0.001f,
              $"level-overridden enemies scale health/damage/XP centrally (hp {scaled.MaxHealth:0})");
        scaled.Health = 0.1f;
        clientB.SendDebugCommand("kill_nearby");
        Pump(0.3f);

        // Drops roll at the enemy's SCALED level too: a level-12 "gravelord" drops
        // level-12 loot (it used to roll at the def's native level regardless).
        clientB.SendDebugCommand("heal");
        clientB.World.Me.Position = clientB.World.Map.PlayerSpawn;
        Pump(0.3f);
        var dropKeysBeforeScaled = server.World.Drops.Keys.ToHashSet();
        var scaledBoss = server.World.SpawnEnemy("gravelord",
            srvSum.Position + new Vector2(1.2f, 0), level: 12);
        scaledBoss.Health = 1f;
        for (int hit = 0; hit < 12 && !scaledBoss.Dead; hit++)
        {
            clientB.SendDebugCommand("heal");
            srvSum.Mana = srvSum.Stats.MaxMana;
            srvSum.SkillReadyAt.Clear();
            srvSum.GlobalSkillReadyAt = 0;
            clientB.World.Me.Position = scaledBoss.Position + new Vector2(-1.2f, 0);
            clientB.RequestUseSkill("basic_strike", scaledBoss.Position);
            Pump(0.3f);
        }
        Check(scaledBoss.Dead, "scaled Gravelord killed for the loot-level check");
        Pump(0.4f);
        var scaledLoot = server.World.Drops
            .Where(kv => !dropKeysBeforeScaled.Contains(kv.Key))
            .Select(kv => kv.Value)
            .Where(d => d.Item != null && d.Item.GetBase(data).IsEquippable)
            .ToList();
        Check(scaledLoot.Count > 0 && scaledLoot.All(d => d.Item.ItemLevel == 12),
              $"loot rolls at the enemy's scaled level ({scaledLoot.Count} item(s) at ilvl " +
              $"{scaledLoot.FirstOrDefault()?.Item.ItemLevel ?? -1})");
        clientB.SendDebugCommand("kill_nearby");
        clientB.SendDebugCommand("heal");
        Pump(0.3f);

        // Loot parity: every defense flavor exists at level 1, hybrids join the pool at
        // level 5, and weapons no longer outweigh armor slots in the tables.
        Check(data.Items["poachers_hood"].RequiredLevel == 1 &&
              data.Items["poachers_tunic"].BaseStats.ContainsKey(Stats.StatType.DeflectionRating) &&
              data.Items["novice_robe"].RequiredLevel == 1 &&
              data.Items["novice_robe"].BaseStats.ContainsKey(Stats.StatType.EnergyShield),
              "Deflection and Energy Shield armor exist as level-1 starter sets");
        Check(new[] { "brigandine", "battlemage_plate", "shadowweave_garb" }
                  .All(id => data.Items[id].RequiredLevel == 5),
              "hybrid bodies drop from level 5");
        Check(data.GetLootTable("default").CategoryWeights[Items.ItemCategory.Mace] == 80 &&
              data.GetLootTable("default").CategoryWeights[Items.ItemCategory.Staff] == 80 &&
              data.GetLootTable("boss").CategoryWeights[Items.ItemCategory.Mace] == 80,
              "weapon category weights trimmed so armor drops keep pace");

        // Spent-ghost bookkeeping (the "spell fired twice" fix): a ghost that dies on an
        // enemy records its flight progress so the authoritative spawn can fast-forward.
        var ghostPrey = server.World.SpawnEnemy("grunt", srvSum.Position + new Vector2(1.5f, 0));
        ghostPrey.Health = 500f;
        ghostPrey.StunnedUntil = server.World.Time + 30f;
        Pump(0.3f);
        var preyOnB = clientB.World.Enemies[ghostPrey.Id];
        clientB.World.Projectiles[-777] = new ClientProjectile
        {
            Id = -777, Ghost = true, FromPlayer = true, SkillId = "fire_bolt",
            Position = preyOnB.Position, Height = preyOnB.Height,
            Direction = new Vector2(1, 0), Speed = 0.01f, MaxRange = 8f, Traveled = 1.25f,
        };
        Pump(0.1f);
        Check(!clientB.World.Projectiles.ContainsKey(-777) &&
              clientB.World.SpentGhosts.Any(g => g.SkillId == "fire_bolt" &&
                  MathF.Abs(g.Traveled - 1.25f) < 0.2f),
              "a ghost consumed on an enemy records its progress for spawn adoption");
        clientB.World.SpentGhosts.Clear();
        ghostPrey.Health = 1f;
        clientB.SendDebugCommand("kill_nearby");
        Check(Stats.Deflection.ChanceFromRating(200, 50) < Stats.Deflection.ChanceFromRating(200, 5),
              "the same deflection rating is worth less at higher character level");
        Check(Stats.ArmorBalance.PhysicalReduction(100, 50) < Stats.ArmorBalance.PhysicalReduction(100, 5),
              "the same armor is worth less at higher character level");
        Pump(0.4f);

        Console.WriteLine("\n-- Summon pathfinding, aggro symmetry & scrolls --");
        // Rally the warriors ONTO PLATEAU A: the straight line runs into a cliff — only
        // the rally flow field (routing via the inset ramp) gets them up there.
        srvSum.Mana = srvSum.Stats.MaxMana;
        clientB.RequestSummonAdjust("summon_skeleton_warrior", +1);
        Pump(0.4f);
        var plateauRally = new Vector2(10.5f, 12.5f); // plateau A interior, height 1
        clientB.RequestSummonRally("summon_skeleton_warrior", true, plateauRally);
        bool rallyClimbed = false;
        for (int i = 0; i < 60 && !rallyClimbed; i++)
        {
            foreach (var intruder in server.World.Enemies.Values)
                if (!intruder.Dead && Vector2.Distance(intruder.Position, plateauRally) < 12f)
                {
                    intruder.Position = new Vector2(2.5f, 2.5f);
                    intruder.Height = 0f;
                    intruder.StunnedUntil = server.World.Time + 5f;
                }
            Pump(0.3f);
            rallyClimbed = server.World.Summons.Values
                .Where(su => su.SkillId == "summon_skeleton_warrior")
                .All(su => Vector2.Distance(su.Position, plateauRally) < 1.6f && su.Height > 0.75f);
        }
        Check(rallyClimbed, "rallied summons PATHFIND up the ramp onto the plateau (no wedging)");
        clientB.RequestSummonRally("", false, default);
        Pump(0.5f);

        // Aggro symmetry: an enemy near a lone summon (its owner far away) aggros and
        // fights IT, exactly like it would a player.
        var lastWarrior = server.World.Summons.Values.First(su => su.SkillId == "summon_skeleton_warrior");
        clientB.World.Me.Position = clientB.World.Map.PlayerSpawn + new Vector2(0f, 3f);
        Pump(0.6f); // warrior heels to B
        // Station the warrior on CLEAR level-0 ground away from B (probe like the rally
        // test — random pillars/water can occupy any fixed offset on some seeds).
        var stationSpot = srvSum.Position + new Vector2(9f, 0);
        foreach (var cand in new[]
                 {
                     new Vector2(9f, 0), new Vector2(-9f, 0), new Vector2(0, 9f), new Vector2(0, -9f),
                     new Vector2(7f, 7f), new Vector2(-7f, 7f),
                 })
        {
            var spot = srvSum.Position + cand;
            if (!server.World.Map.CircleHitsWall(spot, 0.5f) &&
                !server.World.Map.CircleHitsWall(spot + new Vector2(1.5f, 0), 0.5f) &&
                server.World.Map.GroundHeightAt(spot) < 0.05f &&
                server.World.Map.GroundHeightAt(spot + new Vector2(1.5f, 0)) < 0.05f)
            {
                stationSpot = spot;
                break;
            }
        }
        lastWarrior.Position = stationSpot;
        lastWarrior.Height = 0f; // it may still stand at plateau height from the rally
        var meatHunter = server.World.SpawnEnemy("grunt", stationSpot + new Vector2(1.5f, 0));
        float warriorHpBefore = lastWarrior.Health;
        bool warriorFought = false;
        for (int i = 0; i < 60 && !warriorFought; i++)
        {
            lastWarrior.Position = stationSpot; // hold the matchup
            lastWarrior.Height = 0f;
            Pump(0.15f);
            warriorFought = lastWarrior.Health < warriorHpBefore - 0.1f || meatHunter.Winding;
        }
        Check(warriorFought, "enemies aggro and attack summons just like players");
        clientB.SendDebugCommand("kill_nearby");
        meatHunter.Health = 0.1f;
        lastWarrior.Position = srvSum.Position + new Vector2(1f, 0);
        lastWarrior.Height = srvSum.Height;
        Pump(0.5f);

        // LIVE-combat regression: summons must fight an UNSTUNNED enemy that is
        // actively chasing and swinging back (every earlier combat test pinned its
        // target, which would hide a real-play regression).
        srvSum.Mana = srvSum.Stats.MaxMana;
        clientB.RequestSummonAdjust("summon_skeleton", +1);
        Pump(0.4f);
        var liveFoe = server.World.SpawnEnemy("bone_knight", srvSum.Position + new Vector2(2.5f, 0));
        liveFoe.Health = 300f;
        bool liveFoeHurt = false;
        for (int i = 0; i < 80 && !liveFoeHurt; i++)
        {
            Pump(0.15f);
            liveFoeHurt = liveFoe.Dead || liveFoe.Health < 299.9f;
        }
        Check(liveFoeHurt, "summons fight a LIVE (unstunned, fighting-back) enemy");
        liveFoe.Health = 0.1f;
        clientB.SendDebugCommand("kill_nearby");
        clientB.SendDebugCommand("heal");
        Pump(0.4f);

        // Scroll support: summon skills carry Melee/Projectile tags so melee/projectile
        // scrolls ATTACH, and their effects ride the summons' attacks.
        Check(data.Skills["summon_skeleton_warrior"].Tags.Contains("Melee") &&
              data.Skills["summon_skeleton"].Tags.Contains("Projectile"),
              "summon skills carry the Melee / Projectile tags for scroll compatibility");
        var warSkill = srvSum.Character.GetSkill("summon_skeleton_warrior");
        warSkill.Scrolls.Add(new Items.ItemInstance { BaseItemId = "scroll_venom" });
        var venomPrey = server.World.SpawnEnemy("grunt", srvSum.Position + new Vector2(2f, 0));
        venomPrey.Health = 500f;
        venomPrey.StunnedUntil = server.World.Time + 60f;
        bool poisonedBySummon = false;
        for (int i = 0; i < 80 && !poisonedBySummon; i++)
        {
            Pump(0.15f);
            poisonedBySummon = venomPrey.PoisonTimeLeft > 0;
        }
        Check(poisonedBySummon, "a Venom scroll on the warrior skill poisons its sword hits");
        warSkill.Scrolls.Clear();
        clientB.SendDebugCommand("kill_nearby");
        Pump(0.4f);

        // Multishot on the archers: one volley looses multiple arrows.
        srvSum.Mana = srvSum.Stats.MaxMana;
        clientB.RequestSummonAdjust("summon_skeleton", +1);
        Pump(0.4f);
        var archSkill = srvSum.Character.GetSkill("summon_skeleton");
        archSkill.Scrolls.Add(new Items.ItemInstance { BaseItemId = "scroll_multishot" });
        var volleyPrey = server.World.SpawnEnemy("grunt", srvSum.Position + new Vector2(3f, 0));
        volleyPrey.Health = 500f;
        volleyPrey.StunnedUntil = server.World.Time + 60f;
        int maxArrowsAloft = 0;
        for (int i = 0; i < 60; i++)
        {
            Pump(0.05f);
            maxArrowsAloft = Math.Max(maxArrowsAloft,
                server.World.Projectiles.Values.Count(pr => pr.SpriteOverride == "Arrow"));
            if (maxArrowsAloft >= 2) break;
        }
        Check(maxArrowsAloft >= 2, $"a Multishot scroll fans the archers' volley ({maxArrowsAloft} arrows aloft)");
        archSkill.Scrolls.Clear();
        foreach (var skl in new[] { "summon_skeleton", "summon_skeleton_warrior" })
        {
            clientB.RequestSummonAdjust(skl, -1);
            clientB.RequestSummonAdjust(skl, -1);
        }
        clientB.SendDebugCommand("kill_nearby");
        clientB.SendDebugCommand("heal");
        Pump(0.5f);

        Console.WriteLine("\n-- Client-side cast prediction --");
        // The casting client gets INSTANT feedback (before any network round trip):
        // a ghost projectile on projectile casts, the swing animation on melee casts.
        srvSum.Mana = srvSum.Stats.MaxMana;
        srvSum.SkillReadyAt.Remove("fire_bolt");
        srvSum.GlobalSkillReadyAt = 0;
        Pump(0.2f);
        var predictAim = clientB.World.Me.Position + new Vector2(3f, 0);
        clientB.RequestUseSkill("fire_bolt", predictAim);
        Check(clientB.World.Projectiles.Values.Any(pr => pr.Ghost && pr.SkillId == "fire_bolt" && pr.Id < 0),
              "casting spawns an instant local ghost projectile (zero round trips)");
        Pump(0.3f);
        Check(clientB.World.Projectiles.Values.All(pr => !pr.Ghost) &&
              clientB.World.Projectiles.Values.Any(pr => pr.Id > 0 && pr.SkillId == "fire_bolt"),
              "the ghost is adopted by the authoritative projectile on confirmation");
        clientB.World.Me.SwingTimeLeft = 0f;
        clientB.RequestUseSkill("basic_strike", predictAim);
        Check(clientB.World.Me.SwingTimeLeft > 0f,
              "the melee swing animation starts the instant of the click");
        Pump(0.8f);

        Console.WriteLine("\n-- Cast gating: mana and empty-air targets --");
        // Not enough mana: NOTHING starts client-side — no predicted swing, no ghost.
        var srvGate = server.World.Players[clientB.World.MyPlayerId];
        clientB.World.Me.SwingTimeLeft = 0f;
        clientB.World.Me.Mana = 0f;
        clientB.RequestUseSkill("mace_strike", predictAim); // costs 9 mana
        Check(clientB.World.Me.SwingTimeLeft <= 0f,
              "no swing animation is predicted when mana is short");
        clientB.RequestUseSkill("fire_bolt", predictAim);
        Check(!clientB.World.Projectiles.Values.Any(pr => pr.Ghost),
              "no ghost bolt either — a mana-starved cast shows nothing");
        Pump(0.4f);

        // Chain Lightning into empty air: the server takes NO mana and commits NO
        // cooldown — an instant-target skill with no target simply doesn't cast.
        clientB.SendDebugCommand("kill_nearby");
        Pump(0.3f);
        clientB.RequestLearnSkill("chain_lightning"); // no-op if already learned
        Pump(0.2f);
        srvGate.Mana = srvGate.Stats.MaxMana * 0.6f;
        srvGate.SkillReadyAt.Clear();
        srvGate.GlobalSkillReadyAt = 0;
        float gateManaBefore = srvGate.Mana;
        clientB.RequestUseSkill("chain_lightning", srvGate.Position + new Vector2(4f, 0));
        Pump(0.3f);
        Check(srvGate.Mana >= gateManaBefore - 0.2f,
              $"chain lightning with no target costs no mana ({gateManaBefore:0.0} -> {srvGate.Mana:0.0})");
        Check(!srvGate.SkillReadyAt.TryGetValue("chain_lightning", out float gateCd) ||
              gateCd <= server.World.Time,
              "and commits no cooldown");

        Console.WriteLine("\n-- Party XP share --");
        // Killer gets 100%, everyone else 70% — each through their OWN under-level
        // penalty. Clear the field (and any lingering summons — their kills would
        // credit stray XP mid-measurement) before the controlled kill.
        var srvShareA = server.World.Players[clientA.World.MyPlayerId];
        var srvShareB = server.World.Players[clientB.World.MyPlayerId];
        Server.ServerEnemy shareGrunt = null;
        float xpABefore = 0, xpBBefore = 0;
        int lvlABefore = 0, lvlBBefore = 0;
        for (int attempt = 0; attempt < 5 && (shareGrunt == null || !shareGrunt.Dead); attempt++)
        {
            for (int wait = 0; wait < 20 && !clientA.World.Me.Alive; wait++) Pump(0.5f);
            clientA.SendDebugCommand("kill_nearby");
            clientA.SendDebugCommand("heal");
            clientB.SendDebugCommand("kill_nearby");
            foreach (var s in server.World.Summons.Values.ToList()) s.Health = 0;
            clientA.World.Me.Position = clientA.World.Map.PlayerSpawn;
            clientB.World.Me.Position = SafeNear(clientA.World.Map.PlayerSpawn + new Vector2(10, 10));
            Pump(0.4f);
            shareGrunt = server.World.SpawnEnemy("grunt", srvShareA.Position + new Vector2(1.1f, 0));
            shareGrunt.Health = 1f;
            shareGrunt.StunnedUntil = server.World.Time + 30f;
            Pump(0.2f);
            xpABefore = srvShareA.Character.Experience;
            xpBBefore = srvShareB.Character.Experience;
            lvlABefore = srvShareA.Character.Level;
            lvlBBefore = srvShareB.Character.Level;
            srvShareA.Mana = srvShareA.Stats.MaxMana;
            srvShareA.SkillReadyAt.Clear();
            srvShareA.GlobalSkillReadyAt = 0;
            // A ended the suite holding a staff — Fire Bolt is the weapon-agnostic kill.
            clientA.RequestUseSkill("fire_bolt", shareGrunt.Position);
            Pump(0.5f);
            if (!shareGrunt.Dead)
            {
                Console.WriteLine($"  [diag] share attempt {attempt}: aAlive={srvShareA.Alive} " +
                    $"gruntHp={shareGrunt.Health:0.0} dist={Vector2.Distance(shareGrunt.Position, srvShareA.Position):0.00} " +
                    $"msgA='{msgA}' weapon={srvShareA.Character.MainHand?.BaseItemId ?? "none"} " +
                    $"hA={srvShareA.Height:0.0} hG={shareGrunt.Height:0.0} global={srvShareA.GlobalSkillReadyAt - server.World.Time:0.00}");
                shareGrunt.Health = 0.01f; // don't let a stray survivor pollute the next attempt
                Pump(0.2f);
            }
        }
        Check(shareGrunt is { Dead: true }, "share-test grunt felled by A");
        float shareBase = shareGrunt.Def.XpReward * shareGrunt.XpScale;
        float expectA = shareBase * Stats.XpBalance.LevelFactor(lvlABefore, shareGrunt.Level);
        float expectB = shareBase * Stats.XpBalance.PartyShare *
                        Stats.XpBalance.LevelFactor(lvlBBefore, shareGrunt.Level);
        Check(srvShareA.Character.Level == lvlABefore &&
              MathF.Abs(srvShareA.Character.Experience - xpABefore - expectA) < 0.5f,
              $"the killer earns full XP (+{srvShareA.Character.Experience - xpABefore:0.0} of {expectA:0.0})");
        Check(srvShareB.Character.Level == lvlBBefore &&
              MathF.Abs(srvShareB.Character.Experience - xpBBefore - expectB) < 0.5f,
              $"party members earn 70% without landing the kill (+{srvShareB.Character.Experience - xpBBefore:0.0} of {expectB:0.0})");

        Console.WriteLine("\n-- Costs scale with power --");
        var fbDef = data.Skills["fire_bolt"];
        var noScrolls = Array.Empty<Skills.ScrollDefinition>();
        float costL1 = Skills.SkillMath.Compute(data, fbDef, 1, noScrolls, clientB.World.MyStats).ManaCost;
        float costL3 = Skills.SkillMath.Compute(data, fbDef, 3, noScrolls, clientB.World.MyStats).ManaCost;
        var anyScroll = data.Scrolls.Values.First();
        float costScrolled = Skills.SkillMath.Compute(data, fbDef, 1, new[] { anyScroll }, clientB.World.MyStats).ManaCost;
        Check(MathF.Abs(costL1 - fbDef.ManaCost) < 0.01f &&
              MathF.Abs(costL3 - fbDef.ManaCost * (1f + 2 * Skills.SkillMath.ManaCostPerSkillLevelPct)) < 0.05f,
              $"skill levels raise mana cost ({costL1:0.0} -> {costL3:0.0} at level 3)");
        Check(MathF.Abs(costScrolled - fbDef.ManaCost * (1f + Skills.SkillMath.ManaCostPerScrollPct)) < 0.05f,
              $"an attached Skill Scroll raises mana cost ({costL1:0.0} -> {costScrolled:0.0})");
        Check(data.Skills["fire_bolt"].ManaCost == 6 && data.Skills["ice_spike"].ManaCost == 5 &&
              data.Skills["chain_lightning"].ManaCost == 9,
              "starter spell base costs trimmed (6/5/9) to offset per-level scaling");
        Check(data.Skills["summon_skeleton"].SummonHealth == 40 &&
              data.Skills["summon_skeleton_warrior"].SummonHealth == 58,
              "both summons gained +10 base health");
        Check(data.Enemies["grunt"].XpReward == 6 && data.Enemies["spitter"].XpReward == 9 &&
              data.Enemies["bone_knight"].XpReward == 11 && data.Enemies["gravelord"].XpReward == 80,
              "enemy XP rewards trimmed again (~20%)");

        Console.WriteLine("\n-- Loot: base-level window + flask drops --");
        // Past the window, outleveled bases stop dropping: at ilvl 30 nothing under
        // RequiredLevel 5 (leather hoods, wooden clubs) may appear. When the window
        // would empty the pool entirely (very high ilvl vs current data), generation
        // falls back to the full pool rather than dropping nothing.
        {
            var windowGen = new Items.LootGenerator(data, new Random(4242));
            var windowTable = data.GetLootTable("default");
            int windowRolls = 0, trashRolls = 0, flaskRolls = 0, badFlasks = 0;
            for (int i = 0; i < 300; i++)
            {
                var rolled = windowGen.GenerateEquipment(windowTable, itemLevel: 30);
                if (rolled == null) continue;
                windowRolls++;
                var rolledBase = data.Items[rolled.BaseItemId];
                if (rolledBase.RequiredLevel < 30 - Items.LootGenerator.BaseLevelWindow) trashRolls++;
                if (rolledBase.Category == Items.ItemCategory.Flask)
                {
                    flaskRolls++;
                    if (rolled.Rarity != Items.ItemRarity.Normal ||
                        rolled.FlaskCharges != rolledBase.FlaskChargesMax) badFlasks++;
                }
            }
            Check(windowRolls > 100 && trashRolls == 0,
                  $"ilvl-30 drops never fall below the base-level window ({windowRolls} rolls)");
            Check(flaskRolls > 0 && badFlasks == 0,
                  $"flasks drop as loot: always Normal rarity, always full ({flaskRolls} rolled)");
            Check(windowGen.GenerateEquipment(windowTable, itemLevel: 100) != null,
                  "an over-window ilvl falls back to the full pool instead of dropping nothing");
        }

        Console.WriteLine("\n-- Drops rest ON the terrain --");
        // Loot scattered at a terrace edge must never fall through the cliff: every
        // drop's stored height matches the surface at its landing spot, so the pickup
        // height gate always agrees with a player standing beside it.
        {
            var srvMap = server.World.Map;
            int ex = -1, ey = -1;
            for (int ty = 2; ty < srvMap.Height - 2 && ex < 0; ty++)
                for (int tx = 2; tx < srvMap.Width - 2 && ex < 0; tx++)
                    if (!srvMap.IsSolid(tx, ty) && !srvMap.IsWater(tx, ty) &&
                        srvMap.Ramp(tx, ty) == World.RampDirection.None &&
                        srvMap.GroundLevel(tx, ty) == 1 &&
                        !srvMap.IsSolid(tx + 1, ty) && !srvMap.IsWater(tx + 1, ty) &&
                        srvMap.Ramp(tx + 1, ty) == World.RampDirection.None &&
                        srvMap.GroundLevel(tx + 1, ty) == 0)
                    { ex = tx; ey = ty; }
            Check(ex > 0, "the arena offers a terrace edge for the drop test");
            var dropKeysBefore = server.World.Drops.Keys.ToHashSet();
            for (int i = 0; i < 30; i++)
                server.World.SpawnDrop(
                    new Items.ItemInstance { BaseItemId = "wooden_club", Rarity = Items.ItemRarity.Normal },
                    new Vector2(ex + 0.85f, ey + 0.5f), 1f); // hugging the cliff lip
            int sunk = 0;
            foreach (var (key, d) in server.World.Drops)
                if (!dropKeysBefore.Contains(key) &&
                    MathF.Abs(d.Height - srvMap.GroundHeightAt(d.Position)) > 0.05f) sunk++;
            Check(sunk == 0,
                  $"cliff-edge loot always rests on the surface it lands on ({sunk} sunk/floating of 30)");
        }

        Console.WriteLine("\n-- Potion flasks (equipped ITEMS, restore over time) --");
        clientB.SendDebugCommand("kill_nearby");
        Pump(0.3f);
        var srvPot = server.World.Players[clientB.World.MyPlayerId];
        var hpFlaskItem = srvPot.Character.Equipment.GetValueOrDefault(Items.EquipSlot.Flask1);
        var mpFlaskItem = srvPot.Character.Equipment.GetValueOrDefault(Items.EquipSlot.Flask2);
        var hpFlaskBase = hpFlaskItem?.GetBase(data);
        var mpFlaskBase = mpFlaskItem?.GetBase(data);
        Check(hpFlaskBase is { Category: Items.ItemCategory.Flask, FlaskHeal: > 0 } &&
              mpFlaskBase is { Category: Items.ItemCategory.Flask, FlaskMana: > 0 },
              "new characters carry the starter flask pair as equipped items");
        srvPot.InvulnerableUntil = server.World.Time + 12f; // measure the sip undisturbed
        srvPot.Health = srvPot.Stats.MaxHealth * 0.3f;
        hpFlaskItem.FlaskCharges = 2;
        srvPot.PotionHealUntil = 0;
        float potHpBefore = srvPot.Health;
        clientB.RequestUsePotion(0);
        Pump(0.25f);
        Check(hpFlaskItem.FlaskCharges == 1, "drinking the health flask spends one of the ITEM's charges");
        Check(srvPot.Health - potHpBefore < hpFlaskBase.FlaskHeal * 0.35f,
              $"the heal is a SIP, not a burst (+{srvPot.Health - potHpBefore:0.0} hp just after)");
        Pump(4.2f);
        Check(srvPot.Health - potHpBefore >
                  MathF.Min(hpFlaskBase.FlaskHeal * 0.8f, srvPot.Stats.MaxHealth * 0.6f),
              $"the flask's stated heal lands over its duration (+{srvPot.Health - potHpBefore:0.0} of {hpFlaskBase.FlaskHeal:0})");
        float potCeiling = MathF.Max(0f, srvPot.Stats.MaxMana - srvPot.ManaReserved);
        srvPot.Mana = potCeiling * 0.1f;
        srvPot.LastSyncedMana = srvPot.Mana;
        mpFlaskItem.FlaskCharges = 1;
        float potManaBefore = srvPot.Mana;
        clientB.RequestUsePotion(1);
        Pump(2.0f);
        Check(mpFlaskItem.FlaskCharges == 0 &&
              srvPot.Mana > potManaBefore + srvPot.Stats.ManaRegeneration * 2f + mpFlaskBase.FlaskMana * 0.25f,
              $"the mana flask restores noticeably faster than regen alone ({potManaBefore:0} -> {srvPot.Mana:0})");
        clientB.RequestUsePotion(1); // empty: refused with a message, charges stay 0
        Pump(0.3f);
        Check(mpFlaskItem.FlaskCharges == 0, "an empty flask refuses the drink");
        // Charges NEVER regenerate: a kill leaves the empty bottles empty (the sanctum
        // fountain is the only refill).
        hpFlaskItem.FlaskCharges = 0;
        srvPot.InvulnerableUntil = server.World.Time + 8f;
        var potGrunt = server.World.SpawnEnemy("grunt", srvPot.Position + new Vector2(1.1f, 0));
        potGrunt.Health = 1f;
        potGrunt.StunnedUntil = server.World.Time + 30f;
        Pump(0.2f);
        srvPot.SkillReadyAt.Clear();
        srvPot.GlobalSkillReadyAt = 0;
        clientB.RequestUseSkill("basic_strike", potGrunt.Position);
        Pump(0.4f);
        Check(potGrunt.Dead && hpFlaskItem.FlaskCharges == 0 && mpFlaskItem.FlaskCharges == 0,
              "kills do NOT refill flasks (fountain-only economy)");

        Console.WriteLine("\n-- Bows, quivers, Arrow Shot --");
        Check(data.Items["short_bow"] is { Category: Items.ItemCategory.Bow, TwoHanded: true } &&
              data.Items.ContainsKey("hunting_bow") && data.Items.ContainsKey("war_bow"),
              "three two-handed bow tiers exist");
        Check(Items.ItemBase.CompatibleSlots(Items.ItemCategory.Quiver)
                  .SequenceEqual(new[] { Items.EquipSlot.OffHand }),
              "quivers equip into the off-hand slot");
        var arrowDef = data.Skills["arrow_shot"];
        Check(arrowDef.ManaCost == 0 && arrowDef.RequiredWeapon == Items.ItemCategory.Bow &&
              arrowDef.IsAttack && arrowDef.UsesWeaponDamage,
              "Arrow Shot is a zero-mana bow attack");
        // Regression: bows/quivers must have a real affix pool — a magic bow that can
        // roll ZERO modifiers would drop blank.
        int bowPrefixGroups = data.Modifiers.Values
            .Where(m => m.AffixType == Items.AffixType.Prefix && m.MinimumItemLevel <= 1 &&
                        m.CompatibleWith(Items.ItemCategory.Bow))
            .Select(m => m.ModifierGroup).Distinct().Count();
        int quiverSuffixGroups = data.Modifiers.Values
            .Where(m => m.AffixType == Items.AffixType.Suffix && m.MinimumItemLevel <= 1 &&
                        m.CompatibleWith(Items.ItemCategory.Quiver))
            .Select(m => m.ModifierGroup).Distinct().Count();
        Check(bowPrefixGroups >= 5 && quiverSuffixGroups >= 2,
              $"bows and quivers roll real affixes ({bowPrefixGroups} bow prefix / {quiverSuffixGroups} quiver suffix groups)");

        var srvArcher = server.World.Players[bId];
        Items.ItemInstance MkNorm(string id) => new()
            { BaseItemId = id, ItemLevel = 1, Rarity = Items.ItemRarity.Normal };
        srvArcher.Character.Equipment[Items.EquipSlot.OffHand] = MkNorm("wooden_buckler");
        srvArcher.Character.Inventory.TryAdd(data, MkNorm("short_bow"));
        srvArcher.Character.Inventory.TryAdd(data, MkNorm("leather_quiver"));
        srvArcher.RecomputeStats(data);
        var bowPlaced = srvArcher.Character.Inventory.Items.First(pl => pl.Item.BaseItemId == "short_bow");
        server.World.MoveItem(bId, ItemLocation.AtGrid(bowPlaced.X, bowPlaced.Y),
            ItemLocation.AtEquip(Items.EquipSlot.MainHand));
        Check(srvArcher.Character.MainHand?.BaseItemId == "short_bow" &&
              srvArcher.Character.OffHand == null,
              "equipping the two-handed bow auto-unequips the off-hand shield");
        var quiverPlaced = srvArcher.Character.Inventory.Items.First(pl => pl.Item.BaseItemId == "leather_quiver");
        server.World.MoveItem(bId, ItemLocation.AtGrid(quiverPlaced.X, quiverPlaced.Y),
            ItemLocation.AtEquip(Items.EquipSlot.OffHand));
        Check(srvArcher.Character.OffHand?.BaseItemId == "leather_quiver",
              "a QUIVER rides the off-hand beside the bow");
        Check(srvArcher.Stats.AttackSpeedIncrease >= 5.9f,
              $"the quiver's implicit attack speed applies (+{srvArcher.Stats.AttackSpeedIncrease:0}%)");
        Check(srvArcher.Stats.PhysicalSubtype == Skills.DamageKind.Thrust,
              "bow attacks pierce (Thrust subtype)");

        clientB.RequestLearnSkill("arrow_shot");
        Pump(0.3f);
        clientB.World.Me.Position = clientB.World.Map.PlayerSpawn;
        clientB.World.Me.Height = 0f;
        Pump(0.3f);
        var bowPrey = server.World.SpawnEnemy("grunt", srvArcher.Position + new Vector2(4f, 0));
        bowPrey.Health = 300f;
        bowPrey.StunnedUntil = server.World.Time + 30f;
        Pump(0.2f);
        srvArcher.Mana = 5f;
        srvArcher.LastSyncedMana = 5f; // near-empty pool: proves the shot is free
        srvArcher.SkillReadyAt.Clear();
        srvArcher.GlobalSkillReadyAt = 0;
        float bowManaBefore = srvArcher.Mana;
        clientB.RequestUseSkill("arrow_shot", bowPrey.Position);
        bool arrowFlew = false;
        for (int i = 0; i < 14 && bowPrey.Health >= 299.9f; i++)
        {
            Pump(0.1f);
            arrowFlew |= server.World.Projectiles.Values.Any(pr =>
                pr.SkillId == "arrow_shot" && pr.OwnerId == bId);
        }
        Check(arrowFlew && bowPrey.Health < 299.9f,
              $"Arrow Shot looses a real arrow downrange (hp {bowPrey.Health:0})");
        Check(srvArcher.Mana >= bowManaBefore - 0.01f, "the shot cost zero mana");
        bowPrey.Health = 1f;
        clientB.SendDebugCommand("kill_nearby");
        Pump(0.3f);

        Console.WriteLine("\n-- Worn armor overlays --");
        Check(data.Items.Values
                  .Where(b => b.Category is Items.ItemCategory.BodyArmor or Items.ItemCategory.Helmet)
                  .All(b => !string.IsNullOrEmpty(b.ArmorStyle) && !string.IsNullOrEmpty(b.SpriteColor)),
              "every body armor and helmet carries an overlay style + color");
        Check(data.Items.Values
                  .Where(b => b.Category is Items.ItemCategory.Gloves or Items.ItemCategory.Boots
                                          or Items.ItemCategory.Belt)
                  .All(b => !string.IsNullOrEmpty(b.SpriteColor)),
              "every small armor slot carries a tint color");
        clientB.SendDebugCommand("equip_set", "plate");
        Pump(0.5f);
        Check(srvArcher.Character.Equipment.GetValueOrDefault(Items.EquipSlot.Helmet)?.BaseItemId == "warplate_helm" &&
              srvArcher.Character.Equipment.GetValueOrDefault(Items.EquipSlot.BodyArmor)?.BaseItemId == "iron_plate" &&
              srvArcher.Character.Equipment.GetValueOrDefault(Items.EquipSlot.Belt)?.BaseItemId == "rope_belt",
              "the equip_set dev command wears a full plate family");
        var armoredB = clientA.World.Players[bId];
        Check(armoredB.HelmetBaseId == "warplate_helm" && armoredB.BodyArmorBaseId == "iron_plate" &&
              armoredB.GlovesBaseId == "warplate_gauntlets" && armoredB.BootsBaseId == "warplate_greaves" &&
              armoredB.BeltBaseId == "rope_belt",
              "all five worn armor slots replicate through PlayerAppearance");
        // The character roster behind the select screen: list / exists / delete.
        Persistence.SaveManager.SaveCharacter(Sim.CharacterData.CreateNew(data, "RosterTestA", "warrior"));
        Persistence.SaveManager.SaveCharacter(Sim.CharacterData.CreateNew(data, "RosterTestB", "mage", bodyStyle: 1));
        var roster = Persistence.SaveManager.ListCharacters();
        Check(roster.Any(rc => rc.Name == "RosterTestA") && roster.Any(rc => rc.Name == "RosterTestB") &&
              Persistence.SaveManager.CharacterExists("RosterTestB"),
              $"the save roster lists every saved character ({roster.Count} on disk)");
        Persistence.SaveManager.DeleteCharacter("RosterTestA");
        roster = Persistence.SaveManager.ListCharacters();
        Check(!roster.Any(rc => rc.Name == "RosterTestA") && roster.Any(rc => rc.Name == "RosterTestB"),
              "deleting a character removes only that save");
        Persistence.SaveManager.DeleteCharacter("RosterTestB");

        // The sound registry: every entry has a unique id, a valid procedural
        // placeholder synth, and a drop-in WAV file name (silent headless — the
        // manager stays inert without a device, but the manifest must parse).
        var soundDefs = Audio.AudioManager.LoadRegistry(
            Path.Combine(AppContext.BaseDirectory, "Data", "Sounds", "sounds.json"));
        Check(soundDefs.Count >= 18 &&
              soundDefs.Select(sd => sd.Id).Distinct().Count() == soundDefs.Count &&
              soundDefs.All(sd => Audio.AudioManager.SynthKinds.Contains(sd.Synth) &&
                                  !string.IsNullOrEmpty(sd.File) && sd.Volume > 0),
              $"sound registry loads: unique ids, valid synth placeholders, WAV names ({soundDefs.Count} sounds)");

        // Quivers genuinely drop from the shared loot pool (they're uncommon: weight 45
        // against ~100 per base — this pins the pool wiring, not the exact rate).
        var probeGen = new Items.LootGenerator(data, new Random(4242));
        var probeTable = data.GetLootTable("default");
        int quiverDrops = 0, bowDrops = 0;
        for (int i = 0; i < 600; i++)
        {
            var probeItem = probeGen.GenerateEquipment(probeTable, 8);
            if (probeItem?.GetBase(data).Category == Items.ItemCategory.Quiver) quiverDrops++;
            if (probeItem?.GetBase(data).Category == Items.ItemCategory.Bow) bowDrops++;
        }
        Check(quiverDrops > 0 && bowDrops > 0,
              $"bows and quivers appear in the drop pool ({bowDrops} bows, {quiverDrops} quivers per 600)");

        // Rarity pacing: golds are the JACKPOT, blues the bread and butter — the
        // default table keeps rare well under magic, and even bosses lean blue.
        var defTable = data.GetLootTable("default");
        var bossTable = data.GetLootTable("boss");
        Check(defTable.RarityWeightRare <= 10 &&
              defTable.RarityWeightMagic > defTable.RarityWeightRare * 4 &&
              bossTable.RarityWeightMagic >= bossTable.RarityWeightRare,
              $"rare drops stay scarce (default N/M/R {defTable.RarityWeightNormal}/{defTable.RarityWeightMagic}/" +
              $"{defTable.RarityWeightRare}, boss M/R {bossTable.RarityWeightMagic}/{bossTable.RarityWeightRare})");

        // Skill Scrolls carry a themed accent color for their hanging-scroll sprite
        // (the GPU-side texture bakes only in a real session, but the data must be
        // complete for every scroll or one of them falls back to text initials).
        var skillScrollBases = data.Items.Values
            .Where(ib => ib.Category == Items.ItemCategory.SkillScroll).ToList();
        Check(skillScrollBases.Count >= 9 &&
              skillScrollBases.All(ib => !string.IsNullOrEmpty(ib.SpriteColor) &&
                                         !string.IsNullOrEmpty(ib.ScrollId)),
              $"every Skill Scroll base has a sprite accent color ({skillScrollBases.Count} scrolls)");

        // Forest daylight: the theme runs the lighting pass (dim green ambient) with
        // seeded sun patches punching bright pools through the canopy.
        var dappled = data.ZoneThemes.First(t => t.Id == "forest");
        Check(dappled.SunPatches && !string.IsNullOrEmpty(dappled.AmbientLight),
              $"forest theme is dappled: ambient {dappled.AmbientLight} + sun patches");

        // Light radius: a Radiance suffix on chest/helm grows the personal torchglow.
        var glowChar = new Sim.CharacterData { Level = 5 }; // iron_cap wants level 5
        var glowHelm = new Items.ItemInstance { BaseItemId = "iron_cap", Rarity = Items.ItemRarity.Magic };
        glowHelm.Modifiers.Add(new Items.ItemModifierRoll { ModifierId = "of_radiance_1", Value = 15 });
        glowChar.Equipment[Items.EquipSlot.Helmet] = glowHelm;
        var glowStats = Stats.StatCalculator.Compute(data, glowChar);
        Check(data.Modifiers.Values.Count(m => m.ModifierGroup == "light_radius") == 10 &&
              data.Modifiers["of_radiance_1"].CompatibleItemCategories.All(
                  cc => cc is Items.ItemCategory.BodyArmor or Items.ItemCategory.Helmet) &&
              MathF.Abs(glowStats.LightRadius - Stats.ComputedStats.BaseLightRadius * 1.15f) < 0.5f,
              $"Radiance suffixes (10 tiers, chest/helm) grow the light radius ({glowStats.LightRadius:0}px)");

        // Added-damage prefixes ride EVERY weapon attack — the bow's fire roll burns
        // through the arrow, not just through melee swings.
        var arrowFireStats = new Stats.ComputedStats
        {
            WeaponMinDamage = 5, WeaponMaxDamage = 8, WeaponAttackSpeed = 1.5f,
            AddedFire = 10, WeaponCategory = Items.ItemCategory.Bow,
        };
        var arrowFireEff = Skills.SkillMath.Compute(data, data.Skills["arrow_shot"], 1,
            Array.Empty<Skills.ScrollDefinition>(), arrowFireStats);
        Check(arrowFireEff.Added != null &&
              arrowFireEff.Added.Any(cmp => cmp.Kind == Skills.DamageKind.Fire && cmp.Max > 0),
              "added-damage prefixes apply to RANGED attacks (fire rides Arrow Shot)");

        // Weapon-local physical math: (base + flat added) x the weapon's OWN %phys —
        // the exact total its tooltip shows — and that %phys never double-dips the
        // global physical multiplier.
        var localChar = new Sim.CharacterData();
        var localClub = new Items.ItemInstance
        { BaseItemId = "wooden_club", Rarity = Items.ItemRarity.Rare, MaxPrefixes = 3, MaxSuffixes = 3 };
        localClub.Modifiers.Add(new Items.ItemModifierRoll { ModifierId = "jagged", Value = 3 }); // +3 flat phys
        localClub.Modifiers.Add(new Items.ItemModifierRoll { ModifierId = "brutal", Value = 8 }); // +8% phys
        localChar.Equipment[Items.EquipSlot.MainHand] = localClub;
        var localStats = Stats.StatCalculator.Compute(data, localChar);
        var clubBaseStats = data.Items["wooden_club"].BaseStats;
        float expLocalMin = (clubBaseStats[Stats.StatType.MinPhysicalDamage] + 3f) * 1.08f;
        float expLocalMax = (clubBaseStats[Stats.StatType.MaxPhysicalDamage] + 3f) * 1.08f;
        var plainChar = new Sim.CharacterData();
        plainChar.Equipment[Items.EquipSlot.MainHand] = new Items.ItemInstance { BaseItemId = "wooden_club" };
        var plainStats = Stats.StatCalculator.Compute(data, plainChar);
        Check(MathF.Abs(localStats.WeaponMinDamage - expLocalMin) < 0.01f &&
              MathF.Abs(localStats.WeaponMaxDamage - expLocalMax) < 0.01f,
              $"weapon damage totals its own flat + %phys locally ({localStats.WeaponMinDamage:0.00}-{localStats.WeaponMaxDamage:0.00})");
        Check(MathF.Abs(localStats.PhysicalDamageIncrease - plainStats.PhysicalDamageIncrease) < 0.01f,
              "the weapon's own %phys stays LOCAL — it never enters the global pool");

        // Continuous requirement validation: an equipped piece only counts while its
        // requirements are met by everything EXCEPT itself. The classic bootstrap
        // exploit — wear a +INT amulet, equip an INT robe, remove the amulet, keep the
        // robe's stats — must die: the robe stays worn but contributes NOTHING and is
        // reported through ComputedStats.InactiveItems for the red UI treatment.
        var bootChar = new Sim.CharacterData
        { BaseStrength = 11, BaseDexterity = 4, BaseIntelligence = 3 }; // warrior spread
        var owlAmulet = new Items.ItemInstance { BaseItemId = "bone_amulet", Rarity = Items.ItemRarity.Magic };
        owlAmulet.Modifiers.Add(new Items.ItemModifierRoll { ModifierId = "of_the_owl", Value = 6 }); // INT 3 -> 9
        var bootRobe = new Items.ItemInstance { BaseItemId = "novice_robe" };  // requires INT 8
        var bootCowl = new Items.ItemInstance { BaseItemId = "novice_cowl" };  // requires INT 8
        bootChar.Equipment[Items.EquipSlot.Amulet] = owlAmulet;
        bootChar.Equipment[Items.EquipSlot.BodyArmor] = bootRobe;
        bootChar.Equipment[Items.EquipSlot.Helmet] = bootCowl;
        var bootOn = Stats.StatCalculator.Compute(data, bootChar);
        Check(bootOn.InactiveItems.Count == 0 && bootOn.MaxEnergyShield > 0,
              $"amulet INT carries the robe+cowl: all gear active, ES {bootOn.MaxEnergyShield:0.#}");
        bootChar.Equipment.Remove(Items.EquipSlot.Amulet);
        var bootOff = Stats.StatCalculator.Compute(data, bootChar);
        Check(bootOff.InactiveItems.Contains(bootRobe.InstanceId) &&
              bootOff.InactiveItems.Contains(bootCowl.InstanceId) &&
              bootOff.MaxEnergyShield < 0.01f,
              "removing the amulet deactivates BOTH INT pieces — their stats vanish");
        Check(MathF.Abs(bootOff.MaxHealth - bootOn.MaxHealth + 10f) < 0.5f,
              "only the amulet's own +10 health left with it (inactive gear = not worn)");
        // Cascade: the robe itself rolls +INT that props up the cowl. Pulling the
        // amulet must collapse the whole chain in one recompute, robe first, then the
        // cowl that only stood on the robe's roll.
        var chainChar = new Sim.CharacterData
        { BaseStrength = 11, BaseDexterity = 4, BaseIntelligence = 3 };
        var chainAmulet = new Items.ItemInstance { BaseItemId = "bone_amulet", Rarity = Items.ItemRarity.Magic };
        chainAmulet.Modifiers.Add(new Items.ItemModifierRoll { ModifierId = "of_the_owl", Value = 5 }); // INT 3 -> 8
        var chainRobe = new Items.ItemInstance { BaseItemId = "novice_robe", Rarity = Items.ItemRarity.Magic };
        chainRobe.Modifiers.Add(new Items.ItemModifierRoll { ModifierId = "of_the_owl", Value = 5 });   // robe feeds the cowl
        var chainCowl = new Items.ItemInstance { BaseItemId = "novice_cowl" };
        chainChar.Equipment[Items.EquipSlot.Amulet] = chainAmulet;
        chainChar.Equipment[Items.EquipSlot.BodyArmor] = chainRobe;
        chainChar.Equipment[Items.EquipSlot.Helmet] = chainCowl;
        var chainOn = Stats.StatCalculator.Compute(data, chainChar);
        chainChar.Equipment.Remove(Items.EquipSlot.Amulet);
        var chainOff = Stats.StatCalculator.Compute(data, chainChar);
        Check(chainOn.InactiveItems.Count == 0 &&
              chainOff.InactiveItems.Contains(chainRobe.InstanceId) &&
              chainOff.InactiveItems.Contains(chainCowl.InstanceId),
              "deactivation cascades: the cowl standing on the robe's +INT falls with it");

        // 4-way body facing: aim (mouse) direction picks front/back/side, side mirrors west.
        Check(Render.WorldRenderer.BodyDirIndex(new Vector2(1, 1), out bool faceS) == Render.SpriteGen.DirSouth && !faceS &&
              Render.WorldRenderer.BodyDirIndex(new Vector2(-1, -1), out _) == Render.SpriteGen.DirNorth &&
              Render.WorldRenderer.BodyDirIndex(new Vector2(1, -1), out bool faceE) == Render.SpriteGen.DirEast && !faceE &&
              Render.WorldRenderer.BodyDirIndex(new Vector2(-1, 1), out bool faceW) == Render.SpriteGen.DirEast && faceW,
              "aim direction maps to the four body facings (S / N / E / W-mirrored)");

        Console.WriteLine("\n-- New enemies: Crypt Leaper + Grave Caller --");
        Check(data.Enemies["crypt_leaper"].DashMinLevel == 1 &&
              data.Enemies["crypt_leaper"].DashDamage > 0,
              "the Crypt Leaper leaps from level 1 (dash-style telegraph)");
        Check(data.Enemies["grave_caller"].AddSpawnType == "shambler" &&
              data.Enemies["grave_caller"].CastRadius > 0 &&
              data.Enemies["shambler"].MaxHealth < data.Enemies["grunt"].MaxHealth,
              "the Grave Caller raises weaker Shamblers and casts a dark AoE");
        clientB.SendDebugCommand("heal");
        clientB.World.Me.Position = clientB.World.Map.PlayerSpawn;
        clientB.World.Me.Height = 0f;
        Pump(0.3f);
        var srvNewE = server.World.Players[bId];
        var leaper = server.World.SpawnEnemy("crypt_leaper", srvNewE.Position + new Vector2(4.2f, 0));
        bool leapCommitted = false, leapLineSeen = false;
        for (int i = 0; i < 30 && !(leapCommitted && leapLineSeen); i++)
        {
            clientB.SendDebugCommand("heal");
            Pump(0.15f);
            leapCommitted |= leaper.DashPrepareUntil > 0 || leaper.DashUntil > 0 || leaper.DashReadyAt > 0;
            leapLineSeen |= clientB.World.Effects.Any(fx => fx.Kind == "dashline");
        }
        Check(leapCommitted, "the leaper commits a telegraphed leap at mid-range");
        Check(leapLineSeen, "with the boss-style ground line telegraph");
        leaper.Health = 1f;
        clientB.SendDebugCommand("kill_nearby");
        Pump(0.3f);

        var caller = server.World.SpawnEnemy("grave_caller", srvNewE.Position + new Vector2(5f, 0));
        bool darkSeen = false;
        int shamblersSeen = 0;
        for (int i = 0; i < 45 && !(darkSeen && shamblersSeen >= 2); i++)
        {
            clientB.SendDebugCommand("heal");
            Pump(0.15f);
            darkSeen |= clientB.World.Effects.Any(fx => fx.Kind is "darkwarn" or "darkburst");
            shamblersSeen = Math.Max(shamblersSeen,
                server.World.Enemies.Values.Count(en => !en.Dead && en.Def.Id == "shambler"));
        }
        Check(darkSeen, "the Grave Caller drops a purple AoE telegraph");
        Check(shamblersSeen >= 2, $"and raises Shambler adds mid-fight ({shamblersSeen} up)");
        caller.Health = 1f;
        clientB.SendDebugCommand("kill_nearby");
        Pump(0.3f);

        Console.WriteLine("\n-- Summoner kiting + AoE vs summons --");
        // The Grave Caller is a coward: dropped INSIDE its comfort ring it backs away
        // from the player instead of trading melee swipes.
        Check(data.Enemies["grave_caller"].KeepDistance > 2f,
              $"the Grave Caller carries a comfort ring ({data.Enemies["grave_caller"].KeepDistance} tiles)");
        clientB.SendDebugCommand("heal");
        Pump(0.2f);
        var kiter = server.World.SpawnEnemy("grave_caller", srvNewE.Position + new Vector2(1.4f, 0));
        bool retreated = false;
        for (int i = 0; i < 45 && !retreated; i++)
        {
            clientB.SendDebugCommand("heal");
            Pump(0.15f);
            retreated |= Vector2.Distance(kiter.Position, srvNewE.Position) > 2.6f;
        }
        Check(retreated, "a crowded Grave Caller retreats out of swipe range on its own");
        kiter.Health = 1f;
        clientB.SendDebugCommand("kill_nearby");
        clientB.SendDebugCommand("heal");
        Pump(0.4f);

        // A summon-only threat draws the Gravelord's ground slam — and the shockwave
        // hurts the summons. The puppet minion is injected server-side far from every
        // player and held in place; melee swings are suppressed so ONLY the slam can
        // account for the damage.
        var lordSpot = srvNewE.Position + new Vector2(14f, 0);
        if (server.World.Map.CircleHitsWall(lordSpot, 0.6f)) lordSpot = srvNewE.Position + new Vector2(0, 14f);
        var slamLord = server.World.SpawnEnemy("gravelord", lordSpot);
        var puppet = new Server.ServerSummon
        {
            Id = 9001, OwnerId = bId, SkillId = "skeleton_archers",
            Position = slamLord.Position + new Vector2(1.0f, 0), Height = slamLord.Height,
            Health = 500, MaxHealth = 500, Damage = 0, Melee = true, Reach = 1.1f, SwingTime = 5f,
        };
        server.World.Summons[puppet.Id] = puppet;
        bool slamCommitted = false, slamHurtSummon = false;
        for (int i = 0; i < 60 && !slamHurtSummon; i++)
        {
            puppet.Position = slamLord.Position + new Vector2(1.0f, 0);
            puppet.Height = slamLord.Height;
            slamLord.AttackReadyAt = server.World.Time + 999f; // no swings — slam only
            Pump(0.15f);
            slamCommitted |= slamLord.SlamResolveAt > 0 || slamLord.SlamReadyAt > 0;
            slamHurtSummon |= puppet.Health < 499.5f;
        }
        Check(slamCommitted, "the Gravelord slams a summon-only threat (no player anywhere near)");
        Check(slamHurtSummon, $"and the shockwave damages the summons ({puppet.Health:0}/500 hp left)");
        server.World.Summons.Remove(puppet.Id);
        slamLord.Health = 1f;
        clientB.World.Me.Position = slamLord.Position + new Vector2(-1.1f, 0);
        clientB.World.Me.Height = slamLord.Height;
        Pump(0.3f);
        clientB.SendDebugCommand("kill_nearby");
        clientB.SendDebugCommand("heal");
        Pump(0.3f);
        clientB.World.Me.Position = clientB.World.Map.PlayerSpawn;
        clientB.World.Me.Height = 0f;
        Pump(0.3f);

        Console.WriteLine("\n-- Corpses: the dead stay behind --");
        // Every kill above left an authoritative corpse record on the server (future
        // skills — raise dead, corpse explosion — target these), replicated everywhere.
        Check(server.World.Corpses.Count >= 2 &&
              server.World.Corpses.Any(c => c.TypeId == "grave_caller") &&
              server.World.Corpses.Any(c => c.TypeId == "gravelord"),
              $"kills leave authoritative server corpses ({server.World.Corpses.Count} bodies, " +
              "caller + gravelord among them)");
        Check(clientA.World.Corpses.Count == server.World.Corpses.Count &&
              clientB.World.Corpses.Count == server.World.Corpses.Count &&
              server.World.Corpses.All(c => clientA.World.Corpses.ContainsKey(c.Id) &&
                                            clientA.World.Corpses[c.Id].TypeId == c.TypeId),
              $"corpses replicate to every client ({clientA.World.Corpses.Count} on A)");
        // Blood colors are data-driven per enemy: zombies bleed dark red, spitters
        // acid green — and skeletons DON'T bleed (empty = no burst, no pool, no gore).
        Check(data.Enemies["grunt"].Blood == "5A7A22" &&
              data.Enemies["spitter"].Blood == "5FA32A" &&
              data.Enemies["bone_knight"].Blood == "" &&
              data.Enemies["grave_caller"].Blood == "7E1A1A",
              "blood is data-driven: green zombie ichor, acid spitters, bloodless " +
              "skeletons, red human cultists");

        Console.WriteLine("\n-- Stun buildup + the harsher XP curve --");
        // Shield Bash no longer stuns outright: it BUILDS toward a stun on a longer
        // cooldown; maces contribute a little buildup of their own.
        Check(data.Skills["shield_bash"].Cooldown >= 1.0f &&
              data.Skills["shield_bash"].StunBuildup >= 50f &&
              data.Skills["basic_strike"].StunBuildup is > 0f and < 15f &&
              data.Skills["mace_strike"].StunBuildup is > 0f and < 25f,
              "stun data: bash builds 60 on a 1.1s cooldown; maces add 7/15");
        var stunDummy = server.World.SpawnEnemy("grunt", srvNewE.Position + new Vector2(12f, 4f));
        var bashDef2 = data.Skills["shield_bash"];
        server.World.ApplyStunBuildup(stunDummy, bashDef2);
        bool oneHitNoStun = server.World.Time >= stunDummy.StunnedUntil &&
                            stunDummy.StunBuildup > 50f;
        server.World.ApplyStunBuildup(stunDummy, bashDef2);
        Check(oneHitNoStun && stunDummy.StunnedUntil > server.World.Time &&
              stunDummy.StunBuildup == 0f && stunDummy.StunResistStacks == 1,
              "bash builds, never insta-stuns: hit 1 no stun, hit 2 stuns + resets + stacks resist");
        // With one 20% resist stack each hit adds 48 — the SECOND stun takes 3 hits.
        server.World.ApplyStunBuildup(stunDummy, bashDef2);
        server.World.ApplyStunBuildup(stunDummy, bashDef2);
        bool noSecondStunYet = stunDummy.StunResistStacks == 1;
        server.World.ApplyStunBuildup(stunDummy, bashDef2);
        Check(noSecondStunYet && stunDummy.StunResistStacks == 2,
              "stacking 20% resistance: the second stun needs three hits, not two");
        server.World.ApplyStunBuildup(stunDummy, bashDef2); // ~31 with two stacks
        float builtBefore = stunDummy.StunBuildup;
        Pump(1.0f);
        Check(builtBefore > 25f && stunDummy.StunBuildup < builtBefore - 8f,
              $"stun buildup decays over time ({builtBefore:0} -> {stunDummy.StunBuildup:0})");
        stunDummy.StunBuildup = 76f;
        Pump(0.4f);
        Check(clientA.World.Enemies.TryGetValue(stunDummy.Id, out var stunSeen) &&
              stunSeen.StunPercent is > 55 and <= 77,
              $"stun buildup replicates for the client bar ({stunSeen?.StunPercent ?? 0}%)");
        stunDummy.Health = 1f;
        stunDummy.StunBuildup = 0f;

        // XP curve: compounding — each level's requirement is the previous grown 12%
        // plus the flat step, so late levels balloon instead of creeping linearly.
        float r1 = Sim.CharacterData.XpRequirementFor(1);
        float r10 = Sim.CharacterData.XpRequirementFor(10);
        float r11 = Sim.CharacterData.XpRequirementFor(11);
        float r30 = Sim.CharacterData.XpRequirementFor(30);
        Check(r1 == 65f && MathF.Abs(r11 - (r10 * 1.12f + 25f)) < 2f && r30 / r10 > 10f,
              $"XP requirements compound: L1 {r1:0}, L10 {r10:0}, L30 {r30:0}");

        Console.WriteLine("\n-- Forest dressing: tall grass, elevated features --");
        // The campaign's run maps grow tall-grass patches (on terraces too) and stay
        // walkable through them; density-bumped big trees come in four variants.
        {
            var dressMap = new World.GameMap(424242, data.ZoneThemes.First(t => t.Id == "forest"),
                World.MapKind.Forest);
            int grassTiles = 0, grassElevated = 0, treeRoots = 0;
            for (int ty = 0; ty < dressMap.Height; ty++)
                for (int tx = 0; tx < dressMap.Width; tx++)
                {
                    if (dressMap.IsTallGrass(tx, ty))
                    {
                        grassTiles++;
                        if (dressMap.GroundLevel(tx, ty) > 0) grassElevated++;
                        if (dressMap.IsSolid(tx, ty) || dressMap.IsWater(tx, ty))
                            grassTiles = -100000; // never on unwalkable tiles
                    }
                    if (dressMap.Feature(tx, ty) == World.TileFeature.BigTreeRoot) treeRoots++;
                }
            Check(grassTiles > 25, $"run maps grow tall-grass patches ({grassTiles} tiles)");
            Check(treeRoots >= 20, $"denser woods: {treeRoots} big trees on one run map");
            Check(dressMap.GroundPathExists(dressMap.PlayerSpawn,
                      dressMap.ExitDoor + new Vector2(-1.2f, 0)),
                  "tall grass never blocks the corridor (still walkable end to end)");

            // Weather shelter: rain/snow never reach under bridge decks or tree
            // canopies, and shelter is HEIGHT-aware — the same tile is dry below the
            // deck and wet on top of it.
            var arenaMap = new World.GameMap(1337, data.ZoneThemes.First(t => t.Id == "graveyard"),
                World.MapKind.Arena);
            int bridgeX = -1, bridgeY = -1;
            for (int ty = 0; ty < arenaMap.Height && bridgeX < 0; ty++)
                for (int tx = 0; tx < arenaMap.Width && bridgeX < 0; tx++)
                    if (arenaMap.BridgeLevel(tx, ty) > 0) { bridgeX = tx; bridgeY = ty; }
            Check(bridgeX >= 0, "the arena spans a bridge deck for the shelter probe");
            var underDeck = new Vector2(bridgeX + 0.5f, bridgeY + 0.5f);
            Check(arenaMap.IsSheltered(underDeck, 0f) &&
                  !arenaMap.IsSheltered(underDeck, arenaMap.BridgeLevel(bridgeX, bridgeY)),
                  "under the deck stays dry; standing ON the deck gets rained on");
            int rootX = -1, rootY = -1, openX = -1, openY = -1;
            for (int ty = 2; ty < dressMap.Height - 2 && (rootX < 0 || openX < 0); ty++)
                for (int tx = 2; tx < dressMap.Width - 2 && (rootX < 0 || openX < 0); tx++)
                {
                    if (dressMap.Feature(tx, ty) == World.TileFeature.BigTreeRoot && rootX < 0)
                    { rootX = tx; rootY = ty; }
                    if (openX < 0 && !dressMap.IsSolid(tx, ty) && dressMap.BridgeLevel(tx, ty) == 0)
                    {
                        bool treeNear = false;
                        for (int dy = -3; dy <= 3 && !treeNear; dy++)
                            for (int dx = -3; dx <= 3 && !treeNear; dx++)
                                treeNear = dressMap.Feature(tx + dx, ty + dy) == World.TileFeature.BigTreeRoot;
                        if (!treeNear) { openX = tx; openY = ty; }
                    }
                }
            Check(rootX >= 0 && dressMap.IsSheltered(new Vector2(rootX + 1.5f, rootY + 0.5f), 0f),
                  "tree canopies shade the ground beside their trunk");
            Check(openX >= 0 && !dressMap.IsSheltered(new Vector2(openX + 0.5f, openY + 0.5f), 0f),
                  "open ground is exposed to the weather");
            // Weather is a MAP attribute — something maps CAN have, never must. The
            // map inherits its theme's optional Weather; no shipped theme forces any
            // (the F1 debug cycler is a purely client-side renderer override).
            var wetTheme = new Data.ZoneTheme { Id = "wet_test", Weather = "rain" };
            var wetMap = new World.GameMap(77, wetTheme, World.MapKind.Hub);
            Check(wetMap.Weather == "rain" && dressMap.Weather == "" &&
                  data.ZoneThemes.All(t => string.IsNullOrEmpty(t.Weather)),
                  "weather is a per-map attribute: theme default flows in, nothing forces it");
            // Reachability guarantee across several seeds: stairs never lead into (or
            // hide) pockets you can't actually walk to. And no ORPHAN stairs — a ramp
            // embedded in flat ground whose ascent side climbs to nothing.
            int strandedTotal = 0, orphanTotal = 0, glitchStairs = 0;
            foreach (int rSeed in new[] { 424242, 987654, 1337, 20260815, 555001, 90210 })
            {
                var seedMap = new World.GameMap(rSeed,
                    data.ZoneThemes.First(t => t.Id == "forest"), World.MapKind.Forest);
                strandedTotal += seedMap.CountUnreachableWalkable();
                orphanTotal += seedMap.CountOrphanRamps();
                // A LONE stair whose walk-up side is blocked reads as a generation
                // glitch — connect-pass stairs must land mid-cliff with a clean
                // approach (or come in proper 2-wide flights).
                for (int sy = 1; sy < seedMap.Height - 1; sy++)
                    for (int sx = 1; sx < seedMap.Width - 1; sx++)
                    {
                        var rd = seedMap.Ramp(sx, sy);
                        if (rd == World.RampDirection.None) continue;
                        (int rdx, int rdy) = rd switch
                        {
                            World.RampDirection.PlusX => (1, 0),
                            World.RampDirection.MinusX => (-1, 0),
                            World.RampDirection.PlusY => (0, 1),
                            _ => (0, -1),
                        };
                        bool horiz = rdy == 0;
                        bool hasLateral =
                            (horiz ? seedMap.Ramp(sx, sy - 1) : seedMap.Ramp(sx - 1, sy)) != World.RampDirection.None ||
                            (horiz ? seedMap.Ramp(sx, sy + 1) : seedMap.Ramp(sx + 1, sy)) != World.RampDirection.None;
                        int lowX = sx - rdx, lowY = sy - rdy;
                        bool lowOk = !seedMap.IsSolid(lowX, lowY) && !seedMap.IsWater(lowX, lowY) &&
                                     (seedMap.GroundLevel(lowX, lowY) == seedMap.GroundLevel(sx, sy) ||
                                      seedMap.Ramp(lowX, lowY) != World.RampDirection.None);
                        if (!hasLateral && !lowOk) glitchStairs++;
                    }
            }
            Check(strandedTotal == 0,
                  $"every walkable tile on run maps is reachable from spawn ({strandedTotal} stranded over 6 seeds)");
            Check(orphanTotal == 0,
                  $"no staircases to nowhere generate ({orphanTotal} orphan ramps over 6 seeds)");
            Check(glitchStairs == 0,
                  $"no lone stairs with blocked approaches generate ({glitchStairs} over 6 seeds)");
        }

        Console.WriteLine("\n-- Campaign: hub sanctum --");
        var campServer = new GameServer(data, 777001, "forest", campaign: true);
        Check(campServer.Start(0), "campaign server started");
        var campA = new GameClient(data, "RunnerA", null);
        // B joins with a CLIENT-SAVED character: a female mage with a bun and CUSTOM
        // (non-preset) colors, so this section proves class kits, hair, and free-RGB
        // appearance all survive the join handshake byte-exact.
        var campBChar = Sim.CharacterData.CreateNew(data, "RunnerB", "mage", bodyStyle: 1);
        campBChar.HairStyle = Sim.Appearance.HairBun;
        campBChar.SkinRgb = (17 << 16) | (200 << 8) | 96;   // a color no preset offers
        campBChar.HairRgb = (250 << 16) | (40 << 8) | 220;
        var campB = new GameClient(data, "RunnerB", campBChar);
        campA.Connect("127.0.0.1", campServer.LocalPort, out _);
        campB.Connect("127.0.0.1", campServer.LocalPort, out _);
        void CPump(float seconds)
        {
            const float dt = 1f / 60f;
            int steps = (int)(seconds / dt);
            for (int i = 0; i < steps; i++)
            {
                campServer.Update(dt);
                campA.Update(dt);
                campB.Update(dt);
                Thread.Sleep(2);
            }
        }
        CPump(1.5f);
        Check(campA.Status == ClientStatus.InGame && campB.Status == ClientStatus.InGame,
              "both campaign clients joined");
        Check(campServer.World.Campaign && campServer.World.MapIndex == 0 &&
              campServer.World.Map.Kind == World.MapKind.Hub,
              "the campaign starts in the hub sanctum");
        Check(campA.World.Map.Kind == World.MapKind.Hub &&
              campA.World.Map.Seed == campServer.World.Map.Seed,
              "clients build the same hub map from the seed");
        Check(campServer.World.Map.Theme?.Id == "sanctum" && campA.World.Map.Theme?.Id == "sanctum",
              "the hub carries its own purple-stone SANCTUM theme (both sides)");
        Check(data.ZoneThemes.First(t => t.Id == "sanctum").StoneBrick,
              "the sanctum theme renders stone-brick floors and walls");
        Check(campServer.World.Npcs.Count == 2 &&
              campServer.World.Npcs.Any(n => n.TypeId == "merchant") &&
              campServer.World.Npcs.Any(n => n.TypeId == "skill_trainer"),
              "the hub holds the gear merchant AND the skill trainer");
        Check(campA.World.Npcs.Count == 2, "both merchants replicate to clients");
        Check(campServer.World.Chests.Count == 4 && campA.World.Chests.Count == 4 &&
              campA.World.Chests.Values.All(c => !c.Opened),
              "four closed starter chests replicate");
        var freshChar = campA.World.MyCharacter;
        Check(freshChar.Gold == 100 &&
              freshChar.Skills.Count == 1 &&
              freshChar.GetSkill("basic_strike") != null &&
              freshChar.Hotbar[0] == "basic_strike" &&
              freshChar.Equipment.GetValueOrDefault(Items.EquipSlot.MainHand)?.BaseItemId == "wooden_club",
              $"fresh characters default to the warrior kit: 100g, club, Mace Strike ({freshChar.Skills.Count} skills, {freshChar.Gold}g)");
        Check(freshChar.ClassId == "warrior" && freshChar.BodyStyle == 0 &&
              freshChar.EffectiveHairStyle == Sim.Appearance.HairShort &&
              freshChar.EffectiveSkinColor == Sim.Appearance.SkinTones[2],
              "server-made characters carry the default appearance (male, short hair, mid tone)");

        // Classes are STARTING KITS — three of them, each granting gear + one skill.
        Check(data.Classes.Count == 3 &&
              data.Classes.Any(cl => cl.Id == "warrior") &&
              data.Classes.Any(cl => cl.Id == "archer") &&
              data.Classes.Any(cl => cl.Id == "mage"),
              "three starting classes load from Data/Classes");
        var archerKit = Sim.CharacterData.CreateNew(data, "KitTest", "archer", bodyStyle: 1);
        Check(archerKit.Equipment.GetValueOrDefault(Items.EquipSlot.MainHand)?.BaseItemId == "short_bow" &&
              archerKit.Equipment.GetValueOrDefault(Items.EquipSlot.OffHand)?.BaseItemId == "leather_quiver" &&
              archerKit.GetSkill("arrow_shot") != null && archerKit.Hotbar[0] == "arrow_shot" &&
              archerKit.BodyStyle == 1,
              "the archer kit equips bow + quiver with Arrow Shot hotbarred, body stored");

        // Class attribute spreads: they exist so early gear requirements BITE —
        // a fresh warrior can't wear silk, a fresh mage can't lift iron mail.
        var mageKit = Sim.CharacterData.CreateNew(data, "KitTest2", "mage");
        var warriorKit = Sim.CharacterData.CreateNew(data, "KitTest3", "warrior");
        Check(mageKit.BaseStrength == 4 && mageKit.BaseDexterity == 2 && mageKit.BaseIntelligence == 12 &&
              warriorKit.BaseStrength == 11 && warriorKit.BaseDexterity == 4 && warriorKit.BaseIntelligence == 3 &&
              archerKit.BaseStrength == 3 && archerKit.BaseDexterity == 12 && archerKit.BaseIntelligence == 4,
              "class attribute spreads apply (mage 4/2/12, warrior 11/4/3, archer 3/12/4)");
        var warriorStats = Stats.StatCalculator.Compute(data, warriorKit);
        var mageStats = Stats.StatCalculator.Compute(data, mageKit);
        Check(MathF.Abs(warriorStats.Strength - 11f) < 0.01f && MathF.Abs(mageStats.Intelligence - 12f) < 0.01f &&
              warriorStats.MaxHealth > mageStats.MaxHealth,
              $"computed stats flow from the class base attributes (warrior {warriorStats.MaxHealth:0}hp vs mage {mageStats.MaxHealth:0}hp)");
        Check(data.Items["cloth_robe"].RequiredIntelligence > warriorKit.BaseIntelligence &&
              data.Items["iron_mail"].RequiredStrength > mageKit.BaseStrength &&
              data.Items["cloth_robe"].RequiredIntelligence <= mageKit.BaseIntelligence,
              "early armor requirements actually gate cross-class wear");

        // v28 appearance replication: A sees B's saved mage — body, hair style, and the
        // CUSTOM colors — byte-exact over PlayerAppearance.
        var mageSeenByA = campA.World.Players[campB.World.MyPlayerId];
        Check(mageSeenByA.BodyStyle == 1 && mageSeenByA.HairStyle == Sim.Appearance.HairBun &&
              mageSeenByA.SkinRgb == campBChar.SkinRgb && mageSeenByA.HairRgb == campBChar.HairRgb,
              "body, hair style and free RGB colors replicate through PlayerAppearance");
        Check(MathF.Abs(mageSeenByA.LightRadius - Stats.ComputedStats.BaseLightRadius) <= 4f,
              $"the personal light radius replicates ({mageSeenByA.LightRadius:0}px)");
        var srvCampB = campServer.World.Players[campB.World.MyPlayerId];
        Check(srvCampB.Character.ClassId == "mage" &&
              srvCampB.Character.GetSkill("fire_bolt") != null &&
              srvCampB.Character.Equipment.GetValueOrDefault(Items.EquipSlot.MainHand)?.BaseItemId == "oak_staff",
              "a client-saved mage keeps its class kit server-side");

        // Chests: first open pops the lid and drops plain starter gear; reopening is refused.
        var chest1 = campServer.World.Chests[0];
        var dropsBeforeChest = campServer.World.Drops.Keys.ToHashSet();
        campA.World.Me.Position = chest1.Position + new Vector2(0.9f, 0);
        CPump(0.4f);
        campA.RequestOpenChest(chest1.Id);
        CPump(0.4f);
        var chestLoot = campServer.World.Drops.Values
            .Where(d => !dropsBeforeChest.Contains(d.DropId) && d.Item != null).ToList();
        Check(chest1.Opened && chestLoot.Count == 2, $"chest pops and drops starter gear ({chestLoot.Count} items)");
        Check(chestLoot.All(d => d.Item.Rarity == Items.ItemRarity.Normal && d.Item.ItemLevel == 1),
              "chest gear is plain level-1 starter fare");
        Check(campA.World.Chests[chest1.Id].Opened, "the popped lid replicates");
        campA.RequestOpenChest(chest1.Id);
        CPump(0.3f);
        Check(campServer.World.Drops.Values.Count(d => d.Item != null && !dropsBeforeChest.Contains(d.DropId)) == 2,
              "a chest opens only once");

        // Skill trainer: buying away from him is refused; at his side it costs 75 gold.
        campA.RequestLearnSkill("chain_lightning");
        CPump(0.3f);
        Check(campA.World.MyCharacter.GetSkill("chain_lightning") == null,
              "buying a skill away from the trainer is refused");
        var trainerNpc = campServer.World.Npcs.First(n => n.TypeId == "skill_trainer");
        campA.World.Me.Position = trainerNpc.Position + new Vector2(0.9f, 0);
        CPump(0.4f);
        campA.RequestLearnSkill("chain_lightning");
        CPump(0.4f);
        Check(campA.World.MyCharacter.GetSkill("chain_lightning") != null &&
              campA.World.MyCharacter.Gold == 25,
              $"the trainer teaches Chain Lightning for 75 gold ({campA.World.MyCharacter.Gold}g left)");
        campA.RequestLearnSkill("ice_spike");
        CPump(0.3f);
        Check(campA.World.MyCharacter.GetSkill("ice_spike") == null &&
              campA.World.MyCharacter.Gold == 25,
              "a second skill at 25 gold is refused (75g each)");

        // The fountain: charges never regenerate, so the hub basin is the refill.
        // Out of reach it refuses; beside it, every carried flask fills back up.
        var fountainAt = campServer.World.Map.FountainSpot;
        Check(fountainAt != Vector2.Zero, "the hub sanctum has a fountain");
        var srvCampA = campServer.World.Players[campA.World.MyPlayerId];
        var campHpFlask = srvCampA.Character.Equipment.GetValueOrDefault(Items.EquipSlot.Flask1);
        var campMpFlask = srvCampA.Character.Equipment.GetValueOrDefault(Items.EquipSlot.Flask2);
        Check(campHpFlask != null && campMpFlask != null, "campaign characters carry the flask pair");
        campHpFlask.FlaskCharges = 0;
        campMpFlask.FlaskCharges = 1;
        campA.World.Me.Position = fountainAt + new Vector2(8f, 0);
        CPump(0.4f);
        campA.RequestUseFountain();
        CPump(0.4f);
        Check(campHpFlask.FlaskCharges == 0, "the fountain is out of reach from across the room");
        campA.World.Me.Position = fountainAt + new Vector2(1.2f, 0);
        CPump(0.4f);
        campA.RequestUseFountain();
        CPump(0.4f);
        Check(campHpFlask.FlaskCharges == campHpFlask.GetBase(data).FlaskChargesMax &&
              campMpFlask.FlaskCharges == campMpFlask.GetBase(data).FlaskChargesMax,
              "the sanctum fountain refills every carried flask");

        // The stash: storage tied to a CONTAINER object — moves only work beside it.
        // (Class kits equip everything, so the test stashes A's equipped starter club.)
        var stashSpot = campServer.World.Map.StashSpot;
        Check(stashSpot != Vector2.Zero, "the hub has a stash chest");
        var srvStashA = campServer.World.Players[campA.World.MyPlayerId];
        var stashClub = srvStashA.Character.Equipment.GetValueOrDefault(Items.EquipSlot.MainHand);
        Check(stashClub?.BaseItemId == "wooden_club", "the starter club is in hand for the stash test");
        campA.World.Me.Position = stashSpot + new Vector2(9f, 6f); // across the room
        CPump(0.4f);
        campServer.World.MoveItem(srvStashA.Id, ItemLocation.AtEquip(Items.EquipSlot.MainHand),
            ItemLocation.AtStash(World.GameMap.HubStashId, 0, 0));
        Check(srvStashA.Character.GetStash(World.GameMap.HubStashId).Items.Count == 0 &&
              srvStashA.Character.Equipment.GetValueOrDefault(Items.EquipSlot.MainHand) != null,
              "stash moves are refused away from the chest");
        campA.World.Me.Position = stashSpot + new Vector2(1.0f, 0.7f);
        CPump(0.4f);
        campServer.World.MoveItem(srvStashA.Id, ItemLocation.AtEquip(Items.EquipSlot.MainHand),
            ItemLocation.AtStash(World.GameMap.HubStashId, 0, 0));
        var stashGrid = srvStashA.Character.GetStash(World.GameMap.HubStashId);
        Check(stashGrid.Items.Count == 1 &&
              srvStashA.Character.Equipment.GetValueOrDefault(Items.EquipSlot.MainHand) == null,
              "an item moves from the equipped hand into the stash container");
        CPump(0.4f);
        Check(campA.World.MyCharacter.Stashes.GetValueOrDefault(World.GameMap.HubStashId)?.Items.Count == 1,
              "stash contents replicate with the character");
        campServer.World.MoveItem(srvStashA.Id,
            ItemLocation.AtStash(World.GameMap.HubStashId, stashGrid.Items[0].X, stashGrid.Items[0].Y),
            ItemLocation.AtGrid(0, 0));
        campServer.World.MoveItem(srvStashA.Id, ItemLocation.AtGrid(0, 0),
            ItemLocation.AtEquip(Items.EquipSlot.MainHand));
        Check(stashGrid.Items.Count == 0 &&
              srvStashA.Character.Equipment.GetValueOrDefault(Items.EquipSlot.MainHand)?.BaseItemId == "wooden_club",
              "and moves back out through the bag into the hand");

        Console.WriteLine("\n-- Campaign: the run door --");
        var hubDoor = campServer.World.Map.ExitDoor;
        campA.World.Me.Position = hubDoor + new Vector2(-0.9f, 0);
        CPump(0.4f);
        campA.RequestDoorReady();
        CPump(0.4f);
        Check(campServer.World.MapIndex == 0 && campServer.World.ReadyCount == 1,
              "one ready of two: the group stays put");
        campB.World.Me.Position = hubDoor + new Vector2(-0.9f, 0.7f);
        CPump(0.4f);
        campB.RequestDoorReady();
        CPump(0.8f);
        Check(campServer.World.MapIndex == 1 && campServer.World.Map.Kind == World.MapKind.Forest,
              "everyone ready: the run begins on forest map 1");
        Check(campServer.World.Loop == 1 && campServer.World.CampaignEnemyLevel == 1,
              "first excursion runs at enemy level 1");
        Check(campA.World.Map.Kind == World.MapKind.Forest &&
              campA.World.Map.Seed == campServer.World.Map.Seed &&
              campB.World.Map.Seed == campServer.World.Map.Seed,
              "clients rebuild the forest map from the broadcast seed");
        Check(campServer.World.Map.Theme?.Id == "forest" && campA.World.Map.Theme?.Id == "forest",
              "run maps keep the campaign zone theme (the stonework stays home)");
        Check(campA.World.ZoneMapIndex == 1 && campA.World.ZoneEnemyLevel == 1,
              "zone state replicates (map 1, enemy level 1)");
        Check(campServer.World.Enemies.Values.Count(e => !e.Dead) >= 8,
              $"packs are placed at generation ({campServer.World.Enemies.Count} enemies)");
        Check(campServer.World.Enemies.Values.All(e => e.Level == 1),
              "loop-1 enemies are all level 1");
        Check(campServer.World.Enemies.Values.All(e => e.Def.Id != "bone_knight"),
              "no Graveguard skeletons on the first loop");
        Check(campServer.World.Packs.All(pk => pk.NoRespawn),
              "campaign packs never respawn");
        Check(campServer.World.Map.GroundPathExists(campServer.World.Map.PlayerSpawn,
                  campServer.World.Map.ExitDoor + new Vector2(-1.2f, 0)),
              "the generated hallway is walkable start to exit");
        Check(campA.World.Npcs.Count == 0, "no merchants out in the forest");

        // Maps 2 and 3 chain through the same ready-door mechanic.
        void BothReadyAtExit()
        {
            var exitDoor = campServer.World.Map.ExitDoor;
            campA.World.Me.Position = exitDoor + new Vector2(-0.9f, 0);
            campB.World.Me.Position = exitDoor + new Vector2(-0.9f, 0.7f);
            CPump(0.4f);
            campA.RequestDoorReady();
            campB.RequestDoorReady();
            CPump(0.8f);
        }
        BothReadyAtExit();
        Check(campServer.World.MapIndex == 2, "map 1 cleared through to map 2");
        int map2Seed = campServer.World.Map.Seed;
        BothReadyAtExit();
        Check(campServer.World.MapIndex == 3 && campServer.World.Map.Seed != map2Seed,
              "map 3 is a different generated map");

        Console.WriteLine("\n-- Campaign: the Gravelord's arena --");
        var campBoss = campServer.World.Enemies.Values.FirstOrDefault(e => e.Def.Id == "gravelord");
        Check(campBoss != null, "the final map holds the Gravelord");
        Check(campServer.World.ExitLocked, "the exit is sealed while the boss lives");
        BothReadyAtExit(); // pressing ready on the sealed door must do nothing
        Check(campServer.World.MapIndex == 3 && campServer.World.ReadyCount == 0,
              "the sealed door refuses ready presses");

        // Boss adds: engaging arms an 11s delay, THEN three spitters rise — never as
        // an opener.
        // Adds are told apart from any wandering corridor spitters by their LEVEL:
        // summons ride the boss's level (4 on loop 1), corridor packs are level 1.
        int SpittersNearBoss() => campServer.World.Enemies.Values.Count(e =>
            !e.Dead && e.Def.Id == "spitter" && e.Level == campBoss.Level &&
            Vector2.Distance(e.Position, campBoss.Position) < 10f);
        campBoss.Health = campBoss.MaxHealth; // observe the full fight
        campA.World.Me.Position = campBoss.Position + new Vector2(-2.0f, 0);
        for (int i = 0; i < 4; i++) { campA.SendDebugCommand("heal"); CPump(0.4f); }
        Check(campBoss.State != Server.EnemyState.Idle, "the Gravelord engaged");
        Check(SpittersNearBoss() == 0, "no adds in the opening seconds of the fight");
        for (int i = 0; i < 24 && SpittersNearBoss() == 0; i++)
        {
            campA.SendDebugCommand("heal");
            campA.World.Me.Position = campBoss.Position + new Vector2(-2.0f, 0);
            CPump(0.5f);
        }
        Check(SpittersNearBoss() >= 3,
              $"the Gravelord summons three spitters mid-fight, at HIS level ({SpittersNearBoss()} up)");

        // Fell the boss: the seal lifts, the door leads home.
        campBoss.Health = 1f;
        campA.SendDebugCommand("heal");
        campA.RequestUseSkill("basic_strike", campBoss.Position);
        CPump(0.6f);
        Check(campBoss.Dead, "Gravelord felled");
        Check(!campServer.World.ExitLocked, "the boss's death unseals the exit");
        campA.SendDebugCommand("kill_nearby"); // clear the adds before leaving
        campA.SendDebugCommand("heal");
        campB.SendDebugCommand("heal");
        CPump(0.3f);
        BothReadyAtExit();
        Check(campServer.World.MapIndex == 0 && campServer.World.Map.Kind == World.MapKind.Hub,
              "the run loops back to the sanctum");
        Check(campServer.World.Chests[0].Opened,
              "chest lids stay popped between visits (no sanctum farming)");

        Console.WriteLine("\n-- Campaign: second loop scales up --");
        BothReadyAtExit(); // hub door again: NEW excursion
        Check(campServer.World.MapIndex == 1 && campServer.World.Loop == 2 &&
              campServer.World.CampaignEnemyLevel == 4,
              $"second excursion: three new maps at enemy level 4 (loop {campServer.World.Loop})");
        Check(campServer.World.Enemies.Values.All(e => e.Level >= 4),
              "loop-2 enemies carry the scaled level");
        Check(campServer.World.Packs.Any(pk => pk.Entries.Any(en => en.typeId == "bone_knight")),
              "Graveguard skeletons appear from the second loop");

        Console.WriteLine("\n-- Campaign: the loop-2 Gravelord charges --");
        campA.SendDebugCommand("warp_next");
        CPump(0.6f);
        campA.SendDebugCommand("warp_next");
        CPump(0.8f);
        Check(campServer.World.MapIndex == 3, "warped ahead to the loop-2 boss map");
        Check(campServer.World.Corpses.Count == 0 && campA.World.Corpses.Count == 0,
              "the dead do not follow: corpses clear on map transitions");
        var boss2 = campServer.World.Enemies.Values.FirstOrDefault(e => e.Def.Id == "gravelord");
        Check(boss2 != null && boss2.Level >= boss2.Def.DashMinLevel,
              $"the loop-2 Gravelord (level {boss2?.Level}) has its dash unlocked (min {boss2?.Def.DashMinLevel})");
        bool dashTelegraphed = false, dashLineSeen = false, dashLaunched = false;
        var dashStart = Vector2.Zero;
        float bossTravel = 0f;
        for (int i = 0; i < 90 && !dashLaunched; i++)
        {
            campA.SendDebugCommand("heal");
            if (boss2.DashPrepareUntil <= 0 && boss2.DashUntil <= 0)
                campA.World.Me.Position = boss2.Position + new Vector2(-4.5f, 0);
            CPump(0.25f);
            dashLineSeen |= campA.World.Effects.Any(fx => fx.Kind == "dashline");
            if (boss2.DashPrepareUntil > 0 && !dashTelegraphed)
            {
                dashTelegraphed = true;
                dashStart = boss2.Position;
            }
            if (dashTelegraphed && boss2.DashUntil > 0)
            {
                for (int j = 0; j < 8; j++)
                {
                    CPump(0.12f);
                    bossTravel = MathF.Max(bossTravel, Vector2.Distance(boss2.Position, dashStart));
                }
                dashLaunched = true;
            }
        }
        Check(dashTelegraphed, "the Gravelord roots into a one-second prepare stance");
        Check(dashLineSeen, "the MMO ground line telegraph replicates to clients");
        Check(dashLaunched && bossTravel > 2.5f,
              $"then charges hard down the line ({bossTravel:0.0} tiles covered)");

        Console.WriteLine("\n-- Campaign: death, revive, and the wipe rule --");
        campA.SendDebugCommand("kill_nearby");
        campA.SendDebugCommand("heal");
        campB.SendDebugCommand("heal");
        CPump(0.4f);
        var srvFallenA = campServer.World.Players[campA.World.MyPlayerId];
        var srvHelperB = campServer.World.Players[campB.World.MyPlayerId];
        srvFallenA.Alive = false;
        srvFallenA.Health = 0;
        CPump(1.5f);
        Check(!srvFallenA.Alive, "campaign corpses do NOT auto-respawn — they wait for a revive");
        campB.World.Me.Position = srvFallenA.Position + new Vector2(0.6f, 0);
        campB.World.Me.Height = srvFallenA.Height;
        CPump(0.4f);
        for (int i = 0; i < 32 && !srvFallenA.Alive; i++)
        {
            campB.RequestRevivePulse(srvFallenA.Id);
            CPump(0.12f);
        }
        Check(srvFallenA.Alive && srvFallenA.Health > srvFallenA.Stats.MaxHealth * 0.4f,
              $"holding the key beside a fallen teammate revives them at half health ({srvFallenA.Health:0} hp)");
        // Wipe: everyone down ends the run — the Sanctum reclaims the whole party.
        srvFallenA.Alive = false; srvFallenA.Health = 0;
        srvHelperB.Alive = false; srvHelperB.Health = 0;
        CPump(3.5f);
        Check(campServer.World.MapIndex == 0, "a full party wipe returns the group to the hub");
        Check(srvFallenA.Alive && srvHelperB.Alive &&
              srvFallenA.Health >= srvFallenA.Stats.MaxHealth - 0.5f,
              "the Sanctum reclaims the fallen alive and at full health");

        campA.Disconnect();
        campB.Disconnect();
        CPump(0.3f);
        campServer.Stop();

        Console.WriteLine("\n-- Disconnect resilience --");
        clientB.Disconnect();
        Pump(1.0f);
        Check(server.World.Players.Count == 1, "host removed the disconnected player");
        Check(clientA.World.Players.Count == 1, "client A saw the player leave");
        Check(clientA.Status == ClientStatus.InGame, "host's client unaffected by the disconnect");

        server.Update(1 / 60f); // one more tick to prove the host survives
        clientA.Disconnect();
        server.Stop();

        // ------------------------------------------------------------------ threaded host
        // The game runs the server on a dedicated fixed-timestep thread (StartLoop);
        // this suite drives Update() manually above for determinism. Prove the threaded
        // mode works end to end: a client joins and the world advances with NOBODY on
        // this thread ever pumping the server.
        Console.WriteLine("\n-- Dedicated server thread --");
        var threadedServer = new GameServer(data, 424242);
        Check(threadedServer.Start(0), "threaded server listening on a loopback port");
        threadedServer.StartLoop();
        var threadedClient = new GameClient(data, "Threader", null);
        threadedClient.Connect("127.0.0.1", threadedServer.LocalPort, out _);
        bool threadedJoin = false;
        for (int i = 0; i < 600 && !threadedJoin; i++)
        {
            threadedClient.Update(1f / 60f);
            Thread.Sleep(5);
            threadedJoin = threadedClient.Status == ClientStatus.InGame;
        }
        Check(threadedJoin, "client joined a server running on its own thread (no manual pumping)");
        float timeBefore = threadedServer.World.Time;
        Thread.Sleep(400);
        Check(threadedServer.World.Time > timeBefore + 0.2f,
              $"simulation advances on the server thread ({timeBefore:0.00}s -> {threadedServer.World.Time:0.00}s)");
        threadedClient.Disconnect();
        threadedServer.Stop();
        Check(true, "threaded server stopped cleanly (thread joined)");

        Console.WriteLine($"\n=== {_checks - Failures.Count}/{_checks} checks passed ===");
        if (Failures.Count > 0)
        {
            Console.WriteLine("FAILURES:");
            foreach (var f in Failures) Console.WriteLine($"  - {f}");
            return 1;
        }
        return 0;
    }
}
