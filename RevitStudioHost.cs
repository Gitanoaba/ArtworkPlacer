using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Point = System.Windows.Point;

namespace ArtworkPlacer;

internal class RevitStudioHost : IStudioHost
{
	private class FaceState
	{
		public int Key;

		public long LevelId;

		public long RoomId;

		public RoomFace Face;

		public bool Drywall;

		public bool Core;

		public string Block;

		public bool Unroomed;

		public bool TvWall;

		public bool On;

		public bool Suggested;
	}

	private class ArtPlan
	{
		public int Key;

		public FaceState Face;

		public WallPlacer.Piece Piece;

		public WallPlacer Placer;
	}

	private const double MinFreeFace = 2.0;

	private static readonly string[] OpenWords = { "corridor", "hall", "lobby", "reception", "lounge", "waiting", "open space", "מסדרון", "לובי", "קבלה" };

	private readonly UIDocument _uidoc;

	private readonly Document _doc;

	private readonly Units _units;

	private readonly TypeThumbnails _thumbs;

	private readonly SpatialElementBoundaryOptions _boundary = new SpatialElementBoundaryOptions
	{
		SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish
	};

	private readonly Dictionary<long, Room> _rooms;

	private readonly List<FaceState> _faces = new List<FaceState>();

	private readonly Dictionary<Tuple<long, int>, bool> _drywall = new Dictionary<Tuple<long, int>, bool>();

	private readonly Dictionary<long, RoomUse> _uses = new Dictionary<long, RoomUse>();

	private readonly Dictionary<long, TvTarget> _tvPlans = new Dictionary<long, TvTarget>();

	private readonly List<ArtPlan> _artPlans = new List<ArtPlan>();

	private readonly Dictionary<int, string> _faceProblems = new Dictionary<int, string>();

	private readonly Dictionary<long, string> _roomProblems = new Dictionary<long, string>();

	private StudioRequest _req;

	private bool _started;

	private int _nextKey;

	private TvPlacer _tvPlacer;

	private WallPlacer _wallPlacer;

	private List<FamilySymbol> _tvSymbols = new List<FamilySymbol>();

	private List<FamilySymbol> _artSymbols = new List<FamilySymbol>();

	private List<Outline> _doors;

	private readonly Footprint _footprint;

	private readonly Dictionary<long, WallInfo> _wallInfo = new Dictionary<long, WallInfo>();

	private readonly HashSet<long> _artRooms = new HashSet<long>();

	private readonly Dictionary<long, string> _roomCollection = new Dictionary<long, string>();

	private readonly Dictionary<string, WallPlacer> _placers = new Dictionary<string, WallPlacer>();

	private readonly Dictionary<string, List<FamilySymbol>> _collectionSymbols = new Dictionary<string, List<FamilySymbol>>();

	// Rooms "Not Enclosed" de Revit (área 0) que se reconstruyen con el recinto cerrado por los muros
	private readonly Dictionary<long, FoundRoom> _virtual = new Dictionary<long, FoundRoom>();

	private readonly List<Room> _broken;

	private readonly Dictionary<long, List<FoundRoom>> _enclosures = new Dictionary<long, List<FoundRoom>>();

	private readonly List<string> _notRebuilt = new List<string>();

	private static readonly double SmallSpace = UnitUtils.ConvertToInternalUnits(40.0, UnitTypeId.SquareMeters);

	private List<string> _boardMismatches = new List<string>();

	// Avisos al abrir: rooms que no se pudieron reconstruir y piezas cuyo nombre no calza con su lugar en el tablero
	public string StartNote
	{
		get
		{
			List<string> notes = new List<string>();
			if (_notRebuilt.Count > 0)
			{
				notes.Add($"{_notRebuilt.Count} room(s) aren't enclosed in Revit and couldn't be rebuilt from the walls: {string.Join(", ", _notRebuilt.Take(5))}{((_notRebuilt.Count > 5) ? "…" : "")}. Fix them in Revit for better results.");
			}
			if (_boardMismatches.Count > 0)
			{
				notes.Add($"{_boardMismatches.Count} artwork piece(s) sit in a different zone than their name says; the name wins: {string.Join("; ", _boardMismatches.Take(3))}{((_boardMismatches.Count > 3) ? "…" : "")}.");
			}
			return (notes.Count == 0) ? null : string.Join(" ", notes);
		}
	}

	public List<PlanLevel> Levels { get; }

	public long StartLevel { get; }

	public List<TypeItem> ArtTypes { get; private set; }

	public List<TypeItem> TvTypes { get; private set; }

	public List<ArtCollection> Collections { get; private set; } = new List<ArtCollection>();

	public List<ClientBoard> Boards { get; private set; } = new List<ClientBoard>();

	private readonly Dictionary<long, ArtZone> _roomZone = new Dictionary<long, ArtZone>();

	private const string ZoneKey = "zone:";

	public RevitStudioHost(UIDocument uidoc, string revitVersion)
	{
		_uidoc = uidoc;
		_doc = uidoc.Document;
		_units = _doc.GetUnits();
		_thumbs = new TypeThumbnails(_doc, revitVersion);
		_footprint = new Footprint(_doc);
		List<Room> placed = (from Room r in new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
			where r.Location != null
			select r).ToList();
		_rooms = placed.Where((Room r) => r.Area > 0.0).ToDictionary((Room r) => r.Id.Value);
		_broken = placed.Where((Room r) => r.Area <= 0.0).ToList();
		Levels = BuildLevels();
		long active = ((_doc.ActiveView as ViewPlan)?.GenLevel?.Id.Value).GetValueOrDefault(-1L);
		StartLevel = Levels.Any((PlanLevel l) => l.Id == active) ? active : (Levels.FirstOrDefault()?.Id ?? -1);
		CollectTypes(new HashSet<long>());
	}

	public string Fmt(double feet)
	{
		return UnitFormatUtils.Format(_units, SpecTypeId.Length, feet, forEditing: false);
	}

	public string Cm(double cm)
	{
		return Fmt(UnitUtils.ConvertToInternalUnits(cm, UnitTypeId.Centimeters));
	}

	public double? ParseLength(string text)
	{
		return UnitFormatUtils.TryParse(_units, SpecTypeId.Length, text ?? "", out var value) ? new double?(value) : null;
	}

	public Thumb Thumb(long typeId)
	{
		return _thumbs.Get(typeId);
	}

	private static bool Usable(Wall w)
	{
		return w.WallType.Kind == WallKind.Basic && w.Location is LocationCurve { Curve: Line };
	}

	private List<PlanLevel> BuildLevels()
	{
		List<Wall> walls = new FilteredElementCollector(_doc).OfClass(typeof(Wall)).Cast<Wall>().Where((Wall w) => w.Location is LocationCurve).ToList();
		List<Level> levels = new FilteredElementCollector(_doc).OfClass(typeof(Level)).Cast<Level>().OrderBy((Level l) => l.Elevation).ToList();
		List<FamilyInstance> doors = new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_Doors).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().ToList();
		List<PlanLevel> result = new List<PlanLevel>();
		foreach (Level level in levels)
		{
			List<Room> rooms = _rooms.Values.Where((Room r) => r.LevelId == level.Id).ToList();
			List<Wall> onLevel = walls.Where((Wall w) => w.LevelId == level.Id).ToList();
			if (rooms.Count == 0 && onLevel.Count == 0)
			{
				continue;
			}
			List<Room> adopted = AdoptBroken(level, rooms, onLevel);
			PlanLevel plan = new PlanLevel
			{
				Id = level.Id.Value,
				Name = level.Name,
				Rooms = rooms.Select(ToPlan).Where((PlanRoom r) => r.Loops.Count > 0).Concat(adopted.Select(ToVirtualPlan)).ToList(),
				Walls = onLevel.Select((Wall w) => new PlanWall
				{
					Path = ((LocationCurve)w.Location).Curve.Tessellate().Select((XYZ p) => new Point(p.X, p.Y)).ToArray(),
					Thickness = w.Width
				}).ToList(),
				Doors = (from d in doors
					where d.LevelId == level.Id || (d.Host as Wall)?.LevelId == level.Id
					select ToDoor(d) into d
					where d != null
					select d).ToList()
			};
			result.Add(plan);
			AddFaces(level, rooms, adopted, onLevel.Where(Usable).ToList());
		}
		return result;
	}

	// Un room "Not Enclosed" toma el recinto de muros que contiene su punto, si ningún room sano ya está ahí
	private List<Room> AdoptBroken(Level level, List<Room> rooms, List<Wall> onLevel)
	{
		List<FoundRoom> found = RoomFinder.Find(onLevel, out _).OrderBy((FoundRoom f) => f.Area).ToList();
		_enclosures[level.Id.Value] = found;
		List<Room> broken = _broken.Where((Room r) => r.LevelId == level.Id).ToList();
		if (broken.Count == 0)
		{
			return new List<Room>();
		}
		List<XYZ> healthy = rooms.Select((Room r) => (r.Location as LocationPoint)?.Point).Where((XYZ p) => p != null).ToList();
		List<Room> adopted = new List<Room>();
		HashSet<FoundRoom> taken = new HashSet<FoundRoom>();
		foreach (Room room in broken)
		{
			XYZ at = ((LocationPoint)room.Location).Point;
			FoundRoom enclosure = found.FirstOrDefault((FoundRoom f) => Contains(f.Polygon, at));
			if (enclosure == null || taken.Contains(enclosure) || healthy.Any((XYZ p) => Contains(enclosure.Polygon, p)))
			{
				if (enclosure == null)
				{
					_notRebuilt.Add((RoomName(room) + " " + (Clean(room.Number) ?? "")).Trim());
				}
				continue;
			}
			taken.Add(enclosure);
			_rooms[room.Id.Value] = room;
			_virtual[room.Id.Value] = enclosure;
			adopted.Add(room);
		}
		return adopted;
	}

	private static bool Contains(List<XYZ> poly, XYZ p)
	{
		bool inside = false;
		for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
		{
			if ((poly[i].Y > p.Y) != (poly[j].Y > p.Y) && p.X < (poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
			{
				inside = !inside;
			}
		}
		return inside;
	}

	private PlanRoom ToVirtualPlan(Room room)
	{
		FoundRoom found = _virtual[room.Id.Value];
		XYZ at = ((LocationPoint)room.Location).Point;
		return new PlanRoom
		{
			Id = room.Id.Value,
			Name = RoomName(room),
			Number = Clean(room.Number) ?? "",
			AreaText = UnitFormatUtils.Format(_units, SpecTypeId.Area, found.Area, forEditing: false) + ", not enclosed in Revit (taken from the walls)",
			Loops = new List<Point[]> { found.Polygon.Select((XYZ p) => new Point(p.X, p.Y)).ToArray() },
			Label = new Point(at.X, at.Y)
		};
	}

	// Caras de un room reconstruido: los muros del recinto, del lado que mira hacia adentro
	private static List<RoomFace> VirtualFaces(FoundRoom found)
	{
		return (from rw in found.Walls
			where Usable(rw.Wall) && rw.U1 - rw.U0 >= 0.1
			let line = (Line)((LocationCurve)rw.Wall.Location).Curve
			let n = rw.Wall.Orientation * rw.Sign
			let off = n * (rw.Wall.Width / 2.0)
			select new RoomFace
			{
				Wall = rw.Wall,
				Sign = rw.Sign,
				U0 = Math.Max(0.0, rw.U0),
				U1 = Math.Min(line.Length, rw.U1),
				A = line.GetEndPoint(0) + line.Direction * Math.Max(0.0, rw.U0) + off,
				B = line.GetEndPoint(0) + line.Direction * Math.Min(line.Length, rw.U1) + off,
				Normal = n
			}).ToList();
	}

	private double AreaOf(long roomId)
	{
		return _virtual.TryGetValue(roomId, out var found) ? found.Area : _rooms[roomId].Area;
	}

	private static PlanDoor ToDoor(FamilyInstance d)
	{
		BoundingBoxXYZ bb = d.get_BoundingBox(null);
		XYZ c = (d.Location as LocationPoint)?.Point ?? ((bb != null) ? ((bb.Min + bb.Max) / 2.0) : null);
		if (c == null)
		{
			return null;
		}
		XYZ along = d.HandOrientation;
		if (d.Host is Wall w && w.Location is LocationCurve { Curve: Line line })
		{
			along = line.Direction;
			XYZ p0 = line.GetEndPoint(0);
			c = p0 + along * (c - p0).DotProduct(along);
		}
		double width = d.Symbol.get_Parameter(BuiltInParameter.DOOR_WIDTH)?.AsDouble() ?? 0.0;
		if (width <= 0.0)
		{
			width = d.get_Parameter(BuiltInParameter.DOOR_WIDTH)?.AsDouble() ?? 0.0;
		}
		if (width <= 0.0 && bb != null)
		{
			width = Math.Abs((bb.Max - bb.Min).DotProduct(along));
		}
		return new PlanDoor
		{
			Center = new Point(c.X, c.Y),
			Along = new Vector(along.X, along.Y),
			Facing = new Vector(d.FacingOrientation.X, d.FacingOrientation.Y),
			Width = (width > 0.0) ? width : 3.0,
			Thickness = (d.Host as Wall)?.Width ?? 0.4
		};
	}

	private void AddFaces(Level level, List<Room> rooms, List<Room> adopted, List<Wall> walls)
	{
		List<FaceState> roomFaces = (from r in rooms
			from f in RoomScope.Faces(r, _boundary)
			where Usable(f.Wall)
			select NewFace(level, r.Id.Value, f)).Concat(from r in adopted
			from f in VirtualFaces(_virtual[r.Id.Value])
			select NewFace(level, r.Id.Value, f)).ToList();
		_faces.AddRange(roomFaces);
		foreach (Wall wall in walls)
		{
			Line line = (Line)((LocationCurve)wall.Location).Curve;
			foreach (int sign in new[] { 1, -1 })
			{
				List<Tuple<double, double>> covered = (from f in roomFaces
					where f.Face.Wall.Id == wall.Id && f.Face.Sign == sign
					orderby f.Face.U0
					select Tuple.Create(f.Face.U0, f.Face.U1)).ToList();
				foreach (Tuple<double, double> gap in Gaps(line.Length, covered).Where((Tuple<double, double> g) => g.Item2 - g.Item1 >= MinFreeFace))
				{
					XYZ n = wall.Orientation * sign;
					XYZ off = n * (wall.Width / 2.0);
					_faces.Add(NewFace(level, 0L, new RoomFace
					{
						Wall = wall,
						Sign = sign,
						U0 = gap.Item1,
						U1 = gap.Item2,
						A = line.GetEndPoint(0) + line.Direction * gap.Item1 + off,
						B = line.GetEndPoint(0) + line.Direction * gap.Item2 + off,
						Normal = n
					}));
				}
			}
		}
	}

	private static IEnumerable<Tuple<double, double>> Gaps(double length, List<Tuple<double, double>> covered)
	{
		double at = 0.0;
		foreach (Tuple<double, double> c in covered)
		{
			if (c.Item1 > at)
			{
				yield return Tuple.Create(at, c.Item1);
			}
			at = Math.Max(at, c.Item2);
		}
		if (length > at)
		{
			yield return Tuple.Create(at, length);
		}
	}

	private FaceState NewFace(Level level, long roomId, RoomFace face)
	{
		Tuple<long, int> key = Tuple.Create(face.Wall.WallType.Id.Value, face.Sign);
		if (!_drywall.TryGetValue(key, out var dry))
		{
			dry = (_drywall[key] = RoomScope.IsDrywall(face.Wall, face.Sign));
		}
		WallInfo info = Info(face.Wall);
		string block = info.Windows ? "Window wall: nothing goes on walls with windows" : null;
		if (block == null && roomId == 0 && Outside(level, face))
		{
			block = "Facade: this side faces outside the building";
		}
		return new FaceState
		{
			Key = ++_nextKey,
			LevelId = level.Id.Value,
			RoomId = roomId,
			Face = face,
			Drywall = dry,
			Core = info.Core,
			Block = block,
			Unroomed = roomId == 0 && block == null && InSmallSpace(level, face)
		};
	}

	// Cara sin room que da a un espacio chico cerrado por muros: probablemente una oficina o sala sin room en Revit
	private bool InSmallSpace(Level level, RoomFace face)
	{
		if (!_enclosures.TryGetValue(level.Id.Value, out var found))
		{
			return false;
		}
		XYZ mid = (face.A + face.B) / 2.0;
		XYZ probe = RoomFinder.Flat(mid + face.Normal * 1.0);
		FoundRoom space = found.FirstOrDefault((FoundRoom f) => Contains(f.Polygon, probe));
		return space != null && space.Area < SmallSpace;
	}

	private WallInfo Info(Wall wall)
	{
		if (!_wallInfo.TryGetValue(wall.Id.Value, out var info))
		{
			info = (_wallInfo[wall.Id.Value] = WallRules.Classify(wall));
		}
		return info;
	}

	// Una cara sin room está afuera si delante no hay room ni losa; sin losas, se usa la función del muro
	private bool Outside(Level level, RoomFace face)
	{
		XYZ mid = (face.A + face.B) / 2.0;
		XYZ probe = new XYZ(mid.X, mid.Y, level.ProjectElevation + 3.0) + face.Normal * 2.0;
		if (_doc.GetRoomAtPoint(probe) != null)
		{
			return false;
		}
		bool? inside = _footprint.Inside(probe, level);
		if (inside.HasValue)
		{
			return !inside.Value;
		}
		return Info(face.Wall).Exterior && face.Sign > 0;
	}

	private PlanRoom ToPlan(Room room)
	{
		PlanRoom plan = new PlanRoom
		{
			Id = room.Id.Value,
			Name = RoomName(room),
			Number = Clean(room.Number) ?? "",
			AreaText = UnitFormatUtils.Format(_units, SpecTypeId.Area, room.Area, forEditing: false)
		};
		IList<IList<BoundarySegment>> loops = room.GetBoundarySegments(_boundary);
		if (loops != null)
		{
			plan.Loops = loops.Select((IList<BoundarySegment> loop) => loop.SelectMany((BoundarySegment s) => s.GetCurve().Tessellate().Take(Math.Max(1, s.GetCurve().Tessellate().Count - 1)))
				.Select((XYZ p) => new Point(p.X, p.Y)).ToArray()).Where((Point[] l) => l.Length > 2).ToList();
		}
		XYZ at = (room.Location as LocationPoint)?.Point;
		plan.Label = (at != null) ? new Point(at.X, at.Y) : Centroid(plan.Loops.FirstOrDefault());
		return plan;
	}

	private static string RoomName(Room room)
	{
		return Clean(room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString()) ?? "Unnamed room";
	}

	private static Point Centroid(Point[] loop)
	{
		return (loop == null || loop.Length == 0) ? default(Point) : new Point(loop.Average((Point p) => p.X), loop.Average((Point p) => p.Y));
	}

	private static string Clean(string s)
	{
		return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
	}

	private void CollectTypes(HashSet<long> fresh)
	{
		Func<FamilySymbol, bool> none = (FamilySymbol s) => false;
		List<TypeItem> all = PlaceArtworkCommand.CollectTypes(_doc, none, new HashSet<long>());
		Collections = PlaceArtworkCommand.CollectCollections(_doc, all);
		Boards = ClientBoards.Read(_doc, new HashSet<long>(all.Select((TypeItem t) => t.Id)), out _boardMismatches);
		HashSet<long> inSections = new HashSet<long>(Collections.SelectMany((ArtCollection c) => c.Ids));
		List<TypeItem> tv = all.Where((TypeItem t) => PlaceTvCommand.LooksLikeTv(t.Label)).ToList();
		List<TypeItem> art = all.Where((TypeItem t) => (PlaceArtworkCommand.LooksLikeArtwork(t.Label) || inSections.Contains(t.Id)) && !tv.Contains(t)).ToList();
		art.ForEach((TypeItem t) => t.IsChecked = true);
		tv.ForEach((TypeItem t) => t.IsChecked = true);
		List<TypeItem> loaded = all.Where((TypeItem t) => fresh.Contains(t.Id) && !art.Contains(t) && !tv.Contains(t)).ToList();
		loaded.ForEach((TypeItem t) => t.IsChecked = true);
		ArtTypes = Keep(ArtTypes, art.Concat(loaded.Where((TypeItem t) => !PlaceTvCommand.LooksLikeTv(t.Label))).ToList(), fresh);
		TvTypes = Keep(TvTypes, tv.Concat(loaded.Where((TypeItem t) => PlaceTvCommand.LooksLikeTv(t.Label))).ToList(), fresh);
	}

	private static List<TypeItem> Keep(List<TypeItem> old, List<TypeItem> fresh, HashSet<long> loaded)
	{
		if (old != null)
		{
			Dictionary<long, bool> was = old.ToDictionary((TypeItem t) => t.Id, (TypeItem t) => t.IsChecked);
			foreach (TypeItem t in fresh.Where((TypeItem t) => was.ContainsKey(t.Id) && !loaded.Contains(t.Id)))
			{
				t.IsChecked = was[t.Id];
			}
		}
		return fresh.OrderBy((TypeItem t) => t.Label).ToList();
	}

	public void LoadFamilies(string[] paths)
	{
		CollectTypes(PlaceArtworkCommand.LoadFamilies(_doc, paths, "Load families"));
	}

	private List<FamilySymbol> Symbols(IEnumerable<long> ids)
	{
		return ids.Select((long id) => _doc.GetElement(new ElementId(id)) as FamilySymbol).Where((FamilySymbol s) => s != null).ToList();
	}

	private bool Allowed(FaceState f)
	{
		return Why(f) == null;
	}

	// Por qué no se puede usar una cara (null = se puede)
	private string Why(FaceState f)
	{
		if (f.Block != null)
		{
			return f.Block;
		}
		if (!IsTv(f.RoomId))
		{
			string room = RoomBlock(f.RoomId);
			if (room != null)
			{
				return room;
			}
		}
		if (_req != null && _req.DrywallOnly && !f.Drywall && !f.Core)
		{
			return "Not drywall";
		}
		return null;
	}

	// Oficinas privadas y salas de servicio no llevan arte, salvo que el usuario elija Artwork para ese room
	private string RoomBlock(long roomId)
	{
		if (roomId == 0 || _artRooms.Contains(roomId) || !_rooms.ContainsKey(roomId))
		{
			return null;
		}
		string name = RoomName(_rooms[roomId]);
		if (WallRules.IsService(name))
		{
			return "No artwork in restrooms, storage or service rooms";
		}
		if (Client != null && ZoneOfRoom(roomId) == ArtZone.None)
		{
			return "No " + Client.Name + " zone for this room. Pick one in the room panel, or choose Artwork for it";
		}
		if (Client == null && WallRules.IsPrivateOffice(name) && (_req == null || !_req.ArtInOffices))
		{
			return "Private office: no artwork. Choose Artwork for this room to allow it";
		}
		return null;
	}

	// Tablero del cliente elegido (null = artwork por colección o tipos marcados)
	private ClientBoard Client => (_req?.Client == null) ? null : Boards.FirstOrDefault((ClientBoard b) => b.Name == _req.Client);

	// Zona del room: la elegida en el panel, si no la de su nombre. Sin room (pasillos sin room): General
	private ArtZone ZoneOfRoom(long roomId)
	{
		if (roomId == 0)
		{
			return ArtZone.General;
		}
		if (_roomZone.TryGetValue(roomId, out var z))
		{
			return z;
		}
		ArtZone auto = ClientBoards.ZoneOf(RoomName(_rooms[roomId]));
		return (auto == ArtZone.None && _artRooms.Contains(roomId)) ? ArtZone.General : auto;
	}

	private string CollectionOf(long roomId)
	{
		if (roomId != 0 && _roomCollection.TryGetValue(roomId, out var c))
		{
			return c;
		}
		return (Client != null) ? (ZoneKey + ZoneOfRoom(roomId)) : "";
	}

	public PreviewResult SetRoomZone(long roomId, ArtZone? zone)
	{
		if (_req == null)
		{
			return new PreviewResult { Message = "Fix the settings on the right first." };
		}
		if (zone.HasValue)
		{
			_roomZone[roomId] = zone.Value;
		}
		else
		{
			_roomZone.Remove(roomId);
		}
		InTransaction(delegate
		{
			(from f in _faces
				where f.RoomId == roomId
				group f.Face.Wall by f.Face.Wall.Id into g
				select g.First()).ToList().ForEach(ReplanWall);
		});
		return State(null);
	}

	// Placer de una colección (""= los tipos marcados). Se crea dentro de la transacción de vista previa
	private WallPlacer PlacerFor(string collection)
	{
		if (collection.Length == 0)
		{
			return (_artSymbols.Count > 0) ? _wallPlacer : null;
		}
		if (_placers.TryGetValue(collection, out var placer))
		{
			return placer;
		}
		List<FamilySymbol> symbols;
		double gap = _req.Gap;
		int max = _req.MaxPerFace;
		if (collection.StartsWith(ZoneKey, StringComparison.Ordinal))
		{
			// Piezas de la zona en el tablero del cliente; la cantidad depende de la zona
			ArtZone zone = (ArtZone)Enum.Parse(typeof(ArtZone), collection.Substring(ZoneKey.Length));
			symbols = (Client != null && Client.Zones.TryGetValue(zone, out var ids)) ? Symbols(ids) : new List<FamilySymbol>();
			if (zone == ArtZone.General)
			{
				gap = Math.Max(gap, UnitUtils.ConvertToInternalUnits(3.5, UnitTypeId.Meters));
				max = Math.Max(max, 4);
			}
			else
			{
				max = 1;
			}
		}
		else
		{
			ArtCollection c = Collections.FirstOrDefault((ArtCollection x) => x.Name == collection);
			symbols = (c == null) ? new List<FamilySymbol>() : Symbols(c.Ids);
		}
		Activate(symbols);
		placer = (symbols.Count == 0) ? null : new WallPlacer(_doc, symbols, _req.ArtHeight, _req.ArtAlign, _req.Clearance, gap, max, SideMode.Auto, Fmt);
		_collectionSymbols[collection] = symbols;
		_placers[collection] = placer;
		return placer;
	}

	private IEnumerable<FamilySymbol> AllSymbols()
	{
		return _tvSymbols.Concat(_artSymbols).Concat(_collectionSymbols.Values.SelectMany((List<FamilySymbol> l) => l));
	}

	private bool IsTv(long roomId)
	{
		return _uses.TryGetValue(roomId, out var u) && u == RoomUse.Tv;
	}

	private void InTransaction(Action act)
	{
		using Transaction t = new Transaction(_doc, "Room Studio plan");
		PlaceArtworkCommand.SwallowWarnings(t);
		t.Start();
		try
		{
			Activate(AllSymbols());
			act();
		}
		finally
		{
			t.RollBack();
		}
	}

	private void Activate(IEnumerable<FamilySymbol> symbols)
	{
		foreach (FamilySymbol s in symbols.Where((FamilySymbol s) => !s.IsActive))
		{
			s.Activate();
		}
		_doc.Regenerate();
	}

	public PreviewResult Plan(StudioRequest req)
	{
		_req = req;
		if (!_started)
		{
			foreach (KeyValuePair<long, RoomUse> kv in req.Uses.Where((KeyValuePair<long, RoomUse> kv) => kv.Value == RoomUse.Tv && _rooms.ContainsKey(kv.Key)))
			{
				_uses[kv.Key] = RoomUse.Tv;
			}
			foreach (KeyValuePair<long, string> kv in req.RoomCollections.Where((KeyValuePair<long, string> kv) => _rooms.ContainsKey(kv.Key) && Collections.Any((ArtCollection c) => c.Name == kv.Value)))
			{
				_roomCollection[kv.Key] = kv.Value;
			}
			foreach (KeyValuePair<long, ArtZone> kv in req.RoomZones.Where((KeyValuePair<long, ArtZone> kv) => _rooms.ContainsKey(kv.Key)))
			{
				_roomZone[kv.Key] = kv.Value;
			}
			_started = true;
		}
		_placers.Clear();
		_collectionSymbols.Clear();
		foreach (FaceState f in _faces.Where((FaceState f) => f.On && !Allowed(f)))
		{
			f.On = false;
		}
		_tvSymbols = Symbols(req.TvTypes);
		_artSymbols = Symbols(req.ArtTypes);
		_tvPlacer = new TvPlacer(_doc, _tvSymbols, req.Clearance, req.TvBottom, req.TvAutoSize, 4.5, Fmt);
		_wallPlacer = new WallPlacer(_doc, _artSymbols, req.ArtHeight, req.ArtAlign, req.Clearance, req.Gap, req.MaxPerFace, SideMode.Auto, Fmt);
		_doors = PlaceTvCommand.DoorBoxes(_doc);
		_tvPlans.Clear();
		_artPlans.Clear();
		_faceProblems.Clear();
		_roomProblems.Clear();
		InTransaction(delegate
		{
			_uses.Keys.Where(IsTv).ToList().ForEach(PlanTv);
			WallsWithArt().ForEach(ReplanWall);
		});
		return State(null);
	}

	private List<Wall> WallsWithArt()
	{
		return (from f in _faces
			where f.On
			group f.Face.Wall by f.Face.Wall.Id into g
			select g.First()).ToList();
	}

	private FaceState FaceOf(long roomId, Wall wall, int sign)
	{
		return _faces.FirstOrDefault((FaceState f) => f.RoomId == roomId && f.Face.Wall.Id == wall.Id && f.Face.Sign == sign);
	}

	private bool SpotAllowed(long roomId, TvSpot spot)
	{
		FaceState f = FaceOf(roomId, spot.Wall, spot.Sign);
		return spot.Symbol != null && f != null && Allowed(f) && !f.On;
	}

	private void PlanTv(long roomId)
	{
		_tvPlans.Remove(roomId);
		_roomProblems.Remove(roomId);
		FoundRoom found = _virtual.TryGetValue(roomId, out var rebuilt) ? rebuilt : RoomScope.Found(_rooms[roomId], _boundary);
		if (found.Walls.Count == 0)
		{
			_roomProblems[roomId] = "No straight walls around this room";
			SyncTvFaces(roomId);
			return;
		}
		if (_tvSymbols.Count == 0)
		{
			_roomProblems[roomId] = "Tick at least one TV type";
			SyncTvFaces(roomId);
			return;
		}
		_tvPlacer.MeasureTypes(found.Walls.Select((RoomWall w) => w.Wall));
		TvTarget target = _tvPlacer.RoomTarget(found, 1, _doors);
		_tvPlans[roomId] = target;
		target.Current = target.Spots.FindIndex((TvSpot s) => SpotAllowed(roomId, s));
		if (target.Current < 0)
		{
			_roomProblems[roomId] = target.Spots.Any((TvSpot s) => s.Symbol != null) ? "No drywall wall here fits a TV" : Sentence(target.Note ?? target.Spots.FirstOrDefault()?.Problem ?? "No wall here fits a TV");
		}
		SyncTvFaces(roomId);
	}

	private void SyncTvFaces(long roomId)
	{
		TvSpot chosen = _tvPlans.TryGetValue(roomId, out var t) ? t.Chosen : null;
		// La TV tiene su muro; el artwork del room puede ir en los otros
		foreach (FaceState f in _faces.Where((FaceState f) => f.RoomId == roomId))
		{
			f.TvWall = chosen != null && f.Face.Wall.Id == chosen.Wall.Id && f.Face.Sign == chosen.Sign;
			if (f.TvWall)
			{
				f.On = false;
				f.Suggested = false;
			}
		}
	}

	private void ReplanWall(Wall wall)
	{
		_artPlans.RemoveAll((ArtPlan a) => a.Piece.Wall.Id == wall.Id);
		List<FaceState> all = _faces.Where((FaceState f) => f.Face.Wall.Id == wall.Id).ToList();
		all.ForEach((FaceState f) => _faceProblems.Remove(f.Key));
		List<FaceState> on = all.Where((FaceState f) => f.On).ToList();
		if (on.Count == 0)
		{
			return;
		}
		// Cada room puede usar su propia colección: las caras se planifican por colección
		foreach (IGrouping<string, FaceState> group in on.GroupBy((FaceState f) => CollectionOf(f.RoomId)))
		{
			List<FaceState> faces = group.ToList();
			WallPlacer placer = PlacerFor(group.Key);
			if (placer == null)
			{
				string missing = (group.Key.Length == 0) ? "Tick at least one artwork type" : (group.Key.StartsWith(ZoneKey, StringComparison.Ordinal) ? ((Client?.Name ?? "The client") + " has no " + group.Key.Substring(ZoneKey.Length) + " pieces") : (group.Key + " has no artwork types in this model"));
				faces.ForEach((FaceState f) => _faceProblems[f.Key] = missing);
				continue;
			}
			FaceRanges ranges = new FaceRanges();
			faces.ForEach((FaceState f) => ranges.Add(wall.Id, f.Face.Sign, f.Face.U0, f.Face.U1));
			placer.Ranges = ranges;
			List<string> notes = new List<string>();
			foreach (WallPlacer.Piece p in placer.Plan(wall, ranges.Signs(wall.Id), notes))
			{
				FaceState face = faces.FirstOrDefault((FaceState f) => f.Face.Sign == p.Sign && p.Center >= f.Face.U0 - 0.05 && p.Center <= f.Face.U1 + 0.05) ?? faces.First((FaceState f) => f.Face.Sign == p.Sign);
				_artPlans.Add(new ArtPlan { Key = ++_nextKey, Face = face, Piece = p, Placer = placer });
			}
			string why = Sentence(notes.FirstOrDefault((string n) => n.StartsWith("free")) ?? notes.FirstOrDefault() ?? "No free space here for the artwork");
			foreach (FaceState f in faces.Where((FaceState f) => !_artPlans.Any((ArtPlan a) => a.Face == f)))
			{
				_faceProblems[f.Key] = why;
			}
		}
	}

	public PreviewResult SetRoomCollection(long roomId, string collection)
	{
		if (_req == null)
		{
			return new PreviewResult { Message = "Fix the settings on the right first." };
		}
		if (string.IsNullOrEmpty(collection))
		{
			_roomCollection.Remove(roomId);
		}
		else
		{
			_roomCollection[roomId] = collection;
		}
		InTransaction(delegate
		{
			(from f in _faces
				where f.RoomId == roomId
				group f.Face.Wall by f.Face.Wall.Id into g
				select g.First()).ToList().ForEach(ReplanWall);
		});
		return State(null);
	}

	public PreviewResult SetRoomUse(IEnumerable<long> roomIds, RoomUse use)
	{
		if (_req == null)
		{
			return new PreviewResult { Message = "Fix the settings on the right first." };
		}
		List<long> ids = roomIds.Where(_rooms.ContainsKey).ToList();
		List<string> empty = new List<string>();
		InTransaction(delegate
		{
			List<Wall> touched = _faces.Where((FaceState f) => ids.Contains(f.RoomId)).Select((FaceState f) => f.Face.Wall).ToList();
			foreach (long id in ids)
			{
				List<FaceState> faces = _faces.Where((FaceState f) => f.RoomId == id).ToList();
				if (use == RoomUse.Artwork)
				{
					_artRooms.Add(id);
				}
				else
				{
					_artRooms.Remove(id);
				}
				if (use == RoomUse.Tv)
				{
					faces.ForEach(delegate(FaceState f)
					{
						f.On = false;
						f.Suggested = false;
					});
					_uses[id] = RoomUse.Tv;
					PlanTv(id);
					continue;
				}
				_uses.Remove(id);
				_tvPlans.Remove(id);
				_roomProblems.Remove(id);
				faces.ForEach(delegate(FaceState f)
				{
					f.TvWall = false;
					f.On = use == RoomUse.Artwork && Allowed(f);
					f.Suggested = false;
				});
				if (use == RoomUse.Artwork && !faces.Any((FaceState f) => f.On))
				{
					empty.Add(RoomName(_rooms[id]));
				}
			}
			touched.GroupBy((Wall w) => w.Id).Select((IGrouping<ElementId, Wall> g) => g.First()).ToList().ForEach(ReplanWall);
		});
		return State((empty.Count > 0) ? ("No usable walls in " + string.Join(", ", empty.Distinct()) + " (windows, facade or not drywall).") : null);
	}

	public PreviewResult ChangeFace(int faceKey)
	{
		FaceState f = _faces.FirstOrDefault((FaceState x) => x.Key == faceKey);
		if (f == null || _req == null)
		{
			return null;
		}
		if (!Allowed(f))
		{
			return State(Why(f) + ".");
		}
		if (f.TvWall)
		{
			return State("The TV is on this wall. Click the TV to move it to another wall.");
		}
		f.On = !f.On;
		f.Suggested = false;
		InTransaction(delegate
		{
			ReplanWall(f.Face.Wall);
		});
		return State(null);
	}

	public PreviewResult MoveTv(long roomId)
	{
		if (!_tvPlans.TryGetValue(roomId, out var target))
		{
			return null;
		}
		List<TvSpot> spots = target.Spots;
		for (int i = 1; i <= spots.Count; i++)
		{
			int k = (Math.Max(0, target.Current) + i) % spots.Count;
			if (SpotAllowed(roomId, spots[k]))
			{
				target.Current = k;
				break;
			}
		}
		SyncTvFaces(roomId);
		return State(null);
	}

	public PreviewResult Suggest()
	{
		if (_req == null)
		{
			return new PreviewResult { Message = "Fix the settings on the right first." };
		}
		if (_artSymbols.Count == 0 && Client == null)
		{
			return State("Tick at least one artwork type to get suggestions.");
		}
		List<FaceState> picks = (Client != null) ? ClientSuggestions() : SuggestedFaces();
		picks.ForEach(delegate(FaceState f)
		{
			f.On = true;
			f.Suggested = true;
		});
		InTransaction(delegate
		{
			List<Wall> walls = picks.Select((FaceState f) => f.Face.Wall).GroupBy((Wall w) => w.Id).Select((IGrouping<ElementId, Wall> g) => g.First()).ToList();
			walls.ForEach(ReplanWall);
			List<FaceState> failed = picks.Where((FaceState f) => _faceProblems.ContainsKey(f.Key)).ToList();
			failed.ForEach(delegate(FaceState f)
			{
				f.On = false;
				f.Suggested = false;
			});
			failed.Select((FaceState f) => f.Face.Wall).GroupBy((Wall w) => w.Id).Select((IGrouping<ElementId, Wall> g) => g.First()).ToList().ForEach(ReplanWall);
		});
		int count = picks.Count((FaceState f) => f.Suggested);
		return State((count == 0) ? "No more walls to suggest." : $"Suggested artwork on {count} wall(s). Click a blue wall to take it off.");
	}

	private List<FaceState> SuggestedFaces()
	{
		List<FaceState> picks = new List<FaceState>();
		foreach (IGrouping<long, FaceState> room in from f in _faces
			where f.RoomId != 0 && Allowed(f) && !IsTv(f.RoomId)
			group f by f.RoomId)
		{
			Room r = _rooms[room.Key];
			string name = RoomName(r);
			double m2 = UnitUtils.ConvertFromInternalUnits(AreaOf(room.Key), UnitTypeId.SquareMeters);
			bool open = Has(name, OpenWords);
			if (WallRules.IsService(name) || (m2 < 8.0 && !open) || room.Any((FaceState f) => f.On))
			{
				continue;
			}
			int take = open ? 3 : ((m2 >= 40.0) ? 2 : 1);
			double min = UnitUtils.ConvertToInternalUnits(open ? 3.0 : 1.5, UnitTypeId.Meters);
			picks.AddRange(room.Where((FaceState f) => f.Face.U1 - f.Face.U0 >= min).OrderByDescending((FaceState f) => f.Face.U1 - f.Face.U0).Take(take));
		}
		double freeMin = UnitUtils.ConvertToInternalUnits(3.0, UnitTypeId.Meters);
		picks.AddRange(_faces.Where((FaceState f) => f.RoomId == 0 && !f.On && !f.Unroomed && Allowed(f) && f.Face.U1 - f.Face.U0 >= freeMin));
		return picks;
	}

	// Sugerencias por zona del cliente: General (pasillos/open office) varias por muro largo, Kitchen y Lounge 1-2,
	// Meeting room 1 lejos de la TV, Reception 1 en el segundo mejor muro (el mejor queda para el logo)
	private List<FaceState> ClientSuggestions()
	{
		List<FaceState> picks = new List<FaceState>();
		double m15 = UnitUtils.ConvertToInternalUnits(1.5, UnitTypeId.Meters);
		double m3 = UnitUtils.ConvertToInternalUnits(3.0, UnitTypeId.Meters);
		foreach (IGrouping<long, FaceState> room in from f in _faces
			where f.RoomId != 0 && Allowed(f) && !f.TvWall
			group f by f.RoomId)
		{
			if (room.Any((FaceState f) => f.On) || WallRules.IsService(RoomName(_rooms[room.Key])))
			{
				continue;
			}
			List<FaceState> longest = room.OrderByDescending((FaceState f) => f.Face.U1 - f.Face.U0).ToList();
			switch (ZoneOfRoom(room.Key))
			{
			case ArtZone.General:
				picks.AddRange(longest.Where((FaceState f) => f.Face.U1 - f.Face.U0 >= m3).Take(3));
				break;
			case ArtZone.Kitchen:
				picks.AddRange(longest.Where((FaceState f) => f.Face.U1 - f.Face.U0 >= m15).Take(2));
				break;
			case ArtZone.Meeting:
				picks.AddRange(longest.Where((FaceState f) => f.Face.U1 - f.Face.U0 >= m15).Take(IsTv(room.Key) ? 1 : 2));
				break;
			case ArtZone.Reception:
				picks.AddRange(longest.Skip((longest.Count > 1) ? 1 : 0).Where((FaceState f) => f.Face.U1 - f.Face.U0 >= m15).Take(1));
				break;
			}
		}
		picks.AddRange(_faces.Where((FaceState f) => f.RoomId == 0 && !f.On && !f.Unroomed && Allowed(f) && f.Face.U1 - f.Face.U0 >= m3));
		return picks;
	}

	private static bool Has(string text, string[] words)
	{
		return words.Any((string w) => text.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);
	}

	private PreviewResult State(string message)
	{
		PreviewResult result = new PreviewResult { Message = message };
		result.Markers.AddRange(from kv in _tvPlans
			where kv.Value.Chosen != null
			select TvMarker(kv.Key, kv.Value));
		result.Markers.AddRange(_artPlans.Select(ArtMarker));
		result.Faces.AddRange(_faces.Select(ToPlanFace));
		foreach (KeyValuePair<long, string> kv in _roomProblems)
		{
			result.Problems[kv.Key] = kv.Value;
		}
		foreach (long id in _uses.Keys.Where(IsTv))
		{
			result.Uses[id] = RoomUse.Tv;
		}
		foreach (long id in _artPlans.Select((ArtPlan a) => a.Face.RoomId).Where((long id) => id != 0 && !IsTv(id)))
		{
			result.Uses[id] = RoomUse.Artwork;
		}
		return result;
	}

	private PlanFace ToPlanFace(FaceState f)
	{
		XYZ toWall = -f.Face.Normal;
		string side = (Math.Abs(toWall.X) > Math.Abs(toWall.Y)) ? ((toWall.X > 0.0) ? "East" : "West") : ((toWall.Y > 0.0) ? "North" : "South");
		string length = Fmt(f.Face.U1 - f.Face.U0);
		return new PlanFace
		{
			Key = f.Key,
			LevelId = f.LevelId,
			RoomId = f.RoomId,
			A = new Point(f.Face.A.X, f.Face.A.Y),
			B = new Point(f.Face.B.X, f.Face.B.Y),
			Normal = new Vector(f.Face.Normal.X, f.Face.Normal.Y),
			Title = (f.RoomId != 0) ? $"{side} wall of {RoomName(_rooms[f.RoomId])}, {length}" : (f.Unroomed ? $"Wall of a closed space with no room in Revit, {length}" : $"Wall with no room, {length}"),
			WallType = f.Face.Wall.WallType.Name,
			Allowed = Allowed(f),
			Reason = Why(f),
			Core = f.Core,
			TvWall = f.TvWall,
			On = f.On || f.TvWall,
			Suggested = f.Suggested,
			Problem = _faceProblems.TryGetValue(f.Key, out var p) ? p : null
		};
	}

	private static string Sentence(string s)
	{
		return (s.Length == 0) ? s : (char.ToUpper(s[0]) + s.Substring(1));
	}

	private static Vector V(XYZ v)
	{
		return new Vector(v.X, v.Y);
	}

	private static string TypeName(FamilySymbol s)
	{
		return s.FamilyName + " : " + s.Name;
	}

	private PlanMarker TvMarker(long roomId, TvTarget target)
	{
		TvSpot spot = target.Chosen;
		return new PlanMarker
		{
			Key = -(int)roomId,
			LevelId = _rooms[roomId].LevelId.Value,
			RoomId = roomId,
			Use = RoomUse.Tv,
			Center = new Point(spot.OnFace.X, spot.OnFace.Y),
			Along = V(spot.Along),
			Normal = V(spot.Normal),
			Width = spot.Width,
			Label = TypeName(spot.Symbol),
			CanMove = target.Spots.Count((TvSpot s) => SpotAllowed(roomId, s)) > 1
		};
	}

	private static PlanMarker ArtMarker(ArtPlan a)
	{
		return new PlanMarker
		{
			Key = a.Key,
			LevelId = a.Face.LevelId,
			RoomId = a.Face.RoomId,
			Use = RoomUse.Artwork,
			Center = new Point(a.Piece.OnFace.X, a.Piece.OnFace.Y),
			Along = V(a.Piece.Along),
			Normal = V(a.Piece.Normal),
			Width = a.Piece.Width,
			Label = TypeName(a.Piece.Symbol)
		};
	}

	public void Place(HashSet<int> skipped)
	{
		List<ElementId> placed = new List<ElementId>();
		List<string> notes = new List<string>();
		int tvs = 0;
		int arts = 0;
		TransactionStatus status;
		using (Transaction t = new Transaction(_doc, "Room Studio: place TVs and artwork"))
		{
			PlaceArtworkCommand.SwallowWarnings(t);
			t.Start();
			Activate(AllSymbols());
			foreach (KeyValuePair<long, TvTarget> p in _tvPlans.Where((KeyValuePair<long, TvTarget> p) => p.Value.Chosen != null))
			{
				Try(RoomName(_rooms[p.Key]), () => _tvPlacer.Place(p.Value.Chosen, out var reason) ?? Fail(reason), ref tvs);
			}
			foreach (ArtPlan a in _artPlans.Where((ArtPlan a) => !skipped.Contains(a.Key)))
			{
				string label = (a.Face.RoomId != 0) ? RoomName(_rooms[a.Face.RoomId]) : PlaceArtworkCommand.WallLabel(a.Piece.Wall);
				Try(label, () => a.Placer.PlacePiece(a.Piece, out var reason) ?? Fail(reason), ref arts);
			}
			status = t.Commit();
		}
		// Si Revit rechazó la transacción, no quedó nada en el modelo
		if (status != TransactionStatus.Committed)
		{
			TaskDialog.Show("Room Studio", "Revit didn't accept the changes, so nothing was placed. Try again with fewer rooms, or check the model for errors.");
			return;
		}
		if (placed.Count > 0)
		{
			_uidoc.Selection.SetElementIds(placed);
		}
		TaskDialog dialog = new TaskDialog("Room Studio")
		{
			MainInstruction = $"Placed {tvs} TV(s) and {arts} artwork piece(s).",
			MainContent = (notes.Count == 0) ? "Everything from the plan is in the model and selected, so you can review it." : $"{notes.Count} item(s) couldn't be placed (see details). The rest are selected.",
			ExpandedContent = string.Join("\n", notes)
		};
		dialog.Show();

		void Try(string label, Func<FamilyInstance> act, ref int count)
		{
			using SubTransaction sub = new SubTransaction(_doc);
			sub.Start();
			try
			{
				FamilyInstance fi = act();
				placed.Add(fi.Id);
				count++;
				sub.Commit();
			}
			catch (Exception ex)
			{
				sub.RollBack();
				notes.Add(label + ": " + ex.Message);
			}
		}
	}

	private static FamilyInstance Fail(string reason)
	{
		throw new InvalidOperationException(reason ?? "could not be placed");
	}
}
