using System;
using Cairo;
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
    //     action-aware mode heading + contextual icon tile (tool state, always)
    //     scale / projection(+plane) / fill                (tool state, always)
    //     separate width/height dimensions + total voxels (draft OR contextual guide)
    //     compact voxel-cap percentage                    (draft OR contextual guide)
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
    //  Fixed-footprint model: the panel always reserves four contextual rows, recomposing only when
    //  the action state changes the status font, tile glyph/caption, or outer accent. It never resizes
    //  as the crosshair moves. A light 150 ms tick plus targeted event
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

        // --- measurement caches ---
        // v0.3.0: the HUD never voxelises an active draft. The renderer hands back one settled result;
        // until then a changing alien-looking readout makes the pending calculation explicit.
        private GuideExtent _draftExtent = GuideExtent.Empty;
        private int _draftVoxelCount;
        private bool _draftMeasurementReady;

        // Existing guides carry cached metadata; only an active grab temporarily repopulates the dedicated
        // dimensions line from a cheap curve measurement supplied by the controller.
        private Guid? _grabMeasurementGuide;
        private GuideExtent _grabExtent = GuideExtent.Empty;

        private long _tickId;
        private bool _subscribed;

        // The HUD's copy of the Current Shape chip (0.1.16): which "-current" glyph is composed right
        // now, or null when hidden (non-Create modes). A change recomposes the HUD (rare — shape picks).
        private enum HudVisualState
        {
            Create,
            Sculpt,
            Creating,
            Sculpting,
            Edit,
            EditTarget,
            Editing,
            Delete,
            Deleting
        }

        private HudVisualState _composedState;
        private string _tileIcon;
        private string _tileCaption;
        private bool _showClientOnlyIndicator;

        private static readonly double[] CreatingColor = { 1.0, 0.88, 0.15, 1.0 };
        private static readonly double[] ModifyingColor = { 0.28, 0.82, 1.0, 1.0 };
        private static readonly double[] DeletingColor = { 1.0, 0.28, 0.22, 1.0 };

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
            CairoFont font = HudFont();
            CairoFont labelFont = HudLabelFont();

            const double panelW = 200;
            const double lineH   = 20;
            const double gap     = 2;
            double y = 0;

            _composedState = CurrentVisualState();
            (_tileIcon, _tileCaption) = TilePresentation(_composedState);
            _showClientOnlyIndicator = ShouldShowPrivateIndicator();

            double[] accent = AccentColor(_composedState);
            CairoFont statusFont = HudFont();
            statusFont.Orientation = EnumTextOrientation.Center;
            if (accent != null)
            {
                statusFont.Color = accent;
                statusFont.Slant = FontSlant.Italic;
            }

            ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(
                GuiStyle.ElementToDialogPadding * 0.75);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            ElementBounds dialogBounds = ElementStdBounds
                .AutosizedMainDialog
                .WithAlignment(EnumDialogArea.LeftMiddle)
                .WithFixedAlignmentOffset(GuiStyle.DialogToScreenPadding, 0);

            GuiComposer c = capi.Gui
                .CreateCompo("layout:hud", dialogBounds)
                .AddShadedDialogBG(bgBounds, false)
                .BeginChildElements(bgBounds);

            const double tile = 42;
            const double outlineGap = 2;
            double tileX = panelW - tile - outlineGap;
            double tileY = y + 2;
            if (accent != null)
            {
                ElementBounds outlineBounds = ElementBounds.Fixed(
                    tileX - outlineGap, tileY - outlineGap,
                    tile + outlineGap * 2, tile + outlineGap * 2);
                c.AddStaticElement(new TileOutlineElement(capi, outlineBounds, accent));
            }

            ElementBounds tileBounds = ElementBounds.Fixed(tileX, tileY, tile, tile);
            var tileButton = new GuiElementToggleButton(
                capi, _tileIcon, "", font, OnChipToggled, tileBounds, toggleable: true);
            c.AddInteractiveElement(tileButton, "hudcurshape");

            double narrowW = tileX - 6;
            c.AddDynamicText("", statusFont, ElementBounds.Fixed(0, y, narrowW, lineH), "status");
            y += lineH + gap;
            const double scaleLabelW = 48;
            c.AddStaticText("Scale:", labelFont, ElementBounds.Fixed(0, y, scaleLabelW, lineH));
            c.AddDynamicText("", font,
                ElementBounds.Fixed(scaleLabelW + 4, y, narrowW - scaleLabelW - 4, lineH), "scale");
            y += lineH + gap;
            // This row starts below the tile, so it can use the full panel width. The longest valid
            // combination ("Volumetric · Wireframe") otherwise wraps in the narrow header column.
            c.AddDynamicText("", font, ElementBounds.Fixed(0, y, panelW, lineH), "settings");
            y += lineH + gap;

            const double dimensionLabelW = 20;
            CairoFont dimensionLabelFont = HudLabelFont();
            dimensionLabelFont.Orientation = EnumTextOrientation.Center;
            c.AddDynamicText("", dimensionLabelFont,
                ElementBounds.Fixed(0, y, dimensionLabelW, lineH), "ctx1label");
            c.AddDynamicText("", font, ElementBounds.Fixed(dimensionLabelW, y,
                panelW - dimensionLabelW, lineH), "ctx1");
            y += lineH + gap;

            c.AddDynamicText("", dimensionLabelFont,
                ElementBounds.Fixed(0, y, dimensionLabelW, lineH), "ctx2label");
            c.AddDynamicText("", font, ElementBounds.Fixed(dimensionLabelW, y,
                panelW - dimensionLabelW, lineH), "ctx2");
            y += lineH + gap;

            const double totalLabelW = 100;
            c.AddDynamicText("", labelFont, ElementBounds.Fixed(0, y, totalLabelW, lineH), "ctx3label");
            c.AddDynamicText("", font, ElementBounds.Fixed(totalLabelW + 4, y,
                panelW - totalLabelW - 4, lineH), "ctx3");
            y += lineH + gap;

            const double capLabelW = 34;
            c.AddDynamicText("", labelFont, ElementBounds.Fixed(0, y, capLabelW, lineH), "ctx4label");
            c.AddDynamicText("", font, ElementBounds.Fixed(capLabelW + 4, y,
                panelW - capLabelW - 4, lineH), "ctx4");
            y += lineH + gap;

            SingleComposer = c.EndChildElements().Compose();
            SingleComposer.GetToggleButton("hudcurshape")?.SetValue(true);
        }

        // The HUD chip is display-only: if anything ever manages to click it, snap it back to lit.
        private void OnChipToggled(bool on)
        {
            if (on) return;
            SingleComposer?.GetToggleButton("hudcurshape")?.SetValue(true);
        }

        private sealed class TileOutlineElement : GuiElement
        {
            private readonly double[] _color;

            public TileOutlineElement(ICoreClientAPI capi, ElementBounds bounds, double[] color)
                : base(capi, bounds)
            {
                _color = color;
            }

            public override void ComposeElements(Context ctx, ImageSurface surface)
            {
                Bounds.CalcWorldBounds();
                ctx.SetSourceRGBA(_color[0], _color[1], _color[2], _color[3]);
                double stroke = scaled(2);
                double inset = stroke / 2.0;
                ctx.LineWidth = stroke;
                ctx.Rectangle(Bounds.drawX + inset, Bounds.drawY + inset,
                    Math.Max(0, Bounds.InnerWidth - stroke), Math.Max(0, Bounds.InnerHeight - stroke));
                ctx.Stroke();
            }
        }

        // ---------------------------------------------------------------------------------
        //  Text refresh
        // ---------------------------------------------------------------------------------
        private void RefreshText()
        {
            if (SingleComposer == null) return;

            bool wantsClientOnlyIndicator = ShouldShowPrivateIndicator();
            HudVisualState wantState = CurrentVisualState();
            (string wantIcon, string wantCaption) = TilePresentation(wantState);
            if (wantsClientOnlyIndicator != _showClientOnlyIndicator
                || wantState != _composedState
                || wantIcon != _tileIcon
                || wantCaption != _tileCaption)
            {
                SetupHud();
                if (SingleComposer == null) return;
            }

            SetText("status", StatusText(_composedState)
                + (_showClientOnlyIndicator ? " · Private" : ""));
            GuideData settingsGuide = _tool.Mode == ToolMode.Edit ? ResolveSelectedGuide() : null;
            int scale = settingsGuide?.VoxelScale ?? _tool.Scale;
            GuideShapeType shapeType = settingsGuide?.ShapeType ?? _tool.Shape;
            bool wireframe = settingsGuide?.IsWireframe ?? _tool.Wireframe;
            bool filled = settingsGuide?.IsFilled ?? _tool.Filled;
            SetText("scale", ScaleLabel(scale));
            SetText("settings", ProjectionLabel(settingsGuide) + " · "
                + (GuideShapeTypes.IsVolume(shapeType)
                    ? (wireframe ? "Wireframe" : "Shell")
                    : (filled ? "Filled" : "Hollow")));

            ClearContextRows();

            if (_tool.HasActiveDraft && _tool.DraftStart != null)
            {
                ShowContextLabels();
                if (_draftMeasurementReady)
                {
                    SetText("ctx1", HorizontalDimensionsText(_draftExtent));
                    SetText("ctx2", VerticalDimensionsText(_draftExtent));
                    SetText("ctx3", TotalVoxelsText(_draftVoxelCount));
                    SetText("ctx4", CapText(_draftVoxelCount, _net.PerGuideVoxelCap));
                }
                else
                {
                    string calculating = CalculatingText();
                    SetText("ctx1", calculating);
                    SetText("ctx2", calculating);
                    SetText("ctx3", calculating);
                    SetText("ctx4", calculating);
                }
                return;
            }

            GuideData contextGuide = ResolveContextGuide();
            if (contextGuide != null)
            {
                ShowContextLabels();
                bool grabbing = _grabMeasurementGuide == contextGuide.Id;
                GuideExtent extent = grabbing
                    ? _grabExtent
                    : new GuideExtent(
                        contextGuide.CachedVoxelWidth, contextGuide.CachedVoxelHeight,
                        contextGuide.CachedBlockWidth, contextGuide.CachedBlockHeight);

                SetText("ctx1", HorizontalDimensionsText(extent));
                SetText("ctx2", VerticalDimensionsText(extent));
                SetText("ctx3", TotalVoxelsText(contextGuide.CachedVoxelCount));
                SetText("ctx4", CapText(contextGuide.CachedVoxelCount, _net.PerGuideVoxelCap));
            }
        }

        private void ClearContextRows()
        {
            for (int i = 1; i <= 4; i++)
            {
                SetText("ctx" + i + "label", "");
                SetText("ctx" + i, "");
            }
        }

        private void ShowContextLabels()
        {
            SetText("ctx1label", "↔");
            SetText("ctx2label", "↕");
            SetText("ctx3label", "Total Voxels:");
            SetText("ctx4label", "Cap:");
        }

        private HudVisualState CurrentVisualState()
        {
            if (_tool.Mode == ToolMode.Create)
            {
                if (ResolveGrabbedGuide() != null) return HudVisualState.Sculpting;
                if (_tool.HasActiveDraft) return HudVisualState.Creating;
                return ResolveExaminedGuide() != null ? HudVisualState.Sculpt : HudVisualState.Create;
            }
            if (_tool.Mode == ToolMode.Edit)
            {
                if (ResolveSelectedGuide() != null) return HudVisualState.Editing;
                return ResolveExaminedGuide() != null ? HudVisualState.EditTarget : HudVisualState.Edit;
            }
            return ResolveExaminedGuide() != null ? HudVisualState.Deleting : HudVisualState.Delete;
        }

        private (string icon, string caption) TilePresentation(HudVisualState state)
        {
            GuideData context = state switch
            {
                HudVisualState.Sculpt => ResolveExaminedGuide(),
                HudVisualState.Sculpting => ResolveGrabbedGuide(),
                HudVisualState.EditTarget => ResolveExaminedGuide(),
                HudVisualState.Editing => ResolveSelectedGuide(),
                HudVisualState.Deleting => ResolveExaminedGuide(),
                _ => null
            };

            return state switch
            {
                HudVisualState.Create or HudVisualState.Creating =>
                    (GuideToolGui.CurrentShapeIconName(_tool.Shape, _tool.Constraint),
                     GuideToolGui.ShapeDisplayName(_tool.Shape, _tool.Constraint)),
                HudVisualState.Sculpt or HudVisualState.Sculpting =>
                    (GuideToolGui.CurrentShapeIconName(context.ShapeType, context.Constraint),
                     GuideToolGui.ShapeDisplayName(context.ShapeType, context.Constraint)),
                HudVisualState.EditTarget or HudVisualState.Editing =>
                    (GuideToolGui.CurrentShapeIconName(context.ShapeType, context.Constraint),
                     GuideToolGui.ShapeDisplayName(context.ShapeType, context.Constraint)),
                HudVisualState.Edit => (LayoutToolIcons.ModeEdit, "Edit"),
                HudVisualState.Deleting =>
                    (GuideToolGui.CurrentShapeIconName(context.ShapeType, context.Constraint),
                     GuideToolGui.ShapeDisplayName(context.ShapeType, context.Constraint)),
                _ => (LayoutToolIcons.ModeDelete, "Delete")
            };
        }

        private GuideData ResolveContextGuide()
        {
            if (_composedState == HudVisualState.Sculpting) return ResolveGrabbedGuide();
            if (_tool.Mode == ToolMode.Edit) return ResolveSelectedGuide() ?? ResolveExaminedGuide();
            return ResolveExaminedGuide();
        }

        private bool ShouldShowPrivateIndicator()
        {
            GuideData context = ResolveContextGuide();
            if (context != null) return _net.IsLocalGuide(context.Id);
            return _tool.Mode == ToolMode.Create
                && _net.AuthorityMode == ClientAuthorityMode.Local;
        }

        private GuideData ResolveSelectedGuide()
        {
            if (_tool.SelectedGuideId == null) return null;
            return _net.Guides.TryGetValue(_tool.SelectedGuideId.Value, out GuideData guide)
                ? guide : null;
        }

        private static string StatusText(HudVisualState state) => state switch
        {
            HudVisualState.Create => "Create",
            HudVisualState.Sculpt => "Sculpt",
            HudVisualState.Creating => "Creating",
            HudVisualState.Sculpting => "Sculpting",
            HudVisualState.Edit or HudVisualState.EditTarget => "Edit",
            HudVisualState.Editing => "Editing",
            HudVisualState.Delete => "Delete",
            HudVisualState.Deleting => "Deleting",
            _ => ""
        };

        private static double[] AccentColor(HudVisualState state) => state switch
        {
            HudVisualState.Creating => CreatingColor,
            HudVisualState.Sculpting or HudVisualState.Editing => ModifyingColor,
            HudVisualState.Deleting => DeletingColor,
            _ => null
        };

        private string CalculatingText()
        {
            int dots = (int)(capi.World.ElapsedMilliseconds / 350L) % 3 + 1;
            return "Calculating" + new string('.', dots);
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
            _net.GuideAddedOrUpdated     += OnGuideAddedOrUpdated;
            _net.GuideHudMetadataChanged += OnGuideHudMetadataChanged;
            _subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!_subscribed) return;
            _net.GuideAddedOrUpdated     -= OnGuideAddedOrUpdated;
            _net.GuideHudMetadataChanged -= OnGuideHudMetadataChanged;
            _subscribed = false;
        }

        private void OnGuideHudMetadataChanged(Guid guideId)
        {
            if ((_examinedGuide != null && guideId == _examinedGuide.Value)
                || (_grabMeasurementGuide != null && guideId == _grabMeasurementGuide.Value)
                || (_tool.SelectedGuideId != null && guideId == _tool.SelectedGuideId.Value))
                RefreshText();
        }

        private void OnGuideAddedOrUpdated(GuideData g)
        {
            if (g == null) return;
            if ((_examinedGuide != null && g.Id == _examinedGuide.Value)
                || (_grabMeasurementGuide != null && g.Id == _grabMeasurementGuide.Value)
                || (_tool.SelectedGuideId != null && g.Id == _tool.SelectedGuideId.Value))
                RefreshText();
        }

        // ---------------------------------------------------------------------------------
        //  Text builders
        // ---------------------------------------------------------------------------------
        private static string HorizontalDimensionsText(GuideExtent e) =>
            e.VoxelWidth.ToString("N0") + " vox / " + e.BlockWidth.ToString("N0") + " blocks";

        private static string VerticalDimensionsText(GuideExtent e) =>
            e.VoxelHeight.ToString("N0") + " vox / " + e.BlockHeight.ToString("N0") + " blocks";

        private static string TotalVoxelsText(int count) =>
            Math.Max(0, count).ToString("N0");

        private static string CapText(int count, int cap)
        {
            if (cap <= 0) return "Unlimited";
            double frac = Math.Max(0, (double)count / cap);
            int pct = Math.Min(100, (int)Math.Round(frac * 100.0));
            return pct + "%" + (count > cap ? " — OVER CAP" : "");
        }

        private void SetText(string key, string text)
        {
            SingleComposer?.GetDynamicText(key)?.SetNewText(text ?? "");
        }

        private static CairoFont HudFont() => CairoFont.WhiteSmallText();

        private static CairoFont HudLabelFont()
        {
            CairoFont font = HudFont();
            font.Color = new double[] { 1, 1, 1, 0.58 };
            return font;
        }

        // ---------------------------------------------------------------------------------
        //  Small label helpers
        // ---------------------------------------------------------------------------------
        private static string ScaleLabel(int scale) => scale switch
        {
            1  => "1/16 block",
            2  => "1/8 block",
            4  => "1/4 block",
            8  => "1/2 block",
            16 => "1 block",
            _  => "1/" + (16 / Math.Max(1, scale)) + " block"
        };

        private string ProjectionLabel(GuideData guide = null)
        {
            ProjectionMode projection = guide?.Projection ?? _tool.Projection;
            return projection == ProjectionMode.Surface ? "Surface" : "Volumetric";
        }

    }
}
