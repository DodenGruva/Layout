# SESSION 27 — v0.3.53: per-player cumulative-cap overrides and final release

**Checkpoint:** Layout v0.3.53 is the final release checkpoint. It is built, packaged, documented, and
ready on `main`. DataVersion remains **12**, wire protocol remains **16**, and the source tree remains
**77 C# files**. The runnable package is `..\LayoutZips\Layout0.3.53.zip`.

---

## 1. Why this final change was needed

v0.3.50 introduced two distinct public-guide limits:

- `perGuideVoxelCap` limits one guide, with a default of 500,000.
- `perPlayerTotalVoxelCap` limits all currently existing guides attributed to one original creator, with a
  default of 1,000,000.

The existing `/layout voxelcap <player> <number>` command overrides only the first limit. That distinction is
intentional and remains explicit, but it left administrators without a way to grant a trusted builder more
cumulative capacity without raising the server-wide default for everyone.

---

## 2. New command

Administrators with `controlserver` may now use:

```text
/layout totalvoxelcap <player> <number>
```

- A positive number sets that player's persistent cumulative public-guide voxel cap.
- `0` removes the custom value and restores `perPlayerTotalVoxelCap` from `layout.json`.
- The command accepts online or known offline players through the existing player resolver.
- The response reports both the effective cap and current attributed usage.
- Lowering the cap below current usage never deletes guides. Existing state may remain or shrink, but cannot
  grow until usage is back within the effective limit.

`/layout voxelcap` remains the acting player's per-guide override. `/layout totalvoxelcap` follows original
creator attribution. Therefore, a collaborator with a high per-guide allowance still cannot grow someone
else's guides past that original creator's cumulative allowance.

---

## 3. Persistence and enforcement

The world-scoped `LayoutAdminPolicyManager` now persists `PerPlayerTotalVoxelCap` alongside jail state,
guide-count overrides, and per-guide overrides. Its internal policy format advances from version 1 to
version 2; the new JSON field is additive, so existing policy records load with no cumulative override.

`GuideManager` resolves the effective cumulative cap by original creator at the enforcement point. This
covers ordinary and prepared immense creation, restore/push/redo, ordinary mutations, exact-count early
limits, and prepared immense sculpt commits. Queue-time immense limits and final manager validation both use
the effective value, so the command cannot be bypassed by choosing a different operation path.

`/layout info` now reports:

- server-default cumulative cap and number of custom cumulative overrides;
- a player's attributed usage against their effective cumulative cap;
- the custom cumulative value or “none (server default).”

No network packet or client save change was required. Enforcement and administrative policy remain
server-side; DataVersion 12 and protocol 16 are unchanged.

---

## 4. Verification and package

Focused smoke verification covered:

1. rejection at the default cumulative cap;
2. acceptance after raising one creator's override;
3. rejection at the raised cap;
4. persistence round-trip;
5. `0` restoring the server default;
6. another creator remaining independent.

Result:

```text
PASS guide=17 default=17 override=34 alice=34 bob=17
```

Final Release build: **0 warnings, 0 errors**.

`Layout0.3.53.zip` verification:

- **40 entries**
- **37 assets**
- root `Layout.dll`, `modinfo.json`, and `modicon.png`
- forward-slash entry paths
- packaged manifest version **0.3.53**
- DLL SHA-256:
  `1DD24C9F6F7105E88414C6BD3E3341C84935D1620BDDEC01CEEF68113861A3DE`
- ZIP SHA-256:
  `5DDA21A1BEE1DD19E465F8ED5FFC62AC89282A354030E9A09BE1B7A5B3E783B3`

---

## 5. Final-release state

v0.3.53 retains the v0.3.42 renderer baseline restored in v0.3.49, the v0.3.50 persistent visibility and
creator-budget defaults, and the v0.3.51–v0.3.52 off-state guidance/tool lockout. The final administrative
capacity model is:

| Scope | Default/command |
|---|---|
| One guide | `perGuideVoxelCap` / `/layout voxelcap` |
| One original creator's existing public guides | `perPlayerTotalVoxelCap` / `/layout totalvoxelcap` |
| Entire world | `totalVoxelCap` |
| Absolute safety for one guide | 10,000,000, never overridable |

Future work should begin from field reports or an explicit new feature request rather than reopening the
rejected spatial/greedy renderer path.
