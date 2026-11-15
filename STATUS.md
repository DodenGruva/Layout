# Layout — STATUS

**Regenerated at v0.5.0 on 2026-11-15.** Session 46 covers the fine-resolution trials and the release.

## Product and release

Layout is a visual construction and chiseling guide mod for **Vintage Story 1.22.x** and **.NET 10**. Players place translucent plans and build against them by hand. Public guides are server-authoritative. Private guides are stored locally for the current player and world.

| | |
|---|---|
| **Current build** | **v0.5.0**, regular Release configuration |
| **Release artifact** | `Layout0.5.0.zip`, 42 entries, 394,085 bytes |
| **DataVersion** | **18** |
| **Wire protocol** | **33** |
| **Source files** | **92** |
| **Shape catalog** | **16 types / 22 picker tiles** |

## Player-facing changes

- **Enable 1/32 and 1/64 Resolutions** exposes the compact fine-scale tiles. The usual 1/16 chisel resolution remains the default.
- Placement on a chiseled block targets the selected cut face in the interior. **Display Voxel Grid** follows the selected scale. **Outline Targeted Voxel** starts on and adapts its contrast to the block.
- Grid-aligned circles use symmetric rim coverage. Ctrl aligns the diameter and accepts both odd and even sizes. Markers can span two voxels across seams, and the opposite green midpoint is a visual reference.
- New hollow 2D guides normally connect through shared voxel faces. **Enable Thin Shapes** lets diagonal steps touch at corners. The choice is saved on each new guide. Filled coverage and the recorded appearance of existing guides are preserved.
- Bounded 2D outlines check the voxel cap during aim motion. Expensive fills still defer costly checks.

## Upgrade and compatibility

The release uses the `layout-grid64` channel. Use Layout 0.5.0 on both the client and server for public guides. Earlier packets carry voxel coordinates in different units.

On first load, the release imports older regular public and private guide data into the new store, preserving world-space placement and scale. The previous regular and experimental stores remain separate. Existing regular client preferences import into `layout-client-grid64.json`. The non-experimental mod metadata is used in the packaged release.

## Verification

- Debug and Release compiled cleanly.
- Local regression checks cover 16-to-64 grid import, one-time rescaling, backup recovery, fine scales, chiseled targeting, circle marker parity, outline geometry, voxel counts and protobuf round-trips.
- The release archive contains the Release assembly and the original metadata description. The ZIP SHA-256 is `43E2577B26CE3EEF1898E98BA852DBE2B4CB3F1A5D2C8D49403D2F54737DDB59`.

## Open verification

The open-item ledger is `dev/TODO.md`. Dedicated-server claim tests, compound Transform undo and rollback across clients, cancellation deep in public geometry work, and save timing diagnostics remain open. See `dev/sessions/SESSION_46.md` for the release details and `dev/WIRE_HISTORY.md` for packet and save compatibility.
