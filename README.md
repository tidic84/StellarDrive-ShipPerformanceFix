# Ship Performance Fix

A MelonLoader mod for **StellarDrive (Demo)**: no more freeze when you place or remove a hull panel on a big ship.

![Ship Performance Fix](Mods/Thumbnail.png)

Part of the Stellar suite: [StellarAnchor](https://github.com/tidic84/StellarDrive-StellarAnchor) · [StellarBuild](https://github.com/tidic84/StellarDrive-StellarBuild) · [StellarFactory](https://github.com/tidic84/StellarDrive-StellarFactory) · [StellarLogistics](https://github.com/tidic84/StellarDrive-StellarLogistics). Ship Performance Fix works on its own.

## Why the game freezes

After every hull change, the game recomputes the ship's rooms: the closed air volumes that hold oxygen. To do that, it clears the air region around the change and floods it again through an octree of voxels. The air outside the hull is one region that wraps the whole ship. So every panel placed or removed against the outside walks the entire outside of the ship. Inside, a panel walks the whole room. With about 2,000 parts that takes 25 to 100 ms per change. It runs on the host, then again on every client.

## What it does

Ship Performance Fix produces the same rooms and volumes as the game without the full walks:

- **Panel against the outside:** the outside is never cleared. A flood that reaches the edge of the octree is outside and stops there.
- **Panel inside a room:** a flood starts from each side of the panel and stops as soon as the floods meet. If the room really is cut in two, only the piece cut off is walked and becomes a new room with the same air.
- **Panel removed:** the freed air joins the single room (or the outside) it touches.
- **Anything else** falls back to the game's own recompute, limited to the regions involved: a room opened to the outside, two rooms merged, or adds and removes in the same tick.

Measured on a 20 × 10 × 20 two-deck hull (about 2,000 panels), room update per change on each machine:

| Change | Game | Ship Performance Fix |
| --- | --- | --- |
| Panel against the outside | ~37 ms | < 1 ms |
| Panel inside a room | 15–30 ms | ~1 ms |
| Panel removed | 15–30 ms | ~1 ms |
| Room opened / closed | ~60 ms | 12–40 ms |

The hull mesh (3 to 8 ms) and colliders (~1 ms) are rebuilt by the game as before. They are chunked, so they do not grow with the ship.

**Air density.** The game counts every voxel the octree splits during an update as vacuum, so a room's air thins a little with each panel you place inside it. With Ship Performance Fix, a room keeps its density, and a hull closed in an atmosphere takes the atmosphere's density.

## Install

With [Stellar Mod Manager](https://github.com/jollyname/StellarModManager), or extract `StellarDrive-ShipPerformanceFix.zip` into the game folder (MelonLoader 0.7.x).

Multiplayer: every machine computes its own rooms, so each player who installs it gets the gain on their own machine. Room ids never go over the network, and saves store the rooms by position, so players with and without the mod can play together.

## Building

```powershell
./build.ps1    # Release build + dist/StellarDrive-ShipPerformanceFix.zip
```

Override the game path with `-GamePath "D:\SteamLibrary\steamapps\common\StellarDrive Demo"`. Building with `-p:DefineConstants=DEV` adds dev commands read from `UserData/stellar_rooms_cmd.txt`, with timers on the room, mesh and collider updates. The commands are `bigship`, `addpart`, `rmpart`, `addbox`, `rooms`, `roomcheck`, `fuzz`, `vacuum`, `roomsfast on|off`, `prof on [ms]|off` and `spikes <ms>`; see `RoomsDev.cs`.
