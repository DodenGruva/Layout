# Layout — session record index

> **Tier 3 — history. Append-only.**
>
> **This index is what makes 26 unread files cost nothing.** Read the index; read a session record only when
> a row points you at one. One row per session, newest first.
>
> **Session records are history and are never revised.** Where one turned out to be wrong, the correct
> treatment is to annotate it — see the correction banner on `SESSION_26.md` — never to rewrite it.
>
> **Wire** shows the *change* a session made, not the running total. "—" means neither DataVersion nor the
> protocol moved. **`dev/WIRE_HISTORY.md` is the authoritative ledger** — it was built by reading
> `RegistrationOrder()` in `PacketTypes.cs`, so where the two differ, it wins over this column.

| # | Versions | Wire | Summary |
|---|---|---|---|
| 43 | 0.4.72–0.4.73 | — | **Review-driven authority fixes and exact targeting.** Source verification found that an allegedly confusing empty-reshape rejection did not exist: ordinary and immense edits could commit a zero-voxel invisible guide. Both now roll back with Roundover-specific guidance. Unsupported body insertion is rejected in authority rather than allowing a parametric no-op to broadcast contradictory state. v0.4.72 also removes nearest-handle grab snapping: Arch/Free-Shape use the exact clicked body cell; parametric guides require the exact coloured marker voxel. v0.4.73 replaces the HUD's fixed 0.10/0.18-block halos with a scale-aware physical-cell envelope while exact clicks remain authoritative. The human tested the final result as “Excellent.” |
| 42 | 0.4.65–0.4.71 | proto 27 → 28 | **Roundover controls rebuilt through play iteration.** A GUI Radius field (v0.4.65) and block-probed inside/outside inference (v0.4.66) were both tried and removed. v0.4.67 establishes the exact profile with the first three clicks, then accepts an unsnapped open sweep path; legacy one-handle guides remain intact and the two-handle meaning moves public creation to protocol 28. v0.4.68–v0.4.69 add material-side point placement and a readable three-rail construction wireframe. v0.4.70–v0.4.71 add universal initial-placement modifiers: CTRL bypasses grabbing/existing guides, while SHIFT embeds the whole new draft. The human reported profile-first placement works “MUCH better.” |
| 41 | 0.4.55–0.4.64 | proto → 27 | **Independent review hardening, followed by adversarial review, Roundover Path, and exact guide registration.** Whole-guide operations now share coordinate/plane validation and transactional undo; immense cancellation reaches every expensive volume stage; structural claim snapshots are footprint-bounded while exact access checks remain unchanged. The review harness passes 29/29 groups. v0.4.60–v0.4.62 add the routed rolling-ball Roundover and its 52-check harness. v0.4.63 removes a legacy 0.003 camera pull that moved the otherwise exact guide lattice off Vintage Story's 1/16 cells; direct play confirmed the complete fix and stable 0.0001 clearance. v0.4.64 selects 0.0002 as the small-buffer default and records the invariant as G48. **Dedicated-server results for the claim changes remain unreported; the Roundover transition and voxel registration are play-confirmed.** |
| 40 | 0.4.50–0.4.54 | — | **`TODO` A18 delivered — guide serialisation is off the server main thread.** The tick deep-copies the registry (0.52 ms at 3,000 guides), a worker turns it into JSON (42 ms), and the bytes are handed back for the world's own save to write. **The trigger design is the human's** (§2): the game exposes no warning that a save is coming and no autosave interval — the period is a constant inside `VintagestoryLib.dll` — so the period is **measured** from the gap between two saves and the pass is aimed a short lead before the next. **The lead self-corrects** (§3): 3 s to start, doubling on evidence, because it is sized for prediction uncertainty and not for the work, which is ~80 ms. ⚠️ **Three review passes, four defects, and none of them in the threading** (§4) — all four were in the bookkeeping deciding whether a save had been prepared, including **a silent latch that could have stopped guide edits reaching disk for a whole server session** and **a null return with three meanings** (`GOTCHAS` **G43**) that got through *after* a guard for its own failure class had been added. §5 traces why the snapshot is genuinely private (`ControlPoint.SetPosition` mutates in place, so a shared `Vec3d` would have been a live race). ⚠️ §6: **bytes handed to `StoreData` reach disk only at the game's NEXT save, and after the last one there isn't one** — `GOTCHAS` **G44**. The plan's predicted "join obligation" was discharged by supersession instead. **Nothing here is playtested yet; v0.4.54 is the build to test.** |
| 39 | 0.4.44–0.4.49 | proto 24 → 26 | **`TODO` A10.2 closed — the performance review of the non-render code**, the last of the review backlog. **The headline defect: every edit re-serialised every guide in the world**, so a reshape drag did it ten times a second — 18 ms per update at 1,000 guides against a 20 ms tick, 55% of the server main thread at 3,000. Saves are now deferred and coalesced (**20× less work**), with the human setting the server's cadence to the world's own save. Also: arches rebuilt their spline up to 8,193 times per voxel count (§2); the Players roster was quadratic; guide-count caps never reached the HUD (found in play). ⚠️ **A claim pre-filter shipped and was withdrawn the same session** (§3) — geometry verified across 5,400 configurations, premise never checked: `TestAccess` has **seven** denial reasons and only one is land claims — `GOTCHAS` **R12** and **G40**. §4: claim protection **cannot be tested from singleplayer**, where the host is exempt. §6 lists five costs measured and deliberately not fixed; §8 lists four claims of mine corrected mid-session. ⚠️ **§9: `TODO` A17 (per-guide "index cards") was proposed, agreed and DISMISSED in the same session** — the save layer is one blob with no key enumeration and no delete (`GOTCHAS` **G42**), and its premise had already expired when v0.4.45 stopped saving per edit. Replaced by **A18**, moving serialisation off the main thread (a deep copy is ~80× cheaper than serialising). **Twice this session the half that looked obvious was the half nobody checked.** |
| 38 | 0.4.40–0.4.43 | — | **`TODO` A10.1 and A13 both closed** — the GUI layer read end to end (~8,000 lines; the six known traps all intact), and the doc-comment sweep finished. Four GUI defects fixed, led by the Players list going blank when a filter narrowed a long roster, and an aim loop that tested every guide in the world 33 times a second. **A regression shipped and was reverted the same session** (§3): a rewritten redraw guard could return early without clearing its own pending flag, latching the Players dialog dead after one click — `GOTCHAS` **R11**, and the trap as **G36**. The comment sweep turned up a live bug (§5): a hand-written `> Sphere` bound meant **seven of fifteen shapes were never remembered**, and the preference was overwritten on disk each load — **G38**. Chiselling highlights now light after a world load: a blind read is no longer cached — **G37**. |
| 37 | 0.4.34–0.4.39 | — | **`TODO` A14 emptied — all twelve review defects fixed**, in the agreed order, one shippable revision each. Cap refusals now name their cap; coordinates validated at all three untrusted sources (`GuideBounds`); the two immense races; one lock per player; rate limiting. **Two defects the reviews missed matter most:** the human found in play that a guide which voxelises to NOTHING passes every cap and is created invisible and silent (§7 — also A14.8's attack vector, reached by accident), and a self-review found the HUD cap row read REFUSED permanently in five shipped zips (§9). **A14.7 finally reproduced** (§4.1) — seven of eight shapes hang at 2^27, and it corrects three things G31 asserted, including that `BoxShape` does not hang. `GOTCHAS` G33–G35, R10. |
| 36 | — *(none shipped)* | — | **The adversarial code review (`TODO` A10.1), run against v0.4.33 — plus a second review by another model, evaluated and adopted in full.** **Twelve open defects**, led by a **P0: crafted coordinates hang the server** (scan guards bound a shape's size, never its position), and a client cap-warning event **no code has ever subscribed to**, so every edit refused for a cap fails silently. `GOTCHAS` G27–G32; findings in `TODO` A14. The two reviews overlap on nothing — correctness vs hostile-client. §6.1 records where this session's own analysis was wrong. **The GUI layer was not read.** |
| 35 | — *(none shipped)* | — | **Audit of the doc overhaul against source.** Five stale current-state claims in `ARCHITECTURE.md`, the chalk flags in the wrong config, a plan reading "not started" for delivered work; `GOTCHAS` R9 + G26; `DocCheck` 10 → 13 checks; **`main` levelled at v0.4.33**; the code-review brief |
| 34 | 0.4.28–0.4.33 | proto 23 → 24 | Session-33 polish queue in full, send-to-ground, momentary tile press feedback, the Players dialog made editable |
| 33 | 0.4.15–0.4.27 | **DV 12 → 13**, proto 19 → 23 | Admin server-settings section + Save flow, Players dialog, T1 surface snap, free-angle Rectangle/Box re-gesture, Reveal Near/All, private guides confirmed uncapped |
| 32 | 0.4.2–0.4.14 | — | The settings page: F9 panel + hover text, F10 gear glyph, F11 colour schemes with custom palette, sliding Public/Private, Publish |
| 31 | 0.3.90–0.4.0 | proto 17 → 19 | Transform completed — rotate, copy, mirror; the state-driven direction pad, span stepping, copy runs; diagnostic commands hidden |
| 30 | 0.3.86–0.3.89 | proto 16 → 17 | F6 Move mode: whole-guide translation, the arrow pad and free-move, materialization hold, graduated precision floor |
| 29 | 0.3.70–0.3.85 | — *(client-side only)* | Block-occupancy recolour, the face outset, sub-block world reads, live per-batch updates, the first GUI settings page |
| 28 | 0.3.55–0.3.69 | — | **The rendering arc, reopened and measured.** Vertex welding, settled-shell streaming, the custom guide shader (8.2 ms → 1.8 ms), voxel outlines. Corrects Sessions 25–27. |
| 27 | 0.3.53 | DV 12, proto 16 | Per-player cumulative-cap override; the final `main` release |
| 26 | 0.3.44–0.3.52 | — | Meshing experiments, renderer rollback, persistent visibility, cumulative creator cap. ⚠️ **Carries a correction banner — three claims disproved in Session 28.** |
| 25 | 0.3.41–0.3.43 | — | Measured frustum/spatial-culling experiment. Its renderer conclusion is superseded; the measurements stand. |
| 24 | 0.3.35–0.3.40 | — | Materialization completion, shell transitions, intrinsic HUD dimensions |
| 23 | 0.3.22–0.3.34 | DV 12, proto → 16 | Moderation, claim-aware guides, streamed immense-guide execution |
| 22 | 0.3.9–0.3.21 | DV 12, proto → 13 | Action-aware HUD, guide attribution, sculpting parity, projection transitions |
| 21 | 0.3.0–0.3.8 | DV → 11, proto → 11 | Adaptive large-guide drafting: motion wireframes, background refinement, bounded materialization, persistent Shell/Wireframe |
| 20 | 0.2.36–0.2.47 | DV → 9, proto → 9 | Polygonal prisms, precise adjacent locks, stage-aware help, flat-side/diagonal/rim modifiers |
| 19 | 0.2.29–0.2.35 | proto 7 | Tapered Cylinder stabilization, unlimited-cap semantics, adaptive draft throttling, whole-shape dust |
| 18 | 0.2.24–0.2.28 | proto 6 → 7 | The Tapered Cylinder (4-click frustum), plus the scan-guard and cap-clamp fixes it exposed |
| 17 | 0.2.22–0.2.23 | proto 5 → 6 | The seven-item backlog: refill config → client preference, the hard 32-chalk ceiling, publication readiness |
| 16 | 0.2.10–0.2.21 | proto 4 → 5 | **Stage-A exposed-face meshing**, z-fight insets, filled-volume retirement, High fill state |
| 15 | 0.2.0–0.2.9 | proto 3 → 4 | **The Chalking Kit (F5)** — reskin, ground storage, chalk durability and the refill loop |
| 14 | 0.1.53 | DV 8, proto 3 | Hollow-shell scaling and the mesh frontier — the handoff that set up Session 16 |
| 13 | 0.1.46–0.1.52 | DV 8, proto 3 | Optimization and guide-interaction correctness pass |
| 12 | 0.1.28–0.1.45 | proto → 2 | **ClientOnlyFallback (F4)** — client-only authority, then private guides alongside public ones |
| 11 | 0.1.14–0.1.27 | DV 5 → 7 | The backlog, the Free-Shape, GUI polish, **the 3D volume family** (Sphere, Dome), and hardening |
| 10 | 0.1.10–0.1.13 | — | First full UI refinement pass — the icon-driven panel; B-S10-1 fixed; divisions scroll-wheel |
| 9 | 0.1.0 | DV 4 → 5 | Line/Triangle/Rectangle shapes, division marks, the soft-flow regime split |

---

## Reading order for a newcomer

If you are picking this project up, these five carry the most that is still load-bearing:

1. **`SESSION_28.md`** — the rendering arc, and the corrections to Sessions 25–27. Read before any renderer
   work, together with `dev/GOTCHAS.md` G2.
2. **`SESSION_37.md`** — the most recent state. What the two reviews found, what fixing all of it actually
   took, and §4.1's reproduction, which corrects three claims made from source-tracing alone.
3. **`SESSION_33.md`** — the admin arc, and §9's reasoning on why private guides are not capped.
4. **`SESSION_16.md`** — Stage-A meshing, still the basis of how guides are built.
5. **`SESSION_15.md`** — the Chalking Kit, the mod's one resource system.

`SESSION_36.md` is the review that produced the backlog `SESSION_37.md` closes — read it only for the
*reasoning* behind a finding, never for current state.

Do **not** read `SESSION_26.md` alone to plan renderer work — see its banner, and `GOTCHAS` R5.
