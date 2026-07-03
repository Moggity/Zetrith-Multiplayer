using System.Collections.Generic;
using Multiplayer.Common.StateHashing;
using RimWorld.Planet;
using Verse;

namespace Multiplayer.Client.Desyncs.StateHashing;

public record StateHashResult(
    ulong SimRoot,
    ulong CosmeticRoot,
    IReadOnlyList<HashingXmlWriter.RecordedNode> Nodes,
    IReadOnlyList<HashingXmlWriter.RecordedNode> CosmeticNodes);

/// <summary>
/// Computes hierarchical digests of the full synced game state by running the Scribe saving
/// pipeline through a <see cref="HashingXmlWriter"/>. Every ExposeData in the game (including
/// modded ones) contributes, so a digest mismatch between clients localizes diverged state
/// down to the recorded node granularity — independently of when (or whether) the divergence
/// ever touches the RNG stream that <see cref="SyncCoordinator"/> compares.
/// </summary>
public static class StateHasher
{
    /// <summary>
    /// Element names whose subtrees are legitimately different between clients and hash into
    /// the cosmetic channel: name nodes contain strings produced from each client's language
    /// data (pawn/faction/settlement names via grammar and translated name banks).
    /// </summary>
    private static readonly string[] CosmeticElements = { "name" };

    /// <summary>
    /// With the structure written below, depth 4 reaches individual things:
    /// game(0)/maps(1)/li(2)/things(3)/thing(4). World subsystems sit at depth 2.
    /// </summary>
    public const int ThingLevelRecordDepth = 4;

    /// <summary>
    /// Hashes the same state that <see cref="SaveLoad.SaveGameToDoc"/> saves, minus the pieces
    /// that are local to a client by design (meta header, camera, currentMapIndex).
    /// Must run on the main thread, outside of any other Scribe operation, at a tick boundary.
    /// </summary>
    public static StateHashResult HashGame(int recordDepth = ThingLevelRecordDepth)
    {
        var writer = new HashingXmlWriter(recordDepth, CosmeticElements);

        SaveCompression.doSaveCompression = true;
        try
        {
            Scribe.mode = LoadSaveMode.Saving;
            Scribe.saver.writer = writer;
            writer.WriteStartDocument();

            Scribe.EnterNode("game");
            Current.Game.ExposeSmallComponents();
            World world = Current.Game.World;
            Scribe_Deep.Look(ref world, "world");
            List<Map> maps = Find.Maps;
            Scribe_Collections.Look(ref maps, "maps", LookMode.Deep);
            Scribe.ExitNode();
        }
        finally
        {
            SaveCompression.doSaveCompression = false;
            Scribe.saver.FinalizeSaving();
        }

        return new StateHashResult(writer.SimRoot, writer.CosmeticRoot, writer.Records, writer.CosmeticRecords);
    }
}
