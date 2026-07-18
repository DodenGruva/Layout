using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Layout.Guide;
using Layout.Network;
using Layout.Shapes;
using Layout.Systems;
using Layout.UI;

namespace Layout.Client
{
    /// <summary>
    /// The "tidy aim-controller": all client-side interaction logic for the held Layout tool, kept out of
    /// both the (stateless) item and the (thin) ModSystem. Runs a lightweight per-tick loop while the tool
    /// is held: raycasts the crosshair against the guide mirror, feeds the HUD seams
    /// (<see cref="GuideHud.SetExaminedGuide"/>, <see cref="GuideHud.SetDraftAim"/>), drives an active drag,
    /// and routes the item's left/right clicks by the current <see cref="ToolMode"/>.
    /// </summary>
    /// <remarks>
    /// EVERYTHING HERE IS GATED TO THE HELD-ITEM PATH (the §7 invariant): when the tool is not equipped the
    /// tick does nothing beyond equip detection, so placed guides are pure visuals that never steal targeting.
    ///
    /// COMATOSE GRABS (settled this module). Swapping away from the tool mid-grab does NOT release the point:
    /// the grab session is suspended — no more raycasts, no more move packets, HUD hidden — while the server-side
    /// edit lock (and the one-undo-entry drag session) simply persists, because we never send
    /// <c>GuideReleasePacket</c>. On re-equip the session validates itself against the mirror (guide still
    /// present, lock still ours — both can change while we're away, e.g. an admin override-dispel) and either
    /// resumes tracking the crosshair on the next tick or dissolves silently. Drafts survive a swap the same
    /// way (they always did, per §5); the GUI owns draft-cancel on MODE switch, this controller owns
    /// grab-release on mode switch.
    ///
    /// TARGETING MODEL. Guides have no engine selection boxes (deliberately), so "under the crosshair" is our
    /// own math against the mirror: distance from the view ray to each non-phantom control point (a point hit)
    /// and to each chord segment between consecutive visible points (a body hit — the insert target). Hidden
    /// guides expose only their anchors, matching their anchors-only rendering. This is control-polyline
    /// targeting, not voxel-exact — cheap, stable, and accurate where it matters (near points); revisit only
    /// if aiming at deeply-sagging curve bellies proves fiddly in play.
    ///
    /// LOCAL DRAG PREVIEW (the v2.2 changelog-5 "Module-7 concern", honoured here). During our own drag the
    /// grabbed point's mirror position is nudged in place each tick and that one guide is rebuilt, so the
    /// point tracks the crosshair at frame rate; throttled (~10 Hz) move packets go to the server, whose
    /// authoritative broadcasts land on the same mirror instance — the preview is simply ahead of, never
    /// diverging from, the mirror, and the release round-trip trues it up. This is the one sanctioned writer
    /// of mirror state outside the network handler, and it only ever touches the grabbed point's Vec3d.
    /// </remarks>
    public sealed class GuideToolController : IDisposable
    {
        private const int TickIntervalMs = 30;          // ~33 Hz aim loop while the tool is held
        private const int MoveSendIntervalMs = 100;     // ~10 Hz throttled move packets (the M4 figure)
        private const double MaxReach = 12.0;           // furthest a guide can be targeted, in blocks
        private const int PendingInsertTimeoutMs = 2000;
        private const int CapClampIterations = 12;      // sub-voxel precision across the maximum grab reach

        private readonly ICoreClientAPI _capi;
        private readonly DraftManager _draft;
        private readonly ClientNetworkHandler _net;
        private readonly GuideRenderer _renderer;
        private readonly GuideToolGui _gui;
        private readonly GuideHud _hud;

        private long _tickId;
        private bool _toolHeld;
        private ToolMode _lastMode;
        private bool _disposed;

        // --- Grab session (survives tool swaps; see remarks) ---------------------------------------
        private sealed class GrabSession
        {
            public Guid GuideId;
            public int PointIndex;
            public double Depth;                        // retained grab-depth for free-air interior moves
            public bool Suspended;                      // comatose: tool swapped away mid-grab
            public Vec3d LastSentPos;
            public long LastSendMs;
            public Vec3d OriginPos;                     // where the point was when grabbed (cancel snap-back)
            public bool WasInsert;                      // grab began as a body insert → cancel removes it
            public ShapeConstraint OriginConstraint;    // complete pre-gesture state for reliable cancel
            public List<ControlPoint> OriginPoints;

            /// <summary>
            /// Local soft-point preview: captured at grab time for EVERY grab on a free arch (the held
            /// point joins the baseline), so the apex and all other unlocked points flow in the drag
            /// preview exactly as the server will move them (same SoftPointFlow math — broadcast confirms).
            /// </summary>
            public SoftPointFlow SoftFlow;
        }
        private GrabSession _grab;

        /// <summary>
        /// True when the tool is at rest — no draft in progress and no grabbed point. Gates the
        /// ground-storage set-down gesture (CTRL+SHIFT+right-click) so it can never fire mid-edit.
        /// </summary>
        public bool IsIdle => !_draft.HasActiveDraft && _grab == null;

        // --- Targeting cache (Session-8 fix): per-guide sampled-curve polylines --------------------
        // The near-anchor grab bug, finally at the root: targeting used the CHORDS between control points,
        // but the rendered curve near an arch's feet is nowhere near its chord (the phantom design departs
        // the feet vertically while the chord angles straight at the apex) — so clicks on the visible body
        // near a foot missed every chord and fell through to "place a new draft anchor". Targeting now
        // tests the ACTUAL sampled curve. Sampling every guide at 33 Hz would be wasteful, so polylines are
        // cached per guide behind a cheap content fingerprint (point count + coordinate sum), recomputed
        // per tick per guide in O(points) and resampled only when the fingerprint moves.
        private readonly Dictionary<Guid, (ulong fingerprint, List<Vec3d> curve)> _curveCache
            = new Dictionary<Guid, (ulong, List<Vec3d>)>();

        // --- Pending insert (right-click sent; waiting to adopt the new point as a grab) -----------
        private bool _hasPendingInsert;
        private Guid _pendingInsertGuide;
        private Vec3d _pendingInsertPos;
        private long _pendingInsertMs;
        private ShapeConstraint _pendingInsertOriginConstraint;
        private List<ControlPoint> _pendingInsertOriginPoints;

        public GuideToolController(
            ICoreClientAPI capi,
            DraftManager draft,
            ClientNetworkHandler net,
            GuideRenderer renderer,
            GuideToolGui gui,
            GuideHud hud)
        {
            _capi = capi ?? throw new ArgumentNullException(nameof(capi));
            _draft = draft ?? throw new ArgumentNullException(nameof(draft));
            _net = net ?? throw new ArgumentNullException(nameof(net));
            _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
            _gui = gui ?? throw new ArgumentNullException(nameof(gui));
            _hud = hud ?? throw new ArgumentNullException(nameof(hud));

            _lastMode = draft.Mode;

            _net.LockStateChanged += OnLockStateChanged;
            _net.GuideRemoved += OnGuideRemoved;
            _net.GuideAddedOrUpdated += OnGuideAddedOrUpdated;
            _net.AuthorityModeChanged += OnAuthorityModeChanged;
            _capi.Input.InWorldAction += OnInWorldAction;

            _tickId = _capi.Event.RegisterGameTickListener(OnTick, TickIntervalMs);
        }

        // ==========================================================================================
        //  Equip detection + per-tick aim loop
        // ==========================================================================================

        /// <summary>True while the local player's active hotbar slot holds the Layout tool.</summary>
        public bool IsToolHeld()
        {
            var stack = _capi.World?.Player?.InventoryManager?.ActiveHotbarSlot?.Itemstack;
            return stack?.Collectible is Items.ItemGuideTool;
        }

        /// <summary>
        /// Networked worlds use the custom Layout item. Client-only worlds are active while Flax Twine is in
        /// the main hand and a vanilla Hammer is in the off-hand, matching normal held-tool behavior.
        /// </summary>
        public bool IsToolActive()
        {
            if (_net.ServerLayoutAvailable) return IsToolHeld();
            if (_net.AuthorityMode != ClientAuthorityMode.Local) return false;
            return ClientToolGate.HasRequiredItems(_capi.World?.Player);
        }

        /// <summary>
        /// F5 advisory pre-check: true when a NEW draft must be refused because the held Chalking Kit is
        /// empty. Chalk applies to public placements AND to private placements on a Layout server (private
        /// is private, not free — the server applies that charge from the client's report). The one
        /// chalk-free case is a server WITHOUT Layout, where no real kit item can even exist — the accepted
        /// client-only caveat. Creative/spectator players never consume. The server enforces the public
        /// gate authoritatively; if durability is disabled in its config, chalk never drops, so this check
        /// simply never trips.
        /// </summary>
        private bool OutOfChalkForNewDraft()
        {
            bool chalkApplies = _net.AuthorityMode == ClientAuthorityMode.Networked
                || (_net.AuthorityMode == ClientAuthorityMode.Local && _net.ServerLayoutAvailable);
            if (!chalkApplies) return false;

            EnumGameMode mode = _capi.World?.Player?.WorldData?.CurrentGameMode ?? EnumGameMode.Survival;
            if (mode == EnumGameMode.Creative || mode == EnumGameMode.Spectator) return false;

            var stack = _capi.World?.Player?.InventoryManager?.ActiveHotbarSlot?.Itemstack;
            if (!(stack?.Collectible is Items.ItemGuideTool)) return false;

            return Items.ItemGuideTool.GetChalk(stack) <= 0;
        }

        private void OnTick(float dt)
        {
            if (_disposed) return;

            bool held = IsToolActive();
            if (held != _toolHeld)
            {
                _toolHeld = held;
                OnHeldChanged(held);
            }
            if (!held) return;

            // Mode switched (via the GUI, which already cancels any draft): a live grab is released
            // properly so the lock never leaks across modes.
            if (_draft.Mode != _lastMode)
            {
                if (_grab != null && !_grab.Suspended) FinishRelease();
                _lastMode = _draft.Mode;
            }

            // Stale pending insert (packet lost / rejected) simply expires.
            if (_hasPendingInsert && _capi.World.ElapsedMilliseconds - _pendingInsertMs > PendingInsertTimeoutMs)
            {
                _hasPendingInsert = false;
                _pendingInsertOriginPoints = null;
            }

            UpdateAim();
        }

        // The custom Layout item normally owns click interception. Client-only mode deliberately uses
        // vanilla items, so consume the in-world actions here while (and only while) the local tool is active.
        private void OnInWorldAction(EnumEntityAction action, bool on, ref EnumHandling handled)
        {
            if (!on || !IsToolActive()) return;

            if (action == EnumEntityAction.InWorldLeftMouseDown)
            {
                OnPrimaryClick(_capi.World.Player.CurrentBlockSelection);
                handled = EnumHandling.PreventDefault;
            }
            else if (action == EnumEntityAction.InWorldRightMouseDown)
            {
                // Ground-storage set-down (CTRL+SHIFT+right-click, tool idle, REAL item held): step aside
                // instead of consuming, so the engine dispatches to ItemGuideTool.OnHeldInteractStart,
                // which delegates to the vanilla GroundStorable behavior. This hook otherwise consumes
                // right-clicks in BOTH modes (it fires before any held-item hook), so without this early
                // return the set-down gesture could never reach the item. Local mode is deliberately
                // excluded: its vanilla gate items keep the full F4 interception.
                if (IsGroundStoreSetDownGesture()) return;

                OnSecondaryClick(_capi.World.Player.CurrentBlockSelection);
                handled = EnumHandling.PreventDefault;
            }
        }

        /// <summary>
        /// True when this right-click should fall through to ground storage: the REAL Layout item is held
        /// (never the client-only vanilla gate), the tool is idle (no draft, no grab), and CTRL+SHIFT are
        /// down — read from the entity's interaction-modifier controls, the same source the vanilla
        /// GroundStorable behavior checks, so the two gates can never disagree.
        /// </summary>
        private bool IsGroundStoreSetDownGesture()
        {
            if (!IsToolHeld() || !IsIdle) return false;
            var c = _capi.World?.Player?.Entity?.Controls;
            return c != null && c.ShiftKey && c.CtrlKey;
        }

        private void OnHeldChanged(bool held)
        {
            if (held)
            {
                _hud.TryOpen();
                ResumeGrabIfStillValid();
                _lastMode = _draft.Mode;
            }
            else
            {
                // Suspend, don't end: the draft keeps its anchor, the grab keeps its lock (comatose).
                if (_grab != null) _grab.Suspended = true;

                _hud.ClearDraftAim();
                _hud.SetExaminedGuide(null);
                _hud.TryClose();
                if (_gui.IsOpened()) _gui.TryClose();
                _renderer.ClearDraftPreview();       // the DRAFT survives the swap; the live ghost does not
            }
        }

        // On re-equip a suspended grab either resumes (guide still present, lock still ours) or dissolves —
        // the guide may have been admin-dispelled, or our lock freed by a disconnect/reconnect, while away.
        private void ResumeGrabIfStillValid()
        {
            if (_grab == null) return;

            bool valid = _net.Guides.ContainsKey(_grab.GuideId)
                && _net.LockHolders.TryGetValue(_grab.GuideId, out string holder)
                && holder == MyUid;

            if (valid)
            {
                _grab.Suspended = false;   // the next tick's UpdateAim picks the point back up
            }
            else
            {
                DropGrabLocally();
            }
        }

        private void UpdateAim()
        {
            BlockSelection blockSel = _capi.World.Player.CurrentBlockSelection;

            // 1) Guide under the crosshair → HUD examine seam (id, lock status, count, cap bar).
            TargetHit hit = FindTarget(includeLockedPoints: true);
            _hud.SetExaminedGuide(hit.Found ? hit.GuideId : (Guid?)null);

            // 2) Live aim while drafting (Create mode): HUD readout + the world-space ghost of the guide
            //    that would be built. Two-click shapes aim their second foot; a three-click triangle whose
            //    base is already down aims its APEX instead (Session 11). The cardinal snap rides CTRL now;
            //    SHIFT while drafting live-inverts the ghost (upside-down arch / triangle).
            if (_draft.Mode == ToolMode.Create && _draft.HasActiveDraft)
            {
                // The volume HEIGHT stage works in free air too (0.1.23): a targeted block wins, but with
                // none the height handle follows the view ray. Every other stage still needs a block.
                if (blockSel != null || AwaitingVolumeHeight)
                {
                    Vec3d aim = blockSel != null ? ResolveAnchorPoint(blockSel) : FreeAirHeightAim();
                    if (DraftManager.IsChainShape(_draft.Shape))
                    {
                        // Free-Shape (0.1.15): the ghost is the placed chain + a live segment to the
                        // crosshair. CTRL snaps the segment level-and-cardinal off the LAST placed corner;
                        // SHIFT (0.1.16) snaps it VERTICAL (straight up/down from that corner). Aiming
                        // near the first corner (with ≥3 placed) snaps onto it and previews the CLOSED loop.
                        aim = ConstrainChainAim(aim);
                        bool closing = _draft.ChainCount >= 3
                            && Dist(aim, _draft.ChainFirst) <= ChainSnapRadius();
                        if (closing) aim = _draft.ChainFirst;
                        _hud.SetDraftAim(aim);
                        _renderer.SetDraftChainPreview(_draft.DraftChain, closing ? null : aim, closing,
                            BuildSettings(blockSel, aim));
                    }
                    else if (_draft.AwaitingApex)
                    {
                        // SHIFT while aiming a TRIANGLE apex (0.1.15): centre it on the base. The 3-click
                        // volumes (cylinder/cone/box) project the height onto their axis themselves, so
                        // SHIFT-centering doesn't apply to them.
                        if (ShiftHeld() && _draft.Shape == GuideShapeType.Triangle) aim = CenterApexOnBase(aim);
                        _hud.SetDraftAim(aim);
                        _renderer.SetDraftPreview(_draft.DraftStart, _draft.DraftSecond,
                            BuildSettings(blockSel, aim),
                            _draft.Shape, _draft.Constraint, _draft.DraftPlaneAxis,
                            sides: _draft.Sides, apex: aim);
                    }
                    else
                    {
                        if (CtrlHeld()) aim = ConstrainToStart(aim);
                        _hud.SetDraftAim(aim);
                        _renderer.SetDraftPreview(_draft.DraftStart, aim, BuildSettings(blockSel, aim),
                            _draft.Shape, _draft.Constraint, _draft.DraftPlaneAxis,
                            sides: _draft.Sides, inverted: ShiftHeld());
                    }
                }
                else
                {
                    _hud.ClearDraftAim();
                    _renderer.ClearDraftPreview();   // no valid aim → no ghost
                }
            }
            else
            {
                _renderer.ClearDraftPreview();       // no live draft (completed / cancelled / other mode)
            }

            // 3) Active drag: grabbed point follows the crosshair.
            if (_grab != null && !_grab.Suspended) UpdateDrag(blockSel);
        }

        private void UpdateDrag(BlockSelection blockSel)
        {
            if (!_net.Guides.TryGetValue(_grab.GuideId, out GuideData g)) { DropGrabLocally(); return; }
            if (_grab.PointIndex < 0 || _grab.PointIndex >= g.ControlPoints.Count) { DropGrabLocally(); return; }

            ControlPoint cp = g.ControlPoints[_grab.PointIndex];

            // Raycast targeting (§5): snap to a block when aimed at one; an interior point otherwise moves
            // freely in air at its retained grab-depth; an ANCHOR requires a block target — no target, no move.
            Vec3d target;
            if (blockSel != null)
            {
                target = ResolveAnchorPoint(blockSel);

                // CTRL re-grab constraint (Session-8 playtest fix; moved from SHIFT to CTRL in Session 11,
                // freeing SHIFT for invert/spring-back): dragging an ANCHOR with ctrl held constrains it
                // to the cardinal line through the guide's other anchor — the exact constraint drafting
                // offers, reintroduced after placement.
                if (cp.IsAnchor && CtrlHeld())
                {
                    Vec3d other = OtherAnchorOf(g, _grab.PointIndex);
                    if (other != null) target = ConstrainTo(other, target);
                }
            }
            else if (cp.IsAnchor)
            {
                return;                                  // anchor with no block target: hold position
            }
            else
            {
                Vec3d eye = EyePos();
                Vec3d dir = ViewDir();
                target = new Vec3d(
                    eye.X + dir.X * _grab.Depth,
                    eye.Y + dir.Y * _grab.Depth,
                    eye.Z + dir.Z * _grab.Depth);
            }

            // The mirror preview normally runs ahead of the authority. Without a local cap check it can
            // therefore cross the server's per-guide limit, get corrected by a resync, and cross it again
            // on the next frame while the cursor remains outside — visible as violent size flicker. Clamp
            // along this frame's drag segment before either previewing or sending, so the guide meets the
            // cap as a hard geometric boundary instead.
            target = ClampDragTargetToPerGuideCap(g, target);

            // Lock-in-place markers deliberately do not enter the spline when they are created. Once the
            // player actually moves this guide, promote them locally after cap validation and before the
            // preview; the authority performs the identical promotion in its first accepted move. Cancel
            // restores the pre-drag snapshot, including marker status.
            if (!NearlySame(cp.WorldPosition, target) && PromoteLockedMarkers(g))
            {
                ShapeFactory.Adopt(g).RecalculatePhantomPoints();
                _curveCache.Remove(g.Id);
            }

            // Local preview ahead of the mirror (see remarks): nudge the live mirror point in place and
            // rebuild just this guide. The White override is already active for this point.
            if (!NearlySame(cp.WorldPosition, target))
            {
                // Soft-point preview (Session 8): compute where the unlocked interior points flow to for
                // this nudge BEFORE moving the grabbed point (the capture-time mapping expects pre-move
                // reads for the untouched structural points), then apply grabbed + softs together and
                // rebuild once. Only the grabbed edit goes over the wire; the server recomputes the softs
                // with the same math and broadcasts the full batch.
                if (_grab.SoftFlow != null && _grab.SoftFlow.HasWork)
                {
                    foreach (var (sIdx, sPos) in
                             _grab.SoftFlow.ComputeReflowEdits(g.ControlPoints, _grab.PointIndex, target))
                    {
                        if (sIdx >= 0 && sIdx < g.ControlPoints.Count)
                            g.ControlPoints[sIdx].SetPosition(sPos.X, sPos.Y, sPos.Z);
                    }
                }
                cp.SetPosition(target.X, target.Y, target.Z);
                _renderer.SetGrabbedPoint(_grab.GuideId, _grab.PointIndex);   // rebuild with the nudged points
            }

            // Throttled authoritative updates (~10 Hz), only when the position actually changed.
            long now = _capi.World.ElapsedMilliseconds;
            if (now - _grab.LastSendMs >= MoveSendIntervalMs &&
                (_grab.LastSentPos == null || !NearlySame(_grab.LastSentPos, target)))
            {
                _net.SendMovePoint(_grab.GuideId, _grab.PointIndex, target);
                _grab.LastSentPos = new Vec3d(target.X, target.Y, target.Z);
                _grab.LastSendMs = now;
            }
        }

        private Vec3d ClampDragTargetToPerGuideCap(GuideData guide, Vec3d requested)
        {
            int configuredCap = _net.PerGuideVoxelCap;
            int cap = configuredCap > 0
                ? Math.Min(configuredCap, GuideManager.HardVoxelCeiling)
                : GuideManager.HardVoxelCeiling;

            if (PreviewFitsPerGuideCap(guide, requested, cap)) return requested;

            Vec3d current = guide.ControlPoints[_grab.PointIndex].WorldPosition;
            Vec3d low = new Vec3d(current.X, current.Y, current.Z);
            Vec3d high = requested;

            // Current should always be authoritative or a previously validated preview. If an unusual
            // packet ordering leaves it over-cap, do not search from a false premise; v0.1.47's release
            // reconciliation will restore the authority's copy.
            if (!PreviewFitsPerGuideCap(guide, low, cap)) return low;

            for (int i = 0; i < CapClampIterations; i++)
            {
                var mid = new Vec3d(
                    (low.X + high.X) * 0.5,
                    (low.Y + high.Y) * 0.5,
                    (low.Z + high.Z) * 0.5);

                if (PreviewFitsPerGuideCap(guide, mid, cap)) low = mid;
                else high = mid;
            }

            return low;
        }

        // Evaluates a drag candidate on an isolated control-point copy. This deliberately follows the
        // authority's operation order (constraint break, grabbed move, soft reflow) and uses the same
        // threshold counter, so an accepted preview position will also pass the server's per-guide check.
        private bool PreviewFitsPerGuideCap(GuideData guide, Vec3d candidate, int cap)
        {
            var points = new List<ControlPoint>(guide.ControlPoints.Count);
            foreach (ControlPoint point in guide.ControlPoints)
                points.Add(point == null ? new ControlPoint() : point.Clone());

            // Match GuideManager.UpdateControlPoints: the first actual move promotes passive lock markers
            // before reshaping, so cap acceptance includes the geometry that will really be committed.
            for (int i = 0; i < points.Count; i++)
                if (points[i].IsLockMarker && points[i].IsLocked)
                    points[i].IsLockMarker = false;

            IGuideShape shape = ShapeFactory.Adopt(
                guide.ShapeType, guide.Constraint, guide.ShapePlaneAxis, points,
                guide.Sides, guide.IsClosed);

            if (guide.Constraint != ShapeConstraint.None && shape.WouldBreakOnMove(_grab.PointIndex))
                shape.BreakConstraint();

            shape.MoveControlPoint(_grab.PointIndex, candidate);
            if (_grab.SoftFlow != null && _grab.SoftFlow.HasWork)
            {
                foreach (var (softIndex, softPosition) in
                         _grab.SoftFlow.ComputeReflowEdits(
                             guide.ControlPoints, _grab.PointIndex, candidate))
                {
                    if (softIndex >= 0 && softIndex < shape.ControlPoints.Count)
                        shape.MoveControlPoint(softIndex, softPosition);
                }
            }

            return GuideShapeVoxelCounting.CountUpTo(
                shape, guide.VoxelScale, guide.IsFilled, cap) <= cap;
        }

        // ==========================================================================================
        //  Click routing (called by ItemGuideTool, client side, local player only)
        //
        //  SESSION-8 CONTROL SCHEME (two modes; the old Edit/Lock modes folded into Create):
        //
        //  LEFT-CLICK (Create) — resolved by context, in priority order:
        //    1. Grabbing            → release (commit the drag; unchanged behaviour).
        //    2. Drafting            → place the second foot (an active draft outranks guide targeting, so
        //                             finishing an arch next to an existing guide can't grab it instead).
        //    3. Aiming at a POINT   → grab it. Precise hits only — no snap radius (the chisel ethos).
        //    4. Aiming at the BODY  → insert a control point there and grab it, one gesture. The player
        //                             never thinks "insert": every point of the guide is simply grabbable.
        //    5. Aiming at a block   → place the first draft anchor.
        //
        //  RIGHT-CLICK (Create) — the universal cancel, then lock:
        //    1. Grabbing            → cancel the grab (server restores the origin — or REMOVES the point
        //                             if this grab began as a body insert).
        //    2. Drafting            → discard the draft (first foot down = nothing committed yet).
        //    3. Aiming at a POINT   → toggle its lock (2× radius forgiveness, the Session-7 finding).
        //       Aiming at the BODY  → lock that exact spot: insert an on-curve control point there, born
        //                             locked, no grab. Any point of the guide can be locked — literally.
        //    4. Otherwise           → no-op (reserved).
        //
        //  DELETE — left-click dispels the guide under the crosshair; right-click is a no-op.
        // ==========================================================================================

        /// <summary>Left-click: release / place foot / grab point / grab body (implicit insert) / draft.</summary>
        public void OnPrimaryClick(BlockSelection blockSel)
        {
            if (!_toolHeld) return;

            if (_draft.Mode == ToolMode.Delete)
            {
                HandleDispelClick();
                return;
            }

            // 1. Grabbing → release.
            if (_grab != null && !_grab.Suspended)
            {
                FinishRelease();
                return;
            }

            // EDIT mode: click a guide to SELECT it (the GUI's setting rows then act on it); an empty click
            // deselects. SELECT-ONLY — no grab / insert / lock — so choosing a guide to change its settings
            // can never accidentally reshape it. All geometry editing (grab, insert, lock) stays in Create.
            if (_draft.Mode == ToolMode.Edit)
            {
                TargetHit editHit = FindTarget(includeLockedPoints: true);
                if (editHit.Found) _draft.SelectGuide(editHit.GuideId);
                else _draft.ClearSelection();
                return;
            }

            // 2. Drafting → the click is the second foot.
            if (_draft.HasActiveDraft)
            {
                HandleCreateClick(blockSel);
                return;
            }

            // 3./4. A guide under the crosshair outranks anchor placement.
            TargetHit hit = FindTarget(includeLockedPoints: false); // locked points are not grabbable
            if (hit.Found)
            {
                _draft.SelectGuide(hit.GuideId);                    // GUI's per-guide controls act on this

                if (LockedByOther(hit.GuideId))
                {
                    Error("layout-guidelocked", "That guide is being edited by another player right now.");
                    return;
                }

                // SHIFT+click on a placed guide (Session 11): SPRING BACK to its as-placed form instead
                // of grabbing — one undo step, server-restored. SHIFT is free for this now that the
                // cardinal constraint rides CTRL.
                if (ShiftHeld())
                {
                    _net.SendSpringBack(hit.GuideId);
                    return;
                }

                if (hit.PointIndex >= 0)
                {
                    StartGrab(hit.GuideId, hit.PointIndex);
                }
                else if (_net.Guides.TryGetValue(hit.GuideId, out GuideData bodyG)
                         && !TakesBodyInserts(bodyG.ShapeType))
                {
                    // DECISION (Session 8, generalised in Session 9): a body grab on a PARAMETRIC shape
                    // (ellipse family, line, triangle, rectangle, polygon) grabs the NEAREST HANDLE —
                    // pulling the outline stretches it, which is the natural feel; arbitrary interpolation
                    // points have no meaning on these shapes, so there is nothing to insert. The arch
                    // family (free splines) and the Free-Shape (hand-placed polyline; 0.1.15) DO take
                    // body inserts — see TakesBodyInserts.
                    int handle = NearestHandleIndex(bodyG, hit.BodyPos);
                    if (handle >= 0 && !bodyG.ControlPoints[handle].IsLocked) StartGrab(hit.GuideId, handle);
                }
                else
                {
                    BeginBodyInsert(hit);
                }
                return;
            }

            // 5. Nothing of ours under the crosshair → a Create draft-anchor click (Edit returned above).
            _draft.ClearSelection();
            HandleCreateClick(blockSel);
        }

        /// <summary>Right-click: cancel grab / cancel draft / toggle a point's lock, by context. Create only —
        /// geometry edits (lock, lock-in-place) live in Create; Edit is settings-only and Delete dispels.</summary>
        public void OnSecondaryClick(BlockSelection blockSel)
        {
            if (!_toolHeld || _draft.Mode != ToolMode.Create) return;

            // 1. Grabbing → cancel (restore origin, or remove the freshly-inserted point).
            if (_grab != null && !_grab.Suspended)
            {
                CancelGrab();
                return;
            }

            // 2. Drafting → step BACK one click (Session 11: a three-click draft first retracts its base
            //    end, back to "one anchor placed"; the next right-click — or any two-click draft — then
            //    discards the whole draft, exactly as before).
            if (_draft.HasActiveDraft)
            {
                bool draftSurvives = _draft.StepBackDraft();
                if (!draftSurvives)
                {
                    _net.SendDraftCancel();
                    _hud.ClearDraftAim();
                    _renderer.ClearDraftPreview();
                }
                return;
            }

            // 3. Idle → lock. A POINT hit toggles that point's lock; a BODY hit locks that exact spot —
            //    inserting a control point ON the curve there, born locked, no grab (Session-8 finding:
            //    "I should be able to lock any point I want", and every point of the guide IS a point).
            //    Points keep 2× forgiveness for toggling (the Session-7 unlock finding) — safe, because a
            //    near-miss now merely has to beat the point's radius before it means "insert a lock here".
            TargetHit hit = FindTarget(includeLockedPoints: true, pointsOnly: false, pointRadiusScale: 2.0);
            if (!hit.Found) return;
            if (!_net.Guides.TryGetValue(hit.GuideId, out GuideData g)) return;

            if (hit.PointIndex >= 0)
            {
                bool locked = g.ControlPoints[hit.PointIndex].IsLocked;
                _net.SendLockPoint(hit.GuideId, hit.PointIndex, !locked);
            }
            else if (!TakesBodyInserts(g.ShapeType))
            {
                // DECISION (Session 8, generalised in Session 9): lock-in-place has no meaning on a
                // parametric shape (ellipse ring, line, triangle, rectangle, polygon — no arbitrary points
                // can be born there), so a body right-click toggles the NEAREST HANDLE's lock instead —
                // the same "lock what I'm pointing at" intent, mapped to the points these shapes have.
                // The arch family and the Free-Shape fall through to genuine lock-in-place below.
                int handle = NearestHandleIndex(g, hit.BodyPos);
                if (handle >= 0)
                    _net.SendLockPoint(hit.GuideId, handle, !g.ControlPoints[handle].IsLocked);
            }
            else
            {
                // Session-9 batch-2 fix ("locking selects an adjacent voxel and warps the guide"): a
                // right-click aimed at a point's MARKER voxel can resolve as a BODY hit — the claimed
                // marker voxel sits up to a cell away from the true control point, and the body radius is
                // wider than the point radius — which used to fire a lock-in-place INSERT: a brand-new
                // locked point one voxel over, whose extra knot slightly warps the spline. Body hits
                // within ~1.5 voxels of an existing real point now convert to a lock toggle on THAT point.
                // Genuine lock-in-place (aimed clearly away from any point) is unchanged.
                // [Flagged: right-click only — left-click body near a marker still means "insert here"
                //  under the chisel-precision ethos; symmetric snap is one line if wanted.]
                // B-S9-1: forgiving curve-nearest targeting can name an adjacent cell that the view ray
                // never entered. Lock-in-place instead requires a real hit on a rendered outline voxel.
                if (!TryFindFirstGuideVoxelHit(g, out Vec3d lockCellCentre)) return;

                double snap = Math.Max(0.20, g.VoxelScale / 16.0 * 1.5);
                int nearPoint = NearestHandleIndex(g, lockCellCentre);
                if (nearPoint >= 0 &&
                    Dist(g.ControlPoints[nearPoint].WorldPosition, lockCellCentre) <= snap)
                {
                    _net.SendLockPoint(hit.GuideId, nearPoint, !g.ControlPoints[nearPoint].IsLocked);
                }
                else
                {
                    _net.SendInsertPoint(hit.GuideId, lockCellCentre, locked: true);
                }
            }
        }

        // Which shape families take BODY INSERTS (clicking between points creates a new point there):
        // the arch (free spline) and, since 0.1.15, the Free-Shape (hand-placed polyline — inserting a
        // corner mid-segment is exactly how you refine one). Every other shape is parametric: body
        // clicks map to the nearest handle instead.
        private static bool TakesBodyInserts(GuideShapeType t) =>
            t == GuideShapeType.Arch || t == GuideShapeType.FreeShape;

        // Nearest real (non-phantom) control point to a world position — the ellipse family's body-hit →
        // handle mapping.
        private static int NearestHandleIndex(GuideData g, Vec3d pos)
        {
            int best = -1; double bestD2 = double.MaxValue;
            for (int i = 0; i < g.ControlPoints.Count; i++)
            {
                if (g.ControlPoints[i].IsPhantom) continue;
                if (g.ControlPoints[i].IsLockMarker && !g.ControlPoints[i].IsLocked) continue;
                Vec3d p = g.ControlPoints[i].WorldPosition;
                double dx = p.X - pos.X, dy = p.Y - pos.Y, dz = p.Z - pos.Z;
                double d2 = dx * dx + dy * dy + dz * dz;
                if (d2 < bestD2) { bestD2 = d2; best = i; }
            }
            return best;
        }

        // Body hit in Create: insert a new control point there and adopt it as a grab, one gesture. The
        // server acquires the lock for us, inserts, and broadcasts lock + point; we adopt the new point in
        // OnGuideAddedOrUpdated. (Rebuilt from the old right-click insert — B1: watch the log line below in
        // play if adoption ever fails silently again.)
        private void BeginBodyInsert(TargetHit hit)
        {
            // Mark the adoption handshake before sending. Networked authority answers asynchronously, while
            // local authority answers in-process and may publish the inserted point before SendInsertPoint
            // returns; setting this first makes the same event-driven adoption work for both paths.
            if (_net.Guides.TryGetValue(hit.GuideId, out GuideData beforeInsert))
            {
                _pendingInsertOriginConstraint = beforeInsert.Constraint;
                _pendingInsertOriginPoints = CloneControlPoints(beforeInsert.ControlPoints);
            }
            else
            {
                _pendingInsertOriginConstraint = ShapeConstraint.None;
                _pendingInsertOriginPoints = null;
            }
            _hasPendingInsert = true;
            _pendingInsertGuide = hit.GuideId;
            _pendingInsertPos = new Vec3d(hit.BodyPos.X, hit.BodyPos.Y, hit.BodyPos.Z);
            _pendingInsertMs = _capi.World.ElapsedMilliseconds;
            _net.SendInsertPoint(hit.GuideId, hit.BodyPos);
            _capi.Logger.VerboseDebug("[Layout] body-insert sent for {0} at {1}", hit.GuideId, hit.BodyPos);
        }

        private void HandleCreateClick(BlockSelection blockSel)
        {
            // Base/anchor clicks REQUIRE a block target (§5); the volume HEIGHT stage is the exception —
            // it may complete in free air (0.1.23), the view ray standing in for the click point.
            if (blockSel == null && !AwaitingVolumeHeight) return;

            Vec3d anchor = blockSel != null ? ResolveAnchorPoint(blockSel) : FreeAirHeightAim();
            // The cardinal snap rides CTRL now (Session 11); it applies to the BASE clicks, not the apex.
            if (_draft.HasActiveDraft && !_draft.AwaitingApex && CtrlHeld()) anchor = ConstrainToStart(anchor);

            if (!_draft.HasActiveDraft)
            {
                // F5 chalk pre-check, at the FIRST click so no drawing effort is wasted: an empty kit
                // cannot start a new (public, server-authoritative) draft. Advisory only — the server
                // enforces the same gate on create. Local/private placements are chalk-free (F4 no-op),
                // creative mode never consumes, and everything except NEW placement stays open at 0.
                if (OutOfChalkForNewDraft())
                {
                    Error("layout-outofchalk",
                        "Out of chalk. Refill the Chalking Kit with Chalking Powder (hold it and right-click).");
                    return;
                }

                // The first click also fixes the INTRINSIC plane for the ellipse family (Session 8): click
                // the ground → a flat ring (normal Y); click a wall → a ring on the wall (normal X/Z). The
                // arch family carries it unused.
                _draft.StartDraft(anchor, AxisFromFace(blockSel));
                _net.SendDraftStart(anchor, BuildSettings(blockSel, anchor));
                return;
            }

            // FREE-SHAPE (0.1.15): every click chains another corner. Clicking the FIRST corner (≥3
            // placed) closes the loop; clicking the LAST placed corner again — the practical double-click —
            // finishes it open. CTRL snaps each new segment relative to the PREVIOUS corner.
            if (DraftManager.IsChainShape(_draft.Shape))
            {
                Vec3d corner = ConstrainChainAim(ResolveAnchorPoint(blockSel));

                double snap = ChainSnapRadius();
                bool onFirst = _draft.ChainFirst != null && Dist(corner, _draft.ChainFirst) <= snap;
                bool onLast = _draft.ChainLast != null && Dist(corner, _draft.ChainLast) <= snap;

                if (onFirst && _draft.ChainCount >= 3)
                {
                    CompleteChain(closed: true, blockSel, corner);
                }
                else if (onLast && _draft.ChainCount >= 2)
                {
                    CompleteChain(closed: false, blockSel, corner);
                }
                else if (onFirst || onLast)
                {
                    // Too few corners to finish either way — swallow the click rather than stacking a
                    // duplicate corner on top of an existing one.
                }
                else if (!_draft.AppendChainPoint(corner))
                {
                    Error("layout-freeshapecap",
                        $"Free-Shape corner limit reached ({Shapes.FreeShape.MaxCorners}). Finish or step back.");
                }
                return;
            }

            // THREE-CLICK TRIANGLE (Session 11): anchor · anchor · height. The second click stores the
            // base's far end (client-side only — nothing is committed yet); the ghost then shows the
            // triangle with its apex tracking the crosshair until the third click places it.
            if (!_draft.AwaitingApex && DraftManager.NeedsApexClick(_draft.Shape, _draft.Constraint))
            {
                _draft.PlaceSecondPoint(anchor);
                return;
            }

            Vec3d apex = null;
            Vec3d end = anchor;
            if (_draft.AwaitingApex)
            {
                apex = anchor;                       // the third click IS the apex / height
                if (ShiftHeld() && _draft.Shape == GuideShapeType.Triangle)
                    apex = CenterApexOnBase(apex);   // 0.1.15: SHIFT centres a triangle apex on the base
                end = _draft.DraftSecond;            // the base was fixed by the second click
            }

            // SHIFT at the completing click bakes the inverted (upside-down) form — only meaningful for
            // the shapes that derive an "up" (arch family, equilateral triangle); apex-clicked triangles
            // take their height from the click itself.
            bool inverted = apex == null && ShiftHeld();

            DraftCompletion completion = _draft.TryCompleteDraft(end, apex, inverted);
            if (completion.IsReady)
            {
                _net.SendCreateRequest(completion.Start, completion.End, BuildSettings(blockSel, anchor),
                    _draft.Shape, _draft.Constraint, _draft.DraftPlaneAxis,
                    inverted, _draft.Sides, completion.Apex);
                _draft.ClearDraft();
                _hud.ClearDraftAim();
                _renderer.ClearDraftPreview();       // now: the real guide arrives via broadcast
            }
            else if (completion.Status == DraftCompletionStatus.RejectedOverCap)
            {
                // Advisory pre-check against the server-synced cap; the draft stays so the player can adjust.
                Error("layout-overcap",
                    $"Too large: {completion.VoxelCount:n0} voxels (cap {completion.CapLimit:n0}). Aim closer or coarsen the scale.");
            }
        }

        // Finish a Free-Shape chain (0.1.15): cap pre-check, then the create request carrying the full
        // corner list + closed flag; the draft clears on a successful send like every other completion.
        private void CompleteChain(bool closed, BlockSelection blockSel, Vec3d lastAim)
        {
            DraftCompletion completion = _draft.TryCompleteChain(closed);
            if (completion.IsReady)
            {
                _net.SendCreateRequest(completion.Start, completion.End, BuildSettings(blockSel, lastAim),
                    _draft.Shape, _draft.Constraint, _draft.DraftPlaneAxis,
                    inverted: false, sides: 0, apex: null,
                    chain: _draft.DraftChain, closed: closed);
                _draft.ClearDraft();
                _hud.ClearDraftAim();
                _renderer.ClearDraftPreview();
            }
            else if (completion.Status == DraftCompletionStatus.RejectedOverCap)
            {
                Error("layout-overcap",
                    $"Too large: {completion.VoxelCount:n0} voxels (cap {completion.CapLimit:n0}). Step back or coarsen the scale.");
            }
        }

        // The click-forgiveness radius for finishing a Free-Shape on an existing corner: the lock-snap
        // formula, floored a touch higher (clicked cells resolve to identical snapped coords, so this only
        // has to absorb an adjacent-cell miss).
        private double ChainSnapRadius() => Math.Max(0.25, _draft.Scale / 16.0 * 1.5);

        // The Free-Shape's segment constraints, keyed off the LAST placed corner: SHIFT (0.1.16,
        // human-requested) pins the next segment VERTICAL — same X/Z as that corner, height from the aim;
        // CTRL keeps the 0.1.15 horizontal level-and-cardinal snap. SHIFT wins when both are held.
        private Vec3d ConstrainChainAim(Vec3d aim)
        {
            Vec3d reference = _draft.ChainLast ?? _draft.DraftStart;
            if (reference == null) return aim;
            if (ShiftHeld()) return new Vec3d(reference.X, aim.Y, reference.Z);
            if (CtrlHeld()) return ConstrainTo(reference, aim);
            return aim;
        }

        // SHIFT on the apex stage (0.1.15): project the aimed apex onto the base's perpendicular bisector
        // (the isosceles line) — "centred on the current base", height still from the aim.
        private Vec3d CenterApexOnBase(Vec3d aim)
        {
            Vec3d a = _draft.DraftStart, b = _draft.DraftSecond;
            if (a == null || b == null) return aim;
            if (!ShapeGeometry.TryGetFrame(a, b, _draft.DraftPlaneAxis, out _, out Vec3d m, out _))
                return aim;
            var mid = new Vec3d((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5, (a.Z + b.Z) * 0.5);
            double h = (aim.X - mid.X) * m.X + (aim.Y - mid.Y) * m.Y + (aim.Z - mid.Z) * m.Z;
            return new Vec3d(mid.X + m.X * h, mid.Y + m.Y * h, mid.Z + m.Z * h);
        }

        // (HandleEditClick and HandleLockClick are gone — Session 8 folded Edit and Lock into Create's
        //  contextual left/right clicks; see the routing block above.)

        private void HandleDispelClick()
        {
            TargetHit hit = FindTarget(includeLockedPoints: true);
            if (!hit.Found) return;

            // No client-side lock pre-check here: the server owns the full-exclusivity rule AND the admin
            // override, so a pre-check would wrongly stop an admin. Non-admins get the server's error.
            _net.SendDelete(hit.GuideId);
        }

        // ==========================================================================================
        //  Grab lifecycle
        // ==========================================================================================

        private void StartGrab(Guid guideId, int pointIndex)
        {
            if (!_net.Guides.TryGetValue(guideId, out GuideData g)) return;

            ShapeConstraint originConstraint = g.Constraint;
            List<ControlPoint> originPoints = CloneControlPoints(g.ControlPoints);

            // ABSORB-OR-BREAK, local half (Session 8): if this grab is one the constraint cannot absorb
            // (a circle's minor handle), clear the constraint on the MIRROR now so the drag preview
            // samples the free parent immediately — the server breaks authoritatively on the first move
            // packet and its full-state broadcast lands on the same value. Sanctioned mirror write, same
            // rule as the drag preview itself.
            if (g.Constraint != ShapeConstraint.None &&
                ShapeFactory.Adopt(g).WouldBreakOnMove(pointIndex))
            {
                g.Constraint = ShapeConstraint.None;
            }

            // Soft-point preview capture (Session-9 revision: EVERY grab on a free arch flows — the held
            // point joins the baseline and the other unlocked points, apex included, flow around the
            // gesture; mirrors the server exactly).
            SoftPointFlow softFlow = null;
            if (g.ShapeType == GuideShapeType.Arch && g.Constraint == ShapeConstraint.None
                && pointIndex >= 0 && pointIndex < g.ControlPoints.Count)
            {
                softFlow = SoftPointFlow.Capture(g.ControlPoints, pointIndex);
            }

            Vec3d at = g.ControlPoints[pointIndex].WorldPosition;
            _grab = new GrabSession
            {
                GuideId = guideId,
                PointIndex = pointIndex,
                Depth = Math.Max(0.5, Dist(EyePos(), at)),
                Suspended = false,
                LastSendMs = 0,
                OriginPos = new Vec3d(at.X, at.Y, at.Z),    // deep copy (the Vec3d aliasing rule)
                WasInsert = false,
                OriginConstraint = originConstraint,
                OriginPoints = originPoints,
                SoftFlow = softFlow
            };

            _net.SendGrab(guideId);                                 // server broadcasts the lock state
            _renderer.SetGrabbedPoint(guideId, pointIndex);         // optimistic White; revoked via lock event
        }

        // Session-8 right-click cancel: the anti-release. Restore the complete pre-gesture snapshot in the
        // local mirror immediately, then ask the authority to restore the same snapshot and release the
        // lock. This includes soft-flow points, an inserted body point, and any constraint broken by the
        // drag, so cancellation is atomic and does not wait for a network correction to look complete.
        private void CancelGrab()
        {
            if (_grab == null) return;

            if (_grab.OriginPoints != null &&
                _net.Guides.TryGetValue(_grab.GuideId, out GuideData g))
            {
                g.ControlPoints.Clear();
                foreach (ControlPoint point in _grab.OriginPoints)
                    g.ControlPoints.Add(point == null ? new ControlPoint() : point.Clone());
                g.Constraint = _grab.OriginConstraint;
                ShapeFactory.Adopt(g).RecalculatePhantomPoints();
                _curveCache.Remove(g.Id);
            }
            else if (!_grab.WasInsert && _grab.OriginPos != null &&
                     _net.Guides.TryGetValue(_grab.GuideId, out GuideData fallback) &&
                     _grab.PointIndex >= 0 && _grab.PointIndex < fallback.ControlPoints.Count)
            {
                ControlPoint cp = fallback.ControlPoints[_grab.PointIndex];
                cp.SetPosition(_grab.OriginPos.X, _grab.OriginPos.Y, _grab.OriginPos.Z);
                _curveCache.Remove(fallback.Id);
            }

            _net.SendCancelGrab(_grab.GuideId);
            DropGrabLocally();      // ClearGrabbedPoint rebuilds the guide — with the snapped-back point
        }

        // Lands the last position, releases the lock, and commits the whole drag as one undo entry (server-side).
        private void FinishRelease()
        {
            if (_grab == null) return;

            if (_net.Guides.TryGetValue(_grab.GuideId, out GuideData g) &&
                _grab.PointIndex >= 0 && _grab.PointIndex < g.ControlPoints.Count)
            {
                Vec3d final = g.ControlPoints[_grab.PointIndex].WorldPosition;
                Vec3d comparison = _grab.LastSentPos ?? _grab.OriginPos;
                if (comparison != null && !NearlySame(comparison, final))
                    _net.SendMovePoint(_grab.GuideId, _grab.PointIndex, final);
            }

            _net.SendRelease(_grab.GuideId);
            DropGrabLocally();
        }

        // Clears local grab state only — used when the grab ended without us (lock lost, guide gone) or
        // after an explicit release. Never sends anything.
        private void DropGrabLocally()
        {
            _renderer.ClearGrabbedPoint();
            _grab = null;
        }

        // ==========================================================================================
        //  Mirror events
        // ==========================================================================================

        private void OnLockStateChanged(Guid guideId, string holderUid)
        {
            // Our grab is only real while we hold the lock. A denial (someone else shown as holder) or a
            // force-clear both dissolve it.
            if (_grab != null && _grab.GuideId == guideId && holderUid != MyUid)
                DropGrabLocally();
        }

        private void OnAuthorityModeChanged(ClientAuthorityMode mode)
        {
            if (mode == ClientAuthorityMode.Detecting) return;
            if (_grab != null) DropGrabLocally();
            if (_draft.HasActiveDraft) _draft.ClearDraft();
            _hasPendingInsert = false;
            _pendingInsertOriginPoints = null;
            _hud.ClearDraftAim();
            _hud.SetExaminedGuide(null);
            _renderer.ClearDraftPreview();
        }

        private void OnGuideRemoved(Guid guideId)
        {
            if (_grab != null && _grab.GuideId == guideId) DropGrabLocally();
            if (_draft.SelectedGuideId == guideId) _draft.ClearSelection();
            if (_hasPendingInsert && _pendingInsertGuide == guideId)
            {
                _hasPendingInsert = false;
                _pendingInsertOriginPoints = null;
            }
        }

        // Adopt a freshly-inserted point as a grab: the server gave us the lock as part of the insert, so
        // the moment the guide update lands (with our lock confirmed), the nearest point to where we clicked
        // IS the inserted point (the shape contract) — grab it without a further SendGrab.
        private void OnGuideAddedOrUpdated(GuideData g)
        {
            if (!_hasPendingInsert || g.Id != _pendingInsertGuide) return;
            if (!_net.LockHolders.TryGetValue(g.Id, out string holder) || holder != MyUid)
            {
                // B1 diagnostic: if adoption ever fails silently again, this says why (lock not ours yet).
                _capi.Logger.VerboseDebug("[Layout] insert adopt deferred: lock holder is {0}", holder ?? "none");
                return;
            }

            int nearest = -1;
            double best = double.MaxValue;
            for (int i = 0; i < g.ControlPoints.Count; i++)
            {
                ControlPoint cp = g.ControlPoints[i];
                if (cp.IsPhantom || cp.IsLocked || cp.IsLockMarker) continue;
                double d = Dist(cp.WorldPosition, _pendingInsertPos);
                if (d < best) { best = d; nearest = i; }
            }

            ShapeConstraint originConstraint = _pendingInsertOriginConstraint;
            List<ControlPoint> originPoints = _pendingInsertOriginPoints;
            _hasPendingInsert = false;
            _pendingInsertOriginPoints = null;
            if (nearest < 0) return;

            Vec3d at = g.ControlPoints[nearest].WorldPosition;

            // Session-9 revision: the adopted grab flows too — this is THE canonical case ("insert between
            // the apex and an anchor, drag, and the apex should move like any other point"). The fresh
            // point joins the baseline; the apex and every other unlocked point flow around the drag.
            SoftPointFlow adoptedFlow = null;
            if (g.ShapeType == GuideShapeType.Arch && g.Constraint == ShapeConstraint.None)
                adoptedFlow = SoftPointFlow.Capture(g.ControlPoints, nearest);

            _grab = new GrabSession
            {
                GuideId = g.Id,
                PointIndex = nearest,
                Depth = Math.Max(0.5, Dist(EyePos(), at)),
                Suspended = false,
                LastSendMs = 0,
                OriginPos = new Vec3d(at.X, at.Y, at.Z),
                WasInsert = true,       // cancel removes this point rather than restoring it
                OriginConstraint = originConstraint,
                OriginPoints = originPoints,
                SoftFlow = adoptedFlow
            };
            _renderer.SetGrabbedPoint(g.Id, nearest);
            _capi.Logger.VerboseDebug("[Layout] body-insert adopted as grab: point {0} on {1}", nearest, g.Id);
        }

        // ==========================================================================================
        //  Hotkeys (delegated from the ModSystem; all gated to the active tool)
        // ==========================================================================================

        /// <summary>F: toggle the mode/settings GUI. Unhandled (falls through) when the tool isn't held.</summary>
        public bool OnToolGuiHotkey()
        {
            if (!IsToolActive()) return false;
            if (_gui.IsOpened()) _gui.TryClose(); else _gui.TryOpen();
            return true;
        }

        /// <summary>Ctrl+Z: undo. Only consumed while the tool is held, so it never hijacks other UIs.</summary>
        public bool OnUndoHotkey()
        {
            if (!IsToolActive()) return false;
            _net.SendUndo();
            return true;
        }

        /// <summary>Ctrl+Y: redo. Same held-tool gate as undo.</summary>
        public bool OnRedoHotkey()
        {
            if (!IsToolActive()) return false;
            _net.SendRedo();
            return true;
        }

        // ==========================================================================================
        //  Targeting math (control-polyline model — see remarks)
        // ==========================================================================================

        private readonly struct TargetHit
        {
            public readonly bool Found;
            public readonly Guid GuideId;
            public readonly int PointIndex;   // >= 0: a control-point hit; -1: a body (chord) hit
            public readonly Vec3d BodyPos;    // for a body hit: the closest point on the chord

            public TargetHit(bool found, Guid guideId, int pointIndex, Vec3d bodyPos)
            {
                Found = found; GuideId = guideId; PointIndex = pointIndex; BodyPos = bodyPos;
            }

            public static readonly TargetHit None = new TargetHit(false, Guid.Empty, -1, null);
        }

        // Nearest guide feature under the crosshair within reach: control points win over body chords at
        // equal distance (points get a slightly larger radius). Hidden guides expose only their anchors.
        // pointsOnly skips body chords entirely (lock-toggle targeting); pointRadiusScale widens the pick radius.
        private TargetHit FindTarget(bool includeLockedPoints, bool pointsOnly = false, double pointRadiusScale = 1.0)
        {
            Vec3d origin = EyePos();
            Vec3d dir = ViewDir();

            TargetHit bestHit = TargetHit.None;
            double bestScore = double.MaxValue;

            foreach (GuideData g in _net.Guides.Values)
            {
                double voxel = g.VoxelScale / 16.0;
                // Session-8 finding: the old max(0.30, voxel*1.75) radius cast a ~⅓-block shadow around
                // every control point in which body clicks were swallowed by the point — a dead zone for
                // body grabs near the anchors, and a snap radius the control scheme explicitly doesn't
                // want. The point pick radius now matches the single-voxel marker you can actually SEE
                // (with a small floor for the finest scale): hit the voxel to grab the point; everywhere
                // else on the guide — everywhere — is a body hit.
                double pointRadius = Math.Max(0.10, voxel) * pointRadiusScale;
                double bodyRadius = Math.Max(0.18, voxel);

                for (int i = 0; i < g.ControlPoints.Count; i++)
                {
                ControlPoint cp = g.ControlPoints[i];
                if (cp.IsPhantom) continue;
                if (cp.IsLockMarker && !cp.IsLocked) continue;

                    bool pointTargetable = (!g.IsHidden || cp.IsAnchor) && (includeLockedPoints || !cp.IsLocked);
                    if (pointTargetable &&
                        RayPointDistance(origin, dir, cp.WorldPosition, out double dPoint, out double tPoint) &&
                        dPoint <= pointRadius && tPoint <= MaxReach)
                    {
                        double score = tPoint - 0.10;    // slight bias: a point beats the body it sits on
                        if (score < bestScore)
                        {
                            bestScore = score;
                            bestHit = new TargetHit(true, g.Id, i, null);
                        }
                    }
                }

                // Body targeting against the SAMPLED CURVE (Session-8 fix — see _curveCache). Only on
                // visible guides (hidden = anchors only) and never in points-only mode.
                if (!pointsOnly && !g.IsHidden)
                {
                    List<Vec3d> curve = GetCurvePolyline(g);
                    for (int sIdx = 1; sIdx < curve.Count; sIdx++)
                    {
                        if (RaySegmentDistance(origin, dir, curve[sIdx - 1], curve[sIdx],
                                out double dBody, out double tBody, out Vec3d onSeg) &&
                            dBody <= bodyRadius && tBody <= MaxReach)
                        {
                            if (tBody < bestScore)
                            {
                                bestScore = tBody;
                                bestHit = new TargetHit(true, g.Id, -1, onSeg);
                            }
                        }
                    }
                }
            }

            return bestHit;
        }

        // Exact lock-in-place picker for B-S9-1. It intentionally samples the outline even when the guide
        // is filled: body targeting means the defining curve, not arbitrary interior fill cells. This work
        // happens only on a right-click, never in the per-tick targeting loop.
        private bool TryFindFirstGuideVoxelHit(GuideData guide, out Vec3d cellCentre)
        {
            cellCentre = null;
            if (guide?.ControlPoints == null || guide.ControlPoints.Count < 2) return false;

            GuideData copy = guide.DeepClone();
            IGuideShape shape = ShapeFactory.Adopt(copy);
            shape.RecalculatePhantomPoints();
            List<VoxelPosition> voxels = shape.GetVoxelPositions(guide.VoxelScale, filled: false);
            if (voxels.Count == 0) return false;

            Vec3d origin = EyePos();
            Vec3d dir = ViewDir();
            double edge = guide.VoxelScale / 16.0;
            double half = edge * 0.5;
            bool surface = guide.Projection == ProjectionMode.Surface;
            PlaneAxis flatAxis = guide.Plane.FlattenedAxis;
            double plane = guide.Plane.PlaneOffset / 16.0;
            double surfaceHalfThickness = Math.Min(half, 0.02);

            // Raw outline cells can collapse onto one Surface slab, so test each visible tangential cell once.
            HashSet<(int, int)> surfaceCells = surface ? new HashSet<(int, int)>() : null;
            double bestT = double.MaxValue;
            double bestCentreDistance = double.MaxValue;

            foreach (VoxelPosition voxel in voxels)
            {
                double minX = voxel.X / 16.0;
                double minY = voxel.Y / 16.0;
                double minZ = voxel.Z / 16.0;
                double maxX = minX + edge;
                double maxY = minY + edge;
                double maxZ = minZ + edge;
                double cx = minX + half;
                double cy = minY + half;
                double cz = minZ + half;

                if (surface)
                {
                    (int, int) key = flatAxis == PlaneAxis.X ? (voxel.Y, voxel.Z)
                        : flatAxis == PlaneAxis.Y ? (voxel.X, voxel.Z)
                        : (voxel.X, voxel.Y);
                    if (!surfaceCells.Add(key)) continue;

                    if (flatAxis == PlaneAxis.X)
                    {
                        minX = plane - surfaceHalfThickness; maxX = plane + surfaceHalfThickness; cx = plane;
                    }
                    else if (flatAxis == PlaneAxis.Y)
                    {
                        minY = plane - surfaceHalfThickness; maxY = plane + surfaceHalfThickness; cy = plane;
                    }
                    else
                    {
                        minZ = plane - surfaceHalfThickness; maxZ = plane + surfaceHalfThickness; cz = plane;
                    }
                }

                if (!RayAabbEntry(origin, dir, minX, minY, minZ, maxX, maxY, maxZ, out double t)
                    || t > MaxReach) continue;

                var centre = new Vec3d(cx, cy, cz);
                RayPointDistance(origin, dir, centre, out double centreDistance, out _);
                if (t < bestT - 1e-7
                    || (Math.Abs(t - bestT) <= 1e-7 && centreDistance < bestCentreDistance))
                {
                    bestT = t;
                    bestCentreDistance = centreDistance;
                    cellCentre = centre;
                }
            }

            return cellCentre != null;
        }

        // Slab-method ray/AABB intersection. Returns the first non-negative distance along a normalized ray.
        private static bool RayAabbEntry(
            Vec3d origin, Vec3d dir,
            double minX, double minY, double minZ, double maxX, double maxY, double maxZ,
            out double entry)
        {
            double tMin = 0.0;
            double tMax = double.MaxValue;

            if (!ClipRayAxis(origin.X, dir.X, minX, maxX, ref tMin, ref tMax)
                || !ClipRayAxis(origin.Y, dir.Y, minY, maxY, ref tMin, ref tMax)
                || !ClipRayAxis(origin.Z, dir.Z, minZ, maxZ, ref tMin, ref tMax))
            {
                entry = double.MaxValue;
                return false;
            }

            entry = tMin;
            return tMax >= tMin;
        }

        private static bool ClipRayAxis(
            double origin, double direction, double min, double max, ref double tMin, ref double tMax)
        {
            const double epsilon = 1e-12;
            if (Math.Abs(direction) <= epsilon) return origin >= min && origin <= max;

            double a = (min - origin) / direction;
            double b = (max - origin) / direction;
            if (a > b) { double swap = a; a = b; b = swap; }
            if (a > tMin) tMin = a;
            if (b < tMax) tMax = b;
            return tMax >= tMin && tMax >= 0.0;
        }

        // Cached dense polyline of a guide's visible curve; resampled only when its full geometry fingerprint
        // changes. Hashing every coordinate separately prevents stale targeting when multi-point drag deltas
        // cancel each other out in a simple coordinate sum.
        private List<Vec3d> GetCurvePolyline(GuideData g)
        {
            ulong fp = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            fp = unchecked((fp ^ (uint)g.ControlPoints.Count) * prime);
            for (int i = 0; i < g.ControlPoints.Count; i++)
            {
                Vec3d p = g.ControlPoints[i].WorldPosition;
                fp = unchecked((fp ^ (ulong)BitConverter.DoubleToInt64Bits(p.X)) * prime);
                fp = unchecked((fp ^ (ulong)BitConverter.DoubleToInt64Bits(p.Y)) * prime);
                fp = unchecked((fp ^ (ulong)BitConverter.DoubleToInt64Bits(p.Z)) * prime);
                fp = unchecked((fp ^ (g.ControlPoints[i].IsPhantom ? 1UL : 0UL)) * prime);
                fp = unchecked((fp ^ (g.ControlPoints[i].IsLockMarker ? 1UL : 0UL)) * prime);
            }
            fp = unchecked((fp ^ (uint)g.Constraint) * prime);
            fp = unchecked((fp ^ (uint)g.Sides) * prime);
            fp = unchecked((fp ^ (g.IsClosed ? 1UL : 0UL)) * prime);

            if (_curveCache.TryGetValue(g.Id, out var entry) && entry.fingerprint == fp)
                return entry.curve;

            // Sample density: enough that segment error is far below the body radius, scaled with curve
            // complexity. 24 samples per real control point, floored at 64, capped sanely.
            int realPoints = 0;
            for (int i = 0; i < g.ControlPoints.Count; i++)
                if (!g.ControlPoints[i].IsPhantom) realPoints++;
            int samples = Math.Max(64, Math.Min(512, realPoints * 24));

            List<Vec3d> curve = ShapeFactory.Adopt(g).SampleCurve(samples);
            if (_curveCache.Count > _net.Guides.Count * 2 + 8) _curveCache.Clear();  // lazy prune of deleted ids
            _curveCache[g.Id] = (fp, curve);
            return curve;
        }

        // Distance from a ray to a point; t = distance along the ray to the closest approach (>= 0 required).
        // Component form throughout, matching the codebase's math style (no Vec3d operator dependencies).
        private static bool RayPointDistance(Vec3d origin, Vec3d dir, Vec3d p, out double dist, out double t)
        {
            double wx = p.X - origin.X, wy = p.Y - origin.Y, wz = p.Z - origin.Z;
            t = wx * dir.X + wy * dir.Y + wz * dir.Z;
            if (t < 0) { dist = double.MaxValue; return false; }

            double cx = origin.X + dir.X * t, cy = origin.Y + dir.Y * t, cz = origin.Z + dir.Z * t;
            double dx = p.X - cx, dy = p.Y - cy, dz = p.Z - cz;
            dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            return true;
        }

        // Closest approach between a ray and a segment [a,b]; returns the segment-side closest point too.
        private static bool RaySegmentDistance(Vec3d origin, Vec3d dir, Vec3d a, Vec3d b,
            out double dist, out double rayT, out Vec3d onSegment)
        {
            double ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z;      // segment direction (unnormalised)
            double wx = a.X - origin.X, wy = a.Y - origin.Y, wz = a.Z - origin.Z;

            double aa = ux * ux + uy * uy + uz * uz;
            double bb = ux * dir.X + uy * dir.Y + uz * dir.Z;
            double dd = ux * wx + uy * wy + uz * wz;
            double ee = dir.X * wx + dir.Y * wy + dir.Z * wz;
            double denom = aa - bb * bb;                                // dir is normalised (cc = 1)

            double s = denom > 1e-9 ? (bb * ee - dd) / denom : 0.0;     // parallel → segment start
            s = Math.Max(0.0, Math.Min(1.0, s));
            rayT = bb * s + ee;                     // ray parameter of the closest approach to segment(s)

            if (rayT < 0) { dist = double.MaxValue; onSegment = null; return false; }

            onSegment = new Vec3d(a.X + ux * s, a.Y + uy * s, a.Z + uz * s);
            double rx = origin.X + dir.X * rayT, ry = origin.Y + dir.Y * rayT, rz = origin.Z + dir.Z * rayT;
            double ddx = rx - onSegment.X, ddy = ry - onSegment.Y, ddz = rz - onSegment.Z;
            dist = Math.Sqrt(ddx * ddx + ddy * ddy + ddz * ddz);
            return true;
        }

        // ==========================================================================================
        //  Placement targeting (§5): face hit → voxel cell at the current scale
        // ==========================================================================================

        // Resolves a block-face hit to an anchor position: tangential axes snap to the voxel grid at the
        // current scale; the face-normal axis sits flush ON the face plane for Surface, and half a voxel
        // into the empty cell adjacent to the face for Volumetric.
        private Vec3d ResolveAnchorPoint(BlockSelection blockSel)
        {
            double s = _draft.Scale / 16.0;
            Vec3d hit = new Vec3d(
                blockSel.Position.X + blockSel.HitPosition.X,
                blockSel.Position.Y + blockSel.HitPosition.Y,
                blockSel.Position.Z + blockSel.HitPosition.Z);

            Vec3i n = blockSel.Face.Normali;

            // Snap the two tangential axes to voxel-cell CENTERS at the current scale.
            double SnapCenter(double v) => (Math.Floor(v / s + 1e-6)) * s + s * 0.5;

            double x = n.X != 0 ? hit.X : SnapCenter(hit.X);
            double y = n.Y != 0 ? hit.Y : SnapCenter(hit.Y);
            double z = n.Z != 0 ? hit.Z : SnapCenter(hit.Z);

            // Face plane coordinate: the block-boundary the hit face lies on.
            double PlaneCoord(int blockCoord, int normal) => normal > 0 ? blockCoord + 1 : blockCoord;

            bool surface = _draft.Projection == ProjectionMode.Surface;
            if (n.X != 0) x = PlaneCoord(blockSel.Position.X, n.X) + (surface ? 0 : n.X * s * 0.5);
            if (n.Y != 0) y = PlaneCoord(blockSel.Position.Y, n.Y) + (surface ? 0 : n.Y * s * 0.5);
            if (n.Z != 0) z = PlaneCoord(blockSel.Position.Z, n.Z) + (surface ? 0 : n.Z * s * 0.5);

            return new Vec3d(x, y, z);
        }

        // True when the active draft is on the HEIGHT stage of a 3D volume (cylinder/cone/box, base placed).
        private bool AwaitingVolumeHeight =>
            _draft.AwaitingApex && GuideShapeTypes.IsVolume(_draft.Shape);

        // The free-air height aim (0.1.23): with no block under the crosshair, the volume's height handle
        // follows the view ray at the base's distance — looking up/down grows/shrinks the height (the
        // shape projects this onto its axis). A targeted block still takes priority (handled by callers).
        private Vec3d FreeAirHeightAim()
        {
            Vec3d start = _draft.DraftStart, second = _draft.DraftSecond;
            Vec3d baseCentre = second == null ? start
                : new Vec3d((start.X + second.X) * 0.5, (start.Y + second.Y) * 0.5, (start.Z + second.Z) * 0.5);
            Vec3d eye = EyePos(), dir = ViewDir();
            double depth = Math.Max(1.0, Dist(eye, baseCentre));
            return new Vec3d(eye.X + dir.X * depth, eye.Y + dir.Y * depth, eye.Z + dir.Z * depth);
        }

        // Shift-to-constrain: the second foot snaps to the FIRST foot's elevation and to the nearest
        // cardinal line from it — a level, square-on arch (and the all-Blue anchor shade) in one gesture.
        private Vec3d ConstrainToStart(Vec3d aim)
        {
            Vec3d start = _draft.DraftStart;
            return start == null ? aim : ConstrainTo(start, aim);
        }

        // The SHIFT cardinal constraint, generalised (Session-8 playtest fix): snap the aim onto the
        // east-west or north-south line through the reference point (whichever is dominant), at the
        // reference's height. During a draft the reference is the first foot; when RE-GRABBING an anchor
        // it is the guide's OTHER anchor — the same feet-align-to-each-other feel, after placement too.
        private static Vec3d ConstrainTo(Vec3d reference, Vec3d aim)
        {
            double dx = aim.X - reference.X;
            double dz = aim.Z - reference.Z;

            return Math.Abs(dx) >= Math.Abs(dz)
                ? new Vec3d(aim.X, reference.Y, reference.Z)     // east-west line
                : new Vec3d(reference.X, reference.Y, aim.Z);    // north-south line
        }

        // The guide's other anchor (first real anchor that isn't the given index), or null.
        private static Vec3d OtherAnchorOf(GuideData g, int grabbedIndex)
        {
            for (int i = 0; i < g.ControlPoints.Count; i++)
            {
                if (i == grabbedIndex) continue;
                ControlPoint cp = g.ControlPoints[i];
                if (cp.IsPhantom || !cp.IsAnchor) continue;
                return cp.WorldPosition;
            }
            return null;
        }

        // Live settings at send time (v2.1 rule): the plane comes from the clicked face unless the GUI
        // override says otherwise; the offset is the anchor's coordinate on the flattened axis in 1/16ths.
        // The clicked face's normal axis — the ellipse family's intrinsic plane normal.
        private static PlaneAxis AxisFromFace(BlockSelection blockSel)
        {
            Vec3i n = blockSel.Face.Normali;
            return n.Y != 0 ? PlaneAxis.Y : (n.Z != 0 ? PlaneAxis.Z : PlaneAxis.X);
        }

        private GuideRenderSettings BuildSettings(BlockSelection blockSel, Vec3d anchor)
        {
            PlaneAxis axis;
            if (_draft.PlaneOverride.HasValue)
            {
                axis = _draft.PlaneOverride.Value;
            }
            else if (blockSel != null)
            {
                Vec3i n = blockSel.Face.Normali;
                axis = n.Y != 0 ? PlaneAxis.Y : (n.Z != 0 ? PlaneAxis.Z : PlaneAxis.X);
            }
            else
            {
                // Free-air height stage (0.1.23): no clicked face — the draft's captured plane stands in.
                // Volumes force Volumetric below, so the exact plane is immaterial here anyway.
                axis = _draft.DraftPlaneAxis;
            }

            double coord = axis == PlaneAxis.X ? anchor.X : (axis == PlaneAxis.Y ? anchor.Y : anchor.Z);
            var plane = new ProjectionPlane(axis, (int)Math.Round(coord * 16.0));

            GuideRenderSettings settings = _draft.BuildRenderSettings(plane);

            // 3D volumes are ALWAYS Volumetric and carry no division marks (0.1.20 sphere, 0.1.21 family):
            // flattening a volume onto a plane is meaningless, so a lingering Surface/Divisions tool
            // default is overridden for the ghost and the create request both. (The GUI greys those rows
            // for these shapes; this covers the remembered defaults.)
            if (GuideShapeTypes.IsVolume(_draft.Shape)
                && (settings.Mode == ProjectionMode.Surface || settings.Divisions != 0))
                settings = new GuideRenderSettings(settings.Scale, ProjectionMode.Volumetric,
                    settings.Plane, settings.Filled, 0);

            return settings;
        }

        // ==========================================================================================
        //  Small helpers
        // ==========================================================================================

        private static List<ControlPoint> CloneControlPoints(IReadOnlyList<ControlPoint> points)
        {
            var copy = new List<ControlPoint>(points?.Count ?? 0);
            if (points != null)
                for (int i = 0; i < points.Count; i++)
                    copy.Add(points[i] == null ? new ControlPoint() : points[i].Clone());
            return copy;
        }

        private static bool PromoteLockedMarkers(GuideData guide)
        {
            bool changed = false;
            for (int i = 0; i < guide.ControlPoints.Count; i++)
            {
                ControlPoint point = guide.ControlPoints[i];
                if (!point.IsLockMarker || !point.IsLocked) continue;
                point.IsLockMarker = false;
                changed = true;
            }
            return changed;
        }

        private string MyUid => _capi.World.Player.PlayerUID;

        // True when the mirror shows the guide edit-locked by someone other than us — the client-side
        // pre-check for grab/insert (where holding the lock is genuinely required; no admin exception).
        private bool LockedByOther(Guid guideId) =>
            _net.LockHolders.TryGetValue(guideId, out string holder) && holder != null && holder != MyUid;

        private Vec3d EyePos()
        {
            EntityPlayer e = _capi.World.Player.Entity;
            return new Vec3d(
                e.Pos.X + e.LocalEyePos.X,
                e.Pos.Y + e.LocalEyePos.Y,
                e.Pos.Z + e.LocalEyePos.Z);
        }

        private Vec3d ViewDir()
        {
            Vec3f v = _capi.World.Player.Entity.Pos.GetViewVector();
            double len = Math.Sqrt((double)v.X * v.X + (double)v.Y * v.Y + (double)v.Z * v.Z);
            if (len < 1e-9) return new Vec3d(0, 0, 1);
            return new Vec3d(v.X / len, v.Y / len, v.Z / len);
        }

        private static double Dist(Vec3d a, Vec3d b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private bool ShiftHeld() =>
            _capi.Input.KeyboardKeyStateRaw[(int)GlKeys.ShiftLeft] ||
            _capi.Input.KeyboardKeyStateRaw[(int)GlKeys.ShiftRight];

        // Session 11: the cardinal/level snap moved from SHIFT to CTRL (freeing SHIFT for invert +
        // spring-back). Same raw-key read as ShiftHeld.
        private bool CtrlHeld() =>
            _capi.Input.KeyboardKeyStateRaw[(int)GlKeys.ControlLeft] ||
            _capi.Input.KeyboardKeyStateRaw[(int)GlKeys.ControlRight];

        private void Error(string code, string message) => _capi.TriggerIngameError(this, code, message);

        private static bool NearlySame(Vec3d a, Vec3d b)
        {
            const double eps = 1e-4;
            return Math.Abs(a.X - b.X) <= eps && Math.Abs(a.Y - b.Y) <= eps && Math.Abs(a.Z - b.Z) <= eps;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _net.LockStateChanged -= OnLockStateChanged;
            _net.GuideRemoved -= OnGuideRemoved;
            _net.GuideAddedOrUpdated -= OnGuideAddedOrUpdated;
            _net.AuthorityModeChanged -= OnAuthorityModeChanged;
            _capi.Input.InWorldAction -= OnInWorldAction;

            if (_tickId != 0)
            {
                _capi.Event.UnregisterGameTickListener(_tickId);
                _tickId = 0;
            }
        }
    }
}
