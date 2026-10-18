using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace ArtworkPlacer;

internal class WallPlacer : WallFaces
{
	public class Spot
	{
		public int Sign;

		public XYZ OnFace;

		public XYZ Normal;

		public XYZ Along;

		public bool Blocked;
	}

	public class Piece
	{
		public Wall Wall;

		public int Sign;

		public double Center;

		public double MaxWidth;

		public FamilySymbol Symbol;

		public double Width;

		public XYZ OnFace;

		public XYZ Normal;

		public XYZ Along;
	}

	private class Slot
	{
		public double Center;

		public double MaxWidth;
	}

	private const string Marker = "ArtworkPlacer";

	private readonly double? _height;

	private readonly HeightAlign _align;

	private readonly double _gap;

	private readonly int _maxPerFace;

	private readonly SideMode _sideMode;

	private readonly Func<double, string> _fmt;

	private int _typeIndex;

	public FaceRanges Ranges;

	private double RefHeight
	{
		get
		{
			double num = UnitUtils.ConvertToInternalUnits(40.0, UnitTypeId.Centimeters);
			if (!_height.HasValue)
			{
				return UnitUtils.ConvertToInternalUnits(150.0, UnitTypeId.Centimeters);
			}
			if (_align == HeightAlign.Bottom)
			{
				return _height.Value + num;
			}
			if (_align == HeightAlign.Top)
			{
				return _height.Value - num;
			}
			return _height.Value;
		}
	}

	public WallPlacer(Document doc, List<FamilySymbol> symbols, double? height, HeightAlign align, double clearance, double gap, int maxPerFace, SideMode sideMode, Func<double, string> fmt)
		: base(doc, symbols, clearance, "ArtworkPlacer")
	{
		_height = height;
		_align = align;
		_gap = gap;
		_maxPerFace = Math.Max(1, maxPerFace);
		_sideMode = sideMode;
		_fmt = fmt;
	}

	private double Height(FamilySymbol sym)
	{
		if (!_height.HasValue)
		{
			return WallFaces.DefaultElevation(sym);
		}
		return RefHeight;
	}

	public List<int> InitialSides(Wall wall, out string error)
	{
		WallGeom wallGeom = Analyze(wall, RefHeight, out error);
		if (wallGeom != null)
		{
			return (from s in ChooseSides(wallGeom)
				select s.Sign).ToList();
		}
		return null;
	}

	public bool HasBothSides(Wall wall)
	{
		WallGeom wallGeom = Analyze(wall, RefHeight, out var _);
		if (wallGeom == null)
		{
			return false;
		}
		return wallGeom.Sides.Count == 2;
	}

	public List<Spot> Spots(Wall wall)
	{
		List<Spot> list = new List<Spot>();
		WallGeom wallGeom = Analyze(wall, RefHeight, out var _);
		if (wallGeom == null)
		{
			return list;
		}
		foreach (Side value in wallGeom.Sides.Values)
		{
			List<Slot> list2 = (HasExisting(wall, wallGeom, value) ? new List<Slot>() : Slots(wall, wallGeom, value));
			if (list2.Count == 0)
			{
				Tuple<double, double> range = RangesOf(wall, wallGeom, value.Sign)[0];
				list2.Add(new Slot
				{
					Center = (range.Item1 + range.Item2) / 2.0,
					MaxWidth = 0.0
				});
			}
			foreach (Slot item in list2)
			{
				XYZ xYZ = _p0 + _d * item.Center + _n * value.V;
				list.Add(new Spot
				{
					Sign = value.Sign,
					OnFace = new XYZ(xYZ.X, xYZ.Y, wallGeom.RefZ),
					Normal = _n * value.Sign,
					Along = _d,
					Blocked = (item.MaxWidth <= 0.0 || NothingFits(item.MaxWidth))
				});
			}
		}
		return list;
	}

	public List<ElementId> PlaceOnWall(Wall wall, List<int> signs, List<string> skipped)
	{
		List<ElementId> list = new List<ElementId>();
		string text = PlaceArtworkCommand.WallLabel(wall);
		WallGeom wallGeom = Analyze(wall, RefHeight, out var error);
		if (wallGeom == null)
		{
			skipped.Add(text + ": " + error);
			return list;
		}
		foreach (int sign in signs)
		{
			if (!wallGeom.Sides.TryGetValue(sign, out var value))
			{
				continue;
			}
			if (HasExisting(wall, wallGeom, value))
			{
				skipped.Add(text + ", " + value.Name + ": already has artwork");
				continue;
			}
			List<Slot> list2 = Slots(wall, wallGeom, value);
			if (list2.Count == 0)
			{
				skipped.Add(text + ", " + value.Name + ": no free wall segment");
				continue;
			}
			foreach (Slot item in list2)
			{
				FamilyInstance familyInstance = PlaceAt(wall, wallGeom.Level, value, item.Center, item.MaxWidth, out var reason);
				if (familyInstance == null)
				{
					skipped.Add(text + ", " + value.Name + ": " + reason);
				}
				else
				{
					list.Add(familyInstance.Id);
				}
			}
		}
		return list;
	}

	public List<Piece> Plan(Wall wall, List<int> signs, List<string> skipped)
	{
		List<Piece> list = new List<Piece>();
		WallGeom g = Analyze(wall, RefHeight, out var error);
		if (g == null)
		{
			skipped.Add(error);
			return list;
		}
		foreach (int sign in signs)
		{
			if (!g.Sides.TryGetValue(sign, out var side))
			{
				continue;
			}
			if (HasExisting(wall, g, side))
			{
				skipped.Add("already has artwork");
				continue;
			}
			List<Slot> slots = Slots(wall, g, side);
			if (slots.Count == 0)
			{
				skipped.Add("no free wall space");
				continue;
			}
			WidestType(wall, g, side, slots[0].Center);
			foreach (Slot slot in slots)
			{
				FamilySymbol sym = NextFitting(slot.MaxWidth);
				if (sym == null)
				{
					skipped.Add("free wall space (" + _fmt(slot.MaxWidth) + ") is narrower than the artwork");
					continue;
				}
				list.Add(new Piece
				{
					Wall = wall,
					Sign = sign,
					Center = slot.Center,
					MaxWidth = slot.MaxWidth,
					Symbol = sym,
					Width = _widths[sym.Id],
					OnFace = PointOnFace(g.Level, side, slot.Center, Height(sym)),
					Normal = _n * sign,
					Along = _d
				});
			}
		}
		return list;
	}

	// Ancho de la pieza más angosta ya medida (-1 si no hay ninguna medida)
	public double MinWidth => _symbols.Select((FamilySymbol s) => _widths.TryGetValue(s.Id, out var w) ? w : -1.0).Where((double w) => w > 0.0).DefaultIfEmpty(-1.0).Min();

	public FamilyInstance PlacePiece(Piece piece, out string reason)
	{
		WallGeom g = Analyze(piece.Wall, RefHeight, out var error);
		if (g == null || !g.Sides.TryGetValue(piece.Sign, out var side))
		{
			reason = error ?? "wall face not found";
			return null;
		}
		return PlaceAt(piece.Wall, g.Level, side, piece.Center, piece.MaxWidth, out reason, piece.Symbol);
	}

	private FamilySymbol NextFitting(double maxWidth)
	{
		for (int i = 0; i < _symbols.Count; i++)
		{
			FamilySymbol sym = _symbols[(_typeIndex + i) % _symbols.Count];
			if (_widths.TryGetValue(sym.Id, out var w) && w > 0.0 && w <= maxWidth + 0.01)
			{
				_typeIndex = (_typeIndex + i + 1) % _symbols.Count;
				return sym;
			}
		}
		return null;
	}

	private List<Tuple<double, double>> RangesOf(Wall wall, WallGeom g, int sign)
	{
		return Ranges?.Get(wall.Id, sign, g.Length) ?? new List<Tuple<double, double>> { Tuple.Create(0.0, g.Length) };
	}

	private bool HasExisting(Wall wall, WallGeom g, Side side)
	{
		List<Tuple<double, double>> ranges = RangesOf(wall, g, side.Sign);
		return side.Existing.Any((Tuple<double, double> e) => ranges.Any((Tuple<double, double> r) => e.Item1 < r.Item2 && e.Item2 > r.Item1));
	}

	private List<Slot> Slots(Wall wall, WallGeom g, Side side)
	{
		return (from r in RangesOf(wall, g, side.Sign)
			from sl in SlotsIn(wall, g, side, r.Item1, r.Item2)
			orderby sl.Center
			select sl).ToList();
	}

	private List<Slot> SlotsIn(Wall wall, WallGeom g, Side side, double u0, double u1)
	{
		List<Slot> list = new List<Slot>();
		List<Tuple<double, double>> list2 = FreeSegments(u0, u1, side.Blocked);
		if (list2.Count == 0)
		{
			return list;
		}
		Tuple<double, double> tuple = list2[0];
		Slot slot = new Slot
		{
			Center = (tuple.Item1 + tuple.Item2) / 2.0,
			MaxWidth = tuple.Item2 - tuple.Item1
		};
		if (_maxPerFace == 1)
		{
			return new List<Slot> { slot };
		}
		double num = WidestType(wall, g, side, slot.Center);
		if (num <= 0.0)
		{
			return new List<Slot> { slot };
		}
		int num2 = _maxPerFace;
		foreach (Tuple<double, double> item in list2)
		{
			double num3 = item.Item2 - item.Item1;
			int num4 = Math.Min(num2, (int)Math.Floor((num3 + 0.01) / (num + _gap)));
			if (num4 < 1 && num <= num3 + 0.01)
			{
				num4 = 1;
			}
			if (num4 >= 1)
			{
				double num5 = num3 / (double)num4;
				for (int i = 0; i < num4; i++)
				{
					list.Add(new Slot
					{
						Center = item.Item1 + num5 * ((double)i + 0.5),
						MaxWidth = ((num4 == 1) ? num3 : (num5 - _gap))
					});
				}
				num2 -= num4;
				if (num2 == 0)
				{
					break;
				}
			}
		}
		if (list.Count == 0)
		{
			list.Add(slot);
		}
		return list.OrderBy((Slot sl) => sl.Center).ToList();
	}

	private double WidestType(Wall wall, WallGeom g, Side side, double center)
	{
		foreach (FamilySymbol symbol in _symbols)
		{
			if (!_widths.ContainsKey(symbol.Id))
			{
				_widths[symbol.Id] = Measure(symbol, wall, g, side, center, Height(symbol));
			}
		}
		return _symbols.Select((FamilySymbol sy) => _widths[sy.Id]).DefaultIfEmpty(-1.0).Max();
	}

	private bool NothingFits(double maxWidth)
	{
		return _symbols.All((FamilySymbol sy) => _widths.TryGetValue(sy.Id, out var value) && (value <= 0.0 || value > maxWidth + 0.01));
	}

	private IEnumerable<Side> ChooseSides(WallGeom g)
	{
		switch (_sideMode)
		{
		case SideMode.Exterior:
			return from kv in g.Sides
				where kv.Key > 0
				select kv.Value;
		case SideMode.Interior:
			return from kv in g.Sides
				where kv.Key < 0
				select kv.Value;
		case SideMode.Both:
			return g.Sides.Values;
		default:
		{
			Side value = RoomSide(g);
			if (value == null)
			{
				g.Sides.TryGetValue(-1, out value);
			}
			if (value == null)
			{
				value = g.Sides.Values.First();
			}
			return new Side[1] { value };
		}
		}
	}

	private FamilyInstance PlaceAt(Wall wall, Level level, Side side, double center, double maxWidth, out string reason, FamilySymbol preferred = null)
	{
		XYZ faceN = _n * side.Sign;
		double num = 0.0;
		List<FamilySymbol> order = Enumerable.Range(0, _symbols.Count).Select((int k) => _symbols[(_typeIndex + k) % _symbols.Count]).ToList();
		if (preferred != null)
		{
			order.Remove(preferred);
			order.Insert(0, preferred);
		}
		for (int i = 0; i < order.Count; i++)
		{
			FamilySymbol familySymbol = order[i];
			if (_widths.TryGetValue(familySymbol.Id, out var value) && value > maxWidth + 0.01)
			{
				num = Math.Max(num, value);
				continue;
			}
			FamilyInstance familyInstance = Create(familySymbol, wall, level, side, PointOnFace(level, side, center, Height(familySymbol)), faceN);
			if (familyInstance == null)
			{
				continue;
			}
			_doc.Regenerate();
			Box box = PieceBox(familyInstance);
			if (box == null)
			{
				_doc.Delete(familyInstance.Id);
				continue;
			}
			double num2 = box.U1 - box.U0;
			_widths[familySymbol.Id] = num2;
			if (num2 > maxWidth + 0.01)
			{
				num = Math.Max(num, num2);
				_doc.Delete(familyInstance.Id);
				continue;
			}
			XYZ xYZ = _d * (center - (box.U0 + box.U1) / 2.0);
			if (_height.HasValue)
			{
				xYZ += XYZ.BasisZ * ArtGeometry.AlignDelta(_align, level.ProjectElevation + _height.Value, box.Z0, box.Z1);
			}
			if (!xYZ.IsZeroLength())
			{
				ElementTransformUtils.MoveElement(_doc, familyInstance.Id, xYZ);
			}
			Mark(familyInstance);
			if (preferred == null)
			{
				_typeIndex = (_symbols.IndexOf(familySymbol) + 1) % _symbols.Count;
			}
			reason = null;
			return familyInstance;
		}
		reason = ((num > 0.0) ? ("free space (" + _fmt(maxWidth) + ") is narrower than the artwork (" + _fmt(num) + ")") : "none of the types could be placed on this face");
		return null;
	}
}
