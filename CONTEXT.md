# RoR2 Latency Mod

Client-only mod that hides network round-trip delay on specific survivor skills for a player who is not the host.

## Language

**Host**:
The player whose game is the server; runs vanilla code, never has this mod installed.
_Avoid_: server owner

**Remote Client**:
The local player connected to someone else's Host; the only side this mod runs on.
_Avoid_: guest, non-host

**Round Trip**:
One message from Remote Client to Host plus the Host's reply that the skill waits on before something visible happens.

**Skill Patch**:
A self-contained unit that hides the Round Trips of one skill; can be toggled and disables itself on failure without affecting other Skill Patches.
_Avoid_: plugin, module, fix

**Host Patch**:
A Skill Patch that changes the Host's own logic and only runs when the local player is the Host; as a Remote Client the mod is always pure client.
_Avoid_: server fix

**Prediction**:
Showing on the Remote Client what the Host will most likely decide, before the Host's reply arrives.

**Reconciliation**:
Correcting a Prediction once the Host's authoritative result arrives, smoothly where possible.
_Avoid_: rollback, resync

**Authoritative Result**:
Whatever the Host decides (damage, hits, final positions it owns); always wins over a Prediction.

**Start Position Lead**:
Advancing the dash start position the Remote Client reports to the Host by the distance travelled during one-way latency, so the Host's authoritative position lands near where the player already is.
_Avoid_: teleport, position spoof

**Ghost Dash**:
A locally simulated dash shown before the Host's dash arrives; experimental and off by default.

**Network Mod List**:
The game's list of mods every lobby member must share; joining fails if it differs, so this mod must never appear in it.

## Relationships

- Each **Skill Patch** targets exactly one skill and may contain several **Predictions**
- A **Prediction** is always followed by **Reconciliation** against the **Authoritative Result**
- Damage is always an **Authoritative Result**; never predicted as final

## Flagged ambiguities

- "延迟" covered three distinct symptoms: input delay before movement, grapple shown hooked but not pulling, grapple failing to hook at all. Treated as separate targets.
