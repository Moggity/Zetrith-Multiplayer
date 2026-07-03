# Desync root-cause identification via Merkle state hashing

Focus of this document (per investigation decision): build **hierarchical (Merkle) game-state
hashing** into Multiplayer as the primary root-cause identification tool. Everything else from
earlier drafts is discarded except the findings below, which both motivate and validate the
design.

Environment (from `Desync102.zip`): RimWorld 1.6.4871 rev591, Multiplayer `0.11.5+4a3be27-dirty`
(a build of this repo's HEAD), 2 players, async time off, 1 map. Mods: Prepatcher, Harmony,
Core + all DLC, OgreStack, Haul to Stack, Multiplayer, Pick Up And Haul.

---

## Finding from Desync102: the divergent call, exactly

Diffing `local_traces.txt` against `host_traces.txt` (same zip) shows the two RNG streams are
identical up to trace index 517 and shifted by exactly one call from index 518 on. At index 518,
tick 1865619, the **host executed one extra main-stream RNG draw** the client never made:

```
Rand.Bool <- Rand.Element<int>
  <- Multiplayer.Client.SeedGrammar.Prefix            (Source/Client/Patches/Seeds.cs:176)
  <- RimWorld.PawnBioAndNameGenerator.GeneratePawnName
  <- Verse.Pawn_AgeTracker.CheckChangePawnKindName
  <- Pawn_AgeTracker.RecalculateLifeStageIndex
  <- Pawn_AgeTracker.CalculateGrowth
  <- Pawn_AgeTracker.AgeTickInterval                   on 'Cow156958'
```

`Cow156958` changed life stage on that tick on both clients (the simulation itself was still in
sync). Vanilla's `CheckChangePawnKindName` then decides whether to regenerate the animal's
numeric name — a decision **gated on string comparison between the pawn's stored `Name` and the
current translated kind label**. The host's gate said "rename" (consuming one RNG draw via the
`SeedGrammar` advance); the client's gate said "nothing to do". One extra draw → every subsequent
state in the stream differs → "Trace hashes don't match" / "Wrong random state" 21 ticks later.

### Why the gate can differ while the simulation is identical

Pawn name strings are produced by grammar resolution using the **active language's** rule packs
and labels, on each client independently (MP seeds the grammar RNG but cannot make different
language data produce equal strings). Two consequences:

1. In a session where players run different game languages (or differently complete
   translations — the client's earlier logs showed *"Translation data for language Latin American
   Spanish has 35 errors"*), animals get names like `Cow 5` on one side and `Vaca 5` on the
   other, **silently baked into each side's game state** from the moment of naming.
2. That latent string divergence is invisible to the RNG-stream detector — until vanilla code
   *reads it back into a simulation decision*. `CheckChangePawnKindName` is exactly such a reader:
   stored-name-vs-kind-label comparisons evaluate differently per side, so the RNG-consuming
   rename fires on one side only. Every animal life-stage transition is a potential desync — which
   matches the observed cadence (animal-heavy colony, desyncs every few in-game days, both
   map-stream and world-stream hits, since world-pawn animals age too).

This is the archetype of the bug class the RNG detector fundamentally cannot localize: **state
diverges quietly at time T, the desync fires at time T+n from an unrelated-looking callsite.**
The trace diff caught this one because the read-back happens to consume RNG; a gate that instead
altered, say, a stack count would surface thousands of ticks later, anywhere. Hence: state
hashing.

> Immediate corollary (small, separate from the hasher work): MP should patch
> `Pawn_AgeTracker.CheckChangePawnKindName` to isolate it (`Rand.PushState/PopState` around the
> original, so the conditional rename consumes zero main-stream draws on either side). Names are
> already per-language divergent by design; isolating the RNG makes that divergence harmless.
> This fixes the identified desync regardless of the hasher timeline. A players-side workaround
> exists meanwhile: run identical game languages on both sides.

---

# The plan: Merkle state hashing

## Goal

Replace "Wrong random state on map 0" with *"`Thing Cow156958` field `name`: `Cow 5` vs
`Vaca 5`, first diverged ≤ tick 1863000"* — automatically, in the desync report, for any state
divergence, without needing a reproduction.

## Core design

### 1. HashScribe — hashing through the existing save pipeline

Every persistent object already enumerates its state deterministically via `ExposeData`. Reuse
it wholesale: run the Scribe saving pipeline with a **hashing writer** instead of an XML
document builder.

- Implementation point: the `ScribeSaver` writes through an `XmlWriter`. Provide a
  `HashingXmlWriter : XmlWriter` that maintains a stack of running hash contexts (xxHash64 or
  similar non-crypto, allocation-free): `WriteStartElement` pushes a child context,
  `WriteString`/attributes feed it, `WriteEndElement` folds the child digest into the parent and
  — at configurable depth — records `(element path → digest)` into a bounded table.
- Zero per-class work, zero behavior change to saving; anything `ExposeData` covers is covered.
  MP already drives Scribe directly (`ScribeUtil`), so the plumbing precedent exists.
- Depth-limited digest recording keeps memory bounded: record digests for levels
  *game → world/map → subsystem → individual thing/component*; below thing level, digests fold
  into the thing digest without being stored (recomputed on demand during drill-down).

### 2. What gets hashed, at what granularity

Per map: each `Thing` (via its `ExposeData`, bucketed by def category), map components
(reservations, lords, zones, areas, designations, haul destinations, wealth), pawn subtrees
(needs, health, jobs, inventory — these come free as nested elements of the pawn's digest).
Per world: factions, world pawns, ideos, world objects. Plus the RNG state itself as one leaf.

Caveats learned from the codebase:

- **Save-compression must be disabled in hash mode** (`CompressibilityDeciderUtility` /
  `SaveCompressiblePatch`): compressible things (rock, filth) must hash per-thing or at least
  per-grid-chunk, not as one opaque string, to keep drill-down useful.
- **Derived/cached state must not be hashed** — only what `ExposeData` persists. This falls out
  of the design for free and is why reusing Scribe beats reflection-walking objects.
- **Collection order**: `ExposeData` output order is deterministic given identical state; things
  themselves are iterated in a fixed order (spawn/register order). Order divergence is itself
  state divergence — the hasher correctly reports it rather than masking it.

### 3. Two channels: sim-critical vs cosmetic (design constraint proven by the Cow156958 finding)

Some state is **legitimately different across peers**: grammar-generated strings in the active
language (pawn names, art descriptions, battle-log/tale text). Raw whole-state hashing would
flag these instantly and permanently ("everything differs, always").

- Maintain a small **field-level exclusion registry** (element-path patterns, e.g.
  `.../name/*`, tale/battle-log text nodes) whose contents hash into a separate **cosmetic
  channel** instead of the sim-critical channel.
- Sim-critical channel mismatch ⇒ desync-grade event with drill-down.
- Cosmetic channel mismatch ⇒ *report-only* diagnostic in the desync info.

Note the payoff on the actual bug found: the cosmetic channel would have listed
`Cow156958.name: 'Cow 5' vs 'Vaca 5'` on the very first exchange — the root cause, printed
before the desync even fired. And the registry doubles as a **hit-list of vanilla code that must
never read cosmetic state into simulation decisions** — each entry is a place needing a
`CheckChangePawnKindName`-style isolation patch.

### 4. Exchange & drill-down protocol

- Every N ticks (hunt mode: ~600; always-on mode later: ~2500), each client computes the tree
  and appends the two root digests (sim, cosmetic) to its `ClientSyncOpinion`
  (serialization versioned; a few bytes per window on top of the existing RNG state lists).
- `SyncCoordinator.AddClientOpinionAndCheckDesync` compares roots along with today's checks.
- On sim-root mismatch: a new packet pair — `StateHashNodeRequest(path)` /
  `StateHashNodeResponse(children: name→digest)` — walks the tree from the root: subsystem →
  bucket → thing (log-time descent, a handful of round trips). At the leaf, both sides
  serialize the offending object to XML (normal Scribe) and send it; the desync window and
  the `Desync-XX.zip` gain a `state_diff.txt` with a field-level diff.
- The last matching exchange tick bounds the divergence onset: report
  *"states equal at tick A, diverged by tick B"* — ending the detected-at vs diverged-at gap.

### 5. Performance strategy

- **Hunt mode first** (opt-in, both clients): full-map hash every ~600 ticks. Reference point:
  full XML save of their map took ~300 ms in logs; hashing skips string/DOM/IO work, so the
  budget is tens of ms per pass, acceptable while actively hunting.
- Amortization for always-on mode (phase 2): hash 1/K of the thing buckets per tick over a
  K-tick window (each bucket still hashed at its own fixed tick, so windows are comparable),
  map components every window. Only pursue after hunt mode proves value.
- Everything runs on the main thread at a deterministic tick boundary (RimWorld state is not
  safely readable off-thread); the budget math above is what makes that viable.

### 6. Repo layout & integration points

```
Source/Client/Desyncs/StateHashing/
    HashingXmlWriter.cs      // XmlWriter -> Merkle hash stack
    StateHasher.cs           // orchestrates Scribe pass, tree assembly, channels
    StateHashRegistry.cs     // cosmetic-channel exclusion patterns
    StateHashComparer.cs     // drill-down client logic + diff rendering
Source/Common/Networking/Packet/   // StateHashNodeRequest/Response packets
```

Integrations: `ClientSyncOpinion` (roots + serialization), `SyncCoordinator` (comparison),
`SaveableDesyncInfo` (`state_diff.txt` in the zip), `MpSettings` (hunt-mode toggle, interval),
`DesyncedWindow` (show the field diff).

## Milestones

| # | Deliverable | Exit criterion |
|---|---|---|
| M1 | `HashingXmlWriter` + `StateHasher`, local only | Same-process determinism: hashing twice in a row without ticking ⇒ identical tree. **Save→load→hash round-trip ⇒ identical tree** (this test alone also detects save/load asymmetry, the historic "desync after tick -1" loop class). Tests in `Source/Tests`. |
| M2 | Roots in `ClientSyncOpinion` + comparison in `SyncCoordinator`, hunt-mode setting | Two clients in sync report equal roots for 100k+ ticks; a dev-mode injected state mutation on one side trips the root within one window. |
| M3 | Drill-down protocol + `state_diff.txt` | Injected mutation is pinpointed to the exact thing and field in the desync zip without human digging. |
| M4 | Cosmetic channel + exclusion registry | Cross-language session (host English, client Spanish) runs with **zero false sim-channel trips**, while the cosmetic report lists the name-string divergences. |
| M5 | Acceptance test on the real bug | Reproduce Desync102's scenario (aging numeric-named animal, different languages): hasher must name the pawn's `name` field *before* the RNG desync fires. Ship the `CheckChangePawnKindName` isolation patch; hasher confirms sessions stay clean after it. |

## Risks / open questions

- **Scribe re-entrancy**: hash passes must not disturb `Scribe.mode`/cross-ref state used by the
  real saver; run only at tick boundaries outside autosaves (MP already disables the vanilla
  autosaver — `DisableAutosaver` — and controls save timing, which helps).
- **DLC/mod ExposeData nondeterminism**: a mod whose `ExposeData` itself iterates an unordered
  collection will look "divergent" every pass even against itself — the M1 same-process
  determinism test catches this per-mod, and such a finding is itself a desync culprit lead
  (their save output is nondeterministic too).
- **Hash-mode Scribe fidelity**: `ExposeData` can behave differently when `Scribe.mode !=
  Saving`… it *is* Saving here, just with a different writer, so fidelity risk is low; the
  round-trip test in M1 is the guard.
- **Bandwidth** of drill-down XML dumps: bounded (one object, on desync only).
