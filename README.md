# ArtworkPlacer

Revit 2024 add-in that automatically places artwork on walls and TVs in meeting rooms.

## Place Artwork

When you click **Place Artwork** (in the **Qbiq Tools** tab):

1. Takes the walls you selected (or lets you pick them)
2. Lets you choose an artwork collection: one per `Artwork_*` section in the template (HQ, Qbiq, Instant, Empty frames…)
3. Finds the free space on each wall, avoiding doors, windows, openings, joining walls and anything already hung there
4. Shows arrows on the face where each piece will go. Click a wall to switch it to the other face (or both)
5. Places the artwork centered on the free space at the chosen height, rotating through the collection's types

On long walls it can place several pieces, evenly spaced. Everything it places gets `ArtworkPlacer` in **Comments**, and the whole run is a single Undo.

## Place TV

When you click **Place TV** (in the **Qbiq Tools** tab):

1. Takes the walls you selected: all the walls of one or more meeting rooms
2. Finds the closed rooms those walls form. It doesn't use Revit rooms, so it works even when rooms are missing or badly drawn. Walls that don't close a room get one TV each
3. In each room, picks the **short wall farthest from the door**, so the TV faces down the length of the table. Walls with no room for a TV, glass/curtain walls and walls with a door come last
4. Picks the TV size from the room depth (bigger rooms get bigger TVs) and centers it on the room, avoiding doors, windows and corners
5. Shows an arrow and the TV type on each chosen wall. Click another wall of the room to move the TV there, or its own wall/arrow to remove it

Every TV it places gets `TvPlacer` in **Comments**. Rooms that already have a TV are skipped, so it's safe to run again. The whole run is a single Undo.

---

## Installation

### 1. Download the files

From the latest [release](../../releases/latest), download:

- `ArtworkPlacer.dll`
- `ArtworkPlacer.addin`
- `ArtworkPlacer.png` and `TvPlacer.png` (button icons)

### 2. Close Revit

Make sure Revit is completely closed before installing.

### 3. Copy the files

Copy the files to:

```
%APPDATA%\Autodesk\Revit\Addins\2024
```

(Paste that path in the Windows Explorer address bar and press Enter.)

### 4. Unblock the DLL

Windows may block DLLs downloaded from the internet:

1. Right-click `ArtworkPlacer.dll` → **Properties**
2. Check **Unblock** at the bottom
3. Click **OK**

> If there's no "Unblock" checkbox, the file is already unblocked.

### 5. Open Revit

You'll see the **Qbiq Tools** tab with the **Place Artwork** and **Place TV** buttons.

---

## How to use Place Artwork

1. Open a plan or 3D view and select the walls
2. Click **Place Artwork**
3. Pick a **Collection** (or check types by hand), the center height and the spacing options
4. Check the arrows. Click any wall (or its arrow) to cycle: other face → both faces → original face
5. Press **ESC** and choose **Place artwork**

Gray arrows mean there's no room on that face (or it already has artwork), so it will be skipped. A report at the end lists any skipped faces and why.

### Options

| Option | Default | What it does |
|---|---|---|
| Collection | All placeable types | Uses the artwork types shown in an `Artwork_*` section, in left-to-right order |
| Artwork center height | 150 cm | Height of the piece's center above the wall's level. Uncheck to use the family's Default Elevation |
| Min. clearance to doors/corners | 30 cm | Free space kept around doors, windows, openings and wall ends |
| Initial wall face | Auto | Auto picks the face toward a room (the larger room if both sides have one) |
| Multiple pieces on long walls | On | Fills long walls with evenly spaced pieces |
| Min. gap between pieces | 60 cm | Minimum space between neighboring pieces |
| Max. pieces per wall face | 3 | Upper limit per face |
| Preview faces with arrows | On | Shows the arrows so you can check and switch faces before placing |

---

## How to use Place TV

1. Open a plan view and select **all the walls** of the meeting rooms (a window selection over the rooms works; extra walls are ignored if they don't close a room)
2. Click **Place TV**
3. Check the TV types to use (types with "TV" in the family name are checked the first time), the size mode and the height
4. Check the arrows. Click another wall of a room to move its TV there; click the TV's wall or arrow to remove it (click again to restore)
5. Press **ESC** and choose **Place TVs**

To put a TV on one specific wall, select just that wall: it gets one TV on the face toward a Revit room (or the interior face). Click it to switch faces.

### Options

| Option | Default | What it does |
|---|---|---|
| TV size | Auto | **Auto:** the smallest checked TV big enough for the room depth (the largest that fits if none is). **Always the largest that fits:** ignores the room depth |
| Max. room depth = TV width × | 4.5 | A TV is used for rooms up to its width × this number deep. With 4.5, a 55" TV (1.24 m wide) covers rooms up to ~5.6 m, and a 100" (2.22 m) up to ~10 m |
| TV bottom edge height | 100 cm | Height of the bottom of the TV above the wall's level. Uncheck to use the family's own height |
| Min. clearance to doors/corners | 30 cm | Free space kept around doors, windows, openings and room corners |
| Preview with arrows | On | Shows the arrows so you can check and move TVs before placing |

---

## Requirements

- Revit 2024
- Qbiq template with the `Artwork_*` sections (to use collections; otherwise check types by hand)
- TV families loaded in the project (face-based, wall-hosted or unhosted all work)

---

## Troubleshooting

**The Qbiq Tools tab doesn't appear**
Make sure the files are in the Addins folder above and that Revit was restarted after copying them.

**"Could not load file or assembly" error on startup**
Windows blocked the DLL. Right-click `ArtworkPlacer.dll` → Properties → check **Unblock** → OK → restart Revit.

**The Collection list is empty**
The project has no section views with "Artwork" in the name, or those sections don't show any artwork. Use **All placeable types** and check the types by hand.

**No arrows appear**
The preview only works in plan and 3D views. In sections or elevations, the tool offers to place without a preview.

**Curved or curtain walls are skipped**
Only straight basic walls are supported. For Place TV, glass/curtain walls still count as room walls; the TV just never goes on them.

**Place TV says "0 room(s) found"**
The selected walls don't close a room. Select every wall around the meeting room, including glass walls. Small gaps (up to ~30 cm) are bridged automatically.

**The TV went on the wrong wall**
Click the wall you want in the preview. Doors are found by their position, so a door that isn't modeled (just a gap) isn't taken into account.

---

## Building from source

```
dotnet build ArtworkPlacer.csproj -c Release
```

Uses the Revit 2024 API from `C:\Program Files\Autodesk\Revit 2024` when installed, or the `Nice3point.Revit.Api` NuGet packages otherwise (CI). Pushing a `v*` tag builds a GitHub release with the DLL, `.addin` and icon.
