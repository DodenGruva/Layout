# Session 39 — v0.4.44 → v0.4.49

**Branch:** `beta`. **DataVersion 13. Protocol 24 → 26.** **9 source files** (none added).

> **`TODO` A10.2 is closed** — the performance review of the non-render code, the last item of the review
> backlog and the one that had stood since Session 28 with the honest note "nobody has looked". Six defects
> fixed, five costs measured and deliberately left alone, one item too large to belong here and promoted to
> **A17**, and **one fix shipped and withdrawn** — see §3, which is the most useful thing in this record.

---

## 1. The headline: every edit re-saved every guide in the world

`GuideManager.Persist()` serialises the **whole registry** — every guide in the world, not the one that
changed — and it was called at the end of essentially every mutation. Twenty-four call sites, including
`UpdateControlPoints`, which is what a reshape drag goes through about **ten times a second**.

**Measured**, with a benchmark against the mod's own types, the real `JsonSerializerSettings` and the real
UTF8 encode, and **validated against a real save file** on the human's machine (an actual 4-point guide
record is 1,940 bytes; the synthetic one used for the table was 2,097, so the figures are mildly
pessimistic):

| Guides in world | Blob | One save | Per second of dragging | Share of server main thread |
|---|---|---|---|---|
| 50 | 0.2 MB | 0.8 ms | 8 ms | 1% |
| 250 | 1.2 MB | 4.2 ms | 42 ms | 4% |
| 1,000 | 4.7 MB | 18.4 ms | 184 ms | **18%** |
| 3,000 | 14.2 MB | 55.2 ms | 552 ms | **55%** |

About **4 ms per megabyte**, linear. A server tick is 20 ms — so at a thousand guides **one drag update ate
a whole tick**, and at three thousand it overran by nearly three, for *one* player dragging.

⚠️ **The shape of this defect is what matters more than the numbers.** The cost tracked how much had ever
been built, not what was being edited. It is invisible on the world it was written on and crippling on a
world two months old — the same shape as the targeting loop Session 38 found, and it will not be the last.

**The fix (v0.4.45).** Mutations call `MarkDirty()`, a bool. `Persist()` became the flush and writes only
when dirty. Wired to the world save (already present), a periodic tick, and shutdown. Measured on a
10-second sustained drag: **20× less work** at every world size.

`BatchPersist()` was **deleted**, not kept — it existed only to stop the client-only push writing once per
guide (A14.8), and `MarkDirty` subsumes it on every path without a scope having to say so.

⚠️ **Deferring a write creates a new obligation: every path that DROPS the owner must flush first.**
`LocalGuideAuthority` is discarded by plain assignment (`_local = null`) in `EndWorldSession` and
`RemoveLocalOverlay`, with no disposal — which was safe only while every mutation wrote immediately. Both
now flush, as does client shutdown. This was found by looking for it, not by it going wrong.

`LoadPayload` also had to `MarkDirty()`: a load is not a no-op — records migrate by deserialization default,
`DataVersion` is re-stamped, inactive lock markers are dropped, out-of-world guides are skipped, and a
backup recovery replaces a quarantined primary. All of that used to reach disk by accident of the
unconditional write.

**Then the human changed the design for the better (v0.4.46):** the server's periodic timer was removed
entirely. Guide data is now written exactly when the world writes — *"Whatever frequency the world uses
should be fine for us."* If a crash costs the world five minutes of blocks, costing it the same five minutes
of guides is correct and consistent; a separate timer would only make Layout's data more durable than the
world it describes, at a cost paid forever. The client keeps a 60-second timer because **there is no
client-side world-save event to hook**, and in client-only mode against a remote server there is no local
save to borrow.

## 2. Arches rebuilt their curve thousands of times per count

`ArchShape.SampleBodyCells`'s filled path draws up to **8,193** "rules" from the curve to the chord, and
each one called `GetPointAt`, which calls `BuildSpline()` — two Lists plus a `CatmullRomSpline` that
deep-copies its points. The spline is constant for the whole loop.

⚠️ **A shape accessor may rebuild its geometry on every call.** `GetPointAt` looks like a cheap lookup and
is not. Calling one in a loop is the trap; hoisting is the fix.

Measured on a realistic 4-block filled arch (the size that streams during a drag): **1.55 ms → 0.45 ms,
3,195 KB → 485 KB per count.** Shipped v0.4.47.

**Verified by comparing builds, not by argument.** The pre-fix `Layout.dll` was extracted from the shipped
`Layout0.4.46.zip` and run side by side with the new one over **576 arch configurations** (twelve spans,
four scales, filled and hollow, three foot heights, both constraints) and then **720 configurations across
all fifteen shapes**. Every voxel count identical. `RulePointAt` deliberately still takes `t` as a **float**
and clamps exactly as `GetPointAt` does — evaluating the double directly would shift sampled positions in
the last bits, which can move a voxel across a cell boundary, and this count feeds the cap check that
`IGuideShape.GetVoxelCount` requires to agree exactly with what renders.

## 3. The claim pre-filter — shipped v0.4.48, withdrawn v0.4.49

**The most instructive thing in this session.** Read this before proposing anything similar.

**The idea, and it was the human's.** The claim check voxelises the guide and asks the claim API about every
world block it touches, on every drag update. Test a cheap bounding box against the world's land claims
first; if nothing intersects, the exact check would say "permit" anyway, so skip it.

**Session 23 had already rejected bounding boxes** — *"bounding-box validation would wrongly reject hollow
guides that merely surround one"* — but that rejected the box as the **answer**. As a **negative filter**
that can only ever skip a *permit*, the dome-around-a-chest case still falls through to the exact check,
because that box does intersect a claim. The distinction is sound and the reasoning survives.

**The geometry was verified to a standard I was happy with.** 5,400 configurations — every shape, six sizes,
five scales, filled and hollow, three orientations, both projections — every voxel inside the box. The first
run passed with **zero margin** (a flattened guide's blocks landed exactly on the rectangle's edge, because
the projection-plane union pins the edge there); the padding was moved to apply *after* the union and every
case then had four blocks to spare. Measured **1,800× cheaper** than the work it replaced, and still ahead
at 5,000 claims.

⚠️ **AND IT WAS STILL WRONG, because the premise was never checked.** The filter treated
`ILandClaimAPI.All` as the authority behind `TestAccess`. It is not. `TestAccess` answers with **seven**
responses and only one comes from that list:

`Granted` · **`LandClaimed`** · `NoPrivilege` · `InSpectatorMode` · `InGuestMode` · `PlayerDead` ·
**`DeniedByMod`**

So *"no land claim overlaps this box"* never meant *"TestAccess would grant every block in it"*. A player
without the build privilege, a dead player, or an area protected by **another mod** would all have been
waved through. **The hard-looking half was verified exhaustively and the easy-looking half was assumed.**

Withdrawn in full. `TryConservativeFootprintRect` is **retained but wired to nothing**, with the withdrawal
note directly above it, because the geometry is proven and would be the starting point of any future
attempt — and because being right about the geometry was never the hard part.

**Found the honest way:** the human tested on a trader's plot and reported placing guides on protected land.

## 4. The trader's plot — a second, separate answer

That report is almost certainly **not** caused by §3, and the distinction matters.

**Layout deliberately skips the claim check entirely for `controlserver` holders**, consistently in four
places (`WithPlayerVoxelCap`, `OnDraftStart`, and both immense paths). **In singleplayer the host player
holds `controlserver`**, and game mode is not privilege — switching to survival changes what *vanilla*
allows, not what Layout checks.

⚠️ **Claim protection therefore cannot be tested from a singleplayer world.** The one person most likely to
test the feature is the one person exempt from it. Verification needs a non-admin account on a dedicated
server, which the human is doing. **Do not conclude from a singleplayer test that claim gating is broken.**

## 5. The smaller fixes

- **v0.4.44 — the HUD names which cap refused you.** `VoxelCapWarningPacket` carried a count and a limit and
  never said *which* of the four caps fired, so the row could only say `REFUSED — over cap` while the chat
  line named it. A `VoxelCapKind` is appended and the server's existing cascade — the one that already
  words the chat message — supplies it, so the two cannot disagree. Protocol 25.
- **v0.4.45 — the Players dialog was quadratic.** `BuildRoster` built one row per player and each row called
  two helpers that each walked the whole registry: (players × guides × 2). One pass now tallies everyone.
- **v0.4.45 — dead code in the drag throttle.** Two slower send intervals for medium guides sat below an
  early return that already sends nothing at all past 8,000 voxels, so both were unreachable and the
  interval was always `MoveSendIntervalMs`. Removed, with a note that graduated throttling would have to go
  *above* the return.
- **v0.4.46 — guide-count caps never reached the HUD.** Found by the human in play. The count caps
  (`maxGuidesPerPlayer`, `maxGuidesWorldWide`) are not voxel caps and took a different path: chat error, no
  packet, HUD row untouched. Three call sites now go through `SendGuideCountRefusal`. Protocol 26.
- **v0.4.45 — Title Case on the refusal wording**, at the human's request, folded into the next build rather
  than burning a version on its own.

## 6. Found, measured, and deliberately NOT fixed

Recorded so nobody re-derives them. **Each was left because the fix costs more risk than the gain is worth**,
under the standing correctness-over-performance rule.

1. **Flat filled shapes count by materialising their voxel list** (~200–500 KB per count at drag-relevant
   sizes). Fixing means rewriting exact-count geometry that must agree with the renderer to the voxel.
2. **The geometry is walked twice per drag update** — once to count for caps, once to build the claim
   footprint, at almost identical cost (0.4–1.4 ms total). Merging them means restructuring the order in
   which caps and land-claims are enforced. This is what §3 tried to attack from the safe side.
3. **3D volumes cost 5–15 ms per count at large sizes** — but those sizes are past the 8,000-voxel drag
   threshold, so it is paid per *action*, not ten times a second.
4. **`CountForCaps` calls `VoxelCountBy`, which scans the whole registry**, on every drag update. ~1 ms/s
   even at 3,000 guides. A second index would have to stay consistent through create/delete/restore.
5. **`BlockOccupancy`'s cache has no size limit.** Bounded in practice by blocks near guides.

## 7. Verified clean

So the review's scope is on the record: the targeting broad-phase and curve cache, the whole settings GUI
(`DeferRecompose` and fingerprint guards throughout), the icon system (idempotent registration, drawing at
composition not per frame), the Players dialog's client side (event-driven, no polling), held-item rendering
(meshes cached and disposed), the chalk effects (event-driven), and **all eight 3D volume shapes, which
count without allocating anything at all**.

## 8. Corrections made to my own claims, mid-session

Kept because the pattern is the point.

1. **The client-side disk cost was overstated.** Called "worse in kind" and "hammers the disk"; measured at
   1.3–2.9 ms per save and nearly flat with file size. Real, modest, downgraded to minor.
2. **A Large-Object-Heap claim was not supported by the measurement.** `GC.GetAllocatedBytesForCurrentThread`
   is total allocation across many objects, not the size of one — and it is individual large objects that
   land on that heap.
3. **"1,000 guides is a well-used server" was an assertion with nothing behind it.** The cost is a known
   function of world size; where a real server sits on that curve is the human's knowledge, not mine.
4. **The first arch measurement used hand-built control points and was degenerate** — hollow arches
   reporting *zero* voxels, meaning a fallback path was being measured and called the main one. Caught
   because the numbers were absurd (21 MB to produce 513 voxels), then rebuilt to construct guides through
   `ShapeFactory.Create`, the real placement path.

## 9. After the doc pass: A17 was proposed, agreed, and dismissed

**No code shipped for this — it is design work, and it changed the next session's plan entirely.**

The human asked a question that should have been asked before A17 was written: *"is there any merit to
saving guide information OFF the world save file — and is there any reason TO save to it?"* Answering it
meant reading the save layer instead of its interface.

**What was found** (`GOTCHAS` **G42**): the savegame is SQLite with **all mod data in one row of one table**,
holding Layout's keys uncompressed inside a single blob; and `ISaveGame` offers **no key enumeration and no
delete**. So per-guide records need an index we maintain *and* leak a dead key per dispelled guide forever,
and bucketing saves nothing because the whole row is rewritten regardless. **A17 was never viable.**

⚠️ **And its premise had already expired.** A17 was written on *"the cost of an edit grows with how much has
ever been built"* — which stopped being true in v0.4.45, three hours earlier in the same session, when
mutations stopped saving at all. The plan was measuring a problem that the session had already fixed.

**What replaced it.** The remaining cost is the world-save serialisation running **on the main thread** —
42 ms at 3,000 guides, 113 ms at 8,000, against a 20 ms tick. It is there by accident, not design. Measured:
a deep copy of the whole registry costs **0.52 ms at 3,000 guides against 42 ms to serialise**, ~80× cheaper,
so the main thread can snapshot and a worker can do the text conversion — **~99% of the cost leaves the
tick**, with no change to the storage format, no migration, and no loss of rollback consistency.
⚠️ The snapshot is **mandatory**: `GuideData.ControlPoints` is the same list a live shape mutates.
→ `TODO` **A18**, `dev/plans/PLAN_BACKGROUND_SAVE.md`.

**The human's correction to the framing**, recorded because it sharpened the analysis: on a multiplayer
server **only the host holds the world save**. Public guides are persisted server-side only — clients hold a
memory mirror and never write them; only private F4 guides are persisted per player. So save *size* is one
machine's concern, while a main-thread *stall* is felt by everyone as lag.

⚠️ **Twice in one session, the half that looked obvious was the half nobody checked** — the claim pre-filter
(§3) verified 5,400 geometry cases and never read what `TestAccess` decides on; A17 verified the cost curve
against a real save file and never read what the storage layer supports. Both were plausible, both were
agreed, and both were wrong for the same reason.

---

## Delivered

- **v0.4.44** — the HUD cap row names which of the four caps refused the edit (protocol 25).
- **v0.4.45** — saves deferred and coalesced instead of running per mutation (20× less work on a sustained
  drag); the Players dialog roster built in one pass instead of per-player scans; unreachable drag-throttle
  tiers removed; refusal wording in Title Case.
- **v0.4.46** — guide-count caps now reach the HUD (protocol 26); the server's periodic save timer removed
  in favour of the world's own save; the client's private-guide flush relaxed to 60 s.
- **v0.4.47** — `ArchShape`'s filled-count path builds its spline once instead of up to 8,193 times.
- **v0.4.48** — bounding-box pre-filter in front of the claim check. **Withdrawn in v0.4.49.**
- **v0.4.49** — v0.4.48 reverted; claim validation restored to the exact footprint check.

## Decisions

- **Mutations mark dirty; lifecycle points write.** The alternative — keeping `Persist()` immediate and
  adding batching at call sites — was rejected: batching cannot span packets, and a drag *is* many packets.
- **The server runs no periodic save timer** (human-set). Guide data is worth what the world is worth.
- **The client does keep one, at 60 s**, because no client-side world-save event exists to hook.
- **`BatchPersist()` deleted rather than retained as a no-op**, so there is no dead scope to mislead.
- **`RulePointAt` keeps `GetPointAt`'s float parameter and clamp**, choosing bit-identical behaviour over
  tidier code, because the count it feeds must match what renders.
- ⚠️ **REVERSED: the bounding-box claim pre-filter** (§3) — `GOTCHAS` **R12**. Shipped v0.4.48 on verified
  geometry and an unverified premise; withdrawn v0.4.49.
- ⚠️ **DISMISSED: `TODO` A17, per-guide persistence** (§9) — proposed and agreed on the assumption that the
  save layer's interface implied its implementation. It does not: one blob, no enumeration, no delete
  (`GOTCHAS` **G42**). Replaced by **A18**, moving the serialisation off the main thread — a smaller change
  that needs no migration and keeps the world save's rollback consistency.
- **Admin bypass of claim checks left as-is** (§4) — deliberate, consistent with the mod's other admin
  overrides, and flagged for review rather than changed.
- **Five measured costs deliberately not fixed** (§6).

## Traps

⚠️ **A save that re-serialises everything must never run per mutation.** Its cost tracks world size, not
edit size, so it is free on the machine it was written on. → `GOTCHAS` **G39**.

⚠️ **Deferring writes creates a flush obligation on every path that drops the owner.** An object discarded
by plain assignment has no destructor to catch it. → `GOTCHAS` **G39**.

⚠️ **A cheap filter in front of a permission check must be certain about the WHOLE check.** `TestAccess` has
seven denial reasons and only one is land claims. → `GOTCHAS` **G40**, `GOTCHAS` **R12**.

⚠️ **A shape accessor may rebuild its geometry on every call.** `ArchShape.GetPointAt` constructs a spline;
it was being called up to 8,193 times per count. → `GOTCHAS` **G41**.

⚠️ **A safety bound that passes with zero margin has not really passed.** The containment test's first run
put voxels exactly on the rectangle's edge, which only worked if the claim API treats that edge as inside —
something this code has no business assuming. Slack goes on last, after every union. (§3)

⚠️ **Claim protection cannot be tested from a singleplayer world** — the host holds `controlserver` and is
exempt. Game mode is not privilege. (§4)

⚠️ **A synthetic test object built by hand is not the thing the game builds.** Hand-assembled control points
produced degenerate shapes that measured a fallback path. Construct through `ShapeFactory.Create`. (§8.4)

⚠️ **The world save is ONE blob and `StoreData` can neither enumerate nor delete.** An interface that looks
like a key-value store is not one underneath. → `GOTCHAS` **G42**. (§9)

## Flagged and unverified

**Judgement calls awaiting review** — indexed in `dev/TODO.md`:

1. **The client's 60-second private-guide flush interval.** Claude's number. The server side is the world's
   own cadence, human-set; this one has no equivalent to borrow.
2. **`VoxelCapKind.GuideCount` rides `VoxelCapWarningPacket`** even though its two numbers are guide counts,
   not voxels. Tolerable only because the HUD ignores them and uses the kind alone.
3. **The five deliberate non-fixes in §6** — each is a judgement about risk versus gain, not a fact.
4. **Admin bypass of claim validation** (§4) — long-standing, surfaced here, unchanged.

**Claims not tested** — these go to `STATUS.md` → *Known unverified claims*:

- **Claim protection end to end.** Cannot be verified from singleplayer (§4); the human is retesting on a
  dedicated server. This is the one that matters — v0.4.49 restored the pre-v0.4.48 behaviour, so what is
  being verified is the *original* implementation, not a new one.
- **Filled arches in play** (v0.4.47). Verified identical across 576 configurations offline; the geometry
  has not been looked at in a world.
- **The guide-count cap reaching the HUD** (v0.4.46). Unreachable with default config — both count caps
  default to unlimited — so it needs `maxGuidesPerPlayer` set in `layout.json` to exercise.
- **Saving under the v0.4.46 timing.** The human verified reload before that change; the server's cadence
  moved afterwards.
