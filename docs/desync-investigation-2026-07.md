# Desync investigation: recurring "Wrong random state on map 0" + rejoin desync loop

Report based on a non-host client log (`local_logs.txt`, player `MoggCareta`) and the host's
random-state traces (`host_traces.txt`, host faction `Buh0's faction`).

## Environment

From the uploaded client log:

| Item | Value |
|---|---|
| RimWorld | 1.4.3542 rev627 |
| Multiplayer mod | **0.7.0** (first 1.4 release; assembly 0.3.0, API 0.3) |
| Connection | Steam relay |
| Mods listed in the log | Harmony 2.2.2, Core, Multiplayer — no DLC, no other mods |

Reported by the players as their actual mod list (both sides, same order):
Prepatcher, Harmony, Core + DLC, OgreStack, Haul to Stack, Multiplayer, Pick Up And Haul.

> **Unresolved discrepancy:** the uploaded log's loaded-mods section shows only
> Harmony/Core/Multiplayer and no DLC, yet the log contains the desync events. Either this log
> comes from a stripped-down test session (in which case: desyncs reproduce even in near-vanilla,
> which is major evidence on its own), or from a different session than the one being described.
> Resolving this is step 0 of the playbook below — it decides whether mod bisection is even a
> priority.

## What "Wrong random state on map 0" actually means

It is not a placeholder and not (only) a generic message. Mechanically
(`ClientSyncOpinion.CheckForDesync`, `SyncCoordinator`):

- Every `Rand` call made during simulation pushes the post-call RNG state into a per-context
  list: one list per map (keyed by real map id — `0` is the colony map), one for the world, one
  for command execution.
- Opinions (windows of a few ticks) are exchanged; the lists are compared **element by
  element** (`SequenceEqual`). The message names the first *category* that mismatched, so "map 0"
  means: the sequence of RNG states produced by map-0 simulation differed. All granularity beyond
  that lives in the **trace hashes**: each RNG call also records a hashed stack trace, and
  `FindTraceHashesDiffTick` computes `diffAt` — the index of the first call where the two sides
  disagree.
- The trace printed as "Trace of first desynced map random state" is what the *host* executed at
  index `diffAt`. What the *client* executed at that same index is in `local_traces.txt` — and
  that pair, host[diffAt] vs local[diffAt], is the closest thing to a smoking gun this system
  produces.

### Why the hive showing up does not (yet) implicate the hive

The hive + 2 megaspiders existed since map start and tick constantly. `CompSpawnerFilth` rolls
MTB randomness on a short interval, making the hive one of the highest-frequency RNG consumers on
the map. When the two RNG streams shift relative to each other (one side made an extra or missing
call anywhere), the first *observed* mismatch tends to land on whatever calls RNG most often —
i.e. the hive is likely the **witness**, not the culprit. The culprit is whatever call appears on
one side but not the other at/just before `diffAt`, which is exactly why the local traces are
required.

## Observed pattern (recap)

- 12× `Wrong random state on map 0`, 1× `Wrong random state for the world`, 1× `Trace hashes
  don't match`, spread over two sessions, roughly every 6k–100k ticks.
- The final two desyncs happened **"after tick -1"**: the client desynced immediately after
  rejoining, before a single comparison window validated — a desync loop. The state the host
  serializes for rejoin already disagrees with the host's own live simulation, or the client's
  load of it diverges instantly.

---

# Pinpointing playbook

Ordered so that each step either finds the cause or eliminates a whole class of causes. The
guiding principle: the current detector compares **RNG streams**, which observe the *symptom* —
divergence is only detected when the diverged state finally consumes randomness differently,
possibly long after the actual split. The later steps therefore move from "where did the RNG
streams split" to "where did the *game state* split", which is the innovative part.

## Step 0 — Resolve the evidence discrepancy (minutes)

Confirm which session the uploaded log belongs to. Grab the newest `Desync-XX.zip` files from the
`MpDesyncs` folder (both machines ideally). Each zip already contains:

- `desync_info` — versions, async time, arbiter state, machine info for **both** interpretation
  and config comparison
- `local_traces.txt` **and** `host_traces.txt` — both sides of the divergence
- `local_logs.txt`, jitted method lists, and (if enabled) `replay.rwmts`

If the mods list in those logs shows near-vanilla: desyncs reproduce without the hauling mods →
skip Step 3's bisection and go straight to Steps 4–6.

## Step 1 — Diff both sides' traces at `diffAt` (hours, no code)

Line up `local_traces.txt` and `host_traces.txt` by trace index and compare at and around the
divergence. Outcomes:

- **Extra call on one side** (e.g. client shows a `WorkGiver_HaulToInventory`/hauling frame the
  host doesn't have): direct culprit identification.
- **Same call, different RNG state already at entry**: the split happened *before* the window;
  proceed to state-hashing (Step 5), because trace radius will never show the origin.
- **Same calls, different thing ids**: object identity divergence (things created/destroyed in
  different order earlier) — also a Step 5 case.

Deliverable for the repo: a small `trace-diff` script/tool (parse both files, align indices,
print first divergence with N frames of context from BOTH sides). Every future report becomes
actionable in minutes instead of guesswork from one side.

## Step 2 — Split the hypothesis space with same-machine runs (an evening, no code)

The single most information-dense experiment available without touching code. Run **host + a
second game instance joining from the same machine** (or host + arbiter — the arbiter is exactly
this, built in: a headless instance simulating the same commands on the host's machine; visible
in `desync_info` as "Arbiter Connected And Playing").

- **Desyncs still occur same-machine** → hardware/OS differences are excluded; the cause is
  genuine nondeterminism in code (mods, vanilla edge case, save/load asymmetry). Focus Steps 3–6.
- **Same-machine never desyncs, cross-machine does** → focus on machine-environment divergence:
  - **FP round-mode corruption**: some audio drivers/overlays flip the x87/SSE rounding mode,
    silently changing float results. This was a real, historically confirmed desync cause — new
    MP versions explicitly compare `RoundMode` between clients (see
    `ClientSyncOpinion.CheckForDesync`'s "FP round mode doesn't match"); 0.7 predates that check,
    so this cause is *invisible* on their version.
  - **OS locale/culture**: the client machine runs a Spanish (LatAm) locale. Vanilla parses defs
    culture-invariantly, but mods frequently `float.Parse` their settings without
    `InvariantCulture` — identical settings *files* then yield different in-memory values
    (`1.5` vs `15`) on machines with different decimal separators. Test: set both Windows
    regions/formats identical and retest; audit the mod list for culture-sensitive parsing.
  - CPU-specific float paths (denormals, FMA differences) — rarer; the arbiter/local test brackets
    it.

## Step 3 — Controlled mod bisection with accelerated repro (only if Step 0 implicates mods)

The reported mod list contains three hauling/stacking mods — **Pick Up And Haul, Haul to Stack,
OgreStack** — none of which are MP-aware natively. PUAH in particular is a historically notorious
desync source (inventory-hauling decisions from per-instance caches). "Same mods on both sides"
does **not** imply determinism: a mod can be internally nondeterministic on identical setups
(unordered `Dictionary`/`HashSet` iteration, static `System.Random`, camera/UI-dependent caches,
time-based logic).

- First check: is **Multiplayer Compatibility** ("MP Compat") installed? It carries community
  sync patches for many popular mods including hauling ones. If not: install on both sides,
  retest before bisecting anything.
- Bisect with a *time-compressed* protocol instead of hours of natural play: dev mode, max speed,
  spawn large hauling workloads (many stacks + storage churn) to hammer the suspect code paths;
  give each configuration a fixed tick budget (e.g. 300k ticks). Halve the mod set on each
  desync-free budget.
- Prepatcher note: it rewrites `Assembly-CSharp` itself. Verify both sides produce identical
  patched assemblies (compare hashes of the patched output) — a one-sided patch difference is a
  silent determinism killer that no mod-list comparison will catch.

## Step 4 — Turn the desync zip's replay into a deterministic repro-in-a-box (code: small)

`SaveableDesyncInfo` can embed `replay.rwmts` (enable `includeReplayInDesync`): the initial save
plus the full command stream. A replay *must* produce identical simulation everywhere. So:

- Run the same desync replay on both machines to completion, hashing state every N ticks.
- If the two machines' replays diverge → machine-environment nondeterminism captured in a
  shareable, re-runnable file: bisect *in the replay* (binary-search the tick of first hash
  divergence) rather than in live play. No second player needed, infinitely repeatable.
- If replays agree everywhere → the live desync came from something outside the recorded
  command+save state (unsynced input, UI-side mutation, network-order effects), which is itself a
  huge clue.

This makes desyncs *offline-debuggable* — the biggest force multiplier of the plan.

## Step 5 — Hierarchical map-state hashing: find the state split, not the RNG symptom (code: the innovative core)

RNG-stream comparison can only see divergence when RNG is consumed. A quietly diverged stack
count (very relevant for stack-modifying mods) can sit latent for thousands of ticks. Proposal —
a debug-mode **Merkle-style state hasher**:

1. Reuse `ExposeData`: implement a `ScribeSaver` variant that streams into a rolling hash instead
   of XML ("hash mode Scribe"). Every savable object already enumerates its persistent state
   deterministically — zero per-class work.
2. Every N ticks compute hashes per subsystem: things (bucketed: pawns / items / buildings /
   filth), reservations, jobs, lords, hediffs, zones, designations, plus the RNG state; combine
   into per-map roots and a game root.
3. Exchange only the root per window (a few bytes — cheaper than today's RNG state lists). On
   mismatch, walk down the tree over the network: root → subsystem → bucket → individual thing →
   full XML dump of that one object from both sides, field-diffed in the desync window.

End result: desync reports change from *"Wrong random state on map 0"* to
*"`Muffalo47101.inventory` stack count 75 vs 74"* — the actual first diverged state, named.
Follow-up features enabled by it:

- **Earliest-divergence bisection in time**: keep a ring buffer of periodic root hashes; on
  desync, report the last tick roots matched — the true divergence onset, not the detection tick.
- **Save/load asymmetry self-test** (targets the "after tick -1" rejoin loop directly): a dev
  action on the host that saves the game, reloads it in memory (the rejoin path), and compares
  state hash before vs after. If they differ, the rejoin desync loop is explained *on one
  machine, in one click* — no second player, no network.

## Step 6 — Full-fidelity RNG ledger (code: medium, complements Step 5)

A "hunt mode" toggle that records **every** RNG call (tick, caller hash, `ThingContext` id/def,
post-call state) into a bounded compressed ring buffer on both sides — the existing
`DeferredStackTracing`/`StackTraceLogItemRaw` infrastructure already captures per-call context;
this extends retention from the current ±`desyncTracesRadius` (default 40) window to hundreds of
thousands of calls. On desync both sides dump the buffer; a machine diff finds the first
divergent call even when it precedes the detection window by many ticks. Ship the dumps inside
the existing `Desync-XX.zip`.

---

# Fix phases (updated)

1. **Alignment & quick wins:** update MP on both sides to the latest release for their RimWorld
   version (0.7.0 predates years of desync fixes *and* the FP round-mode check); install MP
   Compat; verify identical DLC sets and Prepatcher output; don't dismiss join-data warnings.
2. **Run the playbook** (Steps 0–4 need little or no code) until a cause class is isolated.
3. **Implement tooling** (trace-diff tool → replay divergence runner → state hasher → RNG ledger)
   in this repo behind debug settings; they permanently upgrade every future desync report.
4. **Fix the identified cause(s)** — sync patches for the offending mod paths (contribute to MP
   Compat where the culprit is a third-party mod), or determinism/serialization fixes here if
   vanilla-or-MP code is at fault.
5. **Break the rejoin loop** regardless of root cause: when a desync fires with
   `lastValidTick == -1` immediately after a rejoin, retrying the client-only rejoin from the same
   host state cannot succeed — detect it and escalate to a full host save+reload so all parties
   resynchronize from identical serialized state.
