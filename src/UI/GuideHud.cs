using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Layout.Client;
using Layout.Guide;
using Layout.Network;
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
    //     SetDraftCalculating(Vec3d) / ClearDraftAim() – the live draft pose / pending measurement state
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
        private bool _draftFlatSideAligned;
        private Guid? _examinedGuide;     // guide under the crosshair, if any

        // --- cap-warning flash state (fed by VoxelCapWarningReceived) ---
        private Guid _warnGuide = Guid.Empty;
        private int _warnCount;
        private int _warnCap;
        private long _warnUntil;          // ElapsedMilliseconds deadline for the flash

        // --- measurement caches ---
        // v0.3.0: the HUD never voxelises an active draft. The renderer hands back one settled result;
        // until then a changing alien-looking readout makes the pending calculation explicit.
        private GuideExtent _draftExtent = GuideExtent.Empty;
        private int _draftVoxelCount;
        private bool _draftMeasurementReady;
        private int _calculationFrame;

        // Existing guides carry cached metadata; only an active grab temporarily repopulates the dedicated
        // dimensions line from a cheap curve measurement supplied by the controller.
        private Guid? _grabMeasurementGuide;
        private GuideExtent _grabExtent = GuideExtent.Empty;

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
        public void SetDraftCalculating(Vec3d aim, bool flatSideAligned = false)
        {
            _draftAim = aim == null ? null : new Vec3d(aim.X, aim.Y, aim.Z);
            _draftFlatSideAligned = flatSideAligned;
            _draftMeasurementReady = false;
            RefreshText();
        }

        public void SetDraftMeasurement(GuideExtent extent, int voxelCount)
        {
            _draftExtent = extent;
            _draftVoxelCount = Math.Max(0, voxelCount);
            _draftMeasurementReady = true;
            RefreshText();
        }

        public void ClearDraftAim()
        {
            _draftAim = null;
            _draftFlatSideAligned = false;
            _draftVoxelCount = 0;
            _draftExtent = GuideExtent.Empty;
            _draftMeasurementReady = false;
            RefreshText();
        }

        public void SetGrabMeasurement(Guid guideId, GuideExtent extent)
        {
            _grabMeasurementGuide = guideId;
            _grabExtent = extent;
            RefreshText();
        }

        public void ClearGrabMeasurement()
        {
            if (_grabMeasurementGuide == null) return;
            _grabMeasurementGuide = null;
            _grabExtent = GuideExtent.Empty;
            RefreshText();
        }

        public void SetExaminedGuide(Guid? guideId)
        {
            // The controller feeds this every tick. Existing guide text now reads cached authoritative
            // metadata only, but unchanged targeting still has no reason to recompose the HUD.
            if (guideId == _examinedGuide) return;

            _examinedGuide = guideId;
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
            SetText("fill", GuideShapeTypes.IsVolume(_tool.Shape)
                ? "Form: " + (_tool.Wireframe ? "Wireframe" : "Shell")
                : "Fill: " + (_tool.Filled ? "Filled" : "Hollow"));

            // --- situational: draft has priority over examine ---
            if (_tool.HasActiveDraft && _draftAim != null && _tool.DraftStart != null)
            {
                _calculationFrame = (_calculationFrame + 1) % CalculationGlyphs.Length;
                SetText("dims", _draftMeasurementReady
                    ? DimsText(_draftExtent)
                    : CalculatingDimensionsText(_calculationFrame));
                SetText("guide", "Placing new arch…");
                SetText("cap", _draftMeasurementReady
                    ? CapGauge(_draftVoxelCount, _net.PerGuideVoxelCap, Guid.Empty)
                    : CalculatingCapText(_calculationFrame));
                return;
            }

            GuideData examined = ResolveGrabbedGuide() ?? ResolveExaminedGuide();
            if (examined != null)
            {
                bool grabbing = _grabMeasurementGuide == examined.Id;
                SetText("dims", grabbing ? DimsText(_grabExtent) : "");
                SetText("guide", GuideInfoText(examined));
                SetText("cap", CapGauge(examined.CachedVoxelCount, _net.PerGuideVoxelCap, examined.Id));
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

        private GuideData ResolveGrabbedGuide()
        {
            if (_grabMeasurementGuide == null) return null;
            return _net.Guides.TryGetValue(_grabMeasurementGuide.Value, out GuideData guide)
                ? guide : null;
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
            if ((_examinedGuide != null && g.Id == _examinedGuide.Value)
                || (_grabMeasurementGuide != null && g.Id == _grabMeasurementGuide.Value))
                RefreshText();
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

        // Deliberately strange but compact: the shifting sequence reads as active computation without
        // pretending that an old number still describes the moving guide.
        private static readonly string[] CalculationGlyphs =
        {
            "⌬⟟⋔⧖", "⟟⋔⧖⌬", "⋔⧖⌬⟟", "⧖⌬⟟⋔"
        };

        private static string CalculatingDimensionsText(int frame)
        {
            string a = CalculationGlyphs[frame % CalculationGlyphs.Length];
            string b = CalculationGlyphs[(frame + 2) % CalculationGlyphs.Length];
            return "↔ " + a + " / ↕ " + b + " vox    ↔ " + b + " / ↕ " + a + " blk";
        }

        private static string CalculatingCapText(int frame) =>
            "Cap: " + CalculationGlyphs[(frame + 1) % CalculationGlyphs.Length] + "  ·  calculating";

        private string GuideInfoText(GuideData g)
        {
            string editable = _net.LockHolders.TryGetValue(g.Id, out string holder) && !string.IsNullOrEmpty(holder)
                ? "in use"
                : "editable";
            string guideLabel = _net.IsLocalGuide(g.Id) ? "Private guide " : "Guide ";
            string name = string.IsNullOrWhiteSpace(g.DisplayName) ? "dimensions pending" : g.DisplayName;
            return guideLabel + name + " · " + editable + " · "
                + g.CachedVoxelCount.ToString("N0") + " vox";
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

    }
}
