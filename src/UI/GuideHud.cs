using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Layout.Client;
using Layout.Guide;
using Layout.Network;
using Layout.Shapes;
using Layout.Systems;

namespace Layout.UI
{
    // =====================================================================================
    //  GuideHud  —  Module 6 (UI layer)
    // -------------------------------------------------------------------------------------
    //  A small, always-on status panel anchored to the left-middle of the screen. It never
    //  takes focus and never ungrabs the mouse, so it coexists with normal play and with
    //  the modal GuideToolGui. It shows, at a glance:
    //     mode / scale / projection(+plane) / fill        (tool state, always)
    //     dimensions of what you're aiming at             (draft OR examined guide)
    //     the examined guide's id + editability + count   (examine only)
    //     a voxel-cap gauge with a near/over-cap warning  (draft OR examine)
    //
    //  Draft has priority over examine: while you're mid-placement (second foot not yet
    //  clicked) the dims/gauge describe the *pending* arch; otherwise they describe the
    //  guide currently under your crosshair.
    //
    //  ---- Tool-driven seams (Module 7 drives these; mirrors Module 5's SetGrabbedPoint) --
    //  The HUD does not raycast. The held tool feeds it:
    //     SetDraftAim(Vec3d) / ClearDraftAim()   – the live "where the 2nd foot would land"
    //                                              point while a draft is active.
    //     SetExaminedGuide(Guid?)                – the guide currently under the crosshair
    //                                              (null when not looking at one).
    //
    //  Compose-once model: the panel is composed a single time with a fixed set of
    //  dynamic-text lines (seven, some blanked when inapplicable) at a fixed width, so it
    //  never resizes or flickers as values change. A light 150 ms tick plus targeted event
    //  subscriptions keep the text current; measurement of arch extents is cached and only
    //  recomputed when the relevant inputs actually change.
    //
    //  ---- Vintage Story GUI API touchpoints (verified only by local `dotnet build`) ----
    //    - GuiDialog property overrides: DialogType (EnumDialogType.HUD), Focusable,
    //      PrefersUngrabbedMouse, DrawOrder, ToggleKeyCombinationCode
    //    - GuiDialog.Dispose() override (GuiDialog is IDisposable with a virtual Dispose in
    //      the current API). If a given build lacks a virtual Dispose, delete that override
    //      entirely — OnGuiClosed already performs the same tick-listener/subscription
    //      teardown, so nothing is lost.
    //    - capi.Gui.CreateCompo(string, ElementBounds) -> GuiComposer
    //    - GuiComposer.AddShadedDialogBG(ElementBounds, bool withTitleBar==false)
    //    - GuiComposer.AddDynamicText(text, CairoFont, ElementBounds, string key)
    //    - SingleComposer.GetDynamicText(key).SetNewText(string)
    //    - ElementStdBounds.AutosizedMainDialog ; EnumDialogArea.LeftMiddle
    //    - GuiStyle.{ElementToDialogPadding, DialogToScreenPadding}
    //    - CairoFont.WhiteSmallText()
    //    - capi.Event.RegisterGameTickListener(Action<float>, int) / UnregisterGameTickListener(long)
    //    - capi.World.ElapsedMilliseconds
    //  Each is localized to this file if a signature differs on the installed DLL.
    // =====================================================================================
    public class GuideHud : GuiDialog
    {
        private readonly DraftManager _tool;
        private readonly ClientNetworkHandler _net;

        // --- tool-driven inputs ---
        private Vec3d _draftAim;          // live second-foot aim while drafting (deep-copied)
        private Guid? _examinedGuide;     // guide under the crosshair, if any

        // --- cap-warning flash state (fed by VoxelCapWarningReceived) ---
        private Guid _warnGuide = Guid.Empty;
        private int _warnCount;
        private int _warnCap;
        private long _warnUntil;          // ElapsedMilliseconds deadline for the flash

        // --- measurement caches ---
        // Draft: keyed by the quantized aim point + scale, so tiny sub-1/16 jitters don't
        // trigger a rebuild every frame.
        private long _draftKeyX = long.MinValue, _draftKeyY = long.MinValue, _draftKeyZ = long.MinValue;
        private int _draftKeyScale = int.MinValue;
        private GuideExtent _draftExtent = GuideExtent.Empty;
        private int _draftVoxelCount;

        // Examine: coarse cache, invalidated whenever the examined guide changes or the
        // server reports an update to it (GuideAddedOrUpdated).
        private Guid _measuredGuide = Guid.Empty;
        private GuideExtent _guideExtent = GuideExtent.Empty;
        private int _guideVoxelCount;

        private long _tickId;
        private bool _subscribed;

        // The HUD's copy of the Current Shape chip (0.1.16): which "-current" glyph is composed right
        // now, or null when hidden (non-Create modes). A change recomposes the HUD (rare — shape picks).
        private string _chipIcon;
        private bool _showClientOnlyIndicator;

        public GuideHud(ICoreClientAPI capi, DraftManager tool, ClientNetworkHandler net) : base(capi)
        {
            _tool = tool;
            _net = net;
        }

        // Never auto-toggled; the tool shows/hides it.
        public override string ToggleKeyCombinationCode => null;
        public override EnumDialogType DialogType => EnumDialogType.HUD;
        public override bool Focusable => false;
        public override bool PrefersUngrabbedMouse => false;
        public override double DrawOrder => 0.1;

        // ---------------------------------------------------------------------------------
        //  Lifecycle
        // ---------------------------------------------------------------------------------
        public override void OnGuiOpened()
        {
            base.OnGuiOpened();
            Subscribe();
            SetupHud();
            _tickId = capi.Event.RegisterGameTickListener(_ => RefreshText(), 150);
            RefreshText();
        }

        public override void OnGuiClosed()
        {
            if (_tickId != 0)
            {
                capi.Event.UnregisterGameTickListener(_tickId);
                _tickId = 0;
            }
            Unsubscribe();
            base.OnGuiClosed();
        }

        public override void Dispose()
        {
            // Belt-and-suspenders: ensure we never leak the tick listener / subscriptions
            // even if the dialog is disposed without a clean OnGuiClosed.
            if (_tickId != 0)
            {
                capi.Event.UnregisterGameTickListener(_tickId);
                _tickId = 0;
            }
            Unsubscribe();
            base.Dispose();
        }

        // ---------------------------------------------------------------------------------
        //  Tool-driven seams
        // ---------------------------------------------------------------------------------
        public void SetDraftAim(Vec3d aim)
        {
            _draftAim = aim == null ? null : new Vec3d(aim.X, aim.Y, aim.Z);
            RefreshText();
        }

        public void ClearDraftAim()
        {
            _draftAim = null;
            RefreshText();
        }

        public void SetExaminedGuide(Guid? guideId)
        {
            // The controller feeds this EVERY tick (33 Hz). Bail when the target hasn't changed, or the
            // measurement cache below is wiped each tick and a huge guide re-generates its whole voxel set
            // 30+ times a second just from being hovered (invisible on small guides; found via the
            // ~100-block sphere, 0.2.18). Server-side updates to the examined guide still invalidate via
            // OnGuideAddedOrUpdated.
            if (guideId == _examinedGuide) return;

            _examinedGuide = guideId;
            _measuredGuide = Guid.Empty; // force a re-measure for the new target
            RefreshText();
        }

        // ---------------------------------------------------------------------------------
        //  Composition (once)
        // ---------------------------------------------------------------------------------
        private void SetupHud()
        {
            CairoFont font = CairoFont.WhiteSmallText();

            const double panelW = 240;
            const double lineH   = 20;
            const double gap     = 2;
            double y = 0;

            ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            ElementBounds dialogBounds = ElementStdBounds
                .AutosizedMainDialog
                .WithAlignment(EnumDialogArea.LeftMiddle)
                .WithFixedAlignmentOffset(GuiStyle.DialogToScreenPadding, 0);

            GuiComposer c = capi.Gui
                .CreateCompo("layout:hud", dialogBounds)
                .AddShadedDialogBG(bgBounds, false)
                .BeginChildElements(bgBounds);

            // The HUD's Current Shape chip (0.1.16, human-requested): the same always-lit, guide-body-
            // yellow glyph as the F-menu's chip, top-right of the panel, shown while creating. The first
            // two text lines narrow so they never run under it.
            const double chip = 42;
            bool showChip = _tool.Mode == ToolMode.Create;
            _showClientOnlyIndicator = _net.AuthorityMode == ClientAuthorityMode.Local;
            _chipIcon = showChip
                ? GuideToolGui.CurrentShapeIconName(_tool.Shape, _tool.Constraint)
                : null;
            if (showChip)
            {
                ElementBounds chipBounds = ElementBounds.Fixed(panelW - chip, 0, chip, chip);
                var chipBtn = new GuiElementToggleButton(
                    capi, _chipIcon, "", font, OnChipToggled, chipBounds, toggleable: true);
                c.AddInteractiveElement(chipBtn, "hudcurshape");
            }

            var keys = new List<string>();
            if (_showClientOnlyIndicator) keys.Add("authority");
            keys.AddRange(new[] { "mode", "scale", "proj", "fill", "dims", "guide", "cap" });
            foreach (string key in keys)
            {
                double lineW = showChip && (key == "mode" || key == "scale") ? panelW - chip - 6 : panelW;
                ElementBounds lineBounds = ElementBounds.Fixed(0, y, lineW, lineH);
                c.AddDynamicText("", font, lineBounds, key);
                y += lineH + gap;
            }

            SingleComposer = c.EndChildElements().Compose();
            SingleComposer.GetToggleButton("hudcurshape")?.SetValue(true);   // a status light, always lit
        }

        // The HUD chip is display-only: if anything ever manages to click it, snap it back to lit.
        private void OnChipToggled(bool on)
        {
            if (on) return;
            SingleComposer?.GetToggleButton("hudcurshape")?.SetValue(true);
        }

        // ---------------------------------------------------------------------------------
        //  Text refresh
        // ---------------------------------------------------------------------------------
        private void RefreshText()
        {
            if (SingleComposer == null) return;

            bool wantsClientOnlyIndicator = _net.AuthorityMode == ClientAuthorityMode.Local;
            if (wantsClientOnlyIndicator != _showClientOnlyIndicator)
            {
                SetupHud();
                if (SingleComposer == null) return;
            }

            // Current Shape chip upkeep (0.1.16): a shape pick or mode flip swaps/hides the glyph, which
            // needs a recompose — rare (only on those changes), so it's done here on the cheap tick.
            string wantIcon = _tool.Mode == ToolMode.Create
                ? GuideToolGui.CurrentShapeIconName(_tool.Shape, _tool.Constraint)
                : null;
            if (wantIcon != _chipIcon && IsOpened()) SetupHud();

            // --- always-present tool state ---
            if (_showClientOnlyIndicator) SetText("authority", "Client-Only Guides");
            // Session 8: the mode line also carries the next guide's shape in Create mode, so the
            // player can confirm the pick without opening the F-menu.
            SetText("mode", _tool.Mode == ToolMode.Create
                ? "Mode: " + ModeLabel(_tool.Mode) + "  ·  "
                    + GuideToolGui.ShapeDisplayName(_tool.Shape, _tool.Constraint)
                : "Mode: " + ModeLabel(_tool.Mode));
            SetText("scale", "Scale: " + ScaleLabel(_tool.Scale));
            SetText("proj", "Projection: " + ProjectionLabel());
            SetText("fill", "Fill: " + (_tool.Filled ? "Filled" : "Hollow"));

            // --- situational: draft has priority over examine ---
            if (_tool.HasActiveDraft && _draftAim != null && _tool.DraftStart != null)
            {
                EnsureMeasuredDraft();
                SetText("dims", DimsText(_draftExtent));
                SetText("guide", "Placing new arch…");
                SetText("cap", CapGauge(_draftVoxelCount, _net.PerGuideVoxelCap, Guid.Empty));
                return;
            }

            GuideData examined = ResolveExaminedGuide();
            if (examined != null)
            {
                EnsureMeasuredGuide(examined);
                SetText("dims", DimsText(_guideExtent));
                SetText("guide", GuideInfoText(examined, _guideVoxelCount));
                SetText("cap", CapGauge(_guideVoxelCount, _net.PerGuideVoxelCap, examined.Id));
                return;
            }

            // Nothing to describe right now — blank the three situational lines so the
            // panel keeps its shape without stale text.
            SetText("dims", "");
            SetText("guide", "");
            SetText("cap", "");
        }

        private GuideData ResolveExaminedGuide()
        {
            if (_examinedGuide == null) return null;
            if (_net.Guides.TryGetValue(_examinedGuide.Value, out GuideData g) && g != null) return g;
            return null;
        }

        // ---------------------------------------------------------------------------------
        //  Measurement (cached)
        // ---------------------------------------------------------------------------------
        private void EnsureMeasuredDraft()
        {
            int scale = _tool.Scale;
            Vec3d start = _tool.DraftStart;
            Vec3d aim = _draftAim;
            if (start == null || aim == null) { _draftExtent = GuideExtent.Empty; _draftVoxelCount = 0; return; }

            // Quantize the aim to 1/16-block units for cache keying.
            long qx = (long)Math.Floor(aim.X * 16.0);
            long qy = (long)Math.Floor(aim.Y * 16.0);
            long qz = (long)Math.Floor(aim.Z * 16.0);

            // Shape changes fold into the scale key slot cheaply: shifting the key by shape/constraint/
            // fill/sides/draft-stage/chain-length forces a re-measure whenever any changes mid-draft.
            int shapeKey = scale + 1000 * ((int)_tool.Shape + 4 * (int)_tool.Constraint + 16 * (_tool.Filled ? 1 : 0)
                + 32 * _tool.Sides + 1024 * (_tool.AwaitingApex ? 1 : 0) + 2048 * _tool.ChainCount
                + 65536 * (_tool.AwaitingRim ? 1 : 0));
            if (qx == _draftKeyX && qy == _draftKeyY && qz == _draftKeyZ && shapeKey == _draftKeyScale) return;
            _draftKeyX = qx; _draftKeyY = qy; _draftKeyZ = qz; _draftKeyScale = shapeKey;

            try
            {
                IGuideShape shape;
                if (DraftManager.IsChainShape(_tool.Shape))
                {
                    // The Free-Shape measures its whole placed chain + the live aim corner (0.1.15).
                    var corners = _tool.DraftChain;
                    corners.Add(new Vintagestory.API.MathTools.Vec3d(aim.X, aim.Y, aim.Z));
                    shape = new FreeShape(corners, false);
                }
                else
                {
                    // A three-click triangle whose base is down measures with the aim as its APEX (the
                    // base is fixed); a four-click Tapered Cylinder on its last stage measures with the
                    // aim as its RIM (0.2.24); every other draft measures start → aim (Session 11).
                    Vec3d end = _tool.AwaitingApex ? _tool.DraftSecond : aim;
                    shape = ShapeFactory.Create(
                        _tool.Shape, _tool.Constraint, _tool.DraftPlaneAxis, start, end,
                        sides: _tool.Sides);
                    DraftManager.ApplyPlacementPoints(shape, _tool.Shape, _tool.Constraint,
                        _tool.AwaitingRim ? _tool.DraftThird : _tool.AwaitingApex ? aim : null,
                        _tool.AwaitingRim ? aim : null);
                }
                var voxels = shape.GetVoxelPositions(scale, _tool.Filled);
                _draftVoxelCount = voxels.Count;
                _draftExtent = GuideMeshBuilder.MeasureExtent(voxels, scale);
            }
            catch
            {
                _draftExtent = GuideExtent.Empty;
                _draftVoxelCount = 0;
            }
        }

        private void EnsureMeasuredGuide(GuideData g)
        {
            if (g.Id == _measuredGuide) return;
            _measuredGuide = g.Id;

            try
            {
                // Adopt the guide's own control points (by reference, matching how the
                // renderer builds it), refresh phantoms so the spline endpoints are correct,
                // then measure. This runs on the main thread alongside rendering, so sharing
                // the list is safe.
                IGuideShape shape = ShapeFactory.Adopt(g);
                shape.RecalculatePhantomPoints();
                var voxels = shape.GetVoxelPositions(g.VoxelScale, g.IsFilled);
                _guideVoxelCount = voxels.Count;
                _guideExtent = GuideMeshBuilder.MeasureExtent(voxels, g.VoxelScale);
            }
            catch
            {
                _guideExtent = GuideExtent.Empty;
                _guideVoxelCount = 0;
            }
        }

        // ---------------------------------------------------------------------------------
        //  Subscriptions
        // ---------------------------------------------------------------------------------
        private void Subscribe()
        {
            if (_subscribed) return;
            _net.VoxelCapWarningReceived += OnCapWarning;
            _net.GuideAddedOrUpdated     += OnGuideAddedOrUpdated;
            _subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!_subscribed) return;
            _net.VoxelCapWarningReceived -= OnCapWarning;
            _net.GuideAddedOrUpdated     -= OnGuideAddedOrUpdated;
            _subscribed = false;
        }

        private void OnCapWarning(Guid guideId, int count, int cap)
        {
            _warnGuide = guideId;
            _warnCount = count;
            _warnCap = cap;
            _warnUntil = capi.World.ElapsedMilliseconds + 2500; // flash for ~2.5s
            RefreshText();
        }

        private void OnGuideAddedOrUpdated(GuideData g)
        {
            if (g == null) return;
            // If the guide we're currently measuring changed, drop the cache so the next
            // refresh recomputes its extent/count.
            if (_examinedGuide != null && g.Id == _examinedGuide.Value)
            {
                _measuredGuide = Guid.Empty;
                RefreshText();
            }
        }

        // ---------------------------------------------------------------------------------
        //  Text builders
        // ---------------------------------------------------------------------------------
        private static string DimsText(GuideExtent e)
        {
            // \u2194 = ↔ (width) , \u2195 = ↕ (height)
            return "\u2194 " + e.VoxelWidth + " / \u2195 " + e.VoxelHeight + " vox    "
                 + "\u2194 " + e.BlockWidth + " / \u2195 " + e.BlockHeight + " blk";
        }

        private string GuideInfoText(GuideData g, int voxelCount)
        {
            string editable = _net.LockHolders.TryGetValue(g.Id, out string holder) && !string.IsNullOrEmpty(holder)
                ? "in use"
                : "editable";
            string guideLabel = _net.IsLocalGuide(g.Id) ? "Private guide " : "Guide ";
            return guideLabel + ShortId(g.Id) + " · " + editable + " · " + voxelCount.ToString("N0") + " vox";
        }

        // Text-only cap gauge: "Cap [████░░░░] 62%" plus a near/over-cap marker. We keep it
        // to plain text (no font-colour mutation) for rendering safety; the warning words
        // carry the urgency instead of colour.
        private string CapGauge(int count, int cap, Guid guideId)
        {
            const int cells = 12;
            double frac = cap > 0 ? (double)count / cap : 0.0;
            if (frac < 0) frac = 0;
            double clamped = frac > 1.0 ? 1.0 : frac;

            int filled = (int)Math.Round(clamped * cells);
            if (filled < 0) filled = 0;
            if (filled > cells) filled = cells;

            // \u2588 = █ (full) , \u2591 = ░ (light)
            string bar = new string('\u2588', filled) + new string('\u2591', cells - filled);
            int pct = (int)Math.Round(frac * 100.0);

            string marker = "";
            bool flashing = guideId != Guid.Empty && guideId == _warnGuide
                            && capi.World.ElapsedMilliseconds <= _warnUntil;
            if (flashing || frac >= 1.0)
            {
                marker = "  \u26A0 OVER CAP"; // \u26A0 = ⚠
            }
            else if (frac >= 0.9)
            {
                marker = "  \u26A0 near cap";
            }

            return "Cap [" + bar + "] " + pct + "%" + marker;
        }

        private void SetText(string key, string text)
        {
            SingleComposer?.GetDynamicText(key)?.SetNewText(text ?? "");
        }

        // ---------------------------------------------------------------------------------
        //  Small label helpers
        // ---------------------------------------------------------------------------------
        private static string ModeLabel(ToolMode mode) => mode switch
        {
            ToolMode.Create => "Create",
            ToolMode.Edit => "Edit",
            ToolMode.Delete => "Delete",
            _ => mode.ToString()
        };

        private static string ScaleLabel(int scale) => scale switch
        {
            1  => "1/16 block",
            2  => "1/8 block",
            4  => "1/4 block",
            8  => "1/2 block",
            16 => "1 block",
            _  => "1/" + (16 / Math.Max(1, scale)) + " block"
        };

        private string ProjectionLabel()
        {
            if (_tool.Projection != ProjectionMode.Surface) return "Volumetric";

            PlaneAxis? ov = _tool.PlaneOverride;
            if (ov == null) return "Surface (auto plane)";
            return "Surface (" + PlaneLabel(ov.Value) + ")";
        }

        private static string PlaneLabel(PlaneAxis axis) => axis switch
        {
            PlaneAxis.Y => "floor",
            PlaneAxis.Z => "N–S wall",
            PlaneAxis.X => "E–W wall",
            _ => axis.ToString()
        };

        private static string ShortId(Guid id) => id.ToString("N").Substring(0, 8);
    }
}
