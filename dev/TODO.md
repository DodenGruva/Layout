# Layout — TODO / Outstanding Items (current: v0.4.14 on `beta`)

> **Purpose.** The running punch-list. Companion to `ARCHITECTURE.md` (the plan), `PROJECT_STATUS.md` (the
> status), and `HANDOFF.md` (the consolidated current-state brief).

---

## ⭐ Top of the list

> **✅ THE SETTINGS PAGE IS COMPLETE — v0.4.2–v0.4.14 (SESSION_32).**
> **F9 (in-game settings panel), T2 (its formatting + hover text), F10 (the gear glyph) and F11 (colour
> schemes) are all delivered.** DataVersion 12 and protocol 19 both unchanged — none of it touches the wire.
> **82 source files** (one added: `GuidePalette.cs`).
>
> The page is now a two-section panel: a coloured **Layout: On/Off** master switch above the sections, then
> **Appearance** (opacity + Reset, colour scheme, Chiseling Highlight) and **Behaviour** (a sliding
> Public/Private control with the server's policy above it and a Publish button below, plus the two chalk
> refill shortcuts). Every row carries hover text.
>
> **Colours** ship as Default / Red-Green Safe / **Custom**, the last with a seven-role table and a
> sixteen-swatch grid. The human overrode the preset-only design and was right to: presets answer "I cannot
> tell these apart", not "yellow disappears against sandstone".
>
> Full record in `SESSION_32.md`; its §9 carries nine flagged items, none blocking. **§8 records two
> mistakes worth not repeating** — the `LoadedTexture` null-ref that crashed v0.4.4, and a PowerShell text
> round-trip that double-encoded a source file.
>
> **QUEUED, not started:** **T1** below. That is the whole of the old F-queue backlog.

> **✅ F9 / T2 DELIVERED (SESSION_32).** The settings panel and its formatting both shipped across
> v0.4.2–v0.4.14. Detail below under F9; the per-revision record is in `SESSION_32.md`.

> **🔧 OPEN FROM THE TRANSFORM ARC (human-requested 2026-07-27, not started):**
>
> **T1. CTRL on free-move constrains the guide to the targeted surface.** While free-moving, holding CTRL
> should drop the guide onto the block face under the crosshair instead of riding the fixed view-ray depth
> (`MoveSession.Depth`) — the same idea as CTRL's level/cardinal snap while drafting.
> **✅ DECIDED 2026-07-27: the guide's LOWEST VOXEL PLANE meets the surface.** Unambiguous on every shape
> and matches the common case of setting a build down on the ground. Accepted consequence: aiming at a wall
> or ceiling still pushes the guide's BOTTOM to that face, which may read oddly — predictable was preferred
> over clever. (Rejected: nearest-face, which handles walls but has no sane meaning on a sphere; base
> anchors, which would sink a dome halfway into the floor since its anchors are its base ring, not its
> lowest point.)
> The offset must stay a whole number of the guide's own voxels or `TranslateGuide` will refuse it — snap
> AFTER computing the contact, not before. The controller already has `CtrlHeld()` and the block raycast;
> the work is the contact rule, not the plumbing.
>
> **T2. Settings page: better formatting + mouse-over descriptions. — ✅ DELIVERED (v0.4.2, SESSION_32).**
> Every row carries `AddAutoSizeHoverText`, restoring the prose removed in v0.3.87, and the page is grouped
> into Appearance and Behaviour with consistent label-left / control-right rows.

> **📄 DOC DEBT (human-requested 2026-07-27): merge `PROJECT_STATUS.md` into `HANDOFF.md`.** The two are
> largely redundant — both are "where the project stands" briefs, and `PROJECT_STATUS.md` has been left to
> trail (it now carries a banner saying it is three sessions behind, which is itself the evidence). Keep
> **`HANDOFF.md`** as the single current-state document, fold across anything `PROJECT_STATUS.md` says that
> HANDOFF does not (its per-module descriptions are the likeliest unique content), then delete
> `PROJECT_STATUS.md` and update every reference: `CLAUDE.md`, `HANDOFF.md`'s own header and §3 repo tree,
> `ARCHITECTURE.md`'s banner, and this file's Purpose line. Note that maintaining one fewer status doc is
> the actual goal — do not simply move the redundancy into HANDOFF.

> **Session-16 playtest results (human-confirmed):** the v0.2.21 **inventory refill and hotbar refill both
> work** — the MouseDown-hook ordering and the `InventoryID` round-trip both hold, closing `SESSION_16.md` §8.
> **Guide updates in multiplayer read well:** watching a guide change a few times per second communicates
> "someone is moving that" clearly. Keep it. **Audited in item A2 (closed):** that is the ≤10 Hz
> grab-and-reshape path, not a draft — a remote DRAFT renders only a static anchor dot, and drafting sends no
> per-tick traffic at all. Nothing is being bombarded.

### A0. Session-18 delivery — the Tapered Cylinder arc (v0.2.24–v0.2.28) — full detail in `SESSION_18.md`

1. ~~**New shape: Tapered Cylinder (frustum).**~~ **DONE (v0.2.24).** `GuideShapeType.TaperedCylinder = 12`,
   the windmill/tower silhouette. **Four clicks** (base · base · height · rim) — the only 4-click shape.
   `rTop = 0` reproduces the cone exactly, `rTop = r` the cylinder exactly (verified to the voxel).
   **Protocol 6 → 7** (`GuideCreateRequestPacket.Rim`); DataVersion unchanged at 8.
2. ~~**Cylinder-family scan guard was the real size limit.**~~ **FIXED (v0.2.25).** The guard cubed
   `(2r + |h|)`, over-counting an upright cylinder's box ~7×, so the drag froze at **56% of cap** while the
   HUD honestly read 56%. It now measures the true AABB; the cap binds instead, as the HUD claims.
3. ~~**Cap clamp re-solved every tick (major lag past the cap).**~~ **FIXED (v0.2.26).** 14 full voxel counts
   per frame → **42–55 ms/tick** the moment the aim crossed the cap. Replaced with `CapClampTracker`, a
   persistent narrowing bracket: ≤2 checks/tick, **0 ms** once settled. Covers the drag path too.
4. ~~**Rim stage flared open and could not be adjusted or finished.**~~ **FIXED (v0.2.27).** Self-inflicted
   in v0.2.24: the clamp shrank toward the RAW height click, so its off-axis distance became the minimum
   top radius. Now shrinks toward the lid centre **on the axis**. Free-air rim aiming also stabilised
   (view ray sampled at the lid's distance — the v0.2.25 lid-plane intersection blew up at shallow angles).
5. ~~**Chalking Powder recipe accepted raw clay jugs.**~~ **FIXED (v0.2.28).** `game:jug-*` matched
   `jug-*-raw`, a plain `Block` that cannot hold the 0.1 L of dye. Now `game:jug-*-fired`. The bowl was
   already correctly `-fired`; `woodbucket` has no raw variant. Still 6 permutations.

> **Playtest closed (v0.2.35):** the revised tapered-cylinder flow feels good; crafting with a fired jug
> still works and a raw jug correctly does not. Public/private multiplayer also passed the human's release
> test. Release now and collect bug reports rather than holding for a larger test matrix.

### A1. Session-19 stabilization and placement effects (v0.2.29–v0.2.35) — full detail in `SESSION_19.md`

1. ~~**Unlimited/high server cap still hit a hidden tapered-cylinder size ceiling.**~~ **FIXED (v0.2.29).**
   Removed the shape-local 4M scan cutoff; the configured per-guide cap, or the 10M hard render ceiling when
   unlimited, now governs the shape.
2. ~~**Height click could immediately throw the rim to the old cursor target.**~~ **FIXED (v0.2.30–v0.2.32).**
   The rim begins at 60%, ignores the carried height click, requires a real release, and captures only after
   entering its annular handle band. The final release seam uses the engine's actual mouse-up event.
3. ~~**Large drafts could lag badly enough for input and preview work to pile up.**~~ **MITIGATED (v0.2.31).**
   Input still samples at ~33 Hz, while expensive guide generation/HUD/cap work scales from ~33 Hz down to
   ~2 Hz based on the last voxel count. Unlimited public servers skip pointless live cap-clamp recounts.
   This was the pre-v0.3 mitigation; Session 23 now delivers the bounded immense-guide pipeline.
4. ~~**Placement feedback needed complementary drifting dust.**~~ **DONE (v0.2.33–v0.2.35).** The original
   falling flecks remain; capped dust sites now cover 2D curves and full 3D shells without voxel generation.
   Dust has zero gravity. Flat shapes scatter broadly sideways; 3D dust drifts outward/upward.

### A2. Session-20 polygonal volumes and placement modifiers (v0.2.36–v0.2.47) — full detail in `SESSION_20.md`

1. ~~**Adjacent lock targeting could snap to a formerly locked voxel.**~~ **FIXED and playtest-confirmed
   (v0.2.36).** The first rendered voxel hit is authoritative, and each point owns only its nearest visible
   marker cell. **B-S9-1 is closed.**
2. ~~**Chalking Kit ground placement used the wrong chord.**~~ **FIXED (v0.2.37).** It now uses the standard
   SHIFT+right-click gesture advertised by its tooltip.
3. ~~**Add 3D regular-polygon shapes.**~~ **DONE (v0.2.38).** Polygonal Prism and Tapered Polygonal Prism use
   the same 3–24 side control as Polygon. The tapered version uses the four-stage base · base · height · rim
   flow. Protocol advanced 7 → 8 for the appended catalog/wire fields.
4. ~~**Make placement modifiers discoverable and stage-aware.**~~ **DONE (v0.2.39–v0.2.43).** Line supports
   vertical constraint; tapered rims support Center Apex; native held-item notes appear only while their
   action applies and use concise technical labels. Internal stage refreshes do not repeat the kit's ground
   placement note.
5. ~~**Add flat-side alignment, diagonal lines, and safe tapered rims.**~~ **DONE (v0.2.45).** SHIFT aligns a
   Polygon-family base to a flat side; CTRL+SHIFT constrains Line/Free-Shape to 45°; tapered rims stop at the
   base radius unless SHIFT explicitly allows flare. `FlatSideAligned` advanced DataVersion 8 → 9 and protocol
   8 → 9.
6. ~~**Create header shape names competed with explanatory text.**~~ **DONE (v0.2.47).** The rejected adaptive
   shrink experiment was fully reverted; the header now shows `Create Mode` and the normal-size shape name,
   without `- Next Guide:`.

### A3. Session-21 adaptive large-guide and structural-form arc (v0.3.0–v0.3.8) — full detail in `SESSION_21.md`

1. ~~**Large 3D drafts repeatedly calculated full shells/HUD dimensions and lagged despite fast settled
   rendering.**~~ **FIXED (v0.3.0–v0.3.2).** Cheap poses keep their selected-scale shell. Expensive motion
   uses an adaptive structural wireframe; the cursor retains selected-scale precision; settling performs one
   generation-safe background refinement and materializes bounded random batches. Pending HUD values yield
   to changing calculation glyphs.
2. ~~**Placed hover could re-trigger behemoth calculation.**~~ **FIXED (v0.3.3).** Cached dimensions become
   the display name, cached count feeds the cap bar, and the placed dimension field is blank. Active
   draft/grab measurement remains live when ready. DataVersion/protocol advanced to 10.
3. ~~**Cancelling a giant wireframe grab could restore visually, then launch a delayed shell expansion and
   climb from ~7 GB toward 12 GB.**~~ **FIXED (v0.3.4–v0.3.5).** Cancel reveals the retained settled mesh,
   invalidates transient generations, and quarantines the confirming authority echo by render fingerprint.
4. ~~**Structural guides needed more legible wires.**~~ **DONE (v0.3.6).** Round volumes use eight ribs;
   polygonal prisms use one longitudinal wire per corner.
5. ~~**Expose structural Wireframe as a real 3D guide form.**~~ **DONE (v0.3.7).** The contextual volume pair
   is Shell/Wireframe; state persists and has save/network/local-authority/undo/count/render/grab parity at
   selected scale. DataVersion/protocol 11. 2D Hollow/Filled is unchanged.
6. ~~**Large-guide placement sound should carry more weight.**~~ **DONE (v0.3.7).** Cached size drives a
   capped logarithmic curve: larger guides are louder, lower-pitched, and audible farther away.
7. ~~**Cylinder/Cone/Box still refused oversized valid shells at their legacy cubic scan guard.**~~ **FIXED
   (v0.3.8).** Under the legacy work budget they retain established voxelization; beyond it they use a
   surface-proportional ring/face fallback. Normal caps and the hard ceiling remain.
8. ~~**3D Form borrowed the Hollow/Filled icons.**~~ **FIXED (v0.3.8).** Dedicated faced-skin Shell and open
   corner-strutted Wireframe Cairo glyphs.

### A4. Session-22 action HUD, attribution, and sculpting parity (v0.3.9–v0.3.21) — full detail in `SESSION_22.md`

1. ~~**Make drafting/targeting state legible without expanding the HUD.**~~ **DONE (v0.3.9–v0.3.21).**
   Fixed-footprint Create/Sculpt/Creating/Sculpting/Edit/Editing/Delete/Deleting heading; contextual 42-pixel
   shape tile with outside active-state accent; compact Scale, projection/form, split dimensions, Total Voxels,
   and cap rows; cycling calculation dots; final label-width/alignment cleanup.
2. ~~**Track who created and last visibly changed a guide.**~~ **DONE (v0.3.9, DataVersion/protocol 12).**
   Creator is immutable. Last Sculptor covers committed geometry/settings/visibility/undo/redo changes under
   public or local authority, but not hover, selection, cancellation, rejection, or no-op operations.
3. ~~**Keep attribution out of the permanent HUD.**~~ **DONE (v0.3.13, protocol 13).** `/layout who` resolves
   the selected Edit guide, then grabbed/aimed guide and reports exact shape, private status, Creator, and
   Last Sculptor.
4. ~~**Bring large-guide sculpting up to placement feel.**~~ **DONE (v0.3.14–v0.3.15).** Expensive Shell
   grabs use bounded motion wireframes, then selected-scale background refinement and batched materialization;
   tapered rim CTRL/SHIFT constraints match placement; Edit right-click deselects without mutation.
5. ~~**Surface→Volumetric mid-draft could lose Arch/Half-Circle anchors.**~~ **FIXED (v0.3.16).** All placed
   draft points move by the signed half-voxel convention offset when projection changes.
6. ~~**GUI/HUD accumulated clipped or redundant text.**~~ **FIXED (v0.3.16–v0.3.21).** Fill is centered,
   Surface omits plane descriptors, the empty Edit instruction is gone, label punctuation no longer wraps,
   arrows align, and `Volumetric · Wireframe` uses the below-tile full width.

### A5. Session-23 moderation, claims, and immense-guide execution (v0.3.22–v0.3.34) — full detail in `SESSION_23.md`

1. ~~**Add persistent server moderation/cap controls.**~~ **DONE (v0.3.22–v0.3.25, protocol 14).**
   Per-player jail and capacity overrides persist; administrative inspect/cleanup commands and policy sync
   are implemented; blocked drafts present a comatose HUD state.
2. ~~**Make public guide geometry respect block claims.**~~ **DONE (v0.3.25).** The server validates every
   affected claim before public create/sculpt commits. Private/local authority remains local.
3. ~~**Remove the immense final-placement server hang.**~~ **DONE (v0.3.26–v0.3.27, protocol 15).** Above
   the 8,000-voxel immediate threshold, pure generation/counting uses one below-normal worker and claim
   validation advances by at most 128 blocks or about 1 ms per 20 ms tick. One immense operation runs at a
   time; explicit rejection closes the client handoff.
4. ~~**Keep an immense guide visible through final placement and sculpt release.**~~ **DONE
   (v0.3.28–v0.3.32).** Selected-scale scaffolds/settled meshes persist through authority and first
   replacement batches; cancellable scans discard stale work; competing immense operations are gated; old
   meshes retire over later frames.
5. ~~**Replace banded/square reveal with torn organic growth.**~~ **DONE (v0.3.33–v0.3.34).**
   Deterministic multi-seed 26-neighbour growth, broad/detail noise, and fine grain stream the exact voxel
   set in frayed spreading fronts. Immense sculpt release no longer performs a synchronous full rebuild.
6. ~~**Do not render guides beyond the game's range; add a personal master switch.**~~ **DONE (v0.3.33,
   protocol 16).** Conservative whole-guide bounds follow live `viewDistance`; `/layout off|on` and
   `.layout off|on` skip/resume the full Layout render pass without deleting guides.

**Next playtest focus:** small guides still appear immediately; immense final click never freezes or
disappears; server tick health during allowed/denied claim checks; organic reveal stays torn from beginning
to end; immense sculpt release; overlapping-operation gate; distance culling; and `/layout off|on` in public,
private, and vanilla-fallback contexts.

### A6. Session-24 materialization completion and HUD dimensions (v0.3.35–v0.3.40) — full detail in `SESSION_24.md`

1. ~~**Scale nucleation with guide size and align growth with readiness.**~~ **DONE.** Site count is a
   six-site base plus one per 500 voxels, while upload cadence scales from about 18 ms for small guides to
   45 ms by 120,000 voxels and backs off under frame pressure.
2. ~~**Restore a completely clean final shell.**~~ **DONE.** Organic growth and the uniform final shell are
   separate lockstep mesh sets. After every exact voxel has appeared, growth remains visible for 200 ms,
   then swaps atomically to the clean shell.
3. ~~**Remove scaffold overlap and gate placement effects.**~~ **DONE.** The wireframe disappears with the
   first organic batch. Particles and sound wait until both final placement authority and the clean-shell
   swap, including drafts allowed to finish materializing before their last click.
4. ~~**Prevent sculpt lockout after resizing an immense guide below threshold.**~~ **DONE.** The ordinary
   settled rebuild now closes the retained immense transaction and clears its latch.
5. ~~**Materialize Wireframe→Shell Edit changes.**~~ **DONE.** A settled form change uses the cancellable,
   off-thread organic pipeline instead of synchronously generating the complete clean shell.
6. ~~**Remove polygonal-prism startup stalls.**~~ **DONE.** Side normals are precomputed and safe
   inner/outer annulus rejection avoids expensive edge tests across empty bounding-box regions.
7. ~~**Correct round-volume HUD dimensions.**~~ **DONE.** Shape-provided intrinsic width/axial height
   replaces the diagonal of the world-axis XZ bounds; an 83-block dome now displays 83 blocks wide.

**Next playtest focus:** verify the entire growth surface is visible before the 200 ms hold and clean swap;
particles occur only after final placement; small/large timing feels proportional; Wireframe→Shell edits and
repeated large→small sculpt releases remain responsive; polygonal prisms start promptly; and every 3D family
reports intuitive block dimensions.

### A7. Session-25 measured guide culling (v0.3.41–v0.3.43) — full detail in `SESSION_25.md`

> **Historical experiment, not current architecture.** The isolated measurements remain useful, but broader
> playtesting exposed spatial seams. v0.3.49 restored the v0.3.42 renderer; see A8 and `SESSION_26.md`.

1. ~~**Cull complete guides outside the camera.**~~ **DONE (v0.3.41).** Whole-guide bounds combine live
   view distance with Vintage Story's current frustum for placed guides, drafts, pending visuals, and markers.
2. ~~**Measure the actual render submission.**~~ **DONE (v0.3.42).** `.layout renderstats` reports last-frame
   guide visibility, batches, approximate triangles, extras, and smoothed frame time.
3. ~~**Cull invisible portions of an intersecting immense guide.**~~ **DONE (v0.3.43).** Clean final
   materialization uses fixed 32-block regions with independent bounds. Small guides and organic growth
   retain their paths; existing immense saved Shells reload through a temporary scaffold/background build.
4. ~~**Preserve region-boundary fidelity.**~~ **DONE and harnessed.** Mathematical floor division covers
   negative coordinates and every region receives the full guide occupancy, so cross-boundary shared faces
   remain absent. Human playtest approved the result.
5. ~~**Try same-colour/orientation greedy face merging.**~~ **TRIED and REJECTED (v0.3.44–v0.3.48).**
   Submission reductions did not compensate for bands, unstable depth/visibility, blur/brightness defects,
   delayed clean swaps, and worse real-play FPS.

**Measured result:** partial view changed from 128 drawn batches / 32.6M submitted triangles / 7.5 ms to
33 / 7.5M / 4.4 ms, with 110 spatial batches culled. This did not survive broader fidelity/performance testing.

### A8. Session-26 rollback, persistent visibility, and cumulative budgets (v0.3.44–v0.3.52) — full detail in `SESSION_26.md`

1. ~~**Restore the accepted renderer.**~~ **DONE (v0.3.49).** Exact v0.3.42
   `GuideRenderer`/`GuideMeshBuilder` behavior restored; whole-guide distance/frustum culling and render stats
   remain, while spatial final meshes and greedy/depth experiments are gone.
2. ~~**Persist personal `/layout off|on`.**~~ **DONE (v0.3.50).** `guideRenderingEnabled` saves immediately
   in `layout-client.json` for both public and client-only commands and restores after reconnect/restart.
3. ~~**Raise/default and add budgets.**~~ **DONE (v0.3.50).** Per-guide default 500,000; cumulative
   original-creator public-guide cap 1,000,000; default world total unlimited. Delete frees budget; restore,
   mutation, and immense async paths cannot bypass it. Existing over-cap guides may remain/shrink, not grow.
4. ~~**Explain a saved off-state at login.**~~ **DONE (v0.3.51).** Chat tells the player Layout is off and
   gives `/layout on` plus the client-only alternative.
5. ~~**Prevent invisible Chalking Kit use.**~~ **DONE (v0.3.52).** Tool clicks, settings, undo, and redo are
   consumed with a warning while off; disabling cancels draft/grab/transient UI state; the HUD shows the same
   recovery instruction.
6. **Field-soak next:** verify saved off/on state across public, mixed, and fallback sessions; exercise
   cumulative creator accounting through multiplayer edits, deletes, and undo/restore; confirm every blocked
   Chalking Kit input gives clear feedback.

### A9. Session-27 cumulative-cap override and final release (v0.3.53) — full detail in `SESSION_27.md`

1. ~~**Give administrators a per-player cumulative-cap override.**~~ **DONE.**
   `/layout totalvoxelcap <player> <number>` sets a persistent original-creator cumulative allowance; `0`
   removes it and restores `perPlayerTotalVoxelCap`.
2. ~~**Keep per-guide and cumulative semantics distinct.**~~ **DONE.** `/layout voxelcap` still controls the
   acting player's one-guide limit. Collaborating on another creator's guide cannot bypass that creator's
   effective cumulative cap.
3. ~~**Cover every authority path.**~~ **DONE.** Ordinary and immense creation, restore/push/redo, ordinary
   mutation, early count limits, and immense sculpt commits all resolve the effective creator allowance.
4. ~~**Expose and persist the policy.**~~ **DONE.** `/layout info` reports usage/effective/override state;
   world-scoped admin-policy JSON advances additively to version 2. DataVersion 12 and protocol 16 remain.
5. ~~**Verify and package the final release.**~~ **DONE.** Focused cumulative-cap smoke test passed; Release
   build has 0 warnings/errors; `Layout0.3.53.zip` has 40 entries and 37 assets.

### A10. Current human backlog (status at v0.3.58)

1. **Fix Box and Square guides automatically constraining to cardinal directions.** — **OPEN.** The only
   outstanding functional defect; nothing in Sessions 25–28 touched it.
2. **Perform an adversarial code review.** — **OPEN.**
3. **Perform a performance-focused code review.** — **PARTLY ADDRESSED** by Session 28's measured renderer
   work, but a general review of the non-render code has not been done.
4. **Explore alternative rendering options for performance gains.** — **IN PROGRESS**, see
   `PLAN_RENDER_PERFORMANCE.md` and `SESSION_28.md`. Vertex welding (v0.3.57) removed 41% of guide frame
   cost with no visible change and is spent. A custom shader is under discussion. Ordering + spatial
   culling remain gated behind a visible change to the accepted look.
5. **Clean up and consolidate the documentation.** — **OPEN**, though Session 28 corrected the false
   performance claims in `SESSION_26.md`.

### A11. Session-28 rendering arc (v0.3.55–v0.3.58) — full detail in `SESSION_28.md`

1. ~~**Measure the real bottleneck instead of guessing.**~~ **DONE (v0.3.55–v0.3.56).** `.layout
   renderstats` reports vertices/indices/bytes and warns only on evidence of frame-cap clipping. Every
   frame-time figure in Sessions 25–27 was taken against a 238 FPS cap and understates the truth.
2. ~~**Reopen the renderer arc Session 26 closed.**~~ **DONE.** Its 140→80 FPS evidence was a double-mesh
   draw bug; both rejected experiments were rejected on appearance, not performance. See the correction
   banner atop `SESSION_26.md`.
3. ~~**Halve the cost of a large guide with no visible change.**~~ **DONE (v0.3.57).** Vertex welding:
   −72.9% vertices, −58.4% mesh data, −41.5% frame cost, triangle stream provably identical (10/10
   harness), human A/B confirmed indistinguishable. Welding now runs at ~1.0–1.08 vertices per quad — the
   theoretical floor — so this lever is spent.
4. ~~**Stop large guides freezing the client on world load and dropping onto other players in one lump.**~~
   **DONE (v0.3.58), NOT YET PLAYTESTED.** `RebuildGuide` was fully synchronous with no size check; guides
   above 100,000 voxels now scaffold and stream. The remote-arrival half needs a second player to verify.
5. **Next playtest focus:** world load with the 8M guide (scaffold → grow-in, no hang); whether 100,000 is
   the right threshold; a second player watching a large guide arrive.

### A12. Open — minor ground z-fighting (reported 2026-07-25, v0.3.6x)

Guide voxels resting **on the ground** show very minor z-fighting. Not reproduced or judged from outside
the game; the reporter was away from their machine.

**Made tunable rather than guessed at (v0.3.65).** `/layout inset <blocks>` sets the anti-z-fight inset
live, saves to `layout-client.json` as `zFightInset`, and rebuilds every guide. Default is the historical
**0.003**, so nothing changes until it is deliberately dialled.

**Read this before picking a value.** The inset was settled by three rounds of playtest — 0.004 rejected as
"seamy", 0.001 shimmered with distance, 0.003 chosen — but **the objection to 0.004 is obsolete**. That seam
was between two adjacent guide voxels meeting across a block boundary, and exposed-face meshing (0.2.14) no
longer emits those faces at all. The inset now only ever separates a guide face from a **world block** face,
so values above 0.003 are safer today than when the ceiling was set.

**Candidate causes, most to least likely:**

1. **0.003 is simply too small at distance or shallow angles.** Depth precision falls with distance, and the
   `CameraNudge` (0.003, in `GuideRenderer.SetModelMatrix`) pulls the mesh toward the camera — which helps
   a horizontal ground face when looking down at it, and barely at all when viewing from far away at a
   shallow angle. Try `/layout inset 0.006` then `0.01`.
2. **The custom shader does not implement `extraZOffset`.** `standard.vsh` applies
   `gl_Position.w += extraZOffset`; the lean shader omits it. If the engine sets a nonzero value for the
   Opaque stage, guides lost a small depth bias in v0.3.60. **Check first: does `/layout shader off` make
   the shimmer go away?** That single test separates cause 1 from cause 2, and no code change is needed
   to run it.
3. **The lift is not being applied at all on that surface.** The lowest layer's bottom face is only lifted
   when the solidity probe reports a solid block below (`GuideMeshBuilder`, `fy0`). A guide resting on
   something the probe reads as non-solid — snow layer, farmland, a slab top — would get no lift. If the
   shimmer is specific to certain ground types, this is the cause and the fix is in the probe, not the
   constant.

**Do not raise `CameraNudge` to compensate.** It is view-dependent by design and scales with distance from
the mesh origin; using it to paper over a face-inset problem would shift every guide, not just ground faces.

> **Standing constraint discovered in Session 28 — do not lose this.** Guides are *order-dependent*
> translucent geometry: Opaque stage, manual alpha blending, depth-tested, double-sided. On a hollow shell
> the guide overlaps itself at nearly every pixel, so whichever batch draws first wins. The accepted
> appearance is therefore partly a by-product of voxel emission order, and **any change that regroups
> primitives changes the picture**. Welding was safe precisely because it regroups nothing.

### A. Human backlog — queued at Session-16 end — ✅ ALL SEVEN DELIVERED (v0.2.22–v0.2.23)

1. ~~**Move the chalk-refill config from the server to the client.**~~ **DONE (v0.2.22).** Both toggles now
   live in `layout-client.json` (still default false — ground refill stays the intended ritual); removed from
   `layout.json`, where stale keys are simply ignored. Server-side *policy* checks dropped; server-side
   *integrity* validation (does the request name a real kit + real powder stack) deliberately KEPT — that is
   what stops a lost mouse-hook race corrupting an inventory.
   **⚠️ The non-obvious part, worth remembering:** a plain client-side gate would have silently made the
   HOTBAR toggle a no-op. `ItemChalkingPowder`'s held-interact callbacks run on BOTH sides and the SERVER
   mutates the stacks, so a permissive server refills for a player who switched the shortcut off. The server
   therefore has to be *told* the preference: new `ChalkRefillPrefsPacket` (C→S, sent on join, stored
   per-player, cleared on disconnect) — **protocol 5 → 6**. The two old `GuideBulkSyncPacket` flags are left
   declared-but-dead as padding (registration is append-only — never renumber). The inventory channel needed
   none of this; it was already client-initiated.
2. ~~**Audit the mid-draft broadcast rate so the server isn't bombarded.**~~ **DONE — audited, no code
   change needed.** Findings:
   - **Drafting is duration-independent: ~2 packets total.** `SendDraftStart` is guarded by
     `!_draft.HasActiveDraft` (`GuideToolController` ~line 889) so it fires ONCE at the first click; cancel /
     complete sends one more. **There is no per-tick draft traffic at all** — the ghost is purely local.
   - **The only continuous path is dragging a PLACED guide,** already capped at `GuideToolController` ~line
     477: ≤10 Hz (`MoveSendIntervalMs = 100`) AND skipped when the aim hasn't actually moved. That is already
     "a few updates per second".
   - **It stays cheap at scale because the packet carries an EDIT ARRAY, not geometry.** Voxels are never
     stored or sent (each client derives them), so dragging a 100-block sphere costs the same bandwidth as
     dragging a 2-block line. Fan-out is ≈10×(players) small packets/sec while one person drags.
   - **Correction to the original observation:** other players do NOT see an evolving draft. A remote draft
     renders as a SINGLE STATIC ANCHOR DOT (`GuideRenderer._remoteAnchors` is one `Vec3d` per player). The
     "guide updating a few times per second" that looked good in play was a **grab-and-reshape of an
     already-placed guide** — the 10 Hz path above.
   - **Known gap, deliberately not fixed:** the 10 Hz throttle is CLIENT-SIDE ONLY. `ServerNetworkHandler`
     `OnUpdate` checks privilege, lock-holder and guide existence but has no rate limit, so a modified client
     holding the lock could send at frame rate and be rebroadcast to everyone. The lock requirement bounds
     this to one driver per guide. If it is ever worth hardening, set the server floor WELL ABOVE 10 Hz
     (~20): the drag's final position arrives as an ordinary move packet just before release, so a tight
     limiter could drop it and settle the guide slightly off.
   - **Not built:** live broadcast of another player's evolving draft. That would be a new feature and the
     first continuous draft-time traffic — the one case where a deliberate few-per-second cap would matter.
3. ~~**Hard-cap chalk durability at 32.**~~ **DONE (v0.2.23).** `ItemGuideTool.MaxChalk = 32` is now the one
   source of truth. `GetChalk` reads the stored `durability` attribute DIRECTLY and clamps to [0, 32]; a new
   `IsChalkFull` replaced every "is it full?" test. All nine engine-durability call sites converted.
   **Root cause (decompiled, worth knowing):** `CollectibleObject.GetMaxDurability` **walks the collectible's
   BEHAVIORS** and lets any of them replace the value, and `GetRemainingDurability` does the same *and*
   defaults to that inflated max for a stack with no stored value. That behavior walk is the hook xskills
   uses — so a quality-crafted kit reported max 45 and refilled to 45. We therefore override BOTH
   (`GetMaxDurability` → 32, `GetRemainingDurability` → `GetChalk`) **without calling `base`**, since base is
   what walks the behaviors. An already-inflated kit self-heals: it reads 32 and the next placement writes
   32 − cost. Defends against both routes (behavior override AND a direct attribute write at craft).
   Verified by a throwaway harness against the Release DLL — **21/21**, covering 45→32, 9999→32, negative→0,
   the no-attribute default (fresh craft → full), and every fill-state boundary (22/21, 11/10, 1/0).
   ⚠️ Mechanism verified offline; **not yet tested against xskills itself** — craft a quality kit and confirm
   it comes out 32/32.
4. ~~**Remove the "hotbar refill is off here…" warning.**~~ **DONE (v0.2.22).** A disabled channel is now a
   silent no-op; only genuine failures ("already full" / "no kit in hotbar") still report, and only when the
   channel is switched on.
5. ~~**Change all authorship to "Doden".**~~ **DONE (v0.2.22).** `modinfo.json` `authors` is `["Doden"]`;
   swept — no other author/attribution string exists in source.
6. ~~**Remove personal file paths from the project.**~~ **DONE.** `Layout.csproj`'s `<VintagestoryDir>` now
   auto-resolves: `-p:VintagestoryDir=…` → `VINTAGE_STORY` env var → platform default
   (`$(APPDATA)\Vintagestory` on Windows, `~/.local/share/vintagestory` otherwise). Verified via MSBuild that
   it resolves to the SAME path this machine used before, so the local build was unaffected; a new
   `VerifyVintagestoryDir` target fails with one clear message instead of five missing-reference errors
   (negative-tested). **Publication sweep:** no tracked file contains an absolute `C:\Users` path any more;
   `bin/`+`obj/` were already gitignored and untracked; the incidental username mentions in
   `BUILD_INSTRUCTIONS.txt`, `SESSION_14.md` and `HANDOFF.md` were genericised. Build-time only — the
   distributed zip ships no `.csproj`, and there is **no runtime path** anywhere, so players were never affected.
7. ~~**Target Vintage Story 1.22.0–1.22.3.**~~ **DONE (v0.2.22)** — `dependencies.game` is `"1.22.0"`, which
   VS reads as a MINIMUM, so all of 1.22.x is covered (there is no upper-bound syntax). ⚠️ **Declared, not
   verified:** the code was developed against 1.22.3 and no one has confirmed every API used exists in
   1.22.0. Smoke-test on a 1.22.0/1.22.1 install before relying on the range.

### B. Carried forward

1. **Large-guide follow-up only from a new measured bottleneck and fidelity-preserving design.**
   v0.3.43–v0.3.48's spatial/greedy path was rejected and v0.3.49 restored the v0.3.42 renderer. Preserve
   true scale, role colour/alpha, exact face plane/orientation/inset, guide-wide exposed-face occupancy,
   stable front/back visibility, world/cloud depth, uninterrupted materialization, and real-play FPS. See
   `SESSION_26.md`.
2. ~~**Fix the narrowed B-S9-1 adjacent-lock targeting residual.**~~ **CLOSED (v0.2.36).** Exact rendered-cell
   ownership removed the former-neighbor selection bias, and the human approved the result in play. Retain
   the interaction as regression coverage; do not reopen the passive-marker design without a new report.
3. ~~**The final F4/public multiplayer release check.**~~ **PASSED for v0.2.35.** Public/private multiplayer
   worked in the human's test. Release and wait for field reports; retain the full matrix as regression scope.
4. Remaining flagged decisions (11a–11r, 16a–16d in `SESSION_11.md`) are cosmetic — walk them
   opportunistically.
5. **Then, if asked:** **Roof / Tunnel** volumes; a concave-safe **Free-Shape fill**; broadcasting the whole
   Free-Shape draft chain to other players (11q); the **F3 re-constrain op**.

**Standing workflow rule (human-set — also in CLAUDE.md):** ship a NEW zip per code iteration into
`..\LayoutZips\`, but update docs / commit ONLY when the human says so. Warn before any context trim if
the docs are stale.

---

## Implemented in Session 16 (v0.2.10 → v0.2.21) — full detail in `SESSION_16.md`

**Mesh + polish arc.** All of it is committed and pushed on `main` (`6a48d2f` code, `b227d7d` docs).

- **Large-guide mesh Stage A — exposed-face meshing (0.2.14–0.2.16):** the Volumetric cube path now emits
  ONLY faces with no neighbour (per-voxel role colours preserved), cutting a hollow-shell guide to its skin.
  The z-fight inset was made exposed-only, then **solidity-aware** (`GuideMeshOptions.IsNeighborSolid`) so it
  never opens a seam between two guide voxels — only clears a guide face from a solid world block. A
  deterministic mesh-count harness locks the counts + flush/inset invariants (15/15).
- **Filled 3D volumes RETIRED (0.2.17):** volumes are always hollow shells now (GUI grey + server normalise +
  per-shape `filled=false` coercion; legacy filled saves auto-lighten, no data-version change). 2D fills intact.
- **Dome faces the clicked surface (0.2.11);** **whole-guide placement dust + pencil-icon fix (0.2.10);**
  ground **z-fight inset** tuned to `0.003` (0.2.10–0.2.13).
- **HUD hover no longer re-measures every tick (0.2.18);** **Divisions/Sides number fields aligned to the
  tile grid (0.2.18);** **draft cap-clamp restored (0.2.19);** **fifth High fill state (0.2.20)** — full=32 ·
  high 22–31 · medium 11–21 · low 1–10 · empty 0.
- **Refill channels — config + inventory-slot refill (0.2.21, protocol 4 → 5):** `allowHotbarChalkRefill` /
  `allowInventoryChalkRefill` server config (both default false; ground storage always allowed), synced to
  clients; inventory refill via a client MouseDown hook + the server-validated `ChalkInventoryRefillPacket`.

---

## Implemented in Session 15 (v0.2.0 → v0.2.9) — full detail in `SESSION_15.md`

**F5 delivered:** the tool is now the **Chalking Kit** — custom model, renamed item (mod stays Layout),
**32-chalk durability** (2D −1 / 3D volume −2, completed placements only; no refunds; NO lockout at 0 —
edit/dispel always work and the kit can never break), **Chalking Powder** refills (tap +4 / hold-to-pour;
hotbar or SHIFT+right-click a ground-stored kit in place), **private placements charge too** (client-reported
`ChalkChargePacket`, protocol 3 → 4; chalk-free only where physically unenforceable — servers without
Layout), **four deflating fill-state models** with progressively chalkier textures (full ≥22 · medium 11–21 ·
low 1–10 · empty 0) rendering in every context including ground storage, **ground storage** of the kit
(SHIFT+right-click since v0.2.37, idle-gated), chalk-puff particles on refill + placement, and the **chalk-line snap**
(bow-release twang) on placement. Recipes: `8× powder/flour + 0.1 L yellow dye (bucket/fired bowl/fired jug) → 8 powder`;
the kit = 8 powder + linen sack + flax twine + rope + copper nails. Config: `enableChalkDurability`
(default true); creative never consumes. Plan-vs-shipped deltas listed atop `PLAN_CHALKING_KIT.md`.

---

## Implemented in Session 14 (v0.1.53) — full detail and mesh handoff in `SESSION_14.md`

- Added `SphericalShellScan`: hollow Sphere/Dome work now follows shell area rather than scanning the full
  cubic bounding box. The exact legacy surface predicate and Dome half-space clip are preserved.
- Exact-equivalence validation passed for every voxel scale, off-grid centres, inverted domes, wall-facing
  domes, and threshold counts. A 20-block hollow dome produced 242,500 voxels in ~5 ms in isolation.
- Human playtest: a roughly 100-block hollow Sphere placed successfully and caused visible lag. This is a
  successful generator test and a confirmed renderer bottleneck.
- Filled Sphere/Dome still retain the old 4M candidate-cell scan guard. Do not raise the 10M actual-voxel hard
  ceiling before replacing the current one-complete-cube-per-voxel mesh path.
- v0.1.53 is built/packaged and good in play, but source/docs are uncommitted. Last pushed commit: `1461c19`.

---

## Implemented in Session 13 (v0.1.46 → v0.1.52) — full detail in `SESSION_13.md`

- **Threshold-aware 3D counting:** Box/Cone/Cylinder/Dome/Sphere cap checks can stop once the threshold is
  exceeded instead of building an entire rejected voxel set.
- **Natural drag cap:** an over-cap release safely reconciles, and client drag binary-search clamping stops
  at the largest accepted size without flicker. Playtest-confirmed.
- **Interaction restoration:** exact rendered-voxel first-hit lock picking, full geometry cache fingerprints,
  and complete pre-drag snapshots improve lock targeting and make right-click cancellation reliable.
- **Non-deforming Arch locks:** passive `IsLockMarker` control points do not alter the curve until deliberately
  dragged. DataVersion is now **8** and protocol is **3**. The human confirms lock placement no longer shifts.
- **v0.1.52 follow-up:** unlocked passive markers are removed and new lock/grab insertions preserve their
  position along the curve. This latest behavior is better but remains under wider playtest; B-S9-1 is not
  yet marked closed.

---

## Resolved in Session 12 (v0.1.28 → v0.1.45) — full detail in `SESSION_12.md`

F4 is implemented and playtested on `ClientOnlyFallback`: automatic local authority when the server lacks
Layout; policy-controlled private overlays on Layout servers; Hammer + Flax Twine vanilla-server activation;
the unchanged Layout tool on mixed servers; per-world/per-player persistence with backup recovery; normal
create/edit/reshape/settings/undo parity; `.layout dispel`; `/layout private`, `/layout public`, and
`/layout client push all`; private HUD/target indicators; mixed-server orange private anchors; ownership and
last-operation routing; and protocol-2 policy/mode/publication messages. DataVersion remains **7** and the
source count is **64**. See `PLAN_CLIENT_ONLY.md` for the final behavior matrix and architectural record.

The reported pre-drag restoration and lock/constraint oddities predate F4 and remain parked for a later
interaction-focused pass; they are not ClientOnlyFallback regressions.

---

## Resolved after Session 11's finalize (v0.1.20 → v0.1.27) — condensed; full detail in `SESSION_11.md`

The human pivoted to **3D volumes** (long parked as "LATER"), then iterated fixes. Committed to `main` and
pushed to GitHub through v0.1.27.

- **v0.1.20 Sphere · v0.1.21 Dome/Cylinder/Cone/Box** (the **3D volume family**, playtest-CONFIRMED):
  `GuideShapeTypes.IsVolume`; hollow = one-cell shell, filled = solid, via a **cell-lattice scan** (exact
  for sphere/box/dome, centre-banded for cylinder/cone) with a `MaxScanCells` **scan guard**. Sphere/Dome
  = 2-click; Cylinder/Cone/Box = 3-click (base + height, reusing `NeedsApexClick`). Always Volumetric;
  Surface + Divisions gated off. Wireframe targeting; deterministic up-axis. Own **3D catalog section**.
- **v0.1.22–0.1.23 (3D fixes, CONFIRMED):** `SetShape` whitelist fix (3D shapes no longer fall back to
  Arch); centred **2D/3D section labels**; catalog stays expanded after a pick; centred row labels;
  Divisions row hidden on volumes; **height-inversion fix** (`ShapeGeometry.BaseNormal` — axis stops
  flipping when the 2nd base point crosses sides); **free-air height** for Cylinder/Cone/Box.
- **v0.1.24–0.1.25 (client-lifecycle fixes, CONFIRMED):** GUI icons re-register per client start (fix
  blank tiles after exit-to-title → re-enter — was a process-static guard vs. a fresh `CustomIcons` dict);
  pinned favorites now persist (save-on-change + the real bug: `ObjectCreationHandling.Replace` so
  Newtonsoft stops appending saved pins to the default list, which `Normalize` then trimmed off); standalone
  Divisions field narrowed to the 76 px polygon width.
- **v0.1.26–0.1.27 (admin + safety, CONFIRMED through 0.1.26):** **`/layout dispel all`** and
  **`/layout dispel <chunk radius>`** (controlserver; namespaced under `/layout` in 0.1.27 so it can't
  clash); a **hard voxel ceiling** (`HardVoxelCeiling = 10M`) that rejects un-renderable giant guides
  regardless of caps — no more invisible guides silently maxing the world total; **clear in-game errors**
  on server-side placement rejection (were silent — the flash was tied to a not-yet-existent guide id); the
  running voxel total widened `int → long` (can't overflow-wrap negative on a caps-off server).

**No new persisted/wire fields** for the volumes — they reuse `ControlPoints` + `ShapePlaneAxis`; the
enum values are appended. DataVersion stays **7**. New files: `SphereShape`, `DomeShape`, `CylinderShape`,
`ConeShape`, `BoxShape` → **59 source files**.

---

## Resolved in Session 11 (v0.1.14 → v0.1.19) — condensed; full per-version detail in `SESSION_11.md`

The queued backlog, the Free-Shape, and a long GUI-polish loop — all playtest-CONFIRMED.

- **The Session-10 backlog (0.1.14):** B-S10-2 air-side bake fix; three-click triangle; **CTRL = cardinal
  constraint**, **SHIFT = draft-invert + placed-guide spring-back** (all shapes default "up"); **Polygon**
  (N-gon, 3–24 sides); auto-size tooltips; thinner Surface slabs. DataVersion 5 → **6**.
- **Favorites + Free-Shape (0.1.15):** hard-kept 4-slot favorites (star/unstar, never evict); the yellow
  **Current Shape chip**; SHIFT-centred triangle apex; the **Free-Shape** irregular polyline (chained
  clicks; click-last = open, click-first = close; body inserts like the arch; fill deferred). `IsClosed`
  → DataVersion **7**.
- **GUI polish (0.1.16–0.1.19):** Sides floor at 3; 5-wide catalog + separator; favorites as YELLOW glyphs;
  Projection+Fill and Divisions+Sides on shared rows; HUD Current Shape chip; Delete-mode "-ghost" tile
  greying; Free-Shape SHIFT = vertical segment; pin/unpin wording; Fill greyed on Free-Shapes; zips moved
  to `..\LayoutZips\`.

---

## Resolved in Session 10 (0.1.10 → 0.1.13) — full record in `SESSION_10.md`

Icon-tile UI pass (`LayoutToolIcons`, Cairo glyphs; native N×N scale icons); third **Edit** tool mode
(per-guide settings without the panel expanding); **B-S10-1 fixed** (Surface guides no longer sink behind
the block face on reload — unloaded-chunk-aware air probe + re-probe tick); divisions scroll-wheel on the
native number input, floored at 0; division markers pair on off-cell boundaries.

---

## OPEN BUGS

~~**B-S9-1 — Lock-in-place interaction regression.**~~ **RESOLVED in v0.2.36 and playtest-confirmed.**

The original symptoms were an adjacent voxel turning red and the Arch visibly shifting merely because a lock
was placed. The following fixes are now implemented:

- **Rendered-voxel first-hit picking:** the clicked cell, not nearest-point-on-curve, is authoritative.
- **Full geometry cache fingerprint:** post-drag targeting cannot reuse stale curve voxels.
- **Complete pre-drag snapshots:** right-click cancel restores points, constraints, soft flow, and inserted
  gesture state for both server and local authority.
- **Passive lock markers:** locking an Arch no longer inserts an active Catmull-Rom knot. The marker promotes
  only when deliberately dragged. Human-confirmed: placing a lock no longer shifts the guide.
- **Marker lifecycle/order (v0.1.52):** unlock removes passive markers; restored old data is cleaned; inactive
  markers are not adopted or targeted; later inserts are ordered by curve position so right-side grabs do not
  jump toward the apex.

v0.2.36 completed the fix: the first rendered voxel hit is authoritative, a point owns only its nearest
visible rendered marker cell, and an adjacent body voxel receives its own passive marker. The human approved
the lock → unlock → neighboring-lock behavior. Keep this as regression coverage.

~~**B-S10-2 — Surface→Volumetric bake grows the WRONG way (into the block).**~~ **FIXED in 0.1.14 and
PLAYTEST-CONFIRMED ("This was fixed" — human, same session).** The bake runs exactly the fix this entry
proposed: a server-side solidity probe mirroring the renderer's `CountSolidProbes` picks the air side, and
baked points land half a voxel into it (`GuideManager.ProbeAirSide`). (Was flagged item #15 — closed.)

---

## Requested next — human backlog (queued Session-10 end) — ✅ ALL DELIVERED IN 0.1.14

Every item below shipped in Session 11 (see the Resolved section above and `SESSION_11.md`; awaiting
playtest). Kept for the record of what was asked:

- **F1b — Polygon (N-gon).** Implement the regular-polygon shape (arbitrary side count) — the next catalog
  addition already sketched in F1 below. Same two-click gesture + a side-count control (like Divisions).
- **Triangle → THREE-click placement.** Change the triangle gesture from *2 clicks + a born apex* to
  **anchor · anchor · height-adjust** (place the base with two clicks, then a third click/drag sets the apex
  height). **Supersedes flagged decision #2** (the 2-click born-apex call) and touches `ShapeFactory` /
  `TriangleShape` / the draft state machine (`DraftManager` currently stores a single start point — a
  three-click draft needs a second stored point). Note: this makes the triangle the only non-two-click shape,
  reopening the "every shape is the same two-click gesture" settled decision — the human is explicitly asking.
- **Swap the SHIFT constraint to CTRL.** The cardinal/level snap (drafting the second foot; re-grabbing an
  anchor) currently rides **SHIFT** (`ShiftHeld()` in `GuideToolController`); move it to **CTRL**. Frees SHIFT
  for the next item. (Settled "SHIFT cardinal constraint" decision — human-reopened.)
- **New SHIFT function — context-dependent (spring-back + placement invert).** With SHIFT freed from the
  cardinal constraint (moved to CTRL above), it does two things depending on when it's held:
  - **During initial placement (drafting):** SHIFT **inverts the shape upside-down** — e.g. an arch opens
    downward instead of up; the height/apex is mirrored across the base. A live toggle on the ghost.
  - **After placement:** SHIFT **springs the shape back to its initially-placed form** (undo soft-flow / hand
    distortions back to the pristine geometry). One undo step.
  - **Prerequisite — all shapes default to "up."** Today some shapes (notably triangles) flip upside-down
    depending on which direction the base anchors are placed (the in-plane frame's perpendicular sign follows
    anchor order). Make the default orientation **consistently "up"** (world-up-biased) for every shape,
    regardless of placement direction; SHIFT-at-placement is then the *only* way to get the inverted
    orientation. Touches the apex/height derivation (`ShapeGeometry.TryGetFrame` / `InPlaneAxes` sign choice
    in `TriangleShape` and friends).
  - Open Qs to settle when built: "original" for spring-back = as-first-placed, or the last clean parametric
    form? Spring-back scope = whole guide or grabbed region only? Live-while-held vs one-shot snap?
- **Shape picker → 3 buttons + an "expand" arrow.** Reduce the initial shape row from 11 tiles to **3 shape
  buttons + a 4th arrow/▾ tile** that opens a submenu (fly-out or expanded grid) with the full catalog.
  Keeps the panel compact. (Reworks the `AddIconGrid` "shape" row in `GuideToolGui`.)
- **Favorites = the 3 initial slots (remove the Favorites strip).** Delete the Favorites placeholder row;
  instead, **starring a shape puts it into one of the 3 initial shape-picker slots** (from the submenu
  above). Persist per-player in `layout-client.json` as `{type + constraint}` triples. **Supersedes F2** and
  the Favorites-placeholder decision — the two GUI items (this + the picker rework) are one design.

**Two small tweaks the human added afterward:**

- **Hover tooltip box is too wide for its text.** The per-tile hover description reserves a lot of empty
  space. In `GuideToolGui.AddIconTile` the hover is `c.AddHoverText(hover, WhiteDetailText(), 200, bounds, key)`
  — the `200` is a fixed max width. **Fix:** either drop that width to fit (≈ the longest option name) or
  switch to `AddAutoSizeHoverText(...)` (confirmed to exist) so the box sizes to the text.
- **Surface voxel slabs too thick — reduce thickness by 75%.** `GuideRenderer.SurfaceSlabThicknessWorld`
  is currently `0.01f`; take it to **`0.0025f`** (¼ of current). Sanity-check afterward that the anti-z-fight
  `SurfacePlaneInset` (`0.004f`) still holds the thinner slab off the wall cleanly at scale 1
  (`t = min(thickness, edge − 2·inset)` keeps it valid, but eyeball it in play).

- ~~**Divisions scroll-wheel.**~~ **DONE — confirmed in play (0.1.12; floored at 0 in 0.1.13).** Dropdown
  removed. Attempt 1 (0.1.10: plain text field + dialog `OnMouseWheel` with a raw `Bounds.PointInside`
  hit-test) did not work — text inputs have no native wheel handler. Attempt 2: the field in
  `GuideToolGui.AddDivisionsControl` is the game's native **`GuiElementNumberInput`** (own built-in wheel +
  up/down spinner buttons; `IntMode = true`, `Interval = 1`); dialog-level fallback stays for
  hover-without-focus (`IsPositionInside`). 0.1.13: `OnDivisionsTyped` snaps the display back on clamp so the
  field can't go below 0 (or over `MaxDivisions`). GUI-only; `DivisionMarks` / packet / command untouched.

---

## Flagged decisions awaiting the human's review

Made under the standing "decide, note for review" rule; each is cheap to reverse. None block play.

**Session-11 additions — full text in `SESSION_11.md` §8 (11a–11j) and §11 (11k–11r):** spring-back
restores the as-placed POSITION too (11a) · spring-back vs a baked Surface exit (11b) · spring-back skips
the cap check (11c) · ~~newest pin evicts (11d)~~ superseded by the 0.1.15 hard-kept model · catalog folds
shut on select (11e) · legacy below-the-feet arches re-derive phantoms toward the body (11f) · sides not
restored by spring-back (11g) · draft right-click steps back per-click (11h) · invert is live-while-held
(11i) · N=3/N=4 polygons overlap the constraint tiles (11j) · chip at the far right of the Mode row, not
touching + (11k) · Free-Shape finish is position-based, ~1.5-cell snap (11l) · 64-corner cap (11m) · Fill
toggle inert on Free-Shapes (11n) · no auto-added 4th pin on upgrade (11o) · ★ badge catalog-only (11p) ·
others still see only your first chain corner (11q) · Free-Shape can hand-draw triangles/rectangles —
deliberate (11r).

**Session-10 additions (the icon UI pass; partially reviewed in play already):**

0f. **Edit mode is SELECT-ONLY** — clicking a guide in Edit selects it for the button-driven settings but
    does NOT grab/insert/lock (so a select-click can't accidentally reshape). All geometry editing stays in
    Create. Alternative if you want it: allow grabbing in Edit too, with body-click = select and point-click
    = grab to keep insert from firing on a select.
0a. **Divisions wheel works on hover** (point at the field and scroll), not only while focused — more
    discoverable than the original "while focused" spec. The native number input additionally responds
    when focused, and its spinner buttons are a third path.
0b. **2×2 scale icon stays mid-sized** while 4×4/8×8 run edge-to-edge (reading of the vanilla icons);
    the 16× icon is one solid square (human-confirmed choice).
0c. **Tile edge = 42 px**, label column 74 px — the "compact / native proportions" dial, one constant each.
0d. **Triangle constraint glyphs** rely on small geometry notation (right-angle mark, equal-side ticks);
    if illegible at tile size, differentiate by proportion instead.
0e. **Plane glyphs** (iso cube, active face filled) — N–S vs E–W legibility unproven in play.

**Session-9 additions:**

1. **Soft-flow regime split** (slave for interior grabs, shape-preserving for structural grabs). The decisive
   fix for "the apex acts like a pin." Human confirmed grabbing "MUCH better"; the split itself is Claude's
   call and can be revisited.
2. ~~**Triangle gesture: 2 clicks + a born apex.**~~ **Superseded (Session 11, 0.1.14):** the human reopened
   it and the THREE-click anchor·anchor·height placement is now built (Equilateral stays two-click).
3. **Right / Isosceles / Square have no break gesture in v1** — their constrained drags always absorb
   (slide/resize), so they never demote to the free parent. They live as separate catalog tiles (like
   circle/ellipse). Equilateral *does* break (apex drag → free triangle).
4. **Rectangle's derived corners are markers, not grabbable** — body clicks map to the nearest stored anchor.
5. **Division marks are magenta** (`VoxelRenderType.Division`) — the one hue distinct from the six existing
   roles. Open to review.
6. **Divisions type-in field applies per keystroke** with a changed-value guard — typing "12" briefly applies
   1 then 12, i.e. two sends / two undo steps on a selected guide. Acceptable v1; a commit-on-blur pass is the
   upgrade. (Superseded in part by the scroll-wheel request above, but the per-keystroke behavior of the
   remaining field still applies.)

**Session-8, still unreviewed (carried):**

7. **Ellipse body left-click → grab nearest handle** (parametric ring has nothing to insert).
8. **Ellipse body right-click → toggle nearest handle's lock.**
9. **New ellipse minor radius = ½ major** (visibly elliptical default; circles come from the Circle tile).
10. **Minor handle slides along its axis** (free-space drags project onto it).
11. **A placed guide's shape isn't menu-editable** — live guides reshape by grabbing, not menus (a
    menu-driven re-constrain op is possible future work — see F3). *(Session 10: Edit mode simply hides the
    shape picker rather than showing it read-only; the decision is unchanged.)*
12. ~~**The appended Selected-guide section itself.**~~ **Superseded (Session 10, 0.1.13):** the appended
    section is gone; per-guide editing now happens in the new **Edit mode**, reusing the main rows (see
    Resolved / flag 0f).
13. **Re-grab constraint reference = the guide's OTHER ANCHOR** (reproduces the drafting feel). *(Session
    11: the key is CTRL now; the reference-point decision itself is unchanged.)*
14. **Circle → ellipse is the break floor** — an ellipse does not break further into a free closed spline in
    v1 (terminate-at-ellipse).
15. ~~**Bake cell-side.**~~ **CLOSED (Session 11, 0.1.14):** became bug B-S10-2 and got exactly the proposed
    fix — the server-side air-probe bake (`GuideManager.ProbeAirSide`). Awaiting playtest.
16. **Proportional soft flow (structural-grab regime)** — deserves a stretch/shrink/rotate torture test on an
    arch with several inserted points.

---

## Known costs / tuning (deliberate, watch in real play)

- **Filled guides recount exactly per drag update** (cells generated each move packet). If big filled discs
  drag sluggishly → add a per-drag count cache. Correctness-first per the standing rule.
- **Settled Shells and persistent Wireframes use true selected scale.** Adaptive coarsening is motion-only;
  the cursor neighbourhood and final-click scaffold stay precise while exact Shell refinement streams.
- **`PreviewFullResVoxelCap` = 8,000** — the cheap/full-shell moving threshold. Tune only from playtest data;
  adaptive scale, work time, and frame pressure already provide secondary controls.
- **Immense materialization is streamed and cancellable.** Shape scans feed a capacity-three queue, and
  deterministic multi-seed 26-neighbour ordering creates exact torn/frayed growth. Current client targets:
  about 750 voxels per upload, 8–128 total batches, nominal 45 ms cadence, with frame-pressure backoff.
  Preserve exact final occupancy; never upload one voxel at a time or rebuild one growing mesh each frame.
- **Immense public validation is intentionally serialized.** One below-normal worker performs pure
  generation/counting; claim checks consume no more than 128 blocks or about 1 ms per 20 ms server tick.
  Longer build time is acceptable; server tick health is the priority.
- **Whole-guide + regional culling is conservative and measured.** A complete bound outside live
  `viewDistance`/camera frustum submits nothing; an intersecting immense clean Shell then tests fixed 32-block
  regions independently. Cross-region neighbours use the complete guide occupancy, so boundaries add no
  internal faces. The measured partial-view win is recorded in `SESSION_25.md`.
- **Division marks add a render-side pass** *(verified)* — `DivisionMarks.Apply` is called on every mesh
  rebuild in `GuideRenderer` (both the draft ghost and placed guides), walking `SampleCurve(128)` for arc
  length then a nearest-cell claim per boundary; cheap, but it does walk the cell list. Watch on very high
  division counts × large guides.
- **Ghost-greying** — if the API's toggle buttons expose an `Enabled` flag, native disabled state beats the
  alpha-ghost approach (`GuideToolGui` tile-row seam).
- Carried from Session 7: item transforms; recipe balance; scroll-wheel bindings (the divisions field is the
  first concrete use — see Deferred); Surface flatten's eventual move into the shape layer (`TODO(Surface)`).

---

## Feature ledger (delivered and future)

### F1. Remaining shape catalog — ✅ FIRST WAVE DONE (Session 9)
**Built this session:** Line, Triangle (+ Right / Equilateral / Isosceles), Rectangle (+ Square). *(Verified
against `GuideShapeType` = {Arch,Ellipse,Line,Triangle,Rectangle}, `ShapeConstraint` =
{None,SemiCircle,Circle,Right,Equilateral,Isosceles,Square}, and the shape files — all present.)*
**SECOND WAVE (Session 11): Polygon (regular N-gon, 3–24 sides) — DONE (0.1.14) + Free-Shape (0.1.15).**
**THIRD WAVE (post-Session-11): the 3D VOLUME family — DONE (0.1.20–0.2.38): Sphere, Dome, Cylinder,
Cone, Box, Tapered Cylinder, Polygonal Prism, and Tapered Polygonal Prism.** The "planar-only, 3D LATER"
decision has been **reopened and delivered** — see the resolved
section near the top. Natural next volumes if wanted: **Roof, Tunnel** (a walk-through extruded arch).

### F2. Favorites — ✅ DELIVERED AND CONFIRMED (Session 11, 0.1.15)
The picker has four hard-kept pinned slots + a ▾ catalog fold-out; right-click pins/unpins and never evicts an
existing favorite. Persisted per-player in `layout-client.json` as shape codes (each code = a
{type + constraint} pair).

### ★ MAJOR — F4. Client-only / server-less fallback mode — ✅ DELIVERED (v0.1.28–v0.1.45)

The implemented design deliberately differs from the original feasibility sketch: it uses a tangible
vanilla-item gate (Hammer offhand + Flax Twine main hand) on servers without Layout, persists private guides
per world/server + player UID, and retains normal locks/caps/undo semantics through a client-side instance of
the same `GuideManager`. On Layout servers, private mode is denied by default unless the server enables
`allowClientOnlyMode`; mixed-mode players keep using the real Layout tool. Public and private guides share
one renderer/controller, ownership routes edits, and last-operation authority routes undo/redo.

The finalized behavior matrix, command/config contract, persistence/recovery details, publication semantics,
protocol rules, and remaining validation are in `PLAN_CLIENT_ONLY.md`. The only remaining F4 work is the
release-candidate regression pass listed below.

### F3. Re-constrain op (idea, unrequested)
The inverse of a break: a menu action to snap a free shape back under a constraint (arch → half-circle,
ellipse → circle, triangle → equilateral, rectangle → square) with a best-fit. Natural undo pairing exists.
Park until asked.

### F6. "Move" tool mode — ✅ DELIVERED (Session 30, v0.3.86–v0.3.89)
A fourth tool mode beside Create · Edit · Delete. Select a guide, then slide the ENTIRE guide, its shape
untouched. **The motivating case: a guide sculpted over a long session that turns out to be one voxel off.**

**Shipped as:** an arrow pad in the GUI (steps of ×1/×2/×4/×8/×16 of the moved guide's own voxel; the four
horizontal arrows read from the player's facing snapped to the nearest world axis, Up/Down are world
vertical) plus a free-move toggle that drags the guide on the crosshair. One nudge or one drag is one undo
step. No chalk charged; claims re-checked at the destination.

**Plan-vs-shipped deltas — read these before touching it:**
- **The locked-point worry was wrong.** This entry claimed Session-20 adjacent locks "tie a point to a
  neighbouring guide" and recommended refusing to move a locked guide. **No such relationship exists** —
  `IsLocked`/`IsLockMarker` are plain per-control-point flags; B-S9-1 was a click-*targeting* defect, not
  stored data. Locks travel with the guide, which matters because a long-sculpted guide is exactly the one
  covered in them. (`UpdateControlPoints` does refuse locked points, so the translate needed its own seam.)
- **No modifier for a finer step.** A move must be a WHOLE number of the guide's own voxels; `TranslateGuide`
  enforces it and refuses anything finer, because a sub-voxel delta moves cells by a whole cell wherever it
  crosses a quantise boundary and by nothing elsewhere. That restriction is also what makes the count
  provably unchanged, so no cap check and no rescan of a behemoth.
- **`OriginalControlPoints` does move too**, as this entry required.
- The precedent used was `SpringBackCommand`/`OnSpringBack` (wholesale rewrite + full-state broadcast)
  rather than `RescaleGuideCommand`; `GuideManager.RestoreControlPoints` was the seam to copy.

Full record, including the two-round rendering defect it exposed, in `SESSION_30.md`.

### F12. Rotate a guide — ✅ DELIVERED (Session 31, v0.3.90)
Turn a whole guide about a vertical axis, in the Transform mode beside Move, Copy and Mirror. Raised while
naming that mode; the human "likes the idea", which is interest rather than a commitment.

**Expect this to be the hardest of the four, harder than Mirror.** The control points are trivial to rotate;
the orientation state around them is not. `ShapePlaneAxis`, `ProjectionPlane`, `FlatSideAligned` and the
apex/primary point of directional shapes (arch, tapered cylinder, cone, polygonal prism) all encode
orientation, and a naive point rotation leaves a turned shape whose settings still describe the old facing —
the same trap F7 documents, but worse, because mirror maps an axis to itself and rotation maps one axis onto
another.

Two constraints worth settling early:
- **90-degree increments only, about the vertical.** Arbitrary angles would put control points off the
  quantise lattice, so the rendered cells would no longer be a clean remap of the originals — the property
  that makes Move exact and cheap (see F6). At 90 degrees the lattice maps onto itself and a rotation is as
  exact as a translation. Free rotation is a much larger piece of work and probably should not be attempted.
- **About what centre?** The guide's own bounding centre is the obvious default; rotating about a picked
  anchor is the more useful behaviour when aligning to an existing build. Both are cheap; pick one in play.

### F11. Configurable voxel colour scheme — ✅ DELIVERED (SESSION_32, v0.4.8–v0.4.14)
Shipped as **Default / Red-Green Safe / Custom** on the settings page. Deltas from the plan below:

- **Deuteranopia-safe and protanopia-safe merged into one "Red-Green Safe" preset** — separate palettes would
  have differed only in ways neither group can see. High Contrast shipped in 0.4.8 and was **removed by the
  human in 0.4.11**; its scheme number (2) is retired, not reused.
- **"Prefer presets over pickers" was overruled by the human, correctly.** Presets answer "I cannot tell these
  roles apart"; they do not answer "yellow disappears against sandstone". **Custom** adds a seven-role table
  and a **sixteen-swatch grid** — a fixed grid rather than a free picker, because every swatch is guaranteed
  legible at guide alpha over stone and an arbitrary colour is not.
- ⚠ **The shared-static hazard was FIXED, not inherited.** The colour arrays became an immutable
  `GuidePalette` behind one static reference, and `BuildGuideMesh` reads that reference once per batch. A
  palette change can no longer produce a mesh built from two palettes.
- Far-anchor off-shades are **derived** from the chosen anchor (a quarter-step toward white), not picked, so
  the pair cannot drift apart. Grabbed stays white in every scheme.
- Custom colours persist as `"#RRGGBB"` in `layout-client.json`; a bad entry degrades one role, not the set.

**Not playtested:** Red-Green Safe's apex-as-purple is the biggest departure from the mod's established
colour language.

<details><summary>Original plan (retained)</summary>

Let players change the guide colour palette from the settings page. **Motivation is accessibility, not
taste:** the current palette can be unreadable for colour-blind players.

The concrete problem. Today's roles and hues (`GuideMeshBuilder`, "Colour table"):

| Role | Colour |
|---|---|
| Normal body | yellow |
| Locked point | **red** |
| Primary / apex | **green** |
| Anchor (aligned / far off-shade) | blue / indigo |
| Private anchor (aligned / far off-shade) | orange / burnt orange |
| Grabbed | white |
| Division mark | magenta |

**Red and green are the two that matter.** They mark locked points and apex points — different meanings,
both control-point markers, seen side by side — and red/green is exactly the pair that deuteranopia and
protanopia collapse (around 8% of men). A player with that deficiency cannot tell a locked point from an
apex. Yellow body vs orange private-anchor is a weaker second case; blue/indigo and orange/burnt-orange are
deliberately close (same role, off-shade) and are fine.

Implementation notes:
- **The plumbing precedent exists.** Alphas are already client-configurable through
  `GuideMeshBuilder.ConfigureOpacities()`, which writes the `[3]` slot of each colour array at client start.
  A sibling `ConfigureColors()` writing `[0..2]` follows the identical shape. The RGBs are currently
  `static readonly float[]` described in-code as "the settled colour language" — that comment is what this
  item overturns.
- **Prefer presets over six colour pickers.** A short list (default / deuteranopia-safe / protanopia-safe /
  high contrast) is far less UI and colour-blind-safe palettes are a solved design problem — Okabe-Ito is
  the usual starting point. Individual pickers can come later if anyone asks.
- **Colours are baked into vertex data**, so changing them rebuilds every guide, exactly like opacity. Reuse
  the debounce added for the v0.3.72 opacity slider (`QueueOpacityApply` in `GuideToolGui`) rather than
  rebuilding per interaction.
- ⚠ **Watch the shared-static hazard.** The colour arrays are static and are mutated in place, while
  materialization batches build on background threads. `ConfigureOpacities` is documented as "called once at
  client start, before any mesh is built" — the v0.3.72 opacity slider already breaks that assumption and
  can in principle produce one guide meshed with two palettes until the rebuild settles. Transient and
  probably invisible, but it should be handled properly rather than inherited.

Interacts with **F9** (the settings page this lives on) and with `PLAN_BLOCK_OCCUPANCY.md` §7.4, whose
"green is already taken" risk is softened considerably by a palette the player can change.

</details>

### F10. Redraw the settings gear glyph — ✅ DELIVERED (SESSION_32, v0.4.8–v0.4.11)
Shipped as a **spoked wheel-gear** drawn from a reference image the human supplied: 8 trapezoidal teeth with
rounded valleys, 6 spokes, a bored hub, phased half a tooth so a VALLEY sits at twelve and six o'clock.

- **Filled, not stroked** — the guess in the original note was right, and it is what makes teeth possible at
  icon size at all.
- **Three separate fill passes.** Rim, spokes and hub overlap; under one even-odd path every overlap cancels
  and the spokes punch holes through the hub.
- **`gearSize` 18 → 22 in `GuideToolGui`.** The spoked design does not survive 18 px — the spoke gaps close
  and the bore fills in. This was found by porting the glyph to GDI+ and rendering it at 18/22/28/40 px
  before shipping, which also caught 12 teeth being too many and an 8-tooth variant whose teeth were half
  again wider than its gaps.
- ⚠ **Radii are bounded by the Canvas zoom** (box 60 × 1.12): past ~26.8 from centre falls outside the tile.

### F7. Mirror / flip a guide — ✅ DELIVERED (Session 31, v0.3.93)
Reflect a guide so it changes handedness. Rides in the Transform mode.

**✅ DECIDED 2026-07-27: ONE button, mirroring about the guide's OWN CENTRE plane.**
Quarter turns about two axes already reach all 24 axis-aligned orientations; a reflection is the one thing
no rotation can produce, but adding **any single** reflection to that set generates all 48 — so three
per-axis mirror buttons would only add new ways to reach results that one button plus the rotate buttons
already reach. Mirroring about the guide's own centre also leaves it where it stands, so it composes with
move and rotate instead of displacing the guide as a side effect.

**The old "harder than F6, expect per-shape work" estimate predates F12 and no longer holds.** Rotate built
the entire scaffold: snapshot, transform points + `OriginalControlPoints` together, remap `ShapePlaneAxis`,
remap the Surface `Plane`, re-adopt the shape, recount, claim-check, roll back. `MirrorGuide` is
`RotateGuide` with a different point transform and no pivot ambiguity. Rotate also PROVED the load-bearing
assumption — that a volume's facing survives a transform, because its rise direction takes its sign from
which side the apex control point sits on.

**What mirror is actually for.** On a sphere, box, cylinder, dome, regular polygon, rectangle, circle or
ellipse a mirror is either a no-op or the same as a rotation. Its value is concentrated in **Free-Shapes,
scalene/right triangles, and anything hand-sculpted asymmetrically** — where a reflected copy genuinely
cannot be reached any other way. Real, but narrower than "mirror any guide".

### F8. Copy an existing guide — ✅ DELIVERED (Session 31, v0.3.93)
Duplicate a guide, presumably placing the copy offset by a step so it is immediately visible, then let F6
move it into position. **The cheapest of the three** — `GuideData.DeepClone()` already exists; a copy is a
clone with a fresh `Guid`. **F6 has shipped**, so the compose story is now real: copy, then nudge into
place. The Move mode's selection, arrow pad and grey-out behaviour are all reusable as-is.

**✅ DECIDED 2026-07-27: a copy CHARGES CHALK as a placement** — 2D −1 / 3D −2, exactly like any completed
placement, and it counts against the cumulative creator cap. Without that, copy is the obvious way to dodge
the Chalking Kit's durability entirely: place one guide, then duplicate it for free forever.
- **It can therefore be REFUSED** where move, rotate and mirror cannot (they are all free and can only be
  refused by a land claim). That refusal must read clearly, not fail silently.
- Follow the existing kit rule where they meet: the kit never LOCKS OUT at 0 chalk, so match whatever
  placement does at 0 rather than inventing a second policy for copy.

**Panel placement (agreed 2026-07-27):** Copy and Mirror get their own short labelled row BENEATH the pad,
not slots inside it. The pad means position and facing; these two differ in kind — one makes a new guide,
the other changes handedness — and the vertical column is better left with its free slot than filled with
an unrelated button.

```
[spin<] [away]   [spin>]    [Up]
[left]  [free]   [right]
[tip <] [toward] [tip >]    [Down]

Actions   [Copy]  [Mirror]
```

### F9. In-game settings panel in the GUI — ✅ DELIVERED (SESSION_32, v0.4.2–v0.4.14)
Absorbed **T2** as planned. Full record in `SESSION_32.md`; deltas from the plan:

- **Shipped:** the master on/off, guide opacity + Reset, the colour scheme (F11), Chiseling Highlight,
  Public/Private, Publish, and both chalk-refill shortcuts. All grouped under **Appearance** / **Behaviour**,
  all carrying hover text.
- **Deliberately NOT shipped:** `ShaderGuideBrightness`, `ShaderAmbientResponse`, `VoxelFrameStrength` and
  `ZFightInset`. They keep their `/layout` commands. These are tuning knobs for problems most players will
  never hit, and the page was already long — **a candidate if anyone asks for them.**
- **The `Default*` placement preferences were deliberately excluded.** They are ALREADY remembered
  automatically (the tool writes them back on shutdown), so a second place to set them would create two
  controls for one value with the tool page silently winning.
- The plan's open question — whether `ForceClientOnly` and the refill flags belong beside the appearance
  sliders — resolved as **yes, in their own Behaviour section**.
- "Reset to defaults" narrowed to **"Reset"** beside the opacity slider, plus **"Reset colors"** in the
  colour table. One page-wide reset would have implied it touched settings it never did.
- ⚠ **A settings page that gates itself needs an escape hatch.** The pre-existing rendering lockout closed
  the dialog when guides went off, stranding the player on the chat command; v0.4.3 changed the gate from
  BLOCK to DISABLE. Anything else added here that can turn itself off needs the same treatment.

### F5. Chalking-kit durability + refill loop — ✅ DELIVERED AND CONFIRMED (Session 15, v0.2.0–v0.2.9)
Shipped as designed with human-directed refinements during the build: 32 chalk, 2D −1 / 3D −2 on completed
placements only, no refunds, NO lockout at 0 (the kit can never break); **Chalking Powder** refills (tap +4 /
hold-to-pour, hotbar or ground-stored kit in place); **private placements charge chalk too** (the plan's
local-no-op survives only where unenforceable — servers without Layout); recipes `8× powder/flour + 0.1 L
yellow dye → 8` and the 8-powder kit craft; four deflating fill-state models (a fifth **High** state added in
v0.2.20); placement/refill effects. Plan-vs-shipped deltas atop `PLAN_CHALKING_KIT.md`; full record in
`SESSION_15.md`. **Held items now RESOLVED in Session 16:** the fifth High fill state (v0.2.20) and the
cursor-stack inventory refill (v0.2.21, server-config opt-in) both shipped — only the in-play verification of
the inventory refill remains (Top of the list).

---

## Next session — start here

**The agenda is "⭐ Top of the list" at the top of this file** — it is not repeated here. Current state:
**v0.4.14** is built, packaged and documented on `beta`. The Transform category (F6/F7/F8/F12) is complete,
and so is the settings page (F9/T2/F10/F11).

**The whole F-queue is empty.** What is left is **T1** (CTRL surface-snap on free-move — its contact rule is
already decided, only the implementation is missing), the nine flagged items in `SESSION_32.md` §9, the six
in `SESSION_31.md` §7, the Session-29 occupancy verification items, and the
`PROJECT_STATUS.md` → `HANDOFF.md` merge.

**Nothing from Session 32 has been playtested beyond the v0.4.4 crash report** — a field pass over the whole
settings page is the sensible next move before starting new work.

**Workflow reminders:** every code iteration ships a NEW `Layout<version>.zip` into `..\Layout Zips\`;
docs are updated ONLY when the human says so — and **`CHANGELOG.md` is part of that set** (it was missed at
the end of Session 29 and backfilled in Session 30); commits/pushes only when the human instructs. `main` is
the mainline.

---

## Resolved in Session 8 (ledger — do not reopen without cause)

| Item | Resolution |
|------|-----------|
| **B1** insert broken | Superseded + rebuilt: left-click body = insert+grab in one gesture; root targeting cause fixed separately. |
| **B2** White highlight blob | Single-voxel nearest-claim; radius constants deleted. |
| **B3** even-span apex | Apex claims 2 voxels at even spans, 0.35-cell tolerance. |
| **B4** fill inert | Tier 2: arch = curve closed by foot-to-foot chord; ellipse = disc; caps filled-aware. |
| **I1** opacity | All six type alphas client-configurable, defaults lowered. |
| **I2** right-click cancel | Universal cancel + `GuideCancelGrabPacket`; insert-born points removed on cancel. |
| **I3** GUI rework | Tile rows; initial-shape picker; Favorites placeholder; permanent main rows + Selected-guide section + Deselect. |
| **I4** more shapes | First wave: half-circle, circle, ellipse (primitives+constraints, absorb-or-break). **Polygon/line family added in Session 9 — see F1.** |
| **S8** near-anchor grabs | Fingerprint-cached sampled-curve targeting. |
| **S8** GUI "locked into edit mode" | Main rows permanent; per-guide section appended + Deselect. |
| **S8** draft coarsening stuck | Settled guides render at true scale; coarsening is draft-ghost-only. |
| **S8** SHIFT on re-grab | Cardinal constraint on anchor drags, referenced to the other anchor. |
| **S8** Surface→Volumetric mismatch + plane loss | Surface-exit bake (undoable) + stored-plane restore. |
| **S8** soft points still constraining | Proportional + frame-relative offsets. **NOTE: revisited again in Session 9 (slave-regime) — the S8 fix was not sufficient; see SESSION_9.md.** |
| **S8** Delete greying too subtle | Ghost fonts (~22% alpha), no lit tiles, input guard. |
| **SELECTION THREAD** | Clicking a guide in Create mode selects it for the Selected-guide section; Deselect clears; Delete suppresses. |

---

*End of TODO / outstanding-items handoff.*
