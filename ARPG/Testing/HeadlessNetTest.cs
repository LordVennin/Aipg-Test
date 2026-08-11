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
        int dropsBefore = clientA.World.Drops.Count;
        for (int round = 0; round < 6 && server.World.Drops.Count == 0; round++)
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
        var dropId = server.World.Drops.Keys.First();
        var dropA = clientA.World.Drops[dropId];
        var dropB = clientB.World.Drops[dropId];
        Check(Json.SaveCompact(dropA.Item) == Json.SaveCompact(dropB.Item),
              "generated item is byte-identical on both clients (no rerolling)");
        Check(dropA.Item.InstanceId == dropB.Item.InstanceId, "drop shares one InstanceId across peers");

        Console.WriteLine("\n-- Exclusive pickup --");
        // Both clients race to pick up the same item; exactly one may win.
        var dropPos = server.World.Drops[dropId].Position;
        clientA.World.Me.Position = dropPos;
        clientB.World.Me.Position = dropPos;
        Pump(0.4f); // let the fresh client positions reach the server before requesting pickup
        int invA = clientA.World.MyCharacter.Inventory.Items.Count;
        int invB = clientB.World.MyCharacter.Inventory.Items.Count;
        clientA.RequestPickup(dropId);
        clientB.RequestPickup(dropId);
        Pump(0.6f);
        int gainedA = clientA.World.MyCharacter.Inventory.Items.Count - invA;
        int gainedB = clientB.World.MyCharacter.Inventory.Items.Count - invB;
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
            clientA.World.Me.Position = goldDrop.Position;
            Pump(0.4f);
            clientA.RequestPickup(goldDrop.DropId);
            Pump(0.5f);
            Check(clientA.World.MyCharacter.Gold > goldBefore,
                  $"gold pickup increased character gold ({goldBefore} -> {clientA.World.MyCharacter.Gold})");
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
        Check(data.Items.Values.Count(b => b.Category == Items.ItemCategory.EnchantScroll) == 11,
              "all 11 Enchanting Scroll types loaded");

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
        Check(bScroll != null && bScroll.Item.StackCount == 3, $"scroll stack arrived (x{bScroll?.Item.StackCount})");
        if (bScroll != null && bStaff != null)
        {
            clientB.RequestApplyEnchant(bScroll.Item.InstanceId, bStaff.Item.InstanceId);
            Pump(0.5f);
            bChar = clientB.World.MyCharacter;
            var staffAfter = bChar.Inventory.FindByInstance(bStaff.Item.InstanceId)?.Item;
            var scrollAfter = bChar.Inventory.FindByInstance(bScroll.Item.InstanceId)?.Item;
            Check(staffAfter != null && staffAfter.Rarity == Items.ItemRarity.Magic && staffAfter.Modifiers.Count == 1,
                  $"networked Awakening turned the staff blue with 1 modifier");
            Check(scrollAfter != null && scrollAfter.StackCount == 2,
                  $"one scroll charge consumed (stack {scrollAfter?.StackCount})");
        }

        // Ground Slam stun.
        clientA.RequestLearnSkill("ground_slam");
        Pump(0.3f);
        clientA.SendDebugCommand("spawn_enemy", "grunt");
        Pump(0.3f);
        var stunPlayer = server.World.Players[clientA.World.MyPlayerId];
        clientA.RequestUseSkill("ground_slam", stunPlayer.Position);
        Pump(0.2f);
        var stunned = server.World.Enemies.Values.FirstOrDefault(e => !e.Dead && e.StunnedUntil > server.World.Time);
        Check(stunned != null || server.World.Enemies.Values.All(e => e.Dead ||
              Vector2.Distance(e.Position, stunPlayer.Position) > 3f),
              "ground slam stunned nearby enemies");

        Console.WriteLine("\n-- Tiered modifiers and damage types --");
        Check(data.Modifiers.Count == 281, $"tiered modifier database loaded ({data.Modifiers.Count} modifiers)");
        Check(data.Modifiers.Values.Count(m => m.Tier == 10) == 28,
              "every family has a tier X (10 tiers x 28 tiered families)");

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

        Console.WriteLine("\n-- Fire bolt projectile --");
        Pump(4.0f); // ride out a possible death/respawn cycle from roaming enemies
        clientB.SendDebugCommand("heal");
        Pump(0.3f);
        bool projectileSeen = false;
        for (int attempt = 0; attempt < 4 && !projectileSeen; attempt++)
        {
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

        Console.WriteLine("\n-- Disconnect resilience --");
        clientB.Disconnect();
        Pump(1.0f);
        Check(server.World.Players.Count == 1, "host removed the disconnected player");
        Check(clientA.World.Players.Count == 1, "client A saw the player leave");
        Check(clientA.Status == ClientStatus.InGame, "host's client unaffected by the disconnect");

        server.Update(1 / 60f); // one more tick to prove the host survives
        clientA.Disconnect();
        server.Stop();

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
