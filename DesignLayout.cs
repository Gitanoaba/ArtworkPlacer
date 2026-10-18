using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows;

namespace ArtworkPlacer;

// Un room del layout de Qbiq (el JSON del diseño), en centímetros y en las coordenadas del JSON
public class LayoutRoom
{
	public string Type;

	public string Subtype;

	public Point[] Polygon;

	// Personas que entran (head_count), si el JSON lo dice
	public int? People;

	// Muebles del diseño: nombre y contorno, en cm del JSON
	public List<Tuple<string, Point[]>> Furniture = new List<Tuple<string, Point[]>>();

	// Áreas de giro de las puertas del room
	public List<Point[]> DoorSwings = new List<Point[]>();

	public string Name
	{
		get
		{
			// El subtipo solo si dice algo (no medidas como "152x76" ni lo mismo que el tipo)
			bool useful = !string.IsNullOrWhiteSpace(Subtype) && !Subtype.Any(char.IsDigit) && Type.IndexOf(Subtype, StringComparison.OrdinalIgnoreCase) < 0;
			return useful ? (Type + " (" + Subtype + ")") : Type;
		}
	}
}

// El layout del diseño: rooms y ejes de muros, leído del JSON que exporta la plataforma de Qbiq
public class DesignLayout
{
	public string File;

	public List<LayoutRoom> Rooms = new List<LayoutRoom>();

	public List<Tuple<Point, Point>> Walls = new List<Tuple<Point, Point>>();

	// El core del edificio (ascensores, escaleras, baños): no viene como room
	public List<Point[]> Cores = new List<Point[]>();

	// Puerta principal (sale del hall de ascensores) y su dirección según el JSON
	public Point? Entrance;

	public Vector EntranceDir;

	// Ventanas (ejes), columnas y la circulación (pasillos)
	public List<Tuple<Point, Point>> Windows = new List<Tuple<Point, Point>>();

	public List<Point[]> Columns = new List<Point[]>();

	public List<Point[]> Circulation = new List<Point[]>();

	private static readonly Regex Num = new Regex("-?\\d+(?:\\.\\d+)?(?:[eE][-+]?\\d+)?");

	public static DesignLayout Read(string path)
	{
		JavaScriptSerializer js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 512 };
		Dictionary<string, object> root = js.DeserializeObject(System.IO.File.ReadAllText(path)) as Dictionary<string, object>;
		if (root == null || !(root.TryGetValue("rooms", out var roomsObj) && roomsObj is IEnumerable rooms))
		{
			throw new InvalidDataException("this isn't a Qbiq design JSON (no rooms)");
		}
		DesignLayout layout = new DesignLayout { File = path };
		foreach (Dictionary<string, object> r in rooms.OfType<Dictionary<string, object>>())
		{
			// El JSON descargado usa space_type/subtype; el *_design_data.json de la carpeta del Design ID, room_type/room_subtype
			if (Get(r, "is_filler") is bool filler && filler)
			{
				continue;
			}
			Point[] poly = Polygon(Get(r, "geometry") as string);
			if (poly == null || poly.Length < 3)
			{
				continue;
			}
			LayoutRoom room = new LayoutRoom
			{
				Type = (Get(r, "space_type") as string) ?? (Get(r, "room_type") as string) ?? "Room",
				Subtype = (Get(r, "subtype") as string) ?? (Get(r, "room_subtype") as string),
				Polygon = poly
			};
			try
			{
				int people = Convert.ToInt32(Get(r, "head_count") ?? 0, CultureInfo.InvariantCulture);
				room.People = (people > 0) ? new int?(people) : null;
			}
			catch
			{
			}
			// Cada mueble: su caja local (geom_box) llevada a la planta con su matriz
			foreach (Dictionary<string, object> c in Dicts(Get(r, "stencil_geometry_components")))
			{
				if ((Get(c, "show") is bool show && !show) || (Get(c, "is_remove") is bool removed && removed))
				{
					continue;
				}
				double[] m = Matrix(Get(c, "matrix"));
				Point[] box = Polygon(Get(c, "geom_box") as string);
				if (m != null && box != null && box.Length >= 3)
				{
					room.Furniture.Add(Tuple.Create((Get(c, "name") as string) ?? "", box.Select((Point p) => new Point(m[0] * p.X + m[1] * p.Y + m[2], m[3] * p.X + m[4] * p.Y + m[5])).ToArray()));
				}
			}
			foreach (Dictionary<string, object> d in Dicts(Get(r, "doors")))
			{
				Point[] swing = Polygon(Get(d, "clearance_geometry") as string);
				if (swing != null && swing.Length >= 3)
				{
					room.DoorSwings.Add(swing);
				}
			}
			layout.Rooms.Add(room);
		}
		if (Get(root, "nfp_data") is Dictionary<string, object> nfp)
		{
			if (Get(nfp, "cores") is IEnumerable cores)
			{
				foreach (string wkt in cores.OfType<string>())
				{
					Point[] core = Polygon(wkt);
					if (core != null && core.Length >= 3)
					{
						layout.Cores.Add(core);
					}
				}
			}
			if (Get(nfp, "doors") is IEnumerable doors)
			{
				Dictionary<string, object> main = doors.OfType<Dictionary<string, object>>().FirstOrDefault((Dictionary<string, object> d) => (Get(d, "type") as string) == "main");
				Point[] at = (main == null) ? null : Polygon(Get(main, "pt") as string, "(");
				Point? dir = (main == null) ? null : Pt(Get(main, "vec"));
				if (at != null && at.Length == 1 && dir.HasValue)
				{
					layout.Entrance = at[0];
					layout.EntranceDir = new Vector(dir.Value.X, dir.Value.Y);
				}
			}
			if (!layout.Entrance.HasValue)
			{
				Point[] at = Polygon(Get(nfp, "main_entrance") as string, "(");
				if (at != null && at.Length == 1)
				{
					layout.Entrance = at[0];
				}
			}
			foreach (Dictionary<string, object> w in Dicts(Get(nfp, "windows")))
			{
				Point? a = Pt(Get(w, "corner1"));
				Point? b = Pt(Get(w, "corner2"));
				if (a.HasValue && b.HasValue && (b.Value - a.Value).Length > 1.0)
				{
					layout.Windows.Add(Axis(a.Value, b.Value));
				}
			}
			layout.Columns.AddRange(Polygons(Get(nfp, "columns") as string));
			foreach (string key in new[] { "walls", "interior_walls" })
			{
				if (!(Get(nfp, key) is IEnumerable list))
				{
					continue;
				}
				foreach (Dictionary<string, object> w in list.OfType<Dictionary<string, object>>())
				{
					Point? a = Pt(Get(w, "corner1"));
					Point? b = Pt(Get(w, "corner2"));
					if (a.HasValue && b.HasValue)
					{
						layout.Walls.Add(Axis(a.Value, b.Value));
					}
				}
			}
		}
		layout.Circulation.AddRange(Polygons(Get(root, "circulation") as string));
		// Muros del *_design_data.json: ejes x0,y0 → x1,y1
		if (Get(root, "walls") is IEnumerable axes)
		{
			foreach (Dictionary<string, object> w in axes.OfType<Dictionary<string, object>>())
			{
				Point? a = Pt(new[] { Get(w, "x0"), Get(w, "y0") });
				Point? b = Pt(new[] { Get(w, "x1"), Get(w, "y1") });
				if (a.HasValue && b.HasValue)
				{
					layout.Walls.Add(Tuple.Create(a.Value, b.Value));
				}
			}
		}
		if (layout.Rooms.Count == 0)
		{
			throw new InvalidDataException("the JSON has no rooms with a shape");
		}
		return layout;
	}

	private static IEnumerable<Dictionary<string, object>> Dicts(object o)
	{
		return (o is IEnumerable list && !(o is string)) ? list.OfType<Dictionary<string, object>>() : Enumerable.Empty<Dictionary<string, object>>();
	}

	// [[a, b, c], [d, e, f], [0, 0, 1]] → a..f
	private static double[] Matrix(object o)
	{
		List<double> v = new List<double>();
		if (o is IEnumerable rows && !(o is string))
		{
			foreach (object row in rows)
			{
				if (row is IEnumerable cells && !(row is string))
				{
					v.AddRange(cells.Cast<object>().Select((object x) => Convert.ToDouble(x, CultureInfo.InvariantCulture)));
				}
			}
		}
		return (v.Count >= 6) ? v.ToArray() : null;
	}

	// Anillo exterior de cada polígono de un POLYGON o MULTIPOLYGON
	private static List<Point[]> Polygons(string wkt)
	{
		List<Point[]> result = new List<Point[]>();
		if (string.IsNullOrEmpty(wkt))
		{
			return result;
		}
		foreach (Match ring in Regex.Matches(wkt, @"\(\(([^()]*)\)"))
		{
			List<double> n = (from Match m in Num.Matches(ring.Groups[1].Value)
				select double.Parse(m.Value, CultureInfo.InvariantCulture)).ToList();
			List<Point> pts = new List<Point>();
			for (int i = 0; i + 1 < n.Count; i += 2)
			{
				pts.Add(new Point(n[i], n[i + 1]));
			}
			if (pts.Count > 2 && (pts[0] - pts[pts.Count - 1]).Length < 0.01)
			{
				pts.RemoveAt(pts.Count - 1);
			}
			if (pts.Count >= 3)
			{
				result.Add(pts.ToArray());
			}
		}
		return result;
	}

	private static object Get(Dictionary<string, object> d, string key)
	{
		return d.TryGetValue(key, out var v) ? v : null;
	}

	private static Point? Pt(object o)
	{
		object[] a = (o as IEnumerable)?.Cast<object>().ToArray();
		if (a == null || a.Length < 2 || a[0] == null || a[1] == null)
		{
			return null;
		}
		return new Point(Convert.ToDouble(a[0], CultureInfo.InvariantCulture), Convert.ToDouble(a[1], CultureInfo.InvariantCulture));
	}

	// Un muro del JSON es un rectángulo dado por dos esquinas opuestas: su eje va por el lado largo, al centro
	private static Tuple<Point, Point> Axis(Point a, Point b)
	{
		double cx = (a.X + b.X) / 2.0;
		double cy = (a.Y + b.Y) / 2.0;
		return (Math.Abs(b.X - a.X) >= Math.Abs(b.Y - a.Y))
			? Tuple.Create(new Point(Math.Min(a.X, b.X), cy), new Point(Math.Max(a.X, b.X), cy))
			: Tuple.Create(new Point(cx, Math.Min(a.Y, b.Y)), new Point(cx, Math.Max(a.Y, b.Y)));
	}

	// "POLYGON ((x y, x y, …))": el anillo exterior ("POINT (x y)" con open = "(")
	private static Point[] Polygon(string wkt, string openWith = "((")
	{
		if (string.IsNullOrEmpty(wkt))
		{
			return null;
		}
		int open = wkt.IndexOf(openWith, StringComparison.Ordinal);
		int close = (open < 0) ? -1 : wkt.IndexOf(')', open);
		if (open < 0 || close < 0)
		{
			return null;
		}
		List<double> n = (from Match m in Num.Matches(wkt.Substring(open, close - open))
			select double.Parse(m.Value, CultureInfo.InvariantCulture)).ToList();
		List<Point> pts = new List<Point>();
		for (int i = 0; i + 1 < n.Count; i += 2)
		{
			pts.Add(new Point(n[i], n[i + 1]));
		}
		if (pts.Count > 2 && (pts[0] - pts[pts.Count - 1]).Length < 0.01)
		{
			pts.RemoveAt(pts.Count - 1);
		}
		return pts.ToArray();
	}
}

// Cómo pasar del JSON (cm) al modelo (pies): rotación/espejo y desplazamiento que mejor calzan los muros
public class LayoutFit
{
	private const double CmToFt = 1.0 / 30.48;

	public int Rotation;

	public bool Mirror;

	public Vector Offset;

	public double Score;

	// Una dirección del JSON en el modelo (sin desplazamiento)
	public Vector ApplyDir(Vector v)
	{
		Vector d = Apply(new Point(v.X * 100.0, v.Y * 100.0)) - Apply(new Point(0.0, 0.0));
		d.Normalize();
		return d;
	}

	public Point Apply(Point p)
	{
		double x = p.X * CmToFt;
		double y = (Mirror ? (0.0 - p.Y) : p.Y) * CmToFt;
		switch (Rotation)
		{
		case 90:
			return new Point(0.0 - y, x) + Offset;
		case 180:
			return new Point(0.0 - x, 0.0 - y) + Offset;
		case 270:
			return new Point(y, 0.0 - x) + Offset;
		default:
			return new Point(x, y) + Offset;
		}
	}

	// Para cada una de las 8 orientaciones: candidatos de desplazamiento emparejando bordes largos del JSON con muros
	// de Revit de igual dirección y largo parecido; el que más puntos deja sobre muros gana y se afina.
	// No centra los dos conjuntos, así que muros sueltos lejos del edificio no lo desvían.
	public static LayoutFit Find(DesignLayout layout, List<Tuple<Point, Point>> revitWalls)
	{
		List<Tuple<Point, Point>> walls = revitWalls.Where((Tuple<Point, Point> w) => (w.Item2 - w.Item1).Length >= 0.5).ToList();
		if (walls.Count == 0)
		{
			return null;
		}
		List<Tuple<Point, Point>> jsonSegs = JsonSegments(layout);
		List<Point> samples = Samples(jsonSegs);
		if (samples.Count == 0)
		{
			return null;
		}
		SegGrid grid = new SegGrid(walls, 3.0);
		List<Point> probe = samples.Where((Point q, int i) => i % Math.Max(1, samples.Count / 300) == 0).ToList();
		List<Tuple<Point, Point>> longest = jsonSegs.OrderByDescending((Tuple<Point, Point> j) => (j.Item2 - j.Item1).Length).Take(20).ToList();
		LayoutFit best = null;
		foreach (int rot in new[] { 0, 90, 180, 270 })
		{
			foreach (bool mirror in new[] { false, true })
			{
				LayoutFit fit = new LayoutFit { Rotation = rot, Mirror = mirror };
				List<Vector> candidates = new List<Vector>();
				foreach (Tuple<Point, Point> j in longest)
				{
					Point ja = fit.Apply(j.Item1);
					Point jb = fit.Apply(j.Item2);
					Vector dj = jb - ja;
					double lj = dj.Length;
					dj.Normalize();
					Point jm = ja + (jb - ja) / 2.0;
					foreach (Tuple<Point, Point> w in walls)
					{
						Vector dr = w.Item2 - w.Item1;
						double lr = dr.Length;
						dr.Normalize();
						if (Math.Abs(Vector.CrossProduct(dj, dr)) > 0.05 || lr < lj * 0.5 || lr > lj * 2.0)
						{
							continue;
						}
						candidates.Add(w.Item1 + (w.Item2 - w.Item1) / 2.0 - jm);
					}
				}
				if (candidates.Count == 0)
				{
					continue;
				}
				Vector bestT = candidates[0];
				int bestHits = -1;
				foreach (Vector t in candidates)
				{
					int hits = probe.Count((Point q) => grid.Nearest(fit.Apply(q) + t, 1.0).HasValue);
					if (hits > bestHits)
					{
						bestHits = hits;
						bestT = t;
					}
				}
				fit.Offset = bestT;
				foreach (double tol in new[] { 2.0, 1.0, 0.6, 0.6 })
				{
					List<Vector> pulls = new List<Vector>();
					foreach (Point q in samples)
					{
						Point at = fit.Apply(q);
						Point? near = grid.Nearest(at, tol);
						if (near.HasValue)
						{
							pulls.Add(near.Value - at);
						}
					}
					if (pulls.Count == 0)
					{
						break;
					}
					fit.Offset += new Vector(Median(pulls.Select((Vector v) => v.X)), Median(pulls.Select((Vector v) => v.Y)));
				}
				fit.Score = samples.Count((Point q) => grid.Nearest(fit.Apply(q), 0.75).HasValue) / (double)samples.Count;
				if (best == null || fit.Score > best.Score)
				{
					best = fit;
				}
			}
		}
		return best;
	}

	// Los bordes de los rooms (siguen los tabiques) y los muros que traiga el JSON, en cm
	private static List<Tuple<Point, Point>> JsonSegments(DesignLayout layout)
	{
		List<Tuple<Point, Point>> segs = layout.Rooms.SelectMany((LayoutRoom r) => r.Polygon.Select((Point q, int i) => Tuple.Create(q, r.Polygon[(i + 1) % r.Polygon.Length]))).ToList();
		segs.AddRange(layout.Walls);
		return segs.Where((Tuple<Point, Point> x) => (x.Item2 - x.Item1).Length >= 20.0).ToList();
	}

	// Puntos cada ~30 cm sobre esos segmentos, 1500 como máximo
	private static List<Point> Samples(List<Tuple<Point, Point>> segs)
	{
		List<Point> pts = new List<Point>();
		foreach (Tuple<Point, Point> x in segs)
		{
			int n = Math.Max(1, (int)((x.Item2 - x.Item1).Length / 30.0));
			for (int i = 0; i <= n; i++)
			{
				pts.Add(x.Item1 + (x.Item2 - x.Item1) * (i / (double)n));
			}
		}
		int step = Math.Max(1, pts.Count / 1500);
		return pts.Where((Point q, int i) => i % step == 0).ToList();
	}

	// Índice espacial de los muros de Revit para buscar el más cercano rápido
	private class SegGrid
	{
		private readonly List<Tuple<Point, Point>> _segs;

		private readonly double _size;

		private readonly Dictionary<long, List<int>> _cells = new Dictionary<long, List<int>>();

		public SegGrid(List<Tuple<Point, Point>> segs, double size)
		{
			_segs = segs;
			_size = size;
			for (int i = 0; i < segs.Count; i++)
			{
				int x0 = Cell(Math.Min(segs[i].Item1.X, segs[i].Item2.X));
				int x1 = Cell(Math.Max(segs[i].Item1.X, segs[i].Item2.X));
				int y0 = Cell(Math.Min(segs[i].Item1.Y, segs[i].Item2.Y));
				int y1 = Cell(Math.Max(segs[i].Item1.Y, segs[i].Item2.Y));
				for (int x = x0; x <= x1; x++)
				{
					for (int y = y0; y <= y1; y++)
					{
						long k = Key(x, y);
						if (!_cells.TryGetValue(k, out var list))
						{
							list = (_cells[k] = new List<int>());
						}
						list.Add(i);
					}
				}
			}
		}

		private int Cell(double v)
		{
			return (int)Math.Floor(v / _size);
		}

		private static long Key(int x, int y)
		{
			return ((long)x << 32) | (uint)y;
		}

		public Point? Nearest(Point p, double maxD)
		{
			int r = (int)Math.Ceiling(maxD / _size);
			int cx = Cell(p.X);
			int cy = Cell(p.Y);
			Point? best = null;
			double bestD = maxD * maxD;
			for (int x = cx - r; x <= cx + r; x++)
			{
				for (int y = cy - r; y <= cy + r; y++)
				{
					if (!_cells.TryGetValue(Key(x, y), out var list))
					{
						continue;
					}
					foreach (int i in list)
					{
						Vector ab = _segs[i].Item2 - _segs[i].Item1;
						double len = ab.LengthSquared;
						double t = (len < 1E-09) ? 0.0 : Math.Max(0.0, Math.Min(1.0, (p - _segs[i].Item1) * ab / len));
						Point q = _segs[i].Item1 + ab * t;
						double d = (q - p).LengthSquared;
						if (d <= bestD)
						{
							bestD = d;
							best = q;
						}
					}
				}
			}
			return best;
		}
	}

	private static double Median(IEnumerable<double> values)
	{
		List<double> v = values.OrderBy((double x) => x).ToList();
		return (v.Count == 0) ? 0.0 : v[v.Count / 2];
	}


}

// El muro del logo que eligió Room Studio (el que se ve al entrar), para que Place Logo lo use
public class LogoTarget
{
	public long WallId;

	public int Sign;

	public double U0;

	public double U1;

	public string Room;
}

public static class DesignMemory
{
	public static readonly Dictionary<string, LogoTarget> LogoWalls = new Dictionary<string, LogoTarget>();
}
