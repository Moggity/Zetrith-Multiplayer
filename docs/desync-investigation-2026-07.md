# Desync investigation: recurring "Wrong random state on map 0" + rejoin desync loop

Report based on a non-host client log (`local_logs.txt`, player `MoggCareta`) and the host's
random-state traces (`host_traces.txt`, host faction `Buh0's faction`).

## Environment (from the client log)

| Item | Value |
|---|---|
| RimWorld | 1.4.3542 rev627 |
| Multiplayer mod | **0.7.0** (first 1.4 release; assembly 0.3.0, API 0.3) |
| Connection | Steam relay |
| Mods on client | Harmony 2.2.2, Core, Multiplayer — nothing else, and **no DLC entries** in the loaded-mods list |

## Observed desync pattern

Fourteen desyncs across two play sessions:

- 12× `Wrong random state on map 0` (ticks 94380, 204735, 210997, 226297, 282588, 298062,
  310056, 327095, 336251, 345481, 346185, 346934)
- 1× `Wrong random state for the world` (tick 262564)
- 1× `Trace hashes don't match` (tick 107114)

Every desync auto-triggers "requesting rejoin" → rejoin succeeds → play continues for a while
→ desync again. The final two desyncs happen **"after tick -1"** (`SyncCoordinator.lastValidTick`
still at its initial `-1`), i.e. the client desynced *immediately after rejoining*, before a single
sync window was validated. That is a **desync loop**: the state the client receives on rejoin
already disagrees with the host's live simulation. The session ended with a Steam disconnect.

## What the host traces show

The host trace file corresponds to the final desync (ticks 346894–346896). The "first desynced
map random state" — the call where the two rand-state streams first differ — is:

```
Rand.MTBEventOccurs
  <- RimWorld.CompSpawnerFilth.TickInterval
  <- CompSpawnerFilth.CompTick
  <- Hive.Tick            ('Hive47125')
```

Context traces around it are dominated by an **active infestation** (`Hive47125`, `Megaspider47137/8`)
plus normal ambient simulation on map 0:

- `HediffGiver_RandomAgeCurved.OnIntervalPassed` for wild animals (Boomalope, Hare, Muffalo,
  Squirrel, Warg, Tortoise)
- `Plant.TickLong` dealing damage to `Plant_TallGrass` (`DamageInfo..ctor` / `DamageWorker.Apply`)
- `Pawn_FilthTracker.Notify_EnteredNewCell` while pawns walk
- Insect/pawn job selection: `ThinkNode_PrioritySorter`, `JobGiver_Wander`, and
  `JobGiver_GetFood.TryGiveJob -> FoodUtility.TryFindBestFoodSourceFor -> PawnUtility.IsTeetotaler
  -> PreceptComp_UnwillingToDo_Chance.MemberWillingToDo` (an ideo-precept chance roll inside
  food search)

These are all *host-side* calls; they tell us where the streams diverged, not which side made the
extra/missing call. The client-side counterpart traces are needed for that (see Phase 2).

## Likely causes, in order of probability

### 1. Severely outdated Multiplayer version (0.7.0)

0.7.0 was the *first* RimWorld 1.4 build (November 2022). A long series of desync fixes shipped
after it (through 0.8.x/0.9.x for 1.4, and this repo is now at 0.11.5), many of them exactly this
class of bug — one-sided `Rand` consumption in vanilla systems during ticking (e.g. #893 quest
site tile divergence, #857 Unnatural Corpse, plus many 1.4-era fixes). Running both sides on the
latest release that matches their RimWorld version is the highest-value, lowest-cost fix and a
precondition for any further debugging — there is no point chasing bugs already fixed upstream.

### 2. Host/client configuration mismatch

The client runs pure vanilla with no DLC rows in the loaded-mods list. The traces exercise ideo
precepts, which also exist in no-DLC "classic" mode, so this is not conclusive — but a
host/client mismatch in **DLC set, RimWorld build, mod versions, or Multiplayer version** produces
exactly this recurring-desync signature. MP's join-data check warns about mismatches but players
can click through it. This must be ruled out explicitly (screenshot both mod lists + DLC list).

### 3. Save/load asymmetry causing the rejoin desync loop

`Desynced after tick -1` twice in a row means: host serialized its game, client loaded it, and the
very first comparison window already mismatched. Either the host's live state is not equal to its
own saved+reloaded state (unsaved fields, caches, tick-order effects), or the client's load path
initializes something differently. Since the divergence point is the hive's `CompSpawnerFilth` MTB
roll and the map has an active infestation, state around the infestation (hive comps, insect
lord/AI state, filth) is the prime suspect for not round-tripping through save/load
deterministically.

### 4. One `Trace hashes don't match`

Random states matched but trace hashes differed — same rand stream, different recorded call
sites/context. Usually an early symptom of the same underlying divergence rather than a separate
bug; deprioritize until 1–3 are addressed.

## Fix plan

### Phase 1 — Version & config alignment (users, no code)

1. Both players update Multiplayer to the newest release compatible with their RimWorld version
   (or move the save to current RimWorld + MP 0.11.x). Versions must match **exactly** on both sides.
2. Verify identical RimWorld build, DLC set, mod list and mod versions; do not ignore the
   join-data mismatch warning.
3. Retest. If desyncs persist, continue below.

### Phase 2 — Collect complete evidence

1. Get the client's desync archives (`MpDesyncs` folder). Each zip contains *both* local and remote
   traces plus `SaveableDesyncInfo` — the host-only trace file supplied so far is half the picture.
2. Diff local vs. remote traces at `diffAt` (the index reported by
   `SyncCoordinator.FindTraceHashesDiffTick`) to identify which side made the extra/missing `Rand`
   call and from where.
3. Optional tooling: a small trace-diff utility (input: two desync trace files, output: first
   divergent frame with context) would make every future report actionable in minutes.

### Phase 3 — Reproduce and fix (dev work in this repo)

1. **Save/load determinism harness** (targets the tick `-1` loop): automated test that loads the
   same save into two instances (or host + arbiter), runs N ticks with no commands, and compares
   rand-state streams — the exact comparison `ClientSyncOpinion.CheckForDesync` does. A companion
   test saves, reloads, and re-compares to catch host-live vs. host-saved asymmetry. Fits the
   existing `Source/Tests` project alongside `StandaloneMapStreamingTest`.
2. **Infestation scenario test**: run the harness on a map with an active hive + insect lord.
   The final desync's divergence point (`Hive.Tick -> CompSpawnerFilth`) and the desync loop both
   point here. If it reproduces, bisect the hive/lord/filth state that fails to round-trip.
3. **Audit UI-reachable rand calls in food/job search**: `FoodUtility.TryFindBestFoodSourceFor ->
   IsTeetotaler -> PreceptComp_UnwillingToDo_Chance.MemberWillingToDo` rolls `Rand` and is reachable
   from non-simulation paths (float menus, gizmo validation). Verify all such entry points are
   wrapped in `Rand.PushState/PopState` in MP; an unwrapped path silently shifts one client's rand
   stream — exactly the recurring `Wrong random state on map 0` signature.
4. **Break the desync loop**: when a client desyncs with `lastValidTick == -1` right after a rejoin
   (i.e. the fresh state itself is bad), a second rejoin from the same host state cannot succeed.
   Detect this in `SyncCoordinator`/`DesyncedWindow` and escalate: force the *host* to save and
   reload too (resynchronizing everyone from identical serialized state) instead of retrying the
   client-only rejoin.

### Phase 4 — Diagnostics hardening

- Surface `diffAt` context (local *and* remote frames around the divergence) directly in the
  desynced-window info so users attach one file with both sides.
- Keep extending `DeferredStackTracing` context (thing id/def already recorded via
  `StackTraceLogItemRaw`) with the ticking phase (TickList type, map component vs. thing tick) to
  disambiguate ordering bugs from extra-call bugs.
