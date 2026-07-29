using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Layout.Network;

namespace Layout.UI
{
    /// <summary>
    /// The admin Players dialog (v0.4.26): who Layout knows about, what limits they carry, and who is
    /// jailed. Opened from the Players button in the settings page's Admin section.
    /// </summary>
    /// <remarks>
    /// THREE TABS OVER ONE ROSTER. Players, Overrides and Jail are three filters of the same server-built
    /// list, not three separate queries — fetching them independently would let the three views disagree
    /// about someone whose policy changed between requests. Tab order is Players, Overrides, Jail
    /// (human-directed): the general case first, then the two exception lists.
    ///
    /// READ-ONLY, deliberately for now. It answers "who has what", which is the question the /layout info,
    /// jailroster and top commands answer in prose and which a table answers far better. Changing a
    /// player's limits still goes through those commands; adding buttons here would mean building
    /// confirmation and undo affordances for destructive per-player actions, which was not asked for.
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

        private Tab _tab = Tab.Players;
        private string _selectedUid;
        private bool _suppress;
        private bool _recomposePending;

        /// <summary>
        /// Rows drawn per tab before the rest are summarised. The dialog auto-sizes to its children and
        /// has no scroll pane, so an unbounded list on a long-running server would grow the window off the
        /// top and bottom of the screen with no way to reach either end.
        /// </summary>
        private const int MaxListedRows = 30;

        public GuidePlayersDialog(ICoreClientAPI capi, ClientNetworkHandler net) : base(capi)
        {
            _net = net ?? throw new ArgumentNullException(nameof(net));
            _net.PlayerRosterChanged += OnDataChanged;
            _net.PlayerGuidesChanged += OnDataChanged;
        }

        public override string ToggleKeyCombinationCode => null;

        public override bool PrefersUngrabbedMouse => true;

        public override void OnGuiOpened()
        {
            base.OnGuiOpened();
            _tab = Tab.Players;
            _selectedUid = null;
            // Always re-fetch on open: a roster cached from ten minutes ago would show stale guide counts
            // and a since-freed player still in the Jail tab.
            _net.RequestPlayerRoster();
            Compose();
        }

        private void OnDataChanged()
        {
            if (IsOpened()) DeferRecompose();
        }

        // Never recompose from inside a composer callback — the same rule and the same delay the tool
        // panel follows, so a click that changes tabs is not rebuilding elements the click is still in.
        private void DeferRecompose()
        {
            if (_recomposePending) return;
            _recomposePending = true;
            capi.Event.RegisterCallback(_ =>
            {
                _recomposePending = false;
                if (IsOpened()) Compose();
            }, 30);
        }

        // ---------------------------------------------------------------------------------
        //  Layout
        // ---------------------------------------------------------------------------------

        private const double DialogW = 620;
        private const double RowH = 22;
        private const double TabW = 110, TabH = 26;

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
                string key = "tab" + i;
                c.AddToggleButton(TabNames[i], CairoFont.WhiteSmallText(),
                    on =>
                    {
                        if (_suppress || !on) return;
                        _tab = tab;
                        DeferRecompose();
                    },
                    ElementBounds.Fixed(i * (TabW + 4), y, TabW, TabH), key);
            }
            y += TabH + 6;

            c.AddInset(ElementBounds.Fixed(0, y, DialogW, 1), 1, 0.4f);
            y += 1 + 6;

            PlayerRosterEntryDto[] roster = _net.PlayerRoster;
            if (roster == null || roster.Length == 0)
            {
                c.AddStaticText(
                    _net.ServerLayoutAvailable
                        ? "No players known yet. Nobody has built a guide, been jailed, or been given a limit."
                        : "This world has no Layout server, so there is no player roster.",
                    CairoFont.WhiteDetailText(), ElementBounds.Fixed(0, y, DialogW, 34));
                y += 34;
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
            try { SingleComposer.GetToggleButton("tab" + (int)_tab)?.SetValue(true); }
            finally { _suppress = false; }
        }

        // ---- Players: the roster, with a detail pane for whoever is selected ----
        private void BuildPlayersTab(GuiComposer c, PlayerRosterEntryDto[] roster, ref double y)
        {
            const double listW = 230;
            double headerY = y;
            c.AddStaticText("Player", Accent(), ElementBounds.Fixed(0, headerY, listW, 18));
            c.AddStaticText("Details", Accent(), ElementBounds.Fixed(listW + 12, headerY, DialogW - listW - 12, 18));
            y += 20;

            double rowY = y;
            int shown = Math.Min(roster.Length, MaxListedRows);
            for (int i = 0; i < shown; i++)
            {
                PlayerRosterEntryDto p = roster[i];
                string uid = p.Uid;
                string label = p.Name + (p.Online ? "" : "  (offline)");
                c.AddSmallButton(label,
                    () => { _selectedUid = uid; _net.RequestPlayerGuides(uid); DeferRecompose(); return true; },
                    ElementBounds.Fixed(0, rowY, listW, RowH));
                rowY += RowH + 2;
            }
            if (roster.Length > shown)
            {
                c.AddStaticText("...and " + (roster.Length - shown) + " more (use /layout info <name>)",
                    CairoFont.WhiteDetailText(), ElementBounds.Fixed(0, rowY, listW, 18));
                rowY += 18;
            }

            double detailY = y;
            PlayerRosterEntryDto sel = Find(roster, _selectedUid);
            if (sel == null)
            {
                c.AddStaticText("Select a player to see their limits and guides.",
                    CairoFont.WhiteDetailText(),
                    ElementBounds.Fixed(listW + 12, detailY, DialogW - listW - 12, 20));
                detailY += 20;
            }
            else
            {
                detailY = BuildPlayerDetail(c, sel, listW + 12, detailY);
            }

            y = Math.Max(rowY, detailY);
        }

        private double BuildPlayerDetail(
            GuiComposer c, PlayerRosterEntryDto p, double x, double y)
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
            Line("Guides: " + p.GuideCount.ToString("N0") + " of " + Cap(p.EffectiveGuideLimit),
                CairoFont.WhiteDetailText());
            Line("Voxels: " + p.VoxelTotal.ToString("N0") + " of " + Cap(p.EffectiveTotalVoxelCap),
                CairoFont.WhiteDetailText());
            Line("Per-guide cap: " + Cap(p.EffectiveVoxelCap), CairoFont.WhiteDetailText());

            // Overrides are called out rather than folded into the numbers above, because an effective cap
            // that came from an override behaves quite differently from one that came from the server: the
            // admin panel's settings do not move it.
            if (p.HasOverride)
                Line("Overridden: " + DescribeOverrides(p) + " (clear with /layout voxelcap "
                    + p.Name + " 0)", Warn());

            y += 6;
            if (_net.PlayerGuidesUid == p.Uid)
            {
                PlayerGuideDto[] guides = _net.PlayerGuides;
                if (guides.Length == 0)
                {
                    Line("No guides.", CairoFont.WhiteDetailText());
                }
                else
                {
                    Line("Guides (largest first):", Accent());
                    for (int i = 0; i < guides.Length; i++)
                    {
                        PlayerGuideDto g = guides[i];
                        Line("  " + g.ShapeName + " - " + g.VoxelCount.ToString("N0") + " voxels at "
                            + g.X + ", " + g.Y + ", " + g.Z + (g.Hidden ? "  (hidden)" : ""),
                            CairoFont.WhiteDetailText());
                    }
                    if (_net.PlayerGuidesTotal > guides.Length)
                        Line("  ...and " + (_net.PlayerGuidesTotal - guides.Length).ToString("N0")
                            + " more.", CairoFont.WhiteDetailText());
                }
            }
            else
            {
                Line("Loading guides...", CairoFont.WhiteDetailText());
            }

            return y;
        }

        // ---- Overrides: only players whose limits differ from the server's ----
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

            c.AddStaticText("Player", Accent(), ElementBounds.Fixed(0, y, 180, 18));
            c.AddStaticText("Override", Accent(), ElementBounds.Fixed(192, y, DialogW - 192, 18));
            y += 20;

            foreach (PlayerRosterEntryDto p in withOverride)
            {
                c.AddStaticText(p.Name + (p.Online ? "" : "  (offline)"),
                    CairoFont.WhiteDetailText(), ElementBounds.Fixed(0, y, 180, 18));
                c.AddStaticText(DescribeOverrides(p), Warn(),
                    ElementBounds.Fixed(192, y, DialogW - 192, 18));
                y += RowH;
            }
        }

        // ---- Jail: who is currently suspended from public guide changes ----
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

            c.AddStaticText("Player", Accent(), ElementBounds.Fixed(0, y, 180, 18));
            c.AddStaticText("Guides still standing", Accent(),
                ElementBounds.Fixed(192, y, DialogW - 192, 18));
            y += 20;

            foreach (PlayerRosterEntryDto p in jailed)
            {
                c.AddStaticText(p.Name + (p.Online ? "" : "  (offline)"),
                    CairoFont.WhiteDetailText(), ElementBounds.Fixed(0, y, 180, 18));
                // Jailing stops new changes; it never removes what is already built, so the count is the
                // thing an admin actually wants next.
                c.AddStaticText(
                    p.GuideCount.ToString("N0") + " guides, " + p.VoxelTotal.ToString("N0")
                    + " voxels  (free with /layout free " + p.Name + ")",
                    CairoFont.WhiteDetailText(), ElementBounds.Fixed(192, y, DialogW - 192, 18));
                y += RowH;
            }
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
            _net.PlayerRosterChanged -= OnDataChanged;
            _net.PlayerGuidesChanged -= OnDataChanged;
        }
    }
}
