using System;
using System.Collections.Generic;
using System.Reflection;
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
    [Flags]
    internal enum ShapeModifierHelp
    {
        None = 0,
        CtrlCardinal = 1 << 0,
        ShiftVertical = 1 << 1,
        ShiftCenterApex = 1 << 2,
        ShiftInvert = 1 << 3,
        CtrlCloseRim = 1 << 4,
        ShiftRestore = 1 << 5,
        ShiftFlatSide = 1 << 6,
        CtrlShiftDiagonal = 1 << 7,
        ShiftAllowFlare = 1 << 8,
        RoundoverRoute = 1 << 9,
        ShiftEmbedGuide = 1 << 10,
        CtrlBypassGrab = 1 << 11
    }

    /// <summary>
    /// The "tidy aim-controller": all client-side interaction logic for the held Layout tool, kept out of
    /// both the (stateless) item and the (thin) ModSystem. Runs a lightweight per-tick loop while the tool
    /// is held: raycasts the crosshair against the guide mirror, feeds the HUD seams
    /// (<see cref="GuideHud.SetExaminedGuide"/>, <see cref="GuideHud.SetDraftCalculating"/>), drives an active drag,
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
        private const int PreviewFullResVoxelThreshold = 8000;
        private const double MaxReach = 12.0;           // furthest a guide can be targeted, in blocks
        private const int PendingInsertTimeoutMs = 2000;
        // Cap-clamp trackers (v0.2.25): one for the placement ghost, one for dragging a placed point.
        private readonly CapClampTracker _draftClamp = new CapClampTracker();
        private readonly CapClampTracker _dragClamp = new CapClampTracker();
        private bool _rimAimArmed;
        private bool _rimAwaitingRelease;
        private long _lastDraftClampCheckMs;

        // v0.3 motion-sensitive drafting. Cheap movement keeps the selected-scale shell; expensive movement
        // uses an adaptive precision wireframe, then materializes the final selected-scale shell when still.
        private const int DraftSettleDelayMs = 180;
        private const int DraftMinimumRefineIntervalMs = 120;
        private ulong _draftPoseFingerprint;
        private ulong _draftRawAimFingerprint;
        private int _draftGeneration;
        private int _acceptedDraftScale;
        private int _acceptedDraftVoxelCount = -1;
        private int _adaptiveMovingScale;
        private int _healthyMovingUpdates;
        private ulong _draftAdaptiveContextFingerprint;
        private long _draftLastMotionMs;
        private long _lastDraftRefineRequestMs;
        private long _lastDraftWorkMilliseconds;
        private double _draftBaselineFrameMilliseconds = 16.67;
        private DraftPreviewSpec _currentDraftSpec;

        private readonly ICoreClientAPI _capi;
        private readonly DraftManager _draft;
        private readonly ClientNetworkHandler _net;
        private readonly GuideRenderer _renderer;
        private readonly GuideToolGui _gui;
        private readonly GuideHud _hud;

        private long _tickId;
        private bool _toolHeld;
        private Guid? _currentTargetGuide;
        private bool _springBackAvailable;
        private bool _modifierHelpInitialised;
        private bool _modifierHelpRefreshUnavailable;
        private bool _suppressStandardHeldHelp;
        private ShapeModifierHelp _lastModifierHelp;
        private object _hotbarHud;
        private FieldInfo _hotbarPrevIndex;
        private MethodInfo _hotbarRecomposeHelp;
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

        // --- Free-move session (F6, v0.3.86) -------------------------------------------------------
        //
        // Armed from the Move panel's crosshair toggle and engaged the moment the panel closes. Unlike a
        // grab it takes NO edit lock and sends NOTHING until it is committed: the whole gesture is a local
        // render offset (see GuideRenderer.SetMoveOffset), so dragging an immense guide around costs one
        // matrix translation per frame. The committed move is a single translate packet, one undo step.
        private sealed class MoveSession
        {
            public Guid GuideId;
            public double Depth;          // held distance along the view ray, as an interior grab does
            public Vec3d Anchor;          // where the ray-at-depth sat when the drag began
            public int OffsetX, OffsetY, OffsetZ;   // current snapped offset, in 1/16-block units
        }
        private MoveSession _move;

        // A committed move whose authority answer has not landed yet; its render offset is held until it
        // does (or until PendingMoveCommitTimeoutMs, so a lost packet cannot strand a guide off-position).
        private Guid _pendingMoveCommit = Guid.Empty;
        private long _pendingMoveCommitMs;
        private const long PendingMoveCommitTimeoutMs = 5000;

        /// <summary>
        /// True when the tool is at rest — no draft in progress and no grabbed point. Gates the
        /// ground-storage set-down gesture (SHIFT+right-click) so it can never fire mid-edit.
        /// </summary>
        public bool IsIdle => !_draft.HasActiveDraft && _grab == null;

        /// <summary>True only while Layout is rebuilding the hotbar for a shape-stage note.</summary>
        internal bool SuppressStandardHeldHelp => _suppressStandardHeldHelp;

        /// <summary>
        /// Live modifier help for Vintage Story's held-item prompt. Layout recomposes the native hotbar
        /// help whenever this value changes so the visible rows follow the exact draft/grab stage.
        /// </summary>
        internal ShapeModifierHelp ModifierHelp
        {
            get
            {
                if (!_toolHeld || _draft.Mode != ToolMode.Create) return ShapeModifierHelp.None;

                if (_draft.HasActiveDraft)
                {
                    if (DraftManager.IsChainShape(_draft.Shape))
                    {
                        if (_draft.Shape == GuideShapeType.Roundover)
                            return ShapeModifierHelp.RoundoverRoute
                                | (_draft.DraftEmbedded
                                    ? ShapeModifierHelp.None : ShapeModifierHelp.ShiftEmbedGuide);
                        ShapeModifierHelp chainHelp = ShapeModifierHelp.CtrlCardinal
                            | ShapeModifierHelp.ShiftVertical | ShapeModifierHelp.CtrlShiftDiagonal;
                        return chainHelp;
                    }
                    if (_draft.AwaitingRim)
                        return DraftManager.IsTaperedRimStage(_draft.Shape)
                            ? ShapeModifierHelp.CtrlCloseRim | ShapeModifierHelp.ShiftAllowFlare
                            : ShapeModifierHelp.None;   // the Box's fourth click is a plain height click
                    if (_draft.AwaitingApex)
                        return _draft.Shape == GuideShapeType.Triangle
                            ? ShapeModifierHelp.ShiftCenterApex : ShapeModifierHelp.None;

                    ShapeModifierHelp help = ShapeModifierHelp.CtrlCardinal;
                    if (_draft.Shape == GuideShapeType.Line) help |= ShapeModifierHelp.ShiftVertical;
                    if (_draft.Shape == GuideShapeType.Line) help |= ShapeModifierHelp.CtrlShiftDiagonal;
                    if (GuideShapeTypes.UsesSides(_draft.Shape)) help |= ShapeModifierHelp.ShiftFlatSide;
                    if (_draft.Shape == GuideShapeType.Arch || _draft.Shape == GuideShapeType.Dome
                        || (_draft.Shape == GuideShapeType.Triangle
                            && _draft.Constraint == ShapeConstraint.Equilateral)
                        // v0.4.15: a Square is settled by one clicked edge, so SHIFT is what picks which
                        // side of that edge it lies on — the two-click counterpart of the free rectangle's
                        // width click.
                        || (_draft.Shape == GuideShapeType.Rectangle
                            && _draft.Constraint == ShapeConstraint.Square))
                        help |= ShapeModifierHelp.ShiftInvert;
                    return help;
                }

                if (_grab != null && !_grab.Suspended
                    && _net.Guides.TryGetValue(_grab.GuideId, out GuideData guide)
                    && _grab.PointIndex >= 0 && _grab.PointIndex < guide.ControlPoints.Count)
                {
                    if (_grab.PointIndex == 3 && IsTaperedVolume(guide.ShapeType))
                        return ShapeModifierHelp.CtrlCloseRim | ShapeModifierHelp.ShiftAllowFlare;
                    if (guide.ControlPoints[_grab.PointIndex].IsAnchor)
                        return ShapeModifierHelp.CtrlCardinal;
                    return _springBackAvailable ? ShapeModifierHelp.ShiftRestore : ShapeModifierHelp.None;
                }

                ShapeModifierHelp idleHelp = ShapeModifierHelp.CtrlBypassGrab;
                if (_springBackAvailable && !CtrlHeld())
                    idleHelp |= ShapeModifierHelp.ShiftRestore;
                else
                    idleHelp |= ShapeModifierHelp.ShiftEmbedGuide;
                return idleHelp;
            }
        }

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
            _net.GuideWhoRequested += OnGuideWhoRequested;
            _renderer.DraftPreviewCompleted += OnDraftPreviewCompleted;
            _renderer.PlacementMaterializationCompleted += OnPlacementMaterializationCompleted;
            _capi.Input.InWorldAction += OnInWorldAction;
            _capi.Event.MouseDown += OnMouseDown;      // F5 inventory refill (independent of the tool)
            _capi.Event.MouseUp += OnMouseUp;          // release-to-rearm between height and rim clicks

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

        private bool WarnIfRenderingDisabled()
        {
            if (_renderer.RenderingEnabled) return false;
            Error("layout-renderingoff",
                "Layout guides are hidden. Turn them back on with \"Show guides\" on the tool panel's "
                + "settings page (F, then the gear), or /layout on.");
            return true;
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
            if (!_renderer.RenderingEnabled)
            {
                _currentTargetGuide = null;
                _hud.SetExaminedGuide(null);
                return;
            }

            // Mode switched (via the GUI, which already cancels any draft): a live grab is released
            // properly so the lock never leaks across modes.
            if (_draft.Mode != _lastMode)
            {
                if (_grab != null && !_grab.Suspended) FinishRelease();
                CancelFreeMove();          // leaving Move mode abandons an un-committed drag
                _lastMode = _draft.Mode;
            }

            UpdateFreeMove();
            if (_pendingMoveCommit != Guid.Empty
                && _capi.World.ElapsedMilliseconds - _pendingMoveCommitMs > PendingMoveCommitTimeoutMs)
                ClearPendingMoveCommit();

            // Stale pending insert (packet lost / rejected) simply expires.
            if (_hasPendingInsert && _capi.World.ElapsedMilliseconds - _pendingInsertMs > PendingInsertTimeoutMs)
            {
                _hasPendingInsert = false;
                _pendingInsertOriginPoints = null;
            }

            UpdateAim();
            RefreshModifierHelpIfChanged();
        }

        // Vintage Story 1.22 composes held-item interaction help only when the hotbar slot changes; its
        // ShouldApply predicates are evaluated once during that composition, not live. Re-run the native
        // hotbar composition when our stage flags change so the normal help rows appear at the moment they
        // become useful and retain the game's own key glyphs, position, and 3.5-second lifetime.
        private void RefreshModifierHelpIfChanged()
        {
            ShapeModifierHelp current = ModifierHelp;
            if (!_modifierHelpInitialised)
            {
                _modifierHelpInitialised = true;
                _lastModifierHelp = current;
                // A normal hotbar swap already composed the standard Chalking Kit note. Do not immediately
                // replay it merely because our held-state tick caught up with the engine.
                if (current == ShapeModifierHelp.None) return;
            }
            else
            {
                if (current == _lastModifierHelp) return;
                _lastModifierHelp = current;
            }
            TryRefreshNativeHeldHelp();
        }

        private void TryRefreshNativeHeldHelp()
        {
            if (_modifierHelpRefreshUnavailable) return;
            try
            {
                if (_hotbarHud == null)
                {
                    foreach (object gui in _capi.LoadedGuis)
                    {
                        Type type = gui?.GetType();
                        if (type?.FullName != "Vintagestory.Client.NoObf.HudHotbar") continue;
                        _hotbarHud = gui;
                        _hotbarPrevIndex = type.GetField("prevIndex",
                            BindingFlags.Instance | BindingFlags.NonPublic);
                        _hotbarRecomposeHelp = type.GetMethod("RecomposeActiveSlotHoverText",
                            BindingFlags.Instance | BindingFlags.NonPublic);
                        break;
                    }
                }

                if (_hotbarHud == null || _hotbarPrevIndex == null || _hotbarRecomposeHelp == null)
                {
                    _modifierHelpRefreshUnavailable = true;
                    _capi.Logger.Warning("[Layout] Could not locate Vintage Story's held-help refresh path; "
                        + "shape modifier notes will only update after changing hotbar slots.");
                    return;
                }

                int slot = _capi.World.Player.InventoryManager.ActiveHotbarSlotNumber;
                _hotbarPrevIndex.SetValue(_hotbarHud, -1);
                _suppressStandardHeldHelp = true;
                try
                {
                    _hotbarRecomposeHelp.Invoke(_hotbarHud, new object[] { slot });
                }
                finally
                {
                    _suppressStandardHeldHelp = false;
                }
            }
            catch (Exception e)
            {
                _modifierHelpRefreshUnavailable = true;
                _capi.Logger.Warning("[Layout] Held-help refresh failed: {0}", e.Message);
            }
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
                // Ground-storage set-down (SHIFT+right-click, tool idle, REAL item held): step aside
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
        /// (never the client-only vanilla gate), the tool is idle (no draft, no grab), and SHIFT is
        /// down — read from the entity's interaction-modifier controls, the same source the vanilla
        /// GroundStorable behavior checks, so the two gates can never disagree.
        /// </summary>
        private bool IsGroundStoreSetDownGesture()
        {
            if (!IsToolHeld() || !IsIdle) return false;
            var c = _capi.World?.Player?.Entity?.Controls;
            return c != null && c.ShiftKey;
        }

        // F5 inventory refill (0.2.21): right-click a held Chalking Powder stack (on the mouse cursor) onto a
        // Chalking Kit in an open inventory. Works regardless of whether the Layout tool is held, and only
        // when the PLAYER has opted in (v0.2.22: a client preference in layout-client.json, formerly server
        // policy). The server re-validates that the request names a real kit and a real powder stack and, if
        // its own default swap somehow ran first, simply refuses — so the worst case is a harmless swap,
        // never a corrupted inventory (see ServerNetworkHandler.OnInventoryChalkRefill). Setting Handled
        // suppresses the default swap when we win the ordering race.
        private void OnMouseDown(MouseEvent args)
        {
            if (args.Button != EnumMouseButton.Right || args.Handled) return;
            if (!(_capi.ModLoader.GetModSystem<LayoutModSystem>()?.InventoryChalkRefillAllowed ?? false)) return;

            var im = _capi.World?.Player?.InventoryManager;
            ItemStack cursor = im?.MouseItemSlot?.Itemstack;
            if (!(cursor?.Collectible is Items.ItemChalkingPowder) || cursor.StackSize <= 0) return;

            ItemSlot hovered = im.CurrentHoveredSlot;
            ItemStack kit = hovered?.Itemstack;
            if (!(kit?.Collectible is Items.ItemGuideTool)) return;
            if (Items.ItemGuideTool.IsChalkFull(kit)) return;                          // full: leave it

            var inv = hovered.Inventory;
            if (inv == null) return;
            _net.SendInventoryChalkRefill(inv.InventoryID, inv.GetSlotId(hovered));
            args.Handled = true;
        }

        // MouseUp is the API's reliable physical-release seam; InWorldAction only delivered the press in
        // play, which left the first release-latch attempt permanently closed at the rim stage.
        private void OnMouseUp(MouseEvent args)
        {
            if (args.Button == EnumMouseButton.Left) _rimAwaitingRelease = false;
        }

        private void OnHeldChanged(bool held)
        {
            if (held)
            {
                _modifierHelpInitialised = false;
                _hud.SetComatoseDraft(false);
                _hud.TryOpen();
                ResumeGrabIfStillValid();
                _lastMode = _draft.Mode;
            }
            else
            {
                // Suspend, don't end: the draft keeps its anchor, the grab keeps its lock (comatose).
                if (_grab != null) _grab.Suspended = true;
                // A free-move has no lock and nothing pending, so it simply ends — putting the guide
                // straight back rather than leaving a phantom offset behind while the tool is away.
                CancelFreeMove();

                _hud.SetExaminedGuide(null);
                _currentTargetGuide = null;
                if (_draft.HasActiveDraft)
                {
                    // Preserve the last settled readout as a faint reminder while the draft and its
                    // world-space preview are suspended. Re-equipping restores normal HUD styling.
                    _hud.SetComatoseDraft(true);
                    _hud.TryOpen();
                }
                else
                {
                    _hud.ClearDraftAim();
                    _hud.SetComatoseDraft(false);
                    _hud.TryClose();
                }
                if (_gui.IsOpened()) _gui.TryClose();
                ResetDraftVisualState();
                _renderer.ClearDraftPreview();       // the DRAFT survives the swap; the live ghost does not
                _springBackAvailable = false;
                _modifierHelpInitialised = false;
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
                if (_net.Guides.TryGetValue(_grab.GuideId, out GuideData guide))
                {
                    _renderer.SetGrabbedPoint(_grab.GuideId, _grab.PointIndex);
                    _hud.SetGrabMeasurement(_grab.GuideId, _renderer.CurrentGrabExtent);
                }
            }
            else
            {
                DropGrabLocally();
            }
        }

        // ==========================================================================================
        //  Free-move (F6): the selected guide rides the crosshair until a click settles it
        // ==========================================================================================

        /// <summary>True while a guide is riding the crosshair, so clicks mean settle/put-back.</summary>
        private bool FreeMoving => _move != null;

        // Engages when the Move panel armed it and then closed; updates the render offset every tick.
        private void UpdateFreeMove()
        {
            // A commit still in flight blocks a new drag: its anchor would be measured against the guide's
            // old data position while the screen already shows the new one, so the second move would be
            // wrong by exactly the first one.
            bool wanted = _draft.Mode == ToolMode.Transform
                && _draft.FreeMove
                && _draft.SelectedGuideId != null
                && _pendingMoveCommit == Guid.Empty
                && !_gui.IsOpened();

            if (!wanted) { CancelFreeMove(); return; }

            Guid guideId = _draft.SelectedGuideId.Value;
            if (!_net.Guides.TryGetValue(guideId, out GuideData g)) { CancelFreeMove(); return; }

            if (_move == null || _move.GuideId != guideId)
            {
                CancelFreeMove();
                Vec3d centre = GuideAnchorCentre(g);
                double depth = Math.Max(1.0, Dist(EyePos(), centre));
                Vec3d eye = EyePos(), dir = ViewDir();
                _move = new MoveSession
                {
                    GuideId = guideId,
                    Depth = depth,
                    // Deep-copied by construction — the ray point is a fresh Vec3d, never a guide's own.
                    Anchor = new Vec3d(
                        eye.X + dir.X * depth, eye.Y + dir.Y * depth, eye.Z + dir.Z * depth)
                };
            }

            // T1 (v0.4.15): CTRL sets the guide DOWN on the surface under the crosshair instead of riding
            // the held view-ray depth — the same idea as CTRL's level/cardinal snap while drafting. The aim
            // point becomes the real face hit, and the vertical offset is then solved so the guide's LOWEST
            // VOXEL PLANE meets that face (see TryGuideFloorSixteenths). With no block under the crosshair
            // there is no surface to meet, so the ordinary held-depth drag simply continues.
            BlockSelection surface = CtrlHeld() ? _capi.World.Player.CurrentBlockSelection : null;

            double px, py, pz;
            if (surface != null)
            {
                px = surface.Position.X + surface.HitPosition.X;
                py = surface.Position.Y + surface.HitPosition.Y;
                pz = surface.Position.Z + surface.HitPosition.Z;
            }
            else
            {
                Vec3d e = EyePos(), d = ViewDir();
                px = e.X + d.X * _move.Depth;
                py = e.Y + d.Y * _move.Depth;
                pz = e.Z + d.Z * _move.Depth;
            }

            // Snap to whole voxels of THIS guide — the authority refuses anything finer, and a sub-voxel
            // offset would move cells by a whole cell wherever it happened to cross a quantise boundary.
            int step = Math.Max(1, g.VoxelScale);
            int ox = SnapSixteenths(px - _move.Anchor.X, step);
            int oz = SnapSixteenths(pz - _move.Anchor.Z, step);

            // Solve the contact FIRST and snap the result, never the other way round: rounding the hit
            // height to the voxel grid before subtracting the guide's underside would leave the guide a
            // voxel proud of — or sunk into — the very face it is supposed to be resting on.
            int oy = surface != null && TryGuideFloorSixteenths(g, out int floor16)
                ? SnapSixteenths(py - floor16 / 16.0, step)
                : SnapSixteenths(py - _move.Anchor.Y, step);

            if (ox == _move.OffsetX && oy == _move.OffsetY && oz == _move.OffsetZ) return;

            _move.OffsetX = ox;
            _move.OffsetY = oy;
            _move.OffsetZ = oz;
            _renderer.SetMoveOffset(guideId, ox / 16.0, oy / 16.0, oz / 16.0);
        }

        private static int SnapSixteenths(double worldDelta, int step) =>
            (int)Math.Round(worldDelta * 16.0 / step) * step;

        /// <summary>
        /// The bottom of the guide's LOWEST voxel plane, in 1/16 units — the plane T1's CTRL surface-snap
        /// brings down onto the targeted face. False when the guide has no usable geometry yet.
        /// </summary>
        /// <remarks>
        /// WHY THE LOWEST PLANE (decided 2026-07-27). It is unambiguous on every shape and matches the
        /// common case of setting a build down on the ground. Aiming at a wall or ceiling therefore still
        /// pushes the guide's BOTTOM to that face, which can read oddly — predictable was preferred over
        /// clever. Nearest-face has no sane meaning on a sphere; base anchors would sink a dome halfway
        /// into the floor, since its anchors are its base ring rather than its lowest point.
        ///
        /// Measured from the shape's own sampled outline (the cached targeting polyline) plus its real
        /// control points, and never from the voxel set: this runs every tick of a drag, and voxelising a
        /// behemoth merely to find its underside is exactly the cost free-move exists to avoid. Cells
        /// quantise to Floor(world*16/scale)*scale, so flooring the outline's lowest point onto that grid
        /// IS the cell the shell's underside occupies.
        ///
        /// Phantom points are excluded. They steer end tangents and are never emitted as voxels, and an
        /// arch's phantoms sit BELOW its feet — including them would hang the whole guide in the air.
        ///
        /// A Surface guide is drawn FLATTENED onto its plane, so when that plane is the horizontal one its
        /// stored points say nothing about where the decal actually lies; the plane offset is the answer.
        /// </remarks>
        private bool TryGuideFloorSixteenths(GuideData g, out int floor16)
        {
            floor16 = 0;
            int scale = Math.Max(1, g.VoxelScale);

            if (g.Projection == ProjectionMode.Surface && g.Plane.FlattenedAxis == PlaneAxis.Y)
            {
                floor16 = FloorToStep(g.Plane.PlaneOffset, scale);
                return true;
            }

            double minY = double.MaxValue;
            List<Vec3d> curve = GetCurvePolyline(g);
            if (curve != null)
                for (int i = 0; i < curve.Count; i++)
                    if (curve[i] != null && curve[i].Y < minY) minY = curve[i].Y;

            List<ControlPoint> points = g.ControlPoints;
            if (points != null)
                for (int i = 0; i < points.Count; i++)
                {
                    ControlPoint cp = points[i];
                    if (cp == null || cp.IsPhantom || cp.WorldPosition == null) continue;
                    if (cp.WorldPosition.Y < minY) minY = cp.WorldPosition.Y;
                }

            if (minY == double.MaxValue) return false;
            floor16 = (int)Math.Floor(minY * 16.0 / scale) * scale;
            return true;
        }

        // Floors a 1/16-unit coordinate onto the guide's own voxel grid. Negative-safe by construction:
        // integer division truncates toward zero, which would lift a below-sea-level plane by a voxel.
        private static int FloorToStep(int sixteenths, int step) =>
            (int)Math.Floor((double)sixteenths / step) * step;

        // ---------------------------------------------------------------------------------
        //  Send to ground (v0.4.28)
        // ---------------------------------------------------------------------------------

        /// <summary>How far below a guide's underside the search gives up, in whole blocks.</summary>
        private const int GroundSearchBlocks = 160;

        /// <summary>Sample points along each horizontal axis of the guide's footprint.</summary>
        private const int GroundSamplesPerAxis = 32;

        /// <summary>
        /// How far DOWN, in 1/16 units, the selected guide must travel for its underside to come to rest on
        /// the ground beneath it. Always zero or negative; zero means it is already resting, is buried, or
        /// has nothing under it within <see cref="GroundSearchBlocks"/>.
        /// </summary>
        /// <remarks>
        /// THE SAME CONTACT RULE AS T1, APPLIED DOWNWARD. The guide's LOWEST VOXEL PLANE meets the surface,
        /// and it is <see cref="TryGuideFloorSixteenths"/> that says where that plane is — deliberately the
        /// same method rather than a second derivation of the same idea, so the CTRL free-move snap and this
        /// button can never come to disagree about where the bottom of a guide is.
        ///
        /// THE HIGHEST GROUND UNDER THE FOOTPRINT WINS, not the ground under the centre. On a slope that is
        /// the difference between setting a house plan down on the hillside and burying half of it: the
        /// guide comes to rest on the first thing it would touch, which is what dropping an object does.
        ///
        /// SAMPLED, NOT SWEPT. A large guide's footprint is thousands of block columns and this runs on a
        /// button press, so the footprint is sampled on a grid of at most 32x32 points. A spike of terrain
        /// thinner than the sample spacing can be missed, which lets a corner of the guide intersect it;
        /// the alternative is a click that stalls the client on a hundred-block plan. Guides up to 32 blocks
        /// across — nearly all of them — are sampled at every single block and cannot miss anything.
        ///
        /// MATERIAL IS READ FROM COLLISION BOXES via <see cref="BlockOccupancy"/>, so the guide lands flush
        /// on a slab, a stair or a chiselled block instead of a whole block above it. The instance is local
        /// and thrown away with the call: this fires once per click, and keeping a cache alive between
        /// clicks would hold a stale picture of a world the player is actively building in.
        /// </remarks>
        public int GroundDropSixteenths(GuideData g)
        {
            IBlockAccessor accessor = _capi?.World?.BlockAccessor;
            if (g == null || accessor == null) return 0;
            if (!TryGuideFloorSixteenths(g, out int floor16)) return 0;
            if (!TryGuideFootprint(g, out double minX, out double maxX, out double minZ, out double maxZ))
                return 0;

            var occupancy = new BlockOccupancy();
            int best = int.MinValue;

            int nx = SampleCount(minX, maxX), nz = SampleCount(minZ, maxZ);
            for (int ix = 0; ix < nx; ix++)
            {
                int x16 = SampleSixteenths(minX, maxX, ix, nx);
                for (int iz = 0; iz < nz; iz++)
                {
                    int z16 = SampleSixteenths(minZ, maxZ, iz, nz);
                    int top16 = ColumnSurfaceSixteenths(occupancy, accessor, x16, z16, floor16);
                    if (top16 > best) best = top16;

                    // Nothing can beat contact — the guide is already touching down somewhere.
                    if (best == floor16) return 0;
                }
            }

            if (best == int.MinValue) return 0;      // open air all the way down, within reach

            // The surface sits on the WORLD's 1/16 grid; the guide may only move in whole voxels of its own
            // scale. Rounding toward zero rather than away leaves the guide resting on, or up to one voxel
            // proud of, the surface — never sunk into it. Only bites on scale-2 and coarser guides, whose
            // cells cannot land flush on a half-block face in the first place.
            int scale = Math.Max(1, g.VoxelScale);
            int drop = best - floor16;
            return -((-drop) / scale) * scale;
        }

        // The guide's horizontal extent, from the same two sources TryGuideFloorSixteenths measures its
        // underside from: the sampled outline and the real (non-phantom) control points.
        private bool TryGuideFootprint(
            GuideData g, out double minX, out double maxX, out double minZ, out double maxZ)
        {
            minX = minZ = double.MaxValue;
            maxX = maxZ = double.MinValue;

            List<Vec3d> curve = GetCurvePolyline(g);
            if (curve != null)
                for (int i = 0; i < curve.Count; i++)
                {
                    Vec3d p = curve[i];
                    if (p == null) continue;
                    if (p.X < minX) minX = p.X;
                    if (p.X > maxX) maxX = p.X;
                    if (p.Z < minZ) minZ = p.Z;
                    if (p.Z > maxZ) maxZ = p.Z;
                }

            List<ControlPoint> points = g.ControlPoints;
            if (points != null)
                for (int i = 0; i < points.Count; i++)
                {
                    ControlPoint cp = points[i];
                    if (cp == null || cp.IsPhantom || cp.WorldPosition == null) continue;
                    Vec3d p = cp.WorldPosition;
                    if (p.X < minX) minX = p.X;
                    if (p.X > maxX) maxX = p.X;
                    if (p.Z < minZ) minZ = p.Z;
                    if (p.Z > maxZ) maxZ = p.Z;
                }

            return minX <= maxX && minZ <= maxZ;
        }

        // One sample per block of extent, capped. A one-block-wide guide still gets its single column.
        private static int SampleCount(double min, double max) =>
            Math.Max(1, Math.Min(GroundSamplesPerAxis, (int)Math.Ceiling(max - min) + 1));

        // Sample i of n, spread across [min, max] with the ends included, in absolute 1/16 units.
        private static int SampleSixteenths(double min, double max, int i, int n)
        {
            double t = n <= 1 ? 0.5 : (double)i / (n - 1);
            return (int)Math.Floor((min + (max - min) * t) * 16.0);
        }

        /// <summary>
        /// The top of the first material found straight down from <paramref name="floor16"/> in this one
        /// 1/16 column, or <see cref="int.MinValue"/> if there is none within reach.
        /// </summary>
        /// <remarks>
        /// TWO RESOLUTIONS, and that is what keeps this cheap. Falling a cell at a time through open sky
        /// would be 2,560 queries per sample point; the block classification skips a whole block per query
        /// and only the one block that actually stops the fall is examined cell by cell. A partial block
        /// that happens to be hollow at this column — the gap between two fence posts — correctly does not
        /// stop it, and the walk continues below.
        /// </remarks>
        private static int ColumnSurfaceSixteenths(
            BlockOccupancy occupancy, IBlockAccessor accessor, int x16, int z16, int floor16)
        {
            int bx = x16 >> 4, bz = z16 >> 4;
            int startBlockY = (floor16 - 1) >> 4;      // the first block strictly below the underside
            var pos = new BlockPos(bx, 0, bz);

            for (int i = 0; i < GroundSearchBlocks; i++)
            {
                int by = startBlockY - i;
                pos.Y = by;
                BlockFill fill = occupancy.FillAt(accessor, pos);
                if (fill == BlockFill.Empty) continue;

                // The topmost cell of this block that is both material and below the guide, +1 for its top
                // face. Capped at floor16 so the block the underside sits inside cannot report a surface
                // above the guide itself.
                int highest = Math.Min(by * 16 + 15, floor16 - 1);
                for (int y16 = highest; y16 >= by * 16; y16--)
                    if (occupancy.IsMaterialAt(accessor, x16, y16, z16)) return y16 + 1;
            }

            return int.MinValue;
        }

        // Commits the drag as ONE translate — one undo step — and disarms. Disarming matters: leaving it
        // armed would re-engage on the very next tick and the guide would start following the crosshair
        // again the instant it was put down.
        private void CommitFreeMove()
        {
            if (_move == null) return;
            MoveSession session = _move;
            _move = null;
            _draft.SetFreeMove(false);

            if (session.OffsetX == 0 && session.OffsetY == 0 && session.OffsetZ == 0)
            {
                _renderer.ClearMoveOffset();
                return;
            }

            // Deliberately NOT clearing the render offset here: it stays until the authority answers, so a
            // remote server's round trip cannot make the guide snap back and then jump forward again. The
            // timeout is only a safety net for an answer that never arrives.
            _pendingMoveCommit = session.GuideId;
            _pendingMoveCommitMs = _capi.World.ElapsedMilliseconds;
            _renderer.HoldMoveMaterialization(session.GuideId);
            _net.SendTranslate(
                session.GuideId, session.OffsetX, session.OffsetY, session.OffsetZ);
        }

        private void ClearPendingMoveCommit()
        {
            if (_pendingMoveCommit == Guid.Empty) return;
            _pendingMoveCommit = Guid.Empty;
            _renderer.ClearMoveOffset();
        }

        // Abandons the drag with nothing sent — the guide simply draws where its data always said it was.
        private void CancelFreeMove()
        {
            if (_move == null) return;
            _move = null;
            _draft.SetFreeMove(false);
            _renderer.ClearMoveOffset();
        }

        // The middle of a guide's real anchors — good enough to pick a held distance from, and cheap
        // (control points only, never voxels: this must not touch a behemoth's shell).
        private static Vec3d GuideAnchorCentre(GuideData g)
        {
            double sx = 0, sy = 0, sz = 0;
            int n = 0;
            List<ControlPoint> points = g.ControlPoints;
            if (points != null)
            {
                for (int i = 0; i < points.Count; i++)
                {
                    ControlPoint cp = points[i];
                    if (cp == null || cp.IsPhantom || cp.WorldPosition == null) continue;
                    sx += cp.WorldPosition.X; sy += cp.WorldPosition.Y; sz += cp.WorldPosition.Z;
                    n++;
                }
            }
            return n == 0 ? new Vec3d() : new Vec3d(sx / n, sy / n, sz / n);
        }

        private void UpdateAim()
        {
            BlockSelection blockSel = _capi.World.Player.CurrentBlockSelection;

            // A guide riding the crosshair is DRAWN offset but its data has not moved, so a raycast would
            // still hit where it used to be — reporting a guide the player can no longer see there. Nothing
            // is targetable mid-drag anyway (the click means "put it down"), so skip the cast entirely.
            if (FreeMoving)
            {
                _currentTargetGuide = null;
                _springBackAvailable = false;
                _hud.SetExaminedGuide(null);
                return;
            }

            // 1) Guide under the crosshair → HUD examine seam (id, lock status, count, cap bar).
            TargetHit hit = FindTarget(includeLockedPoints: true);
            _currentTargetGuide = hit.Found ? hit.GuideId : (Guid?)null;
            _springBackAvailable = hit.Found && !LockedByOther(hit.GuideId);
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
                    bool embedAim = _draft.DraftEmbedded
                        || (_draft.Shape == GuideShapeType.Roundover && ShiftHeld());
                    Vec3d aim = blockSel != null
                        ? ResolveGuidePoint(blockSel, embedAim) : FreeAirAim();
                    if (DraftManager.IsChainShape(_draft.Shape))
                    {
                        if (_draft.Shape == GuideShapeType.Roundover)
                        {
                            ObserveDraftMotion(aim, flatSideAligned: false);
                            if (_draft.AwaitingRoundoverProfileFirst)
                            {
                                PresentDraft(new DraftPreviewSpec(
                                    0, BuildSettings(blockSel, aim), _draft.DraftChain, aim, false),
                                    aim, false);
                            }
                            else if (_draft.AwaitingRoundoverProfileSecond)
                            {
                                var profileLegs = new List<Vec3d>
                                {
                                    _draft.RoundoverProfileFirst,
                                    _draft.DraftStart
                                };
                                PresentDraft(new DraftPreviewSpec(
                                    0, BuildSettings(blockSel, aim), profileLegs, aim, false),
                                    aim, false);
                            }
                            else
                            {
                                Vec3d routeAim = Dist(aim, _draft.ChainLast) > 1e-7 ? aim : null;
                                PresentDraft(new DraftPreviewSpec(
                                    0, RoundoverSweepPreviewSettings(blockSel, aim), _draft.DraftChain,
                                    _draft.RoundoverProfileFirst, _draft.RoundoverProfileSecond,
                                    routeAim, _draft.DraftPlaneAxis), aim, false);
                            }
                            return;
                        }

                        // Free-Shape (0.1.15): the ghost is the placed chain + a live segment to the
                        // crosshair. CTRL snaps the segment level-and-cardinal off the LAST placed corner;
                        // SHIFT (0.1.16) snaps it VERTICAL (straight up/down from that corner). Aiming
                        // near the first corner (with ≥3 placed) snaps onto it and previews the CLOSED loop.
                        aim = ConstrainChainAim(aim);
                        bool closing = _draft.Shape == GuideShapeType.FreeShape
                            && _draft.ChainCount >= 3
                            && Dist(aim, _draft.ChainFirst) <= ChainSnapRadius();
                        if (closing) aim = _draft.ChainFirst;
                        ObserveDraftMotion(aim, flatSideAligned: false);
                        PresentDraft(new DraftPreviewSpec(0, BuildSettings(blockSel, aim),
                            _draft.DraftChain, closing ? null : aim, closing), aim, false);
                    }
                    else if (_draft.AwaitingRim)
                    {
                        // A four-click shape's LAST stage. For a tapered volume the base and height are
                        // down and the crosshair now sets the lid's radius — the ghost's taper opens and
                        // closes live, and the one-way capture gate prevents the just-clicked, usually
                        // distant HEIGHT block from becoming a giant rim before the player has aimed back
                        // toward the lid. The Box's fourth click (v0.4.15) is a plain HEIGHT click and
                        // takes none of that: it has no lid radius to hold, flare, or close.
                        if (DraftManager.IsTaperedRimStage(_draft.Shape))
                        {
                            aim = StabilizeDraftRimAim(aim);
                            aim = ConstrainDraftRim(aim);
                        }
                        ObserveDraftMotion(aim, _draft.DraftFlatSideAligned);
                        aim = ClampDraftAimToPerGuideCap(aim);   // 0.2.19: ghost stops at the cap
                        PresentDraft(new DraftPreviewSpec(0, BuildSettings(blockSel, aim),
                            _draft.Shape, _draft.Constraint, _draft.DraftPlaneAxis,
                            _draft.DraftStart, _draft.DraftSecond,
                            sides: _draft.Sides, apex: _draft.DraftThird, rim: aim,
                            flatSideAligned: _draft.DraftFlatSideAligned),
                            aim, _draft.DraftFlatSideAligned);
                    }
                    else if (_draft.AwaitingApex)
                    {
                        // SHIFT while aiming a TRIANGLE apex (0.1.15): centre it on the base. The 3-click
                        // volumes (cylinder/cone/box) project the height onto their axis themselves, so
                        // SHIFT-centering doesn't apply to them.
                        if (ShiftHeld() && _draft.Shape == GuideShapeType.Triangle) aim = CenterApexOnBase(aim);
                        ObserveDraftMotion(aim, _draft.DraftFlatSideAligned);
                        aim = ClampDraftAimToPerGuideCap(aim);   // 0.2.19: ghost stops at the cap
                        PresentDraft(new DraftPreviewSpec(0, BuildSettings(blockSel, aim),
                            _draft.Shape, _draft.Constraint, _draft.DraftPlaneAxis,
                            _draft.DraftStart, _draft.DraftSecond,
                            sides: _draft.Sides, apex: aim,
                            flatSideAligned: _draft.DraftFlatSideAligned),
                            aim, _draft.DraftFlatSideAligned);
                    }
                    else
                    {
                        aim = ConstrainDraftBaseAim(aim);
                        bool flatSideAligned = GuideShapeTypes.UsesSides(_draft.Shape) && ShiftHeld();
                        ObserveDraftMotion(aim, flatSideAligned);
                        aim = ClampDraftAimToPerGuideCap(aim);   // 0.2.19: ghost stops at the cap
                        PresentDraft(new DraftPreviewSpec(0, BuildSettings(blockSel, aim),
                            _draft.Shape, _draft.Constraint, _draft.DraftPlaneAxis,
                            _draft.DraftStart, aim,
                            sides: _draft.Sides,
                            // Shift belongs exclusively to flat-side alignment for polygonal guides.
                            inverted: !GuideShapeTypes.UsesSides(_draft.Shape) && EffectiveInverted(),
                            flatSideAligned: flatSideAligned), aim, flatSideAligned);
                    }
                }
                else
                {
                    ResetDraftVisualState();
                    _hud.ClearDraftAim();
                    _renderer.ClearDraftPreview();   // no valid aim → no ghost
                }
            }
            else
            {
                if (_currentDraftSpec != null) ResetDraftVisualState();
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

            // Match the fourth placement stage when re-sculpting a tapered rim: the top cannot widen
            // beyond the base by accident, SHIFT deliberately permits a flare, and CTRL closes to a cone.
            if (_grab.PointIndex == 3 && IsTaperedVolume(g.ShapeType))
                target = ConstrainSculptRim(g, target);

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
                _hud.SetGrabMeasurement(_grab.GuideId, _renderer.CurrentGrabExtent);
            }

            // Throttled authoritative updates (~10 Hz), only when the position actually changed.
            long now = _capi.World.ElapsedMilliseconds;
            // A behemoth is edited as a local wireframe transaction. Streaming intermediate poses makes
            // the authority recount every historical size and can leave a costly packet backlog behind a
            // cancel. Only FinishRelease sends its final pose; cancellation sends no geometry at all.
            if (g.CachedVoxelCount > PreviewFullResVoxelThreshold) return;

            // ⚠️ NO TIERED INTERVAL HERE, deliberately. Two slower tiers used to be written just below this
            // line — 500 ms over 20,000 voxels, 250 ms over 8,000 — and BOTH WERE UNREACHABLE, because the
            // return above already sends nothing at all past 8,000 (PreviewFullResVoxelThreshold). Every
            // guide that reaches this line is under that, so the interval was always MoveSendIntervalMs and
            // the tiers were describing behaviour the code did not have. If graduated throttling is ever
            // wanted, it has to go ABOVE the early return, not below it.
            if (now - _grab.LastSendMs >= MoveSendIntervalMs &&
                (_grab.LastSentPos == null || !NearlySame(_grab.LastSentPos, target)))
            {
                _net.SendMovePoint(_grab.GuideId, _grab.PointIndex, target);
                _grab.LastSentPos = new Vec3d(target.X, target.Y, target.Z);
                _grab.LastSendMs = now;
            }
        }

        /// <summary>
        /// The cap clamp's search, spread ACROSS FRAMES instead of restarted every tick (v0.2.25).
        /// </summary>
        /// <remarks>
        /// The 0.2.19 clamp bisected from scratch on every tick: 14 full voxel counts per frame, each one
        /// walking the whole candidate lattice. Below the cap that cost nothing (the first check passes and
        /// returns), but the moment the aim crossed the cap it became ~14x — measured at 42-55 ms per tick
        /// for a 6-block cylinder at 1/16 scale, i.e. the game locking up exactly when the player pushes
        /// past the limit and stays there. Chasing a target that moves a few pixels per frame does not need
        /// a fresh 12-step bisection each time; it needs to remember where the boundary was.
        ///
        /// So this keeps ONE number — the reach (distance from the stationary reference) that last fitted —
        /// and each tick spends at most TWO fit-checks nudging it: reach a little further when there is
        /// headroom, pull in when there is not. It converges within a few frames, which at tick rate is
        /// imperceptible, and an unmoved aim costs ZERO checks. Shrinking back under the cap stays instant
        /// (the aim itself is tried first and wins outright). The reach is direction-agnostic on purpose:
        /// these shapes cost about the same in any direction, so swinging the view re-uses the same reach
        /// and simply re-verifies it.
        ///
        /// The unverified third step can leave the ghost a hair over cap for one frame. That is deliberate:
        /// the ghost is transient, Stage-A meshing makes drawing it cheap, and the next tick corrects it.
        /// The COMPLETING CLICK never relies on this — it re-checks through TryCompleteDraft / the server.
        /// </remarks>
        private sealed class CapClampTracker
        {
            private const int StepsPerTick = 2;             // fit-checks a single tick may spend

            // Unit-direction components are floored onto this many buckets per axis to form part of the
            // context key. Coarse on purpose: fine buckets would reset the bracket on every small cursor
            // jitter and make the ghost hunt, while coarse ones keep a normal drag in one context.
            private const double DirectionBuckets = 8.0;
            private const double Tolerance = 1.0 / 32.0;    // sub-voxel: tighter than this is not visible

            private long _key = long.MinValue;
            private double _lo;                             // largest reach known to FIT (0 = degenerate)
            private double _hi = double.MaxValue;           // smallest reach known to FAIL

            /// <summary>
            /// The largest point along reference→aim that passes <paramref name="fits"/>, spending at most
            /// <see cref="StepsPerTick"/> checks. <paramref name="key"/> identifies the search context
            /// (shape, scale, stage, anchor); when it changes the learned bracket is discarded.
            /// </summary>
            public Vec3d Clamp(long key, Vec3d reference, Vec3d aim, System.Func<Vec3d, bool> fits,
                int maxSteps = StepsPerTick)
            {
                double full = Dist(reference, aim);
                if (full < 1e-6) return Copy(aim);

                double dx = (aim.X - reference.X) / full;
                double dy = (aim.Y - reference.Y) / full;
                double dz = (aim.Z - reference.Z) / full;

                // AIM DIRECTION IS PART OF THE CONTEXT (v0.3.68 fix). _lo and _hi are distances along the
                // reference→aim ray, and both are only meaningful for THAT ray: the same reach in a
                // different direction can be a wholly different voxel count, because shapes are not
                // spherically symmetric. The key covered shape, scale, stage and anchor but NOT direction,
                // so a failure recorded while aiming one way became a permanent ceiling for every other
                // way — an invisible wall at an arbitrary distance, unrelated to the real cap. It gave way
                // only once the reference drifted far enough to change the key, which is exactly why
                // pushing at it repeatedly eventually broke through, and why a fresh draft helped until its
                // own first failure set a new ceiling.
                //
                // Direction is BUCKETED, not exact, so an ordinary drag keeps one context and still
                // converges for free. Cost stays bounded either way: the per-tick step budget caps the work
                // whether the bracket is fresh or settled, so resetting more often costs a little latency,
                // never frame time — which is the property the tracker was introduced to protect.
                key ^= ((long)Math.Floor(dx * DirectionBuckets) * 73856093L)
                     ^ ((long)Math.Floor(dy * DirectionBuckets) * 19349663L)
                     ^ ((long)Math.Floor(dz * DirectionBuckets) * 83492791L);

                if (key != _key) { _key = key; _lo = 0; _hi = double.MaxValue; }

                // Inside the reach already proven to fit FOR THIS RAY: a shorter reach along the same ray
                // cannot cost more, so the ghost follows the cursor exactly, for free.
                if (full <= _lo) return Copy(aim);
                Vec3d At(double d) => new Vec3d(reference.X + dx * d, reference.Y + dy * d, reference.Z + dz * d);

                maxSteps = Math.Max(0, Math.Min(StepsPerTick, maxSteps));
                // A throttled, brand-new context has no verified reach yet. Keep the current ghost for the
                // short interval until its scheduled check rather than collapsing it to the reference.
                if (maxSteps == 0 && _lo <= 0 && _hi == double.MaxValue) return Copy(aim);

                // With no known failure boundary, try the requested point first. In-cap movement therefore
                // costs one count and follows immediately; only an actual failure opens a bisection bracket.
                if (maxSteps > 0 && _hi == double.MaxValue)
                {
                    if (fits(aim)) { _lo = full; return Copy(aim); }
                    _hi = full;
                    maxSteps--;
                }

                // Narrow the standing bracket a couple of steps. _lo only rises and _hi only falls, so the
                // answer converges monotonically instead of hunting — once the bracket is tighter than a
                // sub-voxel the loop stops running and further cursor travel costs nothing at all.
                double hi = Math.Min(_hi, full);
                for (int i = 0; i < maxSteps && hi - _lo > Tolerance; i++)
                {
                    double mid = (_lo + hi) * 0.5;
                    if (fits(At(mid))) _lo = mid;
                    else { _hi = mid; hi = mid; }
                }

                return _lo >= full - 1e-6 ? Copy(aim) : Copy(At(_lo));
            }

            public void Reset() { _key = long.MinValue; _lo = 0; _hi = double.MaxValue; }

            private static Vec3d Copy(Vec3d v) => new Vec3d(v.X, v.Y, v.Z);
        }

        // Quantized position contribution to a clamp key — a moved anchor/reference restarts the search.
        private static long ClampKeyOf(Vec3d p) => p == null ? 0L
            : ((long)Math.Floor(p.X * 4) * 73856093) ^ ((long)Math.Floor(p.Y * 4) * 19349663)
              ^ ((long)Math.Floor(p.Z * 4) * 83492791);

        private Vec3d ClampDragTargetToPerGuideCap(GuideData guide, Vec3d requested)
        {
            // A behemoth's exact count is intentionally absent from the live client path. The authority still
            // validates throttled move packets and the final release; repeated local scans would reintroduce
            // the same lag-lock the wireframe preview removes.
            if (guide.CachedVoxelCount > PreviewFullResVoxelThreshold) return requested;

            int configuredCap = _net.PerGuideVoxelCap;
            int cap = configuredCap > 0
                ? Math.Min(configuredCap, GuideManager.HardVoxelCeiling)
                : GuideManager.HardVoxelCeiling;

            Vec3d current = guide.ControlPoints[_grab.PointIndex].WorldPosition;
            long key = ClampKeyOf(current) ^ (_grab.PointIndex * 6291469L)
                ^ (guide.VoxelScale * 2654435761L) ^ guide.Id.GetHashCode()
                ^ (guide.Divisions * 22801763L) ^ (guide.IsFilled ? 3298534883L : 0L)
                ^ (guide.IsWireframe ? 1099511628211L : 0L)
                ^ (cap * 51539607551L);
            return _dragClamp.Clamp(key, current, requested, c => PreviewFitsPerGuideCap(guide, c, cap));
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
                guide.Sides, guide.IsClosed, guide.FlatSideAligned);

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

            return guide.IsWireframe
                ? ShapeWireframe.GetVoxelCount(shape, guide.VoxelScale) <= cap
                : GuideShapeVoxelCounting.CountUpTo(
                    shape, guide.VoxelScale, guide.IsFilled, cap) <= cap;
        }

        /// <summary>
        /// Draft-ghost cap clamp (0.2.19): pulling a draft stops growing at the per-guide voxel cap.
        /// It used to *appear* to do this only because over-budget ghosts were too heavy to rebuild;
        /// Stage-A meshing made them cheap and the accidental stop vanished. This is the real mechanism,
        /// mirroring <see cref="ClampDragTargetToPerGuideCap"/>: the aimed point (the second anchor — or
        /// the apex/height/rim of a later-click draft) is pulled back toward its stationary reference to
        /// the largest size that still fits, so the ghost never shows an un-placeable guide and the
        /// completing click lands exactly on the cap. Chain drafts are excluded (corners are discrete
        /// clicks; the completion pre-check covers them). The search itself lives in
        /// <see cref="CapClampTracker"/>, which converges across frames — see its remarks for why.
        /// </summary>
        private Vec3d ClampDraftAimToPerGuideCap(Vec3d aim, bool allowScheduledCheck = true)
        {
            if (aim == null || _draft.DraftStart == null || DraftManager.IsChainShape(_draft.Shape))
                return aim;
            // The held 60% rim is the same geometry that already passed the height-stage clamp. Avoid
            // recounting it while the player is releasing/reacquiring the rim.
            if (_draft.AwaitingRim && !_rimAimArmed) return aim;

            int configuredCap = _net.PerGuideVoxelCap;
            // An unlimited public server has no configured boundary for the live ghost to chase. Avoid
            // repeatedly counting toward the much higher emergency render ceiling while aiming; the exact
            // completion/server checks still enforce that absolute safeguard once.
            if (configuredCap <= 0) return aim;
            int cap = Math.Min(configuredCap, GuideManager.HardVoxelCeiling);

            // Shrinking pulls the aim back toward a stationary reference chosen so that reference == the
            // SMALLEST form of the shape. For a rim stage that is the lid's centre ON THE AXIS, where the
            // taper collapses to a cone — emphatically NOT the raw height click, which generally sits off
            // to one side. Using the raw click (the v0.2.24/25 bug) made the reference itself a wide lid:
            // the minimum reachable top radius became the height click's own distance from the axis, so a
            // height clicked a few blocks to the side left the top flared open, unshrinkable, and stuck
            // over the cap with no way to finish the placement.
            Vec3d anchorRef =
                _draft.AwaitingRim && TryGetDraftLid(out Vec3d lidCentre, out _) ? lidCentre
                : _draft.AwaitingApex && _draft.DraftSecond != null ? _draft.DraftSecond
                : _draft.DraftStart;

            // The stage is part of the key: advancing base → height → rim restarts the search, since the
            // reach learned for one stage means nothing for the next.
            long key = ClampKeyOf(anchorRef)
                ^ ((long)_draft.Shape * 1000003L) ^ ((long)_draft.Constraint * 1000033L)
                ^ (_draft.Scale * 2654435761L) ^ (_draft.Sides * 40503L)
                ^ (_draft.Divisions * 22801763L) ^ (_draft.Filled ? 3298534883L : 0L)
                ^ (_draft.Wireframe ? 1099511628211L : 0L)
                ^ (_draft.AwaitingApex ? 5915587277L : 0L) ^ (_draft.AwaitingRim ? 1500450271L : 0L)
                ^ (cap * 51539607551L);

            return _draftClamp.Clamp(key, anchorRef, aim, c => DraftCandidateFits(c, cap),
                allowScheduledCheck ? DraftClampStepBudget() : 0);
        }

        private void ObserveDraftMotion(Vec3d aim, bool flatSideAligned)
        {
            if (aim == null) return;
            ulong h = 1469598103934665603UL;
            void Mix(long value) { unchecked { h ^= (ulong)value; h *= 1099511628211UL; } }
            Mix((long)Math.Floor(aim.X * 16.0));
            Mix((long)Math.Floor(aim.Y * 16.0));
            Mix((long)Math.Floor(aim.Z * 16.0));
            Mix((long)_draft.Shape); Mix((long)_draft.Constraint); Mix(_draft.Scale); Mix(_draft.Sides);
            Mix(_draft.AwaitingApex ? 1 : 0); Mix(_draft.AwaitingRim ? 1 : 0);
            Mix(flatSideAligned ? 1 : 0); Mix(ShiftHeld() ? 1 : 0); Mix(CtrlHeld() ? 1 : 0);
            if (h == _draftRawAimFingerprint) return;
            _draftRawAimFingerprint = h;
            _draftLastMotionMs = _capi.World.ElapsedMilliseconds;
        }

        /// <summary>
        /// One owner for every live-draft visual decision. Small poses keep their exact shell and HUD number;
        /// costly poses invalidate the number and use a wireframe. Once still, one background calculation
        /// builds the player's final selected-scale shell and reveals it in bounded scattered batches.
        /// </summary>
        private void PresentDraft(DraftPreviewSpec unversioned, Vec3d hudAim, bool flatSideAligned)
        {
            if (unversioned == null) return;
            ulong fingerprint = unversioned.Fingerprint();
            long now = _capi.World.ElapsedMilliseconds;

            if (_currentDraftSpec == null || fingerprint != _draftPoseFingerprint)
            {
                ulong adaptiveContext = DraftAdaptiveContext(unversioned);
                if (_draftAdaptiveContextFingerprint != adaptiveContext)
                {
                    _draftAdaptiveContextFingerprint = adaptiveContext;
                    _adaptiveMovingScale = unversioned.Settings.Scale;
                    _healthyMovingUpdates = 0;
                    _draftBaselineFrameMilliseconds = Math.Max(1.0, _renderer.SmoothedFrameMilliseconds);
                }

                _draftGeneration++;
                _draftPoseFingerprint = fingerprint;
                _acceptedDraftScale = 0;
                _acceptedDraftVoxelCount = -1;
                _draftLastMotionMs = now;
                _lastDraftRefineRequestMs = 0;
                _currentDraftSpec = unversioned.WithGeneration(_draftGeneration);

                _renderer.BeginDraftGeneration(_draftGeneration);
                MovingDraftPreviewResult moving = _renderer.ShowMovingDraft(
                    _currentDraftSpec, _adaptiveMovingScale);
                _hud.SetDraftCalculating(hudAim, flatSideAligned);

                if (moving == null)
                {
                    return;
                }

                _lastDraftWorkMilliseconds = Math.Max(1, moving.WorkMilliseconds);
                if (moving.IsFullShell)
                {
                    _acceptedDraftScale = moving.RenderScale;
                    _acceptedDraftVoxelCount = moving.VoxelCount;
                    _hud.SetDraftMeasurement(moving.Extent, moving.VoxelCount);
                }

                AdaptMovingDraftScale(moving);
                return;
            }

            // Keep the newest deep copy (e.g. harmless sub-cell movement) under the same generation.
            _currentDraftSpec = unversioned.WithGeneration(_draftGeneration);
            if (now - _draftLastMotionMs < DraftSettleDelayMs || _renderer.DraftRefinementBusy) return;

            int targetScale = _currentDraftSpec.Settings.Scale;
            if (_acceptedDraftScale == targetScale) return; // exact selected scale is already visible

            long interval = Math.Max(DraftMinimumRefineIntervalMs,
                Math.Min(1500L, Math.Max(1L, _lastDraftWorkMilliseconds) * 6L));

            // Frame time is a safety signal, not the sole controller. Preserve at least a 60-FPS budget,
            // or the pre-draft baseline plus 25%, whichever is more forgiving; recover without oscillating.
            double targetFrameMs = Math.Max(16.67, _draftBaselineFrameMilliseconds * 1.25);
            double pressure = _renderer.SmoothedFrameMilliseconds / targetFrameMs;
            if (pressure > 1.0) interval = (long)Math.Min(2000.0, interval * Math.Min(4.0, pressure * pressure));

            if (_lastDraftRefineRequestMs != 0 && now - _lastDraftRefineRequestMs < interval) return;
            if (_renderer.RequestDraftRefinement(_currentDraftSpec, targetScale))
                _lastDraftRefineRequestMs = now;
        }

        private void AdaptMovingDraftScale(MovingDraftPreviewResult moving)
        {
            double targetFrameMs = Math.Max(16.67, _draftBaselineFrameMilliseconds * 1.25);
            double pressure = _renderer.SmoothedFrameMilliseconds / targetFrameMs;
            bool expensive = moving.WorkMilliseconds >= 6 || pressure > 1.15;
            if (expensive)
            {
                int steps = moving.WorkMilliseconds >= 14 || pressure > 1.5 ? 2 : 1;
                int scale = Math.Max(_adaptiveMovingScale, moving.RenderScale);
                while (steps-- > 0) scale = NextCoarserDraftScale(scale);
                _adaptiveMovingScale = scale;
                _healthyMovingUpdates = 0;
                return;
            }

            if (moving.WorkMilliseconds > 3 || pressure > 0.95)
            {
                _healthyMovingUpdates = 0;
                return;
            }

            if (++_healthyMovingUpdates < 7) return;
            _healthyMovingUpdates = 0;
            _adaptiveMovingScale = NextFinerAdaptiveScale(
                _adaptiveMovingScale, _currentDraftSpec.Settings.Scale);
        }

        private static int NextCoarserDraftScale(int scale)
        {
            int[] scales = GuideData.ValidVoxelScales;
            for (int i = 0; i < scales.Length; i++)
                if (scales[i] > scale) return scales[i];
            return scales[scales.Length - 1];
        }

        private static int NextFinerAdaptiveScale(int scale, int targetScale)
        {
            if (scale <= targetScale) return targetScale;
            int[] scales = GuideData.ValidVoxelScales;
            for (int i = scales.Length - 1; i >= 0; i--)
                if (scales[i] < scale && scales[i] >= targetScale) return scales[i];
            return targetScale;
        }

        private static ulong DraftAdaptiveContext(DraftPreviewSpec spec)
        {
            ulong h = 1469598103934665603UL;
            void Mix(long value) { unchecked { h ^= (ulong)value; h *= 1099511628211UL; } }
            Mix((long)spec.ShapeType); Mix((long)spec.Constraint); Mix((long)spec.PlaneAxis);
            Mix(spec.Settings.Scale); Mix((long)spec.Settings.Mode); Mix(spec.Settings.Filled ? 1 : 0);
            Mix(spec.Settings.Wireframe ? 1 : 0);
            Mix((long)spec.Settings.Plane.FlattenedAxis); Mix(spec.Settings.Plane.PlaneOffset);
            Mix(spec.Settings.Divisions); Mix(spec.Sides); Mix(spec.Inverted ? 1 : 0);
            Mix(spec.FlatSideAligned ? 1 : 0); Mix(spec.IsChain ? 1 : 0); Mix(spec.ChainClosing ? 1 : 0);
            Mix(spec.Chain?.Count ?? 0); Mix(spec.Apex == null ? 0 : 1); Mix(spec.Rim == null ? 0 : 1);
            return h;
        }

        private void OnDraftPreviewCompleted(object sender, DraftPreviewCompletedEventArgs e)
        {
            if (e == null || e.Generation != _draftGeneration || _currentDraftSpec == null) return;
            _acceptedDraftScale = e.RenderScale;
            _acceptedDraftVoxelCount = e.VoxelCount;
            _lastDraftWorkMilliseconds = Math.Max(1, e.WorkMilliseconds);
            if (e.RenderScale == _currentDraftSpec.Settings.Scale)
                _hud.SetDraftMeasurement(e.Extent, e.VoxelCount);
        }

        private void OnPlacementMaterializationCompleted(GuideData guide)
        {
            ChalkEffects.PlacementEffects(_capi.World, guide);
        }

        private void ResetDraftVisualState()
        {
            _draftGeneration++;
            _draftPoseFingerprint = 0;
            _draftRawAimFingerprint = 0;
            _acceptedDraftScale = 0;
            _acceptedDraftVoxelCount = -1;
            _adaptiveMovingScale = 0;
            _healthyMovingUpdates = 0;
            _draftAdaptiveContextFingerprint = 0;
            _draftLastMotionMs = 0;
            _lastDraftRefineRequestMs = 0;
            _lastDraftWorkMilliseconds = 0;
            _currentDraftSpec = null;
            _renderer.BeginDraftGeneration(_draftGeneration);
        }

        private int DraftClampStepBudget()
        {
            long now = _capi.World.ElapsedMilliseconds;
            // Never perform exact cap scans while the cursor is moving. The last safe ghost remains visible,
            // and exact client/server completion validation is unchanged. Once still, converge gradually.
            if (_draftLastMotionMs != 0 && now - _draftLastMotionMs < DraftSettleDelayMs) return 0;
            long interval = Math.Max(120L, Math.Min(1000L,
                Math.Max(1L, _lastDraftWorkMilliseconds) * 4L));
            if (_lastDraftClampCheckMs != 0 && now - _lastDraftClampCheckMs < interval) return 0;
            _lastDraftClampCheckMs = now;
            return _lastDraftWorkMilliseconds <= 8 ? 2 : 1;
        }

        // Builds the candidate draft exactly as the HUD measure / completion pre-check do, and counts with
        // the shared threshold counter — an accepted ghost size therefore also passes the server's check.
        private bool DraftCandidateFits(Vec3d candidate, int cap)
        {
            try
            {
                Vec3d start = _draft.DraftStart;
                Vec3d end = _draft.AwaitingApex ? _draft.DraftSecond : candidate;
                IGuideShape shape = ShapeFactory.Create(
                    _draft.Shape, _draft.Constraint, _draft.DraftPlaneAxis, start, end,
                    sides: _draft.Sides,
                    flatSideAligned: _draft.AwaitingApex
                        ? _draft.DraftFlatSideAligned
                        : GuideShapeTypes.UsesSides(_draft.Shape) && ShiftHeld());
                Vec3d apex = _draft.AwaitingRim ? _draft.DraftThird
                    : _draft.AwaitingApex ? candidate : null;
                Vec3d rim = _draft.AwaitingRim ? candidate : null;
                DraftManager.ApplyPlacementPoints(shape, _draft.Shape, _draft.Constraint, apex, rim);

                return GuideShapeTypes.IsVolume(_draft.Shape) && _draft.Wireframe
                    ? ShapeWireframe.GetVoxelCount(shape, _draft.Scale) <= cap
                    : GuideShapeVoxelCounting.CountUpTo(
                        shape, _draft.Scale, _draft.Filled, cap) <= cap;
            }
            catch
            {
                return true;   // the clamp must never break drafting; the completion pre-check backstops
            }
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
            if (WarnIfRenderingDisabled()) return;

            if (_draft.Mode == ToolMode.Delete)
            {
                HandleDispelClick();
                return;
            }

            // MOVE mode: a live free-move settles here; otherwise a click SELECTS the guide to move (and an
            // empty click deselects), exactly as Edit does. Geometry is never touched in this mode.
            if (_draft.Mode == ToolMode.Transform)
            {
                if (FreeMoving) { CommitFreeMove(); return; }
                TargetHit moveHit = FindTarget(includeLockedPoints: true);
                if (moveHit.Found) _draft.SelectGuide(moveHit.GuideId);
                else _draft.ClearSelection();
                _gui.RefreshSelection();
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

            // Idle CTRL+click is an explicit placement override. Layout's guides have no engine selection
            // boxes, so the block selection still names the real block behind the translucent guide; skip
            // only our own guide hit-test and feed that block directly into first-anchor placement.
            if (CtrlHeld())
            {
                _draft.ClearSelection();
                HandleCreateClick(blockSel);
                return;
            }

            // 3./4. A guide under the crosshair outranks anchor placement.
            TargetHit hit = FindTarget(includeLockedPoints: false); // locked points are not grabbable
            if (hit.Found)
            {
                // The sampled curve is only a cheap candidate finder. A grab exists only when the view ray
                // actually enters a rendered guide voxel; this removes both point-radius forgiveness and
                // invisible parametric body-to-nearest-handle snapping from the left-click gesture. The
                // exact visible Dome base-rim exception is routed below.
                if (!_net.Guides.TryGetValue(hit.GuideId, out GuideData exactGuide)
                    || !TryFindFirstGuideVoxelHit(
                        exactGuide, out Vec3d exactCell, out int exactPoint))
                    hit = TargetHit.None;
                else
                    hit = new TargetHit(true, hit.GuideId, exactPoint, exactCell);
            }
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
                         && !GuideShapeTypes.SupportsBodyInsert(bodyG.ShapeType))
                {
                    // A parametric body voxel has no independent point to move. It must not silently grab
                    // some other handle. The Dome's base rim is the one intentional exception: historically
                    // its complete circumference acted as the diameter grip, which is essential when its two
                    // marker voxels are hidden behind the shell. The click is still required to hit an exact
                    // rendered voxel above, and the plane check below limits the mapping to the visible rim.
                    int handle = DomeBaseHandleAt(bodyG, hit.BodyPos);
                    if (handle >= 0 && !bodyG.ControlPoints[handle].IsLocked)
                        StartGrab(hit.GuideId, handle);
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
            if (!_toolHeld) return;
            if (WarnIfRenderingDisabled()) return;

            // Edit selection is client-side UI state. Right-click returns to an unselected Edit tool
            // without mutating the guide, matching the cancel/backtrack gesture used in Create.
            if (_draft.Mode == ToolMode.Edit)
            {
                if (_draft.SelectedGuideId != null)
                {
                    _draft.ClearSelection();
                    _gui.RefreshSelection();
                }
                return;
            }

            // MOVE mode: right-click is the same backtrack gesture as everywhere else — first it puts an
            // in-progress free-move back where it started, and only then does it drop the selection.
            if (_draft.Mode == ToolMode.Transform)
            {
                if (FreeMoving) { CancelFreeMove(); return; }
                if (_draft.SelectedGuideId != null)
                {
                    _draft.ClearSelection();
                    _gui.RefreshSelection();
                }
                return;
            }

            if (_draft.Mode != ToolMode.Create) return;

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
                _rimAimArmed = false;
                _rimAwaitingRelease = false;
                if (!draftSurvives)
                {
                    _net.SendDraftCancel();
                    _hud.ClearDraftAim();
                    _renderer.ClearDraftPreview();
                }
                ResetDraftVisualState();
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

            if (GuideShapeTypes.SupportsBodyInsert(g.ShapeType))
            {
                // B-S9-1 (v0.2.36): the rendered voxel actually entered by the ray is authoritative.
                // The old 2x point-radius + 1.5-voxel fallback made a neighboring body voxel lose to a
                // previously locked point. A point now toggles only when this exact visible cell is the
                // cell it owns; an adjacent body cell receives its own passive lock marker.
                if (!TryFindFirstGuideVoxelHit(g, out Vec3d lockCellCentre, out int cellPoint)) return;
                if (cellPoint >= 0)
                    _net.SendLockPoint(hit.GuideId, cellPoint, !g.ControlPoints[cellPoint].IsLocked);
                else
                    _net.SendInsertPoint(hit.GuideId, lockCellCentre, locked: true);
            }
            else if (hit.PointIndex >= 0)
            {
                bool locked = g.ControlPoints[hit.PointIndex].IsLocked;
                _net.SendLockPoint(hit.GuideId, hit.PointIndex, !locked);
            }
            else
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
        }

        // Which shape families take BODY INSERTS (clicking between points creates a new point there):
        // the arch (free spline) and, since 0.1.15, the Free-Shape (hand-placed polyline — inserting a
        // corner mid-segment is exactly how you refine one). Every other shape is parametric: left-click
        // body grabs do nothing except on the Dome's base rim, while right-click lock intent maps to the
        // nearest existing handle.
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

        // Maps an exact Dome base-rim voxel to one of its two diameter anchors. The tolerance covers the
        // centre of a voxel intersecting the mathematical base plane, including diagonally oriented domes;
        // curved-shell/rib voxels farther up the hemisphere remain ordinary non-grabbable body voxels.
        private static int DomeBaseHandleAt(GuideData g, Vec3d pos)
        {
            if (g == null || g.ShapeType != GuideShapeType.Dome || pos == null
                || g.ControlPoints == null || g.ControlPoints.Count < 3)
                return -1;

            if (g.ControlPoints[0]?.WorldPosition == null
                || g.ControlPoints[1]?.WorldPosition == null
                || g.ControlPoints[2]?.WorldPosition == null)
                return -1;

            Vec3d a = g.ControlPoints[0].WorldPosition;
            Vec3d b = g.ControlPoints[1].WorldPosition;
            Vec3d apex = g.ControlPoints[2].WorldPosition;
            var centre = new Vec3d(
                (a.X + b.X) * 0.5,
                (a.Y + b.Y) * 0.5,
                (a.Z + b.Z) * 0.5);
            double nx = apex.X - centre.X;
            double ny = apex.Y - centre.Y;
            double nz = apex.Z - centre.Z;
            double normalLength = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (normalLength < 1e-9) return -1;

            double px = pos.X - centre.X;
            double py = pos.Y - centre.Y;
            double pz = pos.Z - centre.Z;
            double planeDistance = Math.Abs(px * nx + py * ny + pz * nz) / normalLength;
            double cell = Math.Max(1, g.VoxelScale) / 16.0;
            if (planeDistance > cell * 1.5) return -1;

            double daX = pos.X - a.X, daY = pos.Y - a.Y, daZ = pos.Z - a.Z;
            double dbX = pos.X - b.X, dbY = pos.Y - b.Y, dbZ = pos.Z - b.Z;
            double da2 = daX * daX + daY * daY + daZ * daZ;
            double db2 = dbX * dbX + dbY * dbY + dbZ * dbZ;
            return da2 <= db2 ? 0 : 1;
        }

        // Body hit in Create: insert a new control point there and adopt it as a grab, one gesture. The
        // server acquires the lock for us, inserts, and broadcasts lock + point; we adopt the new point in
        // OnGuideAddedOrUpdated. (Rebuilt from the old right-click insert — B1: watch the log line below in
        // play if adoption ever fails silently again.)
        private void BeginBodyInsert(TargetHit hit)
        {
            if (_renderer.PlacementMaterializationBusy || _renderer.SculptMaterializationBusy)
            {
                Error("layout-guide-materializing",
                    "Wait for the immense guide to finish materializing before reshaping a guide.");
                return;
            }

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
            // The height press cannot also become the fourth click, even if a long sampling stall causes
            // its event to be delivered again after the draft advances to the rim stage.
            if (_draft.AwaitingRim && _rimAwaitingRelease) return;

            // Base/anchor clicks REQUIRE a block target (§5); the volume HEIGHT stage is the exception —
            // it may complete in free air (0.1.23), the view ray standing in for the click point.
            if (blockSel == null && !AwaitingVolumeHeight) return;

            bool embedPoint = !_draft.HasActiveDraft
                ? ShiftHeld()
                : _draft.DraftEmbedded
                    || (_draft.Shape == GuideShapeType.Roundover && ShiftHeld());
            Vec3d anchor = blockSel != null
                ? ResolveGuidePoint(blockSel, embedPoint) : FreeAirAim();
            // Base-stage modifiers: SHIFT makes a Line vertical; CTRL supplies the normal cardinal snap.
            // SHIFT wins if both are held. Multi-corner Free-Shape segments use their own equivalent helper.
            if (_draft.HasActiveDraft && !_draft.AwaitingApex
                && !DraftManager.IsChainShape(_draft.Shape))
                anchor = ConstrainDraftBaseAim(anchor);

            if (!_draft.HasActiveDraft)
            {
                if (_draft.Shape == GuideShapeType.Roundover && !_net.RoundoverPlacementSupported)
                {
                    Error("layout-roundoverprotocol",
                        "Fillet placement requires Layout 0.4.67 or newer on the server. Private placement remains available where the server permits it.");
                    return;
                }

                if (_renderer.PlacementMaterializationBusy || _renderer.SculptMaterializationBusy)
                {
                    Error("layout-guide-materializing",
                        "Wait for the immense guide to finish materializing before placing another guide.");
                    return;
                }

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
                _draft.StartDraft(anchor, AxisFromFace(blockSel), FaceIsNegative(blockSel),
                    embedded: embedPoint);
                _rimAimArmed = false;
                _rimAwaitingRelease = false;
                ResetDraftVisualState();
                _lastDraftClampCheckMs = 0;
                _draftClamp.Reset();
                _net.SendDraftStart(anchor, BuildSettings(blockSel, anchor));
                return;
            }

            // FREE-SHAPE (0.1.15): every click chains another corner. Clicking the FIRST corner (≥3
            // placed) closes the loop; clicking the LAST placed corner again — the practical double-click —
            // finishes it open. CTRL snaps each new segment relative to the PREVIOUS corner.
            if (DraftManager.IsChainShape(_draft.Shape))
            {
                Vec3d corner = _draft.Shape == GuideShapeType.Roundover
                    ? ResolveGuidePoint(blockSel, _draft.DraftEmbedded || ShiftHeld())
                    : ResolveGuidePoint(blockSel, _draft.DraftEmbedded);

                if (_draft.Shape == GuideShapeType.Roundover)
                {
                    if (_draft.AwaitingRoundoverProfileFirst || _draft.AwaitingRoundoverProfileSecond)
                    {
                        if (Dist(corner, _draft.DraftStart) < 1e-7)
                        {
                            Error("layout-roundoverprofile",
                                "Set each fillet side away from corner.");
                            return;
                        }
                        if (_draft.AwaitingRoundoverProfileSecond
                            && Dist(corner, _draft.RoundoverProfileFirst) < 1e-7)
                        {
                            Error("layout-roundoverprofile",
                                "Set second fillet side at a different point from first.");
                            return;
                        }
                        _draft.PlaceRoundoverProfilePoint(corner);
                        ResetDraftVisualState();
                        return;
                    }

                    if (Dist(corner, _draft.ChainLast) < 1e-7)
                    {
                        if (_draft.ChainCount >= 2) CompleteRoundover(blockSel);
                        return;
                    }

                    if (!_draft.AppendChainPoint(corner))
                    {
                        Error("layout-chaincap",
                            $"Route corner limit reached ({Shapes.RoundoverShape.MaxRoutePoints}). Finish or step back.");
                    }
                    return;
                }

                corner = ConstrainChainAim(corner);

                double snap = ChainSnapRadius();
                bool onFirst = _draft.ChainFirst != null && Dist(corner, _draft.ChainFirst) <= snap;
                bool onLast = _draft.ChainLast != null && Dist(corner, _draft.ChainLast) <= snap;

                if (_draft.Shape == GuideShapeType.FreeShape && onFirst && _draft.ChainCount >= 3)
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
                    int maximum = Shapes.FreeShape.MaxCorners;
                    Error("layout-chaincap",
                        $"Route corner limit reached ({maximum}). Finish or step back.");
                }
                return;
            }

            // THREE-CLICK TRIANGLE (Session 11): anchor · anchor · height. The second click stores the
            // base's far end (client-side only — nothing is committed yet); the ghost then shows the
            // triangle with its apex tracking the crosshair until the third click places it.
            if (!_draft.AwaitingApex && DraftManager.NeedsApexClick(_draft.Shape, _draft.Constraint))
            {
                _draft.PlaceSecondPoint(anchor,
                    GuideShapeTypes.UsesSides(_draft.Shape) && ShiftHeld());
                return;
            }

            // FOUR-CLICK SHAPES: the third click is stored and the draft moves on to its fourth stage —
            // the tapered volumes' rim, where the crosshair sets how wide the lid is (0.2.24), or the
            // Box's HEIGHT after its width (v0.4.15). Every other 3-click shape finishes here instead.
            if (_draft.AwaitingApex && !_draft.AwaitingRim && DraftManager.NeedsFourthClick(_draft.Shape))
            {
                _draft.PlaceThirdPoint(ClampDraftAimToPerGuideCap(anchor));
                // The one-way capture gate belongs to the tapered rim alone. A box's height click is an
                // ordinary height click: nothing to hold at 60%, so nothing to wait for a release over.
                bool taperedRim = DraftManager.IsTaperedRimStage(_draft.Shape);
                _rimAimArmed = !taperedRim;
                _rimAwaitingRelease = taperedRim;
                _draftClamp.Reset();
                return;
            }

            Vec3d apex = null;
            Vec3d rim = null;
            Vec3d end = anchor;
            if (_draft.AwaitingRim)
            {
                if (DraftManager.IsTaperedRimStage(_draft.Shape))
                {
                    rim = StabilizeDraftRimAim(anchor);  // held at 60% until the lid area captures the aim
                    rim = ConstrainDraftRim(rim);        // CTRL closes; SHIFT deliberately permits a flare
                }
                else
                {
                    rim = anchor;                        // the Box's fourth click is a plain height click
                }
                apex = _draft.DraftThird;            // the third click was fixed already
                end = _draft.DraftSecond;            // the base by the second
            }
            else if (_draft.AwaitingApex)
            {
                apex = anchor;                       // the third click IS the apex / height
                if (ShiftHeld() && _draft.Shape == GuideShapeType.Triangle)
                    apex = CenterApexOnBase(apex);   // 0.1.15: SHIFT centres a triangle apex on the base
                end = _draft.DraftSecond;            // the base was fixed by the second click
            }

            // 0.2.19: the completing click lands on the same clamped (≤ per-guide cap) size the ghost
            // showed — clicking while pulled past the cap places AT the cap instead of erroring out.
            if (_draft.AwaitingRim) rim = ClampDraftAimToPerGuideCap(rim, allowScheduledCheck: false);
            else if (_draft.AwaitingApex) apex = ClampDraftAimToPerGuideCap(apex, allowScheduledCheck: false);
            else end = ClampDraftAimToPerGuideCap(end, allowScheduledCheck: false);

            // SHIFT at the completing click bakes the inverted (upside-down) form — only meaningful for
            // the shapes that derive an "up" (arch family, equilateral triangle, Dome); apex-clicked
            // triangles take their height from the click itself. For a Dome the clicked face's SIGN is
            // folded in first, so it defaults away from the surface it was placed on (see EffectiveInverted).
            bool inverted = apex == null && EffectiveInverted();

            bool flatSideAligned = GuideShapeTypes.UsesSides(_draft.Shape)
                && (_draft.AwaitingApex ? _draft.DraftFlatSideAligned : ShiftHeld());
            GuideRenderSettings placementSettings = BuildSettings(blockSel, anchor);
            var candidateSpec = new DraftPreviewSpec(
                _draftGeneration, placementSettings,
                _draft.Shape, _draft.Constraint, _draft.DraftPlaneAxis,
                _draft.DraftStart, end,
                sides: _draft.Sides, inverted: inverted,
                apex: apex, rim: rim, flatSideAligned: flatSideAligned);
            bool candidateMatchesPreview = candidateSpec.Fingerprint() == _draftPoseFingerprint;
            int knownVoxelCount = candidateMatchesPreview
                && _acceptedDraftScale == placementSettings.Scale
                ? _acceptedDraftVoxelCount : -1;
            bool deferCapCheck = knownVoxelCount < 0
                && _net.AuthorityMode == ClientAuthorityMode.Networked
                && GuideShapeTypes.IsVolume(_draft.Shape)
                && !_draft.Wireframe;
            DraftCompletion completion = _draft.TryCompleteDraft(
                end, apex, inverted, rim, flatSideAligned,
                knownVoxelCount, deferCapCheck);
            if (completion.IsReady)
            {
                DraftPreviewSpec placementSpec = candidateSpec;

                // An immense selected-scale job can cross the placement seam at any stage: even a final click
                // that beats the next preview tick gets a bounded exact-pose scaffold before authority work.
                bool retainedExactPreview =
                    _renderer.RetainExactDraftForPlacement(placementSpec);

                _net.SendCreateRequest(completion.Start, completion.End, placementSettings,
                    _draft.Shape, _draft.Constraint, _draft.DraftPlaneAxis,
                    inverted, _draft.Sides, completion.Apex, rim: completion.Rim,
                    flatSideAligned: completion.FlatSideAligned,
                    deferPlacementEffects: retainedExactPreview);
                _draft.ClearDraft();
                _rimAimArmed = false;
                _rimAwaitingRelease = false;
                ResetDraftVisualState();
                _lastDraftClampCheckMs = 0;
                _hud.ClearDraftAim();
                if (!retainedExactPreview)
                    _renderer.ClearDraftPreview();   // cheap/unsettled path: real guide arrives via authority
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
                ResetDraftVisualState();
                _hud.ClearDraftAim();
                _renderer.ClearDraftPreview();
            }
            else if (completion.Status == DraftCompletionStatus.RejectedOverCap)
            {
                Error("layout-overcap",
                    $"Too large: {completion.VoxelCount:n0} voxels (cap {completion.CapLimit:n0}). Step back or coarsen the scale.");
            }
        }

        private void CompleteRoundover(BlockSelection blockSel)
        {
            List<Vec3d> chain = _draft.DraftChain;
            chain.Add(_draft.RoundoverProfileFirst);
            chain.Add(_draft.RoundoverProfileSecond);
            var shape = new Shapes.RoundoverShape(chain, hasTwoProfileHandles: true);
            if (GuideShapeVoxelCounting.CountUpTo(shape, _draft.Scale, false, 0) == 0)
            {
                Error("layout-roundoverradius",
                    "Fillet sides must stay away from corner, and sweep path cannot reverse directly back on itself.");
                return;
            }

            Vec3d lastAim = _draft.ChainLast;
            GuideRenderSettings settings = BuildSettings(blockSel, lastAim);
            var candidateSpec = new DraftPreviewSpec(
                _draftGeneration, settings, _draft.DraftChain,
                _draft.RoundoverProfileFirst, _draft.RoundoverProfileSecond,
                null, _draft.DraftPlaneAxis);
            bool candidateMatchesPreview = candidateSpec.Fingerprint() == _draftPoseFingerprint;
            int knownVoxelCount = candidateMatchesPreview
                && _acceptedDraftScale == settings.Scale ? _acceptedDraftVoxelCount : -1;
            bool deferCapCheck = knownVoxelCount < 0
                && _net.AuthorityMode == ClientAuthorityMode.Networked
                && !_draft.Wireframe;
            DraftCompletion completion = _draft.TryCompleteRoundover(knownVoxelCount, deferCapCheck);
            if (completion.IsReady)
            {
                bool retainedExactPreview = _renderer.RetainExactDraftForPlacement(candidateSpec);
                _net.SendCreateRequest(completion.Start, completion.End, settings,
                    GuideShapeType.Roundover, ShapeConstraint.None, _draft.DraftPlaneAxis,
                    inverted: false, sides: 0, apex: null,
                    chain: chain, closed: true,
                    deferPlacementEffects: retainedExactPreview);
                _draft.ClearDraft();
                ResetDraftVisualState();
                _hud.ClearDraftAim();
                if (!retainedExactPreview) _renderer.ClearDraftPreview();
            }
            else if (completion.Status == DraftCompletionStatus.RejectedOverCap)
            {
                Error("layout-overcap",
                    $"Too large: {completion.VoxelCount:n0} voxels (cap {completion.CapLimit:n0}). Choose a smaller radius, shorten the route, or coarsen the scale.");
            }
        }

        // The click-forgiveness radius for finishing a Free-Shape on an existing corner: the lock-snap
        // formula, floored a touch higher (clicked cells resolve to identical snapped coords, so this only
        // has to absorb an adjacent-cell miss).
        private double ChainSnapRadius() => Math.Max(0.25, _draft.Scale / 16.0 * 1.5);

        // The Free-Shape's segment constraints, keyed off the LAST placed corner: SHIFT (0.1.16,
        // human-requested) pins the next segment VERTICAL — same X/Z as that corner, height from the aim;
        // CTRL keeps the 0.1.15 horizontal level-and-cardinal snap. Holding both modifiers instead
        // selects a 45-degree vertical diagonal in the nearest cardinal plane.
        private Vec3d ConstrainChainAim(Vec3d aim)
        {
            Vec3d reference = _draft.ChainLast ?? _draft.DraftStart;
            if (reference == null) return aim;
            if (CtrlHeld() && ShiftHeld()) return ConstrainDiagonal(reference, aim);
            if (ShiftHeld()) return new Vec3d(reference.X, aim.Y, reference.Z);
            if (CtrlHeld()) return ConstrainTo(reference, aim);
            return aim;
        }

        // The 2D Line shares Free-Shape's world-vertical SHIFT constraint. CTRL retains the usual
        // level/cardinal base snap. Holding both selects a 45-degree vertical diagonal.
        private Vec3d ConstrainDraftBaseAim(Vec3d aim)
        {
            Vec3d start = _draft.DraftStart;
            if (start == null) return aim;
            if (_draft.Shape == GuideShapeType.Line && CtrlHeld() && ShiftHeld())
                return ConstrainDiagonal(start, aim);
            if (_draft.Shape == GuideShapeType.Line && ShiftHeld())
                return new Vec3d(start.X, aim.Y, start.Z);
            return CtrlHeld() ? ConstrainTo(start, aim) : aim;
        }

        // The final tapered-rim stage is safe by default: its radius cannot pass the existing base.
        // CTRL closes the rim to a point; SHIFT explicitly unlocks an outward flare. CTRL wins if both
        // are held, making the destructive/simplifying action deterministic.
        private Vec3d ConstrainDraftRim(Vec3d aim)
        {
            if (aim == null || !TryGetDraftRimFrame(
                out Vec3d lid, out Vec3d axis, out _, out double baseRadius)) return aim;
            if (CtrlHeld()) return lid;
            if (ShiftHeld()) return aim;

            double px = aim.X - lid.X, py = aim.Y - lid.Y, pz = aim.Z - lid.Z;
            double axial = px * axis.X + py * axis.Y + pz * axis.Z;
            double rx = px - axis.X * axial, ry = py - axis.Y * axial, rz = pz - axis.Z * axial;
            double radius = Math.Sqrt(rx * rx + ry * ry + rz * rz);
            if (radius <= baseRadius || radius < 1e-9) return aim;
            double factor = baseRadius / radius;
            return new Vec3d(lid.X + rx * factor, lid.Y + ry * factor, lid.Z + rz * factor);
        }

        private static bool IsTaperedVolume(GuideShapeType shape) =>
            shape == GuideShapeType.TaperedCylinder
            || shape == GuideShapeType.TaperedPolygonalPrism;

        private Vec3d ConstrainSculptRim(GuideData guide, Vec3d aim)
        {
            if (guide?.ControlPoints == null || guide.ControlPoints.Count < 4 || aim == null) return aim;

            Vec3d a = guide.ControlPoints[0]?.WorldPosition;
            Vec3d b = guide.ControlPoints[1]?.WorldPosition;
            Vec3d lid = guide.ControlPoints[2]?.WorldPosition;
            if (a == null || b == null || lid == null) return aim;
            if (CtrlHeld()) return new Vec3d(lid.X, lid.Y, lid.Z);
            if (ShiftHeld()) return aim;

            if (!ShapeGeometry.TryGetFrame(a, b, guide.ShapePlaneAxis,
                out Vec3d radial, out _, out double baseLength)) return aim;
            Vec3d axis = ShapeGeometry.BaseNormal(radial, guide.ShapePlaneAxis);
            if (axis == null) return aim;

            bool polygonal = guide.ShapeType == GuideShapeType.TaperedPolygonalPrism;
            int sides = PolygonShape.ClampSides(guide.Sides);
            double apothemRatio = Math.Cos(Math.PI / sides);
            double near = polygonal && guide.FlatSideAligned ? apothemRatio : 1.0;
            double far = !polygonal ? 1.0
                : guide.FlatSideAligned
                    ? (sides % 2 == 0 ? apothemRatio : 1.0)
                    : (sides % 2 == 0 ? 1.0 : apothemRatio);
            double baseRadius = baseLength / (near + far);

            double px = aim.X - lid.X, py = aim.Y - lid.Y, pz = aim.Z - lid.Z;
            double axial = px * axis.X + py * axis.Y + pz * axis.Z;
            double rx = px - axis.X * axial;
            double ry = py - axis.Y * axial;
            double rz = pz - axis.Z * axial;
            double radius = Math.Sqrt(rx * rx + ry * ry + rz * rz);
            if (radius <= baseRadius || radius < 1e-9) return aim;

            double factor = baseRadius / radius;
            return new Vec3d(lid.X + rx * factor, lid.Y + ry * factor, lid.Z + rz * factor);
        }

        // A 45-degree slope in the nearest north/south or east/west vertical plane. Averaging the
        // horizontal and vertical reaches avoids an abrupt size jump when the modifier is pressed.
        private static Vec3d ConstrainDiagonal(Vec3d reference, Vec3d aim)
        {
            double dx = aim.X - reference.X, dy = aim.Y - reference.Y, dz = aim.Z - reference.Z;
            bool useX = Math.Abs(dx) >= Math.Abs(dz);
            double horizontal = useX ? dx : dz;
            if (Math.Abs(horizontal) < 1e-9 && Math.Abs(dy) < 1e-9) return aim;
            double reach = (Math.Abs(horizontal) + Math.Abs(dy)) * 0.5;
            double horizontalSign = horizontal < 0 ? -1.0 : 1.0;
            double verticalSign = dy < 0 ? -1.0 : 1.0;
            return useX
                ? new Vec3d(reference.X + horizontalSign * reach,
                    reference.Y + verticalSign * reach, reference.Z)
                : new Vec3d(reference.X, reference.Y + verticalSign * reach,
                    reference.Z + horizontalSign * reach);
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
            if (_renderer.PlacementMaterializationBusy || _renderer.SculptMaterializationBusy)
            {
                Error("layout-guide-materializing",
                    "Wait for the immense guide to finish materializing before reshaping a guide.");
                return;
            }

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
            _hud.SetGrabMeasurement(guideId, _renderer.CurrentGrabExtent);
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

            Guid guideId = _grab.GuideId;
            // End the local render transaction first. Client-only cancellation applies synchronously and
            // releases the lock; otherwise that event could take the expensive ordinary-release path.
            DropGrabLocally(cancelled: true);
            _net.SendCancelGrab(guideId);
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
        private void DropGrabLocally(bool cancelled = false)
        {
            if (cancelled) _renderer.CancelGrabbedPoint();
            else _renderer.ClearGrabbedPoint();
            _hud.ClearGrabMeasurement();
            _grab = null;
            _dragClamp.Reset();     // the learned reach belonged to that point on that guide
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
            _rimAimArmed = false;
            _rimAwaitingRelease = false;
            ResetDraftVisualState();
            _lastDraftClampCheckMs = 0;
            _hasPendingInsert = false;
            _pendingInsertOriginPoints = null;
            _hud.ClearDraftAim();
            _hud.SetExaminedGuide(null);
            _currentTargetGuide = null;
            _renderer.ClearDraftPreview();
            _hud.SetComatoseDraft(false);
            if (!_toolHeld) _hud.TryClose();
        }

        /// <summary>Immediately dissolves public interaction state when an administrator jails this player.</summary>
        public void OnPublicGuidePolicyChanged(bool jailed)
        {
            if (!jailed) return;
            if (_grab != null && !_net.IsLocalGuide(_grab.GuideId)) CancelGrab();
            if (_draft.HasActiveDraft && _net.AuthorityMode == ClientAuthorityMode.Networked)
            {
                _net.SendDraftCancel();
                _draft.ClearDraft();
                _rimAimArmed = false;
                _rimAwaitingRelease = false;
                ResetDraftVisualState();
                _lastDraftClampCheckMs = 0;
                _hud.ClearDraftAim();
                _renderer.ClearDraftPreview();
            }
            if (_draft.SelectedGuideId.HasValue && !_net.IsLocalGuide(_draft.SelectedGuideId.Value))
                _draft.ClearSelection();
            if (!_draft.HasActiveDraft && !_toolHeld)
            {
                _hud.SetComatoseDraft(false);
                _hud.TryClose();
            }
        }

        /// <summary>
        /// Applies the personal rendering gate to tool state. Turning rendering off cancels any invisible
        /// draft or grab so it cannot retain a server lock or resume later without the player seeing it.
        /// </summary>
        public void OnRenderingChanged(bool enabled)
        {
            _hud.SetRenderingEnabled(enabled);
            // The GUI is told either way, and is NOT closed when guides go off (v0.4.2). Its settings page
            // now carries the switch that turns them back on, so closing the dialog stranded the player on
            // the chat command. It composes its tool page fully inert instead.
            _gui.SetRenderingEnabled(enabled);
            if (enabled) return;

            if (_grab != null) CancelGrab();
            if (_draft.HasActiveDraft)
            {
                _net.SendDraftCancel();
                _draft.ClearDraft();
            }
            _draft.ClearSelection();
            _rimAimArmed = false;
            _rimAwaitingRelease = false;
            ResetDraftVisualState();
            _lastDraftClampCheckMs = 0;
            _hasPendingInsert = false;
            _pendingInsertOriginPoints = null;
            _hud.ClearDraftAim();
            _hud.ClearGrabMeasurement();
            _hud.SetExaminedGuide(null);
            _currentTargetGuide = null;
            _renderer.ClearDraftPreview();
            _hud.SetComatoseDraft(false);
            if (!_toolHeld) _hud.TryClose();
        }

        private void OnGuideRemoved(Guid guideId)
        {
            if (_grab != null && _grab.GuideId == guideId) DropGrabLocally();
            if (_move != null && _move.GuideId == guideId) CancelFreeMove();
            if (_pendingMoveCommit == guideId) ClearPendingMoveCommit();
            if (_draft.SelectedGuideId == guideId) _draft.ClearSelection();
            if (_currentTargetGuide == guideId) _currentTargetGuide = null;
            if (_hasPendingInsert && _pendingInsertGuide == guideId)
            {
                _hasPendingInsert = false;
                _pendingInsertOriginPoints = null;
            }
        }

        private void OnGuideWhoRequested()
        {
            string description = DescribeCurrentGuide(out _);
            _capi.ShowChatMessage("[Layout] " + description);
        }

        /// <summary>Describes the selected Edit/Move guide first, then a grabbed or crosshair-targeted one.</summary>
        public string DescribeCurrentGuide(out bool found)
        {
            Guid? guideId = null;
            if ((_draft.Mode == ToolMode.Edit || _draft.Mode == ToolMode.Transform)
                && _draft.SelectedGuideId != null
                && _net.Guides.ContainsKey(_draft.SelectedGuideId.Value))
                guideId = _draft.SelectedGuideId;
            else if (_grab != null && _net.Guides.ContainsKey(_grab.GuideId))
                guideId = _grab.GuideId;
            else if (_currentTargetGuide != null && _net.Guides.ContainsKey(_currentTargetGuide.Value))
                guideId = _currentTargetGuide;

            if (guideId == null || !_net.Guides.TryGetValue(guideId.Value, out GuideData guide))
            {
                found = false;
                return "Select a guide in Edit mode or aim at one with the Chalking Kit equipped.";
            }

            found = true;
            string shape = GuideToolGui.ShapeDisplayName(guide.ShapeType, guide.Constraint);
            string privacy = _net.IsLocalGuide(guide.Id) ? " (Private)" : "";
            string creator = string.IsNullOrWhiteSpace(guide.CreatorName) ? "Unknown" : guide.CreatorName;
            string sculptor = string.IsNullOrWhiteSpace(guide.LastSculptorName)
                ? "Unknown" : guide.LastSculptorName;
            return shape + privacy + " — Creator: " + creator + "; Last Sculptor: " + sculptor + ".";
        }

        // Adopt a freshly-inserted point as a grab: the server gave us the lock as part of the insert, so
        // the moment the guide update lands (with our lock confirmed), the nearest point to where we clicked
        // IS the inserted point (the shape contract) — grab it without a further SendGrab.
        private void OnGuideAddedOrUpdated(GuideData g)
        {
            // A committed free-move keeps its render offset until the authority's answer lands, so the
            // guide does not flash back to its old position for the round trip. This fires for the accepted
            // move AND for the corrective resync of a refused one, which is exactly when the offset stops
            // being the truth either way.
            if (_pendingMoveCommit != Guid.Empty && g.Id == _pendingMoveCommit) ClearPendingMoveCommit();

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
            _hud.SetGrabMeasurement(g.Id, _renderer.CurrentGrabExtent);
            _capi.Logger.VerboseDebug("[Layout] body-insert adopted as grab: point {0} on {1}", nearest, g.Id);
        }

        // ==========================================================================================
        //  Hotkeys (delegated from the ModSystem; all gated to the active tool)
        // ==========================================================================================

        /// <summary>F: toggle the mode/settings GUI. Unhandled (falls through) when the tool isn't held.</summary>
        public bool OnToolGuiHotkey()
        {
            if (!IsToolActive()) return false;
            // Deliberately NOT gated on rendering (v0.4.2). Opening the panel is the one thing that must
            // still work while guides are hidden, because the switch to unhide them is on its settings page.
            // Everything the panel can DO is inert in that state; only the gear responds.
            if (_gui.IsOpened()) _gui.TryClose(); else _gui.TryOpen();
            return true;
        }

        /// <summary>Ctrl+Z: undo. Only consumed while the tool is held, so it never hijacks other UIs.</summary>
        public bool OnUndoHotkey()
        {
            if (!IsToolActive()) return false;
            if (WarnIfRenderingDisabled()) return true;
            _net.SendUndo();
            return true;
        }

        /// <summary>Ctrl+Y: redo. Same held-tool gate as undo.</summary>
        public bool OnRedoHotkey()
        {
            if (!IsToolActive()) return false;
            if (WarnIfRenderingDisabled()) return true;
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
                // This is a cheap candidate test for the 33 Hz HUD loop, not permission to grab. Bound its
                // halo to one physical guide cell: a cube's centre-to-corner distance is sqrt(3)/2 of its
                // edge. The exact click path still ray-tests the rendered voxel before acting. Fixed 0.10 /
                // 0.18-block floors made a scale-1 guide targetable several cells outside what was visible.
                double cellRadius = voxel * 0.8660254037844386;
                double pointRadius = cellRadius * pointRadiusScale;
                double bodyRadius = cellRadius;

                // BROAD PHASE, before any per-point or per-segment work (v0.4.40).
                if (!WithinTargetingReach(g, origin, dir, Math.Max(pointRadius, bodyRadius))) continue;

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

        /// <summary>
        /// Cheap rejection for a guide the view ray cannot possibly hit within <see cref="MaxReach"/>.
        /// </summary>
        /// <remarks>
        /// THE MIRROR HOLDS EVERY GUIDE IN THE WORLD, and on a settled server that is most of them —
        /// but nothing past twelve blocks is targetable at all. Without this, <see cref="FindTarget"/>
        /// paid the full price for every one of them, 33 times a second: a fingerprint hash over each
        /// guide's control points, a resample on any miss, and a ray test against up to 512 curve
        /// segments. That cost grew with the WORLD rather than with what is in front of the player, which
        /// is the shape of thing that is fine on the machine it was written on and miserable on a server
        /// two months old.
        ///
        /// The box is built from the control points — phantoms INCLUDED, since they steer the curve's
        /// ends and an arch's sit below its feet — and then grown generously before it is trusted to
        /// reject anything. A Catmull-Rom spline bows outside the hull of its own control points, so the
        /// padding carries a quarter of the guide's largest dimension on top of the pick radius. That is
        /// far looser than the real overshoot; it still rejects everything that is merely far away, and
        /// this must never make a guide the player can SEE unclickable. Rejecting nothing costs one pass
        /// over the points, which is cheaper than the fingerprint hash it saves.
        /// </remarks>
        private bool WithinTargetingReach(GuideData g, Vec3d origin, Vec3d dir, double margin)
        {
            List<ControlPoint> points = g.ControlPoints;
            if (points == null || points.Count == 0) return false;

            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            for (int i = 0; i < points.Count; i++)
            {
                Vec3d p = points[i]?.WorldPosition;
                if (p == null) continue;
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
                if (p.Z < minZ) minZ = p.Z;
                if (p.Z > maxZ) maxZ = p.Z;
            }
            if (minX > maxX) return false;      // no usable points at all

            double span = Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ));
            double pad = margin + Math.Max(1.0, span * 0.25);

            return RayAabbEntry(origin, dir,
                       minX - pad, minY - pad, minZ - pad,
                       maxX + pad, maxY + pad, maxZ + pad, out double entry)
                   && entry <= MaxReach;
        }

        // Exact guide-voxel picker for grabs and B-S9-1 lock-in-place. It intentionally samples the outline
        // even when the guide is filled: body targeting means the defining curve, not arbitrary interior
        // fill cells. This work happens only on a click, never in the per-tick targeting loop.
        private bool TryFindFirstGuideVoxelHit(GuideData guide, out Vec3d cellCentre, out int pointIndex)
        {
            cellCentre = null;
            pointIndex = -1;
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
            VoxelPosition hitVoxel = default;
            bool found = false;

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
                    hitVoxel = voxel;
                    found = true;
                }
            }

            if (!found) return false;
            pointIndex = FindPointOwningVoxel(guide, voxels, hitVoxel, surface, flatAxis, plane);
            return true;
        }

        // Assigns the clicked rendered cell to a control point only when it is that point's nearest visible
        // outline cell—the same one-cell marker model used by the shapes. This keeps easy whole-voxel
        // unlocking while removing the old radius shadow over immediately adjacent cells.
        private static int FindPointOwningVoxel(GuideData guide, List<VoxelPosition> voxels,
            VoxelPosition clicked, bool surface, PlaneAxis flatAxis, double plane)
        {
            if (guide.ShapeType == GuideShapeType.Roundover && !guide.IsWireframe)
            {
                // Roundover routes may combine a large shell with many route controls. The generic
                // nearest-cell ownership below is O(points × voxels) per crosshair hit. This shape exposes
                // exact sampled marker positions, keeping the work bounded by the small route itself.
                var roundover = ShapeFactory.Adopt(guide) as RoundoverShape;
                int fastOwner = -1, fastOwnerRole = -1;
                for (int i = 0; i < guide.ControlPoints.Count; i++)
                {
                    ControlPoint point = guide.ControlPoints[i];
                    if (point == null || point.IsPhantom
                        || !roundover.TryGetMarkerPosition(i, guide.VoxelScale, out Vec3d marker))
                        continue;
                    int x = (int)Math.Floor(marker.X * 16.0 / guide.VoxelScale) * guide.VoxelScale;
                    int y = (int)Math.Floor(marker.Y * 16.0 / guide.VoxelScale) * guide.VoxelScale;
                    int z = (int)Math.Floor(marker.Z * 16.0 / guide.VoxelScale) * guide.VoxelScale;
                    if (x != clicked.X || y != clicked.Y || z != clicked.Z) continue;
                    int role = point.IsLocked ? 3 : point.IsPrimary ? 2 : point.IsAnchor ? 1 : 0;
                    if (role > fastOwnerRole) { fastOwner = i; fastOwnerRole = role; }
                }
                return fastOwner;
            }

            double edge = guide.VoxelScale / 16.0;
            double half = edge * 0.5;
            int owner = -1;
            int ownerRole = -1;
            double ownerDistance = double.MaxValue;

            for (int pointIndex = 0; pointIndex < guide.ControlPoints.Count; pointIndex++)
            {
                ControlPoint point = guide.ControlPoints[pointIndex];
                if (point.IsPhantom || (point.IsLockMarker && !point.IsLocked)) continue;

                bool hasNearest = false;
                VoxelPosition nearest = default;
                double nearestDistance = double.MaxValue;
                HashSet<(int, int)> seenSurfaceCells = surface ? new HashSet<(int, int)>() : null;

                foreach (VoxelPosition voxel in voxels)
                {
                    if (surface && !seenSurfaceCells.Add(VisibleCellKey(voxel, flatAxis))) continue;

                    double cx = voxel.X / 16.0 + half;
                    double cy = voxel.Y / 16.0 + half;
                    double cz = voxel.Z / 16.0 + half;
                    if (surface)
                    {
                        if (flatAxis == PlaneAxis.X) cx = plane;
                        else if (flatAxis == PlaneAxis.Y) cy = plane;
                        else cz = plane;
                    }

                    Vec3d p = point.WorldPosition;
                    double dx = cx - p.X, dy = cy - p.Y, dz = cz - p.Z;
                    double distance = dx * dx + dy * dy + dz * dz;
                    if (distance < nearestDistance)
                    {
                        nearestDistance = distance;
                        nearest = voxel;
                        hasNearest = true;
                    }
                }

                if (!hasNearest || !SameVisibleCell(nearest, clicked, surface, flatAxis)) continue;

                int role = point.IsLocked ? 3 : point.IsPrimary ? 2 : point.IsAnchor ? 1 : 0;
                if (role > ownerRole || (role == ownerRole && nearestDistance < ownerDistance))
                {
                    owner = pointIndex;
                    ownerRole = role;
                    ownerDistance = nearestDistance;
                }
            }

            return owner;
        }

        private static (int, int) VisibleCellKey(VoxelPosition voxel, PlaneAxis flatAxis) =>
            flatAxis == PlaneAxis.X ? (voxel.Y, voxel.Z)
            : flatAxis == PlaneAxis.Y ? (voxel.X, voxel.Z)
            : (voxel.X, voxel.Y);

        private static bool SameVisibleCell(VoxelPosition a, VoxelPosition b,
            bool surface, PlaneAxis flatAxis) => surface
                ? VisibleCellKey(a, flatAxis) == VisibleCellKey(b, flatAxis)
                : a.X == b.X && a.Y == b.Y && a.Z == b.Z;

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

        // Embedded placement chooses the material-side voxel without changing either tangential coordinate.
        // Volumetric anchors move exactly one selected-scale cell from the ordinary outside-cell centre to
        // the matching centre inside the targeted block. Surface mode starts on the face plane, so half a
        // cell reaches that same material-side centre.
        private Vec3d ResolveGuidePoint(BlockSelection blockSel, bool embed)
        {
            Vec3d point = ResolveAnchorPoint(blockSel);
            if (!embed || blockSel?.Face == null) return point;

            Vec3i normal = blockSel.Face.Normali;
            double cell = _draft.Scale / 16.0;
            double inward = _draft.Projection == ProjectionMode.Surface ? cell * 0.5 : cell;
            return new Vec3d(point.X - normal.X * inward,
                point.Y - normal.Y * inward,
                point.Z - normal.Z * inward);
        }

        // True when the active draft is on a free-air-capable stage of a 3D volume: the HEIGHT stage
        // (cylinder/cone, base placed) or — 0.2.24 — the Tapered Cylinder's RIM stage after it.
        private bool AwaitingVolumeHeight
        {
            get
            {
                if (!GuideShapeTypes.IsVolume(_draft.Shape) || !_draft.AwaitingApex) return false;
                // v0.4.15: the Box's third click is its base WIDTH — an in-plane click like the two base
                // clicks, so it needs a real block just as they do. Its height moved to the fourth click,
                // and that one is free-air capable like every other volume height.
                if (_draft.Shape == GuideShapeType.Box) return _draft.AwaitingRim;
                return true;
            }
        }

        // The free-air aim for whichever stage the draft is on: a TAPERED rim stage reads the lid plane,
        // every other free-air-capable stage (the Box's fourth click included) reads the height ray.
        private Vec3d FreeAirAim() =>
            _draft.AwaitingRim && DraftManager.IsTaperedRimStage(_draft.Shape)
                ? FreeAirRimAim() : FreeAirHeightAim();

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

        // Full frame needed by the rim capture gate: lid centre, axis, stable radial direction, base radius.
        private bool TryGetDraftRimFrame(out Vec3d lid, out Vec3d axis, out Vec3d radial,
            out double baseRadius)
        {
            lid = axis = radial = null;
            baseRadius = 0;
            Vec3d a = _draft.DraftStart, b = _draft.DraftSecond, third = _draft.DraftThird;
            if (a == null || b == null || third == null) return false;
            if (!ShapeGeometry.TryGetFrame(a, b, _draft.DraftPlaneAxis,
                out radial, out _, out double baseLength))
                return false;
            axis = ShapeGeometry.BaseNormal(radial, _draft.DraftPlaneAxis);
            if (axis == null) return false;
            bool polygonal = _draft.Shape == GuideShapeType.TaperedPolygonalPrism;
            int sides = PolygonShape.ClampSides(_draft.Sides);
            double apothemRatio = Math.Cos(Math.PI / sides);
            double near = polygonal && _draft.DraftFlatSideAligned ? apothemRatio : 1.0;
            double far = !polygonal ? 1.0
                : _draft.DraftFlatSideAligned
                    ? (sides % 2 == 0 ? apothemRatio : 1.0)
                    : (sides % 2 == 0 ? 1.0 : apothemRatio);
            baseRadius = baseLength / (near + far);

            var centre = new Vec3d(a.X + radial.X * baseRadius * near,
                a.Y + radial.Y * baseRadius * near, a.Z + radial.Z * baseRadius * near);
            double h = (third.X - centre.X) * axis.X + (third.Y - centre.Y) * axis.Y
                + (third.Z - centre.Z) * axis.Z;
            lid = new Vec3d(centre.X + axis.X * h, centre.Y + axis.Y * h, centre.Z + axis.Z * h);
            return true;
        }

        /// <summary>
        /// The active draft's LID CENTRE — the height click projected onto the base's axis — plus that
        /// axis. The rim stage measures and shrinks against this, never against the raw height click,
        /// which is a world click and so generally sits off to one side of the axis.
        /// </summary>
        private bool TryGetDraftLid(out Vec3d lid, out Vec3d axis) =>
            TryGetDraftRimFrame(out lid, out axis, out _, out _);

        // One-way rim capture (0.2.31): first wait for the height press to be released, then acquire the
        // existing 60% top ring through a modest annular band. Unlike the old 125%-of-base disk, this does
        // not become an enormous automatic-capture zone on a large guide. Once acquired, normal absolute
        // control stays armed so the player can move inward to a cone or outward to a deliberate flare.
        private Vec3d StabilizeDraftRimAim(Vec3d requested)
        {
            if (requested == null || !TryGetDraftRimFrame(
                out Vec3d lid, out Vec3d axis, out Vec3d radial, out double baseRadius))
                return requested;

            double heldRadius = baseRadius * TaperedCylinderShape.DefaultTopRatio;
            Vec3d HeldAim() => new Vec3d(lid.X + radial.X * heldRadius,
                lid.Y + radial.Y * heldRadius, lid.Z + radial.Z * heldRadius);

            if (_rimAwaitingRelease) return HeldAim();

            // Shift is an explicit flare gesture, so it also deliberately bypasses the normal annular
            // capture step once the height click has physically been released.
            if (ShiftHeld())
            {
                _rimAimArmed = true;
                _draftClamp.Reset();
                return requested;
            }

            if (!_rimAimArmed)
            {
                double px = requested.X - lid.X, py = requested.Y - lid.Y, pz = requested.Z - lid.Z;
                double axial = px * axis.X + py * axis.Y + pz * axis.Z;
                double rx = px - axis.X * axial, ry = py - axis.Y * axial, rz = pz - axis.Z * axial;
                double requestedRadius = Math.Sqrt(rx * rx + ry * ry + rz * rz);

                double cell = _draft.Scale / 16.0;
                double captureBand = Math.Max(0.25,
                    Math.Max(cell * 1.5, Math.Min(1.0, baseRadius * 0.1)));
                if (Math.Abs(requestedRadius - heldRadius) <= captureBand)
                {
                    _rimAimArmed = true;
                    _draftClamp.Reset();
                }
                else
                {
                    return HeldAim();
                }
            }

            return requested;
        }

        // The free-air RIM aim (0.2.26): on the Tapered Cylinder's last stage, with no block under the
        // crosshair, the view ray is sampled at the LID's distance — so sweeping the crosshair across the
        // lid sweeps the top radius smoothly, and the shape's own axis projection turns that point into a
        // width. The generic free-air height aim samples at the BASE's distance instead, which for a tall
        // tower barely moves the taper at all: the original "it needs a block" symptom.
        //
        // v0.2.25 tried intersecting the ray with the lid's PLANE. That reads well from above but is
        // violently unstable from the ground: looking near-parallel to the lid sends the intersection off
        // toward the horizon, so a pixel of view movement swung the radius by blocks and pinned it at the
        // flare limit — the "finicky, flares out immensely" report. Sampling at a fixed depth cannot blow
        // up at any viewing angle, which matters more here than pointing exactly at the rim.
        private Vec3d FreeAirRimAim()
        {
            if (!TryGetDraftLid(out Vec3d lid, out _)) return FreeAirHeightAim();
            Vec3d eye = EyePos(), dir = ViewDir();
            double depth = Math.Max(1.0, Dist(eye, lid));
            return new Vec3d(eye.X + dir.X * depth, eye.Y + dir.Y * depth, eye.Z + dir.Z * depth);
        }

        // The CTRL cardinal constraint, generalised (Session-8 playtest fix): snap the aim onto the
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

        // True when the clicked face's outward normal points toward its axis's NEGATIVE side (a ceiling,
        // a north or west wall face). PlaneAxis alone cannot carry this sign.
        private static bool FaceIsNegative(BlockSelection blockSel)
        {
            if (blockSel?.Face == null) return false;
            Vec3i n = blockSel.Face.Normali;
            return n.X + n.Y + n.Z < 0;      // exactly one component is nonzero
        }

        /// <summary>
        /// The draft's effective inversion: SHIFT is the player's invert, and for the DOME — the one shape
        /// that rises OUT of its clicked plane — the first click's face sign is folded in first, so a dome
        /// placed on a ceiling or the far side of a wall defaults AWAY from that surface instead of always
        /// growing toward the axis's positive side (0.2.11 fix; SHIFT still inverts relative to that).
        /// </summary>
        private bool EffectiveInverted()
        {
            bool inv = ShiftHeld();
            if (_draft.Shape == GuideShapeType.Dome && _draft.DraftPlaneNegative) inv = !inv;
            return inv;
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
                    settings.Plane, settings.Filled, 0, settings.Wireframe);

            return settings;
        }

        private GuideRenderSettings RoundoverSweepPreviewSettings(
            BlockSelection blockSel, Vec3d anchor)
        {
            GuideRenderSettings settings = BuildSettings(blockSel, anchor);
            return new GuideRenderSettings(settings.Scale, settings.Mode, settings.Plane,
                settings.Filled, settings.Divisions, wireframe: true);
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
            _net.GuideWhoRequested -= OnGuideWhoRequested;
            _renderer.DraftPreviewCompleted -= OnDraftPreviewCompleted;
            _renderer.PlacementMaterializationCompleted -= OnPlacementMaterializationCompleted;
            _capi.Input.InWorldAction -= OnInWorldAction;
            _capi.Event.MouseDown -= OnMouseDown;
            _capi.Event.MouseUp -= OnMouseUp;

            if (_tickId != 0)
            {
                _capi.Event.UnregisterGameTickListener(_tickId);
                _tickId = 0;
            }
        }
    }
}
