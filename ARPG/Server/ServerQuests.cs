using ARPG.Items;

namespace ARPG.Server;

/// <summary>Quest hooks (ServerWorld partial).</summary>
public partial class ServerWorld
{
    private void QuestTurnIn(ServerPlayer p, ItemBase item) { }
    private void QuestItemPickedUp(ServerPlayer p, ItemInstance item) { }
}
