using ARPG.Stats;

namespace ARPG.Skills;

/// <summary>One stat contribution of a passive node (feeds the same StatCollection
/// pipeline as item modifiers, so every existing stat "just works").</summary>
public class PassiveNodeEffect
{
    public StatType Stat { get; set; }
    public float Value { get; set; }
}

/// <summary>A node of the passive skill tree (Data/SkillTree/tree.json).</summary>
public class PassiveNode
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
    /// <summary>Layout position in tree units (the UI scales these to pixels).</summary>
    public float X { get; set; }
    public float Y { get; set; }
    /// <summary>Start nodes are allocatable with no allocated neighbor — the tree's entry
    /// points (later: one per character class).</summary>
    public bool Start { get; set; }
    public List<PassiveNodeEffect> Effects { get; set; } = new();
}

/// <summary>
/// The PoE-style passive tree: nodes plus undirected connections. Deliberately tiny for
/// now (a ~10-perk starter cluster) — the SYSTEM is the point; class-specific trees can
/// replace the data file later without code changes.
/// </summary>
public class PassiveTree
{
    public List<PassiveNode> Nodes { get; set; } = new();
    /// <summary>Undirected edges as [nodeIdA, nodeIdB] pairs.</summary>
    public List<List<string>> Connections { get; set; } = new();

    private Dictionary<string, PassiveNode> _byId;
    private Dictionary<string, List<string>> _adjacency;

    public Dictionary<string, PassiveNode> ById =>
        _byId ??= Nodes.ToDictionary(n => n.Id);

    public IReadOnlyList<string> Neighbors(string nodeId)
    {
        if (_adjacency == null)
        {
            _adjacency = new Dictionary<string, List<string>>();
            foreach (var pair in Connections)
            {
                if (pair is not { Count: 2 }) continue;
                if (!_adjacency.TryGetValue(pair[0], out var la)) _adjacency[pair[0]] = la = new List<string>();
                if (!_adjacency.TryGetValue(pair[1], out var lb)) _adjacency[pair[1]] = lb = new List<string>();
                la.Add(pair[1]);
                lb.Add(pair[0]);
            }
        }
        return _adjacency.TryGetValue(nodeId, out var list) ? list : Array.Empty<string>();
    }

    /// <summary>Total passive points a character has earned: one per level past the first.</summary>
    public static int PointsForLevel(int level) => Math.Max(0, level - 1);
}
