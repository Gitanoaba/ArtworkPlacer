# ArtworkPlacer

Revit 2024 add-in that automatically places artwork on walls and TVs in meeting rooms, and lines up artwork heights.

## Room Studio

**Room Studio** (in the **Qbiq Tools** tab) opens a qbiq-styled window with a floor plan of the level, drawn from the model's rooms and walls.

- **Where things can go:**
  - **Never:** walls with windows (or an embedded curtain wall), and wall faces that look outside the building. "Outside" is detected from the rooms and floor slabs on the level.
  - **No artwork by default:** private offices, restrooms, storage, stairs, lifts and other service rooms. Tick **Allow artwork in private offices**, or choose **Artwork** for one room, to use them anyway.
  - **Core walls:** thick interior walls (18 cm or more) can hold artwork even when they aren't drywall.
  - **Blocked walls:** hover over one to see why it's blocked.
- **Suggestions on open:** it proposes artwork on the longest usable walls of corridors, lobbies, open spaces and similar rooms, plus long usable walls with no room behind them. It drops any suggestion where no piece fits. **Suggest artwork** adds more.
- **Walls:** every usable wall face is a dashed line. Click one to put artwork on it, including walls with no room. Click a blue wall to take it off. Hover for the room, length and wall type; red walls can't fit a piece, and hovering shows why.
- **Doors** are drawn on the plan (red, with their swing). Artwork and TVs keep clear of them.
- **Collections:** pick an `Artwork_*` section under **Artwork → Collection** to use its pieces for the whole plan. Select one room to give it its own collection, for example HQ pieces in reception and Qbiq pieces everywhere else.
- **Rooms:** click a room (Shift-click for more) and choose **Nothing**, **Artwork** or **TV**. Meeting rooms, conference rooms and boardrooms start with a TV. For one selected room, **Walls in this room** lists its walls: tick the ones for artwork, or pick the TV wall.
- **TVs:** click a TV to move it to the next wall that fits, or click a dashed wall in the room to move it there.
- **Drywall only** (on by default): pieces go only on wall faces whose finish on that side is gypsum, drywall, plasterboard, GWB, sheetrock or גבס (or whose wall type name says so). Core walls are the exception.
- The plan updates live. **Place in model** places exactly what's shown, as a single Undo, and selects the new pieces.

## Place Artwork

When you click **Place Artwork** (in the **Qbiq Tools** tab):

1. Takes the walls you selected (or lets you pick them)
2. Lets you choose an artwork collection: one per `Artwork_*` section in the template (HQ, Qbiq, Instant, Empty frames…). Each type shows a thumbnail of its actual photo
3. Finds the free space on each wall, avoiding doors, windows, openings, joining walls and anything already hung there
4. Shows arrows on the face where each piece will go. Click a wall to switch it to the other face (or both)
5. Places the artwork centered on the free space at the chosen height (by center, bottom edge or top edge), rotating through the collection's types

On long walls it can place several pieces, evenly spaced. Everything it places gets `ArtworkPlacer` in **Comments**, and the whole run is a single Undo.

## Align Heights

Fixes artwork that is already placed so every piece sits at the same height. It only moves pieces up or down, never along the wall.

- With pieces selected, it aligns those
- With nothing selected, it aligns all the artwork visible in the active view (the sample pieces in the `Artwork_*` sections are left alone)
- Align by **center**, **bottom edge** or **top edge**, measured from each wall's level

Heights are measured from the visible artwork only, ignoring hidden lines and helper geometry inside the family.

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

You need the install package (a folder with `Install.bat`, `ArtworkPlacer.dll`, `ArtworkPlacer.addin` and the button icons).

1. **Close Revit**
2. If you got the package as a `.zip`, extract it first (right-click → **Extract All**)
3. Double-click **`Install.bat`**
4. Open Revit: you'll see the **Qbiq Tools** tab with **Room Studio**, **Place Artwork**, **Align Heights** and **Place TV**

`Install.bat` copies the files to `%APPDATA%\Autodesk\Revit\Addins\2024` and unblocks them, so Windows doesn't block the DLL. To update, run the new package's `Install.bat` the same way. To remove the tools, run `Uninstall.bat`.

<details>
<summary>Manual install (without Install.bat)</summary>

1. Close Revit
2. Copy `ArtworkPlacer.dll`, `ArtworkPlacer.addin` and the `.png` icons to `%APPDATA%\Autodesk\Revit\Addins\2024` (paste that path in the Windows Explorer address bar)
3. Right-click `ArtworkPlacer.dll` → **Properties** → check **Unblock** → **OK**
4. Open Revit

</details>

---

## How to use Place Artwork

1. Open a plan or 3D view and select the walls
2. Click **Place Artwork**
3. Pick a **Collection** (or click the thumbnails to choose types by hand; hover to see a piece larger), the height and the spacing options
4. Check the arrows. Click any wall (or its arrow) to cycle: other face → both faces → original face
5. Press **ESC** and choose **Place artwork**

Gray arrows mean there's no room on that face (or it already has artwork), so it will be skipped. A report at the end lists any skipped faces and why.

### Options

| Option | Default | What it does |
|---|---|---|
| Collection | All placeable types | Uses the artwork types shown in an `Artwork_*` section, in left-to-right order |
| Height | Center at 150 cm | **Center at** / **Bottom edge at** (110 cm) / **Top edge at** (200 cm), measured from the wall's level. **Family default height** uses the family's own Default Elevation |
| Min. clearance to doors/corners | 30 cm | Free space kept around doors, windows, openings and wall ends |
| Initial wall face | Auto | Auto picks the face toward a room (the larger room if both sides have one) |
| Multiple pieces on long walls | On | Fills long walls with evenly spaced pieces |
| Min. gap between pieces | 60 cm | Minimum space between neighboring pieces |
| Max. pieces per wall face | 3 | Upper limit per face |
| Preview faces with arrows | On | Shows the arrows so you can check and switch faces before placing |

---

## How to use Align Heights

1. Select the artwork pieces to fix, or select nothing to fix all the artwork in the active view
2. Click **Align Heights**
3. Choose **center**, **bottom edge** or **top edge** and the height
4. Click **Align heights**. A report says how many pieces moved

It's a single Undo, and it remembers the height for the next **Place Artwork**.

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

**Thumbnails show a gray frame or "no preview"**
The photo comes from the texture of the type's photo material, looked up in the same folders Revit uses for textures (Options → Rendering → Additional render appearance paths). If that folder isn't set up on your computer, you see the family preview instead. Placing still works the same.

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

Uses the Revit 2024 API from `C:\Program Files\Autodesk\Revit 2024` when installed, or the `Nice3point.Revit.Api` NuGet packages otherwise (CI). Pushing a `v*` tag builds a GitHub release with the DLL, `.addin`, icons and the install scripts.
