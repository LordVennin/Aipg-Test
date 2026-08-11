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
