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

		// Cara del core que da al pasillo (según el JSON)
		public bool CoreFront;

		// Da a la circulación del JSON (pasillo)
		public bool Corridor;

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

		public int Index;

		public bool Manual;
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

	// Rooms del layout de Qbiq (JSON del diseño): ids negativos, mandan sobre los de Revit en su nivel
	private class LayoutRoomInfo
	{
		public string Name;

		public ElementId LevelId;

		public List<XYZ> Polygon;
	}

	private readonly Dictionary<long, LayoutRoomInfo> _layoutRooms = new Dictionary<long, LayoutRoomInfo>();

	private DesignLayout _layout;

	private LayoutFit _fit;

	private long _layoutLevel = -1;

	public string LayoutNote { get; private set; }

	// Carpeta que crea el add-in de Qbiq al cargar el diseño: Documentos\QBIQ_DEV_DATA\<Design ID>
	public string DesignFolder
	{
		get
		{
			string root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "QBIQ_DEV_DATA");
			string id = LogoPlacer.DesignId(_uidoc.Application);
			string byId = string.IsNullOrEmpty(id) ? null : System.IO.Path.Combine(root, id);
			return (byId != null && System.IO.Directory.Exists(byId)) ? byId : (System.IO.Directory.Exists(root) ? root : null);
		}
	}

	// Tramos de una cara tapados por algo (mueble alto, columna, giro de puerta, ventana): ahí no va arte
	private class FaceBlock
	{
		public double U0;

		public double U1;

		public string Reason;

		public FurnKind Kind;
	}

	private readonly Dictionary<int, List<FaceBlock>> _blocks = new Dictionary<int, List<FaceBlock>>();

	// Caras con un sofá o banco delante: el arte va arriba (preferidas al sugerir)
	private readonly HashSet<int> _sofaFaces = new HashSet<int>();

	// Muebles de cada nivel (de Revit; del JSON si el modelo no tiene)
	private readonly Dictionary<long, List<Furnishing>> _furniture = new Dictionary<long, List<Furnishing>>();

	// Del JSON, ya en el modelo: muebles, giros de puertas, ventanas, columnas, pasillos y personas por room
	private readonly List<Furnishing> _jsonFurniture = new List<Furnishing>();

	private readonly List<Point[]> _jsonSwings = new List<Point[]>();

	private readonly List<Tuple<Point, Point>> _jsonWindows = new List<Tuple<Point, Point>>();

	private readonly List<Point[]> _jsonColumns = new List<Point[]>();

	private readonly List<List<XYZ>> _circulation = new List<List<XYZ>>();

	private readonly Dictionary<long, int> _people = new Dictionary<long, int>();

	// Core y entrada del JSON ya en coordenadas del modelo
	private readonly List<List<XYZ>> _cores = new List<List<XYZ>>();

	private XYZ _entrance;

	private XYZ _entranceOut;

	private FaceState _logoFace;

	public bool HasLogoWall => _logoFace != null;

	// Muros con logo: el que se ve desde la entrada (si el usuario lo quiere) y los que marque a mano
	private readonly HashSet<int> _logoOn = new HashSet<int>();

	private bool _logoAutoOff;

	private bool IsLogo(FaceState f)
	{
		return _logoOn.Contains(f.Key) || (f == _logoFace && _req != null && _req.PlaceLogo && !_logoAutoOff);
	}

	// Accent walls: el de la TV en las salas con TV (si está activado) y los que se marquen a mano
	private readonly HashSet<int> _accentOn = new HashSet<int>();

	private readonly HashSet<int> _accentOff = new HashSet<int>();

	private bool IsAccent(FaceState f)
	{
		if (_accentOn.Contains(f.Key))
		{
			return true;
		}
		return !_accentOff.Contains(f.Key) && _req != null && _req.AccentOnTv && f.TvWall && IsTv(f.RoomId);
	}

	public string LogoImage => LogoPlacer.FindLogo(_uidoc.Application, _doc);

	public List<NamedId> Materials { get; private set; } = new List<NamedId>();

	public List<NamedId> AccentWallTypes { get; private set; } = new List<NamedId>();

	public long AccentMaterialDefault { get; private set; } = -1L;

	public long AccentWallTypeDefault { get; private set; } = -1L;

	public string AccentNote { get; private set; }

	// El muro del logo (el que se ve desde la entrada principal), para Place Logo
	public LogoTarget LogoTarget => (_logoFace == null) ? null : new LogoTarget
	{
		WallId = _logoFace.Face.Wall.Id.Value,
		Sign = _logoFace.Face.Sign,
		U0 = _logoFace.Face.U0,
		U1 = _logoFace.Face.U1,
		Room = NameOf(_logoFace.RoomId)
	};

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

	// Posiciones elegidas arrastrando: (cara, n.º de pieza en esa cara) → centro a lo largo del muro
	private readonly Dictionary<Tuple<int, int>, double> _pins = new Dictionary<Tuple<int, int>, double>();

	// Piezas agregadas a mano con "+", por cara; sobreviven a los cambios de opciones
	private readonly Dictionary<int, List<WallPlacer.Piece>> _manual = new Dictionary<int, List<WallPlacer.Piece>>();

	private readonly Dictionary<string, double> _placerGap = new Dictionary<string, double>();

	private const string ZoneKey = "zone:";

	public RevitStudioHost(UIDocument uidoc, string revitVersion, DesignLayout layout = null)
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
		if (layout != null)
		{
			FitLayout(layout);
		}
		Levels = BuildLevels();
		try
		{
			string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "QbiqTools");
			System.IO.Directory.CreateDirectory(dir);
			System.IO.File.WriteAllLines(System.IO.Path.Combine(dir, "furniture.txt"), _furnitureLog);
		}
		catch
		{
		}
		long active = ((_doc.ActiveView as ViewPlan)?.GenLevel?.Id.Value).GetValueOrDefault(-1L);
		StartLevel = Levels.Any((PlanLevel l) => l.Id == active) ? active : (Levels.FirstOrDefault()?.Id ?? -1);
		CollectTypes(new HashSet<long>());
		CollectAccent(layout);
	}

	// Materiales y tipos de muro para el accent wall; el material que pide el diseño sale de su *_materials.json
	private void CollectAccent(DesignLayout layout)
	{
		Materials = (from Material m in new FilteredElementCollector(_doc).OfClass(typeof(Material))
			orderby m.Name
			select new NamedId { Id = m.Id.Value, Name = m.Name }).ToList();
		AccentWallTypes = (from WallType t in new FilteredElementCollector(_doc).OfClass(typeof(WallType))
			where t.Kind == WallKind.Basic
			orderby t.Name.IndexOf("accent", StringComparison.OrdinalIgnoreCase) < 0, t.Name
			select new NamedId { Id = t.Id.Value, Name = t.Name }).ToList();
		string wanted = DesignAccentMaterial(layout, out var source);
		NamedId match = MatchName(Materials, wanted);
		AccentMaterialDefault = match?.Id ?? -1L;
		NamedId accentType = AccentWallTypes.FirstOrDefault((NamedId t) => t.Name.IndexOf("accent", StringComparison.OrdinalIgnoreCase) >= 0 && wanted != null && Norm(t.Name).Contains(Norm(wanted))) ?? AccentWallTypes.FirstOrDefault((NamedId t) => t.Name.IndexOf("accent", StringComparison.OrdinalIgnoreCase) >= 0);
		AccentWallTypeDefault = accentType?.Id ?? -1L;
		if (wanted == null)
		{
			AccentNote = "Pick the accent material (no *_materials.json found for this design).";
		}
		else if (match == null)
		{
			AccentNote = "The design asks for \"" + wanted + "\", which isn't a material in this model. Pick one, or a wall type that already has it.";
		}
		else
		{
			AccentNote = "The design asks for \"" + wanted + "\" (from " + source + ").";
		}
	}

	// conference_accent_wall del materials.json: junto al JSON cargado o en Documentos\QBIQ_DEV_DATA\<id>
	private string DesignAccentMaterial(DesignLayout layout, out string source)
	{
		source = null;
		List<string> folders = new List<string>();
		string root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "QBIQ_DEV_DATA");
		if (layout != null)
		{
			folders.Add(System.IO.Path.GetDirectoryName(layout.File));
			string digits = new string(System.IO.Path.GetFileNameWithoutExtension(layout.File).TakeWhile(char.IsDigit).ToArray());
			if (digits.Length > 0)
			{
				folders.Add(System.IO.Path.Combine(root, digits));
			}
		}
		string id = LogoPlacer.DesignId(_uidoc.Application);
		if (!string.IsNullOrEmpty(id))
		{
			folders.Add(System.IO.Path.Combine(root, id));
		}
		foreach (string folder in folders.Where(System.IO.Directory.Exists))
		{
			try
			{
				foreach (string file in System.IO.Directory.GetFiles(folder, "*_materials.json"))
				{
					var js = new System.Web.Script.Serialization.JavaScriptSerializer();
					if (js.DeserializeObject(System.IO.File.ReadAllText(file)) is Dictionary<string, object> d && d.TryGetValue("materials", out var m) && m is Dictionary<string, object> mats && mats.TryGetValue("conference_accent_wall", out var v) && v is string name && !string.IsNullOrWhiteSpace(name))
					{
						source = System.IO.Path.GetFileName(file);
						return name.Trim();
					}
				}
			}
			catch
			{
			}
		}
		return null;
	}

	private static string Norm(string s)
	{
		return System.Text.RegularExpressions.Regex.Replace((s ?? "").ToLowerInvariant(), "[^a-z0-9]+", " ").Trim();
	}

	private static NamedId MatchName(List<NamedId> items, string wanted)
	{
		if (string.IsNullOrWhiteSpace(wanted))
		{
			return null;
		}
		string w = Norm(wanted);
		return items.FirstOrDefault((NamedId i) => Norm(i.Name) == w) ?? items.Where((NamedId i) => Norm(i.Name).Contains(w)).OrderBy((NamedId i) => i.Name.Length).FirstOrDefault() ?? items.FirstOrDefault((NamedId i) => Norm(i.Name).Length > 3 && w.Contains(Norm(i.Name)));
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
		List<CurveElement> separators = new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_RoomSeparationLines).WhereElementIsNotElementType().OfType<CurveElement>().ToList();
		List<PlanLevel> result = new List<PlanLevel>();
		foreach (Level level in levels)
		{
			List<Wall> onLevel = walls.Where((Wall w) => w.LevelId == level.Id).ToList();
			List<long> layoutIds = (level.Id.Value == _layoutLevel) ? BuildLayoutRooms(level, onLevel) : new List<long>();
			// En el nivel del layout, un room de Revit que cae dentro de un room del JSON queda reemplazado
			List<Room> rooms = _rooms.Values.Where((Room r) => r.LevelId == level.Id && !InLayoutRoom(r)).ToList();
			if (rooms.Count == 0 && onLevel.Count == 0)
			{
				continue;
			}
			List<Line> seps = SeparatorsOn(separators, level);
			List<Room> adopted = AdoptBroken(level, rooms, onLevel, seps);
			PlanLevel plan = new PlanLevel
			{
				Id = level.Id.Value,
				Name = level.Name,
				Rooms = rooms.Select(ToPlan).Where((PlanRoom r) => r.Loops.Count > 0).Concat(adopted.Select(ToVirtualPlan)).Concat(layoutIds.Select(ToLayoutPlan)).ToList(),
				Walls = onLevel.Select((Wall w) => new PlanWall
				{
					Path = ((LocationCurve)w.Location).Curve.Tessellate().Select((XYZ p) => new Point(p.X, p.Y)).ToArray(),
					Thickness = w.Width
				}).Concat(seps.Select((Line l) => new PlanWall
				{
					Path = new[] { new Point(l.GetEndPoint(0).X, l.GetEndPoint(0).Y), new Point(l.GetEndPoint(1).X, l.GetEndPoint(1).Y) },
					Separator = true
				})).ToList(),
				Doors = (from d in doors
					where d.LevelId == level.Id || (d.Host as Wall)?.LevelId == level.Id
					select ToDoor(d) into d
					where d != null
					select d).ToList()
			};
			if (level.Id.Value == _layoutLevel)
			{
				plan.Cores = _cores.Select((List<XYZ> c) => c.Select((XYZ p) => new Point(p.X, p.Y)).ToArray()).ToList();
				if (_entrance != null)
				{
					plan.Entrance = new Point(_entrance.X, _entrance.Y);
					plan.EntranceOut = new Vector(_entranceOut.X, _entranceOut.Y);
				}
			}
			result.Add(plan);
			AddFaces(level, rooms, adopted, layoutIds, onLevel.Where(Usable).ToList());
			List<Furnishing> furniture = FurnitureOn(level);
			_furniture[level.Id.Value] = furniture;
			// Los muebles no se dibujan (llenan mucho el plano), pero sí cuentan para las reglas
			_furnitureLog.Add($"{level.Name}: {furniture.Count} ({string.Join(", ", furniture.GroupBy((Furnishing f) => f.Kind).Select((IGrouping<FurnKind, Furnishing> g) => g.Key + " " + g.Count()))})");
			BlockFaces(level, furniture, plan.Doors);
			AddPeople(plan, furniture);
			if (level.Id.Value == _layoutLevel)
			{
				_logoFace = PickLogoFace();
			}
		}
		return result;
	}

	// Un room "Not Enclosed" toma el recinto de muros que contiene su punto, si ningún room sano ya está ahí
	// Líneas de separación de rooms del nivel (las del perímetro suelen cerrar los rooms contra el muro cortina)
	private static List<Line> SeparatorsOn(List<CurveElement> all, Level level)
	{
		return (from c in all
			where c.LevelId == level.Id || (c.SketchPlane != null && Math.Abs(c.SketchPlane.GetPlane().Origin.Z - level.ProjectElevation) < 0.5)
			select c.GeometryCurve as Line into l
			where l != null
			select l).ToList();
	}

	private List<Room> AdoptBroken(Level level, List<Room> rooms, List<Wall> onLevel, List<Line> separators)
	{
		List<FoundRoom> found = RoomFinder.Find(onLevel, separators, out _).OrderBy((FoundRoom f) => f.Area).ToList();
		_enclosures[level.Id.Value] = found;
		List<Room> broken = _broken.Where((Room r) => r.LevelId == level.Id).ToList();
		if (broken.Count == 0)
		{
			return new List<Room>();
		}
		List<XYZ> healthy = rooms.Select((Room r) => (r.Location as LocationPoint)?.Point).Where((XYZ p) => p != null).ToList();
		List<Room> adopted = new List<Room>();
		HashSet<FoundRoom> taken = new HashSet<FoundRoom>();
		foreach (Room room in broken.Where((Room r) => !InLayoutRoom(r)))
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

	private bool HasRoom(long roomId)
	{
		return _rooms.ContainsKey(roomId) || _layoutRooms.ContainsKey(roomId);
	}

	private string NameOf(long roomId)
	{
		return _layoutRooms.TryGetValue(roomId, out var l) ? l.Name : RoomName(_rooms[roomId]);
	}

	private ElementId LevelOf(long roomId)
	{
		return _layoutRooms.TryGetValue(roomId, out var l) ? l.LevelId : _rooms[roomId].LevelId;
	}

	// Alinea el JSON con los muros de cada nivel y se queda con el que mejor calza
	private void FitLayout(DesignLayout layout)
	{
		List<Wall> walls = new FilteredElementCollector(_doc).OfClass(typeof(Wall)).Cast<Wall>().Where((Wall w) => w.Location is LocationCurve { Curve: Line }).ToList();
		LayoutFit best = null;
		long bestLevel = -1;
		foreach (IGrouping<long, Wall> g in walls.GroupBy((Wall w) => w.LevelId.Value))
		{
			List<Tuple<Point, Point>> segs = g.Select((Wall w) => (Line)((LocationCurve)w.Location).Curve).Select((Line l) => Tuple.Create(new Point(l.GetEndPoint(0).X, l.GetEndPoint(0).Y), new Point(l.GetEndPoint(1).X, l.GetEndPoint(1).Y))).ToList();
			LayoutFit fit = LayoutFit.Find(layout, segs);
			if (fit != null && (best == null || fit.Score > best.Score))
			{
				best = fit;
				bestLevel = g.Key;
			}
		}
		string file = System.IO.Path.GetFileName(layout.File);
		try
		{
			string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "QbiqTools");
			System.IO.Directory.CreateDirectory(dir);
			List<string> lines = new List<string> { "json " + layout.File, "best level " + bestLevel + " score " + (best?.Score ?? 0.0) + " rot " + best?.Rotation + " mirror " + best?.Mirror + " offset " + best?.Offset };
			foreach (Wall w in walls)
			{
				Line l = (Line)((LocationCurve)w.Location).Curve;
				lines.Add($"{w.LevelId.Value} {l.GetEndPoint(0).X:F3} {l.GetEndPoint(0).Y:F3} {l.GetEndPoint(1).X:F3} {l.GetEndPoint(1).Y:F3} {w.Width:F3} {w.WallType.Kind}");
			}
			System.IO.File.WriteAllLines(System.IO.Path.Combine(dir, "layout_fit.txt"), lines);
		}
		catch
		{
		}
		if (best == null || best.Score < 0.5)
		{
			LayoutNote = $"Couldn't line up {file} with the walls of any level ({Math.Round((best?.Score ?? 0.0) * 100.0)}% of its walls match), so Revit's rooms are used.";
			return;
		}
		_layout = layout;
		_fit = best;
		_layoutLevel = bestLevel;
		foreach (Point[] core in layout.Cores)
		{
			_cores.Add(core.Select((Point p) => _fit.Apply(p)).Select((Point p) => new XYZ(p.X, p.Y, 0.0)).ToList());
		}
		Point[] Map(Point[] poly) => poly.Select(_fit.Apply).ToArray();
		foreach (LayoutRoom lr in layout.Rooms)
		{
			_jsonFurniture.AddRange(lr.Furniture.Select((Tuple<string, Point[]> f) => new Furnishing { Name = f.Item1, Outline = Map(f.Item2), Kind = Furnishing.KindOf(f.Item1, null) }));
			_jsonSwings.AddRange(lr.DoorSwings.Select(Map));
		}
		_jsonWindows.AddRange(layout.Windows.Select((Tuple<Point, Point> w) => Tuple.Create(_fit.Apply(w.Item1), _fit.Apply(w.Item2))));
		_jsonColumns.AddRange(layout.Columns.Select(Map));
		_circulation.AddRange(layout.Circulation.Select((Point[] c) => Map(c).Select((Point p) => new XYZ(p.X, p.Y, 0.0)).ToList()));
		if (layout.Entrance.HasValue)
		{
			Point e = _fit.Apply(layout.Entrance.Value);
			_entrance = new XYZ(e.X, e.Y, 0.0);
			Vector v = (layout.EntranceDir.LengthSquared > 1E-09) ? _fit.ApplyDir(layout.EntranceDir) : new Vector(0.0, 0.0);
			XYZ dir = new XYZ(v.X, v.Y, 0.0);
			// Hacia afuera del core: el lado de la puerta que no cae dentro del core
			_entranceOut = (dir.GetLength() > 0.0 && _cores.Any((List<XYZ> c) => Contains(c, _entrance + dir * 1.5))) ? dir.Negate() : dir;
		}
		string levelName = (_doc.GetElement(new ElementId(bestLevel)) as Level)?.Name ?? "";
		LayoutNote = $"Rooms from {file} on {levelName}: {layout.Rooms.Count} rooms, {Math.Round(best.Score * 100.0)}% of its walls line up with the model.";
		LayoutNote += (layout.Cores.Count > 0 || layout.Entrance.HasValue) ? " The core and the main entrance come from it too." : " It has no core or main entrance (the design JSON downloaded from the platform has them).";
	}

	private static double EdgeDistance(List<XYZ> poly, XYZ p)
	{
		double best = double.MaxValue;
		for (int i = 0; i < poly.Count; i++)
		{
			XYZ a = poly[i];
			XYZ ab = poly[(i + 1) % poly.Count] - a;
			double len = ab.DotProduct(ab);
			double t = (len < 1E-09) ? 0.0 : Math.Max(0.0, Math.Min(1.0, (p - a).DotProduct(ab) / len));
			best = Math.Min(best, (a + ab * t - p).GetLength());
		}
		return best;
	}

	// El muro del logo: el que se ve de frente al salir por la entrada principal, en la recepción si hay;
	// si la recepción no tiene ninguno de frente, cualquier muro a menos de 15 m de la entrada
	private FaceState PickLogoFace()
	{
		if (_entrance == null)
		{
			return null;
		}
		double minLen = UnitUtils.ConvertToInternalUnits(1.2, UnitTypeId.Meters);
		List<FaceState> usable = _faces.Where((FaceState f) => f.LevelId == _layoutLevel && f.Block == null && f.Face.U1 - f.Face.U0 >= minLen && !WallRules.IsService((f.RoomId != 0) ? NameOf(f.RoomId) : "")).ToList();
		List<FaceState> reception = usable.Where((FaceState f) => f.RoomId != 0 && (LogoPlacer.IsReception(NameOf(f.RoomId)) || ClientBoards.ZoneOf(NameOf(f.RoomId)) == ArtZone.Reception)).ToList();
		FaceState best = BestFacing(reception, true);
		return best ?? BestFacing(usable, false);
	}

	private FaceState BestFacing(List<FaceState> faces, bool anyDistance)
	{
		double near = UnitUtils.ConvertToInternalUnits(15.0, UnitTypeId.Meters);
		FaceState best = null;
		double bestScore = 0.0;
		foreach (FaceState f in faces)
		{
			XYZ mid = RoomFinder.Flat((f.Face.A + f.Face.B) / 2.0);
			XYZ toEntrance = RoomFinder.Flat(_entrance) - mid;
			double dist = toEntrance.GetLength();
			if (dist < 0.5 || (!anyDistance && dist > near))
			{
				continue;
			}
			// Que mire hacia la entrada y, si se sabe, que quede delante de quien sale
			double facing = RoomFinder.Flat(f.Face.Normal).Normalize().DotProduct(toEntrance / dist);
			double ahead = (_entranceOut.GetLength() > 0.0) ? (-toEntrance / dist).DotProduct(_entranceOut) : 1.0;
			if (facing < 0.3 || ahead < 0.0)
			{
				continue;
			}
			double score = facing * (0.5 + ahead) * Math.Sqrt(Math.Min(f.Face.U1 - f.Face.U0, 15.0)) / (1.0 + dist / 10.0);
			if (score > bestScore)
			{
				bestScore = score;
				best = f;
			}
		}
		return best;
	}

	private readonly List<string> _furnitureLog = new List<string>();

	private static readonly BuiltInCategory[] FurnitureCategories =
	{
		BuiltInCategory.OST_Furniture,
		BuiltInCategory.OST_FurnitureSystems,
		BuiltInCategory.OST_Casework,
		BuiltInCategory.OST_SpecialityEquipment,
		BuiltInCategory.OST_Planting,
		BuiltInCategory.OST_ElectricalEquipment,
		BuiltInCategory.OST_PlumbingFixtures,
		BuiltInCategory.OST_GenericModel,
		BuiltInCategory.OST_Columns,
		BuiltInCategory.OST_StructuralColumns
	};

	// Muebles y columnas del nivel en el modelo. Si el modelo casi no tiene (y hay JSON), los del diseño
	private List<Furnishing> FurnitureOn(Level level)
	{
		List<Furnishing> list = new List<Furnishing>();
		foreach (FamilyInstance fi in new FilteredElementCollector(_doc).WherePasses(new ElementMulticategoryFilter(FurnitureCategories)).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>())
		{
			// Lo colgado de muros (arte, logo, TVs) ya lo ve el que coloca
			if (fi.Host is Wall || fi.LevelId != level.Id)
			{
				continue;
			}
			BoundingBoxXYZ bb = fi.get_BoundingBox(null);
			if (bb == null || bb.Min.Z > level.ProjectElevation + 6.0)
			{
				continue;
			}
			Point[] outline = Outline(fi, bb);
			if (outline == null)
			{
				continue;
			}
			long cat = fi.Category?.Id.Value ?? 0;
			bool column = cat == (long)BuiltInCategory.OST_Columns || cat == (long)BuiltInCategory.OST_StructuralColumns;
			string name = fi.Symbol.FamilyName + " " + fi.Symbol.Name;
			list.Add(new Furnishing
			{
				Name = name,
				Outline = outline,
				Kind = column ? FurnKind.Column : Furnishing.KindOf(name, bb.Max.Z - level.ProjectElevation)
			});
		}
		_furnitureLog.Add($"{level.Name}: {list.Count} from the Revit model: " + string.Join(", ", list.GroupBy((Furnishing f) => f.Name).OrderByDescending((IGrouping<string, Furnishing> g) => g.Count()).Take(15).Select((IGrouping<string, Furnishing> g) => g.Key + " ×" + g.Count())));
		if (level.Id.Value == _layoutLevel)
		{
			if (list.Count(IsFurniture) < 5)
			{
				list.AddRange(_jsonFurniture);
				_furnitureLog.Add($"{level.Name}: added {_jsonFurniture.Count} from the design JSON");
			}
			if (!list.Any((Furnishing f) => f.Kind == FurnKind.Column))
			{
				list.AddRange(_jsonColumns.Select((Point[] c) => new Furnishing { Name = "Column", Outline = c, Kind = FurnKind.Column }));
			}
		}
		return list;
	}

	private static bool IsFurniture(Furnishing f)
	{
		return f.Kind != FurnKind.Column;
	}

	// Contorno en planta: la caja de la familia girada con la instancia; si no, la caja del modelo
	private static Point[] Outline(FamilyInstance fi, BoundingBoxXYZ bb)
	{
		try
		{
			GeometryInstance gi = fi.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Coarse })?.OfType<GeometryInstance>().FirstOrDefault();
			BoundingBoxXYZ local = gi?.GetSymbolGeometry()?.GetBoundingBox();
			if (local != null && local.Max.X - local.Min.X < 60.0 && local.Max.Y - local.Min.Y < 60.0)
			{
				XYZ[] c = { local.Min, new XYZ(local.Max.X, local.Min.Y, local.Min.Z), new XYZ(local.Max.X, local.Max.Y, local.Min.Z), new XYZ(local.Min.X, local.Max.Y, local.Min.Z) };
				return c.Select((XYZ p) => gi.Transform.OfPoint(p)).Select((XYZ p) => new Point(p.X, p.Y)).ToArray();
			}
		}
		catch
		{
		}
		if (bb.Max.X - bb.Min.X > 60.0 || bb.Max.Y - bb.Min.Y > 60.0)
		{
			return null;
		}
		return new[] { new Point(bb.Min.X, bb.Min.Y), new Point(bb.Max.X, bb.Min.Y), new Point(bb.Max.X, bb.Max.Y), new Point(bb.Min.X, bb.Max.Y) };
	}

	// Qué tramo de la cara ocupa un contorno pegado a ella (null si no la toca): en coordenadas del muro
	private class FaceFrame
	{
		public FaceState Face;

		public Point A;

		public Vector D;

		public Vector N;

		public FaceFrame(FaceState f)
		{
			Line line = (Line)((LocationCurve)f.Face.Wall.Location).Curve;
			XYZ d3 = RoomFinder.Flat(line.Direction).Normalize();
			XYZ n3 = RoomFinder.Flat(f.Face.Normal).Normalize();
			Face = f;
			A = new Point(f.Face.A.X, f.Face.A.Y);
			D = new Vector(d3.X, d3.Y);
			N = new Vector(n3.X, n3.Y);
		}
	}

	private static Tuple<double, double> Against(FaceFrame fr, Point[] outline, double gap)
	{
		FaceState f = fr.Face;
		Vector d = fr.D;
		Vector n = fr.N;
		Point a = fr.A;
		double vMin = double.MaxValue, vMax = double.MinValue, sMin = double.MaxValue, sMax = double.MinValue;
		foreach (Point p in outline)
		{
			Vector ap = p - a;
			double v = ap * n;
			double s = ap * d;
			vMin = Math.Min(vMin, v);
			vMax = Math.Max(vMax, v);
			sMin = Math.Min(sMin, s);
			sMax = Math.Max(sMax, s);
		}
		if (vMax < 0.05 || vMin > gap)
		{
			return null;
		}
		double len = f.Face.U1 - f.Face.U0;
		double s0 = Math.Max(0.0, sMin);
		double s1 = Math.Min(len, sMax);
		return (s1 - s0 < 0.1) ? null : Tuple.Create(f.Face.U0 + s0, f.Face.U0 + s1);
	}

	// Tramos tapados de cada cara del nivel: muebles altos y columnas delante, giro de puertas, ventanas del JSON
	private void BlockFaces(Level level, List<Furnishing> furniture, List<PlanDoor> doors)
	{
		double pad = UnitUtils.ConvertToInternalUnits(15.0, UnitTypeId.Centimeters);
		List<Point[]> swings = doors.Select(Swing).Where((Point[] s) => s != null).ToList();
		if (level.Id.Value == _layoutLevel)
		{
			swings.AddRange(_jsonSwings);
		}
		foreach (FaceState f in _faces.Where((FaceState f) => f.LevelId == level.Id.Value))
		{
			FaceFrame fr = new FaceFrame(f);
			List<FaceBlock> blocks = new List<FaceBlock>();
			foreach (Furnishing item in furniture)
			{
				switch (item.Kind)
				{
				case FurnKind.Tall:
				case FurnKind.Column:
				case FurnKind.Tv:
				{
					Tuple<double, double> r = Against(fr, item.Outline, 0.6);
					if (r != null)
					{
						blocks.Add(new FaceBlock { U0 = r.Item1 - pad, U1 = r.Item2 + pad, Kind = item.Kind, Reason = (item.Kind == FurnKind.Column) ? "a column" : ((item.Kind == FurnKind.Tv) ? "the design's TV" : ("a " + item.Label)) });
					}
					break;
				}
				case FurnKind.Sofa:
				{
					Tuple<double, double> r = Against(fr, item.Outline, 1.0);
					if (r != null && r.Item2 - r.Item1 >= 2.0)
					{
						_sofaFaces.Add(f.Key);
					}
					break;
				}
				}
			}
			foreach (Point[] swing in swings)
			{
				Tuple<double, double> r = Against(fr, swing, 0.3);
				if (r != null)
				{
					blocks.Add(new FaceBlock { U0 = r.Item1, U1 = r.Item2, Kind = FurnKind.Low, Reason = "a door swing" });
				}
			}
			if (level.Id.Value == _layoutLevel)
			{
				foreach (Tuple<Point, Point> w in _jsonWindows)
				{
					Tuple<double, double> r = OnWindow(f, w);
					if (r != null)
					{
						blocks.Add(new FaceBlock { U0 = r.Item1, U1 = r.Item2, Kind = FurnKind.Low, Reason = "a window" });
					}
				}
			}
			if (blocks.Count == 0)
			{
				continue;
			}
			_blocks[f.Key] = blocks;
			// Casi toda la cara tapada: no se usa
			double covered = Merge(blocks.Where((FaceBlock b) => b.Kind != FurnKind.Tv).Select((FaceBlock b) => Tuple.Create(Math.Max(f.Face.U0, b.U0), Math.Min(f.Face.U1, b.U1)))).Sum((Tuple<double, double> x) => Math.Max(0.0, x.Item2 - x.Item1));
			if (f.Block == null && covered >= (f.Face.U1 - f.Face.U0) * 0.85)
			{
				List<string> why = blocks.Where((FaceBlock b) => b.Kind != FurnKind.Tv).Select((FaceBlock b) => b.Reason).Distinct().Take(2).ToList();
				f.Block = (why.Contains("a window") ? "Window wall: " : "Blocked: ") + string.Join(" and ", why) + ((why.Count == 1 && why[0] == "a window") ? " in the design" : " in front of it");
			}
		}
	}

	// Giro de una puerta de Revit: un cuadrado del ancho de la hoja a cada lado del muro
	private static Point[] Swing(PlanDoor door)
	{
		Vector along = door.Along;
		Vector facing = door.Facing;
		if (along.LengthSquared < 1E-09 || facing.LengthSquared < 1E-09)
		{
			return null;
		}
		along.Normalize();
		facing.Normalize();
		Vector half = along * (door.Width / 2.0);
		Vector deep = facing * door.Width;
		return new[] { door.Center - half - deep, door.Center + half - deep, door.Center + half + deep, door.Center - half + deep };
	}

	// Una ventana del JSON sobre el muro de esta cara
	private static Tuple<double, double> OnWindow(FaceState f, Tuple<Point, Point> w)
	{
		Line line = (Line)((LocationCurve)f.Face.Wall.Location).Curve;
		XYZ d3 = RoomFinder.Flat(line.Direction).Normalize();
		Vector d = new Vector(d3.X, d3.Y);
		Vector e = w.Item2 - w.Item1;
		if (e.Length < 0.1 || Math.Abs(Vector.CrossProduct(d, e / e.Length)) > 0.1)
		{
			return null;
		}
		Point p0 = new Point(line.GetEndPoint(0).X, line.GetEndPoint(0).Y);
		Vector n = new Vector(0.0 - d.Y, d.X);
		if (Math.Abs((w.Item1 - p0) * n) > f.Face.Wall.Width + 0.6)
		{
			return null;
		}
		double u0 = Math.Max(f.Face.U0, Math.Min((w.Item1 - p0) * d, (w.Item2 - p0) * d));
		double u1 = Math.Min(f.Face.U1, Math.Max((w.Item1 - p0) * d, (w.Item2 - p0) * d));
		return (u1 - u0 < 0.1) ? null : Tuple.Create(u0, u1);
	}

	private static List<Tuple<double, double>> Merge(IEnumerable<Tuple<double, double>> ranges)
	{
		List<Tuple<double, double>> merged = new List<Tuple<double, double>>();
		foreach (Tuple<double, double> r in ranges.Where((Tuple<double, double> r) => r.Item2 > r.Item1).OrderBy((Tuple<double, double> r) => r.Item1))
		{
			if (merged.Count > 0 && r.Item1 <= merged[merged.Count - 1].Item2)
			{
				merged[merged.Count - 1] = Tuple.Create(merged[merged.Count - 1].Item1, Math.Max(merged[merged.Count - 1].Item2, r.Item2));
			}
			else
			{
				merged.Add(r);
			}
		}
		return merged;
	}

	// Personas por room: del JSON o, si no, las sillas que hay adentro
	private void AddPeople(PlanLevel plan, List<Furnishing> furniture)
	{
		List<Furnishing> chairs = furniture.Where((Furnishing f) => f.Kind == FurnKind.Chair).ToList();
		foreach (PlanRoom room in plan.Rooms)
		{
			int people = _people.TryGetValue(room.Id, out var p) ? p : 0;
			if (people == 0 && chairs.Count > 0 && room.Loops.Count > 0)
			{
				List<XYZ> poly = room.Loops[0].Select((Point q) => new XYZ(q.X, q.Y, 0.0)).ToList();
				people = chairs.Count((Furnishing c) => Contains(poly, new XYZ(c.Center.X, c.Center.Y, 0.0)));
				if (people > 0)
				{
					_people[room.Id] = people;
				}
			}
			if (people > 0)
			{
				room.AreaText += (people == 1) ? ", 1 person" : $", {people} people";
			}
		}
	}

	// Muro de la TV según el diseño: junto a la TV que trae el diseño, o en la cabecera de la mesa de reuniones
	private int DesignTvSpot(long roomId, FoundRoom found, TvTarget target)
	{
		if (!_furniture.TryGetValue(found.LevelId.Value, out var furniture) || found.Polygon == null || found.Polygon.Count < 3)
		{
			return -1;
		}
		List<Furnishing> inside = furniture.Where((Furnishing f) => Contains(found.Polygon, new XYZ(f.Center.X, f.Center.Y, 0.0))).ToList();
		List<int> allowed = Enumerable.Range(0, target.Spots.Count).Where((int i) => SpotAllowed(roomId, target.Spots[i])).ToList();
		if (allowed.Count == 0)
		{
			return -1;
		}
		Furnishing tv = inside.FirstOrDefault((Furnishing f) => f.Kind == FurnKind.Tv);
		if (tv != null)
		{
			int best = allowed.OrderBy((int i) => Distance(target.Spots[i].OnFace, tv.Center)).First();
			return (Distance(target.Spots[best].OnFace, tv.Center) < 4.0) ? best : -1;
		}
		Furnishing table = inside.Where((Furnishing f) => f.Kind == FurnKind.Table && f.Elongation > 1.3).OrderByDescending((Furnishing f) => f.Elongation).FirstOrDefault();
		if (table != null)
		{
			Vector axis = table.LongAxis;
			// La primera (la que el orden normal prefiere, lejos de la puerta) de las que miran a lo largo de la mesa
			foreach (int i in allowed)
			{
				XYZ n = target.Spots[i].Normal;
				if (n != null && Math.Abs(n.X * axis.X + n.Y * axis.Y) > 0.85)
				{
					return i;
				}
			}
		}
		return -1;
	}

	private static double Distance(XYZ p, Point q)
	{
		return (p == null) ? double.MaxValue : Math.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Y - q.Y) * (p.Y - q.Y));
	}

	private bool InLayoutRoom(Room r)
	{
		if (_fit == null || r.LevelId.Value != _layoutLevel || !(r.Location is LocationPoint lp))
		{
			return false;
		}
		return _layoutRooms.Values.Any((LayoutRoomInfo l) => Contains(l.Polygon, lp.Point));
	}

	// Cada room del JSON: su polígono en el modelo y los muros de Revit que siguen sus bordes
	private List<long> BuildLayoutRooms(Level level, List<Wall> onLevel)
	{
		List<long> ids = new List<long>();
		List<Wall> straight = onLevel.Where((Wall w) => w.Location is LocationCurve { Curve: Line }).ToList();
		for (int i = 0; i < _layout.Rooms.Count; i++)
		{
			LayoutRoom lr = _layout.Rooms[i];
			List<XYZ> poly = lr.Polygon.Select((Point p) => _fit.Apply(p)).Select((Point p) => new XYZ(p.X, p.Y, 0.0)).ToList();
			long id = -1000L - i;
			FoundRoom found = new FoundRoom { LevelId = level.Id, Polygon = poly, Area = Math.Abs(SignedArea(poly)) };
			found.Walls.AddRange(EdgeWalls(poly, straight));
			_layoutRooms[id] = new LayoutRoomInfo { Name = lr.Name, LevelId = level.Id, Polygon = poly };
			if (lr.People.HasValue)
			{
				_people[id] = lr.People.Value;
			}
			_virtual[id] = found;
			ids.Add(id);
		}
		return ids;
	}

	private static double SignedArea(List<XYZ> poly)
	{
		double a = 0.0;
		for (int i = 0; i < poly.Count; i++)
		{
			XYZ p = poly[i];
			XYZ q = poly[(i + 1) % poly.Count];
			a += p.X * q.Y - q.X * p.Y;
		}
		return a / 2.0;
	}

	// Un muro sigue un borde si es paralelo, está a menos de medio grosor + 15 cm y se solapan al menos 10 cm
	private static IEnumerable<RoomWall> EdgeWalls(List<XYZ> poly, List<Wall> walls)
	{
		for (int i = 0; i < poly.Count; i++)
		{
			XYZ a = poly[i];
			XYZ b = poly[(i + 1) % poly.Count];
			XYZ e = b - a;
			if (e.GetLength() < 0.3)
			{
				continue;
			}
			XYZ ed = e.Normalize();
			XYZ mid = (a + b) / 2.0;
			foreach (Wall w in walls)
			{
				Line line = (Line)((LocationCurve)w.Location).Curve;
				XYZ p0 = RoomFinder.Flat(line.GetEndPoint(0));
				XYZ d = RoomFinder.Flat(line.Direction).Normalize();
				if (Math.Abs(d.X * ed.Y - d.Y * ed.X) > 0.09)
				{
					continue;
				}
				XYZ n = new XYZ(0.0 - d.Y, d.X, 0.0);
				if (Math.Abs((mid - p0).DotProduct(n)) > w.Width / 2.0 + 0.5)
				{
					continue;
				}
				double ua = (a - p0).DotProduct(d);
				double ub = (b - p0).DotProduct(d);
				double u0 = Math.Max(0.0, Math.Min(ua, ub));
				double u1 = Math.Min(line.Length, Math.Max(ua, ub));
				if (u1 - u0 < 0.33)
				{
					continue;
				}
				XYZ inside = mid + RoomFinder.Flat(w.Orientation) * (w.Width / 2.0 + 0.6);
				int sign = Contains(poly, inside) ? 1 : (-1);
				yield return new RoomWall
				{
					Wall = w,
					Sign = sign,
					U0 = u0,
					U1 = u1,
					A = p0 + d * u0,
					B = p0 + d * u1
				};
			}
		}
	}

	private PlanRoom ToLayoutPlan(long id)
	{
		LayoutRoomInfo l = _layoutRooms[id];
		FoundRoom found = _virtual[id];
		return new PlanRoom
		{
			Id = id,
			Name = l.Name,
			Number = "",
			AreaText = UnitFormatUtils.Format(_units, SpecTypeId.Area, found.Area, forEditing: false) + ", from the design JSON",
			Loops = new List<Point[]> { l.Polygon.Select((XYZ p) => new Point(p.X, p.Y)).ToArray() },
			Label = new Point(l.Polygon.Average((XYZ p) => p.X), l.Polygon.Average((XYZ p) => p.Y))
		};
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

	private void AddFaces(Level level, List<Room> rooms, List<Room> adopted, List<long> layoutIds, List<Wall> walls)
	{
		List<FaceState> roomFaces = (from r in rooms
			from f in RoomScope.Faces(r, _boundary)
			where Usable(f.Wall)
			select NewFace(level, r.Id.Value, f)).Concat(from r in adopted
			from f in VirtualFaces(_virtual[r.Id.Value])
			select NewFace(level, r.Id.Value, f)).Concat(from id in layoutIds
			from f in VirtualFaces(_virtual[id])
			select NewFace(level, id, f)).ToList();
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
		bool coreFront = false;
		if (block == null && level.Id.Value == _layoutLevel && _cores.Count > 0)
		{
			// Con el core del JSON: las caras que dan al pasillo son de core; las de adentro sin room, no se usan
			XYZ mid = (face.A + face.B) / 2.0;
			XYZ axis = RoomFinder.Flat(mid - face.Normal * (face.Wall.Width / 2.0));
			XYZ front = RoomFinder.Flat(mid + face.Normal * 1.0);
			bool frontInCore = _cores.Any((List<XYZ> c) => Contains(c, front));
			if (frontInCore && roomId == 0)
			{
				block = "Inside the core (elevators, stairs, restrooms)";
			}
			else if (!frontInCore && _cores.Any((List<XYZ> c) => EdgeDistance(c, axis) <= face.Wall.Width / 2.0 + 0.5))
			{
				coreFront = true;
			}
		}
		// Si da a un pasillo del JSON, no es fachada ni un cuarto sin room
		bool corridor = roomId == 0 && level.Id.Value == _layoutLevel && _circulation.Count > 0 && _circulation.Any((List<XYZ> c) => Contains(c, RoomFinder.Flat((face.A + face.B) / 2.0 + face.Normal * 1.0)));
		if (block == null && roomId == 0 && !corridor && Outside(level, face))
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
			Core = info.Core || coreFront,
			CoreFront = coreFront,
			Corridor = corridor,
			Block = block,
			Unroomed = roomId == 0 && block == null && !corridor && InSmallSpace(level, face)
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
		MeasureArt(ArtTypes);
		ArtTypes = ArtTypes.OrderBy((TypeItem t) => (t.Area > 0.0) ? 0 : 1).ThenBy((TypeItem t) => t.Area).ThenBy((TypeItem t) => t.Label).ToList();
		TvTypes = Keep(TvTypes, tv.Concat(loaded.Where((TypeItem t) => PlaceTvCommand.LooksLikeTv(t.Label))).ToList(), fresh);
	}

	// Ancho × alto de cada pieza, de una instancia en el modelo (las secciones de artwork las tienen)
	private void MeasureArt(List<TypeItem> items)
	{
		Dictionary<long, TypeItem> byId = items.ToDictionary((TypeItem t) => t.Id);
		HashSet<long> done = new HashSet<long>();
		foreach (FamilyInstance fi in new FilteredElementCollector(_doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>())
		{
			long id = fi.Symbol.Id.Value;
			if (!byId.TryGetValue(id, out var item) || done.Contains(id))
			{
				continue;
			}
			BoundingBoxXYZ bb = fi.get_BoundingBox(null);
			if (bb == null)
			{
				continue;
			}
			double w = Math.Max(bb.Max.X - bb.Min.X, bb.Max.Y - bb.Min.Y);
			double h = bb.Max.Z - bb.Min.Z;
			if (w <= 0.01 || h <= 0.01 || w > 30.0 || h > 30.0)
			{
				continue;
			}
			done.Add(id);
			item.Area = w * h;
			item.Size = $"{Math.Round(UnitUtils.ConvertFromInternalUnits(w, UnitTypeId.Centimeters))} × {Math.Round(UnitUtils.ConvertFromInternalUnits(h, UnitTypeId.Centimeters))} cm";
		}
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
		if (roomId == 0 || _artRooms.Contains(roomId) || !HasRoom(roomId))
		{
			return null;
		}
		string name = NameOf(roomId);
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
		ArtZone auto = ClientBoards.ZoneOf(NameOf(roomId));
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
				gap = Math.Max(gap, UnitUtils.ConvertToInternalUnits(3.5, UnitTypeId.Meters) / AmountFactor);
				max = Math.Max(max, (int)Math.Round(4.0 * AmountFactor));
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
		_placerGap[collection] = gap;
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
			foreach (KeyValuePair<long, RoomUse> kv in req.Uses.Where((KeyValuePair<long, RoomUse> kv) => kv.Value == RoomUse.Tv && HasRoom(kv.Key)))
			{
				_uses[kv.Key] = RoomUse.Tv;
			}
			foreach (KeyValuePair<long, string> kv in req.RoomCollections.Where((KeyValuePair<long, string> kv) => _rooms.ContainsKey(kv.Key) && Collections.Any((ArtCollection c) => c.Name == kv.Value)))
			{
				_roomCollection[kv.Key] = kv.Value;
			}
			foreach (KeyValuePair<long, ArtZone> kv in req.RoomZones.Where((KeyValuePair<long, ArtZone> kv) => HasRoom(kv.Key)))
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
		if (spot.Symbol == null || f == null || !Allowed(f) || f.On)
		{
			return false;
		}
		// Un mueble alto, columna o puerta delante tapa la TV (una TV del diseño no: ahí es donde va)
		double a = spot.Center - spot.Width / 2.0;
		double b = spot.Center + spot.Width / 2.0;
		return !_blocks.TryGetValue(f.Key, out var blocks) || !blocks.Any((FaceBlock x) => x.Kind != FurnKind.Tv && x.U1 > a && x.U0 < b);
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
		int preferred = DesignTvSpot(roomId, found, target);
		if (preferred >= 0)
		{
			target.Current = preferred;
		}
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
			double gap = GapOf(group.Key);
			FaceRanges ranges = new FaceRanges();
			foreach (FaceState f in faces)
			{
				foreach (Tuple<double, double> free in FreeOf(f, ManualOn(f).Select((WallPlacer.Piece m) => Tuple.Create(m.Center - m.Width / 2.0 - gap, m.Center + m.Width / 2.0 + gap))))
				{
					ranges.Add(wall.Id, f.Face.Sign, free.Item1, free.Item2);
				}
			}
			placer.Ranges = ranges;
			List<string> notes = new List<string>();
			foreach (WallPlacer.Piece p in placer.Plan(wall, ranges.Signs(wall.Id), notes))
			{
				FaceState face = faces.FirstOrDefault((FaceState f) => f.Face.Sign == p.Sign && p.Center >= f.Face.U0 - 0.05 && p.Center <= f.Face.U1 + 0.05) ?? faces.First((FaceState f) => f.Face.Sign == p.Sign);
				int index = _artPlans.Count((ArtPlan x) => x.Face == face);
				ArtPlan plan = new ArtPlan { Key = ++_nextKey, Face = face, Piece = p, Placer = placer, Index = index };
				if (_pins.TryGetValue(Tuple.Create(face.Key, index), out var pinned))
				{
					MoveTo(plan, pinned);
				}
				_artPlans.Add(plan);
			}
			foreach (FaceState f in faces)
			{
				foreach (WallPlacer.Piece m in ManualOn(f))
				{
					_artPlans.Add(new ArtPlan { Key = ++_nextKey, Face = f, Piece = m, Placer = placer, Index = -1, Manual = true });
				}
			}
			string why = Sentence(notes.FirstOrDefault((string n) => n.StartsWith("free")) ?? notes.FirstOrDefault() ?? "No free space here for the artwork");
			foreach (FaceState f in faces.Where((FaceState f) => !_artPlans.Any((ArtPlan a) => a.Face == f)))
			{
				_faceProblems[f.Key] = why;
			}
		}
	}

	// Arrastrar una pieza en el plano: se queda dentro de su cara y la posición sobrevive a los cambios de opciones
	public PreviewResult MovePiece(int markerKey, Point center)
	{
		ArtPlan a = _artPlans.FirstOrDefault((ArtPlan x) => x.Key == markerKey);
		if (a == null)
		{
			return State(null);
		}
		Line line = (Line)((LocationCurve)a.Piece.Wall.Location).Curve;
		XYZ p0 = line.GetEndPoint(0);
		double u = (new XYZ(center.X, center.Y, p0.Z) - p0).DotProduct(line.Direction);
		double half = a.Piece.Width / 2.0;
		double lo = a.Face.Face.U0 + half;
		double hi = a.Face.Face.U1 - half;
		u = (lo <= hi) ? Math.Max(lo, Math.Min(hi, u)) : a.Piece.Center;
		MoveTo(a, u);
		if (!a.Manual)
		{
			_pins[Tuple.Create(a.Face.Key, a.Index)] = u;
		}
		return State(null);
	}

	private List<WallPlacer.Piece> ManualOn(FaceState f)
	{
		return (f.On && _manual.TryGetValue(f.Key, out var list)) ? list : new List<WallPlacer.Piece>();
	}

	private double GapOf(string collection)
	{
		return _placerGap.TryGetValue(collection, out var g) ? g : (_req?.Gap ?? 0.0);
	}

	// Tramos libres de la cara, sin los intervalos dados y con la separación a las puntas
	private List<Tuple<double, double>> FreeOf(FaceState f, IEnumerable<Tuple<double, double>> taken)
	{
		List<Tuple<double, double>> free = new List<Tuple<double, double>>();
		double at = f.Face.U0;
		if (_blocks.TryGetValue(f.Key, out var blocks))
		{
			taken = taken.Concat(blocks.Select((FaceBlock b) => Tuple.Create(b.U0, b.U1)));
		}
		foreach (Tuple<double, double> t in taken.OrderBy((Tuple<double, double> t) => t.Item1))
		{
			if (t.Item1 > at)
			{
				free.Add(Tuple.Create(at, Math.Min(t.Item1, f.Face.U1)));
			}
			at = Math.Max(at, t.Item2);
		}
		if (f.Face.U1 > at)
		{
			free.Add(Tuple.Create(at, f.Face.U1));
		}
		return free.Where((Tuple<double, double> x) => x.Item2 - x.Item1 > 0.01).ToList();
	}

	// Dónde cabe una pieza más en una cara con artwork (null si no cabe): el centro del tramo libre más largo
	private Tuple<double, double> RoomForOneMore(FaceState f)
	{
		// Solo en una línea azul que ya tiene piezas y ningún problema (una roja no tiene donde poner otra)
		if (!f.On || f.TvWall || _faceProblems.ContainsKey(f.Key) || !_artPlans.Any((ArtPlan a) => a.Face == f))
		{
			return null;
		}
		string key = CollectionOf(f.RoomId);
		WallPlacer placer = (key.Length == 0) ? ((_artSymbols.Count > 0) ? _wallPlacer : null) : (_placers.TryGetValue(key, out var p) ? p : null);
		double min = placer?.MinWidth ?? -1.0;
		if (min <= 0.0)
		{
			return null;
		}
		double gap = GapOf(key);
		double clear = _req?.Clearance ?? 0.0;
		IEnumerable<Tuple<double, double>> taken = _artPlans.Where((ArtPlan a) => a.Face == f)
			.Select((ArtPlan a) => Tuple.Create(a.Piece.Center - a.Piece.Width / 2.0 - gap, a.Piece.Center + a.Piece.Width / 2.0 + gap))
			.Concat(new[] { Tuple.Create(f.Face.U0 - 1.0, f.Face.U0 + clear), Tuple.Create(f.Face.U1 - clear, f.Face.U1 + 1.0) });
		return FreeOf(f, taken).Where((Tuple<double, double> x) => x.Item2 - x.Item1 >= min).OrderByDescending((Tuple<double, double> x) => x.Item2 - x.Item1).FirstOrDefault();
	}

	public PreviewResult AddPiece(int faceKey)
	{
		FaceState f = _faces.FirstOrDefault((FaceState x) => x.Key == faceKey);
		Tuple<double, double> room = (f == null) ? null : RoomForOneMore(f);
		if (room == null)
		{
			return State("There's no room for another piece on this wall.");
		}
		string key = CollectionOf(f.RoomId);
		WallPlacer.Piece added = null;
		List<string> notes = new List<string>();
		InTransaction(delegate
		{
			WallPlacer placer = PlacerFor(key);
			if (placer == null)
			{
				return;
			}
			FaceRanges ranges = new FaceRanges();
			ranges.Add(f.Face.Wall.Id, f.Face.Sign, room.Item1, room.Item2);
			placer.Ranges = ranges;
			double mid = (room.Item1 + room.Item2) / 2.0;
			added = placer.Plan(f.Face.Wall, new List<int> { f.Face.Sign }, notes).OrderBy((WallPlacer.Piece p) => Math.Abs(p.Center - mid)).FirstOrDefault();
			if (added != null)
			{
				if (!_manual.TryGetValue(f.Key, out var list))
				{
					list = (_manual[f.Key] = new List<WallPlacer.Piece>());
				}
				list.Add(added);
				ReplanWall(f.Face.Wall);
			}
		});
		return State((added == null) ? Sentence(notes.FirstOrDefault() ?? "No piece fits there") + "." : null);
	}

	public PreviewResult RemovePiece(int markerKey)
	{
		ArtPlan a = _artPlans.FirstOrDefault((ArtPlan x) => x.Key == markerKey && x.Manual);
		if (a != null && _manual.TryGetValue(a.Face.Key, out var list))
		{
			list.Remove(a.Piece);
			InTransaction(delegate
			{
				ReplanWall(a.Piece.Wall);
			});
		}
		return State(null);
	}

	private static void MoveTo(ArtPlan a, double u)
	{
		a.Piece.OnFace = a.Piece.OnFace + a.Piece.Along * (u - a.Piece.Center);
		a.Piece.Center = u;
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
		List<long> ids = roomIds.Where(HasRoom).ToList();
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
					empty.Add(NameOf(id));
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
		_pins.Keys.Where((Tuple<int, int> k) => k.Item1 == f.Key).ToList().ForEach((Tuple<int, int> k) => _pins.Remove(k));
		_manual.Remove(f.Key);
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

	// El slider de cantidad: menos o más piezas al sugerir (y separación en pasillos con cliente)
	private double AmountFactor => new[] { 0.4, 0.7, 1.0, 1.4, 2.0 }[Math.Max(1, Math.Min(5, _req?.Amount ?? 3)) - 1];

	private int Take(int normal)
	{
		return Math.Max(1, (int)Math.Round(normal * AmountFactor));
	}

	// Con otra cantidad: se rehacen las sugerencias (lo marcado a mano queda)
	public PreviewResult Resuggest()
	{
		if (_req == null)
		{
			return null;
		}
		List<FaceState> old = _faces.Where((FaceState f) => f.Suggested).ToList();
		if (old.Count == 0)
		{
			return State(null);
		}
		old.ForEach(delegate(FaceState f)
		{
			f.On = false;
			f.Suggested = false;
		});
		InTransaction(delegate
		{
			old.Select((FaceState f) => f.Face.Wall).GroupBy((Wall w) => w.Id).Select((IGrouping<ElementId, Wall> g) => g.First()).ToList().ForEach(ReplanWall);
		});
		return Suggest();
	}

	public PreviewResult ToggleAccent(int faceKey)
	{
		FaceState f = _faces.FirstOrDefault((FaceState x) => x.Key == faceKey);
		if (f == null || _req == null)
		{
			return null;
		}
		if (f.Block != null && !f.Block.StartsWith("Blocked", StringComparison.Ordinal))
		{
			return State(f.Block + ". No accent wall here.");
		}
		if (IsAccent(f))
		{
			if (!_accentOn.Remove(f.Key))
			{
				_accentOff.Add(f.Key);
			}
		}
		else
		{
			_accentOff.Remove(f.Key);
			if (!IsAccent(f))
			{
				_accentOn.Add(f.Key);
			}
		}
		return State(IsAccent(f) ? "Accent wall here. Click it again to take it off." : null);
	}

	public PreviewResult ToggleLogo(int faceKey)
	{
		FaceState f = _faces.FirstOrDefault((FaceState x) => x.Key == faceKey);
		if (f == null || _req == null)
		{
			return null;
		}
		if (!IsLogo(f) && f.Block != null && !f.Block.StartsWith("Blocked", StringComparison.Ordinal))
		{
			return State(f.Block + ". No logo here.");
		}
		if (IsLogo(f))
		{
			_logoOn.Remove(f.Key);
			if (f == _logoFace)
			{
				_logoAutoOff = true;
			}
		}
		else if (f == _logoFace)
		{
			_logoAutoOff = false;
		}
		else
		{
			_logoOn.Add(f.Key);
		}
		if (IsLogo(f) && f.On)
		{
			f.On = false;
			f.Suggested = false;
			InTransaction(delegate
			{
				ReplanWall(f.Face.Wall);
			});
		}
		return State(IsLogo(f) ? "Logo on this wall. Click it again to take it off." : null);
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
			where f.RoomId != 0 && Allowed(f) && !IsTv(f.RoomId) && !IsLogo(f)
			group f by f.RoomId)
		{
			string name = NameOf(room.Key);
			double m2 = UnitUtils.ConvertFromInternalUnits(AreaOf(room.Key), UnitTypeId.SquareMeters);
			bool open = Has(name, OpenWords);
			if (WallRules.IsService(name) || (m2 < 8.0 && !open) || room.Any((FaceState f) => f.On))
			{
				continue;
			}
			int take = Take(open ? 3 : ((m2 >= 40.0) ? 2 : 1));
			double min = UnitUtils.ConvertToInternalUnits(open ? 3.0 : 1.5, UnitTypeId.Meters) / Math.Max(1.0, AmountFactor);
			picks.AddRange(room.Where((FaceState f) => f.Face.U1 - f.Face.U0 >= min).OrderByDescending((FaceState f) => _sofaFaces.Contains(f.Key)).ThenByDescending((FaceState f) => f.Face.U1 - f.Face.U0).Take(take));
		}
		double freeMin = UnitUtils.ConvertToInternalUnits(3.0, UnitTypeId.Meters) / AmountFactor;
		double coreMin = UnitUtils.ConvertToInternalUnits(1.5, UnitTypeId.Meters);
		picks.AddRange(_faces.Where((FaceState f) => f.RoomId == 0 && !f.On && !f.Unroomed && !IsLogo(f) && Allowed(f) && f.Face.U1 - f.Face.U0 >= (f.CoreFront ? coreMin : freeMin)));
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
			where f.RoomId != 0 && Allowed(f) && !f.TvWall && !IsLogo(f)
			group f by f.RoomId)
		{
			if (room.Any((FaceState f) => f.On) || WallRules.IsService(NameOf(room.Key)))
			{
				continue;
			}
			// Los muros con un sofá delante primero (el arte va arriba), después los más largos
			List<FaceState> longest = room.OrderByDescending((FaceState f) => _sofaFaces.Contains(f.Key)).ThenByDescending((FaceState f) => f.Face.U1 - f.Face.U0).ToList();
			switch (ZoneOfRoom(room.Key))
			{
			case ArtZone.General:
				picks.AddRange(longest.Where((FaceState f) => f.Face.U1 - f.Face.U0 >= m3 / AmountFactor).Take(Take(3)));
				break;
			case ArtZone.Kitchen:
				picks.AddRange(longest.Where((FaceState f) => f.Face.U1 - f.Face.U0 >= m15).Take(Take(2)));
				break;
			case ArtZone.Meeting:
				picks.AddRange(longest.Where((FaceState f) => f.Face.U1 - f.Face.U0 >= m15).Take(Take(IsTv(room.Key) ? 1 : 2)));
				break;
			case ArtZone.Reception:
				// El muro del logo ya quedó afuera; sin él, se reserva el más largo
				picks.AddRange(longest.Skip((!_faces.Any((FaceState x) => x.RoomId == room.Key && IsLogo(x)) && longest.Count > 1) ? 1 : 0).Where((FaceState f) => f.Face.U1 - f.Face.U0 >= m15).Take(1));
				break;
			}
		}
		picks.AddRange(_faces.Where((FaceState f) => f.RoomId == 0 && !f.On && !f.Unroomed && !IsLogo(f) && Allowed(f) && f.Face.U1 - f.Face.U0 >= (f.CoreFront ? m15 : m3) / AmountFactor));
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
			Title = ((f.RoomId != 0) ? $"{side} wall of {NameOf(f.RoomId)}, {length}" : (f.CoreFront ? $"Core wall facing the corridor, {length}" : f.Corridor ? $"Corridor wall, {length}" : (f.Unroomed ? $"Wall of a closed space with no room in Revit, {length}" : $"Wall with no room, {length}"))) + PartlyBlocked(f),
			WallType = f.Face.Wall.WallType.Name,
			Allowed = Allowed(f),
			Reason = Why(f),
			Core = f.Core,
			TvWall = f.TvWall,
			Logo = IsLogo(f),
			Accent = IsAccent(f),
			On = f.On || f.TvWall,
			Suggested = f.Suggested,
			Problem = _faceProblems.TryGetValue(f.Key, out var p) ? p : null,
			AddAt = AddPoint(f)
		};
	}

	// Lo que tapa parte de una cara que igual se puede usar: el arte lo esquiva
	private string PartlyBlocked(FaceState f)
	{
		if (f.Block != null || !_blocks.TryGetValue(f.Key, out var blocks))
		{
			return "";
		}
		List<string> what = blocks.Select((FaceBlock b) => b.Reason).Distinct().Take(3).ToList();
		return " (artwork keeps clear of " + string.Join(", ", what) + ")";
	}

	private Point? AddPoint(FaceState f)
	{
		Tuple<double, double> room = RoomForOneMore(f);
		if (room == null)
		{
			return null;
		}
		Line line = (Line)((LocationCurve)f.Face.Wall.Location).Curve;
		XYZ at = line.GetEndPoint(0) + line.Direction * ((room.Item1 + room.Item2) / 2.0) + f.Face.Normal * (f.Face.Wall.Width / 2.0);
		return new Point(at.X, at.Y);
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
			LevelId = LevelOf(roomId).Value,
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
			Label = TypeName(a.Piece.Symbol),
			Manual = a.Manual
		};
	}

	public void Place(HashSet<int> skipped)
	{
		List<ElementId> placed = new List<ElementId>();
		List<string> notes = new List<string>();
		int tvs = 0;
		int arts = 0;
		int logos = 0;
		int accents = 0;
		List<FaceState> logoFaces = _faces.Where(IsLogo).ToList();
		List<FaceState> accentFaces = _faces.Where(IsAccent).ToList();
		// El logo del cliente: familia y material antes de la transacción, como Place Logo
		FamilySymbol logo = (logoFaces.Count > 0) ? BuildLogo(logoFaces, notes) : null;
		TransactionStatus status;
		using (Transaction t = new Transaction(_doc, "Room Studio: place TVs and artwork"))
		{
			PlaceArtworkCommand.SwallowWarnings(t);
			t.Start();
			Activate(AllSymbols());
			// Primero los accent walls: si hay que cortar un muro, lo que va encima se reubica en su tramo
			foreach (FaceState f in accentFaces)
			{
				using SubTransaction sub = new SubTransaction(_doc);
				sub.Start();
				try
				{
					ApplyAccent(f);
					accents++;
					sub.Commit();
				}
				catch (Exception ex)
				{
					sub.RollBack();
					notes.Add("Accent wall in " + ((f.RoomId != 0) ? NameOf(f.RoomId) : PlaceArtworkCommand.WallLabel(f.Face.Wall)) + ": " + ex.Message);
				}
			}
			foreach (KeyValuePair<long, TvTarget> p in _tvPlans.Where((KeyValuePair<long, TvTarget> p) => p.Value.Chosen != null))
			{
				Try(NameOf(p.Key), () => _tvPlacer.Place(p.Value.Chosen, out var reason) ?? Fail(reason), ref tvs);
			}
			foreach (ArtPlan a in _artPlans.Where((ArtPlan a) => !skipped.Contains(a.Key)))
			{
				string label = (a.Face.RoomId != 0) ? NameOf(a.Face.RoomId) : PlaceArtworkCommand.WallLabel(a.Piece.Wall);
				Try(label, () => a.Placer.PlacePiece(a.Piece, out var reason) ?? Fail(reason), ref arts);
			}
			if (logo != null)
			{
				foreach (FaceState f in logoFaces)
				{
					Try("Logo", () => PlaceLogo(logo, f), ref logos);
				}
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
			MainInstruction = $"Placed {tvs} TV(s), {arts} artwork piece(s)" + ((logos > 0) ? $", {logos} logo(s)" : "") + ((accents > 0) ? $" and {accents} accent wall(s)." : "."),
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

	private static readonly double LogoWidth = UnitUtils.ConvertToInternalUnits(120.0, UnitTypeId.Centimeters);

	// Arma la familia "Qbiq Logo" con la imagen del cliente. Sin imagen todavía, con una de reemplazo:
	// cuando llegue la del cliente, Room Studio o Place Logo la cambian y todos los logos se actualizan
	private FamilySymbol BuildLogo(List<FaceState> faces, List<string> notes)
	{
		string image = (_req?.LogoImage != null && System.IO.File.Exists(_req.LogoImage)) ? _req.LogoImage : LogoImage;
		if (image == null)
		{
			image = LogoFamily.Placeholder();
			notes.Add("Logo: no client image yet, so a placeholder went in. When the client sends it, choose it in Room Studio or Place Logo and every logo updates.");
		}
		double width = Math.Min(LogoWidth, faces.Min((FaceState f) => f.Face.U1 - f.Face.U0) * 0.8);
		double height = width * LogoCommand.Aspect(image);
		try
		{
			FamilySymbol symbol = LogoFamily.Build(_doc, _uidoc.Application.Application, width, height);
			if (symbol == null)
			{
				throw new InvalidOperationException("the logo family didn't load");
			}
			using (Transaction t = new Transaction(_doc, "Room Studio: logo material"))
			{
				t.Start();
				LogoFamily.Paint(_doc, _uidoc.Application.Application, symbol, image, width, height);
				t.Commit();
			}
			return symbol;
		}
		catch (Exception ex)
		{
			notes.Add("Logo: couldn't build it: " + ex.Message);
			return null;
		}
	}

	// Un logo centrado en su cara, a 1,50 m al centro (si ese muro ya tiene uno de ese lado, no se repite)
	private FamilyInstance PlaceLogo(FamilySymbol symbol, FaceState f)
	{
		if (!symbol.IsActive)
		{
			symbol.Activate();
			_doc.Regenerate();
		}
		bool already = new FilteredElementCollector(_doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().Any((FamilyInstance fi) => fi.Symbol.Family.Id == symbol.Family.Id && fi.Host?.Id == f.Face.Wall.Id && fi.FacingOrientation.DotProduct(f.Face.Normal) > 0.5);
		if (already)
		{
			return Fail("this wall already has the logo (its image was updated)");
		}
		WallPlacer placer = new WallPlacer(_doc, new List<FamilySymbol> { symbol }, UnitUtils.ConvertToInternalUnits(150.0, UnitTypeId.Centimeters), HeightAlign.Center, UnitUtils.ConvertToInternalUnits(30.0, UnitTypeId.Centimeters), 0.0, 1, SideMode.Auto, Fmt);
		FaceRanges ranges = new FaceRanges();
		ranges.Add(f.Face.Wall.Id, f.Face.Sign, f.Face.U0, f.Face.U1);
		placer.Ranges = ranges;
		List<string> why = new List<string>();
		WallPlacer.Piece piece = placer.Plan(f.Face.Wall, new List<int> { f.Face.Sign }, why).FirstOrDefault();
		if (piece == null)
		{
			return Fail(why.FirstOrDefault() ?? "no free space on the logo wall");
		}
		return placer.PlacePiece(piece, out var reason) ?? Fail(reason);
	}

	// Accent wall: si el muro sigue más allá de este room, se corta para cambiar solo su tramo;
	// después se pinta la cara con el material o se cambia el tramo al tipo de muro elegido
	private void ApplyAccent(FaceState f)
	{
		Wall wall = f.Face.Wall;
		Line line = (Line)((LocationCurve)wall.Location).Curve;
		double len = line.Length;
		double a = Math.Max(0.0, f.Face.U0);
		double b = Math.Min(len, f.Face.U1);
		if (a > 1.0 || b < len - 1.0)
		{
			wall = SplitWall(wall, a, b);
		}
		_doc.Regenerate();
		if (_req.AccentMode == AccentMode.WallType)
		{
			if (!(_doc.GetElement(new ElementId(_req.AccentWallType)) is WallType type))
			{
				throw new InvalidOperationException("pick the accent wall type first");
			}
			if (wall.WallType.Id != type.Id)
			{
				wall.ChangeTypeId(type.Id);
			}
			return;
		}
		if (!(_doc.GetElement(new ElementId(_req.AccentMaterial)) is Material material))
		{
			throw new InvalidOperationException("pick the accent material first");
		}
		IList<Reference> refs = HostObjectUtils.GetSideFaces(wall, (f.Face.Sign > 0) ? ShellLayerType.Exterior : ShellLayerType.Interior);
		int painted = 0;
		foreach (Reference r in refs)
		{
			if (wall.GetGeometryObjectFromReference(r) is Face face)
			{
				_doc.Paint(wall.Id, face, material.Id);
				painted++;
			}
		}
		if (painted == 0)
		{
			throw new InvalidOperationException("couldn't find that face of the wall to paint");
		}
	}

	// Corta el muro en [0,a] [a,b] [b,largo]. El original se queda con el tramo que tiene las puertas y ventanas
	// (si están en más de un tramo no se corta); los otros son copias. Devuelve el muro del tramo [a,b].
	private Wall SplitWall(Wall wall, double a, double b)
	{
		Line line = (Line)((LocationCurve)wall.Location).Curve;
		double len = line.Length;
		List<Tuple<double, double>> parts = new List<Tuple<double, double>>();
		if (a > 1.0)
		{
			parts.Add(Tuple.Create(0.0, a));
		}
		parts.Add(Tuple.Create(a, b));
		if (b < len - 1.0)
		{
			parts.Add(Tuple.Create(b, len));
		}
		// Puertas, ventanas y lo que ya cuelga del muro: ¿en qué tramo está cada uno?
		XYZ p0 = line.GetEndPoint(0);
		XYZ d = line.Direction;
		List<ElementId> hosted = wall.FindInserts(addRectOpenings: true, includeShadows: false, includeEmbeddedWalls: true, includeSharedEmbeddedInserts: true).ToList();
		hosted.AddRange(new FilteredElementCollector(_doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().Where((FamilyInstance fi) => fi.Host?.Id == wall.Id).Select((FamilyInstance fi) => fi.Id));
		HashSet<int> used = new HashSet<int>();
		foreach (ElementId id in hosted.Distinct())
		{
			Element e = _doc.GetElement(id);
			XYZ at = (e?.Location as LocationPoint)?.Point;
			BoundingBoxXYZ bb = e?.get_BoundingBox(null);
			if (at == null && bb != null)
			{
				at = (bb.Min + bb.Max) / 2.0;
			}
			if (at == null)
			{
				continue;
			}
			double u = (at - p0).DotProduct(d);
			int k = parts.FindIndex((Tuple<double, double> p) => u >= p.Item1 - 0.05 && u <= p.Item2 + 0.05);
			used.Add(Math.Max(0, k));
		}
		if (used.Count > 1)
		{
			throw new InvalidOperationException("the wall runs past this room and has doors or other pieces on more than one stretch, so it wasn't split. Split it by hand and run Room Studio again");
		}
		int keep = (used.Count == 1) ? used.First() : parts.FindIndex((Tuple<double, double> p) => p.Item1 == a);
		Wall[] walls = new Wall[parts.Count];
		walls[keep] = wall;
		for (int i = 0; i < parts.Count; i++)
		{
			if (i == keep)
			{
				continue;
			}
			ICollection<ElementId> copied = ElementTransformUtils.CopyElement(_doc, wall.Id, XYZ.Zero);
			Wall copy = copied.Select((ElementId id) => _doc.GetElement(id)).OfType<Wall>().First();
			// Las copias no se llevan las puertas ni las piezas: esas siguen en el original
			List<ElementId> extra = copied.Where((ElementId id) => id != copy.Id).ToList();
			if (extra.Count > 0)
			{
				_doc.Delete(extra);
			}
			walls[i] = copy;
		}
		for (int i = 0; i < parts.Count; i++)
		{
			((LocationCurve)walls[i].Location).Curve = Line.CreateBound(p0 + d * parts[i].Item1, p0 + d * parts[i].Item2);
		}
		_doc.Regenerate();
		// Lo planificado sobre el muro original pasa al tramo que le toca, con su posición en ese tramo
		for (int i = 0; i < parts.Count; i++)
		{
			Remap(wall, parts[i].Item1, parts[i].Item2, walls[i]);
		}
		return walls[parts.FindIndex((Tuple<double, double> p) => p.Item1 == a)];
	}

	private void Remap(Wall original, double from, double to, Wall target)
	{
		bool In(double u) => u >= from - 0.01 && u <= to + 0.01;
		foreach (FaceState f in _faces.Where((FaceState f) => f.Face.Wall.Id == original.Id && In((f.Face.U0 + f.Face.U1) / 2.0)).ToList())
		{
			f.Face.Wall = target;
			f.Face.U0 = Math.Max(0.0, f.Face.U0 - from);
			f.Face.U1 = Math.Min(to - from, f.Face.U1 - from);
		}
		foreach (ArtPlan p in _artPlans.Where((ArtPlan p) => p.Piece.Wall.Id == original.Id && In(p.Piece.Center)).ToList())
		{
			p.Piece.Wall = target;
			p.Piece.Center -= from;
		}
		foreach (TvSpot spot in _tvPlans.Values.Select((TvTarget t) => t.Chosen).Where((TvSpot x) => x != null && x.Wall.Id == original.Id && In(x.Center)).ToList())
		{
			spot.Wall = target;
			spot.Center -= from;
			spot.U0 = Math.Max(0.0, spot.U0 - from);
			spot.U1 = Math.Min(to - from, spot.U1 - from);
		}
	}

	private static FamilyInstance Fail(string reason)
	{
		throw new InvalidOperationException(reason ?? "could not be placed");
	}
}
