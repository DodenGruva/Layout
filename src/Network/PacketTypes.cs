using System;
using System.Collections.Generic;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Server;
using Vintagestory.API.MathTools;
using Layout.Guide;

namespace Layout.Network
{
    // ===============================================================================================
    //  Layout networking — wire protocol (Module 4)
    // ===============================================================================================
    //
    //  WIRE FORMAT — protobuf DTOs, not JSON.
    //  Vintage Story serialises mod packets with protobuf-net, so every packet and every nested payload
    //  here is a [ProtoContract] type that protobuf-net turns straight into compact binary. This is the
    //  faster-on-both-ends choice the project settled on: a guide crosses the wire as a flat DTO and is
    //  serialised exactly once. (The rejected alternative — embedding a Newtonsoft JSON string inside a
    //  packet — would serialise twice and lean on reflection-heavy JSON on the server and every client.)
    //
    //  ENCODING CONVENTIONS (kept deliberately lean and version-stable):
    //    • Enums travel as int (GuideShapeType / ProjectionMode / PlaneAxis already carry pinned values,
    //      so the int is a stable contract). DTO enum-ish fields are int, cast at the boundary.
    //    • A Guid travels as its 16-byte form (Guid.ToByteArray), the leanest unambiguous encoding.
    //    • A position travels as three doubles, in the same world units a Vec3d holds. No Vec3d ever
    //      goes on the wire directly — Vec3Dto pins the exact three fields we want.
    //    • The full control-point list (phantoms included) is sent verbatim, mirroring how the guide is
    //      persisted, so a client's mirror is an exact copy of the server's record. Phantom positions are
    //      deterministic, so even before a client re-derives them the data is already correct.
    //
    //  PROTO MEMBER NUMBERS are append-only per type: never renumber or reuse a [ProtoMember] index once
    //  shipped, exactly as the data-model enums are append-only. New fields take the next free number.
    //
    //  REGISTRATION ORDER IS A CONTRACT. A VS network channel identifies a message by its registration
    //  order, so the server and client MUST register the same packet types in the same order or the IDs
    //  desync and packets are mis-routed. Both handlers call the single RegisterMessageTypes() below to
    //  make that impossible to get wrong.
    // ===============================================================================================

    // ----------------------------------------------------------------------------------------------
    //  Shared constants + id helpers
    // ----------------------------------------------------------------------------------------------

    /// <summary>Channel name and protocol version, shared by both handlers.</summary>
    public static class LayoutChannel
    {
        /// <summary>The network channel name. Must match on server and client.</summary>
        public const string Name = "layout";

        /// <summary>
        /// Bumped if the packet set or field meanings change incompatibly. Carried in the bulk sync so a
        /// future client can detect a mismatch; informational for now (there is only one version).
        /// </summary>
        public const int ProtocolVersion = 23;
    }

    /// <summary>Guid &lt;-&gt; 16-byte wire form helpers.</summary>
    internal static class NetIds
    {
        public static byte[] ToBytes(Guid id) => id.ToByteArray();

        public static Guid ToGuid(byte[] bytes) =>
            bytes != null && bytes.Length == 16 ? new Guid(bytes) : Guid.Empty;
    }

    // ----------------------------------------------------------------------------------------------
    //  Payload DTOs (nested inside packets; never registered as channel messages themselves)
    // ----------------------------------------------------------------------------------------------

    /// <summary>A world position as three doubles — the only form a position takes on the wire.</summary>
    [ProtoContract]
    public class Vec3Dto
    {
        [ProtoMember(1)] public double X;
        [ProtoMember(2)] public double Y;
        [ProtoMember(3)] public double Z;

        public Vec3Dto() { }

        public Vec3Dto(double x, double y, double z) { X = x; Y = y; Z = z; }

        /// <summary>Projects a Vec3d (null → origin). The source is read-only; nothing is aliased.</summary>
        public static Vec3Dto From(Vec3d v) => v == null ? new Vec3Dto() : new Vec3Dto(v.X, v.Y, v.Z);

        /// <summary>Builds a fresh, owned Vec3d from this DTO.</summary>
        public Vec3d ToVec3d() => new Vec3d(X, Y, Z);
    }

    /// <summary>One control point: its position plus the four role flags. Phantoms are included.</summary>
    [ProtoContract]
    public class ControlPointDto
    {
        [ProtoMember(1)] public Vec3Dto Position;
        [ProtoMember(2)] public bool IsLocked;
        [ProtoMember(3)] public bool IsPhantom;
        [ProtoMember(4)] public bool IsAnchor;
        [ProtoMember(5)] public bool IsPrimary;
        [ProtoMember(6)] public bool IsLockMarker;

        public ControlPointDto() { }

        public static ControlPointDto From(ControlPoint cp)
        {
            if (cp == null) return new ControlPointDto { Position = new Vec3Dto() };
            return new ControlPointDto
            {
                Position = Vec3Dto.From(cp.WorldPosition),
                IsLocked = cp.IsLocked,
                IsPhantom = cp.IsPhantom,
                IsAnchor = cp.IsAnchor,
                IsPrimary = cp.IsPrimary,
                IsLockMarker = cp.IsLockMarker
            };
        }

        /// <summary>Builds an owned ControlPoint (position deep-copied via the ControlPoint constructor).</summary>
        public ControlPoint ToControlPoint()
        {
            Vec3d pos = Position != null ? Position.ToVec3d() : new Vec3d();
            return new ControlPoint(pos, IsLocked, IsPhantom, IsAnchor, IsPrimary, IsLockMarker);
        }
    }

    /// <summary>A whole guide record, flattened for the wire. Maps 1:1 to <see cref="GuideData"/>.</summary>
    [ProtoContract]
    public class GuideDataDto
    {
        [ProtoMember(1)] public byte[] IdBytes;
        [ProtoMember(2)] public int ShapeType;
        [ProtoMember(3)] public ControlPointDto[] ControlPoints;
        [ProtoMember(4)] public int VoxelScale;
        [ProtoMember(5)] public bool IsHidden;
        [ProtoMember(6)] public int Projection;
        [ProtoMember(7)] public int PlaneAxis;
        [ProtoMember(8)] public int PlaneOffset;
        [ProtoMember(9)] public bool IsFilled;
        [ProtoMember(10)] public int DataVersion;
        // Session-8 additive fields (protobuf: safe to append, absent = 0 = None / X→remapped below)
        [ProtoMember(11)] public int Constraint;
        [ProtoMember(12)] public int ShapePlaneAxis;
        [ProtoMember(13)] public int Divisions;      // Session-9 additive: visual division marks
        [ProtoMember(14)] public int Sides;          // Session-11 additive: polygon side count
        [ProtoMember(15)] public bool IsClosed;      // Session-11 (0.1.15) additive: Free-Shape loop flag
        [ProtoMember(16)] public bool FlatSideAligned; // 0.2.45: polygon edge, rather than vertex, alignment
        [ProtoMember(17)] public string DisplayName;
        [ProtoMember(18)] public int CachedVoxelCount;
        [ProtoMember(19)] public int CachedVoxelWidth;
        [ProtoMember(20)] public int CachedVoxelHeight;
        [ProtoMember(21)] public int CachedBlockWidth;
        [ProtoMember(22)] public int CachedBlockHeight;
        [ProtoMember(23)] public bool IsWireframe;
        [ProtoMember(24)] public string CreatorName;
        [ProtoMember(25)] public string LastSculptorName;
        // (The as-placed spring-back snapshot deliberately does NOT cross the wire: the server executes
        //  spring-back; clients only ever request it by guide id.)

        public GuideDataDto() { }

        public static GuideDataDto From(GuideData g)
        {
            var points = g.ControlPoints ?? new List<ControlPoint>();
            var dtoPoints = new ControlPointDto[points.Count];
            for (int i = 0; i < points.Count; i++) dtoPoints[i] = ControlPointDto.From(points[i]);

            return new GuideDataDto
            {
                IdBytes = NetIds.ToBytes(g.Id),
                ShapeType = (int)g.ShapeType,
                ControlPoints = dtoPoints,
                VoxelScale = g.VoxelScale,
                IsHidden = g.IsHidden,
                Projection = (int)g.Projection,
                PlaneAxis = (int)g.Plane.FlattenedAxis,
                PlaneOffset = g.Plane.PlaneOffset,
                IsFilled = g.IsFilled,
                DataVersion = g.DataVersion,
                Constraint = (int)g.Constraint,
                ShapePlaneAxis = (int)g.ShapePlaneAxis,
                Divisions = g.Divisions,
                Sides = g.Sides,
                IsClosed = g.IsClosed,
                FlatSideAligned = g.FlatSideAligned,
                DisplayName = g.DisplayName,
                CachedVoxelCount = g.CachedVoxelCount,
                CachedVoxelWidth = g.CachedVoxelWidth,
                CachedVoxelHeight = g.CachedVoxelHeight,
                CachedBlockWidth = g.CachedBlockWidth,
                CachedBlockHeight = g.CachedBlockHeight,
                IsWireframe = g.IsWireframe,
                CreatorName = g.CreatorName,
                LastSculptorName = g.LastSculptorName
            };
        }

        /// <summary>
        /// Rebuilds an owned <see cref="GuideData"/> from the wire. The server-assigned Id is preserved
        /// (so this does NOT go through <see cref="GuideData.Create"/>, which would mint a new Id). The
        /// control-point list is rebuilt with fresh ControlPoint instances, ready for the renderer to adopt
        /// by reference and re-derive phantoms against.
        /// </summary>
        public GuideData ToGuideData()
        {
            var dtoPoints = ControlPoints ?? Array.Empty<ControlPointDto>();
            var points = new List<ControlPoint>(dtoPoints.Length);
            for (int i = 0; i < dtoPoints.Length; i++)
            {
                ControlPointDto cp = dtoPoints[i];
                points.Add(cp != null ? cp.ToControlPoint() : new ControlPoint());
            }

            return new GuideData
            {
                Id = NetIds.ToGuid(IdBytes),
                ShapeType = (GuideShapeType)ShapeType,
                ControlPoints = points,
                VoxelScale = VoxelScale,
                IsHidden = IsHidden,
                Projection = (ProjectionMode)Projection,
                Plane = new ProjectionPlane((PlaneAxis)PlaneAxis, PlaneOffset),
                IsFilled = IsFilled,
                DataVersion = DataVersion,
                Constraint = (ShapeConstraint)Constraint,
                ShapePlaneAxis = (PlaneAxis)ShapePlaneAxis,
                Divisions = Divisions,
                Sides = Sides,
                IsClosed = IsClosed,
                FlatSideAligned = FlatSideAligned,
                DisplayName = DisplayName,
                CachedVoxelCount = CachedVoxelCount,
                CachedVoxelWidth = CachedVoxelWidth,
                CachedVoxelHeight = CachedVoxelHeight,
                CachedBlockWidth = CachedBlockWidth,
                CachedBlockHeight = CachedBlockHeight,
                IsWireframe = IsWireframe,
                CreatorName = CreatorName,
                LastSculptorName = LastSculptorName
            };
        }
    }

    /// <summary>S→C. Lightweight HUD metadata refresh following a successful persistent visible mutation.
    /// Creator is immutable and travels in the full guide DTO; Last Sculptor and cached measurements change.</summary>
    [ProtoContract]
    public class GuideHudMetadataPacket
    {
        [ProtoMember(1)] public byte[] GuideIdBytes;
        [ProtoMember(2)] public string LastSculptorName;
        [ProtoMember(3)] public int CachedVoxelCount;
        [ProtoMember(4)] public int CachedVoxelWidth;
        [ProtoMember(5)] public int CachedVoxelHeight;
        [ProtoMember(6)] public int CachedBlockWidth;
        [ProtoMember(7)] public int CachedBlockHeight;

        public GuideHudMetadataPacket() { }

        public GuideHudMetadataPacket(GuideData guide)
        {
            GuideIdBytes = NetIds.ToBytes(guide?.Id ?? Guid.Empty);
            LastSculptorName = guide?.LastSculptorName;
            CachedVoxelCount = guide?.CachedVoxelCount ?? 0;
            CachedVoxelWidth = guide?.CachedVoxelWidth ?? 0;
            CachedVoxelHeight = guide?.CachedVoxelHeight ?? 0;
            CachedBlockWidth = guide?.CachedBlockWidth ?? 0;
            CachedBlockHeight = guide?.CachedBlockHeight ?? 0;
        }

        public Guid GuideId() => NetIds.ToGuid(GuideIdBytes);
    }

    /// <summary>The render settings carried in a create request. Maps to <see cref="GuideRenderSettings"/>.</summary>
    [ProtoContract]
    public class RenderSettingsDto
    {
        [ProtoMember(1)] public int Scale;
        [ProtoMember(2)] public int Mode;
        [ProtoMember(3)] public int PlaneAxis;
        [ProtoMember(4)] public int PlaneOffset;
        [ProtoMember(5)] public bool Filled;
        // Session-9 additive: visual division marks (absent = 0 = none).
        [ProtoMember(6)] public int Divisions;
        [ProtoMember(7)] public bool Wireframe;

        public RenderSettingsDto() { }

        public static RenderSettingsDto From(GuideRenderSettings s) => new RenderSettingsDto
        {
            Scale = s.Scale,
            Mode = (int)s.Mode,
            PlaneAxis = (int)s.Plane.FlattenedAxis,
            PlaneOffset = s.Plane.PlaneOffset,
            Filled = s.Filled,
            Divisions = s.Divisions,
            Wireframe = s.Wireframe
        };

        public GuideRenderSettings ToRenderSettings() => new GuideRenderSettings(
            Scale,
            (ProjectionMode)Mode,
            new ProjectionPlane((PlaneAxis)PlaneAxis, PlaneOffset),
            Filled,
            Divisions,
            Wireframe);
    }

    /// <summary>One element of a control-point update: which point (by index) moves, and to where.</summary>
    [ProtoContract]
    public class ControlPointEditDto
    {
        [ProtoMember(1)] public int Index;
        [ProtoMember(2)] public Vec3Dto Position;

        public ControlPointEditDto() { }

        public ControlPointEditDto(int index, Vec3Dto position) { Index = index; Position = position; }
    }

    // ----------------------------------------------------------------------------------------------
    //  Packets — server → client
    // ----------------------------------------------------------------------------------------------

    /// <summary>S→C. The full world state sent to a player on join: every guide plus the active caps.</summary>
    [ProtoContract]
    public class GuideBulkSyncPacket
    {
        [ProtoMember(1)] public GuideDataDto[] Guides;
        [ProtoMember(2)] public int PerGuideVoxelCap;
        [ProtoMember(3)] public int TotalVoxelCap;
        [ProtoMember(4)] public int ProtocolVersion;
        [ProtoMember(5)] public bool AllowClientOnlyMode;
        // DEAD as of protocol 6 / v0.2.22: these carried the SERVER's refill-channel policy in 0.2.21.
        // The channels are now a CLIENT preference (layout-client.json) reported upward by
        // ChalkRefillPrefsPacket, so these are never written or read. Retained (never renumbered) because
        // packet fields are append-only; a 0.2.21 client reading them simply sees false.
        [ProtoMember(6)] public bool AllowHotbarChalkRefill;
        [ProtoMember(7)] public bool AllowInventoryChalkRefill;

        public GuideBulkSyncPacket() { }

        public GuideBulkSyncPacket(GuideDataDto[] guides, int perGuideVoxelCap, int totalVoxelCap,
            bool allowClientOnlyMode = false)
        {
            Guides = guides;
            PerGuideVoxelCap = perGuideVoxelCap;
            TotalVoxelCap = totalVoxelCap;
            ProtocolVersion = LayoutChannel.ProtocolVersion;
            AllowClientOnlyMode = allowClientOnlyMode;
        }
    }

    /// <summary>
    /// C→S (protocol 6). The player's own chalk refill-channel preferences, reported on join.
    /// </summary>
    /// <remarks>
    /// These are CONVENIENCE toggles the player owns, not server policy — but the server has to be told,
    /// because <c>ItemChalkingPowder</c>'s held-interact callbacks run on BOTH sides and it is the SERVER
    /// that actually mutates the stacks. Without this the server would happily refill a hotbar kit for a
    /// player who had switched the shortcut off, making the setting a no-op. Only the hotbar channel needs
    /// it (the inventory channel is already client-initiated via <see cref="ChalkInventoryRefillPacket"/>);
    /// the inventory flag rides along so the server can log/reason about intent.
    /// </remarks>
    [ProtoContract]
    public class ChalkRefillPrefsPacket
    {
        [ProtoMember(1)] public bool AllowHotbarRefill;
        [ProtoMember(2)] public bool AllowInventoryRefill;

        public ChalkRefillPrefsPacket() { }

        public ChalkRefillPrefsPacket(bool allowHotbarRefill, bool allowInventoryRefill)
        {
            AllowHotbarRefill = allowHotbarRefill;
            AllowInventoryRefill = allowInventoryRefill;
        }
    }

    /// <summary>S→C. The player's effective placement mode after a server policy decision.</summary>
    [ProtoContract]
    public class ClientPlacementModePacket
    {
        [ProtoMember(1)] public bool ClientOnly;
        [ProtoMember(2)] public bool Allowed;
        [ProtoMember(3)] public bool UpdatePreference;
        [ProtoMember(4)] public string Message;

        public ClientPlacementModePacket() { }

        public ClientPlacementModePacket(bool clientOnly, bool allowed, bool updatePreference, string message = null)
        {
            ClientOnly = clientOnly;
            Allowed = allowed;
            UpdatePreference = updatePreference;
            Message = message;
        }
    }

    /// <summary>S→C. Requests that the client upload all currently loaded private guides.</summary>
    [ProtoContract]
    public class ClientGuidePushRequestPacket
    {
        public ClientGuidePushRequestPacket() { }
    }

    /// <summary>S→C. Confirms which private guide ids were accepted by a push operation.</summary>
    [ProtoContract]
    public class ClientGuidePushResultPacket
    {
        [ProtoMember(1)] public byte[][] AcceptedLocalIdBytes;
        [ProtoMember(2)] public int RejectedCount;
        [ProtoMember(3)] public string Message;

        public ClientGuidePushResultPacket() { }

        public ClientGuidePushResultPacket(byte[][] acceptedLocalIdBytes, int rejectedCount, string message)
        {
            AcceptedLocalIdBytes = acceptedLocalIdBytes;
            RejectedCount = rejectedCount;
            Message = message;
        }
    }

    /// <summary>
    /// S→C. A guide's full current state. Sent on create, as the generic broadcast after an undo/redo,
    /// and as a corrective resync. The client treats it as an upsert (add or replace by Id).
    /// </summary>
    [ProtoContract]
    public class GuideCreatePacket
    {
        [ProtoMember(1)] public GuideDataDto Guide;

        public GuideCreatePacket() { }

        public GuideCreatePacket(GuideDataDto guide) { Guide = guide; }
    }

    /// <summary>S→C. The authoritative lock state of a guide: the holder UID, or null/empty when free.</summary>
    [ProtoContract]
    public class GuideLockStatePacket
    {
        [ProtoMember(1)] public byte[] GuideIdBytes;
        [ProtoMember(2)] public string HolderUid;

        public GuideLockStatePacket() { }

        public GuideLockStatePacket(Guid guideId, string holderUid)
        {
            GuideIdBytes = NetIds.ToBytes(guideId);
            HolderUid = holderUid;
        }

        public Guid GuideId() => NetIds.ToGuid(GuideIdBytes);
    }

    /// <summary>S→C. Another player has placed a draft start anchor; render it as a green dot.</summary>
    [ProtoContract]
    public class DraftAnchorBroadcastPacket
    {
        [ProtoMember(1)] public string PlayerUid;
        [ProtoMember(2)] public Vec3Dto Start;

        public DraftAnchorBroadcastPacket() { }

        public DraftAnchorBroadcastPacket(string playerUid, Vec3Dto start) { PlayerUid = playerUid; Start = start; }
    }

    /// <summary>S→C. A player's draft anchor is gone (completed, cancelled, or they disconnected).</summary>
    [ProtoContract]
    public class DraftAnchorRemovePacket
    {
        [ProtoMember(1)] public string PlayerUid;

        public DraftAnchorRemovePacket() { }

        public DraftAnchorRemovePacket(string playerUid) { PlayerUid = playerUid; }
    }

    /// <summary>S→C. A mutation the player attempted would exceed a voxel cap; here are the figures.</summary>
    [ProtoContract]
    public class VoxelCapWarningPacket
    {
        [ProtoMember(1)] public byte[] GuideIdBytes;
        [ProtoMember(2)] public int CurrentCount;
        [ProtoMember(3)] public int Cap;

        public VoxelCapWarningPacket() { }

        public VoxelCapWarningPacket(Guid guideId, int currentCount, int cap)
        {
            GuideIdBytes = NetIds.ToBytes(guideId);
            CurrentCount = currentCount;
            Cap = cap;
        }

        public Guid GuideId() => NetIds.ToGuid(GuideIdBytes);
    }

    // ----------------------------------------------------------------------------------------------
    //  Packets — client → server
    // ----------------------------------------------------------------------------------------------

    /// <summary>C→S. The second click of a placement: two foot points + settings. The server builds the guide.</summary>
    [ProtoContract]
    public class GuideCreateRequestPacket
    {
        [ProtoMember(1)] public Vec3Dto Start;
        [ProtoMember(2)] public Vec3Dto End;
        [ProtoMember(3)] public RenderSettingsDto Settings;
        // Session-8 additive: which primitive/constraint to build, and the intrinsic plane for the
        // ellipse family (from the first click's block face). Absent = 0 = Arch / None / X→see server.
        [ProtoMember(4)] public int ShapeType;
        [ProtoMember(5)] public int Constraint;
        [ProtoMember(6)] public int ShapePlaneAxis;
        // Session-11 additive: SHIFT-at-placement inversion (upside-down arch/triangle), the polygon side
        // count, and the third click of a 3-click triangle (null = no apex click; the shape derives one).
        [ProtoMember(7)] public bool Inverted;
        [ProtoMember(8)] public int Sides;
        [ProtoMember(9)] public Vec3Dto Apex;
        // Session-11 (0.1.15) additive: the Free-Shape's full corner chain (Start/End still carry the
        // first/last corners for the legacy fields) and whether it closed back onto corner 0.
        [ProtoMember(10)] public Vec3Dto[] Chain;
        [ProtoMember(11)] public bool Closed;
        // 0.2.24 additive (protocol 7): the FOURTH click of a Tapered Cylinder — the rim point whose
        // distance from the axis sets the lid's radius. Null for every other shape (they derive one).
        [ProtoMember(12)] public Vec3Dto Rim;
        [ProtoMember(13)] public bool FlatSideAligned;

        public GuideCreateRequestPacket() { }

        public GuideCreateRequestPacket(Vec3Dto start, Vec3Dto end, RenderSettingsDto settings,
            int shapeType = 0, int constraint = 0, int shapePlaneAxis = 0,
            bool inverted = false, int sides = 0, Vec3Dto apex = null,
            Vec3Dto[] chain = null, bool closed = false, Vec3Dto rim = null,
            bool flatSideAligned = false)
        {
            Start = start;
            End = end;
            Settings = settings;
            ShapeType = shapeType;
            Constraint = constraint;
            ShapePlaneAxis = shapePlaneAxis;
            Inverted = inverted;
            Sides = sides;
            Apex = apex;
            Chain = chain;
            Closed = closed;
            Rim = rim;
            FlatSideAligned = flatSideAligned;
        }
    }

    /// <summary>C→S. Request the edit lock on a guide.</summary>
    [ProtoContract]
    public class GuideGrabPacket
    {
        [ProtoMember(1)] public byte[] GuideIdBytes;

        public GuideGrabPacket() { }

        public GuideGrabPacket(Guid guideId) { GuideIdBytes = NetIds.ToBytes(guideId); }

        public Guid GuideId() => NetIds.ToGuid(GuideIdBytes);
    }

    /// <summary>C→S. Release the edit lock on a guide (also the commit point for a drag's undo entry).</summary>
    [ProtoContract]
    public class GuideReleasePacket
    {
        [ProtoMember(1)] public byte[] GuideIdBytes;

        public GuideReleasePacket() { }

        public GuideReleasePacket(Guid guideId) { GuideIdBytes = NetIds.ToBytes(guideId); }

        public Guid GuideId() => NetIds.ToGuid(GuideIdBytes);
    }

    /// <summary>
    /// Both ways: set a guide's equal-part division count (Session 9 — purely visual; see
    /// <see cref="Layout.Guide.GuideData.Divisions"/>). C→S requests; S→C broadcasts the applied value.
    /// </summary>
    [ProtoContract]
    public class GuideSetDivisionsPacket
    {
        [ProtoMember(1)] public byte[] GuideIdBytes;
        [ProtoMember(2)] public int Divisions;

        public GuideSetDivisionsPacket() { }

        public GuideSetDivisionsPacket(Guid guideId, int divisions)
        {
            GuideIdBytes = NetIds.ToBytes(guideId);
            Divisions = divisions;
        }

        public Guid GuideId() => NetIds.ToGuid(GuideIdBytes);
    }

    /// <summary>
    /// Both ways: set a polygon guide's side count (Session 11). C→S requests; S→C broadcasts the applied
    /// (clamped) value. Mirrors <see cref="GuideSetDivisionsPacket"/>.
    /// </summary>
    [ProtoContract]
    public class GuideSetSidesPacket
    {
        [ProtoMember(1)] public byte[] GuideIdBytes;
        [ProtoMember(2)] public int Sides;

        public GuideSetSidesPacket() { }

        public GuideSetSidesPacket(Guid guideId, int sides)
        {
            GuideIdBytes = NetIds.ToBytes(guideId);
            Sides = sides;
        }

        public Guid GuideId() => NetIds.ToGuid(GuideIdBytes);
    }

    /// <summary>
    /// C→S. SHIFT+click on a placed guide (Session 11): spring the guide back to its as-placed form —
    /// the server restores the original control points + constraint recorded at creation, as one undo
    /// step, and broadcasts the guide's full state. Guides placed before the snapshot existed get an
    /// in-game error instead.
    /// </summary>
    [ProtoContract]
    public class GuideSpringBackPacket
    {
        [ProtoMember(1)] public byte[] GuideIdBytes;

        public GuideSpringBackPacket() { }

        public GuideSpringBackPacket(Guid guideId) { GuideIdBytes = NetIds.ToBytes(guideId); }

        public Guid GuideId() => NetIds.ToGuid(GuideIdBytes);
    }

    /// <summary>
    /// C→S. Cancel (rather than commit) the sender's in-progress grab on this guide — the Session-8
    /// right-click-cancels contract. The server restores every dragged point to its pre-drag origin, or,
    /// if the grab began as a body insert, REMOVES the inserted point entirely (insert + grab were one
    /// gesture, so cancel undoes the whole gesture); either way no undo command is recorded, the drag
    /// session is discarded, and the lock is released. The server knows which case applies from its own
    /// drag-session bookkeeping — the client sends nothing but the guide id.
    /// </summary>
    [ProtoContract]
    public class GuideCancelGrabPacket
    {
        [ProtoMember(1)] public byte[] GuideIdBytes;

        public GuideCancelGrabPacket() { }

        public GuideCancelGrabPacket(Guid guideId) { GuideIdBytes = NetIds.ToBytes(guideId); }

        public Guid GuideId() => NetIds.ToGuid(GuideIdBytes);
    }

    /// <summary>C→S. The local player's draft start anchor (broadcast to others as a dot).</summary>
    [ProtoContract]
    public class DraftStartPacket
    {
        [ProtoMember(1)] public Vec3Dto Start;
        [ProtoMember(2)] public RenderSettingsDto Settings;

        public DraftStartPacket() { }

        public DraftStartPacket(Vec3Dto start, RenderSettingsDto settings) { Start = start; Settings = settings; }
    }

    /// <summary>C→S. Cancel the local player's draft (player implied by the connection).</summary>
    [ProtoContract]
    public class DraftCancelPacket
    {
        public DraftCancelPacket() { }
    }

    /// <summary>C→S. Undo the requesting player's most recent action (player implied).</summary>
    [ProtoContract]
    public class UndoRequestPacket
    {
        public UndoRequestPacket() { }
    }

    /// <summary>C→S. Redo the requesting player's most recently undone action (player implied).</summary>
    [ProtoContract]
    public class RedoRequestPacket
    {
        public RedoRequestPacket() { }
    }

    /// <summary>C→S. Reports the client's preferred placement mode after the join policy is known.</summary>
    [ProtoContract]
    public class ClientPlacementModeRequestPacket
    {
        [ProtoMember(1)] public bool ClientOnly;

        public ClientPlacementModeRequestPacket() { }
        public ClientPlacementModeRequestPacket(bool clientOnly) { ClientOnly = clientOnly; }
    }

    /// <summary>A complete private-guide snapshot used only by the explicit push-to-server operation.</summary>
    [ProtoContract]
    public class ClientGuidePushDto
    {
        [ProtoMember(1)] public GuideDataDto Guide;
        [ProtoMember(2)] public ControlPointDto[] OriginalControlPoints;
        [ProtoMember(3)] public int OriginalConstraint;

        public ClientGuidePushDto() { }

        public static ClientGuidePushDto From(GuideData guide)
        {
            ControlPointDto[] original = null;
            if (guide.OriginalControlPoints != null)
            {
                original = new ControlPointDto[guide.OriginalControlPoints.Count];
                for (int i = 0; i < original.Length; i++)
                    original[i] = ControlPointDto.From(guide.OriginalControlPoints[i]);
            }

            return new ClientGuidePushDto
            {
                Guide = GuideDataDto.From(guide),
                OriginalControlPoints = original,
                OriginalConstraint = (int)guide.OriginalConstraint
            };
        }

        public GuideData ToGuideData()
        {
            GuideData guide = Guide?.ToGuideData();
            if (guide == null) return null;
            if (OriginalControlPoints != null)
            {
                guide.OriginalControlPoints = new List<ControlPoint>(OriginalControlPoints.Length);
                foreach (ControlPointDto point in OriginalControlPoints)
                    guide.OriginalControlPoints.Add(point?.ToControlPoint() ?? new ControlPoint());
            }
            guide.OriginalConstraint = (ShapeConstraint)OriginalConstraint;
            return guide;
        }
    }

    /// <summary>C→S. Uploads private guide snapshots after an explicit push-all request.</summary>
    [ProtoContract]
    public class ClientGuidePushPacket
    {
        [ProtoMember(1)] public ClientGuidePushDto[] Guides;

        public ClientGuidePushPacket() { }
        public ClientGuidePushPacket(ClientGuidePushDto[] guides) { Guides = guides; }
    }

    // ----------------------------------------------------------------------------------------------
    //  Packets — bidirectional (C→S as a request, S→C as the authoritative broadcast)
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Both directions. C→S: the points the lock-holder is moving. S→C: the same edits, now authoritative,
    /// for every client to apply. The whole drag coalesces into one undo entry on release (see the server
    /// handler), so live updates carry no command of their own.
    /// </summary>
    [ProtoContract]
    public class GuideUpdatePacket
    {
        [ProtoMember(1)] public byte[] GuideIdBytes;
        [ProtoMember(2)] public ControlPointEditDto[] Edits;

        public GuideUpdatePacket() { }

        public GuideUpdatePacket(Guid guideId, ControlPointEditDto[] edits)
        {
            GuideIdBytes = NetIds.ToBytes(guideId);
            Edits = edits;
        }

        public Guid GuideId() => NetIds.ToGuid(GuideIdBytes);
    }

    /// <summary>
    /// Both directions. C→S: insert a body point at the clicked position (the server derives the curve
    /// parameter and the landing index). S→C: the authoritative landing index + position for clients to
    /// insert. Index is unused on the C→S leg (server computes it).
    /// </summary>
    [ProtoContract]
    public class GuideInsertPointPacket
    {
        [ProtoMember(1)] public byte[] GuideIdBytes;
        [ProtoMember(2)] public int Index;
        [ProtoMember(3)] public Vec3Dto Position;

        /// <summary>
        /// Session-8 (additive field): when true this is a LOCK-IN-PLACE insert — the server snaps the
        /// position to the curve, creates the point born-locked, and does NOT retain the edit lock (no
        /// grab follows). When false (the default, and the meaning of every pre-existing packet): the
        /// classic body insert that is itself the start of a grab.
        /// </summary>
        [ProtoMember(4)] public bool Locked;

        public GuideInsertPointPacket() { }

        public GuideInsertPointPacket(Guid guideId, int index, Vec3Dto position, bool locked = false)
        {
            GuideIdBytes = NetIds.ToBytes(guideId);
            Index = index;
            Position = position;
            Locked = locked;
        }

        public Guid GuideId() => NetIds.ToGuid(GuideIdBytes);
    }

    /// <summary>Both directions. C→S: dispel a guide. S→C: a guide was removed; drop it.</summary>
    [ProtoContract]
    public class GuideDeletePacket
    {
        [ProtoMember(1)] public byte[] GuideIdBytes;

        public GuideDeletePacket() { }

        public GuideDeletePacket(Guid guideId) { GuideIdBytes = NetIds.ToBytes(guideId); }

        public Guid GuideId() => NetIds.ToGuid(GuideIdBytes);
    }

    /// <summary>Both directions. Toggle a guide's hidden flag.</summary>
    [ProtoContract]
    public class GuideHidePacket
    {
        [ProtoMember(1)] public byte[] GuideIdBytes;
        [ProtoMember(2)] public bool Hidden;

        public GuideHidePacket() { }

        public GuideHidePacket(Guid guideId, bool hidden)
        {
            GuideIdBytes = NetIds.ToBytes(guideId);
            Hidden = hidden;
        }

        public Guid GuideId() => NetIds.ToGuid(GuideIdBytes);
    }

    /// <summary>Both directions. Set a control point's locked-constraint flag.</summary>
    [ProtoContract]
    public class GuideLockPointPacket
    {
        [ProtoMember(1)] public byte[] GuideIdBytes;
        [ProtoMember(2)] public int Index;
        [ProtoMember(3)] public bool Locked;

        public GuideLockPointPacket() { }

        public GuideLockPointPacket(Guid guideId, int index, bool locked)
        {
            GuideIdBytes = NetIds.ToBytes(guideId);
            Index = index;
            Locked = locked;
        }

        public Guid GuideId() => NetIds.ToGuid(GuideIdBytes);
    }

    /// <summary>Both directions. Change a guide's voxel scale.</summary>
    [ProtoContract]
    public class GuideRescalePacket
    {
        [ProtoMember(1)] public byte[] GuideIdBytes;
        [ProtoMember(2)] public int Scale;

        public GuideRescalePacket() { }

        public GuideRescalePacket(Guid guideId, int scale)
        {
            GuideIdBytes = NetIds.ToBytes(guideId);
            Scale = scale;
        }

        public Guid GuideId() => NetIds.ToGuid(GuideIdBytes);
    }

    /// <summary>
    /// Both directions (F6 Move, protocol 17). Slides a whole guide by a world delta, shape untouched.
    /// The delta is carried in 1/16-block units as INTEGERS, not doubles: a move is only legal at a whole
    /// number of the guide's own voxels, so integers are both the exact representation and a free
    /// well-formedness check — a fractional nudge cannot even be expressed on the wire.
    /// </summary>
    [ProtoContract]
    public class GuideTranslatePacket
    {
        [ProtoMember(1)] public byte[] GuideIdBytes;
        [ProtoMember(2)] public int DeltaX;
        [ProtoMember(3)] public int DeltaY;
        [ProtoMember(4)] public int DeltaZ;

        public GuideTranslatePacket() { }

        public GuideTranslatePacket(Guid guideId, int deltaX, int deltaY, int deltaZ)
        {
            GuideIdBytes = NetIds.ToBytes(guideId);
            DeltaX = deltaX;
            DeltaY = deltaY;
            DeltaZ = deltaZ;
        }

        public Guid GuideId() => NetIds.ToGuid(GuideIdBytes);

        /// <summary>The delta as world units (sixteenths / 16). A fresh instance every call — never aliased.</summary>
        public Vec3d ResolveDelta() => new Vec3d(DeltaX / 16.0, DeltaY / 16.0, DeltaZ / 16.0);
    }

    /// <summary>
    /// Client → server (F12 Rotate, protocol 18). Turns a whole guide a quarter at a time about a world
    /// axis. Only the axis and the number of quarter turns cross the wire — the PIVOT is derived from the
    /// guide's own control points by the authority, so both sides cannot disagree about it and a client
    /// cannot nominate a pivot that would move the guide somewhere it should not go.
    /// </summary>
    [ProtoContract]
    public class GuideRotatePacket
    {
        [ProtoMember(1)] public byte[] GuideIdBytes;
        [ProtoMember(2)] public int Axis;            // PlaneAxis, pinned: X=0, Y=1, Z=2
        [ProtoMember(3)] public int QuarterTurns;    // +1 / -1; the authority normalises

        public GuideRotatePacket() { }

        public GuideRotatePacket(Guid guideId, PlaneAxis axis, int quarterTurns)
        {
            GuideIdBytes = NetIds.ToBytes(guideId);
            Axis = (int)axis;
            QuarterTurns = quarterTurns;
        }

        public Guid GuideId() => NetIds.ToGuid(GuideIdBytes);

        public PlaneAxis ResolveAxis() => (PlaneAxis)Axis;
    }

    /// <summary>
    /// Client → server (F7/F8 Transform pad, protocol 19). One compound pad action: an optional mirror, an
    /// optional rotation, and an optional translation, applied to the guide in place or to a fresh COPY of
    /// it. Covers every state of the pad's Move/Copy/Mirror toggles in one message, so a compound action is
    /// one server operation, one validation and one undo step.
    /// </summary>
    /// <remarks>
    /// The delta is in 1/16 units as INTEGERS — a move is only ever a whole number of the guide's own
    /// voxels, so a fractional nudge cannot be expressed. The mirror and rotation PIVOT never crosses the
    /// wire: the authority derives it from the guide itself, so the two sides cannot disagree and a client
    /// cannot nominate one that would put the guide somewhere it should not go.
    /// </remarks>
    [ProtoContract]
    public class GuideTransformPacket
    {
        [ProtoMember(1)] public byte[] GuideIdBytes;
        [ProtoMember(2)] public int DeltaX;
        [ProtoMember(3)] public int DeltaY;
        [ProtoMember(4)] public int DeltaZ;
        [ProtoMember(5)] public int MirrorAxis;      // PlaneAxis, or -1 for no mirror
        [ProtoMember(6)] public int RotateAxis;      // PlaneAxis; ignored when QuarterTurns is 0
        [ProtoMember(7)] public int QuarterTurns;
        [ProtoMember(8)] public bool AsCopy;

        public GuideTransformPacket() { MirrorAxis = -1; }

        public GuideTransformPacket(
            Guid guideId, int deltaX, int deltaY, int deltaZ,
            int mirrorAxis, PlaneAxis rotateAxis, int quarterTurns, bool asCopy)
        {
            GuideIdBytes = NetIds.ToBytes(guideId);
            DeltaX = deltaX;
            DeltaY = deltaY;
            DeltaZ = deltaZ;
            MirrorAxis = mirrorAxis;
            RotateAxis = (int)rotateAxis;
            QuarterTurns = quarterTurns;
            AsCopy = asCopy;
        }

        public Guid GuideId() => NetIds.ToGuid(GuideIdBytes);

        /// <summary>The delta as world units. A fresh instance every call — never aliased.</summary>
        public Vec3d ResolveDelta() => new Vec3d(DeltaX / 16.0, DeltaY / 16.0, DeltaZ / 16.0);

        public PlaneAxis ResolveRotateAxis() => (PlaneAxis)RotateAxis;
    }

    /// <summary>Both directions. Set a guide's projection mode and plane together.</summary>
    [ProtoContract]
    public class GuideSetProjectionPacket
    {
        [ProtoMember(1)] public byte[] GuideIdBytes;
        [ProtoMember(2)] public int Mode;
        [ProtoMember(3)] public int PlaneAxis;
        [ProtoMember(4)] public int PlaneOffset;

        public GuideSetProjectionPacket() { }

        public GuideSetProjectionPacket(Guid guideId, int mode, int planeAxis, int planeOffset)
        {
            GuideIdBytes = NetIds.ToBytes(guideId);
            Mode = mode;
            PlaneAxis = planeAxis;
            PlaneOffset = planeOffset;
        }

        public Guid GuideId() => NetIds.ToGuid(GuideIdBytes);

        public ProjectionPlane ResolvePlane() => new ProjectionPlane((PlaneAxis)PlaneAxis, PlaneOffset);
    }

    /// <summary>Both directions. Toggle a guide's filled flag (hollow vs filled).</summary>
    [ProtoContract]
    public class GuideSetFilledPacket
    {
        [ProtoMember(1)] public byte[] GuideIdBytes;
        [ProtoMember(2)] public bool Filled;

        public GuideSetFilledPacket() { }

        public GuideSetFilledPacket(Guid guideId, bool filled)
        {
            GuideIdBytes = NetIds.ToBytes(guideId);
            Filled = filled;
        }

        public Guid GuideId() => NetIds.ToGuid(GuideIdBytes);
    }

    /// <summary>S→C. Refreshes the invoking player's effective per-guide cap after an admin change.</summary>
    [ProtoContract]
    public class PlayerGuidePolicyPacket
    {
        [ProtoMember(1)] public int PerGuideVoxelCap;
        [ProtoMember(2)] public bool Jailed;

        public PlayerGuidePolicyPacket() { }
        public PlayerGuidePolicyPacket(int perGuideVoxelCap, bool jailed)
        {
            PerGuideVoxelCap = perGuideVoxelCap;
            Jailed = jailed;
        }
    }

    /// <summary>
    /// The server settings the in-game Admin panel may read and change (protocol 20). PINNED VALUES — this
    /// crosses the wire, so treat it exactly like the data-model enums: append only, never renumber.
    /// </summary>
    /// <remarks>
    /// NOT EVERY KEY IN layout.json IS HERE, deliberately. <c>requiredPrivilege</c> is a free-text privilege
    /// name and fumbling it in a GUI can lock every player — including the admin — out of the tool, which is
    /// exactly the situation you cannot then fix from inside the game. <c>undoHistoryDepth</c> is a memory
    /// trade-off nobody tunes in play. Both stay in the file, where changing them is a deliberate act.
    /// </remarks>
    public enum LayoutAdminSetting
    {
        PerGuideVoxelCap = 0,
        PerPlayerTotalVoxelCap = 1,
        TotalVoxelCap = 2,
        MaxGuidesPerPlayer = 3,
        MaxGuidesWorldWide = 4,
        AllowClientOnlyMode = 5,
        // RETIRED, and NOT reused — pinned numbers are append-only. Both settings still live in
        // layout.json, where changing them takes a restart; the panel no longer offers either and the
        // server no longer accepts them here, so a request naming one changes nothing.
        //   6 — chalk consumption, dropped v0.4.20 (human-directed).
        //   7 — admin lock-override, dropped v0.4.17 (human-directed).
        EnableChalkDurability = 6,
        AdminCanOverrideLocks = 7
    }

    /// <summary>
    /// S→C (protocol 20). The live server settings behind the settings page's Admin section, plus whether
    /// THIS player is allowed to change them. Sent to everyone on join and re-broadcast after every change.
    /// </summary>
    /// <remarks>
    /// SENT TO EVERY PLAYER, not just admins. The caps are not secret — a player already learns the
    /// per-guide cap from the bulk sync, and the HUD draws a gauge against it — and a non-admin needs the
    /// values anyway if the panel is ever to explain WHY a placement was refused. What is per-player is
    /// <see cref="CanEdit"/>, and it is advisory only: it decides whether the section is drawn, never
    /// whether a change is accepted. The server re-checks the privilege on every incoming request, because
    /// a modified client can set any flag it likes on its own copy of this packet.
    /// </remarks>
    [ProtoContract]
    public class LayoutAdminConfigPacket
    {
        [ProtoMember(1)] public int PerGuideVoxelCap;
        [ProtoMember(2)] public int PerPlayerTotalVoxelCap;
        [ProtoMember(3)] public int TotalVoxelCap;
        [ProtoMember(4)] public int MaxGuidesPerPlayer;
        [ProtoMember(5)] public int MaxGuidesWorldWide;
        [ProtoMember(6)] public bool AllowClientOnlyMode;
        [ProtoMember(7)] public bool EnableChalkDurability;
        [ProtoMember(8)] public bool AdminCanOverrideLocks;
        [ProtoMember(9)] public bool CanEdit;
        // The RECIPIENT's own per-player overrides (protocol 22), 0 when they have none. A personal
        // override silently beats every cap above it, so a panel that showed the server's numbers without
        // these was telling an admin their caps applied to them when they did not — which is exactly how a
        // forgotten 10,000,000-voxel override survived several rounds of cap debugging undetected.
        [ProtoMember(10)] public int YourVoxelCapOverride;
        [ProtoMember(11)] public int YourTotalVoxelCapOverride;
        [ProtoMember(12)] public int YourGuideLimitOverride;

        public LayoutAdminConfigPacket() { }

        /// <summary>True when this player's own limits are not the server-wide ones shown above.</summary>
        public bool HasPersonalOverride =>
            YourVoxelCapOverride > 0 || YourTotalVoxelCapOverride > 0 || YourGuideLimitOverride > 0;
    }

    /// <summary>
    /// C→S (protocol 20). Asks the server to change ONE admin setting. Booleans travel as 0/1 so a single
    /// packet covers every key; the server clamps, normalises, and may refuse.
    /// </summary>
    [ProtoContract]
    public class LayoutAdminConfigRequestPacket
    {
        [ProtoMember(1)] public int Setting;
        [ProtoMember(2)] public int Value;

        public LayoutAdminConfigRequestPacket() { }

        public LayoutAdminConfigRequestPacket(LayoutAdminSetting setting, int value)
        {
            Setting = (int)setting;
            Value = value;
        }
    }

    /// <summary>
    /// C→S (protocol 21). "Un-hide every guide I created." Carries no payload: the sender IS the
    /// argument, and letting a client name whose guides to reveal would be a permission hole.
    /// </summary>
    /// <remarks>
    /// WHY THE SERVER DOES THIS AND NOT THE CLIENT. The obvious client-side version — walk the local
    /// mirror, keep the guides whose creator is me — cannot work: <see cref="GuideDataDto"/> carries
    /// <c>CreatorName</c> for the HUD but has never carried the creator's UID, so every guide in a
    /// client's mirror has a null CreatorUid and the filter matches nothing. That was the v0.4.18 bug.
    /// Adding the UID to the DTO would have worked too, but it would broadcast every creator's UID to
    /// every client to serve one button; asking the side that already knows is both smaller and tighter.
    /// </remarks>
    [ProtoContract]
    public class GuideRevealMinePacket
    {
        public GuideRevealMinePacket() { }
    }

    // ----------------------------------------------------------------------------------------------
    //  Admin player roster (protocol 23) — the settings page's Players dialog
    // ----------------------------------------------------------------------------------------------
    //
    // The same ground the /layout info, jailroster and top commands cover, assembled server-side and sent
    // as data instead of as prose. It is ONE roster rather than three: the Players, Overrides and Jail
    // tabs are three views of the same rows, and fetching them separately would let the three disagree
    // about a player whose policy changed between requests.

    /// <summary>One player as the admin dialog sees them. Everything here is already resolved.</summary>
    [ProtoContract]
    public class PlayerRosterEntryDto
    {
        [ProtoMember(1)] public string Uid;
        [ProtoMember(2)] public string Name;
        [ProtoMember(3)] public bool Online;
        [ProtoMember(4)] public bool Jailed;
        [ProtoMember(5)] public int GuideCount;
        [ProtoMember(6)] public long VoxelTotal;
        /// <summary>Per-player overrides; 0 means "no override, the server default applies".</summary>
        [ProtoMember(7)] public int VoxelCapOverride;
        [ProtoMember(8)] public int TotalVoxelCapOverride;
        [ProtoMember(9)] public int GuideLimitOverride;
        /// <summary>What actually applies to them once overrides are folded in. 0 = unlimited.</summary>
        [ProtoMember(10)] public int EffectiveVoxelCap;
        [ProtoMember(11)] public int EffectiveTotalVoxelCap;
        [ProtoMember(12)] public int EffectiveGuideLimit;

        public bool HasOverride =>
            VoxelCapOverride > 0 || TotalVoxelCapOverride > 0 || GuideLimitOverride > 0;
    }

    /// <summary>One of a player's guides, for the Players tab's detail list.</summary>
    [ProtoContract]
    public class PlayerGuideDto
    {
        [ProtoMember(1)] public string ShapeName;
        [ProtoMember(2)] public int VoxelCount;
        [ProtoMember(3)] public int X;
        [ProtoMember(4)] public int Y;
        [ProtoMember(5)] public int Z;
        [ProtoMember(6)] public bool Hidden;
    }

    /// <summary>C→S. Asks for the admin player roster. Refused without controlserver.</summary>
    [ProtoContract]
    public class PlayerRosterRequestPacket
    {
        public PlayerRosterRequestPacket() { }
    }

    /// <summary>S→C. Every player Layout knows about, with their policy and usage.</summary>
    [ProtoContract]
    public class PlayerRosterPacket
    {
        [ProtoMember(1)] public PlayerRosterEntryDto[] Players;

        public PlayerRosterPacket() { }
    }

    /// <summary>C→S. Asks for one player's guide list, for the Players tab's detail pane.</summary>
    [ProtoContract]
    public class PlayerGuidesRequestPacket
    {
        [ProtoMember(1)] public string Uid;

        public PlayerGuidesRequestPacket() { }
        public PlayerGuidesRequestPacket(string uid) { Uid = uid; }
    }

    /// <summary>
    /// S→C. One player's guides, newest-largest first and capped — a prolific builder's full list would
    /// be thousands of rows nobody can read, and the dialog shows a "and N more" line instead.
    /// </summary>
    [ProtoContract]
    public class PlayerGuidesPacket
    {
        [ProtoMember(1)] public string Uid;
        [ProtoMember(2)] public PlayerGuideDto[] Guides;
        [ProtoMember(3)] public int TotalCount;

        public PlayerGuidesPacket() { }
    }

    /// <summary>Both directions. Select a 3D guide's persistent shell or structural wireframe.</summary>
    [ProtoContract]
    public class GuideSetWireframePacket
    {
        [ProtoMember(1)] public byte[] GuideIdBytes;
        [ProtoMember(2)] public bool Wireframe;

        public GuideSetWireframePacket() { }

        public GuideSetWireframePacket(Guid guideId, bool wireframe)
        {
            GuideIdBytes = NetIds.ToBytes(guideId);
            Wireframe = wireframe;
        }

        public Guid GuideId() => NetIds.ToGuid(GuideIdBytes);
    }

    /// <summary>
    /// Client → server (F5 chalk, protocol 4). An honest client reports a completed PRIVATE placement on a
    /// mixed Layout server so the server — which owns the inventory but cannot see private guides — applies
    /// the chalk charge to the held kit. Public placements never send this: the server charges those itself
    /// inside the create handler. The server validates everything it can (feature enabled, game mode, kit
    /// actually held) and clamps the cost to the legal 1–2 range.
    /// </summary>
    [ProtoContract]
    public class ChalkChargePacket
    {
        [ProtoMember(1)] public int Cost;

        public ChalkChargePacket() { }

        public ChalkChargePacket(int cost) { Cost = cost; }
    }

    /// <summary>
    /// Client → server (F5 inventory refill, protocol 5). The player right-clicked a held Chalking Powder
    /// stack onto a Chalking Kit's inventory slot; the server re-validates everything — the channel is
    /// permitted, the named slot really holds a kit, the player's cursor really holds powder — then
    /// consumes one powder and adds its chalk. Sent only after the client's own pre-checks pass, so a
    /// rejection here is a stale/hostile packet and is dropped silently.
    /// </summary>
    [ProtoContract]
    public class ChalkInventoryRefillPacket
    {
        [ProtoMember(1)] public string InventoryId;
        [ProtoMember(2)] public int SlotId;

        public ChalkInventoryRefillPacket() { }

        public ChalkInventoryRefillPacket(string inventoryId, int slotId)
        {
            InventoryId = inventoryId;
            SlotId = slotId;
        }
    }

    /// <summary>S→C. A player invoked /layout who; the client resolves its selected/aimed guide locally.</summary>
    [ProtoContract]
    public class GuideWhoQueryPacket
    {
    }

    /// <summary>
    /// S to C. The authoritative create pipeline rejected the player's provisional placement. The ordinary
    /// ingame error carries the explanation; this tiny result packet lets the renderer immediately discard
    /// a retained immense draft instead of waiting for a safety timeout.
    /// </summary>
    [ProtoContract]
    public class GuidePlacementRejectedPacket
    {
        [ProtoMember(1)] public int Reason;

        public GuidePlacementRejectedPacket() { }

        public GuidePlacementRejectedPacket(int reason) { Reason = reason; }
    }

    /// <summary>S to C. Personally enables or disables every Layout render pass for one player.</summary>
    [ProtoContract]
    public class GuideRenderingPacket
    {
        [ProtoMember(1)] public bool Enabled;

        public GuideRenderingPacket() { }

        public GuideRenderingPacket(bool enabled) { Enabled = enabled; }
    }

    // ----------------------------------------------------------------------------------------------
    //  Registration — the single source of truth for type order on BOTH sides
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Registers every Layout packet type on a channel, in one fixed order. Both the server and the client
    /// handler call this on their channel so the message-id assignment is identical on both ends. A side
    /// only sets handlers for the packets it receives, but BOTH sides must register the full set here, or
    /// the per-message ids would not line up. APPEND new packet types at the end only — never reorder.
    /// </summary>
    public static class LayoutPackets
    {
        // Overload resolution sends each handler to the right one: the server handler holds an
        // IServerNetworkChannel, the client handler an IClientNetworkChannel. The two bodies MUST register
        // the same types in the same order (the body is generated from RegistrationOrder() so they cannot
        // drift). The non-generic RegisterMessageType(Type) overload is used so the single ordered list
        // below is the only source of truth.

        /// <summary>Server-side registration. Call from ServerNetworkHandler with its channel.</summary>
        public static void RegisterMessageTypes(IServerNetworkChannel channel)
        {
            foreach (Type t in RegistrationOrder()) channel.RegisterMessageType(t);
        }

        /// <summary>Client-side registration. Call from ClientNetworkHandler with its channel.</summary>
        public static void RegisterMessageTypes(IClientNetworkChannel channel)
        {
            foreach (Type t in RegistrationOrder()) channel.RegisterMessageType(t);
        }

        /// <summary>
        /// The one fixed packet order, shared by both sides so message ids line up. APPEND new packet types
        /// at the end only — never reorder or remove, exactly like the pinned enum values.
        /// </summary>
        private static Type[] RegistrationOrder() => new[]
        {
            // server → client
            typeof(GuideBulkSyncPacket),
            typeof(GuideCreatePacket),
            typeof(GuideLockStatePacket),
            typeof(DraftAnchorBroadcastPacket),
            typeof(DraftAnchorRemovePacket),
            typeof(VoxelCapWarningPacket),
            // client → server
            typeof(GuideCreateRequestPacket),
            typeof(GuideGrabPacket),
            typeof(GuideReleasePacket),
            typeof(DraftStartPacket),
            typeof(DraftCancelPacket),
            typeof(UndoRequestPacket),
            typeof(RedoRequestPacket),
            // bidirectional
            typeof(GuideUpdatePacket),
            typeof(GuideInsertPointPacket),
            typeof(GuideDeletePacket),
            typeof(GuideHidePacket),
            typeof(GuideLockPointPacket),
            typeof(GuideRescalePacket),
            typeof(GuideSetProjectionPacket),
            typeof(GuideSetFilledPacket),
            // Session-8 additions (append-only, per the rule above)
            typeof(GuideCancelGrabPacket),
            // Session-9 additions
            typeof(GuideSetDivisionsPacket),
            // Session-11 additions
            typeof(GuideSetSidesPacket),
            typeof(GuideSpringBackPacket),
            // Client-only policy, mode, and explicit publication (append-only)
            typeof(ClientPlacementModePacket),
            typeof(ClientGuidePushRequestPacket),
            typeof(ClientGuidePushResultPacket),
            typeof(ClientPlacementModeRequestPacket),
            typeof(ClientGuidePushPacket),
            // F5 chalk durability (protocol 4)
            typeof(ChalkChargePacket),
            // F5 inventory refill (protocol 5)
            typeof(ChalkInventoryRefillPacket),
            // F5 refill channels became a client preference (protocol 6)
            typeof(ChalkRefillPrefsPacket),
            // 0.3.7: persistent 3D shell/wireframe mode (protocol 11)
            typeof(GuideSetWireframePacket),
            // 0.3.9: creator / last-sculptor HUD attribution (protocol 12)
            typeof(GuideHudMetadataPacket),
            // 0.3.13: /layout who asks the invoking client to inspect its current target (protocol 13)
            typeof(GuideWhoQueryPacket),
            // 0.3.22: live refresh for per-player per-guide cap overrides (protocol 14)
            typeof(PlayerGuidePolicyPacket),
            // 0.3.27: explicit rejection for provisional immense placements (protocol 15)
            typeof(GuidePlacementRejectedPacket),
            // 0.3.33: personal /layout on|off rendering control (protocol 16)
            typeof(GuideRenderingPacket),
            // 0.3.86: F6 Move mode — whole-guide translation (protocol 17)
            typeof(GuideTranslatePacket),
            // 0.3.90: F12 Rotate — whole-guide quarter turns (protocol 18)
            typeof(GuideRotatePacket),
            // 0.3.93: F7/F8 Transform pad — compound mirror / rotate / move, in place or as a copy (19)
            typeof(GuideTransformPacket),
            // 0.4.16: the settings page's Admin section — live server settings (protocol 20)
            typeof(LayoutAdminConfigPacket),
            typeof(LayoutAdminConfigRequestPacket),
            // 0.4.19: "Reveal All" — un-hide every guide the sender created (protocol 21)
            typeof(GuideRevealMinePacket),
            // 0.4.26: the Admin section's Players dialog (protocol 23)
            typeof(PlayerRosterRequestPacket),
            typeof(PlayerRosterPacket),
            typeof(PlayerGuidesRequestPacket),
            typeof(PlayerGuidesPacket)
        };
    }
}
