# Chained Is Hard

A BepInEx mod for **Flipping is Hard** that chains the players in a lobby together, the way
*Chained Together* does. Everyone is tied to their neighbour by a rope of a fixed length: walk
too far and it snaps taut and stops you, fall and you drag everybody down with you, die and the
rest of the chain is pulled to the checkpoint you respawned at.

Everyone in the lobby needs the mod installed. The host decides whether the chain is on, how
long it is and whether the rescue teleport is allowed; everything else is per player.

## Installing

Copy the `ChainedIsHard` folder into `BepInEx\plugins`. The config file lives next to the dll,
so the whole folder can be copied between installs and keeps your binds.

## Playing

| Key | What it does |
|---|---|
| `Insert` | Opens the settings menu. Arrows move and change, `R` restores a default. |
| `Q` | Calls a countdown on everyone's screen. Have the host press it — see below. |
| `F1` | Chain on/off. On a client this only takes effect once the host stops publishing. |

The countdown exists because the hard part of a cannon is not the jump, it is agreeing on when —
and voice chat has its own delay. A number counting down in the same place on three screens is
what lets three people move on the same beat.

> **Have the host call it.** In practice only the host's countdown reaches everybody. A client
> can start one and will see it on their own screen, but do not count on the others seeing it.

The corner readout shows the network role, the chain as a list of player ids with yours in
brackets, how far your furthest neighbour is and a bar that turns orange when the rope is
pulling. **If two machines show a different chain, something is wrong** — that list is derived
from the same numbers on every machine and should always match.

## Known limitations

**Cannons and boost pads are not perfect.** The chain gets out of their way while they fire (see
*How it works*) and the run is completable — you can reach the end with them — but expect the
occasional launch where somebody is left behind or lands somewhere odd. That is what the rescue
teleport is there for: it picks the group back up rather than ending the run. Friction, not a
wall.

**Only the host's countdown reliably reaches everybody.** A client can call one and will see it,
but the others may not.

**Running Chained Is Hard alongside Chaos Is Hard** makes both mods' settings sync slowly and
unreliably — they share one channel. See the end of *How it works*.

## Settings

**The host sets the chain for everybody.** Everything that decides how it behaves — the length,
the elasticity, the rescue, the launch handling — is published by the host and followed by every
client, so nobody can quietly play by their own rules. On a client those rows show the host's
value and cannot be edited; your own values stay in your config file and come back the moment the
host stops publishing. What stays yours: the drawn rope, the corner readout and your key binds.

| Setting | Default | What it does |
|---|---|---|
| `Chained` | on | Whether players are chained at all. **Host.** |
| `ChainLength` | 5 m | Rope between two neighbours. **Host.** |
| `Slack` | 0 | How far past the length it stretches before pulling. Raise it if standing still at full stretch shakes: some slack keeps network jitter below the threshold. |
| `Elasticity` | 0.98 | How much give the rope has. 0 stops you dead the moment it runs out. Higher leaves part of the stretch for the next step, so it hauls you back over a few tenths of a second. |
| `SpeedPull` | 1 | How much speed decides who wins the tug of war. At 0 a standing player anchors a launched one. Higher lets whoever is moving faster drag the slower one. |
| `Share` | 1 | How much of the overshoot you correct before speed has its say. |
| `LaunchSuspend` | 3 s | Minimum time the chain goes slack for a launch. See below. |
| `LaunchGrace` | 1 s | How long after hitting something the chain stays off, to cover the bounce. |
| `ShareLaunches` | on | Take the same kick as a neighbour who crosses a boost pad. |
| `LaunchRange` | 12 m | Distance from the pad past which a shared launch does not apply. |
| `CountdownSeconds` | 3 s | How long the shared countdown runs. |
| `RescueKeepsSpeed` | on | Arrive at a rescue with their speed instead of standing still. |
| `Damping` | 0.6 | How much of the speed you are moving away at is killed when it goes tight. 1 is a dead stop. |
| `MaxCorrection` | 0.6 m | Most you can be pulled in one physics step. Stops a lag spike from launching you. |
| `RescueEnabled` | on | Teleport to your neighbour when the rope is stretched past saving. **Host.** |
| `RescueDistance` | 20× | Chain lengths before the rescue fires. **Host.** |
| `RescueDelay` | 0 s | How long it has to stay that far before teleporting. |
| `RespawnGrace` | 3 s | How long after your own respawn you are the anchor everyone comes to. |
| `ShowChain` / `ChainWidth` / `ChainSag` / `ChainColor` | on, 0.07 m, 0.4, grey | The drawn rope. Droop is proportional to the slack left, so you can see the moment it is about to bite. |

## How it works

**The chain order is never sent over the network.** Players are sorted by their FishNet owner
id and linked to their neighbour, and since the server hands out those ids, every machine
derives the same chain from the same numbers on its own.

**The pull is local.** Remote players are driven by their NetworkTransform and are not
simulated on your machine, so only your own rigidbody is ever moved. Both ends run the same
solver against each other and each corrects its share, which adds up to a link that holds.
Inside the length nothing happens at all, so you forget the rope is there until it goes tight.

**Cannons and boost pads do not use the rope at all.** They fire far harder than any correction
is allowed to pull, and two players cannot hit one at the same instant when what each sees of the
other is a couple of hundred milliseconds old — coordinating the jump does not fix that, and the
chain meant to keep you together is what strands whoever is left behind.

**So during a launch there is no rope.** Touch a pad, or stand in a cannon while its timer runs,
and the chain goes slack. The rescue stands down too: mid-flight you are *supposed* to be far
apart, and dragging someone out of the air is the opposite of helping.

**The suspension is not on a clock.** A fixed number of seconds was never going to be right — a
cannon throws you for as long as it throws you, and the rope coming back mid-arc is exactly what
stops you short of where the level wants you. So it lasts until you hit something, plus
`LaunchGrace` to cover the bounce, since the first surface a cannon throws you into is rarely the
one you end up on. Cannons always aim at somewhere with a wall or a floor at the end of it, and
that surface is the honest end of the flight. A 30 second timeout covers the launch that never
lands, so a fall into nothing cannot leave the chain switched off for the rest of the run.

Two things do not count as landing, or the flight would end before it started: waiting inside a
cannon, and the frame you cross a pad — a pad throws you from the ground, so you are still
standing on it when it fires. The flight only starts counting surfaces once you have actually
left one. If nothing lifts you at all, the chain comes straight back.

Nothing is sent to arrange that. Everyone sees the same pads, the same cannons and — because the
cannon's timer is a FishNet `SyncStopwatch` — the same countdown, so every machine suspends at
the same moment on its own.

`ShareLaunches` covers the case where a neighbour crosses a pad you did not: you get the same
impulse, direction from the pad's own `FindShootDirection()` and force from its `boostForce`, and
fly the same arc from where you are standing. Detecting it late moves your starting point, not
your trajectory, which is why the lag does not break it. Pads come in two flavours —
`EHS.SceneBoostPad` and the networked one — and both are handled.

An earlier version watched for a neighbour moving fast and tried to drag you after them, and
another teleported you into a cannon a neighbour was waiting in. Both are gone. The first reacted
to the consequence instead of the cause and was always late; the second dragged players into
cannons they had not chosen to enter.

**Speed decides who gives way.** Since the only body you can move is your own, winning a tug of
war means correcting less than the other end does. Both machines measure the same two speeds —
a neighbour's from how far they travelled since the last physics step, because their velocity is
not really ours to read — and the faster player stops yielding while the slower one takes up the
whole correction. Without that, a player standing still anchors one that has just been fired out
of a cannon, and `SpeedPull` is what turns that around.

**The rescue decides locally which end moves.** Whoever just respawned is the anchor and does
not move; the other one comes to them, which is what turns one death into a shared trip back to
the checkpoint. When neither respawned, the higher owner id goes to the lower one, and the lower
one waits an extra 1.5 s before going itself in case the higher one was the anchor. No messages,
so nothing to lose.

**The settings and the countdown do travel**, over `PlayerNetworked.LookDirectionSyncVar` with a
numeric tag, polled rather than subscribed. There is no way to add a channel: FishNet generates
its serializers at compile time, so a broadcast type declared in a mod has none, and riding on
one of the game's own dies in Il2CppInterop.

A SyncVar is the server's to write, so the host sets it directly and a client asks for it through
`RequestUpdateLookDirectionServerRpc` — the round trip the game already uses to publish a look
direction. That is what lets anyone, not just the host, call a countdown everybody sees.

### Running it with Chaos Is Hard

Both mods write to that same SyncVar. The tags are different, so they ignore each other's
messages instead of acting on them, but each still overwrites the other's: run both at once and
the settings sync of both becomes slow and unreliable. The chain itself keeps working - it does
not depend on the network - and so does everything Chaos Is Hard does locally.

## Building

`build.bat` targets the Steam build, `build-playtest.bat` the 1.8 playtest on `D:`. Both need
BepInEx 6 IL2CPP installed in the game and the game run once so the interop assemblies exist.
Two installs also make a two player test possible on one machine.

```
dotnet build -c Release -p:GameDir="path\to\game" -p:Deploy=true
```
