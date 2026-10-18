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
			if (Get(r, "is_filler") is bool filler && filler)
			{
				continue;
			}
			Point[] poly = Polygon(Get(r, "geometry") as string);
			if (poly == null || poly.Length < 3)
			{
				continue;
			}
			layout.Rooms.Add(new LayoutRoom
			{
				Type = (Get(r, "space_type") as string) ?? "Room",
				Subtype = Get(r, "subtype") as string,
				Polygon = poly
			});
		}
		if (Get(root, "nfp_data") is Dictionary<string, object> nfp)
		{
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
		if (layout.Rooms.Count == 0)
		{
			throw new InvalidDataException("the JSON has no rooms with a shape");
		}
		return layout;
	}

	private static object Get(Dictionary<string, object> d, string key)
	{
		return d.TryGetValue(key, out var v) ? v : null;
	}

	private static Point? Pt(object o)
	{
		object[] a = (o as IEnumerable)?.Cast<object>().ToArray();
		if (a == null || a.Length < 2)
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

	// "POLYGON ((x y, x y, …))": el anillo exterior
	private static Point[] Polygon(string wkt)
	{
		if (string.IsNullOrEmpty(wkt))
		{
			return null;
		}
		int open = wkt.IndexOf("((", StringComparison.Ordinal);
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
		if (pts.Count > 1 && (pts[0] - pts[pts.Count - 1]).Length < 0.01)
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

	// Prueba las 8 orientaciones; para cada una centra y afina el desplazamiento con el muro más cercano
	public static LayoutFit Find(DesignLayout layout, List<Tuple<Point, Point>> revitWalls)
	{
		if (revitWalls.Count == 0)
		{
			return null;
		}
		List<Point> samples = Samples(layout);
		if (samples.Count == 0)
		{
			return null;
		}
		Rect target = Bounds(revitWalls.SelectMany((Tuple<Point, Point> w) => new[] { w.Item1, w.Item2 }));
		LayoutFit best = null;
		foreach (int rot in new[] { 0, 90, 180, 270 })
		{
			foreach (bool mirror in new[] { false, true })
			{
				LayoutFit fit = new LayoutFit { Rotation = rot, Mirror = mirror };
				Rect placed = Bounds(samples.Select(fit.Apply));
				fit.Offset = new Point(target.X + target.Width / 2.0, target.Y + target.Height / 2.0) - new Point(placed.X + placed.Width / 2.0, placed.Y + placed.Height / 2.0);
				foreach (double tol in new[] { 8.0, 4.0, 2.0, 1.0, 1.0, 0.6 })
				{
					List<Vector> pulls = new List<Vector>();
					foreach (Point s in samples)
					{
						Point p = fit.Apply(s);
						Point q = Nearest(revitWalls, p);
						if ((q - p).Length <= tol)
						{
							pulls.Add(q - p);
						}
					}
					if (pulls.Count == 0)
					{
						break;
					}
					fit.Offset += new Vector(Median(pulls.Select((Vector v) => v.X)), Median(pulls.Select((Vector v) => v.Y)));
				}
				fit.Score = samples.Count((Point s) => (Nearest(revitWalls, fit.Apply(s)) - fit.Apply(s)).Length <= 0.75) / (double)samples.Count;
				if (best == null || fit.Score > best.Score)
				{
					best = fit;
				}
			}
		}
		return best;
	}

	// Puntos cada ~30 cm sobre los muros del JSON (o sobre los bordes de los rooms si no trae muros), 1500 como máximo
	private static List<Point> Samples(DesignLayout layout)
	{
		List<Tuple<Point, Point>> segs = layout.Walls.Where((Tuple<Point, Point> w) => (w.Item2 - w.Item1).Length >= 30.0).ToList();
		if (segs.Count < 8)
		{
			segs.AddRange(layout.Rooms.SelectMany((LayoutRoom r) => r.Polygon.Select((Point p, int i) => Tuple.Create(p, r.Polygon[(i + 1) % r.Polygon.Length]))));
		}
		List<Point> pts = new List<Point>();
		foreach (Tuple<Point, Point> s in segs)
		{
			int n = Math.Max(1, (int)((s.Item2 - s.Item1).Length / 30.0));
			for (int i = 0; i <= n; i++)
			{
				pts.Add(s.Item1 + (s.Item2 - s.Item1) * (i / (double)n));
			}
		}
		int step = Math.Max(1, pts.Count / 1500);
		return pts.Where((Point p, int i) => i % step == 0).ToList();
	}

	private static Point Nearest(List<Tuple<Point, Point>> segs, Point p)
	{
		Point best = segs[0].Item1;
		double bestD = double.MaxValue;
		foreach (Tuple<Point, Point> s in segs)
		{
			Vector ab = s.Item2 - s.Item1;
			double len = ab.LengthSquared;
			double t = (len < 1E-09) ? 0.0 : Math.Max(0.0, Math.Min(1.0, (p - s.Item1) * ab / len));
			Point q = s.Item1 + ab * t;
			double d = (q - p).LengthSquared;
			if (d < bestD)
			{
				bestD = d;
				best = q;
			}
		}
		return best;
	}

	private static double Median(IEnumerable<double> values)
	{
		List<double> v = values.OrderBy((double x) => x).ToList();
		return (v.Count == 0) ? 0.0 : v[v.Count / 2];
	}

	private static Rect Bounds(IEnumerable<Point> pts)
	{
		List<Point> p = pts.ToList();
		double x0 = p.Min((Point q) => q.X);
		double y0 = p.Min((Point q) => q.Y);
		return new Rect(x0, y0, p.Max((Point q) => q.X) - x0, p.Max((Point q) => q.Y) - y0);
	}
}
