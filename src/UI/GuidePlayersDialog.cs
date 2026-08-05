using System;
using System.Collections.Generic;
using System.Linq;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Layout.Network;

namespace Layout.UI
{
    /// <summary>
    /// The admin Players dialog (v0.4.26; became editable in v0.4.31): who Layout knows about, what limits
    /// they carry, who is jailed — and now the place those limits are changed from. Opened from the Players
    /// button in the settings page's Admin section.
    /// </summary>
    /// <remarks>
    /// THREE TABS OVER ONE ROSTER. Players, Overrides and Jail are three filters of the same server-built
    /// list, not three separate queries — fetching them independently would let the three views disagree
    /// about someone whose policy changed between requests. Tab order is Players, Overrides, Jail
    /// (human-directed): the general case first, then the two exception lists.
    ///
    /// EDITING (v0.4.31, human-directed). Selecting a player and pressing Edit turns the cap lines into
    /// fields. Edits STAGE and reach the server only on Save, for the same two reasons the server-settings
    /// section stages its own (v0.4.20): a cap typed digit by digit would otherwise arrive as a series of
    /// nonsense values, each one briefly real and each one clamping that player's draft preview; and an
    /// admin needs to be able to tell a typed value from a stored one. After Save the server sends the whole
    /// roster back, so the fields end up showing what was actually stored rather than what was typed.
    ///
    /// JAILING IS NOT PART OF THAT SAVE. It is the one action here with immediate consequences for someone
    /// else's session, so it is its own button with its own confirmation — see PlayerJailPacket.
    ///
    /// THE COMMANDS ARE NOT DEPRECATED. /layout voxelcap, totalvoxelcap, limit, jail and free all still
    /// work and land on the same setters. This is a second route to the same state, not a replacement.
    ///
    /// A SEPARATE DIALOG, not another page of the tool panel. The tool panel is anchored to the right edge
    /// at a width driven by its 42 px tile grid; a player table wants to be wider and centred, and pushing
    /// it through the tool panel's layout would have distorted both.
    /// </remarks>
    public class GuidePlayersDialog : GuiDialog
    {
        private readonly ClientNetworkHandler _net;

        private enum Tab { Players = 0, Overrides = 1, Jail = 2 }
        private static readonly string[] TabNames = { "Players", "Overrides", "Jail" };

        /// <summary>Which column the roster is ordered by.</summary>
        private enum SortKey { Name = 0, Guides = 1, Voxels = 2 }

        private Tab _tab = Tab.Players;
        private string _selectedUid;
        private bool _suppress;
        private bool _recomposePending;

        // VOXELS DESCENDING BY DEFAULT (v0.4.31). The server sorts online-first-then-name, which is the
        // right answer for "who can I act on", but the question an admin actually opens this dialog with is
        // "who is eating the budget". Sorting is a client-side view choice, so the server's order is left
        // alone and remains available as the Player column.
        private SortKey _sort = SortKey.Voxels;
        private bool _descending = true;

        private string _filter = "";

        /// <summary>
        /// One-shot: this recompose was caused by typing in the filter, so focus and the caret have to go
        /// back there. Deliberately NOT sticky — a recompose from clicking a row or a tab must leave focus
        /// where the click put it, or every selection would silently move the keyboard to the filter box.
        /// </summary>
        private bool _restoreFilterFocus;

        // ---- edit state -------------------------------------------------------------------------
        private bool _editing;
        private int _editVoxelCap, _editTotalVoxelCap, _editGuideLimit;

        /// <summary>
        /// The player whose Jail/Free button is armed, if any: the second press is the one that acts.
        /// </summary>
        /// <remarks>
        /// A UID rather than a bool (v0.4.32), because the Jail tab now has a Free button on every row and a
        /// single flag would arm all of them at once — an admin aiming at one row would see every row asking
        /// for confirmation, and a stray second click anywhere would free the wrong player.
        /// </remarks>
        private string _confirmUid;

        // ---- list scrolling ---------------------------------------------------------------------
        private GuiElementContainer _rows;
        private float _scroll;

        /// <summary>
        /// Rows drawn per tab on the two tabs that have no scroll pane of their own. The Players tab
        /// scrolls and therefore has no such limit.
        /// </summary>
        private const int MaxListedRows = 40;

        public GuidePlayersDialog(ICoreClientAPI capi, ClientNetworkHandler net) : base(capi)
        {
            _net = net ?? throw new ArgumentNullException(nameof(net));
            _net.PlayerRosterChanged += OnRosterChanged;
            _net.PlayerGuidesChanged += OnGuidesChanged;
        }

        public override string ToggleKeyCombinationCode => null;

        public override bool PrefersUngrabbedMouse => true;

        public override void OnGuiOpened()
        {
            base.OnGuiOpened();
            _tab = Tab.Players;
            _selectedUid = null;
            _filter = "";
            _restoreFilterFocus = false;
            _editing = false;
            _confirmUid = null;
            _scroll = 0;
            // Always re-fetch on open: a roster cached from ten minutes ago would show stale guide counts
            // and a since-freed player still in the Jail tab.
            _net.RequestPlayerRoster();
            Compose();
        }

        /// <summary>
        /// The roster arrived. This is the server's answer to whatever was just saved, so it also ends edit
        /// mode: leaving the fields open over freshly authoritative numbers would show the typed values
        /// winning over the stored ones, which is the exact confusion staging exists to prevent.
        /// </summary>
        private void OnRosterChanged()
        {
            if (!IsOpened()) return;
            _editing = false;
            _confirmUid = null;
            DeferRecompose();
        }

        /// <summary>
        /// A player's guide list arrived. REDRAW ONLY — this must not touch edit mode, which is why the two
        /// events are handled separately. They shared one handler until v0.4.32, and that made the Overrides
        /// tab's Edit button impossible: it opens the form AND asks for that player's guides, so the guide
        /// list would arrive a tick later and close the form the click had just opened.
        /// </summary>
        private void OnGuidesChanged()
        {
            if (IsOpened()) DeferRecompose();
        }

        // Never recompose from inside a composer callback — the same rule and the same delay the tool
        // panel follows, so a click that changes tabs is not rebuilding elements the click is still in.
        //
        // ⚠ THE FLAG MUST NEVER OUTLIVE ITS CALLBACK. v0.4.40 replaced the guard below with an
        // "earliest request wins" deadline, so a click would not have to wait out the filter's 350 ms
        // debounce. Its callback returned early when it found the deadline had moved — WITHOUT clearing
        // the flag and WITHOUT rescheduling. One early return and `_recomposePending` was latched true
        // with nothing in flight, so every later request took the "already pending" path and the dialog
        // never redrew again: tabs stayed pressed in, rows would not select, the whole panel dead.
        // (Human-reported; reverted in v0.4.41.)
        //
        // The latency it was chasing is a third of a second on one uncommon interleaving. This guard is
        // simple, has shipped since v0.4.26, and cannot deadlock — the one-shot callback ALWAYS clears
        // the flag, and its IsOpened() check neutralises a late fire. The focus half of that fix was the
        // half that mattered and it is unaffected: see _restoreFilterFocus, cleared by every click path.
        private void DeferRecompose(int delayMs = 30)
        {
            if (_recomposePending) return;
            _recomposePending = true;
            capi.Event.RegisterCallback(_ =>
            {
                _recomposePending = false;
                if (IsOpened()) Compose();
            }, delayMs);
        }

        // ---------------------------------------------------------------------------------
        //  Layout
        // ---------------------------------------------------------------------------------

        // WIDENED from 620 in v0.4.31. The list became a three-column table and the detail pane gained a
        // form; at 620 the two were fighting over the same pixels and the detail lines were already
        // overflowing (that was the v0.4.28 fix). An admin table is allowed to be wide.
        private const double DialogW = 780;
        private const double RowH = 24;
        private const double TabW = 110, TabH = 26;

        private const double ListW = 300;
        private const double ScrollW = 18;
        private const double VisibleRows = 13;
        private const double ListH = VisibleRows * RowH;
        private const double DetailX = ListW + ScrollW + 22;
        private const double DetailW = DialogW - DetailX;

        // Column offsets inside one row, and matched by the clickable headers above the list.
        private const double ColNameX = 5;
        private const double ColGuidesRight = 200;
        private const double ColVoxelsRight = ListW - 6;

        // How much room the name is allowed before the Guides column starts. A name is player-supplied and
        // has no length this table can rely on, so it is CLIPPED — drawn unclipped it runs straight through
        // the numbers to its right, which leaves the sortable headers above lining up with nothing.
        private const double ColGuidesWidest = 44;      // "999 *" at detail size, with room to spare
        private const double ColNameWidth = ColGuidesRight - ColNameX - ColGuidesWidest;

        private void Compose()
        {
            ElementBounds bg = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bg.BothSizing = ElementSizing.FitToChildren;

            ElementBounds dialog = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.CenterMiddle);

            GuiComposer c = capi.Gui
                .CreateCompo("layout:players", dialog)
                .AddShadedDialogBG(bg)
                .AddDialogTitleBar("Layout Players", () => TryClose())
                .BeginChildElements(bg);

            double y = Math.Max(0, GuiStyle.TitleBarHeight - GuiStyle.ElementToDialogPadding) + 4;

            // ---- tabs ----
            for (int i = 0; i < TabNames.Length; i++)
            {
                var tab = (Tab)i;
                c.AddToggleButton(TabNames[i], CairoFont.WhiteSmallText(),
                    on =>
                    {
                        if (_suppress || !on) return;
                        _tab = tab;
                        _editing = false;
                        _confirmUid = null;
                        _restoreFilterFocus = false;   // a click's redraw, not the filter's
                        DeferRecompose();
                    },
                    ElementBounds.Fixed(i * (TabW + 4), y, TabW, TabH), "tab" + i);
            }
            y += TabH + 6;

            PlayerRosterEntryDto[] roster = _net.PlayerRoster ?? Array.Empty<PlayerRosterEntryDto>();

            // ---- world totals: what the Admin section's WORLD caps are actually measured against ----
            if (roster.Length > 0)
            {
                c.AddStaticText(
                    "World: " + _net.WorldVoxelTotal.ToString("N0") + " voxels in "
                    + _net.WorldGuideCount.ToString("N0") + " guides, from "
                    + roster.Length.ToString("N0") + (roster.Length == 1 ? " player" : " players"),
                    Accent(), ElementBounds.Fixed(0, y, DialogW, 18));
                y += 20;
            }

            c.AddInset(ElementBounds.Fixed(0, y, DialogW, 1), 1, 0.4f);
            y += 1 + 6;

            if (roster.Length == 0)
            {
                string empty = _net.ServerLayoutAvailable
                    ? "No players known yet. Nobody has built a guide, been jailed, or been given a limit."
                    : "This world has no Layout server, so there is no player roster.";
                double emptyH = TextHeight(empty, CairoFont.WhiteDetailText(), DialogW);
                c.AddStaticText(empty, CairoFont.WhiteDetailText(),
                    ElementBounds.Fixed(0, y, DialogW, emptyH));
                y += emptyH;
            }
            else
            {
                switch (_tab)
                {
                    case Tab.Players: BuildPlayersTab(c, roster, ref y); break;
                    case Tab.Overrides: BuildOverridesTab(c, roster, ref y); break;
                    case Tab.Jail: BuildJailTab(c, roster, ref y); break;
                }
            }

            y += 8;
            c.AddSmallButton("Refresh", () => { _net.RequestPlayerRoster(); return true; },
                ElementBounds.Fixed(0, y, 90, 24));
            c.AddSmallButton("Close", () => { TryClose(); return true; },
                ElementBounds.Fixed(DialogW - 90, y, 90, 24));
            y += 24;

            SingleComposer = c.EndChildElements().Compose();

            _suppress = true;
            try
            {
                SingleComposer.GetToggleButton("tab" + (int)_tab)?.SetValue(true);
                ApplyPostComposeValues();
            }
            finally { _suppress = false; }
        }

        /// <summary>
        /// The value pushes that can only happen once the elements exist: the filter's text and focus, the
        /// edit fields' numbers, and the scrollbar's extent.
        /// </summary>
        private void ApplyPostComposeValues()
        {
            GuiElementTextInput filter = SingleComposer.GetTextInput("filter");
            if (filter != null)
            {
                filter.SetValue(_filter ?? "", true);
                // Typing into the filter recomposes the dialog, which destroys the element being typed in.
                // Handing focus and the caret straight back is what lets a filter be typed at all; without
                // it the field would take exactly one character per click.
                if (_restoreFilterFocus)
                {
                    _restoreFilterFocus = false;
                    SingleComposer.FocusElement(filter.TabIndex);
                }
            }

            if (_editing)
            {
                // Whole numbers, and the SAME wheel/spinner step the equivalent server-wide field carries in
                // the Admin section (AdminCapSteps). IntMode and Interval can only be set on the live
                // element, so they are pushed here rather than at AddNumberInput.
                PushEditField("editvoxelcap", _editVoxelCap, AdminCapSteps.PerGuideVoxels);
                PushEditField("edittotalcap", _editTotalVoxelCap, AdminCapSteps.PerPlayerVoxels);
                PushEditField("editguidelimit", _editGuideLimit, AdminCapSteps.PerPlayerGuides);
            }

            GuiElementScrollbar bar = SingleComposer.GetScrollbar("scrollbar");
            if (bar != null && _rows != null)
            {
                double content = Math.Max(ListH, _rows.Bounds.fixedHeight);
                bar.SetHeights((float)ListH, (float)content);

                // CLAMPED FIRST, and that is the whole point. The scroll position deliberately survives a
                // recompose so a selection click keeps your place — but the list it was measured against
                // may have got SHORTER in between: a filter narrowing the roster, or a refresh that lost a
                // player. Re-applying a stale offset parks the container above the window and the tab shows
                // an empty list, which reads as "the filter matched nobody" rather than as a scroll bug.
                _scroll = (float)Math.Max(0, Math.Min(_scroll, content - ListH));

                // Restore the scroll position, then move the container to match: setting the bar alone
                // would leave the two disagreeing.
                bar.CurrentYPosition = _scroll;
                OnListScroll(_scroll);
            }
        }

        private void PushEditField(string key, int value, float interval)
        {
            GuiElementNumberInput num = SingleComposer.GetNumberInput(key);
            if (num == null) return;
            num.IntMode = true;
            num.Interval = interval;
            num.SetValue(value.ToString());
        }

        // The container sits inside a scope already positioned at the top of the list window, so the scroll
        // offset is simply a negative Y within that scope.
        private void OnListScroll(float value)
        {
            _scroll = value;
            if (_rows == null) return;
            _rows.Bounds.fixedY = -value;
            _rows.Bounds.CalcWorldBounds();
        }

        // ---------------------------------------------------------------------------------
        //  Players tab
        // ---------------------------------------------------------------------------------
        private void BuildPlayersTab(GuiComposer c, PlayerRosterEntryDto[] roster, ref double y)
        {
            // ---- filter ----
            c.AddStaticText("Filter", CairoFont.WhiteDetailText(), ElementBounds.Fixed(0, y + 4, 40, 20));
            c.AddTextInput(ElementBounds.Fixed(42, y, ListW - 42, 24), OnFilterTyped,
                CairoFont.WhiteDetailText(), "filter");

            List<PlayerRosterEntryDto> visible = FilterAndSort(roster);
            if (visible.Count != roster.Length)
                c.AddStaticText(visible.Count + " of " + roster.Length + " shown",
                    CairoFont.WhiteDetailText(), ElementBounds.Fixed(DetailX, y + 4, DetailW, 20));
            y += 26;

            // ---- column headers, clickable to sort ----
            AddSortHeader(c, "Player", SortKey.Name, ElementBounds.Fixed(0, y, 120, 20));
            AddSortHeader(c, "Guides", SortKey.Guides, ElementBounds.Fixed(122, y, 80, 20));
            AddSortHeader(c, "Voxels", SortKey.Voxels, ElementBounds.Fixed(204, y, ListW - 204, 20));
            c.AddStaticText("Details", Accent(), ElementBounds.Fixed(DetailX, y, DetailW, 18));
            y += 20;

            // ---- the scrolling list ----
            ElementBounds clip = ElementBounds.Fixed(0, y, ListW, ListH);
            ElementBounds scrollBounds = ElementBounds.Fixed(ListW + 4, y, ScrollW, ListH);

            // A FIXED-SIZE SCOPE AROUND THE SCROLLING CONTAINER, and it is load-bearing. The dialog sizes
            // itself to its children (ElementSizing.FitToChildren), and the container is as tall as the
            // WHOLE roster — a hundred rows of it if the server has a hundred players. Handed to the dialog
            // directly, that height would be what the dialog sized itself to: a window taller than the
            // screen, almost all of it the empty space behind a clipped list. The scope reports ListH
            // instead, so the dialog is sized by the window and not by its contents.
            //
            // The container still carries its TRUE height, because that is what the composer hit-tests
            // clicks against. Clamping it to the window would make rows unclickable the moment they were
            // scrolled into view, which is worse than a tall dialog.
            ElementBounds listScope = ElementBounds.Fixed(0, y, ListW, ListH);

            // Built and populated BEFORE it is handed to the composer, so Compose() reaches every row. A
            // container filled afterwards would have children that were never composed and never drawn.
            double ry = 0;
            _rows = new GuiElementContainer(capi, ElementBounds.Fixed(0, 0, ListW, 0));
            foreach (PlayerRosterEntryDto p in visible)
            {
                string uid = p.Uid;
                _rows.Add(new PlayerRowElement(capi, ElementBounds.Fixed(0, ry, ListW, RowH), p,
                    uid == _selectedUid, () => SelectPlayer(uid)), -1);
                ry += RowH;
            }
            _rows.Bounds.fixedHeight = ry;

            c.BeginClip(clip);
            c.BeginChildElements(listScope);
            c.AddInteractiveElement(_rows, "rows");
            c.EndChildElements();
            c.EndClip();
            c.AddVerticalScrollbar(OnListScroll, scrollBounds, "scrollbar");

            double detailY = y;
            PlayerRosterEntryDto sel = Find(roster, _selectedUid);
            if (sel == null)
            {
                c.AddStaticText("Select a player to see and change their limits.",
                    CairoFont.WhiteDetailText(), ElementBounds.Fixed(DetailX, detailY, DetailW, 20));
                detailY += 20;
            }
            else
            {
                detailY = _editing
                    ? BuildPlayerEditForm(c, sel, DetailX, detailY)
                    : BuildPlayerDetail(c, sel, DetailX, detailY);
            }

            y = Math.Max(y + ListH, detailY);
        }

        private void AddSortHeader(GuiComposer c, string label, SortKey key, ElementBounds bounds)
        {
            // The active column carries the direction arrow, so the sort is legible without a legend.
            string text = _sort == key ? label + (_descending ? " v" : " ^") : label;
            c.AddInteractiveElement(
                new HeaderElement(capi, bounds, text, _sort == key, () =>
                {
                    // Clicking the active column flips it; a new column starts on the order that column is
                    // usually wanted in — biggest first for the numbers, A-Z for the name.
                    if (_sort == key) _descending = !_descending;
                    else { _sort = key; _descending = key != SortKey.Name; }
                    _scroll = 0;
                    _restoreFilterFocus = false;   // a click's redraw, not the filter's
                    DeferRecompose();
                }),
                "sort" + (int)key);
        }

        private void OnFilterTyped(string text)
        {
            if (_suppress) return;
            _filter = text ?? "";
            _restoreFilterFocus = true;
            // Debounced: a recompose per keystroke would rebuild the whole roster on every letter. 350 ms is
            // long enough that a normal typing run recomposes once at the end of a word.
            DeferRecompose(350);
        }

        private List<PlayerRosterEntryDto> FilterAndSort(PlayerRosterEntryDto[] roster)
        {
            string needle = _filter?.Trim();
            IEnumerable<PlayerRosterEntryDto> q = roster;
            if (!string.IsNullOrEmpty(needle))
                q = q.Where(p => p.Name != null
                    && p.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0);

            // Name is the tie-break in every ordering, so a refresh never reshuffles equal rows.
            switch (_sort)
            {
                case SortKey.Guides:
                    q = _descending
                        ? q.OrderByDescending(p => p.GuideCount).ThenBy(p => p.Name, NameOrder)
                        : q.OrderBy(p => p.GuideCount).ThenBy(p => p.Name, NameOrder);
                    break;
                case SortKey.Voxels:
                    q = _descending
                        ? q.OrderByDescending(p => p.VoxelTotal).ThenBy(p => p.Name, NameOrder)
                        : q.OrderBy(p => p.VoxelTotal).ThenBy(p => p.Name, NameOrder);
                    break;
                default:
                    q = _descending
                        ? q.OrderByDescending(p => p.Name, NameOrder)
                        : q.OrderBy(p => p.Name, NameOrder);
                    break;
            }
            return q.ToList();
        }

        private static readonly StringComparer NameOrder = StringComparer.OrdinalIgnoreCase;

        private void SelectPlayer(string uid)
        {
            _selectedUid = uid;
            _editing = false;
            _confirmUid = null;
            // Cancel any focus hand-back a half-finished keystroke left armed: this redraw was caused by
            // a CLICK, and focus belongs where the click put it. See _restoreFilterFocus.
            _restoreFilterFocus = false;
            _net.RequestPlayerGuides(uid);
            DeferRecompose();
        }

        // ---------------------------------------------------------------------------------
        //  Detail pane — read-only view
        // ---------------------------------------------------------------------------------
        private double BuildPlayerDetail(GuiComposer c, PlayerRosterEntryDto p, double x, double y)
        {
            double w = DialogW - x;
            void Line(string text, CairoFont font)
            {
                c.AddStaticText(text, font, ElementBounds.Fixed(x, y, w, 18));
                y += 18;
            }

            Line(p.Name + (p.Online ? " - online" : " - offline"), CairoFont.WhiteSmallText());
            Line(p.Jailed ? "Public access: JAILED" : "Public access: allowed",
                p.Jailed ? Warn() : CairoFont.WhiteDetailText());

            // EACH LIMIT SAYS WHERE IT CAME FROM (v0.4.31). This replaced the separate "Overridden: ..."
            // block, which was a fixed-height line carrying up to three values, a name and a command, and
            // overflowed the pane. Marking the source inline answers the same question in no extra space —
            // and an effective cap that came from an override behaves quite differently from one that came
            // from the server, because the Admin section's settings do not move it.
            Line("Guides: " + p.GuideCount.ToString("N0") + " of " + Cap(p.EffectiveGuideLimit)
                + Source(p.GuideLimitOverride), CairoFont.WhiteDetailText());
            Line("Voxels: " + p.VoxelTotal.ToString("N0") + " of " + Cap(p.EffectiveTotalVoxelCap)
                + Source(p.TotalVoxelCapOverride), CairoFont.WhiteDetailText());
            Line("Per-guide cap: " + Cap(p.EffectiveVoxelCap) + Source(p.VoxelCapOverride),
                CairoFont.WhiteDetailText());

            y += 4;

            // ---- actions ----
            const double btnW = 90;
            c.AddSmallButton("Edit", () => { BeginEdit(p, Tab.Players); return true; },
                ElementBounds.Fixed(x, y, btnW, 24));

            bool armed = _confirmUid == p.Uid;
            AddJailButtons(c, p, x + btnW + 8, y, armed);
            y += 24;

            if (armed) y = AddJailWarning(c, x, y + 2, w, !p.Jailed);

            y += 6;
            y = BuildGuideList(c, p, x, y, w);
            return y;
        }

        /// <summary>
        /// The Jail/Free button, plus the Cancel that appears beside it once it is armed. Shared by the
        /// Players tab's detail pane and the Jail tab's rows so the two cannot drift apart.
        /// </summary>
        /// <remarks>
        /// TWO-PRESS CONFIRMATION rather than a modal. Jailing cancels the target's in-flight public work,
        /// clears their undo history and interrupts them with a message, so it must not be one stray click
        /// away — but a second dialog over this one to ask a yes/no question is more machinery than the
        /// question deserves. Returns the total width it occupied, so a caller laying out a row can leave
        /// room for it.
        /// </remarks>
        private double AddJailButtons(
            GuiComposer c, PlayerRosterEntryDto p, double x, double y, bool armed)
        {
            const double btnW = 90, confirmW = 130;
            bool jailing = !p.Jailed;
            string uid = p.Uid;

            string label = armed
                ? (jailing ? "Confirm jail" : "Confirm free")
                : (jailing ? "Jail" : "Free");

            c.AddSmallButton(label, () =>
            {
                if (_confirmUid != uid) { _confirmUid = uid; DeferRecompose(); return true; }
                _confirmUid = null;
                _net.SendPlayerJail(uid, jailing);
                return true;
            }, ElementBounds.Fixed(x, y, armed ? confirmW : btnW, 24));

            if (!armed) return btnW;

            c.AddSmallButton("Cancel", () => { _confirmUid = null; DeferRecompose(); return true; },
                ElementBounds.Fixed(x + confirmW + 8, y, btnW, 24));
            return confirmW + 8 + btnW;
        }

        /// <summary>Space the armed pair of buttons needs, for callers that right-align them in a row.</summary>
        private const double JailButtonsWidth = 90;
        private const double JailButtonsArmedWidth = 130 + 8 + 90;

        /// <summary>The warning shown while a jail or free is armed. Its height is MEASURED, not guessed.</summary>
        private double AddJailWarning(GuiComposer c, double x, double y, double w, bool jailing)
        {
            string text = jailing
                ? "Jailing blocks every public guide change, cancels what they are doing right now, and "
                  + "clears their undo history. Their existing guides are NOT removed."
                : "Freeing restores public guide access. Their cap overrides are kept.";
            CairoFont font = Warn();
            double h = TextHeight(text, font, w);
            c.AddStaticText(text, font, ElementBounds.Fixed(x, y, w, h));
            return y + h;
        }

        /// <summary>
        /// Height needed to draw <paramref name="text"/> wrapped to <paramref name="width"/>.
        /// </summary>
        /// <remarks>
        /// MEASURED, NOT GUESSED, and that is the point. A static text wraps inside its bounds but the bounds
        /// never grow to fit it, so a height guessed too small does not clip — it draws straight over
        /// whatever comes next. That is exactly how the jail warning came to overprint the guide list under
        /// it (human-reported, fixed v0.4.32). Every variable-length block in this dialog is sized through
        /// here now, so the same mistake cannot be re-made by eye.
        /// </remarks>
        private double TextHeight(string text, CairoFont font, double width)
        {
            if (string.IsNullOrEmpty(text) || width <= 0) return 0;
            int lines = Math.Max(1,
                capi.Gui.Text.GetQuantityTextLines(font, text, width, EnumLinebreakBehavior.Default));
            return Math.Ceiling(lines * capi.Gui.Text.GetLineHeight(font)) + 2;
        }

        /// <summary>
        /// Opens the edit form on <paramref name="p"/>, seeded with their stored overrides.
        /// </summary>
        /// <remarks>
        /// <paramref name="from"/> is the tab the click came from. The Overrides tab's Edit button hands over
        /// to the Players tab (human-directed) because that is where the form lives; it also CLEARS THE
        /// FILTER, since a filter left over from an earlier search could easily exclude the very player
        /// being sent there and leave the list looking as though the jump had failed.
        /// </remarks>
        private void BeginEdit(PlayerRosterEntryDto p, Tab from)
        {
            _selectedUid = p.Uid;
            _editVoxelCap = p.VoxelCapOverride;
            _editTotalVoxelCap = p.TotalVoxelCapOverride;
            _editGuideLimit = p.GuideLimitOverride;
            _editing = true;
            _confirmUid = null;

            if (from != Tab.Players)
            {
                _tab = Tab.Players;
                _filter = "";
                _restoreFilterFocus = false;
                _scroll = 0;
                // The detail pane lists their guides, and arriving from another tab we may never have asked
                // for them. Safe to ask while the form is open: the guide-list event only redraws.
                _net.RequestPlayerGuides(p.Uid);
            }

            DeferRecompose();
        }

        // Which of a player's limits are the server's and which are theirs alone.
        private static string Source(int overrideValue) => overrideValue > 0 ? "  (override)" : "  (server)";

        private double BuildGuideList(
            GuiComposer c, PlayerRosterEntryDto p, double x, double y, double w)
        {
            void Line(string text, CairoFont font)
            {
                c.AddStaticText(text, font, ElementBounds.Fixed(x, y, w, 18));
                y += 18;
            }

            if (_net.PlayerGuidesUid != p.Uid) { Line("Loading guides...", CairoFont.WhiteDetailText()); return y; }

            PlayerGuideDto[] guides = _net.PlayerGuides;
            if (guides.Length == 0) { Line("No guides.", CairoFont.WhiteDetailText()); return y; }

            Line("Guides (largest first):", Accent());

            // CAPPED AT THE PANE, not at what the server sent. The server lists up to 60, and 60 lines at
            // 18 px is over a thousand pixels of dialog — taller than many screens, and the dialog sizes
            // itself to its children, so it really would grow that far. The list is sorted largest first,
            // so the rows that get dropped are the ones an admin cares least about.
            int shown = Math.Min(guides.Length, MaxListedGuideRows);
            for (int i = 0; i < shown; i++)
            {
                PlayerGuideDto g = guides[i];
                Line("  " + g.ShapeName + " - " + g.VoxelCount.ToString("N0") + " voxels at "
                    + g.X + ", " + g.Y + ", " + g.Z + (g.Hidden ? "  (hidden)" : ""),
                    CairoFont.WhiteDetailText());
            }

            int hidden = _net.PlayerGuidesTotal - shown;
            if (hidden > 0)
                Line("  ...and " + hidden.ToString("N0") + " more.", CairoFont.WhiteDetailText());
            return y;
        }

        /// <summary>How many of the selected player's guides the detail pane lists.</summary>
        private const int MaxListedGuideRows = 12;

        // ---------------------------------------------------------------------------------
        //  Detail pane — edit form
        // ---------------------------------------------------------------------------------
        private double BuildPlayerEditForm(GuiComposer c, PlayerRosterEntryDto p, double x, double y)
        {
            double w = DialogW - x;
            CairoFont font = CairoFont.WhiteDetailText();

            c.AddStaticText("Editing " + p.Name, CairoFont.WhiteSmallText(),
                ElementBounds.Fixed(x, y, w, 20));
            y += 22;

            y = AddEditRow(c, font, x, y, w, "Per-guide voxel cap", "editvoxelcap",
                p.VoxelCapOverride, v => _editVoxelCap = v);
            y = AddEditRow(c, font, x, y, w, "Cumulative voxel cap", "edittotalcap",
                p.TotalVoxelCapOverride, v => _editTotalVoxelCap = v);
            y = AddEditRow(c, font, x, y, w, "Guide limit", "editguidelimit",
                p.GuideLimitOverride, v => _editGuideLimit = v);

            y += 4;
            // Measured, like the jail warning: this is three lines at this width, and the 34 px it used to
            // be reserved would have drawn it straight over the Save row underneath.
            string help =
                "0 clears an override and puts this player back on the server's own limit. Lowering a cap "
                + "never deletes anything - guides already over it stay, they just cannot grow.";
            double helpH = TextHeight(help, font, w);
            c.AddStaticText(help, font, ElementBounds.Fixed(x, y, w, helpH));
            y += helpH + 4;

            const double btnW = 90;
            string uid = p.Uid;
            c.AddSmallButton("Save", () =>
            {
                _net.SendPlayerPolicyEdit(uid, _editVoxelCap, _editTotalVoxelCap, _editGuideLimit);
                // Deliberately NOT closing edit mode here. The arriving roster does that, so the form stays
                // up until the server has actually answered — which is what makes a refused or clamped
                // value visible instead of looking like it was accepted.
                return true;
            }, ElementBounds.Fixed(x, y, btnW, 24));

            c.AddSmallButton("Cancel", () => { _editing = false; DeferRecompose(); return true; },
                ElementBounds.Fixed(x + btnW + 8, y, btnW, 24));

            // One press instead of three commands with 0. Stages like any other edit rather than sending
            // immediately, so it is still the Save press that changes anything.
            c.AddSmallButton("Clear all", () =>
            {
                _editVoxelCap = _editTotalVoxelCap = _editGuideLimit = 0;
                DeferRecompose();
                return true;
            }, ElementBounds.Fixed(x + 2 * (btnW + 8), y, btnW, 24));
            y += 24;

            return y;
        }

        private double AddEditRow(
            GuiComposer c, CairoFont font, double x, double y, double w,
            string label, string key, int serverValue, Action<int> store)
        {
            const double fieldW = 130, fieldH = 26;
            c.AddStaticText(label, font, ElementBounds.Fixed(x, y + 4, w - fieldW - 8, 20));
            c.AddNumberInput(ElementBounds.Fixed(x + w - fieldW, y, fieldW, fieldH),
                text =>
                {
                    if (_suppress) return;
                    // An empty field is someone midway through clearing it, not a request for unlimited;
                    // they have to type the 0. Same rule as the server-settings fields.
                    if (string.IsNullOrWhiteSpace(text)) return;
                    if (!int.TryParse(text.Trim(), out int value)) return;
                    if (value < 0)
                    {
                        value = 0;
                        _suppress = true;
                        try { SingleComposer?.GetNumberInput(key)?.SetValue("0"); }
                        finally { _suppress = false; }
                    }
                    store(value);
                },
                font, key);
            return y + fieldH + 4;
        }

        // ---------------------------------------------------------------------------------
        //  Overrides / Jail tabs — filtered views of the same roster
        // ---------------------------------------------------------------------------------
        private void BuildOverridesTab(GuiComposer c, PlayerRosterEntryDto[] roster, ref double y)
        {
            var withOverride = new List<PlayerRosterEntryDto>();
            foreach (PlayerRosterEntryDto p in roster) if (p.HasOverride) withOverride.Add(p);

            if (withOverride.Count == 0)
            {
                c.AddStaticText("No player has an override. Everyone is on the server's own limits.",
                    CairoFont.WhiteDetailText(), ElementBounds.Fixed(0, y, DialogW, 20));
                y += 20;
                return;
            }

            // The override text stops short of the button column so the two never collide.
            const double editW = 70;
            double textW = DialogW - 192 - editW - 10;

            c.AddStaticText("Player", Accent(), ElementBounds.Fixed(0, y, 180, 18));
            c.AddStaticText("Override", Accent(), ElementBounds.Fixed(192, y, textW, 18));
            y += 20;

            int shown = Math.Min(withOverride.Count, MaxListedRows);
            for (int i = 0; i < shown; i++)
            {
                PlayerRosterEntryDto p = withOverride[i];
                c.AddStaticText(p.Name + (p.Online ? "" : "  (offline)"),
                    CairoFont.WhiteDetailText(), ElementBounds.Fixed(0, y, 180, 18));
                c.AddStaticText(DescribeOverrides(p), Warn(),
                    ElementBounds.Fixed(192, y, textW, 18));

                // Edit sends you to the Players tab with this player's fields already open (human-directed).
                // The form is not duplicated here: it needs the room the detail pane has, and having two
                // places to type a cap into would mean two staging states to keep honest.
                PlayerRosterEntryDto row = p;
                c.AddSmallButton("Edit", () => { BeginEdit(row, Tab.Overrides); return true; },
                    ElementBounds.Fixed(DialogW - editW, y - 2, editW, 22));
                y += RowH;
            }
            y = AddOverflowNote(c, y, withOverride.Count, shown);
        }

        private void BuildJailTab(GuiComposer c, PlayerRosterEntryDto[] roster, ref double y)
        {
            var jailed = new List<PlayerRosterEntryDto>();
            foreach (PlayerRosterEntryDto p in roster) if (p.Jailed) jailed.Add(p);

            if (jailed.Count == 0)
            {
                c.AddStaticText("Nobody is jailed.", CairoFont.WhiteDetailText(),
                    ElementBounds.Fixed(0, y, DialogW, 20));
                y += 20;
                return;
            }

            // Room for the widest state the button column can reach, so an armed row does not shove the
            // text of the rows around it.
            double textW = DialogW - 192 - JailButtonsArmedWidth - 10;

            c.AddStaticText("Player", Accent(), ElementBounds.Fixed(0, y, 180, 18));
            c.AddStaticText("Guides still standing", Accent(), ElementBounds.Fixed(192, y, textW, 18));
            y += 20;

            int shown = Math.Min(jailed.Count, MaxListedRows);
            for (int i = 0; i < shown; i++)
            {
                PlayerRosterEntryDto p = jailed[i];
                bool armed = _confirmUid == p.Uid;

                c.AddStaticText(p.Name + (p.Online ? "" : "  (offline)"),
                    CairoFont.WhiteDetailText(), ElementBounds.Fixed(0, y, 180, 18));
                // Jailing stops new changes; it never removes what is already built, so the count is the
                // thing an admin actually wants next.
                c.AddStaticText(
                    p.GuideCount.ToString("N0") + " guides, " + p.VoxelTotal.ToString("N0") + " voxels",
                    CairoFont.WhiteDetailText(), ElementBounds.Fixed(192, y, textW, 18));

                // Freeing from here STAYS HERE (human-directed) — unlike the Overrides tab's Edit, which has
                // to hand over to the form on the Players tab. The confirmation and the warning are the same
                // ones the detail pane uses, via the same two helpers, so the two routes cannot diverge.
                // Right-aligned to the widest armed layout, so the button does not move when it arms.
                double bx = DialogW - (armed ? JailButtonsArmedWidth : JailButtonsWidth);
                AddJailButtons(c, p, bx, y - 2, armed);
                y += RowH;

                if (armed) y = AddJailWarning(c, 0, y, DialogW, false) + 4;
            }
            y = AddOverflowNote(c, y, jailed.Count, shown);
        }

        // These two tabs draw straight down the dialog with no clip, so an unbounded list would grow the
        // window off the top and bottom of the screen with no way to reach either end.
        private double AddOverflowNote(GuiComposer c, double y, int total, int shown)
        {
            if (total <= shown) return y;
            c.AddStaticText(
                "...and " + (total - shown) + " more. Use the Players tab's filter, or /layout info <name>.",
                CairoFont.WhiteDetailText(), ElementBounds.Fixed(0, y, DialogW, 18));
            return y + 18;
        }

        // ---------------------------------------------------------------------------------

        private static PlayerRosterEntryDto Find(PlayerRosterEntryDto[] roster, string uid)
        {
            if (string.IsNullOrEmpty(uid)) return null;
            foreach (PlayerRosterEntryDto p in roster) if (p.Uid == uid) return p;
            return null;
        }

        private static string Cap(long cap) => cap > 0 ? cap.ToString("N0") : "unlimited";

        private static string DescribeOverrides(PlayerRosterEntryDto p)
        {
            var parts = new List<string>(3);
            if (p.VoxelCapOverride > 0)
                parts.Add(p.VoxelCapOverride.ToString("N0") + " per guide");
            if (p.TotalVoxelCapOverride > 0)
                parts.Add(p.TotalVoxelCapOverride.ToString("N0") + " voxels total");
            if (p.GuideLimitOverride > 0)
                parts.Add(p.GuideLimitOverride.ToString("N0") + " guides");
            return string.Join(", ", parts);
        }

        private static CairoFont Accent()
        {
            CairoFont f = CairoFont.WhiteDetailText();
            f.Color = new double[] { 1, 0.85, 0.55, 1 };
            return f;
        }

        private static CairoFont Warn()
        {
            CairoFont f = CairoFont.WhiteDetailText();
            f.Color = new double[] { 1, 0.55, 0.30, 1 };
            return f;
        }

        public override void Dispose()
        {
            base.Dispose();
            _net.PlayerRosterChanged -= OnRosterChanged;
            _net.PlayerGuidesChanged -= OnGuidesChanged;
        }

        // =================================================================================
        //  Custom elements
        // =================================================================================

        /// <summary>
        /// One roster row: name, guide count and voxel total at fixed column offsets, with the selected row
        /// highlighted.
        /// </summary>
        /// <remarks>
        /// A CUSTOM ELEMENT RATHER THAN A BUTTON, because the columns have to line up. A button centres its
        /// one label, so a three-column row built from buttons would have its numbers wandering with the
        /// length of each name — which would make the sortable headers above it meaningless.
        ///
        /// DRAWN STRAIGHT INTO THE DIALOG'S OWN SURFACE, with no LoadedTexture of its own. Everything the
        /// row shows is fixed for the life of the composition — selecting a different player recomposes —
        /// so there is nothing to update per frame, and this sidesteps the texture-allocation trap that
        /// crashed v0.4.4 entirely.
        /// </remarks>
        private sealed class PlayerRowElement : GuiElement
        {
            private readonly PlayerRosterEntryDto _p;
            private readonly bool _selected;
            private readonly Action _onClick;

            public PlayerRowElement(ICoreClientAPI capi, ElementBounds bounds,
                PlayerRosterEntryDto p, bool selected, Action onClick) : base(capi, bounds)
            {
                _p = p;
                _selected = selected;
                _onClick = onClick;
                // The cursor is the hover feedback here: with the row drawn into the static surface there
                // is no per-frame pass to highlight it in, and a cursor change costs nothing.
                MouseOverCursor = "linkselect";
            }

            public override void ComposeElements(Context ctx, ImageSurface surface)
            {
                Bounds.CalcWorldBounds();

                double x = Bounds.drawX, y = Bounds.drawY;
                double w = Bounds.InnerWidth, h = Bounds.InnerHeight;

                if (_selected)
                {
                    ctx.SetSourceRGBA(1, 0.85, 0.55, 0.20);
                    ctx.Rectangle(x, y, w, h);
                    ctx.Fill();
                }

                CairoFont font = CairoFont.WhiteDetailText();
                // Jailed reads orange and offline reads dim, so the two exceptional states are visible in
                // the list itself rather than only after selecting somebody.
                double[] nameColor =
                    _p.Jailed ? new double[] { 1, 0.55, 0.30, 1 }
                    : _p.Online ? new double[] { 1, 1, 1, 1 }
                    : new double[] { 1, 1, 1, 0.55 };

                font.SetupContext(ctx);
                FontExtents fe = ctx.FontExtents;
                double baseline = y + (h - fe.Height) / 2 + fe.Ascent;

                // Clipped to its column — see ColNameWidth. Save/Restore around it so the two right-aligned
                // numbers below are drawn against the row's own bounds, not through this clip.
                ctx.Save();
                ctx.Rectangle(x + ColNameX, y, ColNameWidth, h);
                ctx.Clip();
                Left(ctx, _p.Name ?? "Unknown", x + ColNameX, baseline, nameColor);
                ctx.Restore();

                // An override is marked in the list too: it is the reason a player's numbers may not match
                // the server settings an admin is looking at on the other panel.
                Right(ctx, _p.GuideCount.ToString("N0") + (_p.HasOverride ? " *" : ""),
                    x + ColGuidesRight, baseline, nameColor);
                Right(ctx, ShortVoxels(_p.VoxelTotal), x + ColVoxelsRight, baseline, nameColor);
            }

            private static void Left(Context ctx, string text, double x, double baseline, double[] rgba)
            {
                ctx.SetSourceRGBA(rgba[0], rgba[1], rgba[2], rgba[3]);
                ctx.MoveTo(x, baseline);
                ctx.ShowText(text ?? "");
            }

            private static void Right(Context ctx, string text, double right, double baseline, double[] rgba)
            {
                TextExtents te = ctx.TextExtents(text ?? "");
                ctx.SetSourceRGBA(rgba[0], rgba[1], rgba[2], rgba[3]);
                ctx.MoveTo(right - te.Width - te.XBearing, baseline);
                ctx.ShowText(text ?? "");
            }

            /// <summary>Voxel totals run to eight digits, which no 96 px column will hold — so they are abbreviated.</summary>
            private static string ShortVoxels(long v)
            {
                if (v >= 10_000_000) return (v / 1_000_000.0).ToString("0") + "M";
                if (v >= 1_000_000) return (v / 1_000_000.0).ToString("0.0") + "M";
                if (v >= 10_000) return (v / 1_000.0).ToString("0") + "k";
                return v.ToString("N0");
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

        /// <summary>A clickable column header. Accented while it is the column being sorted on.</summary>
        private sealed class HeaderElement : GuiElement
        {
            private readonly string _text;
            private readonly bool _active;
            private readonly Action _onClick;

            public HeaderElement(ICoreClientAPI capi, ElementBounds bounds, string text, bool active,
                Action onClick) : base(capi, bounds)
            {
                _text = text;
                _active = active;
                _onClick = onClick;
                MouseOverCursor = "linkselect";
            }

            public override void ComposeElements(Context ctx, ImageSurface surface)
            {
                Bounds.CalcWorldBounds();
                CairoFont font = CairoFont.WhiteDetailText();
                font.SetupContext(ctx);
                FontExtents fe = ctx.FontExtents;
                ctx.SetSourceRGBA(_active ? 1 : 0.78, _active ? 0.85 : 0.78, _active ? 0.55 : 0.78, 1);
                ctx.MoveTo(Bounds.drawX, Bounds.drawY + (Bounds.InnerHeight - fe.Height) / 2 + fe.Ascent);
                ctx.ShowText(_text ?? "");
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
    }
}
