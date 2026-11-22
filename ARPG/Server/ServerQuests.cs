using ARPG.Data;
using ARPG.Items;
using ARPG.Sim;

namespace ARPG.Server;

/// <summary>
/// Quests (ServerWorld partial) — server-authoritative, data-driven
/// (Data/Quests/quests.json), stored on the character (CharacterData.Quests).
///
/// The sync model, decided by the owner: EVERY CHARACTER HAS THEIR OWN QUEST LOG, AND
/// THERE IS NO CATCH-UP. A character completes an objective only by being present when
/// it happens. When an objective event fires, every player on the map (alive or dead)
/// who has that step active gets credit; kill counts are per player; turn-ins and item
/// pickups credit only the player who did them. Quest items are never auto-added — a
/// "collect" step completes when THAT player picks the item up, and an item left
/// behind when the map changes is lost (its source is there again next visit).
/// </summary>
public partial class ServerWorld
{
    /// <summary>Quests are tracked in story worlds (the test grounds have no story to follow).</summary>
    public bool QuestsActive => Story;

    // ------------------------------------------------------------------ log upkeep

    /// <summary>Offer every quest whose prerequisite this character has completed.</summary>
    private bool OfferQuests(ServerPlayer p)
    {
        var log = p.Character.Quests;
        bool added = false;
        foreach (var def in Data.Quests)
        {
            if (def.Steps.Count == 0 || log.IsCompleted(def.Id) || log.Get(def.Id) != null) continue;
            if (!string.IsNullOrEmpty(def.Requires) && !log.IsCompleted(def.Requires)) continue;
            log.Active.Add(new QuestProgress { QuestId = def.Id });
            added = true;
            _events.MessageFor(p, $"New quest — {def.Name}: {def.Steps[0].Text}.");
        }
        if (added) MarkCharacterDirty(p);
        return added;
    }

    /// <summary>The quest a player is "on" for the party's benefit: the first active
    /// main-line quest, else the first active side quest.</summary>
    public (string questId, int step) QuestStatusOf(ServerPlayer p)
    {
        if (!QuestsActive) return ("", 0);
        var log = p.Character.Quests;
        var tracked = log.Active.FirstOrDefault(q => Data.GetQuest(q.QuestId) is { Side: false }) ?? log.Active.FirstOrDefault();
        return tracked == null ? ("", 0) : (tracked.QuestId, tracked.Step);
    }

    private bool MapMatches(string target)
    {
        if (string.IsNullOrEmpty(target)) return true;
        if (target.StartsWith("zone:"))
            return MapIndex == ScrollMapIndex && Map.Theme?.Id == target[5..];
        return Map.Kind.ToString() == target;
    }

    private bool ItemMatches(string target, ItemBase item) => item != null && (target switch
    {
        "category:skill_book" => item.IsSkillBook,
        "category:warp_scroll" => item.Category == ItemCategory.WarpScroll,
        _ => item.Id == target,
    });

    private static bool EnemyMatches(string target, EnemyDefinition def) =>
        target != null && (target.StartsWith("family:") ? def.Family == target[7..] : def.Id == target);

    /// <summary>Credit a player's current step; completes the step / quest as it fills.</summary>
    private void QuestCredit(ServerPlayer p, QuestProgress q, QuestDefinition def, int amount = 1)
    {
        var step = def.Steps[q.Step];
        q.Count += amount;
        MarkCharacterDirty(p);
        if (q.Count < Math.Max(1, step.Count)) return;
        q.Step++;
        q.Count = 0;
        if (q.Step < def.Steps.Count)
        {
            _events.MessageFor(p, $"Quest updated — {def.Name}: {def.Steps[q.Step].Text}.");
            return;
        }
        // Quest complete: rewards, then whatever it unlocks.
        var log = p.Character.Quests;
        log.Active.Remove(q);
        if (!log.Completed.Contains(def.Id)) log.Completed.Add(def.Id);
        var rewards = new List<string>();
        if (def.RewardGold > 0) { p.Character.Gold += def.RewardGold; rewards.Add($"{def.RewardGold} gold"); }
        if (def.RewardXp > 0) { GrantCharacterXp(p, def.RewardXp); rewards.Add($"{def.RewardXp:0} xp"); }
        if (!string.IsNullOrEmpty(def.RewardItem) && Data.Items.TryGetValue(def.RewardItem, out var rewardBase))
        {
            GiveItem(p, new ItemInstance { BaseItemId = rewardBase.Id, ItemLevel = Math.Max(1, p.Character.Level), BaseModifierLimit = rewardBase.BaseModifierLimit });
            rewards.Add(rewardBase.Name);
        }
        _events.MessageFor(p, $"Quest complete — {def.Name}" + (rewards.Count > 0 ? $" ({string.Join(", ", rewards)})." : "."));
        OfferQuests(p);
    }

    /// <summary>Steps that are true just by where the player stands or what they hold:
    /// reach_map on the current map, collect with the item already in the bag.</summary>
    private void QuestCheckPassive(ServerPlayer p)
    {
        if (!QuestsActive) return;
        for (int guard = 0; guard < 16; guard++)
        {
            bool progressed = false;
            foreach (var q in p.Character.Quests.Active.ToList())
            {
                var def = Data.GetQuest(q.QuestId);
                if (def == null || q.Step >= def.Steps.Count) continue;
                var step = def.Steps[q.Step];
                bool done = step.Type switch
                {
                    "reach_map" => MapMatches(step.Target),
                    "collect" => p.Character.Inventory.Items.Any(pl => ItemMatches(step.Target, pl.Item.GetBase(Data))),
                    _ => false,
                };
                if (!done) continue;
                QuestCredit(p, q, def, Math.Max(1, step.Count));
                progressed = true;
            }
            if (!progressed) break;
        }
    }

    /// <summary>Run <paramref name="match"/> over one player's active steps of a type.</summary>
    private void QuestEvent(ServerPlayer p, string type, Func<QuestStep, bool> match)
    {
        if (!QuestsActive) return;
        bool any = false;
        foreach (var q in p.Character.Quests.Active.ToList())
        {
            var def = Data.GetQuest(q.QuestId);
            if (def == null || q.Step >= def.Steps.Count) continue;
            var step = def.Steps[q.Step];
            if (step.Type != type || !match(step)) continue;
            if (!string.IsNullOrEmpty(step.MapKind) && Map.Kind.ToString() != step.MapKind) continue;
            QuestCredit(p, q, def);
            any = true;
        }
        if (any) QuestCheckPassive(p); // the next step may already be true (standing on the map it names)
    }

    // ------------------------------------------------------------------ hooks

    /// <summary>A player joined: offer what they qualify for; they are PRESENT on this map.</summary>
    public void QuestOnJoin(ServerPlayer p)
    {
        if (!QuestsActive) return;
        OfferQuests(p);
        QuestCheckPassive(p);
    }

    private void QuestMapChanged()
    {
        if (!QuestsActive) return;
        foreach (var p in Players.Values) QuestCheckPassive(p);
    }

    /// <summary>An enemy died: everyone on the map with a matching step is credited
    /// (their own count each), alive or dead.</summary>
    private void QuestEnemyKilled(ServerEnemy e)
    {
        if (!QuestsActive) return;
        foreach (var p in Players.Values.ToList())
        {
            QuestEvent(p, "kill_boss", step => step.Target == e.Def.Id);
            QuestEvent(p, "kill", step => EnemyMatches(step.Target, e.Def));
        }
    }

    /// <summary>The player spoke to an NPC (TalkRequest; reach is checked here).</summary>
    public void TalkTo(int playerId, int npcId)
    {
        if (!Players.TryGetValue(playerId, out var p) || !p.Alive) return;
        var npc = Npcs.FirstOrDefault(n => n.Id == npcId);
        if (npc == null || System.Numerics.Vector2.Distance(p.Position, npc.Position) > 3.5f) return;
        QuestEvent(p, "talk", step => step.Target == npc.TypeId);
    }

    /// <summary>Turn-ins credit only the player who turned the item in.</summary>
    private void QuestTurnIn(ServerPlayer p, ItemBase item) =>
        QuestEvent(p, "turn_in", step => ItemMatches(step.Target, item));

    /// <summary>Item steps complete when THAT player picks the item up.</summary>
    private void QuestItemPickedUp(ServerPlayer p, ItemInstance item) =>
        QuestEvent(p, "collect", step => ItemMatches(step.Target, item.GetBase(Data)));

    private void QuestPodiumUsed()
    {
        foreach (var p in Players.Values.ToList()) QuestEvent(p, "use_podium", _ => true);
    }

    /// <summary>The party walked out of a finished portal run (its exit only opens once
    /// the run's goal is met).</summary>
    private void QuestRunCompleted()
    {
        foreach (var p in Players.Values.ToList()) QuestEvent(p, "complete_run", _ => true);
    }

    /// <summary>Does this player have an unfinished "collect" step for this kind of item?</summary>
    public bool QuestStillNeedsItem(ServerPlayer p, ItemBase item)
    {
        if (!QuestsActive || item == null) return false;
        foreach (var q in p.Character.Quests.Active)
        {
            var def = Data.GetQuest(q.QuestId);
            if (def == null) continue;
            for (int i = q.Step; i < def.Steps.Count; i++)
                if (def.Steps[i].Type == "collect" && ItemMatches(def.Steps[i].Target, item)) return true;
        }
        return false;
    }

    /// <summary>Test hook: stand a fallen player back up where they lie.</summary>
    public void DebugRevive(ServerPlayer p) => RevivePlayer(p, atSpawn: false, healthFraction: 1f);

    /// <summary>Tell everyone whose tracked quest step changed (the "helping X" line).</summary>
    private void FlushQuestStatus()
    {
        if (!QuestsActive) return;
        foreach (var p in Players.Values)
        {
            var (questId, step) = QuestStatusOf(p);
            string key = questId + "#" + step;
            if (key == p.LastQuestStatusKey) continue;
            p.LastQuestStatusKey = key;
            _events.QuestStatusChanged(p, questId, step);
        }
    }
}
