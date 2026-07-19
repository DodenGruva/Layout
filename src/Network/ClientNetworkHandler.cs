using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Layout.Client;
using Layout.Guide;
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

        private int _perGuideVoxelCap = 25000;
        private int _totalVoxelCap = 250000;

        /// <summary>The authority selected for the current world.</summary>
        public ClientAuthorityMode AuthorityMode { get; private set; } = ClientAuthorityMode.Detecting;
        public bool ServerLayoutAvailable => _serverLayoutAvailable;
        public bool ServerAllowsClientOnlyMode => _serverAllowsClientOnlyMode;

        // -- Read-only views for the renderer / HUD / tool ----------------------------------------

        /// <summary>The local guide mirror, by id. Treat as read-only; mutate only by sending requests.</summary>
        public IReadOnlyDictionary<Guid, GuideData> Guides => _guides;

        /// <summary>Guide id → the UID currently editing it. A guide absent here is free to grab.</summary>
        public IReadOnlyDictionary<Guid, string> LockHolders => _lockHolders;

        /// <summary>Player UID → their in-progress draft start anchor, for rendering remote anchor dots.</summary>
        public IReadOnlyDictionary<string, Vec3d> RemoteDraftAnchors => _remoteDraftAnchors;

        /// <summary>The server's active per-guide voxel cap (synced on join). Use for the placement pre-check.</summary>
        public int PerGuideVoxelCap => AuthorityMode == ClientAuthorityMode.Local
            ? GuideManager.HardVoxelCeiling
            : _perGuideVoxelCap;

        /// <summary>The server's active total voxel cap (synced on join).</summary>
        public int TotalVoxelCap => AuthorityMode == ClientAuthorityMode.Local ? 0 : _totalVoxelCap;

        // v0.2.22: the chalk refill channels moved from server policy to a CLIENT preference, so the two
        // synced flags that used to be mirrored here are gone. Read
        // LayoutModSystem.{Hotbar,Inventory}ChalkRefillAllowed instead — it reads layout-client.json.
        // The matching packet fields remain declared (append-only) but are no longer populated or read.

        public bool IsLocalGuide(Guid id) => _localGuideIds.Contains(id);

        // -- Events --------------------------------------------------------------------------------

        /// <summary>A guide appeared or changed; the argument is the live mirror record. Rebuild its mesh.</summary>
        public event Action<GuideData> GuideAddedOrUpdated;

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

        /// <summary>A mutation was refused for exceeding a cap: (guide id, attempted count, cap).</summary>
        public event Action<Guid, int, int> VoxelCapWarningReceived;

        /// <summary>Raised after the current world's authority becomes known or is reset.</summary>
        public event Action<ClientAuthorityMode> AuthorityModeChanged;

        /// <summary>Raised when an explicit server command changes the persisted client preference.</summary>
        public event Action<bool> ForceClientOnlyPreferenceChanged;

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
                .SetMessageHandler<GuideLockStatePacket>(OnLockState)
                .SetMessageHandler<DraftAnchorBroadcastPacket>(OnDraftAnchorBroadcast)
                .SetMessageHandler<DraftAnchorRemovePacket>(OnDraftAnchorRemove)
                .SetMessageHandler<VoxelCapWarningPacket>(OnCapWarning)
                .SetMessageHandler<ClientPlacementModePacket>(OnPlacementMode)
                .SetMessageHandler<ClientGuidePushRequestPacket>(OnGuidePushRequest)
                .SetMessageHandler<ClientGuidePushResultPacket>(OnGuidePushResult);
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

        /// <summary>Returns authority selection to neutral without touching a network bulk sync already received.</summary>
        public void ResetAuthorityMode(bool forceClientOnly = false)
        {
            _preferClientOnly = forceClientOnly;
            SetAuthorityMode(ClientAuthorityMode.Detecting);
        }

        /// <summary>Ends the current world session and drops every local mirror entry.</summary>
        public void EndWorldSession()
        {
            ResetAuthorityMode(_preferClientOnly);
            _receivedServerBulkSync = false;
            _serverLayoutAvailable = false;
            _serverAllowsClientOnlyMode = false;
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
                OnSetProjection, OnSetFilled, OnSetDivisions, OnSetSides, OnLockState, OnCapWarning);
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
            _guides[g.Id] = g;                 // upsert: also the resync / undo-broadcast path
            GuideAddedOrUpdated?.Invoke(g);
        }

        // ==========================================================================================
        //  Receive: incremental
        // ==========================================================================================

        private void OnUpdate(GuideUpdatePacket p)
        {
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

        private void OnSetDivisions(GuideSetDivisionsPacket p)
        {
            if (!_guides.TryGetValue(p.GuideId(), out GuideData g)) return;
            g.Divisions = p.Divisions;
            GuideAddedOrUpdated?.Invoke(g);   // renderer repaints the marks via the normal rebuild
        }

        private void OnSetSides(GuideSetSidesPacket p)
        {
            if (!_guides.TryGetValue(p.GuideId(), out GuideData g)) return;
            g.Sides = p.Sides;
            GuideAddedOrUpdated?.Invoke(g);   // the polygon re-derives its outline on the rebuild
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
            IReadOnlyList<Vec3d> chain = null, bool closed = false, Vec3d rim = null)
        {
            bool localMutation = AuthorityMode == ClientAuthorityMode.Local && _local != null;
            _lastMutationWasLocal = localMutation;
            if (localMutation)
            {
                GuideData created = _local.Create(start, end, settings, shapeType, constraint,
                    shapePlaneAxis, inverted, sides, apex, chain, closed, rim);

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
                if (created != null) Systems.ChalkEffects.PlacementEffects(_capi.World, created);
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
                chainDto, closed, rim == null ? null : Vec3Dto.From(rim)));
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
        /// held-interact honours them. Sent once the join sync lands; harmless on a vanilla server (no
        /// channel) because the local-authority guard below short-circuits.
        /// </summary>
        public void SendChalkRefillPrefs(bool allowHotbar, bool allowInventory)
        {
            if (AuthorityMode == ClientAuthorityMode.Local) return;
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
