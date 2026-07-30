using System;
using System.Collections.Generic;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Layout.Client;
using Layout.Config;
using Layout.Guide;
using Layout.Network;
using Layout.Systems;

namespace Layout.UI
{
    /// <summary>
    /// How far one wheel notch or spinner click moves each of the admin cap fields (v0.4.33, human-set).
    /// </summary>
    /// <remarks>
    /// ONE HOME FOR THE STEPS, because the same five limits are edited from two different panels: the
    /// server-wide values in the settings page's Admin section, and the per-player overrides in the Players
    /// dialog. Those had independent step numbers, so the same cap stepped by 1,000 in one place and by
    /// nothing in particular in the other. Reading both from here means adjusting a step adjusts it
    /// everywhere it appears.
    ///
    /// EACH STEP IS SCALED TO THE NUMBER IT EDITS. A single voxel is a null gesture on a six-figure cap, and
    /// a hundred guides is a null gesture on a limit whose realistic value is single digits — so the voxel
    /// caps step in thousands and the guide counts in ones and tens. Wider caps get coarser steps: the world
    /// total is the largest number here and moves fastest.
    ///
    /// THE WORLD-WIDE TWO HAVE NO PER-PLAYER EQUIVALENT and so appear only in the Admin section. A player
    /// carries a per-guide cap, a cumulative cap and a guide limit; there is no "voxels in the world"
    /// override for one person.
    /// </remarks>
    internal static class AdminCapSteps
    {
        /// <summary>Voxels per guide — also the Players dialog's per-guide cap field.</summary>
        public const float PerGuideVoxels = 5000f;

        /// <summary>Voxels per player — also the Players dialog's cumulative cap field.</summary>
        public const float PerPlayerVoxels = 25000f;

        /// <summary>Voxels in the world. Admin section only.</summary>
        public const float WorldVoxels = 100000f;

        /// <summary>Guides per player — also the Players dialog's guide-limit field.</summary>
        public const float PerPlayerGuides = 5f;

        /// <summary>Guides in the world. Admin section only.</summary>
        public const float WorldGuides = 100f;
    }

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
        // "ground" is the odd one out (v0.4.28): a direction with no step, since the distance is the drop
        // to the surface below rather than a multiple of anything — see SendGuideToGround.
        private static readonly string[] MoveArrowCodes =
            { "away", "toward", "left", "right", "up", "down", "ground" };
        private static readonly string[] MoveArrowIcons =
        {
            LayoutToolIcons.MoveAway, LayoutToolIcons.MoveToward, LayoutToolIcons.MoveLeft,
            LayoutToolIcons.MoveRight, LayoutToolIcons.MoveUp, LayoutToolIcons.MoveDown,
            LayoutToolIcons.MoveGround
        };
        private static readonly string[] MoveArrowNames =
        {
            "Away from you", "Toward you", "To your left", "To your right",
            "Up", "Down", "Send to the ground"
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

        // NOTE: the "Re-read the world" button was removed in v0.4.3 (human-directed). The rebuild it
        // triggered is still reachable through /layout occupancy refresh; only the button is gone.

        // Settings tab (v0.3.72): when true the panel shows client display settings instead of the tool
        // rows. Session-local like _shapeGridExpanded — the panel always opens on the tool.
        private bool _settingsTab;

        // A slider drag fires its handler per step, and each step would otherwise rebuild every guide
        // mesh. True while a rebuild is already queued behind the debounce timer.
        private bool _opacityApplyPending;

        // The personal rendering gate, mirrored here so composition can read it (v0.4.2). Pushed by the
        // controller rather than read from _config: ApplyGuideRendering notifies the controller BEFORE it
        // writes the config, so reading the config here would see the old value.
        private bool _renderingOff;

        // Parks a nudged guide on its wireframe for a beat (GuideRenderer.HoldMoveMaterialization) so a
        // string of arrow clicks doesn't restart an immense shell rebuild between every one of them.
        private readonly Action<Guid> _holdMoveMaterialization;

        // Turns this client's guide rendering on or off (LayoutModSystem.ApplyGuideRendering) — the same
        // setter /layout on|off uses, so the switch and the command can never disagree.
        private readonly Action<bool> _applyRendering;

        // How far the send-to-ground tile drops a guide (GuideToolController.GroundDropSixteenths).
        // NOT a constructor argument like the rest: the controller is built AFTER this dialog and takes it
        // as an argument, so it cannot also be one of ours. LayoutModSystem sets this the moment the
        // controller exists; a null one simply makes the tile a no-op rather than throwing.
        // (System-qualified: Vintagestory.API.Common declares its own Func<,>, as _applyOccupancy above
        // already has to work around.)
        private System.Func<GuideData, int> _groundDrop;

        /// <summary>Wires the send-to-ground contact rule, once the controller that owns it exists.</summary>
        public void SetGroundDropResolver(System.Func<GuideData, int> resolver) => _groundDrop = resolver;

        public GuideToolGui(ICoreClientAPI capi, DraftManager tool, ClientNetworkHandler net,
            LayoutClientConfig config, Action saveConfig, Action applyOpacities,
            System.Func<bool, bool> applyOccupancy,
            Action<Guid> holdMoveMaterialization, Action<bool> applyRendering,
            Action openPlayersDialog = null) : base(capi)
        {
            _tool = tool;
            _net = net;
            _config = config;
            _saveConfig = saveConfig;
            _applyOpacities = applyOpacities;
            _applyOccupancy = applyOccupancy;
            _holdMoveMaterialization = holdMoveMaterialization;
            _applyRendering = applyRendering;
            _openPlayersDialog = openPlayersDialog;
        }

        // Opening the Players dialog is delegated rather than done here: the mod system owns every
        // dialog's lifetime, and having one dialog construct another would leave two owners for it.
        private readonly Action _openPlayersDialog;

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
            _customColorsExpanded = false;   // ...and the color table starts folded away
            _adminExpanded = false;          // ...as does the Admin section
            _adminEdits.Clear();             // ...and nothing is left staged from a previous session
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
            // Guides hidden (v0.4.2): the tool genuinely does nothing, so every tool control composes
            // exactly as Delete mode ghosts them — same ghost fonts, same dead tiles. Reusing deleteMode
            // rather than threading a second flag through nine call sites is deliberate: every one of its
            // uses is already "ghost this control", so one is never accidentally left live.
            bool renderingOff = _renderingOff;
            bool deleteMode = _tool.Mode == ToolMode.Delete || renderingOff;
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
            // 18 -> 22 in v0.4.9, with the spoked gear glyph. Rendering the new icon at 18 px closed the
            // spoke gaps and filled in the bore, so it read as a plain toothed disc; 22 is the smallest size
            // at which the spokes and the hole survive, and the title bar (about 31 px tall) has the room.
            const double gearSize = 22;
            const double gearRightOffset = 84;   // nudged further from the stock icons in v0.4.10
            double gearX = contentW + 2 * GuiStyle.ElementToDialogPadding - gearRightOffset;
            // +2: centred on the title bar sat a touch high against the stock icons (human-directed, v0.4.10).
            double gearY = (GuiStyle.TitleBarHeight - gearSize) / 2.0 + 2;
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
            else if (renderingOff)
            {
                // Says what is true and where the way out is, because every other control on this page is
                // dead and the gear is the only thing that still responds.
                CairoFont offFont = CairoFont.WhiteSmallText();
                offFont.Color = new double[] { 1, 0.85, 0.55, 1 };
                c.AddDynamicText("Guides hidden", offFont, headerBounds, "header");
                y += headerH;
                c.AddStaticText("Turn them back on with the gear above.", CairoFont.WhiteDetailText(),
                    ElementBounds.Fixed(0, y, contentW, 18));
                y += 18 - headerH;
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

            // ---- Mode row (the way between modes — live unless guides are hidden, when there is no mode
            // worth being in and the only useful control on the dialog is the settings gear) ----
            double modeRowY = y;
            AddIconRow(c, renderingOff ? ghostFont : font, ref y, labelW, pad, tile, tileGap, rowGap,
                "Mode", ModeCodes, ModeNames, ModeIcons, (int)_tool.Mode, OnModeTile, "mode",
                inert: renderingOff);

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
                AddVisibilityRow(c, rowFont, font, ref y, labelW, pad, tile, tileGap, rowGap,
                    selected, settingsInert);

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
            // DOWN MOVED UP A ROW in v0.4.28 (human-directed) so the vertical column runs Up, Down, ground
            // without a gap, and the new send-to-ground tile takes the bottom slot. Up and Down being
            // adjacent also puts the pair that undoes each other side by side.
            AddMoveTile(c, detail, LayoutToolIcons.MoveDown, MoveArrowNames[5], "down",
                ElementBounds.Fixed(vcol, y, tile, tile), !inert);
            y += tile + tileGap;

            AddRotateTile(c, detail, LayoutToolIcons.RotateTipLeft,
                "Tip left\nTips the guide over toward your left.", "tipleft",
                ElementBounds.Fixed(x0, y, tile, tile), !inert);
            AddMoveTile(c, detail, LayoutToolIcons.MoveToward, MoveArrowNames[1], "toward",
                ElementBounds.Fixed(col1, y, tile, tile), !inert);
            AddRotateTile(c, detail, LayoutToolIcons.RotateTipRight,
                "Tip right\nTips the guide over toward your right.", "tipright",
                ElementBounds.Fixed(col2, y, tile, tile), !inert);
            AddMoveTile(c, detail, LayoutToolIcons.MoveGround,
                "Send to the ground\nDrops the guide straight down until it comes to rest on the ground "
                + "below it - the bottom of the guide meets the surface, the same contact CTRL uses during "
                + "a free-move. It settles on the HIGHEST ground under it, so nothing ends up buried. "
                + "Distance is worked out for you, so the step setting does not apply; Mirror does not "
                + "apply either. With Copy on, the copy is what lands.",
                "ground", ElementBounds.Fixed(vcol, y, tile, tile), !inert);
            y += tile + rowGap;
        }

        // ---------------------------------------------------------------------------------
        //  Momentary tiles — the ones that DO something rather than select something
        // ---------------------------------------------------------------------------------

        /// <summary>How long a momentary tile stays visibly pressed, in milliseconds.</summary>
        /// <remarks>
        /// Long enough to register as a press at a glance, short enough that a run of pad nudges does not
        /// feel like it is lagging behind the mouse. A click held longer than this simply keeps the tile
        /// down until it is released, since the release is timed from the press.
        /// </remarks>
        private const int MomentaryPressMs = 120;

        /// <summary>When each momentary tile was last pressed, so a stale release cannot cut a newer one short.</summary>
        private readonly Dictionary<string, long> _momentaryPressedAt = new Dictionary<string, long>();

        /// <summary>
        /// Drives one press of a momentary tile: light it, run its action, and let it back up a beat later.
        /// </summary>
        /// <remarks>
        /// THESE TILES USED TO GIVE NO FEEDBACK AT ALL (fixed v0.4.28, human-reported). Every one of them
        /// forced itself back off in the very same call that ran its action, so the lit state existed for
        /// less than a frame and was never drawn. Clicking a move arrow, a rotate corner or a Reveal tile
        /// looked identical to clicking dead panel background — the guide moved, but the button never
        /// acknowledged the click.
        ///
        /// EVERY CLICK IS A PRESS, whichever way the underlying toggle happened to flip. That is what the
        /// unconditional SetValue(true) is for: while a tile is still lit from the previous click, the next
        /// click flips it OFF, and a handler that only acted on the ON edge would swallow every second
        /// click of a fast run of nudges. The old code got away with ignoring the OFF edge only because it
        /// reset the tile synchronously, so the OFF edge never arrived from a user click.
        ///
        /// The release is timestamped rather than cancelled: a callback that finds a NEWER press simply
        /// stands down and lets that press own the release. Cheaper than tracking and cancelling timers,
        /// and it cannot leave a tile stuck down, because the newer press has always scheduled its own.
        /// </remarks>
        private void PressMomentaryTile(string tileKey, Action action)
        {
            _momentaryPressedAt[tileKey] = capi.World.ElapsedMilliseconds;

            _suppress = true;
            try { SingleComposer?.GetToggleButton(tileKey)?.SetValue(true); }
            finally { _suppress = false; }

            capi.Event.RegisterCallback(_ => ReleaseMomentaryTile(tileKey), MomentaryPressMs);
            action();
        }

        private void ReleaseMomentaryTile(string tileKey)
        {
            if (!IsOpened()) return;

            // A later press re-armed this tile; its own release is already scheduled.
            if (_momentaryPressedAt.TryGetValue(tileKey, out long at)
                && capi.World.ElapsedMilliseconds - at < MomentaryPressMs) return;

            _suppress = true;
            try { SingleComposer?.GetToggleButton(tileKey)?.SetValue(false); }
            finally { _suppress = false; }
        }

        /// <summary>Builds a momentary icon tile — one that acts on click and does not stay selected.</summary>
        private void AddMomentaryTile(
            GuiComposer c, CairoFont hoverFont, string icon, string hover, string tileKey,
            ElementBounds bounds, bool enabled, Action action)
        {
            var btn = new GuiElementToggleButton(
                capi, enabled ? icon : icon + LayoutToolIcons.GhostSuffix, "",
                CairoFont.WhiteSmallText(),
                _ =>
                {
                    if (_suppress || !enabled) return;
                    PressMomentaryTile(tileKey, action);
                },
                bounds, toggleable: true)
            { Enabled = enabled };
            c.AddInteractiveElement(btn, tileKey);
            c.AddAutoSizeHoverText(hover, hoverFont, 260, bounds.FlatCopy(), tileKey + ":ht");
        }

        // A momentary pad tile, so the pad can be clicked repeatedly without a recompose between nudges
        // (an arrow is an action, not a selection).
        private void AddMoveTile(
            GuiComposer c, CairoFont hoverFont, string icon, string hover, string code,
            ElementBounds bounds, bool enabled)
        {
            AddMomentaryTile(c, hoverFont, icon, hover, "movearrow:" + code, bounds, enabled,
                () => OnMoveArrowTile(code));
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
            AddMomentaryTile(c, hoverFont, icon, hover, "moverotate:" + code, bounds, enabled,
                () => OnRotateTile(code));
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
                + "voxel scale. Left-click places it; right-click puts it back where it started. "
                + "Hold CTRL to set it down on the surface you are aiming at - the bottom of the guide "
                + "meets that face.",
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

            if (code == "ground") { SendGuideToGround(g); return; }

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

        /// <summary>
        /// Send to ground: one downward move whose distance is the drop to the surface below, rather than
        /// a multiple of the step. The contact rule itself is the controller's — see
        /// <c>GuideToolController.GroundDropSixteenths</c>.
        /// </summary>
        /// <remarks>
        /// STEP AND MIRROR BOTH SIT THIS OUT, deliberately, and the hover text says so. Step, because the
        /// distance is not a step — it is however far the ground happens to be. Mirror, because a mirrored
        /// guide has a DIFFERENT underside: the drop is solved before the flip, so composing the two would
        /// land a dome its own height out of place. Rather than solve the contact twice, or land it wrong,
        /// this tile is simply not a mirror tile. FLAGGED for review — the pad is otherwise uniformly
        /// state-driven, and this is the one arrow that ignores a lit toggle.
        ///
        /// COPY DOES apply, and is worth having: a copy is a duplicate of unchanged geometry, so the drop
        /// solved for the original is exactly right for it, and "leave this one here, put another on the
        /// ground below" is the obvious thing to want.
        ///
        /// A silent no-op when it is already resting, when nothing is under it within reach, or when
        /// neither Move nor Copy is lit — matching the other arrows, which also do nothing at all when the
        /// action row leaves them nothing to do.
        /// </remarks>
        private void SendGuideToGround(GuideData g)
        {
            if (_tool.Placement == DraftManager.TransformPlacement.None) return;

            int drop = _groundDrop?.Invoke(g) ?? 0;
            if (drop >= 0) return;

            // A run of ground drops is not a run: the second press lands on the same surface as the first.
            _tool.ResetCopyRun();
            _holdMoveMaterialization?.Invoke(g.Id);
            _net.SendTransform(
                g.Id, 0, drop, 0, -1, PlaneAxis.Y, 0,
                _tool.Placement == DraftManager.TransformPlacement.Copy);
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
        // GROUPED IN TWO SECTIONS (v0.4.2). APPEARANCE is what guides look like; BEHAVIOUR is what the mod
        // does. The split is not decoration — the page tripled in length this revision, and a flat stack of
        // eight unrelated controls gave no clue that "Show built voxels" and "Private guides" are different
        // kinds of thing. Every row is now label-left / control-right on ONE line (the old two-line
        // label-above-control form was what made the page tall), and every row carries hover text.
        //
        // The prose that used to sit under each control was removed in v0.3.87 for overflowing the dialog;
        // hover text was the agreed replacement (TODO T2) and is what the rows now carry.
        private void BuildSettingsSection(
            GuiComposer c, CairoFont font, ref double y, double contentW, double rowGap)
        {
            // ==================== Master switch (above every section) ====================
            // The mod's own on/off sits ABOVE the section headers because it is not one setting among
            // others — it gates all of them. Its label states the CURRENT state in the state's own colour,
            // so the row reads as a status light you can also press.
            bool layoutOn = !_renderingOff;
            CairoFont masterFont = CairoFont.WhiteSmallText();
            masterFont.Color = layoutOn
                ? new double[] { 0.35, 0.90, 0.40, 1 }      // green — guides are drawing
                : new double[] { 0.95, 0.35, 0.30, 1 };     // red — guides are hidden
            AddSettingSwitch(c, masterFont, ref y, contentW, rowGap,
                layoutOn ? "Layout: On" : "Layout: Off", "showguides", OnShowGuidesToggled,
                "Turns Layout on or off for you. Off hides every guide and disables the Chalking Kit "
                + "without deleting anything — the same as /layout off. Other players are unaffected.");

            // ==================== Appearance ====================
            AddSettingsHeader(c, ref y, contentW, "Appearance");

            // The one control that is not a switch. The slider is shortened to leave its Reset beside it —
            // a reset belongs next to the thing it resets, not stranded in a button row at the foot of a
            // page that also holds four settings it does not touch.
            const double resetW = 76;
            double sliderW = contentW - resetW - 8;

            c.AddStaticText("Guide opacity", font, ElementBounds.Fixed(0, y, contentW, 20));
            c.AddAutoSizeHoverText(
                "How solid guides look. Fainter guides make it easier to see the material inside them "
                + "while you chisel; solid guides are easier to read at a distance.",
                CairoFont.WhiteDetailText(), 260, ElementBounds.Fixed(0, y, contentW, 20), "ht:opacity");
            y += 20 + 2;

            c.AddSlider(OnOpacityBodySlider, ElementBounds.Fixed(0, y, sliderW, 22), "opacitybody");
            c.AddSmallButton("Reset", OnResetOpacity,
                ElementBounds.Fixed(contentW - resetW, y, resetW, 22));
            y += 22 + rowGap;

            // ---- Color scheme (F11, v0.4.8) ----
            // A short list of PRESETS plus Custom, not a color control per role. The presets answer the
            // accessibility problem — locked and apex points are red and green, the pair deuteranopia and
            // protanopia collapse — and color-blind-safe palettes are a solved design problem. Custom answers
            // the other, equally real reason: yellow disappears against sandstone.
            bool custom = GuidePalette.Normalize(_config.ColorScheme) == GuidePaletteScheme.Custom;

            // The dropdown sits left of the far edge to leave the collapse chevron the right-hand slot every
            // other control on this page uses (human-directed, v0.4.14).
            const double schemeW = 150, collapseW = 24, collapseGap = 6;
            double schemeX = contentW - schemeW - collapseW - collapseGap;

            c.AddStaticText("Colors", font, ElementBounds.Fixed(0, y + 4, schemeX - 8, 20));
            c.AddDropDown(
                ColorSchemeCodes, GuidePalette.SchemeNames,
                Math.Max(0, Array.IndexOf(ColorSchemeCodes, _config.ColorScheme.ToString())),
                OnColorSchemeSelected, ElementBounds.Fixed(schemeX, y, schemeW, 24), "colorscheme");
            c.AddAutoSizeHoverText(
                "Which colors guides are drawn in. Red-Green Safe separates locked points from apex points "
                + "for red-green color blindness, which the default palette cannot tell apart. Custom lets "
                + "you set each role yourself. Changing this redraws every guide.",
                CairoFont.WhiteDetailText(), 260,
                ElementBounds.Fixed(0, y, schemeX + schemeW, 24), "ht:colorscheme");

            // The chevron only exists when there is something to collapse.
            if (custom)
            {
                ElementBounds chevron = ElementBounds.Fixed(contentW - collapseW, y + 3, 18, 18);
                c.AddInteractiveElement(
                    new BareIconElement(capi,
                        _customColorsExpanded ? LayoutToolIcons.ExpandUp : LayoutToolIcons.ExpandDown,
                        OnToggleCustomColors, chevron),
                    "colorexpand");
                c.AddAutoSizeHoverText(
                    _customColorsExpanded ? "Hide the color table" : "Show the color table",
                    CairoFont.WhiteDetailText(), 200, chevron.FlatCopy(), "ht:colorexpand");
            }
            y += 24 + rowGap;

            if (custom && _customColorsExpanded)
                BuildCustomColorPicker(c, font, ref y, contentW, rowGap);

            // ---- Built-voxel colouring (v0.3.79) ----
            AddSettingSwitch(c, font, ref y, contentW, rowGap,
                "Chiseling Highlight", "occupancy", OnOccupancyToggled,
                "Draws the parts of a guide that already hold material in a different color, so you can "
                + "see how much of the plan is built. Updates as you chisel. Local to you.");

            // ==================== Behaviour ====================
            AddSettingsHeader(c, ref y, contentW, "Behaviour");

            // Public/Private is a CHOICE BETWEEN TWO NAMED THINGS, not something you switch on, so it gets
            // a two-sided slider rather than a switch: both options are always named, and the slider covers
            // whichever one is not in effect. Colours come straight from the guide anchors the two modes
            // actually draw — blue for public, orange for private — so the control teaches the world's
            // colour language rather than inventing a second one.
            const double toggleW = 150, toggleH = 24;

            // The server's policy goes directly ABOVE the control it governs — right-aligned, so it sits over
            // the toggle rather than off on the label side. Its bounds still span the full width (the text is
            // drawn right-aligned inside them), which keeps the longest wording from ever clipping.
            // Green/red matches the master switch at the top of the page.
            c.AddInteractiveElement(
                new AlertTextElement(capi, ElementBounds.Fixed(0, y, contentW, 18),
                    DescribeServerPrivacyPolicy(), CairoFont.WhiteDetailText(), ServerPolicyColor()),
                "serverprivacy");
            y += 18 + 2;

            ElementBounds toggleBounds = ElementBounds.Fixed(contentW - toggleW, y, toggleW, toggleH);
            c.AddStaticText("New guides", font, ElementBounds.Fixed(0, y + 4, contentW - toggleW - 8, 20));
            c.AddInteractiveElement(
                new SlidingChoiceElement(
                    capi, toggleBounds,
                    "Public", PublicTint, "Private", PrivateTint,
                    _config.ForceClientOnly, OnGuidePrivacyChoice),
                "clientonly");
            c.AddAutoSizeHoverText(
                "Where new guides go. Public guides are stored on the server and everyone can see them; "
                + "private guides are stored on your own machine and only you can see them. A server that "
                + "denies private guides will bounce the slider back to Public.",
                CairoFont.WhiteDetailText(), 260,
                ElementBounds.Fixed(0, y, contentW, toggleH), "ht:clientonly");
            y += toggleH + 4;

            // Just "Publish" (human-directed): the hover text carries the explanation, and a button label is
            // not the place to spell out a three-step operation.
            const double publishW = 90;
            ElementBounds publishBounds = ElementBounds.Fixed(contentW - publishW, y, publishW, 22);
            c.AddSmallButton("Publish", OnPublishPrivateGuides, publishBounds);
            c.AddAutoSizeHoverText(
                "Publishes every private guide you are holding to the server, where everyone can see them. "
                + "Publishing requires being public, so this switches you to Public first — the slider "
                + "above moves with it. Your private copies are removed as the server accepts them.",
                CairoFont.WhiteDetailText(), 260, publishBounds.FlatCopy(), "ht:publish");
            y += 22 + rowGap;

            AddSettingSwitch(c, font, ref y, contentW, rowGap,
                "Refill from hotbar", "refillhotbar", OnHotbarRefillToggled,
                "Lets you refill a Chalking Kit by right-clicking with Chalking Powder in hand. Off by "
                + "default so the powder is not spent by accident. Setting a kit on the ground and "
                + "Shift+right-clicking it always works regardless of this.");

            AddSettingSwitch(c, font, ref y, contentW, rowGap,
                "Refill in inventory", "refillinv", OnInventoryRefillToggled,
                "Lets you refill a Chalking Kit by dropping a held stack of Chalking Powder onto its "
                + "inventory slot. Off by default. The ground refill always works regardless of this.");

            // ==================== Admin ====================
            BuildAdminSection(c, font, ref y, contentW, rowGap);

            // The running build, stated plainly at the foot of the page (v0.4.23). Vintage Story loads one
            // mod per modid, so several Layout zips left in the Mods folder means the version that runs is
            // not necessarily the newest one sitting there. Without this the only way to find out was the
            // log, and a fix that was never actually running looks exactly like a fix that does not work.
            y += 6;
            CairoFont versionFont = CairoFont.WhiteDetailText();
            versionFont.Color = new double[] { 0.62, 0.62, 0.62, 1 };
            c.AddStaticText("Layout v" + LayoutModSystem.ModVersion, versionFont,
                ElementBounds.Fixed(0, y, contentW, 16));
            y += 16;
        }

        // ---------------------------------------------------------------------------------
        //  Admin section (v0.4.16)
        // ---------------------------------------------------------------------------------
        // The settings page was deliberately client-only until now — server settings lived on their admin
        // commands and were kept off a player-facing panel. The human reopened that: these are the settings
        // an admin actually reaches for, and a form reads far better than remembering five command names.
        //
        // WHAT IS AND IS NOT HERE. This section holds the server-WIDE settings, the ones that are a fixed
        // short list with no argument but their own value. The per-PLAYER overrides (/layout jail, free,
        // limit, voxelcap, totalvoxelcap) stay on the command line, because every one of them needs a
        // player named first and a panel has nowhere good to put a player picker. The commands are not
        // deprecated; both routes change the same state.
        //
        // COLLAPSED BY DEFAULT and reset on every arrival at the page, exactly like the colour table: it is
        // eight rows that most players will never open, sitting under the four they came for.
        private void BuildAdminSection(
            GuiComposer c, CairoFont font, ref double y, double contentW, double rowGap)
        {
            // Nothing to administer when no Layout server has told us anything — a pre-0.4.16 server, a
            // vanilla server, or single-player client-only. Drawing an empty Admin heading in those worlds
            // would advertise a section that can never open.
            LayoutAdminConfigPacket cfg = _net.AdminConfig;
            if (cfg == null || !_net.CanEditAdminConfig) return;
            _shownAdminConfig = cfg;   // the baseline OnAdminConfigChanged compares later packets against

            // The heading carries the chevron, in the same right-hand slot the colour table's chevron uses.
            y += 8;
            const double collapseW = 24;
            CairoFont headerFont = CairoFont.WhiteSmallText();
            headerFont.Color = new double[] { 1, 0.85, 0.55, 1 };
            c.AddStaticText("Admin - Server Settings", headerFont,
                ElementBounds.Fixed(0, y, contentW - collapseW, 20));

            ElementBounds chevron = ElementBounds.Fixed(contentW - collapseW, y + 1, 18, 18);
            c.AddInteractiveElement(
                new BareIconElement(capi,
                    _adminExpanded ? LayoutToolIcons.ExpandUp : LayoutToolIcons.ExpandDown,
                    OnToggleAdmin, chevron),
                "adminexpand");
            c.AddAutoSizeHoverText(
                _adminExpanded ? "Hide the server settings" : "Show the server settings",
                CairoFont.WhiteDetailText(), 220, chevron.FlatCopy(), "ht:adminexpand");
            y += 20;

            c.AddInset(ElementBounds.Fixed(0, y, contentW, 1), 1, 0.4f);
            y += 1 + 6;

            if (!_adminExpanded) return;

            // The heading itself now says "Server Settings" (v0.4.18), so the separate scope note that used
            // to sit here was saying the same thing twice.
            //
            // THE PERSONAL-OVERRIDE WARNING WAS REMOVED IN v0.4.28 (human-directed). It earned its keep
            // once — a forgotten 10,000,000-voxel override was what made the caps look broken across six
            // revisions — but the human does not want it on the page. The override itself is untouched:
            // LayoutAdminConfigPacket still carries the per-player fields (registration is append-only),
            // the Players dialog's Overrides tab still shows them, and /layout info <player> still reports
            // them. Only this line is gone.

            const string unlimited = "Set 0 for unlimited.";

            // The wheel/spinner step is per row and lives in AdminCapSteps, which the Players dialog's
            // per-player fields read from too — see the remarks there for why each is the size it is.
            AddAdminNumber(c, font, ref y, contentW, rowGap,
                "Voxels per guide", LayoutAdminSetting.PerGuideVoxelCap, cfg.PerGuideVoxelCap,
                AdminCapSteps.PerGuideVoxels,
                "The largest a single guide may be. A player's draft stops growing when it reaches this, "
                + "and the gauge on the heads-up display fills against it. " + unlimited);

            AddAdminNumber(c, font, ref y, contentW, rowGap,
                "Voxels per player", LayoutAdminSetting.PerPlayerTotalVoxelCap, cfg.PerPlayerTotalVoxelCap,
                AdminCapSteps.PerPlayerVoxels,
                "The total across every guide one player has created and still has standing. Deleting a "
                + "guide gives the room back. " + unlimited);

            AddAdminNumber(c, font, ref y, contentW, rowGap,
                "Voxels in the world", LayoutAdminSetting.TotalVoxelCap, cfg.TotalVoxelCap,
                AdminCapSteps.WorldVoxels,
                "The total across every guide from every player on the server. " + unlimited);

            AddAdminNumber(c, font, ref y, contentW, rowGap,
                "Guides per player", LayoutAdminSetting.MaxGuidesPerPlayer, cfg.MaxGuidesPerPlayer,
                AdminCapSteps.PerPlayerGuides,
                "How many guides one player may have standing at once. " + unlimited);

            AddAdminNumber(c, font, ref y, contentW, rowGap,
                "Guides in the world", LayoutAdminSetting.MaxGuidesWorldWide, cfg.MaxGuidesWorldWide,
                AdminCapSteps.WorldGuides,
                "How many guides may exist on the server at once. " + unlimited);

            AddAdminSwitch(c, font, ref y, contentW, rowGap,
                "Allow private guides", "adminprivate", LayoutAdminSetting.AllowClientOnlyMode,
                cfg.AllowClientOnlyMode,
                "Lets players keep guides on their own machine where nobody else can see them. Turning "
                + "this off puts everyone back to public guides for anything NEW - the private guides they "
                + "already have are not touched or taken.");

            // The dirty marker. Without it there is no way to tell a typed-but-unsaved value from a saved
            // one, since both simply sit in the field looking identical.
            //
            // ON ITS OWN LINE since v0.4.28, because Players moved to the left edge. It used to occupy the
            // space left of both buttons; with a button now at each end of the row the gap between them is
            // about 110 px, which is what "2 unsaved changes" needs at detail size — close enough that any
            // future rewording would clip. A full-width line above the row cannot clip at any wording, and
            // it reads more like the warning it is. Costs its 18 px only while there is something unsaved.
            if (_adminEdits.Count > 0)
            {
                CairoFont dirtyFont = CairoFont.WhiteDetailText();
                dirtyFont.Color = new double[] { 1, 0.72, 0.30, 1 };
                c.AddStaticText(
                    _adminEdits.Count == 1 ? "1 unsaved change" : _adminEdits.Count + " unsaved changes",
                    dirtyFont, ElementBounds.Fixed(0, y, contentW, 18));
                y += 18 + 2;
            }

            // ---- Players + Save ----
            // Nothing above this line has reached the server yet; see the staging remarks on _adminEdits.
            const double saveW = 90, playersW = 90;
            ElementBounds saveBounds = ElementBounds.Fixed(contentW - saveW, y, saveW, 24);
            ElementBounds playersBounds = ElementBounds.Fixed(0, y, playersW, 24);

            // Players sits at the row's LEFT EDGE (human-directed, v0.4.28 — it was immediately left of
            // Save until then). The two buttons are not a pair: Save commits the staged edits above it and
            // belongs beside them at the right, while Players opens a separate dialog and changes nothing,
            // so it is deliberately outside the staged-edit flow and now reads that way as well.
            c.AddSmallButton("Players", OnOpenPlayersDialog, playersBounds);
            c.AddAutoSizeHoverText(
                "Opens the player list: who Layout knows about, what limits they carry, and who is jailed. "
                + "A player's own limits can be changed there, and jailed or freed.",
                CairoFont.WhiteDetailText(), 260, playersBounds.FlatCopy(), "ht:adminplayers");

            c.AddSmallButton("Save", OnSaveAdminSettings, saveBounds);
            c.AddAutoSizeHoverText(
                "Applies the settings above to the server and writes them to its config file. Nothing "
                + "above takes effect until this is pressed.",
                CairoFont.WhiteDetailText(), 260, saveBounds.FlatCopy(), "ht:adminsave");

            y += 24 + rowGap;
        }

        // (DescribeOverrides lived here until v0.4.28. It existed only for the personal-override warning
        // removed above; GuidePlayersDialog has its own, which is still in use.)

        // An admin number row: label left, number field right. Editing STAGES — see _adminEdits.
        private void AddAdminNumber(
            GuiComposer c, CairoFont font, ref double y, double contentW, double rowGap,
            string label, LayoutAdminSetting setting, int serverValue, float interval, string hoverText)
        {
            const double fieldW = 110, fieldH = 26;
            string key = "admin" + (int)setting;

            ElementBounds rowBounds = ElementBounds.Fixed(0, y, contentW, fieldH);
            c.AddStaticText(label, font,
                ElementBounds.Fixed(0, y + 4, contentW - fieldW - 8, 20));
            c.AddNumberInput(
                ElementBounds.Fixed(contentW - fieldW, y, fieldW, fieldH),
                text => OnAdminNumberTyped(text, setting, serverValue), font, key);
            c.AddAutoSizeHoverText(hoverText, CairoFont.WhiteDetailText(), 260, rowBounds, "ht:" + key);

            // A staged edit survives a recompose, so the field redraws with what the admin typed rather
            // than snapping back to the server's value under them.
            _pendingAdminText.Add((key, StagedOr(setting, serverValue).ToString(), interval));
            y += fieldH + rowGap;
        }

        private void AddAdminSwitch(
            GuiComposer c, CairoFont font, ref double y, double contentW, double rowGap,
            string label, string key, LayoutAdminSetting setting, bool serverValue, string hoverText)
        {
            AddSettingSwitch(c, font, ref y, contentW, rowGap, label, key,
                on => { if (!_suppress) StageAdminEdit(setting, on ? 1 : 0, serverValue ? 1 : 0); },
                hoverText);
            _pendingAdminSwitches.Add((key, StagedOr(setting, serverValue ? 1 : 0) != 0));
        }

        // Whether the Admin section is showing. Session state, not config, and reset on every arrival at
        // the settings page — same reasoning as the colour table.
        private bool _adminExpanded;

        // Post-compose value pushes for the admin widgets, drained by ApplySettingsWidgetValues.
        private readonly List<(string key, string text, float interval)> _pendingAdminText =
            new List<(string, string, float)>();
        private readonly List<(string key, bool on)> _pendingAdminSwitches =
            new List<(string, bool)>();

        /// <summary>
        /// Admin settings the player has changed on screen but not yet saved. Empty means the panel is
        /// showing exactly what the server holds.
        /// </summary>
        /// <remarks>
        /// EDITS STAGE, THEY DO NOT APPLY (v0.4.20, human-directed). The previous design sent each change
        /// on a timer as it was typed, which was wrong twice over. It was wrong for the SERVER — a cap
        /// typed digit by digit arrives as a series of nonsense values, each one briefly real and each one
        /// clamping every player's draft preview. And it was wrong for the ADMIN, who had no way to tell a
        /// value that had been applied from one that merely sat in a field; when a change failed to reach
        /// the server there was nothing on screen that said so.
        ///
        /// Staging fixes both: nothing leaves the client until Save, the whole set goes at once, and the
        /// panel says how many changes are waiting. After Save the server broadcasts what it actually
        /// stored, so the fields end up showing the SERVER's values — which is also how a rejected or
        /// clamped value becomes visible instead of silently disagreeing.
        ///
        /// An entry whose value matches the server's is REMOVED rather than kept, so typing a number and
        /// then typing it back does not leave the panel claiming an unsaved change it no longer has.
        /// </remarks>
        private readonly Dictionary<LayoutAdminSetting, int> _adminEdits =
            new Dictionary<LayoutAdminSetting, int>();

        private int StagedOr(LayoutAdminSetting setting, int serverValue) =>
            _adminEdits.TryGetValue(setting, out int staged) ? staged : serverValue;

        private void StageAdminEdit(LayoutAdminSetting setting, int value, int serverValue)
        {
            bool wasDirty = _adminEdits.Count > 0;
            if (value == serverValue) _adminEdits.Remove(setting);
            else _adminEdits[setting] = value;
            // Only redraw when the unsaved-changes line has to appear or disappear: a recompose per
            // keystroke would drop focus out of the field being typed in.
            if (wasDirty != (_adminEdits.Count > 0)) DeferRecompose();
        }

        private void OnToggleAdmin()
        {
            _adminExpanded = !_adminExpanded;
            // Folding the section away discards anything unsaved rather than keeping it invisibly pending.
            if (!_adminExpanded) _adminEdits.Clear();
            DeferRecompose();
        }

        private void OnAdminNumberTyped(string text, LayoutAdminSetting setting, int serverValue)
        {
            if (_suppress) return;
            // An empty field is someone midway through clearing it, not a request for unlimited. They have
            // to type the 0 — an accidental select-all-delete must not silently uncap the server.
            if (string.IsNullOrWhiteSpace(text)) return;
            if (!int.TryParse(text.Trim(), out int value)) return;   // ignore partial/garbled input

            // HARD FLOOR AT ZERO. The native spinner and wheel have no minimum of their own, so stepping
            // down from 0 arrives here as -1000 and the field would happily keep counting downward. Zero
            // already MEANS unlimited, so everything below it is a second spelling of the same thing —
            // snap the display back rather than let it show a number the server will never store.
            if (value < 0)
            {
                value = 0;
                _suppress = true;
                try { SingleComposer?.GetNumberInput("admin" + (int)setting)?.SetValue("0"); }
                finally { _suppress = false; }
            }

            StageAdminEdit(setting, value, serverValue);
        }

        /// <summary>Opens the Players dialog. Supplied by the mod system, which owns the dialog's lifetime.</summary>
        private bool OnOpenPlayersDialog()
        {
            _openPlayersDialog?.Invoke();
            return true;
        }

        /// <summary>Sends every staged admin edit. The server validates, applies, and broadcasts back.</summary>
        private bool OnSaveAdminSettings()
        {
            if (_adminEdits.Count == 0)
            {
                capi.TriggerIngameError(this, "layout-nothingtosave", "No changes to save.");
                return true;
            }

            // Said out loud, on purpose. The server answers every setting it applies with its own line, so
            // the pair of messages tells you exactly where a change got to: this line and then the
            // server's means it landed; this line alone means the request never arrived. Without it, a
            // change that failed to reach the server was indistinguishable from one that did, which is
            // what made the v0.4.16-v0.4.19 delivery bugs so hard to pin down.
            capi.ShowChatMessage("[Layout] Sending " + _adminEdits.Count
                + (_adminEdits.Count == 1 ? " setting change" : " setting changes") + " to the server...");
            capi.Logger.Notification("[Layout] Admin save: sending {0} change(s); server available={1}.",
                _adminEdits.Count, _net.ServerLayoutAvailable);

            if (!_net.ServerLayoutAvailable)
            {
                capi.ShowChatMessage(
                    "[Layout] ...but this world has no Layout server to apply them, so nothing changed.");
                _adminEdits.Clear();
                DeferRecompose();
                return true;
            }

            foreach (var pair in _adminEdits) _net.SendAdminConfig(pair.Key, pair.Value);
            _adminEdits.Clear();

            // The fields now show the server's answer, whatever it turns out to be. Recomposing here also
            // clears the unsaved-changes line immediately rather than waiting on the round trip — and it
            // means a field that snaps back to its old number is itself telling you the change was lost.
            DeferRecompose();
            return true;
        }

        /// <summary>
        /// The server's settings arrived or changed. Redraws the page so the Admin section appears for an
        /// admin who has just been granted the privilege, and so a change made by ANOTHER admin — or a value
        /// the server clamped — replaces what is on screen rather than leaving a stale number in the field.
        /// </summary>
        /// <remarks>
        /// ONLY WHEN SOMETHING ACTUALLY DIFFERS. The server broadcasts after every change, including the
        /// admin's own, and a recompose rebuilds every element and so drops keyboard focus. Redrawing on
        /// the echo of your own edit would therefore kick you out of the field on each value you set —
        /// worse when setting two caps in a row, because the first echo lands while you are typing the
        /// second. The usual case (the server took the value verbatim) compares equal and draws nothing.
        /// </remarks>
        public void OnAdminConfigChanged()
        {
            if (!IsOpened() || !_settingsTab) return;

            LayoutAdminConfigPacket now = _net.AdminConfig;
            if (SameAdminConfig(_shownAdminConfig, now)) return;
            _shownAdminConfig = now;
            DeferRecompose();
        }

        // What the Admin section is currently DRAWING, so the echo of an accepted change can be told from
        // a real one. Held by value, not by reference: the handler replaces the packet instance every time.
        private LayoutAdminConfigPacket _shownAdminConfig;

        private static bool SameAdminConfig(LayoutAdminConfigPacket a, LayoutAdminConfigPacket b)
        {
            if (a == null || b == null) return ReferenceEquals(a, b);
            return a.PerGuideVoxelCap == b.PerGuideVoxelCap
                && a.PerPlayerTotalVoxelCap == b.PerPlayerTotalVoxelCap
                && a.TotalVoxelCap == b.TotalVoxelCap
                && a.MaxGuidesPerPlayer == b.MaxGuidesPerPlayer
                && a.MaxGuidesWorldWide == b.MaxGuidesWorldWide
                && a.AllowClientOnlyMode == b.AllowClientOnlyMode
                && a.YourVoxelCapOverride == b.YourVoxelCapOverride
                && a.YourTotalVoxelCapOverride == b.YourTotalVoxelCapOverride
                && a.YourGuideLimitOverride == b.YourGuideLimitOverride
                // EnableChalkDurability and AdminCanOverrideLocks are deliberately absent: the panel
                // stopped drawing them (v0.4.17 and v0.4.20), so a change to either — only possible via
                // layout.json plus a restart — has nothing on screen to redraw.
                && a.CanEdit == b.CanEdit;
        }

        // The two guide-anchor hues, tinted for use as a background behind white text. The RGB is the
        // anchor palette from GuideMeshBuilder verbatim (blue 0.20/0.50/1.00 public, orange 1.00/0.45/0.05
        // private); only the alpha is ours. At full strength both fight the white label and read as alert
        // colours rather than as a setting, so they sit at just under half.
        // Dropdown values are the pinned GuidePaletteScheme numbers as strings, so the control's selection
        // and the config key are the same thing and cannot drift. Taken from SchemeValues rather than
        // written out here, because 2 is a retired number and the sequence has a hole in it.
        private static readonly string[] ColorSchemeCodes =
            Array.ConvertAll(GuidePalette.SchemeValues, v => v.ToString());

        private static readonly double[] PublicTint  = { 0.20, 0.50, 1.00, 0.45 };
        private static readonly double[] PrivateTint = { 1.00, 0.45, 0.05, 0.45 };

        /// <summary>
        /// The Custom scheme's editor (v0.4.12, tabulated v0.4.13): a captioned table of role chips, then a
        /// grid of swatches. Pick the role you want to change, then the colour to give it.
        /// </summary>
        /// <remarks>
        /// ONE GRID SHARED BY EVERY ROLE, rather than a colour control per role. Seven rows each carrying
        /// their own picker would have added roughly 170 px to a page that is already long, and would have
        /// made comparing two roles' colours impossible — they would never be on screen beside each other at
        /// the same size. The role table IS the comparison: seven samples together, which is exactly the view
        /// you need when deciding whether locked and apex are far enough apart.
        ///
        /// TWO GRIDS OF COLOURED SQUARES ON ONE PAGE need telling apart, which is why the roles are captioned
        /// and the swatches carry a heading naming the role they will apply to. Without that, the only
        /// difference between "choose what to change" and "choose what to change it to" was position.
        /// </remarks>
        private void BuildCustomColorPicker(
            GuiComposer c, CairoFont font, ref double y, double contentW, double rowGap)
        {
            float[][] roles = CurrentCustomRoles();
            const double cell = 22, gap = 4;

            // ---- role table: four columns, two rows, each chip captioned ----
            // A bare line of seven colour squares was unreadable — nothing on screen said which square was
            // which, so choosing a colour meant hovering each one in turn to find the role you wanted. The
            // caption under every chip is the fix (human-directed). Four columns puts the four roles you
            // touch most on the first row and leaves the labels room not to collide.
            const int roleCols = 4;
            const double labelH = 14, rolePitch = cell + 2 + labelH + 6;
            double colW = contentW / roleCols;
            CairoFont capFont = Centered(CairoFont.WhiteDetailText());
            CairoFont capSelFont = Centered(CairoFont.WhiteDetailText());
            capSelFont.Color = new double[] { 1, 0.85, 0.55, 1 };   // the selected role, in the page accent

            for (int i = 0; i < GuidePalette.RoleCount; i++)
            {
                int role = i;
                double colX = (i % roleCols) * colW;
                double rowY = y + (i / roleCols) * rolePitch;

                ElementBounds chip = ElementBounds.Fixed(colX + (colW - cell) / 2, rowY, cell, cell);
                c.AddInteractiveElement(
                    new ColorCellElement(capi, chip, roles[i], _customRole == i,
                        () => OnCustomRolePicked(role)),
                    "role" + i);
                c.AddStaticText(GuidePalette.RoleShortNames[i], _customRole == i ? capSelFont : capFont,
                    ElementBounds.Fixed(colX, rowY + cell + 2, colW, labelH));
                // Hover covers the whole cell, caption included, so the full role name is reachable from
                // whichever half of it the cursor happens to be over.
                c.AddAutoSizeHoverText(GuidePalette.RoleNames[i], CairoFont.WhiteDetailText(), 220,
                    ElementBounds.Fixed(colX, rowY, colW, cell + 2 + labelH), "ht:role" + i);
            }
            y += 2 * rolePitch + rowGap;

            // ---- swatch grid: eight across, two down ----
            // Left-aligned, unlike the captions: this is a heading for the grid, not a cell label.
            CairoFont swatchHeadFont = CairoFont.WhiteDetailText();
            swatchHeadFont.Color = new double[] { 1, 0.85, 0.55, 1 };
            c.AddStaticText("Color for " + GuidePalette.RoleShortNames[_customRole], swatchHeadFont,
                ElementBounds.Fixed(0, y, contentW, labelH));
            y += labelH + 3;

            const int perRow = 8;
            for (int i = 0; i < GuidePalette.Swatches.Length; i++)
            {
                int swatch = i;
                double sx = (i % perRow) * (cell + gap);
                double sy = y + (i / perRow) * (cell + gap);
                c.AddInteractiveElement(
                    new ColorCellElement(capi, ElementBounds.Fixed(sx, sy, cell, cell),
                        GuidePalette.Swatches[i], false, () => OnCustomSwatchPicked(swatch)),
                    "swatch" + i);
            }
            y += 2 * cell + gap + rowGap;

            c.AddSmallButton("Reset colors", OnResetCustomColors,
                ElementBounds.Fixed(0, y, 120, 22));
            c.AddAutoSizeHoverText(
                "Puts every role back to the Default palette's color.",
                CairoFont.WhiteDetailText(), 240, ElementBounds.Fixed(0, y, 120, 22), "ht:resetcolors");
            y += 22 + rowGap;
        }

        // The custom roles as they stand, seeded from Default for anything never chosen. Reading through
        // the palette rather than the raw config keeps the chips showing exactly what the guides show.
        private float[][] CurrentCustomRoles()
        {
            float[][] stored = GuidePalette.ParseRoleColors(_config.CustomColors);
            float[][] seed = GuidePalette.RoleColorsOf(GuidePaletteScheme.Default);
            var roles = new float[GuidePalette.RoleCount][];
            for (int i = 0; i < GuidePalette.RoleCount; i++)
                roles[i] = stored != null && stored[i] != null ? stored[i] : seed[i];
            return roles;
        }

        // A section heading: warm-tinted text over a full-width divider line, with breathing room above it.
        private void AddSettingsHeader(GuiComposer c, ref double y, double contentW, string title)
        {
            y += 8;

            CairoFont headerFont = CairoFont.WhiteSmallText();
            headerFont.Color = new double[] { 1, 0.85, 0.55, 1 };   // the dialogs' warm accent
            c.AddStaticText(title, headerFont, ElementBounds.Fixed(0, y, contentW, 20));
            y += 20;

            // A 1 px inset reads as a rule at this size and costs no new element type.
            c.AddInset(ElementBounds.Fixed(0, y, contentW, 1), 1, 0.4f);
            y += 1 + 6;
        }

        // One settings row: label on the left, switch hard right, hover text over the WHOLE row so the
        // explanation is reachable from the label as well as the control. Post-compose value push lives in
        // ApplySettingsWidgetValues — nothing here sets a switch's state.
        private void AddSettingSwitch(
            GuiComposer c, CairoFont font, ref double y, double contentW, double rowGap,
            string label, string key, Action<bool> onToggled, string hoverText)
        {
            const double switchSize = 22;

            ElementBounds rowBounds = ElementBounds.Fixed(0, y, contentW, switchSize);
            c.AddStaticText(label, font,
                ElementBounds.Fixed(0, y + 3, contentW - switchSize - 8, 20));
            c.AddSwitch(onToggled,
                ElementBounds.Fixed(contentW - switchSize, y, switchSize, switchSize), key, switchSize);
            c.AddAutoSizeHoverText(hoverText, CairoFont.WhiteDetailText(), 260, rowBounds, "ht:" + key);

            y += switchSize + rowGap;
        }

        // The server's POLICY on private guides — not what the player currently prefers. ServerLayoutAvailable
        // is checked first because the no-server fallback also reports client-only as "allowed", and calling
        // that "Private Allowed" would imply a server had granted something when there is no server at all.
        private string DescribeServerPrivacyPolicy()
        {
            // No Layout server: nothing has been "allowed" because nothing was asked. The player is in the
            // client-only fallback, where private is not a preference but the only thing on offer.
            if (!_net.ServerLayoutAvailable) return "Client-Only: Always Private";
            return _net.ServerAllowsClientOnlyMode
                ? "Server: Private Allowed"
                : "Server: Private Denied";
        }

        // Red for the one case that takes an option away; green for both cases that leave the player free.
        private double[] ServerPolicyColor() =>
            _net.ServerLayoutAvailable && !_net.ServerAllowsClientOnlyMode
                ? new double[] { 0.95, 0.35, 0.30, 1 }
                : new double[] { 0.35, 0.90, 0.40, 1 };

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
                SingleComposer.GetSwitch("showguides")?.SetValue(_config.GuideRenderingEnabled);
                SingleComposer.GetSwitch("occupancy")?.SetValue(_config.OccupancyRecolour);
                // "clientonly" is deliberately absent: SlidingChoiceElement takes its state through its
                // constructor and then owns it, so pushing a value here would fight its animation.
                SingleComposer.GetSwitch("refillhotbar")?.SetValue(_config.AllowHotbarChalkRefill);
                SingleComposer.GetSwitch("refillinv")?.SetValue(_config.AllowInventoryChalkRefill);

                // Admin section (v0.4.16). Whole numbers, with the wheel/spinner step carried per row —
                // see AddAdminNumber's call sites for why each field gets the step it does.
                foreach ((string key, string text, float interval) in _pendingAdminText)
                {
                    GuiElementNumberInput num = SingleComposer.GetNumberInput(key);
                    if (num == null) continue;
                    num.IntMode = true;
                    num.Interval = interval;
                    num.SetValue(text);
                }
                foreach ((string key, bool on) in _pendingAdminSwitches)
                    SingleComposer.GetSwitch(key)?.SetValue(on);
            }
            catch (Exception e) { capi.Logger.Warning("[Layout] settings widgets: {0}", e.Message); }
            finally
            {
                _suppress = false;
                // Cleared in the finally, not after the loops: a throw partway through must not leave
                // stale entries to be re-applied against the NEXT compose's elements.
                _pendingAdminText.Clear();
                _pendingAdminSwitches.Clear();
            }
        }

        private static int OpacityPercent(float alpha) =>
            Math.Min(100, Math.Max(5, (int)Math.Round(alpha * 100f)));

        /// <summary>
        /// The personal rendering gate changed. Before v0.4.2 the controller CLOSED this dialog when guides
        /// were hidden; now the dialog stays open with its tool page composed inert, because the switch that
        /// turns guides back on lives on its settings page — closing it made that switch a one-way trip.
        /// </summary>
        public void SetRenderingEnabled(bool enabled)
        {
            if (_renderingOff == !enabled) return;
            _renderingOff = !enabled;
            if (IsOpened()) DeferRecompose();
        }

        // The title-bar gear: a plain switch between the tool page and the settings page.
        private void ToggleSettingsTab()
        {
            _settingsTab = !_settingsTab;
            // Every arrival at the settings page starts with the color table and the Admin section folded,
            // not just the first. Leaving the page drops anything staged but unsaved, so a value typed and
            // abandoned cannot be saved by accident on some later visit.
            if (_settingsTab) { _customColorsExpanded = false; _adminExpanded = false; }
            _adminEdits.Clear();
            DeferRecompose();      // never recompose from inside a composer callback
        }

        private bool OnOpacityBodySlider(int value)
        {
            if (_suppress) return true;
            _config.OpacityBody = value / 100f;
            QueueOpacityApply();
            return true;
        }

        // Colours are baked into vertex data exactly as opacity is, so a scheme change rebuilds every guide.
        // It reuses the opacity debounce for that rebuild — one deliberate pick cannot thrash it the way a
        // slider drag can, but there is no reason to have two paths doing the same job.
        private void OnColorSchemeSelected(string code, bool selected)
        {
            if (_suppress || !selected) return;
            if (!int.TryParse(code, out int scheme)) return;

            GuidePaletteScheme picked = GuidePalette.Normalize(scheme);
            GuidePaletteScheme previous = GuidePalette.Normalize(_config.ColorScheme);

            // SWITCHING TO CUSTOM SEEDS IT from whatever preset was showing, and only the first time. The
            // player is looking at a palette when they reach for Custom; landing on that same palette ready
            // to adjust is what they expect, and it means "Custom" never opens on a blank slate. Seeding
            // again on a later revisit would silently throw away colours they had already chosen.
            if (picked == GuidePaletteScheme.Custom && previous != GuidePaletteScheme.Custom
                && _config.CustomColors == null)
            {
                _config.CustomColors = GuidePalette.FormatRoleColors(GuidePalette.RoleColorsOf(previous));
            }

            // Choosing Custom opens the table: it is the one moment the player has actively asked to look at
            // their colors. Choosing anything else shuts it, so switching away does not leave a table behind
            // for a scheme that no longer uses it.
            _customColorsExpanded = picked == GuidePaletteScheme.Custom;

            _config.ColorScheme = (int)picked;
            QueueOpacityApply();
            DeferRecompose();      // the picker appears or disappears with the scheme
        }

        // Which role the swatch grid is currently assigning to. Session state, not config — it is a cursor
        // in the editor, not a setting, and it always opens on the guide body.
        private int _customRole = GuidePalette.RoleBody;

        // Whether the Custom color table is showing. Deliberately NOT persisted and deliberately reset every
        // time the settings page is opened (human-directed, v0.4.14): the table is a third of the page's
        // height, and someone who set their colors weeks ago should not have to scroll past it to reach the
        // settings they actually came for. It opens once, at the moment Custom is chosen, because that is
        // the one time the player definitely wants to see it.
        private bool _customColorsExpanded;

        private void OnToggleCustomColors()
        {
            _customColorsExpanded = !_customColorsExpanded;
            DeferRecompose();
        }

        private void OnCustomRolePicked(int role)
        {
            _customRole = role;
            DeferRecompose();      // moves the selection ring
        }

        private void OnCustomSwatchPicked(int swatch)
        {
            float[][] roles = CurrentCustomRoles();
            roles[_customRole] = GuidePalette.Swatches[swatch];
            _config.CustomColors = GuidePalette.FormatRoleColors(roles);
            QueueOpacityApply();   // rebuilds every guide, debounced like the opacity slider
            DeferRecompose();      // repaints the role chip in its new colour
        }

        private bool OnResetCustomColors()
        {
            _config.CustomColors = null;   // null means "unset", which Build fills from Default per role
            QueueOpacityApply();
            DeferRecompose();
            return true;
        }

        // Rebuilds every guide, so it runs straight away rather than through the opacity debounce — this is
        // a deliberate click, not a drag, and coalescing would only delay the feedback.
        private void OnOccupancyToggled(bool on)
        {
            if (_suppress) return;
            _applyOccupancy?.Invoke(on);
        }

        // The master visibility switch — the same setter as /layout on|off, which persists it itself.
        private void OnShowGuidesToggled(bool on)
        {
            if (_suppress) return;
            _applyRendering?.Invoke(on);
        }

        // Public/Private. The network handler saves the preference (through the ModSystem's
        // preference-changed hook) and asks the server to switch, so nothing is written here.
        //
        // NOTE THE ABSENT RECOMPOSE. Rebuilding the dialog would rebuild the slider at its new resting
        // position and the slide would never be seen. The one thing that has to change on screen — the
        // "In force:" line — is dynamic text, updated in place instead. The server's answer is a round trip
        // away, so RefreshGuidePrivacyState is also called from AuthorityModeChanged when it lands.
        private void OnGuidePrivacyChoice(bool wantPrivate)
        {
            if (_suppress) return;

            // SERVER DENIES PRIVATE: let the slider travel to Private anyway, then send it back
            // (human-directed). A control that refuses to move looks broken, and the player never gets to
            // see that "Private" is the thing behind it. Moving and returning says both "this is the option"
            // and "you cannot have it here" in one gesture. The preference is deliberately NOT touched — a
            // bounce is a demonstration, not a setting.
            if (wantPrivate && _net.ServerLayoutAvailable && !_net.ServerAllowsClientOnlyMode)
            {
                capi.Event.RegisterCallback(_ =>
                {
                    SetPrivacyToggle(false);
                    // The policy line shakes and flashes AS the slider starts back, so the eye is pulled to
                    // the reason at the moment the refusal happens rather than having to go looking for it.
                    AlertServerPolicy();
                }, PrivacyBounceMs);
                return;
            }

            _net.SetClientOnlyPreference(wantPrivate);
        }

        // Long enough for the slide to finish and land (it eases in roughly 200 ms) before it starts back,
        // so the eye reads two deliberate movements rather than one twitch.
        private const int PrivacyBounceMs = 500;

        // Drives the slider from code, animating exactly as a click does. Used by the bounce-back and by the
        // publish button, which switches the player to Public as a side effect.
        private void SetPrivacyToggle(bool wantPrivate)
        {
            if (!IsOpened() || !_settingsTab || SingleComposer == null) return;
            try
            {
                if (SingleComposer.GetElement("clientonly") is SlidingChoiceElement toggle)
                    toggle.SetChoice(wantPrivate);
            }
            catch (Exception e) { capi.Logger.Warning("[Layout] privacy toggle: {0}", e.Message); }
        }

        // Both guard on IsOpened(): these are reached from a network event and a timer, either of which can
        // land after the dialog closed and its elements' textures were disposed. Rebuilding a texture on a
        // disposed element is exactly the class of mistake that crashed v0.4.4.
        private void RefreshServerPrivacyPolicy()
        {
            if (!IsOpened() || SingleComposer == null) return;
            if (SingleComposer.GetElement("serverprivacy") is AlertTextElement line)
                line.SetText(DescribeServerPrivacyPolicy(), ServerPolicyColor());
        }

        private void AlertServerPolicy()
        {
            if (!IsOpened() || SingleComposer == null) return;
            if (SingleComposer.GetElement("serverprivacy") is AlertTextElement line) line.Alert();
        }

        // "Publish Private Guides": the settings-page equivalent of /layout client push all, minus the
        // requirement to have run /layout public first. The network handler owns the ordering (go public,
        // wait for the server to confirm, then upload); all this does is move the slider to match, since the
        // player's mode really is changing and the control must not be left telling them otherwise.
        private bool OnPublishPrivateGuides()
        {
            _net.PublishPrivateGuides();
            // AFTER, not before: a publish that cannot proceed (no Layout server, nothing private to send)
            // leaves the preference alone, and moving the slider first would have shown a switch to Public
            // that never happened — visible until the next recompose snapped it back.
            SetPrivacyToggle(_config.ForceClientOnly);
            return true;
        }

        // The two chalk-refill shortcuts are read live at the moment of a refill attempt
        // (LayoutModSystem.HotbarChalkRefillAllowed), so writing the config IS the whole change.
        private void OnHotbarRefillToggled(bool on)
        {
            if (_suppress) return;
            _config.AllowHotbarChalkRefill = on;
            _saveConfig?.Invoke();
        }

        private void OnInventoryRefillToggled(bool on)
        {
            if (_suppress) return;
            _config.AllowInventoryChalkRefill = on;
            _saveConfig?.Invoke();
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

        /// <summary>
        /// A two-sided sliding choice (v0.4.3). Both options are always named and always coloured; an opaque
        /// slider covers the one NOT in effect, so the visible half is the answer and the covered half is the
        /// alternative you would get by clicking it.
        /// </summary>
        /// <remarks>
        /// Why not a switch. A switch says on/off, and neither "public" nor "private" is the off state of the
        /// other — a player reading an unlabelled switch cannot tell which way is which. This names both
        /// permanently.
        ///
        /// The track is drawn once into the composed surface; only the slider is drawn per frame, from its
        /// own small texture, at an interpolated x. That is what makes the movement free: no recompose and no
        /// Cairo work per frame. It also means the OWNER MUST NOT RECOMPOSE THE DIALOG ON CLICK — a recompose
        /// rebuilds this element at its new resting position and the slide never happens.
        /// </remarks>
        private sealed class SlidingChoiceElement : GuiElement
        {
            private readonly ICoreClientAPI _capi;
            private readonly string _leftText, _rightText;
            private readonly double[] _leftColor, _rightColor;
            private readonly Action<bool> _onChanged;

            private LoadedTexture _sliderTex;
            private bool _rightChosen;
            private float _anim;                    // 0 = slider over the LEFT half, 1 = over the RIGHT

            private const double Radius = 4;

            public SlidingChoiceElement(
                ICoreClientAPI capi, ElementBounds bounds,
                string leftText, double[] leftColor, string rightText, double[] rightColor,
                bool rightChosen, Action<bool> onChanged) : base(capi, bounds)
            {
                _capi = capi;
                _leftText = leftText;
                _rightText = rightText;
                _leftColor = leftColor;
                _rightColor = rightColor;
                _rightChosen = rightChosen;
                _onChanged = onChanged;
                _anim = Target;                     // opens at rest; only a click animates
            }

            // The slider covers the option NOT chosen: choose the right one and it parks over the left.
            private float Target => _rightChosen ? 0f : 1f;

            public override void ComposeElements(Context ctx, ImageSurface surface)
            {
                Bounds.CalcWorldBounds();
                double x = Bounds.drawX, yy = Bounds.drawY;
                double w = Bounds.InnerWidth, h = Bounds.InnerHeight, half = w / 2;

                // Track: the two tinted halves, clipped to one rounded outline so the seam between them is
                // straight but the outer corners are not.
                ctx.Save();
                RoundRect(ctx, x, yy, w, h, Radius);
                ctx.Clip();

                ctx.SetSourceRGBA(_leftColor[0], _leftColor[1], _leftColor[2], _leftColor[3]);
                ctx.Rectangle(x, yy, half, h);
                ctx.Fill();
                ctx.SetSourceRGBA(_rightColor[0], _rightColor[1], _rightColor[2], _rightColor[3]);
                ctx.Rectangle(x + half, yy, half, h);
                ctx.Fill();
                ctx.Restore();

                CenteredText(ctx, _leftText, x, yy, half, h);
                CenteredText(ctx, _rightText, x + half, yy, half, h);

                // Outline last so it sits over both fills.
                ctx.SetSourceRGBA(1, 1, 1, 0.35);
                ctx.LineWidth = 1;
                RoundRect(ctx, x + 0.5, yy + 0.5, w - 1, h - 1, Radius);
                ctx.Stroke();

                BuildSliderTexture(half, h);
            }

            // The slider: a flat dark cover, deliberately opaque enough to hide the tint underneath it
            // completely — a half-visible "Private" behind the slider would read as the active choice.
            private void BuildSliderTexture(double w, double h)
            {
                int pw = (int)Math.Round(w), ph = (int)Math.Round(h);
                if (pw <= 0 || ph <= 0) return;

                var surf = new ImageSurface(Format.Argb32, pw, ph);
                var sctx = new Context(surf);

                RoundRect(sctx, 0.5, 0.5, pw - 1, ph - 1, Radius);
                sctx.SetSourceRGBA(0.14, 0.13, 0.12, 0.97);
                sctx.FillPreserve();
                sctx.SetSourceRGBA(1, 1, 1, 0.30);
                sctx.LineWidth = 1;
                sctx.Stroke();

                // Three grip lines — the conventional "this part moves" cue, and the only thing that
                // distinguishes the slider from a plain dark panel once it is at rest.
                sctx.SetSourceRGBA(1, 1, 1, 0.28);
                double cx = pw / 2.0, gh = ph * 0.34;
                for (int i = -1; i <= 1; i++)
                {
                    sctx.Rectangle(cx + i * 4 - 0.5, (ph - gh) / 2, 1, gh);
                    sctx.Fill();
                }

                // The ref parameter must already point at a LoadedTexture: the platform layer writes into
                // the instance rather than creating one, so handing it a null reference throws inside the
                // engine (NRE in ClientPlatformWindows.LoadOrUpdateCairoTexture, v0.4.4 crash).
                if (_sliderTex == null) _sliderTex = new LoadedTexture(_capi);
                _capi.Gui.LoadOrUpdateCairoTexture(surf, true, ref _sliderTex);
                sctx.Dispose();
                surf.Dispose();
            }

            public override void RenderInteractiveElements(float deltaTime)
            {
                if (_sliderTex == null || _sliderTex.TextureId == 0) return;

                // Exponential ease toward the target: framerate-independent, no tween bookkeeping, and it
                // settles in about a fifth of a second at any framerate.
                float step = Math.Min(1f, deltaTime * 14f);
                _anim += (Target - _anim) * step;
                if (Math.Abs(Target - _anim) < 0.001f) _anim = Target;

                double half = Bounds.InnerWidth / 2;
                _capi.Render.Render2DTexturePremultipliedAlpha(
                    _sliderTex.TextureId,
                    Bounds.renderX + _anim * half, Bounds.renderY,
                    half, Bounds.InnerHeight);
            }

            public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
            {
                if (args.Button != EnumMouseButton.Left) { base.OnMouseDownOnElement(api, args); return; }
                args.Handled = true;
            }

            public override void OnMouseUpOnElement(ICoreClientAPI api, MouseEvent args)
            {
                if (args.Button != EnumMouseButton.Left) { base.OnMouseUpOnElement(api, args); return; }
                args.Handled = true;

                // ANY click flips the control (v0.4.18, human-directed). It previously picked the half you
                // clicked, which meant only the COVERED half — the option not currently in force — ever did
                // anything, and clicking the lit half read as a dead control rather than as "already set".
                // A two-state control that flips wherever you hit it is the safer read: there is no
                // inactive region to discover.
                _rightChosen = !_rightChosen;
                _onChanged?.Invoke(_rightChosen);
            }

            /// <summary>
            /// Moves the control from code, animating exactly as a click does. Used for the denied-private
            /// bounce and by "Publish Private Guides", which switches the player to Public underneath it.
            /// </summary>
            public void SetChoice(bool rightChosen) => _rightChosen = rightChosen;

            public override void Dispose()
            {
                base.Dispose();
                _sliderTex?.Dispose();
            }

            private static void CenteredText(Context ctx, string text, double x, double y, double w, double h)
            {
                CairoFont f = CairoFont.WhiteSmallText();
                f.SetupContext(ctx);
                TextExtents te = ctx.TextExtents(text);
                FontExtents fe = ctx.FontExtents;
                ctx.SetSourceRGBA(1, 1, 1, 0.95);
                ctx.MoveTo(x + (w - te.Width) / 2 - te.XBearing,
                           y + (h - fe.Height) / 2 + fe.Ascent);
                ctx.ShowText(text);
            }

            private static void RoundRect(Context ctx, double x, double y, double w, double h, double r)
            {
                ctx.NewPath();
                ctx.Arc(x + w - r, y + r, r, -Math.PI / 2, 0);
                ctx.Arc(x + w - r, y + h - r, r, 0, Math.PI / 2);
                ctx.Arc(x + r, y + h - r, r, Math.PI / 2, Math.PI);
                ctx.Arc(x + r, y + r, r, Math.PI, 1.5 * Math.PI);
                ctx.ClosePath();
            }
        }

        /// <summary>
        /// A right-aligned line of text that can be told to SHAKE AND FLASH (v0.4.7). Used for the server's
        /// private-guide policy, which has to be able to answer a refused click loudly enough to be noticed
        /// without a chat message.
        /// </summary>
        /// <remarks>
        /// A stock dynamic text cannot do this: it draws itself at its own bounds with its own colour, and
        /// nothing outside it can offset or tint that. So this keeps the same trick the sliding toggle uses —
        /// bake the text into a texture once and move it per frame — and bakes a SECOND, brighter copy for
        /// the flash, because the 2D texture draw has no colour multiplier to modulate.
        /// </remarks>
        private sealed class AlertTextElement : GuiElement
        {
            private readonly ICoreClientAPI _capi;
            private readonly CairoFont _font;

            private LoadedTexture _tex, _hotTex;
            private string _text;
            private double[] _color;
            private float _alert;                       // seconds of alert remaining

            private const float AlertSeconds = 0.55f;
            private const float ShakeAmplitude = 5f;    // pixels either side at full strength
            private const float ShakeRate = 26f;        // radians/second — about four shakes over the alert

            public AlertTextElement(ICoreClientAPI capi, ElementBounds bounds,
                string text, CairoFont font, double[] color) : base(capi, bounds)
            {
                _capi = capi;
                _font = font;
                _text = text;
                _color = color;
            }

            public override void ComposeElements(Context ctx, ImageSurface surface)
            {
                Bounds.CalcWorldBounds();
                Rebuild();
            }

            public void SetText(string text, double[] color)
            {
                if (_text == text && ReferenceEquals(_color, color)) return;
                _text = text;
                _color = color;
                Rebuild();
            }

            public void Alert() => _alert = AlertSeconds;

            private void Rebuild()
            {
                BuildOne(_color, ref _tex);
                // The flash copy: near-white, so it reads as a flash against either the red or the green.
                BuildOne(new double[] { 1, 1, 1, 1 }, ref _hotTex);
            }

            private void BuildOne(double[] color, ref LoadedTexture tex)
            {
                int w = (int)Math.Round(Bounds.InnerWidth), h = (int)Math.Round(Bounds.InnerHeight);
                if (w <= 0 || h <= 0) return;

                var surf = new ImageSurface(Format.Argb32, w, h);
                var sctx = new Context(surf);
                _font.SetupContext(sctx);

                TextExtents te = sctx.TextExtents(_text);
                FontExtents fe = sctx.FontExtents;
                sctx.SetSourceRGBA(color[0], color[1], color[2], color[3]);
                sctx.MoveTo(w - te.Width - te.XBearing, (h - fe.Height) / 2 + fe.Ascent);
                sctx.ShowText(_text);

                if (tex == null) tex = new LoadedTexture(_capi);
                _capi.Gui.LoadOrUpdateCairoTexture(surf, true, ref tex);
                sctx.Dispose();
                surf.Dispose();
            }

            public override void RenderInteractiveElements(float deltaTime)
            {
                LoadedTexture draw = _tex;
                double dx = 0;

                if (_alert > 0)
                {
                    _alert = Math.Max(0, _alert - deltaTime);
                    float elapsed = AlertSeconds - _alert;
                    float decay = _alert / AlertSeconds;              // shake dies away rather than stopping
                    dx = Math.Sin(elapsed * ShakeRate) * ShakeAmplitude * decay;
                    // Flash on the first half of each full swing, so the brightening reads as part of the
                    // same motion instead of a separate blink.
                    if (_hotTex != null && _hotTex.TextureId != 0
                        && Math.Sin(elapsed * ShakeRate * 0.5f) > 0) draw = _hotTex;
                }

                if (draw == null || draw.TextureId == 0) return;
                _capi.Render.Render2DTexturePremultipliedAlpha(
                    draw.TextureId, Bounds.renderX + dx, Bounds.renderY,
                    Bounds.InnerWidth, Bounds.InnerHeight);
            }

            public override void Dispose()
            {
                base.Dispose();
                _tex?.Dispose();
                _hotTex?.Dispose();
            }
        }

        /// <summary>
        /// One clickable colour cell (v0.4.12) — used both for the role chips, which show what a role is
        /// currently drawn in, and for the swatches that assign a new colour to the selected role.
        /// </summary>
        /// <remarks>
        /// Selection is drawn as a bright ring OUTSIDE the fill rather than a tick or an inset border, so it
        /// never covers any part of the colour it is marking. On a grid where the whole point is comparing
        /// colours, a marker that eats into the sample is worse than useless.
        /// </remarks>
        private sealed class ColorCellElement : GuiElement
        {
            private readonly float[] _rgb;
            private readonly bool _selected;
            private readonly Action _onClick;

            public ColorCellElement(ICoreClientAPI capi, ElementBounds bounds,
                float[] rgb, bool selected, Action onClick) : base(capi, bounds)
            {
                _rgb = rgb;
                _selected = selected;
                _onClick = onClick;
            }

            public override void ComposeElements(Context ctx, ImageSurface surface)
            {
                Bounds.CalcWorldBounds();
                double x = Bounds.drawX, y = Bounds.drawY, w = Bounds.InnerWidth, h = Bounds.InnerHeight;

                // Inset the fill by the ring's width so a selected cell is the same size as an unselected
                // one — the grid must not shift under the cursor as the selection moves.
                const double ring = 2;
                ctx.Rectangle(x + ring, y + ring, w - ring * 2, h - ring * 2);
                ctx.SetSourceRGBA(_rgb[0], _rgb[1], _rgb[2], 1);
                ctx.Fill();

                ctx.LineWidth = _selected ? ring : 1;
                ctx.SetSourceRGBA(1, 1, 1, _selected ? 0.95 : 0.30);
                double o = ctx.LineWidth / 2;
                ctx.Rectangle(x + ring - o, y + ring - o, w - ring * 2 + o * 2, h - ring * 2 + o * 2);
                ctx.Stroke();
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

        // ---------------------------------------------------------------------------------
        //  Visibility row + the two Reveal actions (v0.4.18)
        // ---------------------------------------------------------------------------------
        // Shown / Hidden are an exclusive pair describing the SELECTED guide. Reveal Near and Reveal All
        // are momentary ACTIONS over many guides, so they sit in the row's last two tile columns with an
        // empty column between: the gap is what says "these are not a third and fourth visibility state".
        //
        // The two actions stay ENABLED even when the rest of the row is greyed for having no selection.
        // That is the whole point of them — you reach for Reveal precisely when a guide is hidden and
        // therefore awkward to click on, so gating them behind having selected one would make them
        // useless in the only situation that calls for them.
        private void AddVisibilityRow(
            GuiComposer c, CairoFont labelFont, CairoFont hoverFont, ref double y,
            double labelW, double pad, double tile, double tileGap, double rowGap,
            GuideData selected, bool inert)
        {
            c.AddStaticText("Visibility", Centered(labelFont),
                ElementBounds.Fixed(0, y + (tile - 16) / 2, labelW, 20));

            for (int i = 0; i < VisCodes.Length; i++)
            {
                ElementBounds tb = ElementBounds.Fixed(labelW + pad + i * (tile + tileGap), y, tile, tile);
                AddIconTile(c, VisIcons[i], VisNames[i], tb, "vis", VisCodes[i], "vis:" + i,
                    OnGuideVisibilityTile, !inert);
            }
            if (inert) _inertRows.Add("vis");
            else _initialLight.Add(("vis", ClampIndex((selected?.IsHidden ?? false) ? 1 : 0, VisCodes.Length)));

            AddRevealTile(c, hoverFont, LayoutToolIcons.RevealNear, "near",
                ElementBounds.Fixed(labelW + pad + 3 * (tile + tileGap), y, tile, tile),
                "Reveal Near\nUn-hides every hidden guide within " + RevealNearBlocks
                + " blocks of you, whoever made them.");

            AddRevealTile(c, hoverFont, LayoutToolIcons.RevealAll, "all",
                ElementBounds.Fixed(labelW + pad + 4 * (tile + tileGap), y, tile, tile),
                "Reveal All\nUn-hides every hidden guide YOU made, anywhere in the world. Other players' "
                + "guides are left alone.");

            y += tile + rowGap;
        }

        // Momentary, like the Transform pad's rotate tiles: a reveal is something you do, not a state the
        // row sits in, so the tile snaps back off rather than staying lit.
        private void AddRevealTile(
            GuiComposer c, CairoFont hoverFont, string icon, string code, ElementBounds bounds, string hover)
        {
            // Always enabled — see the note on AddVisibilityRow about why these two are not greyed with
            // the rest of the row.
            AddMomentaryTile(c, hoverFont, icon, hover, "reveal:" + code, bounds, true,
                () => RevealGuides(nearOnly: code == "near"));
        }

        /// <summary>How far "near" reaches, in blocks (human-directed).</summary>
        private const int RevealNearBlocks = 6;

        /// <summary>
        /// Un-hides a batch of guides: those within <see cref="RevealNearBlocks"/> of the player, or every
        /// one the player created.
        /// </summary>
        /// <remarks>
        /// SENT ONE AT A TIME through the ordinary hide path rather than as a bulk packet. That path
        /// already carries every rule this must not sidestep — the privilege check, the edit-lock check,
        /// the jail check, the broadcast, and the undo entry — and a bulk packet would have had to
        /// reproduce all of them correctly to gain nothing but fewer bytes. It also means a reveal works
        /// unchanged on private client-only guides, which never touch the network at all.
        ///
        /// The consequence worth knowing: each guide is its own undo step, so undoing a Reveal All that
        /// touched nine guides is nine presses. That is the honest cost of reusing the single-guide seam,
        /// and it is recoverable — a bulk step that half-failed partway would not be.
        ///
        /// REVEAL ALL SPLITS IN TWO (v0.4.19). Only the client knows which guides are its own PRIVATE
        /// ones, and only the server knows who CREATED a public guide — the wire record carries the
        /// creator's display name for the HUD but never their UID, so the v0.4.18 client-side ownership
        /// filter matched nothing and the button did nothing. Each side now answers the half it can.
        ///
        /// The ids are COLLECTED FIRST. Revealing a private guide mutates the local mirror synchronously,
        /// which would invalidate an enumerator still walking it.
        /// </remarks>
        private void RevealGuides(bool nearOnly)
        {
            IClientPlayer player = capi.World?.Player;
            if (player == null) return;

            Vec3d here = player.Entity?.Pos?.XYZ;
            if (nearOnly && here == null) return;

            var targets = new List<Guid>();
            foreach (GuideData g in _net.Guides.Values)
            {
                if (g == null || !g.IsHidden) continue;
                // Near: anyone's guide, so long as it is standing here. All: only guides this client
                // owns outright, which is exactly the private ones — the server handles the rest.
                if (nearOnly ? !WithinBlocks(g, here, RevealNearBlocks) : !_net.IsLocalGuide(g.Id))
                    continue;
                targets.Add(g.Id);
            }

            for (int i = 0; i < targets.Count; i++) _net.SendHide(targets[i], false);

            if (!nearOnly && _net.ServerLayoutAvailable)
            {
                // The server reports the empty case itself, so nothing is said here either way.
                _net.SendRevealMine();
            }
            else if (targets.Count == 0)
            {
                capi.TriggerIngameError(this, "layout-nothingtoreveal",
                    nearOnly
                        ? "No hidden guides within " + RevealNearBlocks + " blocks."
                        : "You have no hidden guides.");
                return;
            }

            DeferRecompose();   // the selected guide's own Shown/Hidden pair may have just moved
        }

        // Distance to the NEAREST control point, not to the guide's centre: a 40-block arch whose foot is
        // beside you is a guide that is near you, and measuring from its middle would say otherwise.
        // Control points only — this must never touch a behemoth's voxel set to answer a proximity test.
        private static bool WithinBlocks(GuideData g, Vec3d here, double blocks)
        {
            List<ControlPoint> points = g.ControlPoints;
            if (points == null) return false;
            double limit = blocks * blocks;
            for (int i = 0; i < points.Count; i++)
            {
                ControlPoint cp = points[i];
                if (cp == null || cp.IsPhantom || cp.WorldPosition == null) continue;
                double dx = cp.WorldPosition.X - here.X;
                double dy = cp.WorldPosition.Y - here.Y;
                double dz = cp.WorldPosition.Z - here.Z;
                if (dx * dx + dy * dy + dz * dz <= limit) return true;
            }
            return false;
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
            _net.AuthorityModeChanged += OnAuthorityModeChanged;
            _subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!_subscribed) return;
            _net.GuideAddedOrUpdated -= OnGuideAddedOrUpdated;
            _net.LockStateChanged   -= OnLockStateChanged;
            _net.AuthorityModeChanged -= OnAuthorityModeChanged;
            _subscribed = false;
        }

        // Authority resolved or changed — the server's private-guide policy is only known once it has. Only
        // that one line is refreshed; recomposing would restart the slider at its resting position mid-slide.
        private void OnAuthorityModeChanged(ClientAuthorityMode mode) => RefreshServerPrivacyPolicy();

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

