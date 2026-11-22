using System.Numerics;
using ARPG.Items;
using ARPG.Sim;

namespace ARPG.Server;

/// <summary>
/// Skill books (ServerWorld partial). Players don't get every skill at the start:
/// rare BOOKS drop from bosses and specific monsters, and handing one to the skill
/// trainer unlocks the skills it describes — for the character who hands it over,
/// nobody else. Unlocked skills still cost gold to learn.
///
/// The onboarding guarantee: the first time a character is PRESENT for a Barrow Lord
/// kill, and for a Gilded Codex kill, a book drops for them (one per eligible player,
/// each reserved for its player). The guarantee is only spent when the book is PICKED
/// UP — a book left on the ground is gone with the map, and the next kill drops it again.
/// </summary>
public partial class ServerWorld
{
    /// <summary>Bosses whose first kill guarantees each present character a skill book.</summary>
    public static readonly string[] BookGuaranteeBosses = { "barrowlord", "codex" };

    /// <summary>Learning is gated by unlocks in STORY worlds. The test grounds (the
    /// sanctum loop) and the arena keep every skill teachable, for tuning.</summary>
    public bool SkillsGated => Story;

    public IEnumerable<ItemBase> SkillBooks => Data.Items.Values.Where(b => b.IsSkillBook);

    /// <summary>The (first) book that teaches a skill, for "Requires: ..." hints.</summary>
    public static ItemBase BookFor(Data.GameData data, string skillId) =>
        data.Items.Values.Where(b => b.IsSkillBook && b.UnlocksSkills.Contains(skillId))
            .OrderBy(b => b.Id, StringComparer.Ordinal).FirstOrDefault();

    /// <summary>Hand a skill book to the trainer (SkillBookTurnInRequest). Mirrors the
    /// researcher's blueprint hand-in: in person, the book is consumed, and the unlock
    /// belongs to the player who turned it in.</summary>
    public void TurnInSkillBook(int playerId, Guid itemInstanceId)
    {
        if (!Players.TryGetValue(playerId, out var p) || !p.Alive) return;
        var trainer = Npcs.FirstOrDefault(n => n.TypeId == "skill_trainer");
        if (trainer == null || Vector2.Distance(p.Position, trainer.Position) > 3.5f)
        {
            _events.MessageFor(p, "Skill books are read by the skill trainer — bring it to them.");
            return;
        }
        var placed = p.Character.Inventory.FindByInstance(itemInstanceId);
        var bookBase = placed?.Item.GetBase(Data);
        if (placed == null || bookBase is not { IsSkillBook: true })
        {
            _events.MessageFor(p, "That isn't a skill book.");
            return;
        }
        var c = p.Character;
        var fresh = bookBase.UnlocksSkills.Where(id => Data.Skills.ContainsKey(id) && !c.IsSkillUnlocked(Data, id)).ToList();
        if (fresh.Count == 0)
        {
            // Nothing new in it: refused and kept (trade it, stash it, sell it).
            _events.MessageFor(p, $"You already know everything {bookBase.Name} can teach — keep it for someone who doesn't.");
            return;
        }
        c.Inventory.Remove(itemInstanceId);
        foreach (var id in fresh) c.UnlockedSkills.Add(id);
        string names = string.Join(", ", fresh.Select(id => Data.Skills[id].Name));
        _events.MessageFor(p, $"The trainer reads {bookBase.Name}: you can now learn {names} ({SkillPrice} gold each).");
        MarkCharacterDirty(p);
        QuestTurnIn(p, bookBase);
    }

    /// <summary>Does this book suit the character's class? (Shares the start skill's
    /// weapon; or both are spells.)</summary>
    private bool BookSuitsClass(ItemBase book, CharacterData c)
    {
        var startId = Data.Classes.FirstOrDefault(cl => cl.Id == c.ClassId)?.StartSkill;
        if (startId == null || !Data.Skills.TryGetValue(startId, out var start)) return false;
        foreach (var id in book.UnlocksSkills)
        {
            if (!Data.Skills.TryGetValue(id, out var sk)) continue;
            if (start.RequiredWeapon != null && sk.RequiredWeapon == start.RequiredWeapon) return true;
            if (start.HasTag(Skills.SkillTags.Spell) && sk.HasTag(Skills.SkillTags.Spell)) return true;
        }
        return false;
    }

    /// <summary>Pick the guarantee book for a character: one that teaches something new,
    /// preferring their class's weapon; any book if they somehow know it all.</summary>
    public ItemBase ChooseGuaranteeBook(CharacterData c)
    {
        var books = SkillBooks.OrderBy(b => b.Id, StringComparer.Ordinal).ToList();
        if (books.Count == 0) return null;
        var useful = books.Where(b => b.UnlocksSkills.Any(id => !c.IsSkillUnlocked(Data, id))).ToList();
        var suited = useful.Where(b => BookSuitsClass(b, c)).ToList();
        var pool = suited.Count > 0 ? suited : useful.Count > 0 ? useful : books;
        return pool[_rng.Next(pool.Count)];
    }

    /// <summary>A guarantee boss died: one reserved book per present character whose
    /// guarantee for that boss is still unspent.</summary>
    private void DropGuaranteedBooks(ServerEnemy boss)
    {
        if (!BookGuaranteeBosses.Contains(boss.Def.Id)) return;
        foreach (var pl in Players.Values)
        {
            if (pl.Character.BookGuarantees.Contains(boss.Def.Id)) continue;
            var book = ChooseGuaranteeBook(pl.Character);
            if (book == null) continue;
            var drop = SpawnDrop(new ItemInstance { BaseItemId = book.Id, ItemLevel = 1, Rarity = ItemRarity.Normal, BaseModifierLimit = 0 },
                boss.Position, boss.Height);
            drop.ReservedForPlayerId = pl.Id;
            drop.GuaranteeBossId = boss.Def.Id;
            _events.MessageFor(pl, $"Something for you among the remains: {book.Name}. Take it to the skill trainer.");
        }
    }

    /// <summary>Per-enemy ExtraDrops (skill books from the monsters that carry them).</summary>
    private void RollExtraDrops(ServerEnemy e)
    {
        foreach (var extra in e.Def.ExtraDrops)
        {
            if (!Data.Items.ContainsKey(extra.ItemId ?? "") || _rng.NextDouble() >= extra.Chance * ZoneLootMultiplier) continue;
            SpawnDrop(new ItemInstance { BaseItemId = extra.ItemId, ItemLevel = Math.Max(1, e.Level), Rarity = ItemRarity.Normal, BaseModifierLimit = 0 },
                e.Position, e.Height);
        }
    }
}
