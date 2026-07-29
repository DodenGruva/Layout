using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Layout.Client;
using Layout.Guide;
using Layout.Shapes;
using Layout.Systems;

namespace Layout.Network
{
    /// <summary>
    /// Client side of the Layout protocol: a read-only mirror of server state plus the send methods the held
    /// tool uses to make requests. It receives S→C packets, applies them to the local mirror, and raises
    /// events for the (not-yet-built) renderer and HUD. It never writes server state and makes no
    /// authoritative decision — every change it shows has already been validated and broadcast by the server.
    /// </summary>
    /// <remarks>
    /// THE MIRROR. <see cref="Guides"/> is a <see cref="GuideData"/> per id, an exact copy of the server's
    /// record (the full control-point list, phantoms included, arrives on the wire). Module 5's renderer
    /// reads these, builds a shape over each guide's control-point list, and re-derives phantoms; because the
    /// shape adopts that list by reference, an incremental edit applied here (a moved point, an inserted
    /// point) is reflected the moment the renderer next rebuilds. This handler therefore does no shape math
    /// and depends only on the data model.
    ///
    /// EVENTS. <see cref="GuideAddedOrUpdated"/> fires whenever a guide appears or changes (the renderer
    /// rebuilds that one mesh); <see cref="GuideRemoved"/> when one is dispelled. <see cref="GuidesBulkSynced"/>
    /// fires after a join sync (rebuild everything). Lock state, remote draft anchors, and cap warnings have
    /// their own events for the HUD and tool. All events fire AFTER the mirror is updated, so a handler can
    /// read the new state directly.
    ///
    /// CAPS. <see cref="PerGuideVoxelCap"/> / <see cref="TotalVoxelCap"/> start at the v2 defaults and are
    /// overwritten by the server's real caps on join, so the tool's placement pre-check matches the server.
    /// </remarks>
    public class ClientNetworkHandler
    {
        private readonly ICoreClientAPI _capi;
        private readonly IClientNetworkChannel _channel;
        private LocalGuideAuthority _local;
        private bool _receivedServerBulkSync;
        private bool _preferClientOnly;

        // A "Publish Private Guides" waiting on the server to confirm the switch to public mode.
        private bool _pendingPublish;
        private bool _serverLayoutAvailable;
        private bool _serverAllowsClientOnlyMode;
        // Placement mode answers where NEW guides go. In a mixed world the player can still edit either
        // public or private guides, so undo/redo must follow the authority of the most recent mutation rather
        // than blindly following placement mode. Null means no mutation has occurred in this world yet.
        private bool? _lastMutationWasLocal;

        private readonly Dictionary<Guid, GuideData> _guides = new Dictionary<Guid, GuideData>();
        private readonly HashSet<Guid> _serverGuideIds = new HashSet<Guid>();
        private readonly HashSet<Guid> _localGuideIds = new HashSet<Guid>();
        private readonly Dictionary<Guid, string> _lockHolders = new Dictionary<Guid, string>();
        private readonly Dictionary<string, Vec3d> _remoteDraftAnchors = new Dictionary<string, Vec3d>();

        // Right-click cancel is locally immediate, while already-sent move responses may still be queued.
        // Hold those responses behind a two-part barrier: the authority must restate the exact origin and
        // release our edit lock before normal geometry packets for the guide are accepted again.
        private sealed class PendingGrabCancel
        {
            public GuideData Expected;
            public GuideData Confirmation;
            public bool LockReleased;
        }

        private readonly Dictionary<Guid, PendingGrabCancel> _pendingGrabCancels =
            new Dictionary<Guid, PendingGrabCancel>();

        private int _perGuideVoxelCap = 500000;
        private int _totalVoxelCap = 0;

        /// <summary>The authority selected for the current world.</summary>
        public ClientAuthorityMode AuthorityMode { get; private set; } = ClientAuthorityMode.Detecting;
        public bool ServerLayoutAvailable => _serverLayoutAvailable;
        public bool ServerAllowsClientOnlyMode => _serverAllowsClientOnlyMode;
        public bool PublicGuideAccessJailed { get; private set; }

        // -- Read-only views for the renderer / HUD / tool ----------------------------------------

        /// <summary>The local guide mirror, by id. Treat as read-only; mutate only by sending requests.</summary>
        public IReadOnlyDictionary<Guid, GuideData> Guides => _guides;

        /// <summary>Guide id → the UID currently editing it. A guide absent here is free to grab.</summary>
        public IReadOnlyDictionary<Guid, string> LockHolders => _lockHolders;

        /// <summary>Player UID → their in-progress draft start anchor, for rendering remote anchor dots.</summary>
        public IReadOnlyDictionary<string, Vec3d> RemoteDraftAnchors => _remoteDraftAnchors;

        /// <summary>The active per-guide voxel cap. Use for the placement pre-check.</summary>
        /// <remarks>
        /// PRIVATE GUIDES OBEY THE SERVER'S CAPS TOO (v0.4.22, and a behaviour change — see below). Local
        /// authority used to report the 10 M hard ceiling unconditionally, which meant that on a server
        /// permitting private guides, a player could step around every cap the admin had set by moving one
        /// slider. A 5,000-voxel cap did not stop a 130,000-voxel private guide, and nothing on screen
        /// explained why. That is the same question the chalk system already settled the other way —
        /// "private is private, not free" — so caps now follow the same rule.
        ///
        /// The client-only FALLBACK is deliberately left uncapped: with no Layout server there is nobody to
        /// have set a cap, and inventing one would be worse than having none.
        ///
        /// Honest-client enforcement, exactly like the private chalk charge. The client IS the authority
        /// for its own private guides, so a modified client can ignore this; that is inherent to the
        /// feature, not a hole opened here.
        /// </remarks>
        public int PerGuideVoxelCap => AuthorityMode == ClientAuthorityMode.Local
            ? (_serverLayoutAvailable ? _perGuideVoxelCap : GuideManager.HardVoxelCeiling)
            : _perGuideVoxelCap;

        /// <summary>The active total voxel cap. Zero (unlimited) with no Layout server; see above.</summary>
        public int TotalVoxelCap => AuthorityMode == ClientAuthorityMode.Local
            ? (_serverLayoutAvailable ? _totalVoxelCap : 0)
            : _totalVoxelCap;

        /// <summary>
        /// The live server settings behind the settings page's Admin section (v0.4.16, protocol 20), or
        /// null on a server that has not sent them — a pre-0.4.16 server, or no Layout server at all.
        /// A null here is what hides the section; there is nothing to administer in those worlds.
        /// </summary>
        public LayoutAdminConfigPacket AdminConfig { get; private set; }

        /// <summary>True when the server has told THIS player they may change the settings above.</summary>
        public bool CanEditAdminConfig => AdminConfig != null && AdminConfig.CanEdit;

        /// <summary>Asks the server to change one admin setting. The server re-checks the privilege.</summary>
        /// <remarks>
        /// Gated on whether a Layout SERVER exists, not on the authority mode. Those are different
        /// questions: an admin who has their own placement set to Private is in Local authority while
        /// still connected to a Layout server they can perfectly well administer. Testing the authority
        /// mode here silently swallowed every change such an admin made (the v0.4.16-v0.4.18 bug).
        /// </remarks>
        public void SendAdminConfig(LayoutAdminSetting setting, int value)
        {
            if (!_serverLayoutAvailable) return;   // nobody to ask
            _channel?.SendPacket(new LayoutAdminConfigRequestPacket(setting, value));
        }

        /// <summary>Asks the server to un-hide every guide this player created ("Reveal All").</summary>
        public void SendRevealMine()
        {
            if (!_serverLayoutAvailable) return;
            _channel?.SendPacket(new GuideRevealMinePacket());
        }

        // -- Admin player roster (v0.4.26) ---------------------------------------------------------

        /// <summary>The last roster the server sent, or empty before the first request answers.</summary>
        public PlayerRosterEntryDto[] PlayerRoster { get; private set; } = Array.Empty<PlayerRosterEntryDto>();

        /// <summary>Which player <see cref="PlayerGuides"/> currently describes.</summary>
        public string PlayerGuidesUid { get; private set; }

        /// <summary>The selected player's guides, largest first and capped by the server.</summary>
        public PlayerGuideDto[] PlayerGuides { get; private set; } = Array.Empty<PlayerGuideDto>();

        /// <summary>How many guides that player has in total, which may exceed the listed ones.</summary>
        public int PlayerGuidesTotal { get; private set; }

        /// <summary>The roster arrived; redraw the Players dialog.</summary>
        public event Action PlayerRosterChanged;

        /// <summary>A player's guide list arrived; redraw the Players dialog's detail pane.</summary>
        public event Action PlayerGuidesChanged;

        /// <summary>Asks for the admin player roster. Refused server-side without controlserver.</summary>
        public void RequestPlayerRoster()
        {
            if (!_serverLayoutAvailable) return;
            _channel?.SendPacket(new PlayerRosterRequestPacket());
        }

        /// <summary>Asks for one player's guide list.</summary>
        public void RequestPlayerGuides(string uid)
        {
            if (!_serverLayoutAvailable || string.IsNullOrEmpty(uid)) return;
            _channel?.SendPacket(new PlayerGuidesRequestPacket(uid));
        }

        // v0.2.22: the chalk refill channels moved from server policy to a CLIENT preference, so the two
        // synced flags that used to be mirrored here are gone. Read
        // LayoutModSystem.{Hotbar,Inventory}ChalkRefillAllowed instead — it reads layout-client.json.
        // The matching packet fields remain declared (append-only) but are no longer populated or read.

        public bool IsLocalGuide(Guid id) => _localGuideIds.Contains(id);

        // -- Events --------------------------------------------------------------------------------

        /// <summary>A guide appeared or changed; the argument is the live mirror record. Rebuild its mesh.</summary>
        public event Action<GuideData> GuideAddedOrUpdated;

        /// <summary>Display-only Last Sculptor/count/dimension metadata changed; no mesh rebuild is needed.</summary>
        public event Action<Guid> GuideHudMetadataChanged;

        /// <summary>A guide was removed; the argument is its id. Drop its mesh.</summary>
        public event Action<Guid> GuideRemoved;

        /// <summary>A full bulk sync was applied (e.g. on join). Rebuild everything.</summary>
        public event Action GuidesBulkSynced;

        /// <summary>A guide's lock state changed: (guide id, holder UID or null when free).</summary>
        public event Action<Guid, string> LockStateChanged;

        /// <summary>A remote player's draft anchor appeared or moved: (player UID, start position).</summary>
        public event Action<string, Vec3d> RemoteDraftAnchorChanged;

        /// <summary>A remote player's draft anchor was removed: (player UID).</summary>
        public event Action<string> RemoteDraftAnchorRemoved;

        /// <summary>The server's admin settings arrived or changed; redraw the settings page if it is open.</summary>
        public event Action AdminConfigChanged;

        /// <summary>A mutation was refused for exceeding a cap: (guide id, attempted count, cap).</summary>
        public event Action<Guid, int, int> VoxelCapWarningReceived;

        /// <summary>Raised after the current world's authority becomes known or is reset.</summary>
        public event Action<ClientAuthorityMode> AuthorityModeChanged;

        /// <summary>Raised when an explicit server command changes the persisted client preference.</summary>
        public event Action<bool> ForceClientOnlyPreferenceChanged;

        /// <summary>Raised when the server-side /layout who command asks this client to inspect its target.</summary>
        public event Action GuideWhoRequested;

        /// <summary>The server changed this player's effective per-guide cap at runtime.</summary>
        public event Action<bool> PublicGuidePolicyChanged;

        /// <summary>The authority rejected the player's provisional final placement.</summary>
        public event Action PlacementRejected;

        /// <summary>A personal command enabled or disabled every Layout render pass.</summary>
        public event Action<bool> GuideRenderingChanged;

        public ClientNetworkHandler(ICoreClientAPI capi)
        {
            _capi = capi ?? throw new ArgumentNullException(nameof(capi));

            _channel = _capi.Network.RegisterChannel(LayoutChannel.Name);
            LayoutPackets.RegisterMessageTypes(_channel);

            _channel
                .SetMessageHandler<GuideBulkSyncPacket>(OnNetworkBulkSync)
                .SetMessageHandler<GuideCreatePacket>(OnServerCreate)
                .SetMessageHandler<GuideUpdatePacket>(p => ApplyServerGuidePacket(p.GuideId(), p, OnUpdate))
                .SetMessageHandler<GuideInsertPointPacket>(p => ApplyServerGuidePacket(p.GuideId(), p, OnInsert))
                .SetMessageHandler<GuideDeletePacket>(OnServerDelete)
                .SetMessageHandler<GuideHidePacket>(p => ApplyServerGuidePacket(p.GuideId(), p, OnHide))
                .SetMessageHandler<GuideLockPointPacket>(p => ApplyServerGuidePacket(p.GuideId(), p, OnLockPoint))
                .SetMessageHandler<GuideRescalePacket>(p => ApplyServerGuidePacket(p.GuideId(), p, OnRescale))
                .SetMessageHandler<GuideSetProjectionPacket>(p => ApplyServerGuidePacket(p.GuideId(), p, OnSetProjection))
                .SetMessageHandler<GuideSetFilledPacket>(p => ApplyServerGuidePacket(p.GuideId(), p, OnSetFilled))
                .SetMessageHandler<GuideSetDivisionsPacket>(p => ApplyServerGuidePacket(p.GuideId(), p, OnSetDivisions))
                .SetMessageHandler<GuideSetSidesPacket>(p => ApplyServerGuidePacket(p.GuideId(), p, OnSetSides))
                .SetMessageHandler<GuideSetWireframePacket>(
                    p => ApplyServerGuidePacket(p.GuideId(), p, OnSetWireframe))
                .SetMessageHandler<GuideHudMetadataPacket>(
                    p => ApplyServerGuidePacket(p.GuideId(), p, OnHudMetadata))
                .SetMessageHandler<GuideLockStatePacket>(OnLockState)
                .SetMessageHandler<DraftAnchorBroadcastPacket>(OnDraftAnchorBroadcast)
                .SetMessageHandler<DraftAnchorRemovePacket>(OnDraftAnchorRemove)
                .SetMessageHandler<VoxelCapWarningPacket>(OnCapWarning)
                .SetMessageHandler<ClientPlacementModePacket>(OnPlacementMode)
                .SetMessageHandler<ClientGuidePushRequestPacket>(OnGuidePushRequest)
                .SetMessageHandler<ClientGuidePushResultPacket>(OnGuidePushResult)
                .SetMessageHandler<GuideWhoQueryPacket>(_ => GuideWhoRequested?.Invoke())
                .SetMessageHandler<PlayerGuidePolicyPacket>(OnPlayerGuidePolicy)
                .SetMessageHandler<GuidePlacementRejectedPacket>(_ => PlacementRejected?.Invoke())
                .SetMessageHandler<GuideRenderingPacket>(
                    packet => GuideRenderingChanged?.Invoke(packet?.Enabled ?? true))
                .SetMessageHandler<LayoutAdminConfigPacket>(OnAdminConfig)
                .SetMessageHandler<PlayerRosterPacket>(OnPlayerRoster)
                .SetMessageHandler<PlayerGuidesPacket>(OnPlayerGuides);
        }

        private void OnPlayerRoster(PlayerRosterPacket packet)
        {
            PlayerRoster = packet?.Players ?? Array.Empty<PlayerRosterEntryDto>();
            PlayerRosterChanged?.Invoke();
        }

        private void OnPlayerGuides(PlayerGuidesPacket packet)
        {
            if (packet == null) return;
            PlayerGuidesUid = packet.Uid;
            PlayerGuides = packet.Guides ?? Array.Empty<PlayerGuideDto>();
            PlayerGuidesTotal = packet.TotalCount;
            PlayerGuidesChanged?.Invoke();
        }

        private void OnAdminConfig(LayoutAdminConfigPacket packet)
        {
            if (packet == null) return;
            AdminConfig = packet;
            // The server's private-guide policy also arrives here, so an admin flipping it mid-session
            // reaches every client's panel rather than only the players who reconnect afterwards.
            _serverAllowsClientOnlyMode = packet.AllowClientOnlyMode;
            // This packet is where the client learns the per-PLAYER total cap, so a change to it has to
            // reach the private-guide store as well as the panel.
            PushServerCapsToLocalAuthority();
            AdminConfigChanged?.Invoke();
        }

        private void OnPlayerGuidePolicy(PlayerGuidePolicyPacket packet)
        {
            if (packet == null) return;
            _perGuideVoxelCap = Math.Max(0, packet.PerGuideVoxelCap);
            PushServerCapsToLocalAuthority();
            PublicGuideAccessJailed = packet.Jailed;
            PublicGuidePolicyChanged?.Invoke(packet.Jailed);
        }

        // Keeps the private-guide store's caps in step with the server's. Only meaningful while a Layout
        // server is present; the no-server fallback is deliberately left unlimited (see PerGuideVoxelCap).
        private void PushServerCapsToLocalAuthority()
        {
            if (_local == null || !_serverLayoutAvailable) return;
            // The per-PLAYER total is not part of the join-time cap sync; it rides on the admin-settings
            // packet, which every player receives. Absent (a pre-0.4.16 server) it stays unlimited.
            _local.ApplyServerCaps(
                _perGuideVoxelCap, _totalVoxelCap, AdminConfig?.PerPlayerTotalVoxelCap ?? 0);
        }

        /// <summary>
        /// Resolves the current world's authority. A real bulk sync is the positive proof that the server
        /// runs Layout; channel state alone is not reliable on every game/server combination.
        /// </summary>
        public bool TryResolveAuthorityMode(bool allowLocalFallback = false)
        {
            EnumChannelState state = _capi.Network.GetChannelState(LayoutChannel.Name);

            if (_receivedServerBulkSync)
            {
                _capi.Logger.Notification(
                    "[Layout] Authority probe: channel state {0}, connected {1}, server sync received.",
                    state, _channel.Connected);
                SetAuthorityMode(ClientAuthorityMode.Networked);
                return true;
            }

            if (state == EnumChannelState.Registered && !allowLocalFallback)
            {
                SetAuthorityMode(ClientAuthorityMode.Detecting);
                return false;
            }

            if (state == EnumChannelState.NotFound
                || state == EnumChannelState.NotConnected
                || !_channel.Connected)
            {
                _capi.Logger.Notification(
                    "[Layout] Authority probe: channel state {0}, connected {1}, no server sync; using client-only mode.",
                    state, _channel.Connected);
                _serverLayoutAvailable = false;
                _serverAllowsClientOnlyMode = true;
                EnsureLocalAuthority();
                SetAuthorityMode(ClientAuthorityMode.Local);
                return true;
            }

            if (!allowLocalFallback)
            {
                SetAuthorityMode(ClientAuthorityMode.Detecting);
                return false;
            }

            _capi.Logger.Warning(
                "[Layout] Channel claimed to be connected but no Layout server sync arrived; using client-only mode.");
            _serverLayoutAvailable = false;
            _serverAllowsClientOnlyMode = true;
            EnsureLocalAuthority();
            SetAuthorityMode(ClientAuthorityMode.Local);
            return true;
        }

        /// <summary>The client PREFERS private, client-stored guides. This is the saved preference, not the
        /// mode currently in force — a server may refuse it (see <see cref="AuthorityMode"/>).</summary>
        public bool PrefersClientOnly => _preferClientOnly;

        /// <summary>
        /// Changes the private-guide preference IN PLAY (v0.4.2, the settings page). Until now this was
        /// file-only: <see cref="ResetAuthorityMode"/> read it once at client start and nothing could move
        /// it afterwards.
        /// </summary>
        /// <remarks>
        /// The preference is saved unconditionally because it is a CROSS-SERVER preference. If this
        /// particular server refuses private guides, its reply switches the active mode back and prints the
        /// refusal, but the preference must survive — reverting it here would clobber the setting for every
        /// other server the player visits. Off a Layout server there is nothing to ask: the session is
        /// already local and stays local, and the preference simply applies the next time it can.
        /// </remarks>
        public void SetClientOnlyPreference(bool clientOnly)
        {
            if (_preferClientOnly == clientOnly) return;
            _preferClientOnly = clientOnly;
            ForceClientOnlyPreferenceChanged?.Invoke(clientOnly);
            if (_serverLayoutAvailable)
                _channel.SendPacket(new ClientPlacementModeRequestPacket(clientOnly));
        }

        /// <summary>Returns authority selection to neutral without touching a network bulk sync already received.</summary>
        public void ResetAuthorityMode(bool forceClientOnly = false)
        {
            _preferClientOnly = forceClientOnly;
            PublicGuideAccessJailed = false;
            SetAuthorityMode(ClientAuthorityMode.Detecting);
        }

        /// <summary>Ends the current world session and drops every local mirror entry.</summary>
        public void EndWorldSession()
        {
            ResetAuthorityMode(_preferClientOnly);
            _receivedServerBulkSync = false;
            _serverLayoutAvailable = false;
            _serverAllowsClientOnlyMode = false;
            // Dropped with the session: carrying one server's settings into the next world would show the
            // Admin section on a server that never sent one, with somebody else's caps in it.
            AdminConfig = null;
            _pendingPublish = false;
            _lastMutationWasLocal = null;
            _local = null;
            _guides.Clear();
            _serverGuideIds.Clear();
            _localGuideIds.Clear();
            _lockHolders.Clear();
            _remoteDraftAnchors.Clear();
            GuidesBulkSynced?.Invoke();
        }

        /// <summary>Runs the admin-style dispel operation against client-only guides.</summary>
        public int DispelLocalGuides(Vec3d playerPosition, int radius)
        {
            return _local?.DispelGuides(playerPosition, radius) ?? 0;
        }

        private void SetAuthorityMode(ClientAuthorityMode mode)
        {
            if (AuthorityMode == mode) return;
            AuthorityMode = mode;
            AuthorityModeChanged?.Invoke(mode);
        }

        private void EnsureLocalAuthority()
        {
            if (_local != null) return;
            _local = new LocalGuideAuthority(
                _capi,
                OnLocalCreate, OnLocalDelete, OnHide, OnLockPoint, OnRescale,
                OnSetProjection, OnSetFilled, OnSetWireframe, OnSetDivisions, OnSetSides,
                OnHudMetadata, OnLockState, OnCapWarning);
            OnLocalBulkSync(_local.CreateBulkSyncPacket());
        }

        private void RemoveLocalOverlay()
        {
            foreach (Guid id in _localGuideIds) _guides.Remove(id);
            _localGuideIds.Clear();
            _local = null;
            GuidesBulkSynced?.Invoke();
        }

        // ==========================================================================================
        //  Receive: full-state
        // ==========================================================================================

        private void OnNetworkBulkSync(GuideBulkSyncPacket p)
        {
            _receivedServerBulkSync = true;
            _serverLayoutAvailable = true;
            bool supportsClientOnlyPolicy = p?.ProtocolVersion >= 2;
            _serverAllowsClientOnlyMode = supportsClientOnlyPolicy && p.AllowClientOnlyMode;
            OnServerBulkSync(p);

            if (_serverAllowsClientOnlyMode) EnsureLocalAuthority();
            else RemoveLocalOverlay();

            PushServerCapsToLocalAuthority();
            SetAuthorityMode(ClientAuthorityMode.Networked);
            if (supportsClientOnlyPolicy)
            {
                _channel.SendPacket(new ClientPlacementModeRequestPacket(_preferClientOnly));
            }
            else if (_preferClientOnly)
            {
                _capi.ShowChatMessage(
                    "[Layout] This server uses an older Layout protocol and cannot permit client-only mode.");
            }
        }

        private void ApplyServerGuidePacket<T>(Guid id, T packet, Action<T> apply)
        {
            if (_serverGuideIds.Contains(id)) apply(packet);
        }

        private void OnServerBulkSync(GuideBulkSyncPacket p)
        {
            foreach (Guid id in _serverGuideIds) _guides.Remove(id);
            _serverGuideIds.Clear();
            _lockHolders.Clear();
            _remoteDraftAnchors.Clear();

            if (p.Guides != null)
                foreach (var dto in p.Guides)
                {
                    if (dto == null) continue;
                    GuideData g = dto.ToGuideData();
                    _serverGuideIds.Add(g.Id);
                    _guides[g.Id] = g;
                }

            _perGuideVoxelCap = p.PerGuideVoxelCap;
            _totalVoxelCap = p.TotalVoxelCap;
            // p.AllowHotbarChalkRefill / p.AllowInventoryChalkRefill are deliberately ignored (v0.2.22).

            GuidesBulkSynced?.Invoke();
        }

        private void OnLocalBulkSync(GuideBulkSyncPacket p)
        {
            foreach (Guid id in _localGuideIds) _guides.Remove(id);
            _localGuideIds.Clear();
            if (p?.Guides != null)
                foreach (GuideDataDto dto in p.Guides)
                {
                    if (dto == null) continue;
                    GuideData guide = dto.ToGuideData();
                    _localGuideIds.Add(guide.Id);
                    _guides[guide.Id] = guide;
                }
            GuidesBulkSynced?.Invoke();
        }

        private void OnServerCreate(GuideCreatePacket p)
        {
            if (p?.Guide == null) return;
            Guid id = NetIds.ToGuid(p.Guide.IdBytes);
            _serverGuideIds.Add(id);
            OnCreate(p);
        }

        private void OnLocalCreate(GuideCreatePacket p)
        {
            if (p?.Guide == null) return;
            Guid id = NetIds.ToGuid(p.Guide.IdBytes);
            _localGuideIds.Add(id);
            OnCreate(p);
        }

        private void OnServerDelete(GuideDeletePacket p)
        {
            Guid id = p.GuideId();
            if (!_serverGuideIds.Remove(id)) return;
            OnDelete(p);
        }

        private void OnLocalDelete(GuideDeletePacket p)
        {
            _localGuideIds.Remove(p.GuideId());
            OnDelete(p);
        }

        private void OnPlacementMode(ClientPlacementModePacket packet)
        {
            if (packet == null) return;
            if (packet.ClientOnly && packet.Allowed)
            {
                EnsureLocalAuthority();
                SetAuthorityMode(ClientAuthorityMode.Local);
            }
            else
            {
                if (AuthorityMode == ClientAuthorityMode.Local) _local?.CancelActiveDrag();
                SetAuthorityMode(ClientAuthorityMode.Networked);
            }

            if (packet.UpdatePreference && packet.Allowed)
            {
                _preferClientOnly = packet.ClientOnly;
                ForceClientOnlyPreferenceChanged?.Invoke(packet.ClientOnly);
            }

            if (!string.IsNullOrWhiteSpace(packet.Message))
                _capi.ShowChatMessage("[Layout] " + packet.Message);

            // A publish parked by PublishPrivateGuides is released here, once the server has confirmed the
            // player is public — earlier than this it would be rejected. Cleared on ANY answer, so a refused
            // switch cannot leave it armed to fire on some unrelated mode change later.
            if (_pendingPublish)
            {
                _pendingPublish = false;
                if (!packet.ClientOnly && packet.Allowed) SendPrivateGuidePush();
                else _capi.ShowChatMessage("[Layout] Could not switch to public mode, so nothing was published.");
            }
        }

        private void OnGuidePushRequest(ClientGuidePushRequestPacket packet)
        {
            if (AuthorityMode != ClientAuthorityMode.Networked)
            {
                _capi.ShowChatMessage("[Layout] Switch to public mode before pushing private guides (/layout public).");
                return;
            }

            ClientGuidePushDto[] guides = _local?.CreatePushDtos() ?? Array.Empty<ClientGuidePushDto>();
            _channel.SendPacket(new ClientGuidePushPacket(guides));
        }

        /// <summary>How many private guides this client is holding — what "Publish Private Guides" would send.</summary>
        public int PrivateGuideCount => _localGuideIds.Count;

        /// <summary>
        /// Publishes every private guide to the server, going public first if necessary (v0.4.6, the settings
        /// page's "Publish Private Guides" button).
        /// </summary>
        /// <remarks>
        /// The chat route is server-PULLED: <c>/layout client push all</c> refuses unless the player is
        /// already public, then asks the client to upload. This is the same upload with the mode switch
        /// folded in, so the player is not made to run two commands in the right order to get one outcome.
        ///
        /// It needs no new packet and no protocol bump. <c>ClientGuidePushPacket</c> may be sent unprompted —
        /// the server's handler re-validates client-only mode and privilege on receipt regardless of how the
        /// push was started, so the request packet was only ever the server's way of asking, never a
        /// permission token.
        ///
        /// GOING PUBLIC IS A ROUND TRIP, and until the server answers it still has the player in its
        /// client-only set and would reject the upload. So the push is PARKED and sent from
        /// <see cref="OnPlacementMode"/> when the switch is confirmed, never fired optimistically.
        /// </remarks>
        public void PublishPrivateGuides()
        {
            if (!_serverLayoutAvailable)
            {
                _capi.ShowChatMessage("[Layout] There is no Layout server here to publish guides to.");
                return;
            }
            if (PrivateGuideCount == 0)
            {
                _capi.ShowChatMessage("[Layout] You have no private guides to publish.");
                return;
            }

            if (AuthorityMode == ClientAuthorityMode.Networked && !_preferClientOnly)
            {
                SendPrivateGuidePush();
                return;
            }

            // Deliberately NOT routed through SetClientOnlyPreference: that early-returns when the
            // preference already reads public, which would leave the parked push waiting for a reply that
            // was never requested.
            _pendingPublish = true;
            if (_preferClientOnly)
            {
                _preferClientOnly = false;
                ForceClientOnlyPreferenceChanged?.Invoke(false);
            }
            _channel.SendPacket(new ClientPlacementModeRequestPacket(false));
        }

        private void SendPrivateGuidePush()
        {
            ClientGuidePushDto[] guides = _local?.CreatePushDtos() ?? Array.Empty<ClientGuidePushDto>();
            if (guides.Length == 0)
            {
                _capi.ShowChatMessage("[Layout] You have no private guides to publish.");
                return;
            }
            _channel.SendPacket(new ClientGuidePushPacket(guides));
        }

        private void OnGuidePushResult(ClientGuidePushResultPacket packet)
        {
            if (packet == null) return;
            var accepted = new List<Guid>();
            if (packet.AcceptedLocalIdBytes != null)
                foreach (byte[] bytes in packet.AcceptedLocalIdBytes)
                {
                    Guid id = NetIds.ToGuid(bytes);
                    if (id != Guid.Empty) accepted.Add(id);
                }

            _local?.RemovePublishedGuides(accepted);
            if (!string.IsNullOrWhiteSpace(packet.Message))
                _capi.ShowChatMessage("[Layout] " + packet.Message);
        }

        private void OnCreate(GuideCreatePacket p)
        {
            if (p?.Guide == null) return;
            GuideData g = p.Guide.ToGuideData();

            if (_pendingGrabCancels.TryGetValue(g.Id, out PendingGrabCancel pending))
            {
                // Constraint-breaking drags can have older full-state packets in the same backlog as their
                // incremental moves. Only the exact pre-grab render state can confirm cancellation.
                if (SameRenderState(g, pending.Expected))
                {
                    pending.Confirmation = g;
                    TryCompleteGrabCancel(g.Id, pending);
                }
                return;
            }

            _guides[g.Id] = g;                 // upsert: also the resync / undo-broadcast path
            GuideAddedOrUpdated?.Invoke(g);
        }

        private void TryCompleteGrabCancel(Guid id, PendingGrabCancel pending)
        {
            if (pending == null || !pending.LockReleased || pending.Confirmation == null) return;
            if (!_pendingGrabCancels.TryGetValue(id, out PendingGrabCancel current)
                || !ReferenceEquals(current, pending)) return;

            _pendingGrabCancels.Remove(id);
            _guides[id] = pending.Confirmation;
            GuideAddedOrUpdated?.Invoke(pending.Confirmation);
        }

        private static bool SameRenderState(GuideData a, GuideData b) =>
            a != null && b != null && RenderFingerprint(a) == RenderFingerprint(b);

        private static ulong RenderFingerprint(GuideData guide)
        {
            unchecked
            {
                ulong h = 1469598103934665603UL;
                void Mix(long value) { h ^= (ulong)value; h *= 1099511628211UL; }

                Mix((long)guide.ShapeType); Mix((long)guide.Constraint); Mix((long)guide.ShapePlaneAxis);
                Mix(guide.VoxelScale); Mix(guide.IsHidden ? 1 : 0); Mix((long)guide.Projection);
                Mix((long)guide.Plane.FlattenedAxis); Mix(guide.Plane.PlaneOffset);
                Mix(guide.IsFilled ? 1 : 0); Mix(guide.Divisions); Mix(guide.Sides);
                Mix(guide.IsWireframe ? 1 : 0);
                Mix(guide.FlatSideAligned ? 1 : 0); Mix(guide.IsClosed ? 1 : 0);

                List<ControlPoint> points = guide.ControlPoints;
                Mix(points?.Count ?? 0);
                if (points != null)
                {
                    for (int i = 0; i < points.Count; i++)
                    {
                        ControlPoint point = points[i];
                        Vec3d p = point?.WorldPosition;
                        Mix(p == null ? 0 : BitConverter.DoubleToInt64Bits(p.X));
                        Mix(p == null ? 0 : BitConverter.DoubleToInt64Bits(p.Y));
                        Mix(p == null ? 0 : BitConverter.DoubleToInt64Bits(p.Z));
                        int flags = point == null ? 0
                            : (point.IsLocked ? 1 : 0)
                            | (point.IsPhantom ? 2 : 0)
                            | (point.IsAnchor ? 4 : 0)
                            | (point.IsPrimary ? 8 : 0)
                            | (point.IsLockMarker ? 16 : 0);
                        Mix(flags);
                    }
                }
                return h;
            }
        }

        // ==========================================================================================
        //  Receive: incremental
        // ==========================================================================================

        private void OnUpdate(GuideUpdatePacket p)
        {
            if (_pendingGrabCancels.ContainsKey(p.GuideId())) return;
            if (!_guides.TryGetValue(p.GuideId(), out GuideData g) || p.Edits == null) return;

            foreach (var edit in p.Edits)
            {
                if (edit?.Position == null) continue;
                if (edit.Index < 0 || edit.Index >= g.ControlPoints.Count) continue;
                Vec3d pos = edit.Position.ToVec3d();
                g.ControlPoints[edit.Index].SetPosition(pos.X, pos.Y, pos.Z);
            }
            // Phantoms are stale until the renderer rebuilds the shape and re-derives them — harmless, as
            // nothing reads phantom positions before then.
            GuideAddedOrUpdated?.Invoke(g);
        }

        private void OnInsert(GuideInsertPointPacket p)
        {
            if (!_guides.TryGetValue(p.GuideId(), out GuideData g) || p.Position == null) return;

            var cp = new ControlPoint(p.Position.ToVec3d());   // a plain body point (no role flags)
            int idx = p.Index;
            if (idx < 0 || idx > g.ControlPoints.Count) g.ControlPoints.Add(cp);
            else g.ControlPoints.Insert(idx, cp);

            GuideAddedOrUpdated?.Invoke(g);
        }

        private void OnDelete(GuideDeletePacket p)
        {
            Guid id = p.GuideId();
            _pendingGrabCancels.Remove(id);
            bool removed = _guides.Remove(id);
            _lockHolders.Remove(id);
            if (removed) GuideRemoved?.Invoke(id);
        }

        private void OnHide(GuideHidePacket p)
        {
            if (!_guides.TryGetValue(p.GuideId(), out GuideData g)) return;
            g.IsHidden = p.Hidden;
            GuideAddedOrUpdated?.Invoke(g);
        }

        private void OnLockPoint(GuideLockPointPacket p)
        {
            if (!_guides.TryGetValue(p.GuideId(), out GuideData g)) return;
            if (p.Index < 0 || p.Index >= g.ControlPoints.Count) return;
            g.ControlPoints[p.Index].IsLocked = p.Locked;
            GuideAddedOrUpdated?.Invoke(g);
        }

        private void OnRescale(GuideRescalePacket p)
        {
            if (!_guides.TryGetValue(p.GuideId(), out GuideData g)) return;
            g.VoxelScale = p.Scale;
            GuideAddedOrUpdated?.Invoke(g);
        }

        private void OnSetProjection(GuideSetProjectionPacket p)
        {
            if (!_guides.TryGetValue(p.GuideId(), out GuideData g)) return;
            g.Projection = (ProjectionMode)p.Mode;
            g.Plane = p.ResolvePlane();
            GuideAddedOrUpdated?.Invoke(g);
        }

        private void OnSetFilled(GuideSetFilledPacket p)
        {
            if (!_guides.TryGetValue(p.GuideId(), out GuideData g)) return;
            g.IsFilled = p.Filled;
            GuideAddedOrUpdated?.Invoke(g);
        }

        private void OnSetWireframe(GuideSetWireframePacket p)
        {
            if (!_guides.TryGetValue(p.GuideId(), out GuideData g)) return;
            g.IsWireframe = p.Wireframe;
            GuideAddedOrUpdated?.Invoke(g);
        }

        private void OnSetDivisions(GuideSetDivisionsPacket p)
        {
            if (!_guides.TryGetValue(p.GuideId(), out GuideData g)) return;
            g.Divisions = p.Divisions;
            GuideAddedOrUpdated?.Invoke(g);   // renderer repaints the marks via the normal rebuild
        }

        private void OnSetSides(GuideSetSidesPacket p)
        {
            if (!_guides.TryGetValue(p.GuideId(), out GuideData g)) return;
            bool taperedPrism = g.ShapeType == GuideShapeType.TaperedPolygonalPrism;
            if (g.ShapeType == GuideShapeType.PolygonalPrism || taperedPrism)
                PolygonalPrismShape.ReseatForSideChange(
                    g.ControlPoints, g.ShapePlaneAxis, g.Sides, p.Sides, taperedPrism,
                    g.FlatSideAligned);
            g.Sides = p.Sides;
            GuideAddedOrUpdated?.Invoke(g);   // polygon geometry re-derives on the rebuild
        }

        private void OnHudMetadata(GuideHudMetadataPacket p)
        {
            if (!_guides.TryGetValue(p.GuideId(), out GuideData g)) return;
            g.LastSculptorName = p.LastSculptorName;
            g.CachedVoxelCount = p.CachedVoxelCount;
            g.CachedVoxelWidth = p.CachedVoxelWidth;
            g.CachedVoxelHeight = p.CachedVoxelHeight;
            g.CachedBlockWidth = p.CachedBlockWidth;
            g.CachedBlockHeight = p.CachedBlockHeight;
            GuideHudMetadataChanged?.Invoke(g.Id);
        }

        // ==========================================================================================
        //  Receive: lock state, drafts, warnings
        // ==========================================================================================

        private void OnLockState(GuideLockStatePacket p)
        {
            Guid id = p.GuideId();
            string holder = string.IsNullOrEmpty(p.HolderUid) ? null : p.HolderUid;
            if (holder == null) _lockHolders.Remove(id);
            else _lockHolders[id] = holder;
            LockStateChanged?.Invoke(id, holder);

            if (holder == null && _pendingGrabCancels.TryGetValue(id, out PendingGrabCancel pending))
            {
                pending.LockReleased = true;
                TryCompleteGrabCancel(id, pending);
            }
        }

        private void OnDraftAnchorBroadcast(DraftAnchorBroadcastPacket p)
        {
            if (string.IsNullOrEmpty(p.PlayerUid) || p.Start == null) return;
            Vec3d start = p.Start.ToVec3d();
            _remoteDraftAnchors[p.PlayerUid] = start;
            RemoteDraftAnchorChanged?.Invoke(p.PlayerUid, start);
        }

        private void OnDraftAnchorRemove(DraftAnchorRemovePacket p)
        {
            if (string.IsNullOrEmpty(p.PlayerUid)) return;
            if (_remoteDraftAnchors.Remove(p.PlayerUid)) RemoteDraftAnchorRemoved?.Invoke(p.PlayerUid);
        }

        private void OnCapWarning(VoxelCapWarningPacket p)
            => VoxelCapWarningReceived?.Invoke(p.GuideId(), p.CurrentCount, p.Cap);

        // ==========================================================================================
        //  Send: the requests the held tool makes (Module 7 wires these to clicks / keybinds)
        // ==========================================================================================

        /// <summary>
        /// Send the completed placement: two foot points + settings (+ Session 11: the SHIFT inversion
        /// flag, the polygon side count, a three-click triangle's apex, and — 0.1.15 — the Free-Shape's
        /// full corner chain + closed flag). The server builds the guide.
        /// </summary>
        public void SendCreateRequest(Vec3d start, Vec3d end, GuideRenderSettings settings,
            GuideShapeType shapeType = GuideShapeType.Arch,
            ShapeConstraint constraint = ShapeConstraint.None,
            PlaneAxis shapePlaneAxis = PlaneAxis.Y,
            bool inverted = false, int sides = 0, Vec3d apex = null,
            IReadOnlyList<Vec3d> chain = null, bool closed = false, Vec3d rim = null,
            bool flatSideAligned = false, bool deferPlacementEffects = false)
        {
            bool localMutation = AuthorityMode == ClientAuthorityMode.Local && _local != null;
            _lastMutationWasLocal = localMutation;
            if (localMutation)
            {
                GuideData created = _local.Create(start, end, settings, shapeType, constraint,
                    shapePlaneAxis, inverted, sides, apex, chain, closed, rim, flatSideAligned);

                // F5: PRIVATE placement on a Layout server still spends chalk — private is private, not
                // free. The server owns the inventory but cannot see private guides, so the honest client
                // reports the completed placement and the server applies (and validates) the charge. On a
                // server WITHOUT Layout there is no channel, no real kit item, and thus no charge — the
                // accepted client-only caveat.
                if (created != null && ServerLayoutAvailable)
                {
                    _channel.SendPacket(new ChalkChargePacket(Guide.GuideShapeTypes.IsVolume(shapeType)
                        ? Items.ItemGuideTool.ChalkCostVolume
                        : Items.ItemGuideTool.ChalkCostFlat));
                }
                // Local placement feedback (snap + dust along the guide), client-side only — matching a
                // private guide's visibility: nobody else can see it, so nobody else hears its snap.
                if (created != null)
                {
                    if (!deferPlacementEffects)
                        Systems.ChalkEffects.PlacementEffects(_capi.World, created);
                }
                else PlacementRejected?.Invoke();
                return;
            }

            Vec3Dto[] chainDto = null;
            if (chain != null && chain.Count > 0)
            {
                chainDto = new Vec3Dto[chain.Count];
                for (int i = 0; i < chain.Count; i++) chainDto[i] = Vec3Dto.From(chain[i]);
            }
            _channel.SendPacket(new GuideCreateRequestPacket(
                Vec3Dto.From(start), Vec3Dto.From(end), RenderSettingsDto.From(settings),
                (int)shapeType, (int)constraint, (int)shapePlaneAxis,
                inverted, sides, apex == null ? null : Vec3Dto.From(apex),
                chainDto, closed, rim == null ? null : Vec3Dto.From(rim), flatSideAligned));
        }

        /// <summary>
        /// F5 (protocol 5): ask the server to refill the kit in the named inventory slot from the powder on
        /// the player's cursor. Only sent when the client's own checks pass (feature allowed, cursor is
        /// powder, slot is a non-full kit); the server re-validates authoritatively.
        /// </summary>
        public void SendInventoryChalkRefill(string inventoryId, int slotId)
        {
            if (string.IsNullOrEmpty(inventoryId)) return;
            _channel.SendPacket(new ChalkInventoryRefillPacket(inventoryId, slotId));
        }

        /// <summary>
        /// v0.2.22: report this player's own refill-channel preferences so the server's half of the
        /// held-interact honours them. GuidesBulkSynced also fires for local-authority loads and world
        /// teardown, so require positive proof of a Layout server and a live optional channel before sending.
        /// </summary>
        public void SendChalkRefillPrefs(bool allowHotbar, bool allowInventory)
        {
            if (!_receivedServerBulkSync || !_channel.Connected) return;
            _channel.SendPacket(new ChalkRefillPrefsPacket(allowHotbar, allowInventory));
        }

        /// <summary>Broadcast the local draft start anchor to other players.</summary>
        public void SendDraftStart(Vec3d start, GuideRenderSettings settings)
        {
            if (AuthorityMode == ClientAuthorityMode.Local) return;
            _channel.SendPacket(new DraftStartPacket(Vec3Dto.From(start), RenderSettingsDto.From(settings)));
        }

        /// <summary>Cancel the local draft.</summary>
        public void SendDraftCancel()
        {
            if (AuthorityMode == ClientAuthorityMode.Local) return;
            _channel.SendPacket(new DraftCancelPacket());
        }

        /// <summary>Request the edit lock on a guide.</summary>
        public void SendGrab(Guid guideId)
        {
            if (IsLocalGuide(guideId) && _local != null) { _local.Grab(guideId); return; }
            _channel.SendPacket(new GuideGrabPacket(guideId));
        }

        /// <summary>Release the edit lock on a guide (the server commits the drag's undo entry on receipt).</summary>
        public void SendRelease(Guid guideId)
        {
            if (IsLocalGuide(guideId) && _local != null) { _local.Release(guideId); return; }
            _channel.SendPacket(new GuideReleasePacket(guideId));
        }

        /// <summary>Cancel (not commit) our in-progress grab — see <see cref="GuideCancelGrabPacket"/>.</summary>
        public void SendCancelGrab(Guid guideId)
        {
            if (_guides.TryGetValue(guideId, out GuideData restored))
            {
                _pendingGrabCancels[guideId] = new PendingGrabCancel
                {
                    Expected = restored.DeepClone()
                };
            }
            if (IsLocalGuide(guideId) && _local != null) { _local.CancelGrab(guideId); return; }
            _channel.SendPacket(new GuideCancelGrabPacket(guideId));
        }

        /// <summary>Move a single control point (the common grab-and-drag case).</summary>
        public void SendMovePoint(Guid guideId, int index, Vec3d position)
        {
            bool localMutation = IsLocalMutation(guideId);
            _lastMutationWasLocal = localMutation;
            if (localMutation)
            {
                _local.MovePoints(guideId, new[] { (index, position) });
                return;
            }
            _channel.SendPacket(new GuideUpdatePacket(
                guideId, new[] { new ControlPointEditDto(index, Vec3Dto.From(position)) }));
        }

        /// <summary>Move several control points at once.</summary>
        public void SendMovePoints(Guid guideId, IReadOnlyList<(int index, Vec3d position)> edits)
        {
            if (edits == null || edits.Count == 0) return;
            bool localMutation = IsLocalMutation(guideId);
            _lastMutationWasLocal = localMutation;
            if (localMutation) { _local.MovePoints(guideId, edits); return; }
            var dto = new ControlPointEditDto[edits.Count];
            for (int i = 0; i < edits.Count; i++)
                dto[i] = new ControlPointEditDto(edits[i].index, Vec3Dto.From(edits[i].position));
            _channel.SendPacket(new GuideUpdatePacket(guideId, dto));
        }

        /// <summary>Insert a body point at the clicked position (the server derives the index).</summary>
        /// <summary>
        /// Body insert. Default: the insert that starts a grab (adopted via OnGuideAddedOrUpdated).
        /// With <paramref name="locked"/>: a lock-in-place insert — the point is created on-curve, born
        /// locked, and no grab follows (the server never leaves us holding the edit lock).
        /// </summary>
        public void SendInsertPoint(Guid guideId, Vec3d position, bool locked = false)
        {
            bool localMutation = IsLocalMutation(guideId);
            _lastMutationWasLocal = localMutation;
            if (localMutation) { _local.Insert(guideId, position, locked); return; }
            _channel.SendPacket(new GuideInsertPointPacket(guideId, -1, Vec3Dto.From(position), locked));
        }

        /// <summary>Dispel a guide.</summary>
        public void SendDelete(Guid guideId)
        {
            bool localMutation = IsLocalMutation(guideId);
            _lastMutationWasLocal = localMutation;
            if (localMutation) { _local.Delete(guideId); return; }
            _channel.SendPacket(new GuideDeletePacket(guideId));
        }

        /// <summary>Toggle a guide's hidden flag.</summary>
        public void SendHide(Guid guideId, bool hidden)
        {
            bool localMutation = IsLocalMutation(guideId);
            _lastMutationWasLocal = localMutation;
            if (localMutation) { _local.SetHidden(guideId, hidden); return; }
            _channel.SendPacket(new GuideHidePacket(guideId, hidden));
        }

        /// <summary>Set a control point's locked-constraint flag.</summary>
        public void SendLockPoint(Guid guideId, int index, bool locked)
        {
            bool localMutation = IsLocalMutation(guideId);
            _lastMutationWasLocal = localMutation;
            if (localMutation) { _local.SetPointLocked(guideId, index, locked); return; }
            _channel.SendPacket(new GuideLockPointPacket(guideId, index, locked));
        }

        /// <summary>Change a guide's voxel scale.</summary>
        public void SendRescale(Guid guideId, int scale)
        {
            bool localMutation = IsLocalMutation(guideId);
            _lastMutationWasLocal = localMutation;
            if (localMutation) { _local.Rescale(guideId, scale); return; }
            _channel.SendPacket(new GuideRescalePacket(guideId, scale));
        }

        /// <summary>Set a guide's projection mode and plane.</summary>
        public void SendSetProjection(Guid guideId, ProjectionMode mode, ProjectionPlane plane)
        {
            bool localMutation = IsLocalMutation(guideId);
            _lastMutationWasLocal = localMutation;
            if (localMutation) { _local.SetProjection(guideId, mode, plane); return; }
            _channel.SendPacket(new GuideSetProjectionPacket(
                guideId, (int)mode, (int)plane.FlattenedAxis, plane.PlaneOffset));
        }

        /// <summary>Toggle a guide's filled flag.</summary>
        public void SendSetFilled(Guid guideId, bool filled)
        {
            bool localMutation = IsLocalMutation(guideId);
            _lastMutationWasLocal = localMutation;
            if (localMutation) { _local.SetFilled(guideId, filled); return; }
            _channel.SendPacket(new GuideSetFilledPacket(guideId, filled));
        }

        /// <summary>Switch a 3D guide between its shell and structural wireframe.</summary>
        public void SendSetWireframe(Guid guideId, bool wireframe)
        {
            bool localMutation = IsLocalMutation(guideId);
            _lastMutationWasLocal = localMutation;
            if (localMutation) { _local.SetWireframe(guideId, wireframe); return; }
            _channel.SendPacket(new GuideSetWireframePacket(guideId, wireframe));
        }

        /// <summary>Session 9: set a guide's equal-part division marks (purely visual).</summary>
        public void SendSetDivisions(Guid guideId, int divisions)
        {
            bool localMutation = IsLocalMutation(guideId);
            _lastMutationWasLocal = localMutation;
            if (localMutation) { _local.SetDivisions(guideId, divisions); return; }
            _channel.SendPacket(new GuideSetDivisionsPacket(guideId, divisions));
        }

        /// <summary>Session 11: set a polygon guide's side count.</summary>
        public void SendSetSides(Guid guideId, int sides)
        {
            bool localMutation = IsLocalMutation(guideId);
            _lastMutationWasLocal = localMutation;
            if (localMutation) { _local.SetSides(guideId, sides); return; }
            _channel.SendPacket(new GuideSetSidesPacket(guideId, sides));
        }

        /// <summary>
        /// F6 Move: slide a whole guide by a delta given in 1/16-block units. The authority validates that
        /// the delta is a whole number of the guide's own voxels and refuses anything finer, so callers must
        /// step by the guide's scale — see <c>GuideManager.TranslateGuide</c>. Nothing is applied optimistically:
        /// both authorities answer with a full-state upsert, and a move can be refused by a land claim.
        /// </summary>
        public void SendTranslate(Guid guideId, int deltaX, int deltaY, int deltaZ)
        {
            bool localMutation = IsLocalMutation(guideId);
            _lastMutationWasLocal = localMutation;
            if (localMutation)
            {
                _local.Translate(guideId, new Vec3d(deltaX / 16.0, deltaY / 16.0, deltaZ / 16.0));
                return;
            }
            _channel.SendPacket(new GuideTranslatePacket(guideId, deltaX, deltaY, deltaZ));
        }

        /// <summary>
        /// F12 Rotate: turn a whole guide by quarter turns about a world axis. The authority derives the
        /// pivot from the guide itself, so nothing about it crosses the wire. Unlike a move this can change
        /// the voxel count and so can be refused by a cap as well as by a land claim.
        /// </summary>
        public void SendRotate(Guid guideId, PlaneAxis axis, int quarterTurns)
        {
            bool localMutation = IsLocalMutation(guideId);
            _lastMutationWasLocal = localMutation;
            if (localMutation) { _local.Rotate(guideId, axis, quarterTurns); return; }
            _channel.SendPacket(new GuideRotatePacket(guideId, axis, quarterTurns));
        }

        /// <summary>
        /// F7/F8 Transform pad: one compound action — optional mirror, optional rotation, optional move —
        /// applied in place or to a fresh copy. A copy is a placement and can be refused for chalk or caps;
        /// the in-place actions can only be refused by a land claim.
        /// </summary>
        public void SendTransform(
            Guid guideId, int deltaX, int deltaY, int deltaZ,
            int mirrorAxis, PlaneAxis rotateAxis, int quarterTurns, bool asCopy)
        {
            bool localMutation = IsLocalMutation(guideId);
            _lastMutationWasLocal = localMutation;
            if (localMutation)
            {
                GuideData copied = _local.Transform(
                    guideId, new Vec3d(deltaX / 16.0, deltaY / 16.0, deltaZ / 16.0),
                    mirrorAxis, rotateAxis, quarterTurns, asCopy);
                // A private COPY is still a placement, so it charges chalk the same way a fresh private
                // placement does: the server owns the inventory but cannot see private guides, so the
                // honest client reports it and the server validates and applies the charge.
                if (copied != null && ServerLayoutAvailable)
                {
                    _channel.SendPacket(new ChalkChargePacket(
                        Guide.GuideShapeTypes.IsVolume(copied.ShapeType)
                            ? Items.ItemGuideTool.ChalkCostVolume
                            : Items.ItemGuideTool.ChalkCostFlat));
                }
                if (copied != null) Systems.ChalkEffects.PlacementEffects(_capi.World, copied);
                return;
            }
            _channel.SendPacket(new GuideTransformPacket(
                guideId, deltaX, deltaY, deltaZ, mirrorAxis, rotateAxis, quarterTurns, asCopy));
        }

        /// <summary>Session 11: spring a guide back to its as-placed form (SHIFT+click).</summary>
        public void SendSpringBack(Guid guideId)
        {
            bool localMutation = IsLocalMutation(guideId);
            _lastMutationWasLocal = localMutation;
            if (localMutation) { _local.SpringBack(guideId); return; }
            _channel.SendPacket(new GuideSpringBackPacket(guideId));
        }

        /// <summary>
        /// Undo against the authority of the most recently mutated guide. Before the first mutation in a
        /// world, placement mode remains the intuitive fallback and preserves the original behaviour.
        /// </summary>
        public void SendUndo()
        {
            if (UndoRedoUsesLocalAuthority()) { _local.Undo(); return; }
            _channel.SendPacket(new UndoRequestPacket());
        }

        /// <summary>Redo against the same most-recent guide authority as undo.</summary>
        public void SendRedo()
        {
            if (UndoRedoUsesLocalAuthority()) { _local.Redo(); return; }
            _channel.SendPacket(new RedoRequestPacket());
        }

        private bool IsLocalMutation(Guid guideId) => _local != null && IsLocalGuide(guideId);

        private bool UndoRedoUsesLocalAuthority() => _local != null
            && (_lastMutationWasLocal ?? AuthorityMode == ClientAuthorityMode.Local);
    }
}
