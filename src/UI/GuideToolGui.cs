using System;
using System.Collections.Generic;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Layout.Config;
using Layout.Guide;
using Layout.Network;
using Layout.Systems;

namespace Layout.UI
{
    // =====================================================================================
    //  GuideToolGui  —  Module 6 (UI layer), Session-8 TILE REWORK (playtest revision B)
    // -------------------------------------------------------------------------------------
    //  Modal control panel for the held guide tool. Opened/closed by the tool item
    //  (Module 7), NOT by an auto-bound hotkey. While open it wants the mouse cursor
    //  (PrefersUngrabbedMouse => true), so the player cannot re-aim / change the current
    //  selection underneath it — that invariant lets us treat the "guide being edited"
    //  as fixed for the lifetime of a given composition.
    //
    //  Every control is a row of clickable TILES (toggle buttons, exactly one lit per
    //  row) — one click per change, every option visible at once, no nested menus.
    //
    //  PLAYTEST REVISION B (the "locked into edit mode" fix): the MAIN rows are now
    //  PERMANENT and NEVER change meaning — Mode, Shape (the initial-shape picker),
    //  Favorites (placeholder), Scale, Projection, Plane, Fill always drive the tool's
    //  defaults for the NEXT guide, whether or not a guide is selected. Selecting a guide
    //  (by clicking it in the world) no longer transforms the panel; instead a clearly
    //  separated SELECTED-GUIDE section is APPENDED below, with its own tile rows that
    //  act on that guide (SendRescale / SendSetProjection / SendSetFilled / SendHide) and
    //  an explicit Deselect button so nothing ever feels stuck. [Flagged for review: if
    //  per-guide GUI editing should go away entirely, delete BuildSelectedGuideSection.]
    //
    //  DELETE MODE (playtest request): everything except the Mode row is greyed out —
    //  dim labels, disabled tiles — signalling that none of those settings apply while
    //  deleting. The rows keep showing the defaults you'll come back to. (Icon tiles use the
    //  toggle button's native Enabled=false dim; the label uses a ghost font.)
    //
    //  ICONS (Session-10 UI pass): every option row is now SQUARE icon tiles (registered glyphs — see
    //  LayoutToolIcons), except Divisions which is a single number field, scroll-wheel adjustable.
    //
    //  Recompose model: tiles need their lit state moved on every selection, and a
    //  recompose is the one honest way to do that with stock toggle buttons — but ONLY on
    //  discrete user clicks or row-set flips, never per-frame. Remote edits to the
    //  selected guide re-light its section's tiles in place (SetValue under a suppress
    //  guard); a remote projection flip that adds/removes its plane row defers a full
    //  recompose, same as before.
    //
    //  ---- Vintage Story GUI API touchpoints (verified only by local `dotnet build`) ----
    //    - capi.Gui.CreateCompo(string, ElementBounds) -> GuiComposer
    //    - GuiComposer.AddShadedDialogBG / AddDialogTitleBar / AddStaticText / AddDynamicText
    //    - GuiComposer.AddToggleButton(string text, CairoFont, Action<bool> onToggle,
    //          ElementBounds, string key)
    //          · accessor: SingleComposer.GetToggleButton(key) with .SetValue(bool)/.On
    //    - GuiComposer.AddSmallButton(string text, ActionConsumable onClick, ElementBounds)
    //          · onClick: Func<bool>, return true when handled
    //    - GuiComposer.AddInset(ElementBounds, int depth) — the Favorites placeholder well
    //    - new GuiElementToggleButton(capi, icon, text, CairoFont, Action<bool>, ElementBounds, bool)
    //          + GuiComposer.AddInteractiveElement(elem, key) — the square icon tiles; .Enabled dims them
    //    - GuiComposer.AddHoverText(text, CairoFont, int width, ElementBounds, string key) — tile tooltips
    //    - GuiComposer.AddTextInput(ElementBounds, Action<string> onTextChanged, CairoFont, string key)
    //          · accessor: GetTextInput(key).SetValue(string) — fires onTextChanged on some builds,
    //            so every programmatic SetValue here runs under the _suppress guard
    //          · OnMouseWheel + field.Bounds.PointInside(mouseX, mouseY) drives the Divisions wheel
    //    - GuiComposer.GetDynamicText(key).SetNewText(string)
    //    - ElementStdBounds.AutosizedMainDialog ; EnumDialogArea.RightMiddle
    //    - GuiStyle.{ElementToDialogPadding, DialogToScreenPadding, TitleBarHeight}
    //    - CairoFont.WhiteSmallText() / WhiteDetailText()
    //    - capi.Event.RegisterCallback(Action<float>, int millis) -> long id
    //  If any of the above differ, the fix is local to this file.
    // =====================================================================================
    public class GuideToolGui : GuiDialog
    {
        private readonly DraftManager _tool;
        private readonly ClientNetworkHandler _net;
        private readonly LayoutClientConfig _config;

        // ---- Tile row tables (code kept stable; display names are short tile labels) ----
        // Order matches the ToolMode enum (Create · Edit · Move · Delete) so (int)Mode indexes the row.
        private static readonly string[] ModeCodes  = { "create", "edit", "transform", "delete" };
        private static readonly string[] ModeNames  = { "Create", "Edit (selected guide)", "Transform (selected guide)", "Delete" };

        // The shape catalog — the primitives+modifiers model, in the order players think of them
        // (0.1.15: thirteen entries with Polygon + Free-Shape, shown in the EXPANDED picker as a grid of
        // four per row). Codes map to (GuideShapeType, ShapeConstraint) in ShapeFromCode below.
        private static readonly string[] ShapeCodes = {
            "arch", "halfcircle", "circle", "ellipse",
            "line", "triangle", "righttri", "equilateral",
            "isosceles", "rectangle", "square", "polygon",
            "freeshape",
            "sphere", "dome", "cylinder", "taperedcylinder",
            "polygonalprism", "taperedpolygonalprism", "cone", "box" };
        private static readonly string[] ShapeNames = {
            "Arch", "Half-circle", "Circle", "Ellipse",
            "Line", "Triangle", "Right triangle", "Equilateral",
            "Isosceles", "Rectangle", "Square", "Polygon",
            "Free-Shape",
            "Sphere", "Dome", "Cylinder", "Tapered Cylinder",
            "Polygonal Prism", "Tapered Polygonal Prism", "Cone", "Box" };

        // Voxel-edge scale, ascending: the NxN icon IS the voxel count (1x1 smallest ... 16x16 = full block),
        // exactly like the game's native scale icons. Names are voxel counts, not fractions (human-requested).
        private static readonly string[] ScaleCodes = { "1", "2", "4", "8", "16" };
        private static readonly string[] ScaleNames = { "1×1", "2×2", "4×4", "8×8", "16×16 (full block)" };

        private static readonly string[] ProjCodes  = { "vol", "surf" };
        private static readonly string[] ProjNames  = { "Volumetric", "Surface" };

        private static readonly string[] FillCodes  = { "hollow", "filled" };
        private static readonly string[] FillNames  = { "Hollow", "Filled" };

        private static readonly string[] VisCodes   = { "shown", "hidden" };
        private static readonly string[] VisNames   = { "Shown", "Hidden" };

        // Plane row — the selected guide is always on a concrete plane, so no "auto" there.
        private static readonly string[] PlaneEditCodes = { "y", "z", "x" };
        private static readonly string[] PlaneEditNames = { "Floor", "N–S wall", "E–W wall" };
        private static readonly string[] PlaneToolCodes = { "auto", "y", "z", "x" };
        private static readonly string[] PlaneToolNames = { "Auto", "Floor", "N–S", "E–W" };

        // ---- Icon names per option (UI pass): parallel to the code/name arrays above. Rows that carry an
        // icon array render icon tiles (hover shows the name); Scale and Divisions stay text/numeric. ----
        private static readonly string[] ShapeIcons = {
            LayoutToolIcons.Arch, LayoutToolIcons.HalfCircle, LayoutToolIcons.Circle, LayoutToolIcons.Ellipse,
            LayoutToolIcons.Line, LayoutToolIcons.Triangle, LayoutToolIcons.RightTri, LayoutToolIcons.Equilateral,
            LayoutToolIcons.Isosceles, LayoutToolIcons.Rectangle, LayoutToolIcons.Square, LayoutToolIcons.Polygon,
            LayoutToolIcons.FreeShapeIcon,
            LayoutToolIcons.Sphere, LayoutToolIcons.Dome, LayoutToolIcons.Cylinder,
            LayoutToolIcons.TaperedCylinder, LayoutToolIcons.PolygonalPrism,
            LayoutToolIcons.TaperedPolygonalPrism, LayoutToolIcons.Cone, LayoutToolIcons.Box };
        private static readonly string[] ModeIcons =
            { LayoutToolIcons.ModeCreate, LayoutToolIcons.ModeEdit, LayoutToolIcons.ModeTransform, LayoutToolIcons.ModeDelete };

        // F6 Move pad. Away/Toward/Left/Right are horizontal and resolve against the player's facing at the
        // moment of the click; Up/Down are world vertical. Codes are what OnMoveArrowTile switches on.
        private static readonly string[] MoveArrowCodes =
            { "away", "toward", "left", "right", "up", "down" };
        private static readonly string[] MoveArrowIcons =
        {
            LayoutToolIcons.MoveAway, LayoutToolIcons.MoveToward, LayoutToolIcons.MoveLeft,
            LayoutToolIcons.MoveRight, LayoutToolIcons.MoveUp, LayoutToolIcons.MoveDown
        };
        private static readonly string[] MoveArrowNames =
        {
            "Away from you", "Toward you", "To your left", "To your right",
            "Up", "Down"
        };
        private static readonly string[] ProjIcons = { LayoutToolIcons.ProjVolumetric, LayoutToolIcons.ProjSurface };
        private static readonly string[] FillIcons = { LayoutToolIcons.FillHollow, LayoutToolIcons.FillFilled };
        private static readonly string[] FormIcons = { LayoutToolIcons.FormShell, LayoutToolIcons.FormWireframe };
        private static readonly string[] VisIcons  = { LayoutToolIcons.VisShown, LayoutToolIcons.VisHidden };
        private static readonly string[] PlaneToolIcons =
            { LayoutToolIcons.PlaneAuto, LayoutToolIcons.PlaneFloor, LayoutToolIcons.PlaneNS, LayoutToolIcons.PlaneEW };
        private static readonly string[] PlaneEditIcons =
            { LayoutToolIcons.PlaneFloor, LayoutToolIcons.PlaneNS, LayoutToolIcons.PlaneEW };
        private static readonly string[] ScaleIcons =
            { LayoutToolIcons.Scale1, LayoutToolIcons.Scale2, LayoutToolIcons.Scale4, LayoutToolIcons.Scale8, LayoutToolIcons.Scale16 };

        // Active number-fields registered for scroll-wheel adjustment (repopulated each compose): the
        // field key, the change handler, and the field's own clamp range (Divisions 0..256, Sides 3..24).
        // See OnMouseWheel.
        private readonly List<(string fieldKey, Action<int> onChanged, int min, int max)> _divWheelFields =
            new List<(string, Action<int>, int, int)>();

        // Last APPLIED value per number field (0.1.16): lets the typed handler tell a native
        // spinner/wheel DECREMENT below the floor (value == last − 1 → hard-stop at min) apart from
        // transient typing ("1" on the way to "12"), since both arrive as the same text event.
        private readonly Dictionary<string, int> _numberFieldValues = new Dictionary<string, int>();

        // Whether the shape picker's full-catalog grid is unfolded (the ▾ tile; Session 11). Session-local
        // GUI state, deliberately not persisted — the panel always opens compact.
        private bool _shapeGridExpanded;

        // When true, our own SetValue refreshes must NOT be treated as user input.
        private bool _suppress;

        // Whether a deferred recompose is already queued (dedupe guard; the one-shot
        // callback self-clears, and its IsOpened() check neutralizes any late fire).
        private bool _recomposePending;

        // Whether we are currently subscribed to network events (idempotent guard).
        private bool _subscribed;

        // Rows composed inert this pass (Delete mode): their tiles ignore clicks.
        private readonly HashSet<string> _inertRows = new HashSet<string>();

        private readonly List<(string key, int sel)> _initialLight = new List<(string, int)>();

        // Standalone toggles that should be lit after compose (the Transform action row). Unlike
        // _initialLight these are not an exclusive row, so each carries its own key rather than an index.
        private readonly List<string> _litToggles = new List<string>();

        // Persists the client config right now (B-24-2 fix, v0.1.24) — wired to LayoutModSystem's full
        // Draft→config sync + StoreModConfig, so a pin change is written immediately instead of relying on
        // Dispose firing on exit-to-title (which it may not).
        private readonly Action _saveConfig;

        // Re-bakes the opacity values into every guide mesh (LayoutModSystem.ApplyOpacitiesAndRebuild).
        // Opacity lives in vertex colours, so the config alone changes nothing until this runs.
        private readonly Action _applyOpacities;

        // Sets the block-occupancy recolour and rebuilds (LayoutModSystem.ApplyOccupancyRecolour).
        private readonly System.Func<bool, bool> _applyOccupancy;

        // Re-reads the world and rebuilds ONCE. The button used to toggle off then on, which rebuilt every
        // guide twice and wrote the config twice — half of the refresh lag spike reported at v0.3.79.
        private readonly Action _refreshOccupancy;

        // Settings tab (v0.3.72): when true the panel shows client display settings instead of the tool
        // rows. Session-local like _shapeGridExpanded — the panel always opens on the tool.
        private bool _settingsTab;

        // A slider drag fires its handler per step, and each step would otherwise rebuild every guide
        // mesh. True while a rebuild is already queued behind the debounce timer.
        private bool _opacityApplyPending;

        // Parks a nudged guide on its wireframe for a beat (GuideRenderer.HoldMoveMaterialization) so a
        // string of arrow clicks doesn't restart an immense shell rebuild between every one of them.
        private readonly Action<Guid> _holdMoveMaterialization;

        public GuideToolGui(ICoreClientAPI capi, DraftManager tool, ClientNetworkHandler net,
            LayoutClientConfig config, Action saveConfig, Action applyOpacities,
            System.Func<bool, bool> applyOccupancy, Action refreshOccupancy,
            Action<Guid> holdMoveMaterialization) : base(capi)
        {
            _tool = tool;
            _net = net;
            _config = config;
            _saveConfig = saveConfig;
            _applyOpacities = applyOpacities;
            _applyOccupancy = applyOccupancy;
            _refreshOccupancy = refreshOccupancy;
            _holdMoveMaterialization = holdMoveMaterialization;
        }

        /// <summary>True if <paramref name="code"/> is one of the shape picker's catalog codes — the
        /// validity check the client config uses when normalising the pinned favorites.</summary>
        public static bool IsKnownShapeCode(string code) => Array.IndexOf(ShapeCodes, code) >= 0;

        // Opened programmatically by the tool item, so no auto key combination.
        public override string ToggleKeyCombinationCode => null;

        // Modal: we want the cursor to click the tiles.
        public override bool PrefersUngrabbedMouse => true;

        public override void OnGuiOpened()
        {
            base.OnGuiOpened();
            _shapeGridExpanded = false;      // the panel always opens compact (Session-11 flag 11e)
            _settingsTab = false;            // ...and always on the tool, not the settings page
            Subscribe();
            SetupDialog();
        }

        public override void OnGuiClosed()
        {
            Unsubscribe();
            CancelPendingRecompose();
            base.OnGuiClosed();
        }

        private void OnTitleBarClose() => TryClose();

        // Scroll-wheel over a Divisions field nudges it by ±1. The number input element handles the wheel
        // NATIVELY when focused (GuiElementNumberInput.OnMouseWheel, routed via base -> composer); if it
        // consumed the event, IsHandled is set and this fallback does nothing. The fallback covers the
        // hover-without-focus case, using the element's own IsPositionInside hit-test (GUI-scale aware —
        // the first attempt's raw Bounds.PointInside check was the likely miss). Consumes the event so the
        // wheel doesn't also drive the hotbar underneath.
        public override void OnMouseWheel(MouseWheelEventArgs args)
        {
            base.OnMouseWheel(args);
            if (args.IsHandled || !IsOpened() || SingleComposer == null) return;

            int mx = capi.Input.MouseX, my = capi.Input.MouseY;
            foreach ((string fieldKey, Action<int> onChanged, int min, int max) in _divWheelFields)
            {
                GuiElementTextInput field = SingleComposer.GetTextInput(fieldKey);
                if (field == null || !field.IsPositionInside(mx, my)) continue;

                int.TryParse(field.GetText()?.Trim(), out int cur);
                int step = args.value != 0 ? Math.Sign(args.value) : Math.Sign(args.delta);
                int val = cur + step;
                if (val < min) val = min;
                if (val > max) val = max;

                if (val != cur)
                {
                    onChanged(val);
                    _numberFieldValues[fieldKey] = val;
                    _suppress = true;
                    try { field.SetValue(val.ToString()); }
                    finally { _suppress = false; }
                }
                args.SetHandled(true);
                return;
            }
        }

        // ---------------------------------------------------------------------------------
        //  Context resolution
        // ---------------------------------------------------------------------------------
        // The selected guide the Edit-mode setting rows act on, or null. Selection drives the GUI ONLY in
        // Edit mode (in Create the rows are tool defaults; Delete suppresses everything). Because the dialog
        // holds the mouse, the selection can't change while composed, so it's safe to resolve once per compose.
        private GuideData ResolveSelectedGuide()
        {
            // Move (F6) selects a guide exactly as Edit does; the two modes then do different things with
            // it — Edit drives the setting rows, Move drives the arrow pad — but resolution is shared.
            if (_tool.Mode != ToolMode.Edit && _tool.Mode != ToolMode.Transform) return null;
            Guid? sel = _tool.SelectedGuideId;
            if (sel == null) return null;
            if (_net.Guides.TryGetValue(sel.Value, out GuideData g) && g != null) return g;
            return null;
        }

        // ---------------------------------------------------------------------------------
        //  Composition
        // ---------------------------------------------------------------------------------
        private void SetupDialog()
        {
            bool deleteMode = _tool.Mode == ToolMode.Delete;
            GuideData selected = ResolveSelectedGuide();

            CairoFont font = CairoFont.WhiteSmallText();
            // Greyed rows in Delete mode (playtest revision: the detail-font dim was far too subtle):
            // GHOST fonts — same size as the live text but at a fraction of its alpha, so the rows fade
            // hard toward the dialog background while keeping their layout. Combined with leaving these
            // rows UNLIT (no pressed tile) and the input guard, the section reads unmistakably disabled.
            CairoFont ghostFont = CairoFont.WhiteSmallText();
            ghostFont.Color = new double[] { 1, 1, 1, 0.22 };
            CairoFont mainLabelFont = deleteMode ? ghostFont : font;

            // Layout metrics (pixels). UI pass: compact, SQUARE tiles left-aligned per row.
            const double pad        = 6;
            const double labelW     = 74;
            const double tile       = 42;                     // square tile edge (native-ish proportions)
            const double tileGap    = 4;
            const double rowGap     = 5;
            const double headerH    = 20;
            const double controlW   = 5 * tile + 4 * tileGap; // widest tile row = 5 tiles (Scale)

            double contentW = labelW + pad + controlW;
            // 0.1.17 (human-requested): the child area is ALREADY inset from the dialog edge by
            // ElementToDialogPadding, so starting a full TitleBarHeight down left ~a text line of dead
            // space under the title bar. Start just below where the bar actually ends instead.
            double y = Math.Max(0, GuiStyle.TitleBarHeight - GuiStyle.ElementToDialogPadding) + 4;

            _inertRows.Clear();
            _initialLight.Clear();
            _litToggles.Clear();
            _divWheelFields.Clear();

            ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            ElementBounds dialogBounds = ElementStdBounds
                .AutosizedMainDialog
                .WithAlignment(EnumDialogArea.RightMiddle)
                .WithFixedAlignmentOffset(-GuiStyle.DialogToScreenPadding, 0);

            GuiComposer c = capi.Gui
                .CreateCompo("layout:toolgui", dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar("Layout Tool", OnTitleBarClose);

            // ---- Settings gear, IN the title bar (v0.3.73, human-requested) ----
            // Added BEFORE BeginChildElements deliberately, so its bounds are in DIALOG space and it cannot
            // drag bgBounds' FitToChildren sizing around (a child at the negative y the title bar occupies
            // would). Dialog space: the bar spans y 0..TitleBarHeight, and the dialog's own width is the
            // content width plus one ElementToDialogPadding on each side, so contentW + 2*pad is the right
            // edge.
            //
            // GearRightOffset was 60 in v0.3.73 and overlapped the fixed/movable icon (human screenshot).
            // The stock pair takes roughly the last 50 px, so the gear is pushed clear of it — measured
            // from the RIGHT edge, since that is what the stock icons are anchored to.
            const double gearSize = 18;
            const double gearRightOffset = 76;
            double gearX = contentW + 2 * GuiStyle.ElementToDialogPadding - gearRightOffset;
            double gearY = (GuiStyle.TitleBarHeight - gearSize) / 2.0;
            ElementBounds gearBounds = ElementBounds.Fixed(gearX, gearY, gearSize, gearSize);
            c.AddInteractiveElement(
                new BareIconElement(capi, LayoutToolIcons.Gear, ToggleSettingsTab, gearBounds),
                "settingsgear");
            c.AddAutoSizeHoverText(_settingsTab ? "Back to the tool" : "Settings",
                CairoFont.WhiteDetailText(), 200, gearBounds.FlatCopy(), "settingsgear:ht");

            c.BeginChildElements(bgBounds);

            // Context header — a dynamic-text line so mode flips can refresh it cheaply. In CREATE the
            // shape name is split out and RIGHT-ALIGNED (0.1.16, human-requested) so it sits naturally
            // above the Current Shape chip at the row's right edge.
            ElementBounds headerBounds = ElementBounds.Fixed(0, y, contentW, headerH);
            if (_settingsTab)
            {
                c.AddDynamicText("Settings", font, headerBounds, "header");
            }
            else if (_tool.Mode == ToolMode.Create)
            {
                c.AddDynamicText("Create Mode", font, headerBounds, "header");
                CairoFont rightFont = CairoFont.WhiteSmallText();
                rightFont.Orientation = EnumTextOrientation.Right;
                c.AddDynamicText(ShapeDisplayName(_tool.Shape, _tool.Constraint), rightFont,
                    ElementBounds.Fixed(0, y, contentW, headerH), "headershape");
            }
            else
            {
                c.AddDynamicText(BuildHeaderText(), font, headerBounds, "header");
            }
            y += headerH + rowGap;

            if (_settingsTab)
            {
                BuildSettingsSection(c, font, ref y, contentW, rowGap);
                SingleComposer = c.EndChildElements().Compose();
                ApplySettingsWidgetValues();
                return;
            }

            // Mode context. In EDIT the setting rows act on the SELECTED guide (via the network senders) and
            // the same panel is reused — no separate expanding section. When Edit has no selection, those rows
            // grey out with a "click a guide" prompt. Create/Delete use the tool defaults exactly as before.
            bool editMode = _tool.Mode == ToolMode.Edit;
            bool transformMode = _tool.Mode == ToolMode.Transform;
            bool editNoSel = editMode && selected == null;
            bool settingsInert = deleteMode || editNoSel;           // setting rows disabled + ghosted
            CairoFont settingLabelFont = settingsInert ? ghostFont : font;

            // ---- Mode row (always live — the way between modes) ----
            double modeRowY = y;
            AddIconRow(c, font, ref y, labelW, pad, tile, tileGap, rowGap,
                "Mode", ModeCodes, ModeNames, ModeIcons, (int)_tool.Mode, OnModeTile, "mode", inert: false);

            // ---- Current-shape chip (0.1.15, human-requested) ----
            // A permanently-lit tile at the far right of the Mode row showing the picked shape IN YELLOW
            // (the guide-body colour), so a selection that isn't on the favorite slots is still visible at
            // a glance. Status only — clicks are swallowed. Hidden in Edit (the tool's next-guide shape
            // isn't what that mode is about); dimmed in Delete like the other shape controls.
            if (!editMode && !transformMode)
            {
                int curIdx = ClampIndex(CurrentShapeIndex(), ShapeCodes.Length);
                string chipIcon = ShapeIcons[curIdx] + LayoutToolIcons.CurrentSuffix
                    + (deleteMode ? LayoutToolIcons.GhostSuffix : "");
                ElementBounds chipB = ElementBounds.Fixed(
                    labelW + pad + 4 * (tile + tileGap), modeRowY, tile, tile);
                var chip = new GuiElementToggleButton(
                    capi, chipIcon, "",
                    CairoFont.WhiteSmallText(), OnCurrentShapeChip, chipB, toggleable: true)
                { Enabled = !deleteMode };
                c.AddInteractiveElement(chip, "curshape");
                c.AddAutoSizeHoverText("Current shape: " + ShapeNames[curIdx],
                    CairoFont.WhiteDetailText(), 260, chipB.FlatCopy(), "curshape:ht");
            }

            // MOVE mode owns the whole panel below the mode row: no setting rows at all, because a move
            // changes position and nothing else. It ends the composition itself.
            if (transformMode)
            {
                BuildTransformSection(c, font, ghostFont, selected,
                    ref y, labelW, pad, tile, tileGap, rowGap, headerH, contentW);
                SingleComposer = c.EndChildElements().Compose();
                LightInitialTiles();
                return;
            }

            if (editMode && selected != null)
            {
                // Once selected, the compact guide-info line and Deselect button replace the shape picker.
                c.AddDynamicText(BuildSelectedHeaderText(selected), font,
                    ElementBounds.Fixed(0, y + 2, contentW - 80, headerH), "gheader");
                c.AddSmallButton("Deselect", OnDeselectClicked,
                    ElementBounds.Fixed(contentW - 76, y, 76, 22));
                y += headerH + rowGap;
            }
            else if (!editMode)
            {
                // ---- Shape picker (Session 11 rework): 3 pinned favorite slots + a ▾ expand tile that
                // unfolds the full catalog. Starring (right-click) a catalog tile pins it into slot 1.
                // The old separate Favorites strip is gone — the slots ARE the favorites.
                AddShapePicker(c, mainLabelFont, ref y, labelW, pad, tile, tileGap, rowGap, deleteMode);
            }

            // ---- Scale / Projection / (Plane) / Fill / Divisions [/ Visibility] ----
            // Edit → the selected guide (guide senders, "g" values); Create/Delete → the tool defaults.
            CairoFont rowFont = editMode ? settingLabelFont : mainLabelFont;

            AddIconRow(c, rowFont, ref y, labelW, pad, tile, tileGap, rowGap,
                "Scale", ScaleCodes, ScaleNames, ScaleIcons,
                IndexOfScale(editMode ? (selected?.VoxelScale ?? _tool.Scale) : _tool.Scale),
                editMode ? OnGuideScaleTile : OnScaleTile, "scale", editMode ? settingsInert : deleteMode);

            // Projection + Fill share ONE row (0.1.17, human-requested — the two-tile Projection row left
            // three empty slots; Fill's two tiles slot into them with a compact second label). Fill greys
            // out for the Free-Shape (0.1.19, human-directed): fill is deferred on it, so the toggle
            // being live was misleading. Projection greys out for every 3D VOLUME (0.1.20 sphere, 0.1.21
            // family): a volume is always Volumetric — flattening it onto a plane is meaningless.
            bool volumePicked = editMode
                ? selected != null && GuideShapeTypes.IsVolume(selected.ShapeType)
                : GuideShapeTypes.IsVolume(_tool.Shape);
            bool surface = !volumePicked
                && (editMode ? (selected != null && selected.Projection == ProjectionMode.Surface)
                             : _tool.Projection == ProjectionMode.Surface);
            bool projFillInert = editMode ? settingsInert : deleteMode;
            bool projInert = projFillInert || volumePicked;
            string[] projNames = !projFillInert && volumePicked
                ? new[] { ProjNames[0] + "\nA 3D shape is always volumetric.",
                          ProjNames[1] + "\nNot available on a 3D shape." }
                : ProjNames;
            bool freeShapePicked = editMode
                ? selected != null && selected.ShapeType == GuideShapeType.FreeShape
                : _tool.Shape == GuideShapeType.FreeShape;
            // Fill also greys for every 3D VOLUME (0.2.17, human-directed): exposed-face meshing made a
            // filled interior emit no geometry at all, so "filled" bought nothing visible at an enormous
            // voxel cost — volumes are always their hollow shell now.
            bool fillInert = projFillInert || freeShapePicked;
            string[] fillNames = !projFillInert && freeShapePicked
                ? new[] { FillNames[0] + "\nFill is not available on a Free-Shape.",
                          FillNames[1] + "\nFill is not available on a Free-Shape." }
                : !projFillInert && volumePicked
                    ? new[] { "Shell\nShow the complete outer shell.",
                              "Wireframe\nShow only the shape's structural wires." }
                    : FillNames;
            AddIconRowPair(c, projInert ? ghostFont : rowFont, ref y, labelW, pad, tile, tileGap, rowGap,
                "Projection", ProjCodes, projNames, ProjIcons, volumePicked ? 0 : (surface ? 1 : 0),
                editMode ? OnGuideProjectionTile : OnProjectionTile, "proj", projInert,
                volumePicked ? "Form" : "Fill", fillInert ? ghostFont : rowFont,
                FillCodes, fillNames, volumePicked ? FormIcons : FillIcons,
                (volumePicked
                    ? (editMode ? (selected?.IsWireframe ?? false) : _tool.Wireframe)
                    : (editMode ? (selected?.IsFilled ?? false) : _tool.Filled)) ? 1 : 0,
                editMode ? OnGuideFillTile : OnFillTile, "fill", fillInert);

            if (surface)
            {
                if (editMode)
                    AddIconRow(c, rowFont, ref y, labelW, pad, tile, tileGap, rowGap,
                        "Plane", PlaneEditCodes, PlaneEditNames, PlaneEditIcons,
                        selected != null ? AxisToEditIndex(selected.Plane.FlattenedAxis) : 0,
                        OnGuidePlaneTile, "plane", settingsInert);
                else
                    AddIconRow(c, mainLabelFont, ref y, labelW, pad, tile, tileGap, rowGap,
                        "Plane", PlaneToolCodes, PlaneToolNames, PlaneToolIcons,
                        OverrideToToolIndex(_tool.PlaneOverride), OnPlaneTile, "plane", deleteMode);
            }

            // Divisions — and, for a polygon, the Sides field beside it on the SAME row (0.1.17,
            // human-requested). Both are the native number input + wheel + spinners. On a 3D VOLUME the
            // whole row is HIDDEN (0.1.23, human-requested — same as the Sides field being polygon-only;
            // the equal-parts marks run along a curve and a volume has none).
            bool sidesRow = editMode ? (selected != null && GuideShapeTypes.UsesSides(selected.ShapeType))
                                     : GuideShapeTypes.UsesSides(_tool.Shape);
            int divCurrent = editMode ? (selected?.Divisions ?? 0) : _tool.Divisions;
            Action<int> divChanged = editMode ? OnGuideDivisionsChanged : OnToolDivisionsChanged;
            bool numberInert = editMode ? settingsInert : deleteMode;
            if (volumePicked && sidesRow)
                AddNumberControl(c, rowFont, font, ref y, labelW, pad, tile, tileGap, rowGap, "Sides",
                    editMode ? (selected?.Sides ?? Shapes.PolygonShape.DefaultSides) : _tool.Sides,
                    Shapes.PolygonShape.MinSides, Shapes.PolygonShape.MaxSides, numberInert, "sides",
                    editMode ? OnGuideSidesChanged : OnToolSidesChanged);
            else if (volumePicked)
            {
                // no Divisions / Sides row for volumes
            }
            else if (sidesRow)
                AddNumberPairControl(c, rowFont, font, ref y, labelW, pad, tile, tileGap, rowGap,
                    "Divisions", divCurrent, 0, Shapes.DivisionMarks.MaxDivisions, "div", divChanged,
                    "Sides",
                    editMode ? (selected?.Sides ?? Shapes.PolygonShape.DefaultSides) : _tool.Sides,
                    Shapes.PolygonShape.MinSides, Shapes.PolygonShape.MaxSides, "sides",
                    editMode ? OnGuideSidesChanged : OnToolSidesChanged,
                    numberInert);
            else
                AddNumberControl(c, rowFont, font, ref y, labelW, pad, tile, tileGap, rowGap, "Divisions",
                    divCurrent, 0, Shapes.DivisionMarks.MaxDivisions, numberInert, "div", divChanged);

            // Visibility is Edit-only — a placed guide can be shown/hidden; the tool has no such state.
            if (editMode)
                AddIconRow(c, rowFont, ref y, labelW, pad, tile, tileGap, rowGap,
                    "Visibility", VisCodes, VisNames, VisIcons, (selected?.IsHidden ?? false) ? 1 : 0,
                    OnGuideVisibilityTile, "vis", settingsInert);

            SingleComposer = c.EndChildElements().Compose();

            // Light each row's current selection. Safe here: SetupDialog is only ever called
            // from OnGuiOpened or the deferred-recompose timer, never from inside a composer
            // callback, and toggle SetValue does not fire the toggle handler.
            LightInitialTiles();
        }

        // ---------------------------------------------------------------------------------
        //  Move section (F6, v0.3.86)
        // ---------------------------------------------------------------------------------
        // The whole panel below the mode row, because a move changes POSITION and nothing else — none of
        // the setting rows apply. Two controls: how far one step travels, and which way to go.
        //
        // The pad is laid out as a cross (away / left / right / toward) with the free-move toggle in its
        // centre, and the world-vertical Up/Down pair in the column at the far right. The horizontal four
        // resolve against the player's facing at click time; the dialog holds the mouse cursor, so the
        // facing physically cannot drift between two clicks of the pad.
        private void BuildTransformSection(
            GuiComposer c, CairoFont font, CairoFont ghostFont, GuideData selected,
            ref double y, double labelW, double pad, double tile, double tileGap, double rowGap,
            double headerH, double contentW)
        {
            CairoFont detail = CairoFont.WhiteDetailText();

            // With nothing selected the controls stay PUT and grey out, exactly as Edit's setting rows do —
            // a panel whose contents appear and vanish makes the mode look broken before you have used it.
            // The header carries the prompt ("Move Mode - Select a guide to move it.").
            bool inert = selected == null;
            CairoFont labelFont = inert ? ghostFont : font;

            if (!inert)
            {
                c.AddDynamicText(BuildSelectedHeaderText(selected), font,
                    ElementBounds.Fixed(0, y + 2, contentW - 80, headerH), "gheader");
                c.AddSmallButton("Deselect", OnDeselectClicked,
                    ElementBounds.Fixed(contentW - 76, y, 76, 22));
                y += headerH + rowGap;
            }

            // ---- Action row: what the direction pad DOES ----
            // Move and Copy are mutually exclusive; Mirror rides on top of either, or drives the pad alone
            // (mirror in place). Span swaps the step distance for the guide's own width along the pressed
            // axis, which is what lands a mirrored copy flush beside the original.
            c.AddStaticText("Action", Centered(labelFont),
                ElementBounds.Fixed(0, y + (tile - 16) / 2, labelW, 20));
            AddActionTile(c, detail, LayoutToolIcons.ActionMove, "Move\nSlide the guide itself.",
                "move", ElementBounds.Fixed(labelW + pad, y, tile, tile),
                _tool.Placement == DraftManager.TransformPlacement.Move, !inert);
            AddActionTile(c, detail, LayoutToolIcons.ActionCopy,
                "Copy\nLeave this guide alone and put a duplicate one step that way. "
                + "Costs chalk and counts against your voxel budget, like any placement.",
                "copy", ElementBounds.Fixed(labelW + pad + (tile + tileGap), y, tile, tile),
                _tool.Placement == DraftManager.TransformPlacement.Copy, !inert);
            AddActionTile(c, detail, LayoutToolIcons.ActionMirror,
                "Mirror\nFlip the guide along the axis you press. On its own the guide does not move; "
                + "with Move or Copy it flips as well as travels.",
                "mirror", ElementBounds.Fixed(labelW + pad + 2 * (tile + tileGap), y, tile, tile),
                _tool.TransformMirror, !inert);
            y += tile + rowGap;

            // ---- Step row: multiples of THIS guide's own voxel, never anything finer ----
            // Greyed only when nothing travels at all (Mirror driving the pad by itself). When the action
            // is one that lands things flush — a copy, or a mirrored move — this row DEFAULTS to the
            // guide's own width instead: no tile is lit, the label reads "Span", and picking a tile
            // overrides it. Clicking the lit tile again hands the span back.
            bool spanning = _tool.TransformSpanStep;
            bool stepInert = inert || _tool.Placement == DraftManager.TransformPlacement.None;
            int[] steps = DraftManager.MoveStepMultipliers;
            c.AddStaticText(spanning ? "Span" : "Step", Centered(stepInert ? ghostFont : font),
                ElementBounds.Fixed(0, y + (tile - 16) / 2, labelW, 20));
            int litStep = 0;
            for (int i = 0; i < steps.Length; i++)
            {
                if (steps[i] == _tool.MoveStep) litStep = i;
                int multiplier = steps[i];
                string tileKey = "movestep:" + i;
                ElementBounds tb = ElementBounds.Fixed(labelW + pad + i * (tile + tileGap), y, tile, tile);
                c.AddToggleButton("x" + multiplier, stepInert ? ghostFont : font,
                    on =>
                    {
                        if (_suppress || stepInert) return;
                        _tool.SelectMoveStep(multiplier, on);
                        DeferRecompose();     // the label and the lit tile both change
                    },
                    tb, tileKey);
                if (!stepInert)
                    c.AddAutoSizeHoverText(
                        StepDescription(selected.VoxelScale, multiplier)
                        + (_tool.SpanStepAvailable
                            ? "\nClick the selected one again to step by the guide's own width instead."
                            : ""),
                        detail, 280, tb.FlatCopy(), tileKey + ":ht");
            }
            // While spanning, NO tile is lit — the row is showing that its distance is being supplied by
            // the guide's own width rather than by any multiplier on it.
            if (stepInert) _inertRows.Add("movestep");
            else if (!spanning) _initialLight.Add(("movestep", litStep));
            y += tile + rowGap;

            // ---- Direction pad ----
            double x0 = labelW + pad;
            double col1 = x0 + (tile + tileGap);
            double col2 = x0 + 2 * (tile + tileGap);
            double vcol = x0 + 4 * (tile + tileGap);      // the Up/Down column, flush with the panel edge

            c.AddStaticText("Move", Centered(labelFont),
                ElementBounds.Fixed(0, y + tile + tileGap + (tile - 16) / 2, labelW, 20));

            // The pad's four CORNERS were empty; they now hold rotation. The top pair SPINS the guide about
            // the vertical axis (a turntable), the bottom pair TIPS it about the horizontal axis pointing
            // away from the player. Left button turns left, right button turns right, in both pairs — and
            // two independent axes of quarter turns reach every orientation.
            AddRotateTile(c, detail, LayoutToolIcons.RotateSpinLeft,
                "Spin left\nTurns the guide anticlockwise, seen from above.", "spinleft",
                ElementBounds.Fixed(x0, y, tile, tile), !inert);
            AddMoveTile(c, detail, LayoutToolIcons.MoveAway, MoveArrowNames[0], "away",
                ElementBounds.Fixed(col1, y, tile, tile), !inert);
            AddRotateTile(c, detail, LayoutToolIcons.RotateSpinRight,
                "Spin right\nTurns the guide clockwise, seen from above.", "spinright",
                ElementBounds.Fixed(col2, y, tile, tile), !inert);
            AddMoveTile(c, detail, LayoutToolIcons.MoveUp, MoveArrowNames[4], "up",
                ElementBounds.Fixed(vcol, y, tile, tile), !inert);
            y += tile + tileGap;

            AddMoveTile(c, detail, LayoutToolIcons.MoveLeft, MoveArrowNames[2], "left",
                ElementBounds.Fixed(x0, y, tile, tile), !inert);
            AddFreeMoveTile(c, detail, ElementBounds.Fixed(col1, y, tile, tile), !inert);
            AddMoveTile(c, detail, LayoutToolIcons.MoveRight, MoveArrowNames[3], "right",
                ElementBounds.Fixed(col2, y, tile, tile), !inert);
            y += tile + tileGap;

            AddRotateTile(c, detail, LayoutToolIcons.RotateTipLeft,
                "Tip left\nTips the guide over toward your left.", "tipleft",
                ElementBounds.Fixed(x0, y, tile, tile), !inert);
            AddMoveTile(c, detail, LayoutToolIcons.MoveToward, MoveArrowNames[1], "toward",
                ElementBounds.Fixed(col1, y, tile, tile), !inert);
            AddRotateTile(c, detail, LayoutToolIcons.RotateTipRight,
                "Tip right\nTips the guide over toward your right.", "tipright",
                ElementBounds.Fixed(col2, y, tile, tile), !inert);
            AddMoveTile(c, detail, LayoutToolIcons.MoveDown, MoveArrowNames[5], "down",
                ElementBounds.Fixed(vcol, y, tile, tile), !inert);
            y += tile + rowGap;
        }

        // A momentary pad tile: it fires on press and pops straight back up, so the pad can be clicked
        // repeatedly without a recompose between nudges (an arrow is an action, not a selection).
        private void AddMoveTile(
            GuiComposer c, CairoFont hoverFont, string icon, string hover, string code,
            ElementBounds bounds, bool enabled)
        {
            string tileKey = "movearrow:" + code;
            var btn = new GuiElementToggleButton(
                capi, enabled ? icon : icon + LayoutToolIcons.GhostSuffix, "",
                CairoFont.WhiteSmallText(),
                on =>
                {
                    if (_suppress || !on || !enabled) return;
                    _suppress = true;
                    try { SingleComposer?.GetToggleButton(tileKey)?.SetValue(false); }
                    finally { _suppress = false; }
                    OnMoveArrowTile(code);
                },
                bounds, toggleable: true)
            { Enabled = enabled };
            c.AddInteractiveElement(btn, tileKey);
            c.AddAutoSizeHoverText(hover, hoverFont, 260, bounds.FlatCopy(), tileKey + ":ht");
        }

        // A LATCHING toggle, unlike the pad tiles: these carry the state the pad reads, so they stay lit.
        // Lit state is pushed after compose (LightInitialTiles) rather than passed to the constructor,
        // matching how every other toggle in this panel is handled.
        private void AddActionTile(
            GuiComposer c, CairoFont hoverFont, string icon, string hover, string code,
            ElementBounds bounds, bool lit, bool enabled)
        {
            string tileKey = "transformaction:" + code;
            var btn = new GuiElementToggleButton(
                capi, enabled ? icon : icon + LayoutToolIcons.GhostSuffix, "",
                CairoFont.WhiteSmallText(),
                _ =>
                {
                    if (_suppress || !enabled) return;
                    OnTransformActionTile(code);
                    DeferRecompose();     // the row set changes (step row greys in and out)
                },
                bounds, toggleable: true)
            { Enabled = enabled };
            c.AddInteractiveElement(btn, tileKey);
            c.AddAutoSizeHoverText(hover, hoverFont, 280, bounds.FlatCopy(), tileKey + ":ht");
            if (lit) _litToggles.Add(tileKey);
        }

        private void OnTransformActionTile(string code)
        {
            switch (code)
            {
                case "move": _tool.ToggleTransformPlacement(DraftManager.TransformPlacement.Move); break;
                case "copy": _tool.ToggleTransformPlacement(DraftManager.TransformPlacement.Copy); break;
                case "mirror": _tool.ToggleTransformMirror(); break;
            }
        }

        // Momentary, exactly like the move arrows — a rotation is an action, not a selection.
        private void AddRotateTile(
            GuiComposer c, CairoFont hoverFont, string icon, string hover, string code,
            ElementBounds bounds, bool enabled)
        {
            string tileKey = "moverotate:" + code;
            var btn = new GuiElementToggleButton(
                capi, enabled ? icon : icon + LayoutToolIcons.GhostSuffix, "",
                CairoFont.WhiteSmallText(),
                on =>
                {
                    if (_suppress || !on || !enabled) return;
                    _suppress = true;
                    try { SingleComposer?.GetToggleButton(tileKey)?.SetValue(false); }
                    finally { _suppress = false; }
                    OnRotateTile(code);
                },
                bounds, toggleable: true)
            { Enabled = enabled };
            c.AddInteractiveElement(btn, tileKey);
            c.AddAutoSizeHoverText(hover, hoverFont, 260, bounds.FlatCopy(), tileKey + ":ht");
        }

        private void AddFreeMoveTile(
            GuiComposer c, CairoFont hoverFont, ElementBounds bounds, bool enabled)
        {
            var btn = new GuiElementToggleButton(
                capi, enabled ? LayoutToolIcons.MoveFree : LayoutToolIcons.MoveFree + LayoutToolIcons.GhostSuffix,
                "", CairoFont.WhiteSmallText(),
                on => { if (!_suppress && enabled) _tool.SetFreeMove(on); },
                bounds, toggleable: true)
            { Enabled = enabled };
            c.AddInteractiveElement(btn, "movefree");
            c.AddAutoSizeHoverText(
                "Free-move: close this panel and the guide follows your crosshair, snapped to its own "
                + "voxel scale. Left-click places it; right-click puts it back where it started.",
                hoverFont, 280, bounds.FlatCopy(), "movefree:ht");
        }

        // Plain-language "how far is one click", from the guide's own scale times the chosen multiplier.
        private static string StepDescription(int voxelScale, int multiplier)
        {
            int sixteenths = voxelScale * multiplier;
            string distance = sixteenths % 16 == 0
                ? (sixteenths / 16) + (sixteenths / 16 == 1 ? " block" : " blocks")
                : sixteenths + "/16 of a block";
            return "One step moves " + distance + " - " + multiplier
                + (multiplier == 1 ? " voxel" : " voxels") + " at this guide's scale ("
                + voxelScale + "/16 each).";
        }

        // Every direction button now runs through the Move/Copy/Mirror toggles, so one pad covers five
        // actions. The DIRECTION picks the world axis; what happens along it depends on the toggles.
        private void OnMoveArrowTile(string code)
        {
            GuideData g = ResolveSelectedGuide();
            if (g == null) return;

            bool mirroring = _tool.TransformMirror;
            bool placing = _tool.Placement != DraftManager.TransformPlacement.None;

            // Direction resolved as a unit step first, so its axis can be read off for the mirror plane and
            // for the span distance before the real magnitude is applied.
            (int ux, int uy, int uz) = MoveOffset(code, 1);
            if (ux == 0 && uy == 0 && uz == 0) return;
            PlaneAxis axis = ux != 0 ? PlaneAxis.X : uy != 0 ? PlaneAxis.Y : PlaneAxis.Z;

            int step = placing
                ? (_tool.TransformSpanStep
                    ? SpanStep(g, axis)
                    : g.VoxelScale * _tool.MoveStep)
                : 0;

            bool asCopy = _tool.Placement == DraftManager.TransformPlacement.Copy;
            if (!mirroring && step == 0) return;

            // A run of copies in one direction marches OUTWARD — one step out, then two, then three — so
            // holding down a direction lays a continuous line rather than stacking every duplicate in the
            // same place. Measured from the still-selected original, which is why the selection does not
            // follow the copies. Changing guide, direction, distance or action starts a fresh run.
            if (asCopy) step *= _tool.NextCopyRunFactor(g.Id, code);
            else _tool.ResetCopyRun();

            int dx = ux * step, dy = uy * step, dz = uz * step;

            _holdMoveMaterialization?.Invoke(g.Id);
            _net.SendTransform(
                g.Id, dx, dy, dz, mirroring ? (int)axis : -1, PlaneAxis.Y, 0, asCopy);
        }

        // The guide's own extent along the pressed axis, in 1/16 units — the "span" step, which lands a
        // copy flush against the original instead of overlapping it.
        private int SpanStep(GuideData g, PlaneAxis axis)
        {
            int span = Systems.GuideManager.SpanAlong(g, axis);
            return span > 0 ? span : g.VoxelScale * _tool.MoveStep;
        }

        // Resolves a pad direction to a world offset in 1/16-block units. Up/Down are world vertical; the
        // other four are read from the player's facing, snapped to whichever horizontal axis they are
        // closest to. Vintage Story is Y-up with +X east and +Z south, so for a forward of (fx, fz) the
        // player's left is (fz, -fx) and their right is (-fz, fx).
        private (int dx, int dy, int dz) MoveOffset(string code, int step)
        {
            if (code == "up") return (0, step, 0);
            if (code == "down") return (0, -step, 0);

            (int fx, int fz) = FacingAxis();
            return code switch
            {
                "away"   => (fx * step, 0, fz * step),
                "toward" => (-fx * step, 0, -fz * step),
                "left"   => (fz * step, 0, -fx * step),
                "right"  => (-fz * step, 0, fx * step),
                _        => (0, 0, 0)
            };
        }

        // F12: a quarter turn about a world axis. The SPIN pair is about world vertical, so it needs no
        // facing at all — "anticlockwise from above" is the same turn wherever you stand. The TIP pair is
        // about the horizontal axis running away from you, so it does, and it resolves the same way the
        // move arrows do.
        private void OnRotateTile(string code)
        {
            GuideData g = ResolveSelectedGuide();
            if (g == null) return;

            _holdMoveMaterialization?.Invoke(g.Id);

            bool asCopy = _tool.Placement == DraftManager.TransformPlacement.Copy;

            if (code == "spinleft" || code == "spinright")
            {
                // GuideManager rotates by the right-hand rule about the POSITIVE axis. About +Y that sends
                // east to north — anticlockwise seen from above (north up, east right) — so a positive turn
                // is "spin left".
                int spin = code == "spinleft" ? 1 : -1;
                // Copy + rotate is the four-corner-towers gesture: duplicate this, turned a quarter.
                if (asCopy) _net.SendTransform(g.Id, 0, 0, 0, -1, PlaneAxis.Y, spin, true);
                else _net.SendRotate(g.Id, PlaneAxis.Y, spin);
                return;
            }

            // Tip = roll about the horizontal axis running AWAY from the player. A +90 degree right-hand
            // turn about that forward axis carries the guide's top toward the player's RIGHT (up' = f x up,
            // which is -left), so tipping left is the negative turn about forward.
            (int fx, int fz) = FacingAxis();
            PlaneAxis axis = fx != 0 ? PlaneAxis.X : PlaneAxis.Z;
            // Forward is only the POSITIVE axis half the time; facing the other way reverses the whole
            // sense, so the facing's sign has to ride along or the buttons swap meaning when you turn round.
            int sign = (fx != 0 ? fx : fz) > 0 ? 1 : -1;
            int turns = (code == "tipleft" ? -1 : 1) * sign;
            if (asCopy) _net.SendTransform(g.Id, 0, 0, 0, -1, axis, turns, true);
            else _net.SendRotate(g.Id, axis, turns);
        }

        private (int fx, int fz) FacingAxis()
        {
            Vec3f view = capi?.World?.Player?.Entity?.Pos?.GetViewVector();
            if (view == null) return (0, 1);
            return Math.Abs(view.X) >= Math.Abs(view.Z)
                ? (view.X >= 0 ? 1 : -1, 0)
                : (0, view.Z >= 0 ? 1 : -1);
        }

        // ---------------------------------------------------------------------------------
        //  Settings tab (v0.3.72)
        // ---------------------------------------------------------------------------------
        // Client display settings, changeable in play instead of by hand-editing layout-client.json and
        // restarting. Deliberately client-only: server settings (voxel caps, claims, moderation) stay on
        // their admin commands and must not appear in a player-facing panel.
        //
        // Currently just guide opacity — added to answer a specific question, whether a fainter guide makes
        // it easier to see material inside it while chiselling. The alternative under consideration is
        // colouring filled voxels differently, which is a much larger change (see PLAN_BLOCK_OCCUPANCY).
        private void BuildSettingsSection(
            GuiComposer c, CairoFont font, ref double y, double contentW, double rowGap)
        {
            // Explanatory paragraphs removed in v0.3.87 — they overflowed the dialog bounds. The labels and
            // controls carry the meaning for now; the prose is a candidate for hover text later.
            c.AddStaticText("Guide opacity", font, ElementBounds.Fixed(0, y, contentW, 20));
            y += 20 + 2;

            c.AddSlider(OnOpacityBodySlider, ElementBounds.Fixed(0, y, contentW, 22), "opacitybody");
            y += 22 + rowGap;

            // ---- Built-voxel colouring (v0.3.79) ----
            c.AddStaticText("Show built voxels", font, ElementBounds.Fixed(0, y, contentW, 20));
            y += 20 + 2;

            c.AddSwitch(OnOccupancyToggled, ElementBounds.Fixed(0, y, 30, 22), "occupancy", 22);
            y += 22 + rowGap;

            c.AddSmallButton("Re-read the world", OnOccupancyRefresh, ElementBounds.Fixed(0, y, 140, 22));
            y += 22 + rowGap;

            c.AddSmallButton("Reset to default", OnResetOpacity, ElementBounds.Fixed(0, y, 130, 22));
            // Belt and braces: the gear is the intended way back, but it lives inside the stock title bar
            // and shares that space with the bar's own drag handling. This button guarantees a way out even
            // if the gear's hit area turns out to be contested there.
            c.AddSmallButton("< Back to tool", OnBackToTool,
                ElementBounds.Fixed(contentW - 110, y, 110, 22));
            y += 22 + rowGap;
        }

        // Post-compose value push for the settings widgets, mirroring what LightInitialTiles does for the
        // tool rows. Runs under _suppress because SetValue fires the change handler on some builds.
        private void ApplySettingsWidgetValues()
        {
            if (SingleComposer == null) return;
            _suppress = true;
            try
            {
                // Floor of 5%: a guide at 0 is invisible, which reads as a bug rather than a setting.
                SingleComposer.GetSlider("opacitybody")?.SetValues(
                    OpacityPercent(_config.OpacityBody), 5, 100, 1, "%");
                SingleComposer.GetSwitch("occupancy")?.SetValue(_config.OccupancyRecolour);
            }
            catch (Exception e) { capi.Logger.Warning("[Layout] settings widgets: {0}", e.Message); }
            finally { _suppress = false; }
        }

        private static int OpacityPercent(float alpha) =>
            Math.Min(100, Math.Max(5, (int)Math.Round(alpha * 100f)));

        // The title-bar gear: a plain switch between the tool page and the settings page.
        private void ToggleSettingsTab()
        {
            _settingsTab = !_settingsTab;
            DeferRecompose();      // never recompose from inside a composer callback
        }

        private bool OnBackToTool()
        {
            _settingsTab = false;
            DeferRecompose();
            return true;
        }

        private bool OnOpacityBodySlider(int value)
        {
            if (_suppress) return true;
            _config.OpacityBody = value / 100f;
            QueueOpacityApply();
            return true;
        }

        // Rebuilds every guide, so it runs straight away rather than through the opacity debounce — this is
        // a deliberate click, not a drag, and coalescing would only delay the feedback.
        private void OnOccupancyToggled(bool on)
        {
            if (_suppress) return;
            _applyOccupancy?.Invoke(on);
        }

        private bool OnOccupancyRefresh()
        {
            if (!_config.OccupancyRecolour) return true;   // nothing to re-read while it is off
            _refreshOccupancy?.Invoke();
            return true;
        }

        private bool OnResetOpacity()
        {
            _config.OpacityBody = DefaultBodyOpacity;
            QueueOpacityApply();
            DeferRecompose();      // move the slider handle back under the new value
            return true;
        }

        // The shipped default for OpacityBody in LayoutClientConfig. Duplicated as a constant rather than
        // read from a fresh config instance so Reset cannot drag along every other default with it.
        private const float DefaultBodyOpacity = 0.5f;

        // Opacity is baked into vertex colours, so applying it means rebuilding every guide mesh — far too
        // expensive to do per slider step on a large guide. Coalesce into one rebuild once the slider goes
        // quiet. The value itself is already live in _config, so nothing is lost if several steps collapse.
        private void QueueOpacityApply()
        {
            if (_opacityApplyPending) return;
            _opacityApplyPending = true;
            capi.Event.RegisterCallback(_ =>
            {
                _opacityApplyPending = false;
                _config.Normalize();
                _applyOpacities?.Invoke();
                _saveConfig?.Invoke();
            }, 250);
        }

        // A labelled number-input row (Session 10's Divisions control, generalised in Session 11 for the
        // polygon's Sides): the game's own NUMBER input (GuiElementNumberInput) — native scroll-wheel
        // handling plus small up/down spinner buttons. IntMode/Interval(=1) are set post-compose in
        // LightInitialTiles. Typing applies per keystroke through the clamp handler; the dialog-level
        // OnMouseWheel fallback keeps hover-scroll working even without focus. Programmatic SetValue runs
        // under the _suppress guard. In inert (Delete / no-selection) mode the row is ghost static text.
        private void AddNumberControl(
            GuiComposer c, CairoFont labelFont, CairoFont font, ref double y,
            double labelW, double pad, double tile, double tileGap, double rowGap, string label,
            int current, int min, int max, bool inert, string key, Action<int> onChanged)
        {
            ElementBounds labelBounds = ElementBounds.Fixed(0, y + (tile - 16) / 2, labelW, 20);
            c.AddStaticText(label, Centered(labelFont), labelBounds);

            if (inert)
            {
                ElementBounds vb = ElementBounds.Fixed(labelW + pad, y + (tile - 16) / 2, 80, 20);
                c.AddStaticText(current > 1 ? current.ToString() : (min > 0 ? current.ToString() : "Off"),
                    labelFont, vb);
                y += tile + rowGap;
                return;
            }

            // The field spans exactly the first TWO tile columns of the icon rows above (its up/down
            // spinners render INSIDE these bounds at the right edge), so both its left edge and the
            // arrows' right edge land on the tile grid (0.2.18, human-requested).
            const double fieldH = 30;
            double fieldW = 2 * tile + tileGap;
            ElementBounds fieldBounds = ElementBounds.Fixed(labelW + pad, y + (tile - fieldH) / 2, fieldW, fieldH);
            c.AddNumberInput(fieldBounds, text => OnNumberTyped(text, key + ":text", onChanged, min, max), font, key + ":text");

            _pendingFieldText.Add((key + ":text", current > 0 ? current.ToString() : min.ToString()));
            _divWheelFields.Add((key + ":text", onChanged, min, max));
            _numberFieldValues[key + ":text"] = current;
            y += tile + rowGap;
        }

        // TWO number fields sharing one row (0.1.17): Divisions + the polygon's Sides. Same native
        // inputs, wheel plumbing, and clamp handling as the single-field row; the second label is compact.
        private void AddNumberPairControl(
            GuiComposer c, CairoFont labelFont, CairoFont font, ref double y,
            double labelW, double pad, double tile, double tileGap, double rowGap,
            string label1, int current1, int min1, int max1, string key1, Action<int> onChanged1,
            string label2, int current2, int min2, int max2, string key2, Action<int> onChanged2,
            bool inert)
        {
            // 0.2.18 (human-requested): everything sits on the icon rows' tile grid — the Divisions field
            // spans tile columns 0–1, the Sides label takes column 2, and the Sides field spans columns
            // 3–4, so every field edge (spinner arrows included — they render inside the field bounds)
            // lines up with the tiles above.
            const double fieldH = 30;
            double fieldW = 2 * tile + tileGap;
            double col2X = labelW + pad + 2 * (tile + tileGap);
            double col3X = labelW + pad + 3 * (tile + tileGap);

            c.AddStaticText(label1, Centered(labelFont), ElementBounds.Fixed(0, y + (tile - 16) / 2, labelW, 20));

            if (inert)
            {
                c.AddStaticText(current1 > 1 ? current1.ToString() : "Off", labelFont,
                    ElementBounds.Fixed(labelW + pad, y + (tile - 16) / 2, 60, 20));
                c.AddStaticText(label2, labelFont,
                    ElementBounds.Fixed(col2X, y + (tile - 16) / 2, tile, 20));
                c.AddStaticText(current2.ToString(), labelFont,
                    ElementBounds.Fixed(col3X, y + (tile - 16) / 2, 60, 20));
                y += tile + rowGap;
                return;
            }

            c.AddNumberInput(ElementBounds.Fixed(labelW + pad, y + (tile - fieldH) / 2, fieldW, fieldH),
                text => OnNumberTyped(text, key1 + ":text", onChanged1, min1, max1), font, key1 + ":text");
            _pendingFieldText.Add((key1 + ":text", current1 > 0 ? current1.ToString() : min1.ToString()));
            _divWheelFields.Add((key1 + ":text", onChanged1, min1, max1));
            _numberFieldValues[key1 + ":text"] = current1;

            c.AddStaticText(label2, Centered(labelFont),
                ElementBounds.Fixed(col2X, y + (tile - 16) / 2, tile, 20));
            c.AddNumberInput(ElementBounds.Fixed(col3X, y + (tile - fieldH) / 2, fieldW, fieldH),
                text => OnNumberTyped(text, key2 + ":text", onChanged2, min2, max2), font, key2 + ":text");
            _pendingFieldText.Add((key2 + ":text", current2 > 0 ? current2.ToString() : min2.ToString()));
            _divWheelFields.Add((key2 + ":text", onChanged2, min2, max2));
            _numberFieldValues[key2 + ":text"] = current2;

            y += tile + rowGap;
        }

        private readonly List<(string key, string text)> _pendingFieldText = new List<(string, string)>();

        private void OnNumberTyped(string text, string fieldKey, Action<int> onChanged, int min, int max)
        {
            if (_suppress) return;
            if (string.IsNullOrWhiteSpace(text))
            {
                if (min <= 0) { onChanged(0); _numberFieldValues[fieldKey] = 0; }   // Divisions: empty = 0
                return;
            }
            if (!int.TryParse(text.Trim(), out int value)) return;           // ignore partial/garbled input
            int clamped = value < min ? min : value > max ? max : value;

            // UNDER-min handling in a floored field (0.1.16 fix — the Sides field could display 2, 1, 0…):
            // the native spinner/wheel has no minimum, so a downward step below the floor arrives here as
            // last−1 — HARD-STOP it: re-apply min and snap the display back. Anything else under min is
            // transient TYPING ("1" on the way to "12"): apply nothing, leave the display for more digits.
            if (value < min && min > 0)
            {
                bool decremented = _numberFieldValues.TryGetValue(fieldKey, out int last) && value == last - 1;
                if (!decremented) return;
                value = int.MinValue;                // force the display snap below
            }
            onChanged(clamped);
            _numberFieldValues[fieldKey] = clamped;
            if (clamped != value)
            {
                _suppress = true;
                try { SingleComposer?.GetTextInput(fieldKey)?.SetValue(clamped.ToString()); }
                finally { _suppress = false; }
            }
        }

        private void OnToolDivisionsChanged(int value) => _tool.SetDivisions(value);

        private void OnGuideDivisionsChanged(int value)
        {
            GuideData g = ResolveSelectedGuide();
            if (g != null && g.Divisions != value) _net.SendSetDivisions(g.Id, value);
        }

        private void OnToolSidesChanged(int value) => _tool.SetSides(value);

        private void OnGuideSidesChanged(int value)
        {
            GuideData g = ResolveSelectedGuide();
            if (g != null && g.Sides != value) _net.SendSetSides(g.Id, value);
        }

        // ---- Icon tiles (UI pass) ----------------------------------------------------------------------
        // Square icon tiles wired into the SAME exclusive-toggle plumbing as the old text tiles (same
        // "{key}:{i}" keys, relight, inert handling, recompose) — only the tile FACE is a registered glyph.
        // Tile size + gaps come from the compact layout metrics; a per-tile hover text names the option.

        // A toggle tile that also reports right-clicks (Session 11: starring a catalog shape pins it as a
        // favorite). The stock button only knows left-clicks; right press/release are swallowed here so
        // they can't fall through to the world or fire the toggle.
        private sealed class StarrableToggleButton : GuiElementToggleButton
        {
            public Action OnRightClick;

            public StarrableToggleButton(ICoreClientAPI capi, string icon, CairoFont font,
                Action<bool> onToggled, ElementBounds bounds)
                : base(capi, icon, "", font, onToggled, bounds, toggleable: true) { }

            public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
            {
                if (args.Button == EnumMouseButton.Right)
                {
                    OnRightClick?.Invoke();
                    args.Handled = true;
                    return;
                }
                base.OnMouseDownOnElement(api, args);
            }

            public override void OnMouseUpOnElement(ICoreClientAPI api, MouseEvent args)
            {
                if (args.Button == EnumMouseButton.Right)
                {
                    args.Handled = true;
                    return;
                }
                base.OnMouseUpOnElement(api, args);
            }
        }

        // One icon toggle tile: a stock GuiElementToggleButton whose face is a CustomIcons glyph.
        // Disabled (Delete-mode greyed) tiles swap to the glyph's "-ghost" variant (0.1.16 — the button's
        // native Enabled=false dims its chrome but NOT a custom icon face, so the tiles didn't read as
        // greyed) on top of the native disabled state. Hover text is AUTO-SIZED to its content;
        // onRightClick, when given, makes the tile starrable.
        private void AddIconTile(GuiComposer c, string icon, string hover, ElementBounds bounds,
            string rowKey, string code, string tileKey, Action<string, string> onTile, bool enabled,
            Action onRightClick = null)
        {
            string face = enabled ? icon : icon + LayoutToolIcons.GhostSuffix;
            GuiElementToggleButton btn;
            if (onRightClick != null)
            {
                btn = new StarrableToggleButton(capi, face, CairoFont.WhiteSmallText(),
                    on => OnTileToggled(rowKey, code, tileKey, on, onTile), bounds)
                { Enabled = enabled, OnRightClick = onRightClick };
            }
            else
            {
                btn = new GuiElementToggleButton(
                    capi, face, "", CairoFont.WhiteSmallText(),
                    on => OnTileToggled(rowKey, code, tileKey, on, onTile), bounds, toggleable: true)
                { Enabled = enabled };
            }
            c.AddInteractiveElement(btn, tileKey);
            if (!string.IsNullOrEmpty(hover))
                c.AddAutoSizeHoverText(hover, CairoFont.WhiteDetailText(), 260, bounds.FlatCopy(), tileKey + ":ht");
        }

        // A registered Layout glyph drawn BARE — no button plate behind it (v0.3.74). AddIconButton paints
        // the game's button chrome, which reads as a tile; the stock close and fixed/movable icons beside it
        // in the title bar are bare glyphs, so a plated one is the odd element out.
        //
        // The press is claimed as well as the release: the title bar sits under these bounds and would
        // otherwise start a window drag from a click meant for the icon.
        private sealed class BareIconElement : GuiElement
        {
            private readonly ICoreClientAPI _capi;
            private readonly string _icon;
            private readonly Action _onClick;

            public BareIconElement(ICoreClientAPI capi, string icon, Action onClick, ElementBounds bounds)
                : base(capi, bounds)
            {
                _capi = capi;
                _icon = icon;
                _onClick = onClick;
            }

            public override void ComposeElements(Context ctx, ImageSurface surface)
            {
                Bounds.CalcWorldBounds();
                var icons = _capi?.Gui?.Icons?.CustomIcons;
                if (icons == null || !icons.ContainsKey(_icon)) return;
                icons[_icon](ctx, (int)Bounds.drawX, (int)Bounds.drawY,
                    (float)Bounds.InnerWidth, (float)Bounds.InnerHeight,
                    new double[] { 1, 1, 1, 0.85 });
            }

            public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
            {
                if (args.Button != EnumMouseButton.Left) { base.OnMouseDownOnElement(api, args); return; }
                args.Handled = true;
            }

            public override void OnMouseUpOnElement(ICoreClientAPI api, MouseEvent args)
            {
                if (args.Button != EnumMouseButton.Left) { base.OnMouseUpOnElement(api, args); return; }
                _onClick?.Invoke();
                args.Handled = true;
            }
        }

        // A plain white rule (0.1.16): separates the pinned favorite slots from the unfolded catalog.
        private sealed class HRuleElement : GuiElement
        {
            public HRuleElement(ICoreClientAPI capi, ElementBounds bounds) : base(capi, bounds) { }

            public override void ComposeElements(Context ctx, ImageSurface surface)
            {
                Bounds.CalcWorldBounds();
                ctx.SetSourceRGBA(1, 1, 1, 0.55);
                ctx.Rectangle(Bounds.drawX, Bounds.drawY, Bounds.InnerWidth, Bounds.InnerHeight);
                ctx.Fill();
            }
        }

        // Label + one row of exclusive SQUARE icon tiles, left-aligned.
        private void AddIconRow(
            GuiComposer c, CairoFont labelFont, ref double y,
            double labelW, double pad, double tile, double tileGap, double rowGap,
            string label, string[] codes, string[] names, string[] icons, int selectedIndex,
            Action<string, string> onTile, string key, bool inert)
        {
            ElementBounds labelBounds = ElementBounds.Fixed(0, y + (tile - 16) / 2, labelW, 20);
            c.AddStaticText(label, Centered(labelFont), labelBounds);

            int sel = ClampIndex(selectedIndex, codes.Length);
            for (int i = 0; i < codes.Length; i++)
            {
                string tileKey = key + ":" + i;
                ElementBounds tb = ElementBounds.Fixed(labelW + pad + i * (tile + tileGap), y, tile, tile);
                AddIconTile(c, icons[i], names[i], tb, key, codes[i], tileKey, onTile, !inert);
            }
            if (inert) _inertRows.Add(key);
            y += tile + rowGap;
            if (!inert) _initialLight.Add((key, sel));
        }

        // TWO short exclusive tile groups sharing one row (0.1.17): the left group exactly like
        // AddIconRow, then a compact second label + its tiles in the leftover slots (Projection + Fill).
        // Each group carries its OWN inert state + label font (0.1.19: Fill greys alone on a Free-Shape)
        // and its own key, so the relight plumbing is untouched.
        private void AddIconRowPair(
            GuiComposer c, CairoFont labelFont, ref double y,
            double labelW, double pad, double tile, double tileGap, double rowGap,
            string label1, string[] codes1, string[] names1, string[] icons1, int sel1,
            Action<string, string> onTile1, string key1, bool inert1,
            string label2, CairoFont labelFont2, string[] codes2, string[] names2, string[] icons2, int sel2,
            Action<string, string> onTile2, string key2, bool inert2)
        {
            c.AddStaticText(label1, Centered(labelFont), ElementBounds.Fixed(0, y + (tile - 16) / 2, labelW, 20));

            double x = labelW + pad;
            int s1 = ClampIndex(sel1, codes1.Length);
            for (int i = 0; i < codes1.Length; i++)
            {
                AddIconTile(c, icons1[i], names1[i], ElementBounds.Fixed(x, y, tile, tile),
                    key1, codes1[i], key1 + ":" + i, onTile1, !inert1);
                x += tile + tileGap;
            }

            // The second group's tiles snap to the SAME column grid as every other row (0.1.18 fix —
            // they sat a few pixels left of the tiles above): its label takes exactly the one tile slot
            // between the groups, and the tiles start on the next slot boundary.
            double x2 = labelW + pad + (codes1.Length + 1) * (tile + tileGap);
            c.AddStaticText(label2, Centered(labelFont2),
                ElementBounds.Fixed(x + 2, y + (tile - 16) / 2, x2 - x - 6, 20));

            int s2 = ClampIndex(sel2, codes2.Length);
            for (int i = 0; i < codes2.Length; i++)
            {
                AddIconTile(c, icons2[i], names2[i], ElementBounds.Fixed(x2 + i * (tile + tileGap), y, tile, tile),
                    key2, codes2[i], key2 + ":" + i, onTile2, !inert2);
            }

            if (inert1) _inertRows.Add(key1);
            if (inert2) _inertRows.Add(key2);
            y += tile + rowGap;
            if (!inert1) _initialLight.Add((key1, s1));
            if (!inert2) _initialLight.Add((key2, s2));
        }

        // ---- Shape picker (Session 11; 0.1.15 hard-kept rework) --------------------------------------
        //
        // The main row holds the player's FOUR pinned shapes (persisted in layout-client.json; empty
        // slots show a faint placeholder) plus a chevron tile that unfolds the full catalog beneath.
        // Left-click anywhere selects. RIGHT-click stars/unstars: starring NEVER evicts — a full list
        // shows a message telling you to unstar first (right-click a starred catalog tile, which wears a
        // small ★ badge, or the slot tile itself). Selecting from the catalog folds it away again.
        private const int FavoriteSlots = 4;

        private void AddShapePicker(
            GuiComposer c, CairoFont labelFont, ref double y,
            double labelW, double pad, double tile, double tileGap, double rowGap, bool inert)
        {
            ElementBounds labelBounds = ElementBounds.Fixed(0, y + (tile - 16) / 2, labelW, 20);
            c.AddStaticText("Shape", Centered(labelFont), labelBounds);

            List<string> favs = _config.FavoriteShapes;
            string currentCode = ShapeCodes[ClampIndex(CurrentShapeIndex(), ShapeCodes.Length)];

            int litSlot = -1;
            for (int i = 0; i < FavoriteSlots; i++)
            {
                ElementBounds tb = ElementBounds.Fixed(labelW + pad + i * (tile + tileGap), y, tile, tile);
                int catalog = i < favs.Count ? Array.IndexOf(ShapeCodes, favs[i]) : -1;
                if (catalog >= 0)
                {
                    string code = favs[i];
                    AddIconTile(c, ShapeIcons[catalog],
                        ShapeNames[catalog] + "\nRight-Click to unpin and free this slot.", tb,
                        "shape", code, "shape:" + i, OnShapeTile, !inert,
                        onRightClick: inert ? null : (Action)(() => OnUnstarFavorite(code)));
                    if (code == currentCode) litSlot = i;
                }
                else
                {
                    // An empty, hard-kept slot: a faint placeholder that only right-click-starring fills.
                    var empty = new GuiElementToggleButton(
                        capi, LayoutToolIcons.EmptySlot, "", CairoFont.WhiteSmallText(),
                        _ => { }, tb, toggleable: false)
                    { Enabled = false };
                    c.AddInteractiveElement(empty, "shapeempty:" + i);
                    c.AddAutoSizeHoverText("Empty Slot - Right-Click a shape below to pin it.",
                        CairoFont.WhiteDetailText(), 260, tb.FlatCopy(), "shapeempty:" + i + ":ht");
                }
            }

            // The expand tile: not part of the exclusive row — its lit state means "catalog open".
            ElementBounds eb = ElementBounds.Fixed(labelW + pad + FavoriteSlots * (tile + tileGap), y, tile, tile);
            string chevron = _shapeGridExpanded ? LayoutToolIcons.ExpandUp : LayoutToolIcons.ExpandDown;
            var expandBtn = new GuiElementToggleButton(
                capi, inert ? chevron + LayoutToolIcons.GhostSuffix : chevron,
                "", CairoFont.WhiteSmallText(), OnExpandToggled, eb, toggleable: true)
            { Enabled = !inert };
            c.AddInteractiveElement(expandBtn, "shapemore");
            c.AddAutoSizeHoverText(
                litSlot < 0 && !_shapeGridExpanded
                    ? "All shapes (current: " + ShapeDisplayName(_tool.Shape, _tool.Constraint) + ")"
                    : "All shapes",
                CairoFont.WhiteDetailText(), 260, eb.FlatCopy(), "shapemore:ht");

            y += tile + rowGap;
            if (!inert && litSlot >= 0) _initialLight.Add(("shape", litSlot));

            // The unfolded catalog (0.1.16: FIVE per row, divided from the slots by a white rule; 0.1.21:
            // the 3D VOLUMES are split into their own group under a SECOND separator). The current pick is
            // lit; pinned tiles show YELLOW; right-click pins (or unpins).
            if (_shapeGridExpanded)
            {
                // Separator between "your slots" and the catalog (human-requested).
                c.AddStaticElement(new HRuleElement(capi,
                    ElementBounds.Fixed(labelW + pad, y, 5 * tile + 4 * tileGap, 2)), "shapesep");
                y += 2 + rowGap;

                // Partition the catalog into 2D shapes and 3D volumes, preserving order + global index.
                var twoD = new List<int>();
                var threeD = new List<int>();
                for (int i = 0; i < ShapeCodes.Length; i++)
                    (GuideShapeTypes.IsVolume(ShapeFromCode(ShapeCodes[i]).Item1) ? threeD : twoD).Add(i);

                DrawCatalogGroup(c, twoD, "2D", ref y, labelW, pad, tile, tileGap, rowGap, favs, inert);
                if (threeD.Count > 0)
                {
                    c.AddStaticElement(new HRuleElement(capi,
                        ElementBounds.Fixed(labelW + pad, y, 5 * tile + 4 * tileGap, 2)), "shapesep3d");
                    y += 2 + rowGap;
                    DrawCatalogGroup(c, threeD, "3D", ref y, labelW, pad, tile, tileGap, rowGap, favs, inert);
                }

                if (inert) _inertRows.Add("shapecat");
                if (!inert) _initialLight.Add(("shapecat", ClampIndex(CurrentShapeIndex(), ShapeCodes.Length)));
            }
            if (inert) _inertRows.Add("shape");
        }

        // Draws one catalog group (2D or 3D) as a 5-wide grid, with its section label centred vertically
        // in the label column to the left (0.1.21). Tile keys stay the GLOBAL catalog index so the
        // exclusive-row relight/lighting plumbing is untouched.
        private void DrawCatalogGroup(GuiComposer c, List<int> indices, string sectionLabel, ref double y,
            double labelW, double pad, double tile, double tileGap, double rowGap,
            List<string> favs, bool inert)
        {
            const int perRow = 5;
            int rows = (indices.Count + perRow - 1) / perRow;
            double groupH = rows * tile + (rows - 1) * tileGap;

            CairoFont labelFont = Centered(inert ? Ghost() : CairoFont.WhiteSmallText());
            c.AddStaticText(sectionLabel, labelFont,
                ElementBounds.Fixed(0, y + (groupH - 16) / 2, labelW, 20));

            for (int k = 0; k < indices.Count; k++)
            {
                int i = indices[k];
                int row = k / perRow, col = k % perRow;
                string code = ShapeCodes[i];
                bool pinned = favs.Contains(code);
                ElementBounds tb = ElementBounds.Fixed(
                    labelW + pad + col * (tile + tileGap), y + row * (tile + tileGap), tile, tile);
                AddIconTile(c,
                    pinned ? ShapeIcons[i] + LayoutToolIcons.CurrentSuffix : ShapeIcons[i],
                    ShapeNames[i] + (pinned ? "\nPinned - Right-Click to unpin."
                                            : "\nRight-Click to pin into a free slot."),
                    tb, "shapecat", code, "shapecat:" + i, OnCatalogShapeTile, !inert,
                    onRightClick: inert ? null
                        : (Action)(pinned ? () => OnUnstarFavorite(code) : () => OnStarFavorite(code)));
            }
            y += groupH + rowGap;
        }

        // A ghost-alpha copy of the small white font (Delete-mode dimming for static text).
        private static CairoFont Ghost()
        {
            CairoFont f = CairoFont.WhiteSmallText();
            f.Color = new double[] { 1, 1, 1, 0.22 };
            return f;
        }

        // A centred copy of a label font (0.1.23) — the section labels (Mode, Shape, Scale…) are centred
        // in their left column. Cloned so the caller's font (reused for other text) is untouched.
        private static CairoFont Centered(CairoFont f)
        {
            CairoFont g = f.Clone();
            g.Orientation = EnumTextOrientation.Center;
            return g;
        }

        // The current-shape chip swallows clicks: snap it straight back to lit (a status light).
        private void OnCurrentShapeChip(bool on)
        {
            if (_suppress || on) return;
            _suppress = true;
            try { SingleComposer?.GetToggleButton("curshape")?.SetValue(true); }
            finally { _suppress = false; }
        }

        // The ▾/▴ tile: folds the catalog open or shut. Not exclusive-row plumbing — clicking the lit
        // (open) tile genuinely closes it.
        private void OnExpandToggled(bool on)
        {
            if (_suppress) return;
            _shapeGridExpanded = on;
            DeferRecompose();
        }

        // A pick from the unfolded catalog: select it and leave the catalog OPEN (0.1.23, human-requested
        // — it only closes when the player clicks the ▾/▴ tile).
        private void OnCatalogShapeTile(string rowKey, string code)
        {
            OnShapeTile(rowKey, code);
        }

        // Right-click star (0.1.15, hard-kept): fill the FIRST FREE slot; a full list refuses with a
        // message — starring never evicts. Persisted IMMEDIATELY (B-24-2 fix, v0.1.24).
        private void OnStarFavorite(string code)
        {
            List<string> favs = _config.FavoriteShapes;
            if (favs.Contains(code)) return;
            if (favs.Count >= FavoriteSlots)
            {
                capi.TriggerIngameError(this, "layout-favoritesfull",
                    "All four shape slots are taken - Right-Click a pinned shape to unpin it first.");
                return;
            }
            favs.Add(code);
            _saveConfig?.Invoke();                              // don't wait for Dispose (may not fire)
            DeferRecompose();                                   // the slot re-draws with the new pin
        }

        // Right-click a starred shape (slot or catalog): unstar it, freeing its slot.
        private void OnUnstarFavorite(string code)
        {
            if (_config.FavoriteShapes.Remove(code)) { _saveConfig?.Invoke(); DeferRecompose(); }
        }

        // Toggle-tile plumbing: exclusive rows on top of independent toggle buttons.
        private void OnTileToggled(string rowKey, string code, string tileKey, bool on, Action<string, string> onTile)
        {
            if (_suppress) return;

            // Greyed row (Delete mode): swallow the click and restore whatever state the tile
            // had — the row is a read-only reminder of the defaults until Create is picked.
            if (_inertRows.Contains(rowKey))
            {
                _suppress = true;
                try { SingleComposer?.GetToggleButton(tileKey)?.SetValue(!on); }
                finally { _suppress = false; }
                return;
            }

            if (!on)
            {
                // Clicking the lit tile toggles it off visually — snap it back on (a row is
                // never empty; re-clicking the current choice is a no-op).
                _suppress = true;
                try { SingleComposer?.GetToggleButton(tileKey)?.SetValue(true); }
                finally { _suppress = false; }
                return;
            }

            onTile(rowKey, code);

            // Relight the row (turn the previous tile off, keep this one on). A recompose
            // does this honestly and also handles any row-set change the selection caused
            // (mode flips, a plane row appearing) in one motion.
            DeferRecompose();
        }

        // Lights each row's current selection immediately after composition (and seeds the divisions
        // fields' text — both under the suppress guard, since SetValue may fire change handlers).
        private void LightInitialTiles()
        {
            if (SingleComposer == null) return;
            _suppress = true;
            try
            {
                foreach ((string key, int sel) in _initialLight)
                    SingleComposer.GetToggleButton(key + ":" + sel)?.SetValue(true);

                // The shape picker's expand tile lights while the catalog is unfolded (not an exclusive row).
                SingleComposer.GetToggleButton("shapemore")?.SetValue(_shapeGridExpanded);

                // The current-shape chip is ALWAYS lit — it's a status light, not a control.
                SingleComposer.GetToggleButton("curshape")?.SetValue(true);

                // Move mode's free-move arm is a standalone toggle, not an exclusive row.
                SingleComposer.GetToggleButton("movefree")?.SetValue(_tool.FreeMove);

                foreach (string key in _litToggles)
                    SingleComposer.GetToggleButton(key)?.SetValue(true);

                // Configure the number inputs: whole numbers, ±1 per wheel notch / spinner click.
                foreach ((string fieldKey, _, _, _) in _divWheelFields)
                {
                    GuiElementNumberInput num = SingleComposer.GetNumberInput(fieldKey);
                    if (num != null) { num.IntMode = true; num.Interval = 1f; }
                }

                foreach ((string key, string text) in _pendingFieldText)
                    SingleComposer.GetTextInput(key)?.SetValue(text);
            }
            finally
            {
                _suppress = false;
                _initialLight.Clear();
                _litToggles.Clear();
                _pendingFieldText.Clear();
            }
        }

        // ---------------------------------------------------------------------------------
        //  Tile handlers — MAIN rows (always the tool's defaults for the next guide)
        // ---------------------------------------------------------------------------------
        private void OnModeTile(string rowKey, string code)
        {
            ToolMode newMode = ModeFromCode(code);
            if (newMode == _tool.Mode) return;

            // Switching tool mode discards any in-progress placement (both locally and on
            // the server, since a draft-start may already have been broadcast).
            if (_tool.HasActiveDraft)
            {
                _net.SendDraftCancel();
                _tool.ClearDraft();
            }

            _tool.SetMode(newMode);
            // Row set changes (greying flips, the guide section hides in Delete); the caller recomposes.
        }

        private void OnShapeTile(string rowKey, string code)
        {
            (GuideShapeType shape, ShapeConstraint constraint) = ShapeFromCode(code);
            _tool.SetShape(shape, constraint);
            // Live like every other draft setting: an in-progress draft's ghost re-shapes on
            // the next tick, and the completed guide uses the new pick.
        }

        private void OnScaleTile(string rowKey, string code)
        {
            if (int.TryParse(code, out int scale)) _tool.SetScale(scale);
        }

        private void OnProjectionTile(string rowKey, string code)
        {
            _tool.SetProjection(code == "surf" ? ProjectionMode.Surface : ProjectionMode.Volumetric);
            // The tool plane row appears/disappears with this toggle; the caller's recompose covers it.
        }

        private void OnPlaneTile(string rowKey, string code)
        {
            if (code == "auto") _tool.ClearPlaneOverride();
            else _tool.SetPlaneOverride(AxisFromCode(code));
        }

        private void OnFillTile(string rowKey, string code)
        {
            if (GuideShapeTypes.IsVolume(_tool.Shape))
                _tool.SetWireframe(code == "filled");
            else
                _tool.SetFilled(code == "filled");
        }

        // ---------------------------------------------------------------------------------
        //  Tile handlers — SELECTED-GUIDE section (sent to the server immediately)
        // ---------------------------------------------------------------------------------
        private void OnGuideScaleTile(string rowKey, string code)
        {
            GuideData g = ResolveSelectedGuide();
            if (g == null || !int.TryParse(code, out int scale)) return;
            if (g.VoxelScale != scale) _net.SendRescale(g.Id, scale);
        }

        private void OnGuideProjectionTile(string rowKey, string code)
        {
            GuideData g = ResolveSelectedGuide();
            if (g == null) return;
            bool toSurface = code == "surf";
            ProjectionMode mode = toSurface ? ProjectionMode.Surface : ProjectionMode.Volumetric;
            if (g.Projection == mode) return;

            ProjectionPlane plane = toSurface ? PlaneForGuideOnSwitch(g) : g.Plane;
            _net.SendSetProjection(g.Id, mode, plane);
        }

        private void OnGuidePlaneTile(string rowKey, string code)
        {
            GuideData g = ResolveSelectedGuide();
            if (g == null) return;
            PlaneAxis axis = AxisFromCode(code);
            _net.SendSetProjection(g.Id, ProjectionMode.Surface, PlaneFromAxisForGuide(axis, g));
        }

        private void OnGuideFillTile(string rowKey, string code)
        {
            GuideData g = ResolveSelectedGuide();
            if (g == null) return;
            bool secondOption = code == "filled";
            if (GuideShapeTypes.IsVolume(g.ShapeType))
            {
                if (g.IsWireframe != secondOption) _net.SendSetWireframe(g.Id, secondOption);
            }
            else if (g.IsFilled != secondOption)
            {
                _net.SendSetFilled(g.Id, secondOption);
            }
        }

        private void OnGuideVisibilityTile(string rowKey, string code)
        {
            GuideData g = ResolveSelectedGuide();
            if (g == null) return;
            bool hidden = code == "hidden";
            if (g.IsHidden != hidden) _net.SendHide(g.Id, hidden);
        }

        private bool OnDeselectClicked()
        {
            _tool.ClearSelection();
            DeferRecompose();       // the section folds away; main rows are untouched
            return true;
        }

        /// <summary>Refreshes the selected-guide section after an in-world Edit-mode deselect.</summary>
        public void RefreshSelection()
        {
            if (IsOpened()) DeferRecompose();
        }

        // ---------------------------------------------------------------------------------
        //  Network event handling (while the dialog is open)
        // ---------------------------------------------------------------------------------
        private void Subscribe()
        {
            if (_subscribed) return;
            _net.GuideAddedOrUpdated += OnGuideAddedOrUpdated;
            _net.LockStateChanged   += OnLockStateChanged;
            _subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!_subscribed) return;
            _net.GuideAddedOrUpdated -= OnGuideAddedOrUpdated;
            _net.LockStateChanged   -= OnLockStateChanged;
            _subscribed = false;
        }

        private void OnGuideAddedOrUpdated(GuideData g)
        {
            if (!IsOpened() || g == null) return;
            GuideData selected = ResolveSelectedGuide();
            if (selected == null || g.Id != selected.Id) return;

            // A remote peer (or the server) changed the selected guide. Re-light its
            // section's tiles in place under the suppress guard (no recompose, no flicker
            // under a remote drag); a row-set change defers a full recompose.
            RefreshSelectedGuideControls(g);
        }

        private void OnLockStateChanged(Guid guideId, string holder)
        {
            if (!IsOpened()) return;
            GuideData selected = ResolveSelectedGuide();
            if (selected != null && selected.Id == guideId) UpdateSelectedHeaderText(selected);
        }

        // Relights the Edit-mode setting rows in place when the selected guide changes remotely (no recompose,
        // no flicker under a remote drag). The rows are the SAME main keys as Create ("scale"/"proj"/...) —
        // only reached here in Edit mode via ResolveSelectedGuide (which is Edit-only).
        private void RefreshSelectedGuideControls(GuideData g)
        {
            if (SingleComposer == null || _tool.Mode != ToolMode.Edit) { DeferRecompose(); return; }

            bool surfaceNow = g.Projection == ProjectionMode.Surface;
            bool planeRowShown = SingleComposer.GetToggleButton("plane:0") != null;

            // A projection flip adds/removes the plane row — that's a row-set change; recompose instead.
            if (surfaceNow != planeRowShown)
            {
                DeferRecompose();
                return;
            }

            _suppress = true;
            try
            {
                RelightRow("scale", ScaleCodes, IndexOfScale(g.VoxelScale));
                RelightRow("proj", ProjCodes, surfaceNow ? 1 : 0);
                if (surfaceNow) RelightRow("plane", PlaneEditCodes, AxisToEditIndex(g.Plane.FlattenedAxis));
                RelightRow("fill", FillCodes,
                    GuideShapeTypes.IsVolume(g.ShapeType) ? (g.IsWireframe ? 1 : 0) : (g.IsFilled ? 1 : 0));
                SingleComposer?.GetTextInput("div:text")?.SetValue(g.Divisions > 0 ? g.Divisions.ToString() : "0");
                if (GuideShapeTypes.UsesSides(g.ShapeType))
                    SingleComposer?.GetTextInput("sides:text")?.SetValue(
                        Shapes.PolygonShape.ClampSides(g.Sides).ToString());
                RelightRow("vis", VisCodes, g.IsHidden ? 1 : 0);
            }
            finally
            {
                _suppress = false;
            }

            UpdateSelectedHeaderText(g);
        }

        private void RelightRow(string key, string[] codes, int selectedIndex)
        {
            int sel = ClampIndex(selectedIndex, codes.Length);
            for (int i = 0; i < codes.Length; i++)
                SingleComposer?.GetToggleButton(key + ":" + i)?.SetValue(i == sel);
        }

        // ---------------------------------------------------------------------------------
        //  Header text
        // ---------------------------------------------------------------------------------
        private string BuildHeaderText()
        {
            return _tool.Mode switch
            {
                // Create composes as TWO texts (left label + right-aligned shape name) in SetupDialog;
                // this left half only backstops any other caller.
                ToolMode.Create => "Create Mode",
                ToolMode.Edit => "Edit Mode - Select a guide to edit it.",
                ToolMode.Transform => "Transform Mode - Select a guide to change it.",
                ToolMode.Delete => "Delete Mode - Click a guide to remove it.",
                _ => "Layout tool"
            };
        }

        private void UpdateSelectedHeaderText(GuideData g)
        {
            SingleComposer?.GetDynamicText("gheader")?.SetNewText(BuildSelectedHeaderText(g));
        }

        private string BuildSelectedHeaderText(GuideData g)
        {
            string lockNote = _net.LockHolders.TryGetValue(g.Id, out string holder) && !string.IsNullOrEmpty(holder)
                ? "in use by " + holder
                : "editable";
            return "Selected: " + ShapeDisplayName(g.ShapeType, g.Constraint)
                + " " + ShortId(g.Id) + " - " + lockNote + ".";
        }

        // ---------------------------------------------------------------------------------
        //  Deferred recompose
        // ---------------------------------------------------------------------------------
        // Rebuilding the composer from inside a tile's own toggle callback is unsafe, so we
        // bounce it onto a short timer. The guard dedupes multiple triggers in one frame.
        private void DeferRecompose()
        {
            if (_recomposePending) return;
            _recomposePending = true;
            capi.Event.RegisterCallback(_ =>
            {
                _recomposePending = false;
                if (IsOpened()) SetupDialog();      // SetupDialog self-lights its tiles
            }, 30);
        }

        private void CancelPendingRecompose()
        {
            // The one-shot callback self-clears the flag; the IsOpened() guard inside it is
            // what makes a late fire a no-op after close.
            _recomposePending = false;
        }

        // ---------------------------------------------------------------------------------
        //  Plane helpers
        // ---------------------------------------------------------------------------------
        // When a guide is switched Volumetric->Surface from the menu: a guide that was EVER on a
        // configured plane keeps it stored through its Volumetric stint (the server preserves g.Plane),
        // so returning to Surface returns to THAT plane (Session-8 playtest fix — it used to reseed the
        // floor every time). Only a guide whose plane is still the untouched factory default (a guide
        // that has never been Surface) gets the seed: a horizontal plane at its first anchor's height,
        // adjustable from the plane row that then appears.
        private ProjectionPlane PlaneForGuideOnSwitch(GuideData g)
        {
            if (g.Projection == ProjectionMode.Surface) return g.Plane;
            if (!g.Plane.Equals(ProjectionPlane.Default)) return g.Plane;
            return PlaneFromAxisForGuide(PlaneAxis.Y, g);
        }

        // Builds a ProjectionPlane on the requested axis, offset to pass through the guide's
        // first real anchor on that axis. Offsets are in 1/16-block units (world coord * 16).
        private ProjectionPlane PlaneFromAxisForGuide(PlaneAxis axis, GuideData g)
        {
            Vec3d a = StartAnchorOf(g);
            switch (axis)
            {
                case PlaneAxis.X: return ProjectionPlane.VerticalEastWest((int)Math.Floor(a.X * 16.0));
                case PlaneAxis.Z: return ProjectionPlane.VerticalNorthSouth((int)Math.Floor(a.Z * 16.0));
                case PlaneAxis.Y:
                default:          return ProjectionPlane.Horizontal((int)Math.Floor(a.Y * 16.0));
            }
        }

        // The first real anchor: for the arch spine that's index 1 (index 0 is the leading
        // phantom); the ellipse family has no phantoms, so index 0 IS the anchor. Scan for
        // the first non-phantom rather than hard-coding either. Guarded so a malformed guide
        // can't throw inside a GUI callback.
        private static Vec3d StartAnchorOf(GuideData g)
        {
            var pts = g.ControlPoints;
            if (pts != null)
            {
                for (int i = 0; i < pts.Count; i++)
                    if (pts[i] != null && !pts[i].IsPhantom && pts[i].WorldPosition != null)
                        return pts[i].WorldPosition;
            }
            return new Vec3d(0, 0, 0);
        }

        // ---------------------------------------------------------------------------------
        //  Small code<->value mappers
        // ---------------------------------------------------------------------------------
        private static ToolMode ModeFromCode(string code) => code switch
        {
            "create" => ToolMode.Create,
            "edit"   => ToolMode.Edit,
            "transform" => ToolMode.Transform,
            "delete" => ToolMode.Delete,
            _        => ToolMode.Create
        };

        private static (GuideShapeType, ShapeConstraint) ShapeFromCode(string code) => code switch
        {
            "halfcircle"  => (GuideShapeType.Arch,      ShapeConstraint.SemiCircle),
            "circle"      => (GuideShapeType.Ellipse,   ShapeConstraint.Circle),
            "ellipse"     => (GuideShapeType.Ellipse,   ShapeConstraint.None),
            "line"        => (GuideShapeType.Line,      ShapeConstraint.None),
            "triangle"    => (GuideShapeType.Triangle,  ShapeConstraint.None),
            "righttri"    => (GuideShapeType.Triangle,  ShapeConstraint.Right),
            "equilateral" => (GuideShapeType.Triangle,  ShapeConstraint.Equilateral),
            "isosceles"   => (GuideShapeType.Triangle,  ShapeConstraint.Isosceles),
            "rectangle"   => (GuideShapeType.Rectangle, ShapeConstraint.None),
            "square"      => (GuideShapeType.Rectangle, ShapeConstraint.Square),
            "polygon"     => (GuideShapeType.Polygon,   ShapeConstraint.None),
            "freeshape"   => (GuideShapeType.FreeShape, ShapeConstraint.None),
            "sphere"      => (GuideShapeType.Sphere,    ShapeConstraint.None),
            "dome"        => (GuideShapeType.Dome,      ShapeConstraint.None),
            "cylinder"    => (GuideShapeType.Cylinder,  ShapeConstraint.None),
            "taperedcylinder" => (GuideShapeType.TaperedCylinder, ShapeConstraint.None),
            "polygonalprism" => (GuideShapeType.PolygonalPrism, ShapeConstraint.None),
            "taperedpolygonalprism" => (GuideShapeType.TaperedPolygonalPrism, ShapeConstraint.None),
            "cone"        => (GuideShapeType.Cone,      ShapeConstraint.None),
            "box"         => (GuideShapeType.Box,       ShapeConstraint.None),
            _             => (GuideShapeType.Arch,      ShapeConstraint.None)
        };

        private int CurrentShapeIndex() => ShapeIndexOf(_tool.Shape, _tool.Constraint);

        private static int ShapeIndexOf(GuideShapeType shape, ShapeConstraint constraint) => shape switch
        {
            GuideShapeType.Ellipse   => constraint == ShapeConstraint.Circle ? 2 : 3,
            GuideShapeType.Line      => 4,
            GuideShapeType.Triangle  => constraint switch
            {
                ShapeConstraint.Right       => 6,
                ShapeConstraint.Equilateral => 7,
                ShapeConstraint.Isosceles   => 8,
                _                           => 5
            },
            GuideShapeType.Rectangle => constraint == ShapeConstraint.Square ? 10 : 9,
            GuideShapeType.Polygon   => 11,
            GuideShapeType.FreeShape => 12,
            GuideShapeType.Sphere    => 13,
            GuideShapeType.Dome      => 14,
            GuideShapeType.Cylinder  => 15,
            GuideShapeType.TaperedCylinder => 16,
            GuideShapeType.PolygonalPrism => 17,
            GuideShapeType.TaperedPolygonalPrism => 18,
            GuideShapeType.Cone      => 19,
            GuideShapeType.Box       => 20,
            _ => constraint == ShapeConstraint.SemiCircle ? 1 : 0
        };

        internal static string ShapeDisplayName(GuideShapeType shape, ShapeConstraint constraint) =>
            ShapeNames[ShapeIndexOf(shape, constraint)];

        /// <summary>The always-yellow "-current" glyph name for a shape pick — the Current Shape chip's
        /// face, shared with the HUD's copy of the chip (0.1.16).</summary>
        internal static string CurrentShapeIconName(GuideShapeType shape, ShapeConstraint constraint) =>
            ShapeIcons[ShapeIndexOf(shape, constraint)] + LayoutToolIcons.CurrentSuffix;

        private static PlaneAxis AxisFromCode(string code) => code switch
        {
            "x" => PlaneAxis.X,
            "z" => PlaneAxis.Z,
            _   => PlaneAxis.Y
        };

        // Selected-guide plane tile order is [y, z, x].
        private static int AxisToEditIndex(PlaneAxis axis) => axis switch
        {
            PlaneAxis.Y => 0,
            PlaneAxis.Z => 1,
            PlaneAxis.X => 2,
            _           => 0
        };

        // Tool-defaults plane tile order is [auto, y, z, x]; null override == auto.
        private static int OverrideToToolIndex(PlaneAxis? axis)
        {
            if (axis == null) return 0;
            return axis.Value switch
            {
                PlaneAxis.Y => 1,
                PlaneAxis.Z => 2,
                PlaneAxis.X => 3,
                _           => 0
            };
        }

        private static int IndexOfScale(int scale)
        {
            for (int i = 0; i < ScaleCodes.Length; i++)
                if (ScaleCodes[i] == scale.ToString()) return i;
            return 0; // default to finest if a stored scale is somehow off-table
        }

        private static int ClampIndex(int index, int length)
            => index < 0 ? 0 : (index >= length ? length - 1 : index);

        private static string ShortId(Guid id) => id.ToString("N").Substring(0, 8);
    }
}
