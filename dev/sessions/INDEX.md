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
2. **`SESSION_33.md`** — the admin arc, and §9's reasoning on why private guides are not capped.
3. **`SESSION_34.md`** — the most recent state, and §7's measured-text-height trap.
4. **`SESSION_16.md`** — Stage-A meshing, still the basis of how guides are built.
5. **`SESSION_15.md`** — the Chalking Kit, the mod's one resource system.

Do **not** read `SESSION_26.md` alone to plan renderer work — see its banner, and `GOTCHAS` R5.
