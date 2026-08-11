# Pending items to re-check during a game

Working notes, not documentation. Each item below is **diagnosed but not confirmed** — a log line was added
that fires only when the suspected cause actually happens. If you hit the symptom in game, tell Claude which
item it was and hand over the logs; the marker turns a guess into a fact and the fix follows from it.

**Logs live in:** `Documents\Mount and Blade II Bannerlord\Coop Crash Reports\<newest session>\logs\`
(`Coop_server.log`, `Coop_client.log`, `rgl_log_errors_*.txt`)

Search each log for the marker in the item. If the marker is **absent**, that is just as informative — it rules
the suspected cause out and points somewhere else.

---

## Let the watcher do it

Rather than remembering which bug happened when, start this before playing and it records every marker hit:

```powershell
& "$env:USERPROFILE\Desktop\Bannerlord co op\Scripts\Watch-PendingItems.ps1"           # follow until Ctrl+C
& "$env:USERPROFILE\Desktop\Bannerlord co op\Scripts\Watch-PendingItems.ps1" -Once     # scan and exit
& "$env:USERPROFILE\Desktop\Bannerlord co op\Scripts\Watch-PendingItems.ps1" -Summary  # print the report
```

Findings land in `Desktop\Bannerlord co op\PendingFindings\` — `findings.md` grouped by item with the count,
first/last time, the next step, and the last few matching lines; `findings.jsonl` for anything that parses.
Items that never fired are listed under **Not seen**, because absence is a result too.

It follows every log it can find (both dedicated-server roots and every `Coop_client*.log`), tracks byte
offsets so a re-run never re-reports, and de-duplicates on item+line.

At the end of a session, hand over `findings.md` and the answer to "which of these did we hit, and what
next" is already written down.

---

## 1. Siege: nobody attacks the besieging player

**Symptom** — You besiege with a large force. The garrison does not sally out and the relief army outside does
not attack, even when together they outnumber you. You take the town uncontested.

**Marker** — `[SiegeRelief]` in the **server** log.

**How to read it**

| What you see | Meaning | Fix |
|---|---|---|
| `DIVERTED to a conversation instead of a battle` | The AI did try to attack, and coop turned the encounter into a conversation. | Add a besieger-camp carve-out next to the existing `IsGarrisonSortie` one |
| `left to vanilla: one of the parties is already in a map event` | Coop stayed out of it — the AI's attack was allowed through and still produced nothing. | Cause is upstream in AI decision-making (`AiMilitaryBehavior` / army formation), not in encounters |
| *No `[SiegeRelief]` lines at all* | The AI never even attempted an encounter. | Same as above — AI never chose to engage |

**Why it matters** — the first row and the other two need opposite fixes, and they are indistinguishable from
inside the game.

---

## 2. Battle size exceeded — far more troops on the field than the cap

**Symptom** — A battle fields visibly more troops than the battle-size setting allows.

**Confirmed** — measured across 3397 samples: `battleSize=400` reaching **668**, `battleSize=320` reaching
**518**. This one is real and already proven; only the *cause* is unconfirmed.

**Marker** — `no side total from the server, so this client is claiming the FULL allocation` in **any client**
log.

| What you see | Meaning | Fix |
|---|---|---|
| Marker present | A client could not tell how big its side was and claimed the whole allowance. Several clients doing this multiplies the side. | Small, safe change to the share calculation |
| Marker absent, but `[BattleSize]` still shows `total > battleSize` | Over-supply comes from the late-reserve fallback instead. | Different fix, in the reserve/fallback path |

Also useful: grep `[BattleSize]` and compare `onField(total=…)` against `battleSize=…`.

---

## 3. Lords stand around instead of joining your battle

**Symptom** — Nearby friendly or hostile lords watch a battle you are in and never join.

**Marker** — `could not be assigned a side because the battle's parties are not all resolved` in the **server**
log.

| What you see | Meaning | Fix |
|---|---|---|
| Marker present | Every nearby lord was refused for a reason unrelated to them — the battle's own state could not be evaluated. | Make the resolution guard server-aware (the server's state is authoritative and does not need client-side protection) |
| Marker absent, `0 nearby parties would join` | The selector genuinely found nobody eligible. | Look at the join window (`outside the AI join window`) next |

**Note** — behaviour here is currently **unchanged**. Only the reporting improved, on purpose, until this is
confirmed.

---

## 4. Battle crashes for a specific player

**Symptom** — One player's client consistently crashes or hangs when a battle starts.

**Needed** — that player's own logs. Everything gathered so far is from the host and one client, and a crash on
another machine leaves no trace in either.

**Marker** — `DUPLICATE HERO` in the **server** log. 41 genuine occurrences were found, with one hero present in
three parties of a single battle; per the engine's own behaviour that leaves the mission without a controllable
player agent and deployment never finishes.

A mitigation is already in place (the duplicate listing is dropped, keeping the copy on the party the hero
actually belongs to), so this may already be fixed. If crashes continue, the client log will say whether it is
this or something else.

---

## 5. Party screen refuses or partly applies your edits

**Symptom** — Discarding or moving troops does nothing, or applies only partly.

**Markers** — in the **server** log:

- `Clamped troop roster delta` — your edit was applied to what was actually there. Expected occasionally; the
  screen refreshes and you are told.
- `Rejected party changes` — should now be rare. If you see it, the request was malformed rather than stale,
  and that is worth investigating.

---

## 6. Troops spawn missing / no Ready button after reloading

**Symptom** — After leaving a battle to the main menu and loading again, a new battle has no troops and no
Ready button.

**Marker** — `A campaign loaded while the spawn gate still held` in the **client** log.

Believed **fixed**. Seeing this marker means the fix caught a real leak — good. Seeing the *symptom* without the
marker means there is a second cause still to find.

---

## 7. Join refused — party count mismatch

**Symptom** — A player joins, never reaches the map, and the host's campaign stays paused with
*"N clients are catching up. Unable to change time."*

**Root-caused and fixed 2026-08-11**, so a hit here is a **regression**, not a diagnosis.

**Marker** — `party count mismatch (baseline=` in that player's **client** log.

The party is not missing, it is `IsActive=false` on the client while active on the server — and
`CampaignObjectManager.MobileParties` only holds active parties, so the counts disagree. The same line now
names the culprit via `onServerNotOnClient` / `onClientNotOnServer` / `aliased` /
`resolvedButNotInClientCollection`; read those before theorising.

**Do NOT "fix" this by tolerating the mismatch.** That was tried: the client joins with a world missing its
own party state and the player cannot move — a worse symptom that looks like a fix.

---

## 8. Join stalled and the peer was dropped

**Symptom** — A joining player is disconnected instead of hanging forever.

**Marker** — `without the join advancing` in the **server** log.

This is the retry cap working as designed: a join that consumes 25 baselines without progressing is dropped
with a reason, rather than resending ~5.5 MB about nine times a second indefinitely (which is what it used
to do, per stuck peer). The line names the phase and the last trigger, and that client's log names the
diverging parties.

---

## 9. Join baseline corrected activation

**Marker** — `[PartySync] Join baseline corrected activation` in a **client** log.

Working as intended — one line per join is normal. What matters is the **count**: a steady rise means
park/restore is drifting more than it should, since each correction is a party whose active state the two
sides disagreed on.

---

## Fixed 2026-08-11 — do not re-investigate

- **Join refused (party count mismatch).** Activation is now part of the join baseline and applied before
  validation. Note `MobileParty.IsActive` is a bare field write that does NOT move a party in or out of
  `MobileParties` — the fix needed `AddMobileParty`/`RemoveMobileParty` as well.
- **Player immovable for ~30–60s after joining.** `SettlementInterface.EndSettlementEncounter` deliberately
  calls `SetMoveModeHold` twice so a party cannot walk back into the settlement it just left. Nothing
  released it, and vanilla's `SetMoveGoToPoint` sets target and behaviour without touching `PartyMoveMode` —
  so every click landed as `target=(valid) shortTerm=GoToPoint moveMode=Hold` and the party stood still. A
  player's own move order now releases that Hold.

  Seven other causes were disproven along the way by measurement — AI behaviour, `IsActive`, control
  registration, the coop order gate, the loading overlay, campaign pause, and terrain readiness. If this
  recurs, instrument the order path before theorising; the answer came from a stack trace, not from reading
  code.

---

## Known, not scheduled

- **`ResetMovementToHold` is dead code.** The flag is serialized and asserted in three test assertions but
  **no production code reads it**. Anything that sets it silently does nothing — that cost a wasted fix
  cycle. Honour it in `ApplyBehavior` or delete it; leaving it looking functional is the hazard.
- **Guards keyed on "is this registered?" cannot tell "registered YET".** During world load registration is
  still filling, so anything vanilla mutates in that window slips past unguarded. This produced two separate
  bugs tonight and is not fixed as a class, only per-site.
- **XP is lost whenever troops are transferred between parties.** Pre-existing, unrelated to recent work.
  A clean transfer of 4 troops carrying 20xp lands with 0 on both sides. Only affects progress toward the next
  troop tier.
- **`MobilePartyBehaviorSnapshotTests.TryCreate_UnregisteredInteractable` fails.** Pre-existing, in files that
  were already modified before this work started.
