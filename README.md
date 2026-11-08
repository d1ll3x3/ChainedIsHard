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
| `Q` | Calls a countdown on everyone's screen. Anyone can press it. |
| `F1` | Chain on/off. On a client this only takes effect once the host stops publishing. |

The countdown exists because the hard part of a cannon is not the jump, it is agreeing on when —
and voice chat has its own delay. A number counting down in the same place on three screens is
what lets three people move on the same beat. It ends on the same network tick on every screen,
not merely starts at the same time: the time the call spent reaching each machine is taken off
the count there.

The corner readout shows the network role, the chain as a list of player ids with yours in
brackets, how far your furthest neighbour is and a bar that turns orange when the rope is
pulling. **If two machines show a different chain, something is wrong** — that list is derived
from the same numbers on every machine and should always match.

## Known limitations

**A shared launch starts from where you stand, not from the pad.** You fly the same arc as your
neighbour, shifted by however far apart you were, so on a tight jump the shifted arc can clip a
wall the real one clears. Stay close to whoever goes first. The rescue teleport is there for the
launch that goes wrong anyway: it picks the group back up rather than ending the run.

**A help beam decides who leads by where everyone is seen.** Your own machine knows exactly when
you are in a beam, but a neighbour's is judged from their position against the beam, which lags
a little. Right at the edge of a beam the two machines can briefly disagree.


**Running Chained Is Hard alongside Chaos Is Hard** makes both mods' settings sync slowly and
unreliably — they share one channel. See the end of *How it works*.

## Settings

**The host sets the chain for everybody.** Everything that decides how it behaves — the length,
the stiffness, the rescue, the launch handling — is published by the host and followed by every
client, so nobody can quietly play by their own rules. On a client those rows show the host's
value and cannot be edited; your own values stay in your config file and come back the moment the
host stops publishing. What stays yours: the drawn rope, the corner readout and your key binds.

| Setting | Default | What it does |
|---|---|---|
| `Chained` | on | Whether players are chained at all. **Host.** |
| `ChainLength` | 5 m | Rope between two neighbours. **Host.** |
| `Slack` | 0.05 | How far past the length it stretches before pulling. Raise it if standing still at full stretch shakes: some slack keeps network jitter below the threshold. |
| `PullRate` | 6 /s | How fast the rope closes you back in per metre of stretch: 6 takes a metre out at 6 m/s. Higher is tighter. |
| `Firmness` | 0.6 | How much of that speed it gets each physics step. 1 feels like a steel cable, lower like a rope with give. It never overshoots either way. |
| `MaxPull` | 120 m/s² | The hardest the rope can change your speed. Stops a lag spike from launching you. |
| `ReelSpeed` | 4 m/s | How fast the rope is wound back in when it comes back after a launch, a respawn or a pause. See below. |
| `SpeedPull` | 0.5 | How much speed decides who wins the tug of war. At 0 a standing player anchors a moving one. At 1 the faster player never feels the rope at all. |
| `LaunchSuspend` | 3 s | Minimum time the chain goes slack for a launch. See below. |
| `LaunchGrace` | 1 s | How long after hitting something the chain stays off, to cover the bounce. |
| `ShareLaunches` | on | Take the same kick as a neighbour who crosses a boost pad. |
| `ShareCannons` | on | Take the same shot as a neighbour fired from a cannon you were not in. |
| `LaunchRange` | 12 m | Distance from the pad or cannon past which a shared launch does not apply. |
| `CountdownSeconds` | 3 s | How long the shared countdown runs. |
| `RescueKeepsSpeed` | on | Arrive at a rescue with their speed instead of standing still. |
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
solver against each other and each makes its part of the correction; the two parts always add
up to exactly one, so the link is as firm as set and not twice that. Inside the length nothing happens at all, so you forget the rope is there until it goes
tight.

**Past the length the rope aims at a closing speed, and works on velocity.** The further it is
stretched, the faster it wants the two of you closing in (`PullRate`, capped at 8 m/s), and it
adds the part of the difference that is yours. It never sets your position: an earlier version
put your body back inside the length every step, and it felt like a string of small teleports,
threw your momentum away and could put you through a floor. It only pulls: a rope cannot push,
so already closing in fast enough is left alone.

**The lag is accounted for, and the rope cannot run away.** Each machine sees the other player
where they were 100-200 ms ago. Two players swinging round each other each pulled towards that
old position, which sits a little ahead in the swing, so every pull pushed the swing along and
the pair sped up and spun until somebody hit a wall. Three things stop it now:

- The neighbour is taken where they most likely are *now*: carried forward along their velocity
  by half the round trip plus FishNet's interpolation buffer, never by more than 3 m.
- Their velocity is smoothed, so a burst of network updates is not read as them lunging away.
- The rope can turn you and slow you, but never take you past 10 m/s by itself. Anything faster
  has to come from the game — a jump, a pad, gravity — so even if the readings are off, a runaway
  cannot start in the rope.

It is also not a spring. A spring between two late-seeing players feeds itself; a target speed
cannot overshoot whatever the lag.

**When the rope comes back it is wound in, not snapped.** After a launch, a respawn or a pause,
each link starts as long as the gap between you and is reeled in at `ReelSpeed` until it is back
to the chain length. It never lets out again, so walking back to each other finishes the job
sooner. The corner readout says *reeling in* while it is happening.

**Cannons and boost pads do not use the rope at all.** They fire far harder than the rope
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

Nothing is sent to arrange that. The cannon's timer is a FishNet `SyncStopwatch`, so everyone
sees the same countdown run out on the same step. And a pad, when it fires, already tells the
whole lobby so the others can play its effect: a scene pad through
`VisualSyncBoostPad.ActivateOneShot(player)`, a networked one through an observer RPC carrying
the client id of whoever crossed it. The mod listens to those, so it knows *who* crossed *which*
pad, on every machine, whether or not it saw them touch it. An earlier version watched positions
instead and missed most crossings: what you see of a neighbour lags behind them, and the pad
throws them out of its volume within a step or two.

`ShareLaunches` covers the case where a neighbour crosses a pad you did not: you get the kick the
game would have given you — direction from the pad's own `FindShootDirection()`, force from its
`boostForce`, and the quarter second of air control the pad hands out — and fly the same arc from
where you are standing. `ShareCannons` does the same for a cannon a neighbour was fired from:
its direction times its `launchForce`, with air control blocked for as long as the cannon blocks
it. Detecting it late moves your starting point, not your trajectory, which is why the lag does
not break it. Neither applies past `LaunchRange`, nor within a moment of your own launch, which
the shared one would otherwise overwrite.

An earlier version watched for a neighbour moving fast and tried to drag you after them, and
another teleported you into a cannon a neighbour was waiting in. Both are gone. The first reacted
to the consequence instead of the cause and was always late; the second dragged players into
cannons they had not chosen to enter.

**Speed decides who gives way.** Since the only body you can move is your own, winning a tug of
war means being pulled less than the other end is. Both machines measure the same two speeds —
a neighbour's from how far they travelled since the last physics step, because their velocity is
not really ours to read — and the faster player stops yielding while the slower one takes up the
whole pull. Without that, a player standing still anchors one that has just been fired out
of a cannon, and `SpeedPull` is what turns that around.

**A help beam decides it instead.** Whoever stands in somebody else's beam is pushed upwards
every physics step, and a rope that reads that as wandering off hauls them back down — the beam
stalls at the chain length and shakes there. So the lifted player leads: they give nothing and
the other end of the link follows them up, the way a rope pulled from above takes everything below
it along. Your own machine knows from the game when you are in a beam; a neighbour is judged from
where they are against the beams everyone has up, which every machine knows because the active
beam is a SyncVar.

**The rescue decides locally which end moves.** Whoever just respawned is the anchor and does
not move; the other one comes to them, which is what turns one death into a shared trip back to
the checkpoint. When neither respawned, the higher owner id goes to the lower one, and the lower
one waits an extra 1.5 s before going itself in case the higher one was the anchor. No messages,
so nothing to lose.

**The settings and the countdown do travel**, on things the game already sends. There is no way
to add a channel: FishNet generates its serializers at compile time, so a broadcast type declared
in a mod has none, and riding on one of the game's own dies in Il2CppInterop.

The settings go over `PlayerNetworked.LookDirectionSyncVar` with a numeric tag, polled rather than
subscribed. A SyncVar is the server's to write, which suits them: only the host has any.

The countdown cannot go that way. The only request the game has for a client to write that field,
`RequestUpdateLookDirectionServerRpc`, does nothing unless a Troll Mode player is in the lobby,
which is why a client's countdown used to reach nobody. It goes through
`PlayerFeatureVisualRelay.ActivateOneShotServerRpc` instead — the call a pad uses to have its
effect played everywhere, which takes a number from anyone and hands it to every machine. Feature
ids are numbered up from 1, so a block at the top of the range is free; the game looks an unknown
one up, does not find it and does nothing. The number is the network tick the count was called
on, so each machine knows how much of it is already gone.

### Running it with Chaos Is Hard

Both mods write the settings to that same SyncVar. The tags are different, so they ignore each other's
messages instead of acting on them, but each still overwrites the other's: run both at once and
the settings sync of both becomes slow and unreliable. The chain itself keeps working - it does
not depend on the network - and so does everything Chaos Is Hard does locally.

## Building

`build.bat` targets the released game, `build-playtest.bat` the 1.8 playtest on `D:`. Both need
BepInEx 6 IL2CPP installed in the game and the game run once so the interop assemblies exist.
Two installs also make a two player test possible on one machine.

```
dotnet build -c Release -p:GameDir="path\to\game" -p:Deploy=true
```
