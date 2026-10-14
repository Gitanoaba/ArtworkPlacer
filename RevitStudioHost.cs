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

		public bool On;

		public bool Suggested;
	}

	private class ArtPlan
	{
		public int Key;

		public FaceState Face;

		public WallPlacer.Piece Piece;
	}

	private const double MinFreeFace = 2.0;

	private static readonly string[] NoArtWords = { "toilet", "wc", "restroom", "rest room", "bath", "shower", "lavatory", "storage", "store", "server", "electric", "mechanical", "shaft", "stair", "elevator", "lift", "janitor", "cleaning", "phone", "booth", "archive", "שירותים", "מחסן", "מדרגות", "מעלית" };

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

	public List<PlanLevel> Levels { get; }

	public long StartLevel { get; }

	public List<TypeItem> ArtTypes { get; private set; }

	public List<TypeItem> TvTypes { get; private set; }

	public RevitStudioHost(UIDocument uidoc, string revitVersion)
	{
		_uidoc = uidoc;
		_doc = uidoc.Document;
		_units = _doc.GetUnits();
		_thumbs = new TypeThumbnails(_doc, revitVersion);
		_rooms = (from Room r in new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
			where r.Location != null && r.Area > 0.0
			select r).ToDictionary((Room r) => r.Id.Value);
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
		List<PlanLevel> result = new List<PlanLevel>();
		foreach (Level level in levels)
		{
			List<Room> rooms = _rooms.Values.Where((Room r) => r.LevelId == level.Id).ToList();
			List<Wall> onLevel = walls.Where((Wall w) => w.LevelId == level.Id).ToList();
			if (rooms.Count == 0 && onLevel.Count == 0)
			{
				continue;
			}
			PlanLevel plan = new PlanLevel
			{
				Id = level.Id.Value,
				Name = level.Name,
				Rooms = rooms.Select(ToPlan).Where((PlanRoom r) => r.Loops.Count > 0).ToList(),
				Walls = onLevel.Select((Wall w) => new PlanWall
				{
					Path = ((LocationCurve)w.Location).Curve.Tessellate().Select((XYZ p) => new Point(p.X, p.Y)).ToArray(),
					Thickness = w.Width
				}).ToList()
			};
			result.Add(plan);
			AddFaces(level, rooms, onLevel.Where(Usable).ToList());
		}
		return result;
	}

	private void AddFaces(Level level, List<Room> rooms, List<Wall> walls)
	{
		List<FaceState> roomFaces = (from r in rooms
			from f in RoomScope.Faces(r, _boundary)
			where Usable(f.Wall)
			select NewFace(level.Id.Value, r.Id.Value, f)).ToList();
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
					_faces.Add(NewFace(level.Id.Value, 0L, new RoomFace
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

	private FaceState NewFace(long levelId, long roomId, RoomFace face)
	{
		Tuple<long, int> key = Tuple.Create(face.Wall.WallType.Id.Value, face.Sign);
		if (!_drywall.TryGetValue(key, out var dry))
		{
			dry = (_drywall[key] = RoomScope.IsDrywall(face.Wall, face.Sign));
		}
		return new FaceState
		{
			Key = ++_nextKey,
			LevelId = levelId,
			RoomId = roomId,
			Face = face,
			Drywall = dry
		};
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
		List<TypeItem> art = all.Where((TypeItem t) => PlaceArtworkCommand.LooksLikeArtwork(t.Label)).ToList();
		List<TypeItem> tv = all.Where((TypeItem t) => PlaceTvCommand.LooksLikeTv(t.Label)).ToList();
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
		return f.Drywall || _req == null || !_req.DrywallOnly;
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
			Activate(_tvSymbols.Concat(_artSymbols));
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
			_started = true;
		}
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
			where f.On && !IsTv(f.RoomId)
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
		return spot.Symbol != null && f != null && Allowed(f);
	}

	private void PlanTv(long roomId)
	{
		_tvPlans.Remove(roomId);
		_roomProblems.Remove(roomId);
		FoundRoom found = RoomScope.Found(_rooms[roomId], _boundary);
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
		foreach (FaceState f in _faces.Where((FaceState f) => f.RoomId == roomId))
		{
			f.On = chosen != null && f.Face.Wall.Id == chosen.Wall.Id && f.Face.Sign == chosen.Sign;
			f.Suggested = false;
		}
	}

	private void ReplanWall(Wall wall)
	{
		_artPlans.RemoveAll((ArtPlan a) => a.Piece.Wall.Id == wall.Id);
		List<FaceState> all = _faces.Where((FaceState f) => f.Face.Wall.Id == wall.Id && !IsTv(f.RoomId)).ToList();
		all.ForEach((FaceState f) => _faceProblems.Remove(f.Key));
		List<FaceState> on = all.Where((FaceState f) => f.On).ToList();
		if (on.Count == 0)
		{
			return;
		}
		if (_artSymbols.Count == 0)
		{
			on.ForEach((FaceState f) => _faceProblems[f.Key] = "Tick at least one artwork type");
			return;
		}
		FaceRanges ranges = new FaceRanges();
		on.ForEach((FaceState f) => ranges.Add(wall.Id, f.Face.Sign, f.Face.U0, f.Face.U1));
		_wallPlacer.Ranges = ranges;
		List<string> notes = new List<string>();
		foreach (WallPlacer.Piece p in _wallPlacer.Plan(wall, ranges.Signs(wall.Id), notes))
		{
			FaceState face = on.FirstOrDefault((FaceState f) => f.Face.Sign == p.Sign && p.Center >= f.Face.U0 - 0.05 && p.Center <= f.Face.U1 + 0.05) ?? on.First((FaceState f) => f.Face.Sign == p.Sign);
			_artPlans.Add(new ArtPlan { Key = ++_nextKey, Face = face, Piece = p });
		}
		string why = Sentence(notes.FirstOrDefault((string n) => n.StartsWith("free")) ?? notes.FirstOrDefault() ?? "No free space here for the artwork");
		foreach (FaceState f in on.Where((FaceState f) => !_artPlans.Any((ArtPlan a) => a.Face == f)))
		{
			_faceProblems[f.Key] = why;
		}
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
				if (use == RoomUse.Tv)
				{
					_uses[id] = RoomUse.Tv;
					PlanTv(id);
					continue;
				}
				_uses.Remove(id);
				_tvPlans.Remove(id);
				_roomProblems.Remove(id);
				faces.ForEach(delegate(FaceState f)
				{
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
		return State((empty.Count > 0) ? ("No drywall walls in " + string.Join(", ", empty.Distinct()) + ".") : null);
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
			return State($"This wall ({f.Face.Wall.WallType.Name}) isn't drywall, so nothing can go on it.");
		}
		if (IsTv(f.RoomId))
		{
			if (!_tvPlans.TryGetValue(f.RoomId, out var target))
			{
				return State("A TV can't be planned in this room.");
			}
			int k = target.Spots.FindIndex((TvSpot s) => s.Wall.Id == f.Face.Wall.Id && s.Sign == f.Face.Sign);
			if (k < 0 || target.Spots[k].Symbol == null)
			{
				string why = (k >= 0) ? target.Spots[k].Problem : null;
				return State("A TV doesn't fit on this wall" + ((why != null) ? (": " + why) : "."));
			}
			target.Current = k;
			_roomProblems.Remove(f.RoomId);
			SyncTvFaces(f.RoomId);
			return State(null);
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
		if (_artSymbols.Count == 0)
		{
			return State("Tick at least one artwork type to get suggestions.");
		}
		List<FaceState> picks = SuggestedFaces();
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
		return State((count == 0) ? "No more drywall walls to suggest." : $"Suggested artwork on {count} wall(s). Click a blue wall to take it off.");
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
			double m2 = UnitUtils.ConvertFromInternalUnits(r.Area, UnitTypeId.SquareMeters);
			bool open = Has(name, OpenWords);
			if (Has(name, NoArtWords) || (m2 < 8.0 && !open) || room.Any((FaceState f) => f.On))
			{
				continue;
			}
			int take = open ? 3 : ((m2 >= 40.0) ? 2 : 1);
			double min = UnitUtils.ConvertToInternalUnits(open ? 3.0 : 1.5, UnitTypeId.Meters);
			picks.AddRange(room.Where((FaceState f) => f.Face.U1 - f.Face.U0 >= min).OrderByDescending((FaceState f) => f.Face.U1 - f.Face.U0).Take(take));
		}
		double freeMin = UnitUtils.ConvertToInternalUnits(3.0, UnitTypeId.Meters);
		picks.AddRange(_faces.Where((FaceState f) => f.RoomId == 0 && !f.On && Allowed(f) && f.Face.U1 - f.Face.U0 >= freeMin));
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
			Title = (f.RoomId != 0) ? $"{side} wall of {RoomName(_rooms[f.RoomId])}, {length}" : $"Wall with no room, {length}",
			WallType = f.Face.Wall.WallType.Name,
			Allowed = Allowed(f),
			On = f.On,
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
		using (Transaction t = new Transaction(_doc, "Room Studio: place TVs and artwork"))
		{
			PlaceArtworkCommand.SwallowWarnings(t);
			t.Start();
			Activate(_tvSymbols.Concat(_artSymbols));
			foreach (KeyValuePair<long, TvTarget> p in _tvPlans.Where((KeyValuePair<long, TvTarget> p) => p.Value.Chosen != null))
			{
				Try(RoomName(_rooms[p.Key]), () => _tvPlacer.Place(p.Value.Chosen, out var reason) ?? Fail(reason), ref tvs);
			}
			foreach (ArtPlan a in _artPlans.Where((ArtPlan a) => !skipped.Contains(a.Key)))
			{
				string label = (a.Face.RoomId != 0) ? RoomName(_rooms[a.Face.RoomId]) : PlaceArtworkCommand.WallLabel(a.Piece.Wall);
				Try(label, () => _wallPlacer.PlacePiece(a.Piece, out var reason) ?? Fail(reason), ref arts);
			}
			t.Commit();
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
