using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
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
    //  dim labels, dim tiles, clicks inert — signalling that none of those settings
    //  apply while deleting. The rows keep showing the defaults you'll come back to.
    //  (Greying = dim fonts + an input guard; if the installed GuiElementToggleButton
    //  exposes an Enabled flag, setting it in AddTileRow is the nicer upgrade.)
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
    //    - GuiComposer.AddDropDown(string[] values, string[] names, int selectedIndex,
    //          SelectionChangedDelegate onSelectionChanged, ElementBounds, string key)
    //          · delegate: (string code, bool selected); accessor GetDropDown(key)
    //    - GuiComposer.AddTextInput(ElementBounds, Action<string> onTextChanged, CairoFont, string key)
    //          · accessor: GetTextInput(key).SetValue(string) — fires onTextChanged on some builds,
    //            so every programmatic SetValue here runs under the _suppress guard
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

        // ---- Tile row tables (code kept stable; display names are short tile labels) ----
        private static readonly string[] ModeCodes  = { "create", "delete" };
        private static readonly string[] ModeNames  = { "Create", "Delete" };

        // The shape catalog — the primitives+modifiers model, in the order players think of them
        // (Session 9: eleven entries, rendered as a tile GRID of four per row). Codes map to
        // (GuideShapeType, ShapeConstraint) in ShapeFromCode below.
        private static readonly string[] ShapeCodes = {
            "arch", "halfcircle", "circle", "ellipse",
            "line", "triangle", "righttri", "equilateral",
            "isosceles", "rectangle", "square" };
        private static readonly string[] ShapeNames = {
            "Arch", "Half-circle", "Circle", "Ellipse",
            "Line", "Triangle", "Right ◺", "Equilateral",
            "Isosceles", "Rectangle", "Square" };

        // Divisions presets for the dropdown; the adjacent field accepts any number (clamped).
        private static readonly string[] DivisionCodes = { "0", "2", "3", "4", "5", "6", "8", "10", "12", "16" };
        private static readonly string[] DivisionNames = { "Off", "2", "3", "4", "5", "6", "8", "10", "12", "16" };

        private static readonly string[] ScaleCodes = { "1", "2", "4", "8", "16" };
        private static readonly string[] ScaleNames = { "1/16", "1/8", "1/4", "1/2", "1" };

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

        public GuideToolGui(ICoreClientAPI capi, DraftManager tool, ClientNetworkHandler net) : base(capi)
        {
            _tool = tool;
            _net = net;
        }

        // Opened programmatically by the tool item, so no auto key combination.
        public override string ToggleKeyCombinationCode => null;

        // Modal: we want the cursor to click the tiles.
        public override bool PrefersUngrabbedMouse => true;

        public override void OnGuiOpened()
        {
            base.OnGuiOpened();
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

        // ---------------------------------------------------------------------------------
        //  Context resolution
        // ---------------------------------------------------------------------------------
        // The guide the appended section acts on, or null when no live selection (or Delete
        // mode, which suppresses the section). Because the dialog holds the mouse, the
        // selection cannot change while composed, so it's safe to resolve this once per
        // composition and reuse it in the section builder.
        private GuideData ResolveSelectedGuide()
        {
            if (_tool.Mode == ToolMode.Delete) return null;
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
            CairoFont dimFont = CairoFont.WhiteDetailText();
            // Greyed rows in Delete mode (playtest revision: the detail-font dim was far too subtle):
            // GHOST fonts — same size as the live text but at a fraction of its alpha, so the rows fade
            // hard toward the dialog background while keeping their layout. Combined with leaving these
            // rows UNLIT (no pressed tile) and the input guard, the section reads unmistakably disabled.
            CairoFont ghostFont = CairoFont.WhiteSmallText();
            ghostFont.Color = new double[] { 1, 1, 1, 0.22 };
            CairoFont mainLabelFont = deleteMode ? ghostFont : font;
            CairoFont mainTileFont = deleteMode ? ghostFont : font;

            // Layout metrics (pixels, pre-scaled by the GUI system). The control column is a
            // touch wider than the dropdown era so four tiles sit comfortably in one row.
            const double pad       = 6;
            const double labelW    = 110;
            const double controlW  = 340;
            const double rowH      = 26;
            const double rowGap    = 6;
            const double headerH   = 22;
            const double favH      = 30;
            const double sectionGap = 10;

            double contentW = labelW + pad + controlW;
            double y = GuiStyle.TitleBarHeight + 6;

            _inertRows.Clear();
            _initialLight.Clear();

            ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            ElementBounds dialogBounds = ElementStdBounds
                .AutosizedMainDialog
                .WithAlignment(EnumDialogArea.RightMiddle)
                .WithFixedAlignmentOffset(-GuiStyle.DialogToScreenPadding, 0);

            GuiComposer c = capi.Gui
                .CreateCompo("layout:toolgui", dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar("Layout Tool", OnTitleBarClose)
                .BeginChildElements(bgBounds);

            // Context header — a dynamic-text line so mode flips can refresh it cheaply.
            ElementBounds headerBounds = ElementBounds.Fixed(0, y, contentW, headerH);
            c.AddDynamicText(BuildHeaderText(), font, headerBounds, "header");
            y += headerH + rowGap;

            // ================= MAIN ROWS — permanent tool defaults, never re-purposed =================

            // ---- Mode row (always live, even in Delete mode — it's the way back) ----
            AddTileRow(c, font, font, ref y, labelW, controlW, pad, rowH, rowGap,
                "Mode", ModeCodes, ModeNames, (int)_tool.Mode, OnModeTile, "mode", inert: false);

            // ---- Shape grid: the initial-shape picker — ALWAYS present; four tiles per row ----
            AddTileGrid(c, mainLabelFont, mainTileFont, ref y, labelW, controlW, pad, rowH, rowGap,
                "Shape", ShapeCodes, ShapeNames, CurrentShapeIndex(), OnShapeTile, "shape", deleteMode, perRow: 4);

            // ---- Favorites placeholder (visible space now, feature later) ----
            {
                ElementBounds lb = ElementBounds.Fixed(0, y + 6, labelW, favH);
                ElementBounds well = ElementBounds.Fixed(labelW + pad, y, controlW, favH);
                ElementBounds hint = ElementBounds.Fixed(labelW + pad + 10, y + 7, controlW - 20, favH - 8);
                CairoFont favFont = deleteMode ? ghostFont : dimFont;
                c.AddStaticText("Favorites", favFont, lb)
                 .AddInset(well, 3)
                 .AddStaticText("☆  star shapes to pin them here — coming soon", favFont, hint);
                y += favH + rowGap;
            }

            // ---- Scale / Projection / (Plane) / Fill — tool defaults for the next guide ----
            AddTileRow(c, mainLabelFont, mainTileFont, ref y, labelW, controlW, pad, rowH, rowGap,
                "Scale", ScaleCodes, ScaleNames, IndexOfScale(_tool.Scale), OnScaleTile, "scale", deleteMode);

            bool toolSurface = _tool.Projection == ProjectionMode.Surface;
            AddTileRow(c, mainLabelFont, mainTileFont, ref y, labelW, controlW, pad, rowH, rowGap,
                "Projection", ProjCodes, ProjNames, toolSurface ? 1 : 0, OnProjectionTile, "proj", deleteMode);

            if (toolSurface)
            {
                AddTileRow(c, mainLabelFont, mainTileFont, ref y, labelW, controlW, pad, rowH, rowGap,
                    "Plane", PlaneToolCodes, PlaneToolNames,
                    OverrideToToolIndex(_tool.PlaneOverride), OnPlaneTile, "plane", deleteMode);
            }

            AddTileRow(c, mainLabelFont, mainTileFont, ref y, labelW, controlW, pad, rowH, rowGap,
                "Fill", FillCodes, FillNames, _tool.Filled ? 1 : 0, OnFillTile, "fill", deleteMode);

            // ---- Divisions (Session 9): preset dropdown + type-in field (stock VS has no combobox,
            // so the requested "editable dropdown" is this composite — picking a preset fills the field;
            // typing any number applies it directly, clamped). Ghost static text in Delete mode.
            AddDivisionsControl(c, mainLabelFont, font, dimFont, ref y, labelW, controlW, pad, rowH, rowGap,
                _tool.Divisions, deleteMode, "div", OnToolDivisionsChanged);

            // ================= SELECTED-GUIDE SECTION — appended, clearly separated =================
            if (selected != null)
            {
                y += sectionGap;
                BuildSelectedGuideSection(c, font, dimFont, ref y,
                    labelW, controlW, pad, rowH, rowGap, headerH, selected);
            }

            SingleComposer = c.EndChildElements().Compose();

            // Light each row's current selection. Safe here: SetupDialog is only ever called
            // from OnGuiOpened or the deferred-recompose timer, never from inside a composer
            // callback, and toggle SetValue does not fire the toggle handler.
            LightInitialTiles();
        }

        // The appended per-guide controls: its own header (dynamic, so lock changes refresh
        // in place), tile rows keyed "g*" acting on THAT guide via the network senders, and
        // an explicit Deselect button (the anti-"locked in" affordance).
        private void BuildSelectedGuideSection(
            GuiComposer c, CairoFont font, CairoFont dimFont, ref double y,
            double labelW, double controlW, double pad, double rowH, double rowGap, double headerH,
            GuideData g)
        {
            double contentW = labelW + pad + controlW;

            ElementBounds gHeaderBounds = ElementBounds.Fixed(0, y, contentW - 100, headerH);
            ElementBounds deselBounds = ElementBounds.Fixed(contentW - 92, y - 3, 92, rowH);
            c.AddDynamicText(BuildSelectedHeaderText(g), font, gHeaderBounds, "gheader")
             .AddSmallButton("Deselect", OnDeselectClicked, deselBounds);
            y += headerH + rowGap;

            ElementBounds hintBounds = ElementBounds.Fixed(0, y, contentW, headerH - 4);
            c.AddStaticText("These act on the selected guide (shape reshapes by grabbing — locks pin, grabs flow):",
                dimFont, hintBounds);
            y += headerH - 4 + rowGap;

            AddTileRow(c, font, font, ref y, labelW, controlW, pad, rowH, rowGap,
                "Scale", ScaleCodes, ScaleNames, IndexOfScale(g.VoxelScale), OnGuideScaleTile, "gscale", inert: false);

            bool gSurface = g.Projection == ProjectionMode.Surface;
            AddTileRow(c, font, font, ref y, labelW, controlW, pad, rowH, rowGap,
                "Projection", ProjCodes, ProjNames, gSurface ? 1 : 0, OnGuideProjectionTile, "gproj", inert: false);

            if (gSurface)
            {
                AddTileRow(c, font, font, ref y, labelW, controlW, pad, rowH, rowGap,
                    "Plane", PlaneEditCodes, PlaneEditNames,
                    AxisToEditIndex(g.Plane.FlattenedAxis), OnGuidePlaneTile, "gplane", inert: false);
            }

            AddTileRow(c, font, font, ref y, labelW, controlW, pad, rowH, rowGap,
                "Fill", FillCodes, FillNames, g.IsFilled ? 1 : 0, OnGuideFillTile, "gfill", inert: false);

            AddDivisionsControl(c, font, font, dimFont, ref y, labelW, controlW, pad, rowH, rowGap,
                g.Divisions, inert: false, key: "gdiv", onChanged: OnGuideDivisionsChanged);

            AddTileRow(c, font, font, ref y, labelW, controlW, pad, rowH, rowGap,
                "Visibility", VisCodes, VisNames, g.IsHidden ? 1 : 0, OnGuideVisibilityTile, "gvis", inert: false);
        }

        // The Divisions composite (Session 9): a preset dropdown plus a numeric type-in field sharing one
        // key space ("{key}:drop" / "{key}:text"). Selecting a preset applies it and mirrors it into the
        // field; typing applies any parseable number (clamped 0..DivisionMarks.MaxDivisions). Programmatic
        // mirroring runs under the _suppress guard so it never re-fires the handlers. In inert (Delete)
        // mode the row renders as ghost static text instead of live controls.
        // [Flagged: the text field applies per keystroke with a changed-value guard — typing "12" briefly
        //  applies 1 then 12; per-guide that is two sends/undo steps. Acceptable v1; a commit-on-close
        //  pass is the upgrade if it annoys.]
        private void AddDivisionsControl(
            GuiComposer c, CairoFont labelFont, CairoFont font, CairoFont dimFont, ref double y,
            double labelW, double controlW, double pad, double rowH, double rowGap,
            int current, bool inert, string key, Action<int> onChanged)
        {
            ElementBounds labelBounds = ElementBounds.Fixed(0, y + 4, labelW, rowH);
            c.AddStaticText("Divisions", labelFont, labelBounds);

            if (inert)
            {
                ElementBounds vb = ElementBounds.Fixed(labelW + pad, y + 4, controlW, rowH);
                c.AddStaticText(current > 1 ? current.ToString() : "Off", labelFont, vb);
                y += rowH + rowGap;
                return;
            }

            double dropW = controlW * 0.55;
            double fieldW = controlW - dropW - 8;
            ElementBounds dropBounds = ElementBounds.Fixed(labelW + pad, y, dropW, rowH);
            ElementBounds fieldBounds = ElementBounds.Fixed(labelW + pad + dropW + 8, y, fieldW, rowH);

            int presetIndex = Array.IndexOf(DivisionCodes, current.ToString());
            c.AddDropDown(DivisionCodes, DivisionNames, presetIndex < 0 ? 0 : presetIndex,
                (code, selected) => OnDivisionsPreset(key, code, onChanged), dropBounds, key + ":drop");
            c.AddTextInput(fieldBounds, text => OnDivisionsTyped(text, current, onChanged), font, key + ":text");

            _pendingFieldText.Add((key + ":text", current > 0 ? current.ToString() : ""));
            y += rowH + rowGap;
        }

        private readonly List<(string key, string text)> _pendingFieldText = new List<(string, string)>();

        private void OnDivisionsPreset(string key, string code, Action<int> onChanged)
        {
            if (_suppress) return;
            if (!int.TryParse(code, out int value)) return;
            onChanged(value);
            _suppress = true;
            try { SingleComposer?.GetTextInput(key + ":text")?.SetValue(value > 0 ? value.ToString() : ""); }
            finally { _suppress = false; }
        }

        private void OnDivisionsTyped(string text, int previous, Action<int> onChanged)
        {
            if (_suppress) return;
            if (string.IsNullOrWhiteSpace(text)) { onChanged(0); return; }
            if (!int.TryParse(text.Trim(), out int value)) return;   // ignore partial/garbled input
            if (value < 0) value = 0;
            if (value > Shapes.DivisionMarks.MaxDivisions) value = Shapes.DivisionMarks.MaxDivisions;
            onChanged(value);
        }

        private void OnToolDivisionsChanged(int value) => _tool.SetDivisions(value);

        private void OnGuideDivisionsChanged(int value)
        {
            GuideData g = ResolveSelectedGuide();
            if (g != null && g.Divisions != value) _net.SendSetDivisions(g.Id, value);
        }

        // Adds a label + a GRID of exclusive tiles (<= perRow per line, one shared key space) and
        // advances y. Used by the Session-9 shape catalog, which outgrew a single row.
        private void AddTileGrid(
            GuiComposer c, CairoFont labelFont, CairoFont tileFont, ref double y,
            double labelW, double controlW, double pad, double rowH, double rowGap,
            string label, string[] codes, string[] names, int selectedIndex,
            Action<string, string> onTile, string key, bool inert, int perRow)
        {
            ElementBounds labelBounds = ElementBounds.Fixed(0, y + 4, labelW, rowH);
            c.AddStaticText(label, labelFont, labelBounds);

            const double tileGap = 4;
            double tileW = (controlW - tileGap * (perRow - 1)) / perRow;
            int sel = ClampIndex(selectedIndex, codes.Length);

            for (int i = 0; i < codes.Length; i++)
            {
                int row = i / perRow, col = i % perRow;
                string code = codes[i];
                string tileKey = key + ":" + i;
                ElementBounds tb = ElementBounds.Fixed(
                    labelW + pad + col * (tileW + tileGap), y + row * (rowH + tileGap), tileW, rowH);
                c.AddToggleButton(names[i], tileFont, on => OnTileToggled(key, code, tileKey, on, onTile), tb, tileKey);
            }

            int rows = (codes.Length + perRow - 1) / perRow;
            if (inert) _inertRows.Add(key);
            y += rows * rowH + (rows - 1) * 4 + rowGap;
            if (!inert) _initialLight.Add((key, sel));
        }

        // Adds one "label + N exclusive tiles" row and advances y. Tile keys are "{key}:{i}".
        // An INERT row (Delete-mode greying) renders normally but its tiles ignore input —
        // clicks snap straight back with no side effects.
        private void AddTileRow(
            GuiComposer c, CairoFont labelFont, CairoFont tileFont, ref double y,
            double labelW, double controlW, double pad, double rowH, double rowGap,
            string label, string[] codes, string[] names, int selectedIndex,
            Action<string, string> onTile, string key, bool inert)
        {
            ElementBounds labelBounds = ElementBounds.Fixed(0, y + 4, labelW, rowH);
            c.AddStaticText(label, labelFont, labelBounds);

            const double tileGap = 4;
            double tileW = (controlW - tileGap * (codes.Length - 1)) / codes.Length;
            int sel = ClampIndex(selectedIndex, codes.Length);

            for (int i = 0; i < codes.Length; i++)
            {
                string code = codes[i];
                string tileKey = key + ":" + i;
                ElementBounds tb = ElementBounds.Fixed(labelW + pad + i * (tileW + tileGap), y, tileW, rowH);
                c.AddToggleButton(names[i], tileFont, on => OnTileToggled(key, code, tileKey, on, onTile), tb, tileKey);
            }

            if (inert) _inertRows.Add(key);
            y += rowH + rowGap;
            // Inert rows stay UNLIT — a pressed tile reads as "active", exactly the signal a greyed row
            // must not send. Live rows light their current selection after composition.
            if (!inert) _initialLight.Add((key, sel));
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
                foreach ((string key, string text) in _pendingFieldText)
                    SingleComposer.GetTextInput(key)?.SetValue(text);
            }
            finally
            {
                _suppress = false;
                _initialLight.Clear();
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
            bool filled = code == "filled";
            if (g.IsFilled != filled) _net.SendSetFilled(g.Id, filled);
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

        private void RefreshSelectedGuideControls(GuideData g)
        {
            if (SingleComposer == null) return;

            bool surfaceNow = g.Projection == ProjectionMode.Surface;
            bool planeRowShown = SingleComposer.GetToggleButton("gplane:0") != null;

            // If the projection changed such that the plane row must appear or disappear,
            // the section's row set itself changed — do a full (deferred) recompose instead
            // of a value-only relight.
            if (surfaceNow != planeRowShown)
            {
                DeferRecompose();
                return;
            }

            _suppress = true;
            try
            {
                RelightRow("gscale", ScaleCodes, IndexOfScale(g.VoxelScale));
                RelightRow("gproj", ProjCodes, surfaceNow ? 1 : 0);
                if (surfaceNow) RelightRow("gplane", PlaneEditCodes, AxisToEditIndex(g.Plane.FlattenedAxis));
                RelightRow("gfill", FillCodes, g.IsFilled ? 1 : 0);
                SingleComposer?.GetTextInput("gdiv:text")?.SetValue(g.Divisions > 0 ? g.Divisions.ToString() : "");
                RelightRow("gvis", VisCodes, g.IsHidden ? 1 : 0);
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
                ToolMode.Create => "Create mode — next guide: "
                    + ShapeDisplayName(_tool.Shape, _tool.Constraint),
                ToolMode.Delete => "Delete mode — click a guide to remove it",
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
                + " " + ShortId(g.Id) + " — " + lockNote;
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
            _ => constraint == ShapeConstraint.SemiCircle ? 1 : 0
        };

        internal static string ShapeDisplayName(GuideShapeType shape, ShapeConstraint constraint) =>
            ShapeNames[ShapeIndexOf(shape, constraint)];

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
