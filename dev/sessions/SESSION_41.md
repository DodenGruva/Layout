# Session 41 — v0.4.55 → v0.4.59

**Branch:** `codex/review-hardening-v055`. **DataVersion 13. Protocol 26.** **86 source files** (one added).

An independent whole-project review found that Session 37's coordinate defence did not cover the later
whole-guide Transform tools, and that a compound Transform packet was documented as one operation while the
server and private-guide authority executed it as two. The review was first performed against a copy at
v0.4.49. Before implementation, the human replaced that copy with the current Session-40 tree at v0.4.54;
every finding was re-traced against commit `9d840f0` before any code changed. Session 40's background-save
work was present, reviewed for overlap, and left untouched.

This session delivered the three original review findings, then kept going through four adversarial follow-ups:
the server-hang boundary was closed on every whole-guide path, compound in-place transforms became genuinely
transactional and one-step undoable, projection fields acquired central validation, malformed no-op transform
packets were rejected, and cancelling an immense create or reshape now interrupts the expensive geometry
already running for it. The former count-only claim revision was replaced by a fail-closed structural snapshot,
then bounded to the guide's exact block footprint so distant claims cannot restart or inflate the operation.

---

## 1. The missed coordinate source: a valid guide moved somewhere invalid

`GuideBounds` was introduced in Session 37 after the volume-scan hang was reproduced at world coordinate
2^27 = 134,217,728. Its validation covered create packets, point edits, inserts, private-guide publication,
world-save load, and the hand-editable private-guide file. That closed every source known at the time.

Whole-guide Move and Transform arrived earlier as separate features, however, and their *deltas* were not
run through the resulting-coordinate check:

- `GuideTranslatePacket` and `GuideTransformPacket` carry three signed integers in sixteenth-block units.
- `int.MaxValue / 16` is approximately 134,217,727.94 blocks — the reproduced overflow boundary.
- `TryQuantiseTranslation` checked only that the delta was aligned to the guide's voxel scale.
- `TransformGuide` applied the delta and then called `CountForCaps` before claim validation.
- Seven of the eight volume types are already proven never to return when a scan begins on that boundary.

One modified-client packet could therefore move a perfectly valid small volume to the dangerous coordinate
and hang the authority during its recount. It did not require a large guide, a large packet, or repeated
traffic. The standalone Move path reused its cached voxel count, but a non-admin move still built the exact
claim footprint at the invalid destination and could reach the same scan; an admin could instead persist and
broadcast the invalid state.

### What v0.4.55 changes

The bounds rule now follows the data rather than a remembered list of packet handlers:

- Move validates both transformed point lists and the carried projection plane before recalculation or claim
  work. Rejection restores the original state without marking it dirty.
- Rotate validates its axis and final point/plane state before adopting or counting the rotated shape.
- In-place Transform validates the final result before shape adoption, voxel counting, claims, persistence,
  undo recording, or broadcast.
- Copy validates its axes and quantised delta; its existing `RestoreGuide` seam validates the finished clone
  before counting it.
- The double-to-int delta conversion now rejects NaN, infinity, and values outside the signed-int range before
  casting instead of relying on unchecked conversion behaviour.
- Projection-plane movement uses `long` for the addition and refuses overflow before narrowing back to `int`.

The fix deliberately does not rewrite the volume scan loops. `GOTCHAS` G31's long/count-based rewrite remains
defence in depth; v0.4.55 closes the newly discovered reachable source with the same validated-entry design
already chosen and measured in Session 37.

## 2. “One operation” was two commits

`GuideTransformPacket` promises one compound pad action: optional rotation, mirror and translation, applied
with one validation and one undo step. The in-place handlers did something materially different on both
authorities:

1. call `RotateGuide`, which mutates the live guide, recounts it, checks claims, stores the count and marks the
   registry dirty;
2. immediately record a `RotateGuideCommand` in undo history;
3. call `TransformGuide` separately for mirror and movement;
4. broadcast only after the second call succeeds.

If rotation succeeded and the movement then crossed protected land or a cap, the rotation remained committed
and saved. Only the requesting player received the failure resync; every other client retained the old guide
because the full-state broadcast lived after the second success. The requester, other players, the save, and
the undo stack could therefore describe different states. Even an entirely successful compound action took
two Undo presses despite the packet contract saying one.

### One transaction, including undo

`GuideManager.TransformGuide` now owns the complete operation. Forward execution preserves the shipped
in-place order — rotate, mirror, translate — against one stored pivot. It snapshots both live point lists,
shape-axis orientation and projection plane; applies the full candidate; validates bounds, caps and access
once; then either commits once or restores the exact snapshot.

The server and `LocalGuideAuthority` now each make one manager call, record one `TransformGuideCommand`, stamp
one sculptor change and publish one full state. A failed operation changes nothing, so a requester-only resync
cannot leave other clients stale.

Undo is part of the same transaction rather than merely sharing one history entry. It applies the mathematical
inverse in reverse order — translate back, mirror back, rotate back — around the recorded original pivot, then
runs the same single validation and rollback boundary. Redo executes the original forward transaction again.

No packet fields, registration slots, `DataVersion`, or protocol number changed.

## 3. Projection data is now validated as coordinate data

Projection mode, plane axis and plane offset all cross the wire as raw integers. `OnSetProjection` cast them
directly, while `GuideManager.SetProjection` rejected only the specific combination “3D volume plus Surface”.
Undefined enum values and arbitrary offsets therefore survived into the live registry, broadcasts and saves.

For Surface guides an offset is a real world coordinate. `int.MinValue` also made the claim-footprint expression
`plane - 1` wrap to `int.MaxValue`, so even bounded control points could send access checks to the opposite
integer extreme. Invalid projection modes were interpreted inconsistently: some paths tested exactly for
`Surface`, while others expected exactly `Volumetric`.

`GuideBounds.IsUsableProjection` is now the central rule:

- the mode must be a defined `ProjectionMode`;
- the flattened axis must be a defined `PlaneAxis`;
- the stored offset must lie within the same axis-specific world limit as control points.

The offset is bounded for Volumetric guides too. It is dormant for rendering there, but whole-guide movement
still carries it; accepting an extreme hidden value would preserve an overflow seam and could make an otherwise
valid guide impossible to move safely.

The rule is applied to direct and prepared creation, prepared commit, private-guide publication/restore,
world/private-file load, projection changes, and every whole-guide mutation. A parseable saved record with an
invalid projection is skipped with a specific warning instead of being adopted and interpreted differently by
later subsystems.

## 4. The two adversarial follow-ups

### v0.4.56 — validate malformed fields before the no-op return

The first adversarial review harness found a small but real contract hole in v0.4.55. `TransformGuide`
worked out whether rotation, mirror or translation would change anything and returned success for a no-op
*before* rejecting invalid enum/sentinel values. A crafted packet carrying `mirrorAxis = -2` was therefore
accepted as a successful no-op. It did not mutate a guide, but it contradicted the new rule that malformed
transform packets are rejected at the authority boundary and left a future handler free to treat the false
success as meaningful. Rotation axis remains deliberately ignored when the normalized turn count is zero,
matching `GuideTransformPacket`'s wire contract; active rotation axes are validated.

v0.4.56 moves validation ahead of the no-op return. The behavior of every legitimate no-op is unchanged;
only malformed values stop being reported as success. A temporary harness outside the repository exercises
the sentinel explicitly and runs 648 combinations of surface axis, rotation axis, turn count, mirror choice
and translation through forward execution, Undo and Redo.

### v0.4.57 — cancellation reaches the work, not merely the seams

The original lower-priority finding was then taken up explicitly. Since v0.4.36, cancelling an immense
create or reshape set a volatile flag that the worker checked before and after `CountUpTo`, and
`BuildFootprint` checked while collapsing a generated voxel list. That stopped a cancelled result from
committing, but it did not stop the expensive count already running, nor the full `GetVoxelPositions`
materialisation that happened before footprint collapse began. One abandoned guide could therefore retain
the single immense-validator lane until all of its geometry finished.

v0.4.57 adds two additive geometry seams:

- `ICancellableThresholdVoxelCounter` extends the existing threshold counter without changing ordinary
  callers. All eight volume variants probe cancellation inside their direct or fallback count loops.
- `ICancellableVoxelGenerator` provides exact materialisation with the same all-or-nothing rule. Sphere,
  Dome, Cylinder, Cone, Box, Tapered Cylinder and the shared normal/tapered Polygonal Prism implementation
  probe inside their scans, large-volume fallback marching and marker assignment.
- `BuildFootprint` dispatches through that cancellable generator before its existing collapse checks, so
  cancellation covers both halves of footprint construction.
- Cancellation throws `OperationCanceledException` inside worker-only paths and is caught by the immense
  create/sculpt worker as an abandoned result. A partial count or voxel list can never be mistaken for an
  exact answer, an exceeded-cap sentinel or an empty footprint.

The first implementation pass covered counting and initially appeared complete. The requested adversarial
review found that `BuildFootprint` still called the ordinary `GetVoxelPositions` first; its cancellation probe
did not run until after the expensive materialisation had finished. The implementation was extended through
exact generation and marker claiming before v0.4.57 was packaged. The renderer's progressive cancellation
path and every existing synchronous method retain their established APIs and output.

One boundary is deliberately unchanged: the small synchronous threshold probe that decides whether a request
must enter the immense lane happens before a pending operation exists, so there is nothing for a later cancel
action to address yet. v0.4.57 closes the disclosed worker-lane availability defect once an immense operation
has been queued.

## 5. Claim consistency: from count-only to bounded structural state

### v0.4.58 — close equal-count and in-place changes

The count surrogate was not a revision. It missed an equal-count remove/add replacement and an in-place claim
resize, and after three observed count changes it proceeded anyway. An immense validation could therefore
commit after permissions had changed between its bounded server-tick slices.

`ClaimAccessSnapshot` now captures the built-in authorization state that Layout can observe: claim geometry,
the installed game's `LandClaim.TestPlayerAccess` result for each relevant claim, and the raw player state that
can change vanilla access (identity, game mode, alive state, build privilege, role privilege and groups). The
snapshot is compared before every later slice and once after the final slice, so a change-and-revert between
slices is still visible. Equal-count replacement, in-place resizing and player authorization changes restart
the operation. A fourth change after three restarts refuses with `layout-validationbusy`; stale work never
commits. The final sculpt commit also repeats privilege and jail checks.

This is deliberately not a replacement for the exact block-by-block `TestAccess` pass. `DeniedByMod` remains
opaque to Layout, so every actual guide block is still checked through the complete engine/mod access chain.

### v0.4.59 — scope consistency to the only claims that can matter

The first correct snapshot was global. On every comparison it copied and permission-tested all claims on the
server, even though a claim wholly outside the guide's footprint cannot affect any exact access result. That
was safe but needlessly broad.

The worker now derives an exact integer 3D bound from the already-built claim footprint. Surface projection's
adjacent cells are part of that footprint and therefore part of the bound automatically. Claim areas use the
engine's minimum-inclusive, maximum-exclusive Cuboidi semantics and internal Y coordinates. A cheap bounds
intersection decides relevance; intersecting geometry is clipped to the footprint before it enters the
snapshot, so changes exclusively outside the footprint do not cause restarts.

The game's API offers `All` but no regional claim query, so Layout still scans each area's bounds. It does not
copy, permission-test or structurally compare distant claims, and distant add/remove/resize operations cannot
restart validation. The cheap bounding-box operation is therefore used only to scope *staleness tracking*;
it never skips the exact per-block access check that v0.4.48 incorrectly bypassed.

## 6. Verification and release artifacts

- `dotnet build Layout.csproj --no-restore`: **0 warnings, 0 errors**.
- `dotnet build Layout.csproj -c Release --no-restore`: **0 warnings, 0 errors**.
- `git diff --check`: pass.
- `modinfo.json` encoding check: pass; no non-ASCII bytes outside the optional BOM.
- A temporary focused harness outside the repository passes **29/29** groups: the original projection and
  transactional-transform cases; 648 compound transform/Undo/Redo combinations; atomic claim rollback;
  malformed no-op rejection; ordinary/cancellable count equivalence; large fallback count equivalence;
  ordinary/cancellable exact-voxel equivalence; prompt cancellation in every volume counter and generator;
  cancellation before work starts; relevant replacement/resize/player-state changes; bounded-edge semantics;
  projection-adjacent cells; clipped extensions; and distant claim churn that correctly remains irrelevant.
- In a focused Release harness with 10,000 synthetic claims and one relevant claim, the final bounded snapshot
  measured about **0.10–0.14 ms and 820 bytes** per capture, versus **2.50–3.10 ms and 4.28 MB** for v0.4.58's
  global structural snapshot. The former count-only read was about 3.3 ns with no allocation, but was unsound.
  Exact block checks retain their existing ≤128-block / approximately 1 ms server-tick budget.
- `Layout0.4.55.zip`: 42 explicit entries and 363,652 bytes.
- `Layout0.4.56.zip`: 42 explicit entries and 363,653 bytes. Its DLL differs from v0.4.55 only in
  `GuideManager`'s validation-before-no-op ordering.
- `Layout0.4.57.zip`: 42 explicit entries and 364,823 bytes; root metadata first, 39/39 assets present,
  no directory entries or backslash paths, `Layout.dll` at the root, and embedded `modinfo.json` reports
  v0.4.57. The packaged DLL's SHA-256 matches the reviewed Release DLL.
- `Layout0.4.58.zip`: 42 explicit entries and 367,375 bytes; the same structural checks pass and its packaged
  DLL SHA-256 is `BACD588E2E8EB0D8E3F38C2CA819414CD7685ABA87A23AF9416ECDFBAC29A241`.
- Final `Layout0.4.59.zip`: 42 explicit entries and 368,115 bytes; root metadata first, 39/39 assets present,
  no directory entries or backslash paths, and embedded `modinfo.json` reports v0.4.59. The packaged DLL
  SHA-256 is `50C92417D16BE059104E46E0C4B40479CF61C1BC07ADC6A0390EE16C39E57307`.
- No automated test project was added to the repository. The focused harness is deliberately disposable;
  actual game feel and dedicated-server lifecycle behavior still require play.

`DocCheck.ps1` was run after the documentation update and passes all mechanical checks.

---

## Delivered

- **v0.4.55** — Closed the whole-guide coordinate/plane validation gap; made public and private compound
  transforms one atomic validation, broadcast and undo step; rejected malformed projection and transform
  values; produced and structurally verified the playable release zip.
- **v0.4.56** — Rejected malformed mirror/rotation values even when the requested transform would otherwise
  be a no-op; added the focused transform and hostile-input regression harness outside the repository.
- **v0.4.57** — Made active immense create/sculpt cancellation interrupt threshold counting, exact volume
  voxel generation, large-volume fallback marching, marker assignment and footprint collapse; verified all
  eight volume variants preserve their ordinary results and abandon promptly.
- **v0.4.58** — Replaced count-only claim-change detection with an immutable semantic snapshot, compared at
  every slice and before commit; added a fail-closed three-restart limit and final sculpt privilege/jail check.
- **v0.4.59** — Bounded claim snapshots to the exact guide footprint while retaining every exact block access
  check; made distant claims irrelevant and reduced the synthetic 10,000-claim snapshot by roughly 20–31× in
  time and more than 5,000× in allocation; packaged the final release.

## Decisions

- **Revalidate against the refreshed tree before implementing.** The source copy moved from v0.4.49 to
  Session 40's v0.4.54 while the review was in progress. Findings were not assumed to survive that change;
  each was traced again, and the new background-save paths were checked for overlap before edits began.
- **Make the manager operation atomic, not merely the handler.** Delaying the first undo record or adding a
  failure broadcast would still leave a partially mutated authoritative guide. The transaction boundary has
  to surround the mutations, validation and rollback themselves, and must be shared by server and private
  client authority.
- **Make undo atomic too.** One history entry that performs several independently committing manager calls is
  not one operation when a later inverse step can fail. Reverse-order undo now runs inside the same transaction.
- **Preserve the existing in-place transform order:** rotate, mirror, translate. The correction changes
  commit semantics, not the visible result of a successful action.
- **Bound dormant Volumetric plane offsets.** Ignored render state still participates in later movement
  arithmetic; accepting an extreme value would leave the coordinate defence incomplete.
- **Cancellation is cooperative and all-or-nothing.** Reuse the existing volatile job flag through cheap
  probes rather than adding token-source lifecycle state. Throw inside worker-only geometry so a partial
  count/list cannot acquire a valid meaning; catch it only when that same job is known cancelled.
- **Keep the renderer's progressive cancellation path separate.** v0.4.57 adds worker-specific interfaces
  and retains every ordinary/progressive public method, avoiding an unrelated renderer behavior change.
- **Fail closed after bounded claim churn.** Three restarts preserve the established liveness allowance; a
  fourth observed relevant change now refuses instead of treating unstable authorization as validated.
- **Scope staleness, never authorization.** Bounding geometry determines which claims can make an immense
  operation stale. It does not answer whether a block is buildable; exact `TestAccess` remains authoritative.
- **Use the exact built footprint as the bound.** This avoids reconstructing shape/projection rules and
  automatically includes the adjacent block checked for Surface projection.

## Traps

⚠️ **Validating every original input source does not validate a later operation that synthesises new
coordinates.** Create/edit/import/load were all guarded, but Move and Transform combined a bounded guide with
an unbounded delta after those seams. Trigger: any operation that translates, rotates, mirrors, projects or
otherwise derives a coordinate. Do: validate the finished candidate before *anything* adopts or scans it.

⚠️ **One packet and one undo record do not make an operation atomic.** The old compound handler committed
rotation before attempting movement and broadcast only at the end. Trigger: a handler that implements one user
action through two mutating manager calls. Do: snapshot, build, validate and commit in one authority operation;
apply the inverse under that same boundary.

⚠️ **An ignored field can still be dangerous when another operation carries it.** Volumetric rendering ignores
the projection plane, but Move adds to its offset. Trigger: allowing malformed dormant state because today's
consumer does not read it. Do: follow the field through every mutation and persistence path before declaring it
irrelevant.

⚠️ **The packaging shell is Windows PowerShell 5.1 even though the mod targets .NET 10.** Loading only
`System.IO.Compression.FileSystem` did not expose `ZipArchiveMode`, and `System.IO.Path.GetRelativePath` was not
available. The first two package attempts produced no usable archive; the exact partial target was removed and
replaced. Do: load both compression assemblies and derive entry names from the already-validated root prefix;
then reopen and inspect every archive invariant before delivery.

⚠️ **A cancellation check after an expensive method does not make that method cancellable.** The first v0.4.57
pass correctly interrupted `CountUpTo`, but `BuildFootprint` still materialised the entire voxel list before
its existing cancellation loop began. Trigger: declaring a multi-stage worker cancellable because each stage
boundary checks a flag. Do: trace every allocation/scan inside each stage and carry the probe into the deepest
loop; incomplete geometry must terminate exceptionally rather than return an ambiguous partial value.

⚠️ **Validate malformed fields before a “nothing changes” early return.** v0.4.55's first ordering returned
success for `mirrorAxis = -2` because the action was otherwise a no-op. Trigger: accepting raw packet/file
values in a method with an idempotent fast path. Do: validate the request's domain first, then decide whether
its valid meaning changes state.

⚠️ **A globally correct consistency snapshot can still be much broader than the permission question.** The
first v0.4.58 snapshot allocated state for every claim. Trigger: protecting sliced work against concurrent
changes when only a spatial subset can affect the result. Do: derive bounds from the exact footprint, clip
intersecting state to those bounds, and ignore outside-only churn — while retaining the full per-block access
check so opaque denial sources are never bypassed.

## Flagged and unverified

**Judgement calls awaiting review:**

1. The successful in-place compound operation retains its old visible order: rotate, mirror, translate.
2. Parseable legacy records with undefined projection values or an out-of-world dormant plane are now dropped
   instead of normalised. Legitimate clients have never produced such records.
3. Relevant claim churn receives three restarts, then a fail-closed busy refusal. This retains Session 37's
   restart allowance but changes the former fourth-change behavior from proceed to refuse.

**Claims not tested:**

- The human explicitly said they would be away while the v0.4.55 and v0.4.57 implementation passes ran, so
  neither revision has been playtested in the game during this session.
- Public and F4-private combined rotate + mirror + move should each take one Undo and one Redo and return to
  the exact prior/result state. This is source-traced and compile-checked, not exercised in the game.
- A denied compound move should leave every connected client on the original geometry. This needs a non-admin
  account on a dedicated server; the singleplayer host is deliberately exempt from claim validation.
- Hostile extreme-coordinate and malformed-enum packets were verified by tracing every rejection before its
  first scan. The malformed no-op sentinel is also harnessed, but no packet-fuzzing harness or automated test
  project exists in the repository.
- Cancellation equivalence and prompt abandonment are verified directly against all eight volume variants;
  player cancel/disconnect/shutdown against a live dedicated-server queue remains an in-game lifecycle test.
- The 29 claim-snapshot groups and performance measurements use a focused Release harness. No dedicated-server
  non-admin result has been reported for equal-count replacement, in-place resize, player-state churn, or the
  three-restart refusal; singleplayer's privileged host cannot exercise Layout's claim enforcement.
- v0.4.55 includes Session 40's background-save implementation unchanged. The human had explicitly not
  playtested v0.4.50–v0.4.54 before this session, so that separate verification remains outstanding too.
