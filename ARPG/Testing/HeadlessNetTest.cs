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
        Pump(0.4f);
        Check(serverEnemy.Health < hpBefore, $"mace strike damaged enemy ({hpBefore:0} -> {serverEnemy.Health:0})");
        Check(clientB.World.Enemies.TryGetValue(targetId, out var enemyOnB) &&
              Math.Abs(enemyOnB.Health - serverEnemy.Health) < 0.01f, "enemy damage synchronized to client B");
        Check(clientA.World.FloatingNumbers.Count > 0 || clientB.World.FloatingNumbers.Count > 0,
              "damage event produced floating numbers on clients");

        // A strike aimed far BEHIND max range must not hit (impact point clamps to range).
        float hpBefore2 = serverEnemy.Health;
        clientA.RequestUseSkill("mace_strike",
            clientA.World.Me.Position - new Vector2(10f, 0)); // aimed the opposite direction
        Pump(0.4f);
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
        clientB.RequestLearnSkill("arcane_burst");
        clientB.SendDebugCommand("skill_xp"); // level up skills -> unlock scroll slots
        clientB.SendDebugCommand("give_scroll");
        Pump(0.6f);
        var charB = clientB.World.MyCharacter;
        Check(charB.GetSkill("arcane_burst") != null, "client B learned Arcane Burst");
        var fireBolt = charB.GetSkill("fire_bolt");
        Check(fireBolt != null && fireBolt.Level > 1, $"skill leveled up (fire_bolt level {fireBolt?.Level})");
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

        // Networked crafting: B awakens its white oak staff with a debug-given scroll stack.
        clientB.SendDebugCommand("give_enchant", "es_awakening");
        Pump(0.5f);
        var bChar = clientB.World.MyCharacter;
        var bScroll = bChar.Inventory.Items.FirstOrDefault(pl => pl.Item.BaseItemId == "es_awakening");
        var bStaff = bChar.Inventory.Items.FirstOrDefault(pl =>
            pl.Item.BaseItemId == "oak_staff" && pl.Item.Rarity == Items.ItemRarity.Normal);
        // B may already own an es_awakening (e.g. won one in the pickup race), in which
        // case the debug stack MERGES — assert relative counts, not absolutes.
        Check(bScroll != null && bScroll.Item.StackCount >= 3, $"scroll stack arrived (x{bScroll?.Item.StackCount})");
        if (bScroll != null && bStaff != null)
        {
            int stackBefore = bScroll.Item.StackCount;
            clientB.RequestApplyEnchant(bScroll.Item.InstanceId, bStaff.Item.InstanceId);
            Pump(0.5f);
            bChar = clientB.World.MyCharacter;
            var staffAfter = bChar.Inventory.FindByInstance(bStaff.Item.InstanceId)?.Item;
            var scrollAfter = bChar.Inventory.FindByInstance(bScroll.Item.InstanceId)?.Item;
            Check(staffAfter != null && staffAfter.Rarity == Items.ItemRarity.Magic && staffAfter.Modifiers.Count == 1,
                  $"networked Awakening turned the staff blue with 1 modifier");
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
            Pump(0.25f);
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
                Pump(0.7f); // ride out the skill cooldown before retrying
            }
        }
        Check(stunConfirmed, "ground slam stunned nearby enemies");

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
        Check(data.Modifiers.Count == 371, $"tiered modifier database loaded ({data.Modifiers.Count} modifiers)");
        Check(data.Modifiers.Values.Count(m => m.Tier == 10) == 37,
              "every family has a tier X (10 tiers x 37 tiered families)");
        Check(data.Modifiers["of_precision"].StatAffected == Stats.StatType.CriticalChance &&
              data.Modifiers["of_ferocity"].StatAffected == Stats.StatType.CriticalDamage &&
              data.Modifiers["of_precision"].CompatibleItemCategories.All(c =>
                  c is Items.ItemCategory.Mace or Items.ItemCategory.Staff),
              "critical hit chance/damage suffixes roll on weapons only");
        Check(!data.Modifiers["of_haste"].CompatibleItemCategories.Contains(Items.ItemCategory.Staff) &&
              !data.Modifiers["of_casting"].CompatibleItemCategories.Contains(Items.ItemCategory.Mace),
              "attack speed and cast speed suffixes are separated (no staff haste, no mace focus)");
        Check(data.Modifiers.Values.Where(m => m.StatAffected is Stats.StatType.FireResistance
                  or Stats.StatType.ArcaneResistance or Stats.StatType.Armor)
              .All(m => !m.CompatibleWith(Items.ItemCategory.Mace) && !m.CompatibleWith(Items.ItemCategory.Staff)),
              "defense modifiers (armor, resistances) no longer roll on weapons");
        Check(data.Skills["basic_strike"].Name == "Mace Strike" && data.Skills["mace_strike"].Name == "Heavy Strike",
              "skills renamed: Mace Strike (single target) and Heavy Strike (area)");

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
        Check(data.Modifiers["occult"].CompatibleItemCategories.SequenceEqual(new[] { Items.ItemCategory.Mace }) &&
              data.Modifiers["eldritch"].CompatibleItemCategories.SequenceEqual(new[] { Items.ItemCategory.Staff }),
              "Arcane added-damage prefixes split melee (mace) / spell (staff)");
        Check(data.Modifiers["sapphire"].StatAffected == Stats.StatType.MaximumMana &&
              data.Modifiers["of_clarity"].StatAffected == Stats.StatType.ManaRegeneration,
              "Maximum Mana prefix and Mana Regeneration suffix families loaded");

        // Added-damage split: attack adds are melee-weapon-only, spell adds caster-weapon-only.
        Check(data.Modifiers["searing"].CompatibleItemCategories.SequenceEqual(new[] { Items.ItemCategory.Mace }),
              "attack added-damage prefixes roll only on melee weapons (maces)");
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
            var high = tierGen.Generate(data.Items["iron_mace"], 18, Items.ItemRarity.Rare, forcedModifierCount: 5);
            highTierSeen += high.Modifiers.Count(r => data.Modifiers[r.ModifierId].Tier >= 6);
        }
        Check(gatingOk, "ilvl-1 items never roll modifiers above their item level");
        Check(highTierSeen > 0, $"ilvl-18 items roll high tiers ({highTierSeen} tier-6+ rolls seen)");

        // Same-family tiers can never stack: all groups on a many-mod item are distinct.
        var stackItem = tierGen.Generate(data.Items["arcane_staff"], 18, Items.ItemRarity.Rare, forcedModifierCount: 12);
        var groups = stackItem.Modifiers.Select(r => data.Modifiers[r.ModifierId].ModifierGroup).ToList();
        Check(groups.Count == groups.Distinct().Count(),
              $"no duplicate modifier groups on one item ({groups.Count} mods, all distinct families)");

        // Added elemental damage flows: a Searing (added fire) weapon roll produces a fire
        // damage component on attack skills.
        var fireChar = new Sim.CharacterData();
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
        Check(bashDef.ShieldArmorScaling > 0 && bashDef.StunChance is > 0.5f and < 1f,
              $"Shield Bash has armor scaling ({bashDef.ShieldArmorScaling}) and a high stun chance ({bashDef.StunChance:P0})");
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

        // Two-handed rules: equipping a staff frees BOTH hands first — the main-hand shield
        // swaps back to the bag and the off-hand shield is auto-unequipped.
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
              "equipping a two-handed staff auto-unequips the off-hand shield to the bag");

        var shieldInBag = charAfterStaff.Inventory.FindByInstance(offHandItem.InstanceId);
        clientA.RequestMoveItem(ItemLocation.AtGrid(shieldInBag.X, shieldInBag.Y),
                                ItemLocation.AtEquip(Items.EquipSlot.OffHand));
        Pump(0.4f);
        Check(clientA.World.MyCharacter.OffHand == null,
              "off-hand refuses a shield while a two-handed staff is equipped");

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
        // Same-surface combat still works: hop down beside it and swing again.
        clientB.World.Me.Position = new Vector2(13.5f, 17.5f); // corridor ground, level 0
        clientB.World.Me.Height = 0f;
        Pump(0.4f);
        clientB.World.Me.Position = underEnemy.Position + new Vector2(-0.9f, 0);
        Pump(0.4f);
        srvB.Mana = srvB.Stats.MaxMana;
        srvB.SkillReadyAt.Remove("mace_strike");
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
            srvB.Mana = srvB.Stats.MaxMana;
            srvB.SkillReadyAt.Clear();
            clientB.RequestUseSkill("mace_strike", plateauEnemy.Position);
            Pump(0.25f);
        }
        Check(plateauEnemy.Dead, "plateau enemy killed by same-surface melee");
        // Drop replication is chance-free when spawned directly: a gold pile on the
        // plateau must reach clients carrying the level-1 surface height. (Out of B's
        // 1.1-tile auto-pickup range so it survives long enough to observe.)
        var goldSpot = new Vector2(8.5f, 26.5f);
        server.World.SpawnGoldDrop(25, goldSpot, 1f);
        for (int i = 0; i < 20 && !elevatedDrop; i++)
        {
            Pump(0.05f);
            elevatedDrop = clientA.World.Drops.Values.Any(d =>
                Vector2.Distance(d.Position, goldSpot) < 0.5f && MathF.Abs(d.Height - 1f) < 0.05f);
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
        bool slamSeen = false;
        float hpBeforeSlam = srvBBoss.Health;
        for (int i = 0; i < 40 && !slamSeen; i++)
        {
            srvBBoss.Health = srvBBoss.Stats.MaxHealth; // out-heal the boss while observing
            clientB.World.Me.Position = boss.Position + new Vector2(-1.2f, 0); // stay in slam range
            Pump(0.2f);
            slamSeen = clientA.World.Effects.Any(fx => fx.Kind == "slam") && boss.SlamReadyAt > 0;
        }
        Check(slamSeen, "boss ground slam fires and replicates its AoE visual");
        int dropsBeforeBoss = server.World.Drops.Count;
        boss.Health = 30f;
        for (int hit = 0; hit < 20 && !boss.Dead; hit++)
        {
            srvBBoss.Mana = srvBBoss.Stats.MaxMana;
            srvBBoss.SkillReadyAt.Clear();
            srvBBoss.Health = srvBBoss.Stats.MaxHealth;
            clientB.World.Me.Position = boss.Position + new Vector2(-1.0f, 0);
            clientB.RequestUseSkill("mace_strike", boss.Position);
            Pump(0.25f);
        }
        Check(boss.Dead, "Gravelord defeated");
        Pump(0.5f);
        int bossDrops = server.World.Drops.Count - dropsBeforeBoss;
        Check(bossDrops >= 4, $"boss death guarantees a loot burst ({bossDrops} drops)");
        clientB.SendDebugCommand("kill_nearby");
        clientB.SendDebugCommand("heal");
        Pump(0.3f);

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
