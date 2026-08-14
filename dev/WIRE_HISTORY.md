# Layout — wire history: protocol and DataVersion ledger

> **Tier 3 — history. Append-only.**
>
> **This is a correctness aid, not tidiness.** Packet registration is append-only and renumbering is
> forbidden (`dev/GOTCHAS.md` **G1**), so *"what did slot 6 used to mean"* is load-bearing. Before this file
> existed the answer lived only as prose scattered across 26 session records plus a code comment.
>
> **The authoritative source is `src/Network/PacketTypes.cs`** — its `RegistrationOrder()` carries a
> per-packet comment naming the protocol version each was added at, and `LayoutChannel.ProtocolVersion` is
> the current number. This ledger was built by reading that list, not by reassembling prose. **If the two
> ever disagree, the source is right and this file is wrong.**

**Current: DataVersion 14, protocol 29.**

---

## Protocol version ledger

One row per bump. "Packets added" is the exact set appended to `RegistrationOrder()` at that version;
**their position in that list is their wire identity and can never change.**

| Proto | Version | Session | Packets added | What it was for |
|---|---|---|---|---|
| 1 | — | 8 | the original set | Bulk sync, create, lock, draft anchors, cap warning; grab/release; update/insert/delete/hide/lock-point/rescale/projection/filled |
| 2 | — | 12 | `ClientPlacementModePacket`, `ClientGuidePushRequestPacket`, `ClientGuidePushResultPacket`, `ClientPlacementModeRequestPacket`, `ClientGuidePushPacket` | **F4 client-only policy, mode, and explicit publication.** Still probed at runtime as `ProtocolVersion >= 2` |
| 3 | — | ~14 | *(none)* | Field/semantic change only |
| 4 | v0.2.x | 15 | `ChalkChargePacket` | **F5 chalk durability** |
| 5 | v0.2.1x | 16 | `ChalkInventoryRefillPacket` | F5 inventory refill |
| 6 | v0.2.22 | 17 | `ChalkRefillPrefsPacket` | Refill channels became a **client preference**, reported to the server on join. ⚠️ **Two `GuideBulkSyncPacket` flags were left declared-but-dead as padding at this bump** — they are not reusable |
| 7 | v0.2.24 | 18 | *(none)* | Tapered Cylinder rim field |
| 8–10 | — | 19–20 | *(none)* | Field/semantic changes only |
| 11 | v0.3.7 | 21 | `GuideSetWireframePacket` | Persistent 3D Shell/Wireframe mode |
| 12 | v0.3.9 | 22 | `GuideHudMetadataPacket` | Creator / last-sculptor HUD attribution |
| 13 | v0.3.13 | 22 | `GuideWhoQueryPacket` | `/layout who` asks the client to inspect its own target |
| 14 | v0.3.22 | 23 | `PlayerGuidePolicyPacket` | Live refresh for per-player per-guide cap overrides |
| 15 | v0.3.27 | 23 | `GuidePlacementRejectedPacket` | Explicit rejection so an immense draft is discarded at once rather than on a timeout |
| 16 | v0.3.33 | 23 | `GuideRenderingPacket` | Personal `/layout on\|off` rendering control |
| 17 | v0.3.86 | 30 | `GuideTranslatePacket` | **F6 Move** — whole-guide translation |
| 18 | v0.3.90 | 31 | `GuideRotatePacket` | **F12 Rotate** — whole-guide quarter turns |
| 19 | v0.3.93 | 31 | `GuideTransformPacket` | **F7/F8 Transform pad** — compound mirror/rotate/move, in place or as a copy |
| 20 | v0.4.16 | 33 | `LayoutAdminConfigPacket`, `LayoutAdminConfigRequestPacket` | The settings page's **Admin section** — live server settings |
| 21 | v0.4.19 | 33 | `GuideRevealMinePacket` | **Reveal All** — server-side, because `GuideDataDto` has never carried `CreatorUid` so a client-side ownership filter matches nothing |
| 22 | — | 33 | *(none)* | Field/semantic change only |
| 23 | v0.4.26 | 33 | `PlayerRosterRequestPacket`, `PlayerRosterPacket`, `PlayerGuidesRequestPacket`, `PlayerGuidesPacket` | The **Players dialog** (read-only) |
| 24 | v0.4.31 | 34 | `PlayerPolicyEditPacket`, `PlayerJailPacket` | The Players dialog **became editable**. Jail is a separate, confirmed packet — not folded into the policy edit |
| 25 | v0.4.44 | 39 | *(none)* | **`VoxelCapWarningPacket` gained `CapKind`** (`[ProtoMember(4)]`), so the HUD can name WHICH of the four caps refused an edit instead of saying "over cap". Field append; an older server leaves it 0 and the client shows the old wording |
| 26 | v0.4.46 | 39 | *(none)* | **`VoxelCapKind` gained `GuideCount = 5`** — the guide-COUNT caps reach the HUD now. Enum append, but the *meaning set* of a field clients read grew, so the number moved |
| 27 | v0.4.60 | 41 | *(none)* | **`GuideShapeType` gained `Roundover = 15`** — a new meaning carried by existing guide/create DTO fields. The client records the server protocol and blocks public Roundover placement against older servers; private placement remains local |
| 28 | v0.4.67 | 42 | *(none)* | **Roundover's shared chain gained the profile-first meaning.** New creates carry the open sweep route followed by two terminal Primary profile handles; `GuideCreateRequestPacket.Closed = true` selects that constructor on authority but does not make the stored Roundover closed. Public placement is gated to protocol 28; legacy one-handle guides remain readable |
| 29 | v0.4.82 | 45 | *(none)* | **`GuideDataDto` appended `ArchUsesShapePlaneAxis` at field 26.** For Arch/Half-circle only, true makes the existing `ShapePlaneAxis` define the curve plane; absent/false preserves the legacy world-vertical interpretation. This is a field and geometry-semantic change, so both sides must agree even though no packet type was added |

**Rows marked *(none)*** bumped the protocol without appending a packet — a field was added to an existing
packet, or a meaning changed. The version still moved because both sides must agree.

---

## Retired-but-reserved slots

**Nothing here may ever be reused.** These are pinned positions whose meaning was withdrawn; the number
survives so that older clients' bytes still land where they expect.

| Where | Slot | Was | Retired |
|---|---|---|---|
| `LayoutAdminSetting` | **6** | Chalk consumption toggle | v0.4.20, human-directed |
| `LayoutAdminSetting` | **7** | Admin lock-override toggle | v0.4.17, human-directed |
| `GuideBulkSyncPacket` | 2 flags | Refill-channel flags | protocol 6 — left as dead padding when refill became a client preference |

The two `LayoutAdminSetting` values are **still declared** (`EnableChalkDurability = 6`,
`AdminCanOverrideLocks = 7`) and both settings still exist in `layout.json`, where changing them takes a
server restart. What was withdrawn is the *wire path*: the panel no longer offers either and **the server no
longer accepts them here, so a request naming one changes nothing.** That is deliberate — an inert slot is
safe, a renumbered one is not.

⚠️ **That last sentence was true of STATE and false of the REPLY until v0.4.38.** A request naming 6 or 7
passed `Enum.IsDefined`, fell into the switch's `default:`, and then ran the whole tail: `layout.json`
rewritten, the change logged, and the admin told *"EnableChalkDurability is now unlimited"* — for a setting
that had not changed and is not a number. Both slots are now answered before the switch, with the truth and
nothing else. **A retired wire slot has to be inert in what it says as well as in what it does.**

---

## Pinned enum values carried as packet payload

Not every wire-pinned number is a packet position. These enums cross the wire **as integer fields**, so G1's
append-only rule governs them too — a renumbering here is as breaking as one in `RegistrationOrder()`, and
far easier to do by accident because the enum looks like ordinary code.

| Enum | Carried by | Rule |
|---|---|---|
| `LayoutAdminSetting` | `LayoutAdminConfigRequestPacket.Setting` | Slots 6 and 7 retired but declared — see above |
| `GuideOpStatus` | `GuidePlacementRejectedPacket.Reason` | **Append only.** `RejectedEmpty` was appended last at v0.4.37 |
| `VoxelCapKind` | `VoxelCapWarningPacket.CapKind` | **Append only.** `GuideCount` appended last at v0.4.46 (protocol 26). ⚠️ **This one IS read by the client**, unlike `GuideOpStatus` below — `GuideHud` switches on it — so an insertion would silently re-label every value after it. `0` is `Unspecified` on purpose: it is what a pre-25 server sends AND what the client normalises any unrecognised value to, so both collapse to the old generic wording |
| `GuideShapeType` | guide state and create/update DTO fields | **Append only.** `Roundover = 15` was appended at v0.4.60 (protocol 27). Its chain gained the two-profile-handle meaning at v0.4.67 (protocol 28). Public creation is protocol-gated client-side; older one-handle Roundovers remain readable |

⚠️ **Today's client ignores `GuidePlacementRejectedPacket.Reason` entirely** — it fires a parameterless event
and never reads the int. That is *why* appending to `GuideOpStatus` is safe without a protocol bump. **It is
not a licence to renumber:** the moment any client reads that field, every value behind an inserted member
means something else. Append, always.

---

## DataVersion ledger

DataVersion stamps the **saved guide record**, not the wire. `GuideData.CurrentDataVersion` is authoritative.

| DV | Session | What changed |
|---|---|---|
| 5 | 9 | Extended shape catalog and division marks |
| 6–7 | 11 | The 3D volume family |
| 8 | 13 | — |
| 9 | 20 | Polygonal volumes |
| 10 | 21 | Cached display name / voxel count / dimensions (v0.3.3, the placed-behemoth safeguards) |
| 11 | 21 | Adaptive large-guide / structural wireframe persistence |
| 12 | 22 | — |
| **13** | 33 | **The Rectangle/Box re-gesture** — Rectangle became 3 clicks, Box 4, Square stayed 2 |
| **14** | 45 | **Plane-aware Arch/Half-circle encoding.** New records set `ArchUsesShapePlaneAxis`; false or absent records retain legacy world-vertical geometry |

**The ledger starts at DV 5**, which is as far back as the session records reach. What DataVersions 2–4
added is recorded in `dev/ARCHITECTURE.md` → *Data & wire* (v4 `Constraint` + `ShapePlaneAxis`, v3
`CreatorUid`, v2 `Projection`/`Plane`/`IsFilled`); no session is claimed for them here rather than guessed.

⚠️ **Legacy encodings are READ IN PLACE, never migrated.** Two-point rectangles and three-point boxes
reproduce to the voxel from their old encoding. Arch/Half-circle records without
`ArchUsesShapePlaneAxis = true` likewise keep their former world-vertical geometry rather than being silently
reinterpreted from an old `ShapePlaneAxis` value. This is not laziness: **shapes are adopted on renderer
worker threads**, so rewriting the shared control-point list from a shape would be a data race. Reading the
old form is the safe design, not a temporary accommodation.

---

## When you bump either number

1. Append the packet to the **end** of `RegistrationOrder()` in `src/Network/PacketTypes.cs`, with a comment
   naming the version — that comment is what keeps this ledger honest.
2. Bump `LayoutChannel.ProtocolVersion` (and/or `GuideData.CurrentDataVersion`).
3. Add a row here.
4. If you withdrew a meaning rather than adding one, add a **retired-but-reserved** row and leave the value
   declared.

**Never reorder, never renumber, never reclaim.** See `dev/GOTCHAS.md` G1.
