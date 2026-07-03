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

---

## Evidence across the last 10 desyncs (Desync-93…101 + 102)

Session facts: local client (MoggCareta) plays **English**, host (Buh0) plays **Latin American
Spanish**. Local-vs-host trace diff of every archive:

| # | Stream | First divergent call (side with the extra/differing draw) |
|---|---|---|
| 93 | map 0 | **L extra**: `CheckChangePawnKindName → GeneratePawnName` on `Cow156958` |
| 94 | map 0 | **L extra**: `CheckChangePawnKindName → GeneratePawnName` on `Dromedary155654` |
| 95 | map 0 | Same pawn `Human64321`, **identical RNG state at entry**: L starts `WorkGiver_DoBill` job, H starts `WorkGiver_CleanFilth` job |
| 96 | map 0 | identical signature to 95 |
| 97 | map 0 | identical signature to 95 |
| 98 | world | **H extra**: `CheckChangePawnKindName → GeneratePawnName` on `Dromedary155654` |
| 99 | world | Ideo generation diverges: L `Precept_Building.Init → GetNextPresenceDemandID` vs H `Precept_ThingStyle.GenerateNameRaw → GrammarResolver`; trace counts L=13 675 vs H=30 426 |
| 100 | map 0 | L `Thing.PostMake → GetNextThingID` vs H `Pawn_FilthTracker.Notify_EnteredNewCell` — filth/thing state already diverged |
| 101 | world | Ideo generation diverges: L `Precept_Ritual.GenerateNameRaw → GrammarResolver` vs H `Precept.Init → GetNextPreceptID` |
| 102 | (map) | **H extra**: `CheckChangePawnKindName → GeneratePawnName` on `Cow156958` |

Two classes emerge:

**Class A — language-data-dependent simulation: 6 of 10 (93, 94, 98, 99, 101, 102).**
Four animal renames (same two animals recurring, in *both* directions — the rename ping-pongs
because every rejoin restores the host's name strings, re-arming the mismatch) and two **ideo
precept generation** divergences. The ideo cases are the severe form: `Precept.GenerateNewName`
resolves grammar with retry-until-acceptable loops over *translated* rule packs, so different
language data produces different numbers of draws **and different `UniqueIDsManager` ID
allocations** (`GetNextPreceptID`, `GetNextPresenceDemandID` visibly interleave differently).
Diverged unique-ID counters shift every subsequently created object's ID — persistent,
compounding, and **not cosmetic**.

**Class B — latent state divergence surfacing in job selection: 4 of 10 (95, 96, 97, 100).**
The RNG streams are *identical* at the divergence point; the same pawn simply picks a different
job on each side (`DoBill` vs `CleanFilth`), i.e. a non-RNG simulation input (filth present,
ingredient/stack availability, work-relevant ideo state) already differed. Trace diffing is
**structurally blind** to the origin of these — this is precisely the class only state hashing
can attribute. Candidate origins: downstream of Class A's ideo/ID divergence, stack-state
divergence (OgreStack settings live in local config files), or an as-yet-unknown source.

**On "we switched the client to Spanish and it desynced more":** consistent with the model.
English is compiled into the game build — byte-identical on both machines by construction.
LatAm Spanish is a community-maintained pack that updates independently; two installs easily
carry different versions (the client's older logs showed *"35 errors"* in that pack), so "same
language" still means different label data — while the save's existing English-generated names
now mismatch labels on *both* sides, arming more renames. The controlled experiment is **both
players on English**: Class A incidents should disappear; whatever remains is pure Class B and
prime hasher input.

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

Important boundary drawn by the Desync-99/101 evidence: language divergence is only cosmetic
while it stays in strings. The ideo-generation cases show it leaking into **unique-ID
allocation**, which is sim-critical state — the hasher must keep `UniqueIDsManager` counters in
the sim channel (they are cheap, high-signal leaves: a counter mismatch immediately flags "object
creation diverged, and everything created afterwards is suspect").

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
| M5 | Acceptance tests on the real bugs | (a) Class A: aging numeric-named animal, different languages — hasher names the pawn's `name` field *before* the RNG desync fires; (b) Class A severe: mid-game ideo generation under different languages — hasher flags `UniqueIDsManager` counters within one window; (c) Class B: reproduce the players' `DoBill` vs `CleanFilth` divergence — hasher names the diverged filth/stack/ideo state that trace diffing cannot attribute. Ship the `CheckChangePawnKindName` isolation patch; hasher confirms sessions stay clean after it. |

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
