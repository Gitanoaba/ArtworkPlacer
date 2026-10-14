using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace ArtworkPlacer;

internal static class RoomFinder
{
	private class Seg
	{
		public Wall Wall;

		public XYZ P0;

		public XYZ D;

		public double Len;

		public double Tol;

		public List<double> Ts = new List<double>();
	}

	private class HalfEdge
	{
		public int From;

		public int To;

		public Seg Seg;

		public HalfEdge Twin;

		public double Angle;

		public bool Used;
	}

	private const double NodeTol = 0.2;

	private const double MinArea = 20.0;

	public static List<FoundRoom> Find(List<Wall> walls, out List<Wall> loose)
	{
		List<FoundRoom> list = new List<FoundRoom>();
		foreach (IGrouping<ElementId, Wall> item in from w in walls
			group w by w.LevelId)
		{
			list.AddRange(FindOnLevel(item.Key, item.ToList()));
		}
		HashSet<ElementId> used = new HashSet<ElementId>(from rw in list.SelectMany((FoundRoom r) => r.Walls)
			select rw.Wall.Id);
		loose = walls.Where((Wall w) => !used.Contains(w.Id)).ToList();
		return list;
	}

	private static List<FoundRoom> FindOnLevel(ElementId levelId, List<Wall> walls)
	{
		List<Seg> list = new List<Seg>();
		foreach (Wall wall in walls)
		{
			if (!(wall.Location is LocationCurve { Curve: Line curve }))
			{
				continue;
			}
			XYZ xYZ = Flat(curve.GetEndPoint(0));
			XYZ xYZ2 = Flat(curve.GetEndPoint(1));
			if (!(xYZ.DistanceTo(xYZ2) < 0.2))
			{
				double num = 0.0;
				try
				{
					num = wall.Width / 2.0;
				}
				catch
				{
				}
				list.Add(new Seg
				{
					Wall = wall,
					P0 = xYZ,
					D = (xYZ2 - xYZ).Normalize(),
					Len = xYZ.DistanceTo(xYZ2),
					Tol = Math.Max(num + 0.2, 0.35)
				});
			}
		}
		for (int i = 0; i < list.Count; i++)
		{
			for (int j = i + 1; j < list.Count; j++)
			{
				Intersect(list[i], list[j]);
			}
		}
		List<XYZ> nodes = new List<XYZ>();
		List<HalfEdge> list2 = new List<HalfEdge>();
		HashSet<Tuple<int, int>> hashSet = new HashSet<Tuple<int, int>>();
		foreach (Seg s in list)
		{
			List<int> list3 = (from t in s.Ts
				orderby t
				select NodeAt(nodes, s.P0 + s.D * t)).ToList();
			for (int num2 = 0; num2 + 1 < list3.Count; num2++)
			{
				int num3 = list3[num2];
				int num4 = list3[num2 + 1];
				if (num3 != num4 && hashSet.Add(Tuple.Create(Math.Min(num3, num4), Math.Max(num3, num4))))
				{
					HalfEdge halfEdge = new HalfEdge
					{
						From = num3,
						To = num4,
						Seg = s
					};
					HalfEdge item = (halfEdge.Twin = new HalfEdge
					{
						From = num4,
						To = num3,
						Seg = s,
						Twin = halfEdge
					});
					list2.Add(halfEdge);
					list2.Add(item);
				}
			}
		}
		int count;
		do
		{
			Dictionary<int, int> degree = (from h in list2
				group h by h.From).ToDictionary((IGrouping<int, HalfEdge> g) => g.Key, (IGrouping<int, HalfEdge> g) => g.Count());
			count = list2.Count;
			list2.RemoveAll((HalfEdge h) => degree[h.From] < 2 || degree[h.To] < 2);
		}
		while (list2.Count != count);
		foreach (HalfEdge item2 in list2)
		{
			XYZ xYZ3 = nodes[item2.To] - nodes[item2.From];
			item2.Angle = Math.Atan2(xYZ3.Y, xYZ3.X);
		}
		Dictionary<int, List<HalfEdge>> dictionary = (from h in list2
			group h by h.From).ToDictionary((IGrouping<int, HalfEdge> g) => g.Key, (IGrouping<int, HalfEdge> g) => g.OrderBy((HalfEdge h) => h.Angle).ToList());
		List<FoundRoom> list4 = new List<FoundRoom>();
		foreach (HalfEdge item3 in list2)
		{
			if (item3.Used)
			{
				continue;
			}
			List<HalfEdge> list5 = new List<HalfEdge>();
			HalfEdge halfEdge2 = item3;
			while (!halfEdge2.Used)
			{
				halfEdge2.Used = true;
				list5.Add(halfEdge2);
				List<HalfEdge> list6 = dictionary[halfEdge2.To];
				int num5 = list6.IndexOf(halfEdge2.Twin);
				halfEdge2 = list6[(num5 - 1 + list6.Count) % list6.Count];
			}
			if (halfEdge2 == item3)
			{
				double num6 = SignedArea(list5.Select((HalfEdge e) => nodes[e.From]).ToList());
				if (!(num6 < 20.0))
				{
					list4.Add(MakeRoom(levelId, list5, nodes, num6));
				}
			}
		}
		return list4;
	}

	private static void Intersect(Seg a, Seg b)
	{
		double num = a.Tol + b.Tol;
		double num2 = Cross(a.D, b.D);
		if (Math.Abs(num2) < 1E-06)
		{
			if (Math.Abs(Cross(a.D, b.P0 - a.P0)) > 0.2)
			{
				return;
			}
			double[] array = new double[2] { 0.0, a.Len };
			foreach (double num3 in array)
			{
				double[] array2 = new double[2] { 0.0, b.Len };
				foreach (double num4 in array2)
				{
					XYZ xYZ = a.P0 + a.D * num3;
					XYZ xYZ2 = b.P0 + b.D * num4;
					if (!(xYZ.DistanceTo(xYZ2) > num))
					{
						XYZ xYZ3 = (xYZ + xYZ2) / 2.0;
						a.Ts.Add((xYZ3 - a.P0).DotProduct(a.D));
						b.Ts.Add((xYZ3 - b.P0).DotProduct(b.D));
					}
				}
			}
		}
		else
		{
			XYZ a2 = b.P0 - a.P0;
			double num5 = Cross(a2, b.D) / num2;
			double num6 = Cross(a2, a.D) / num2;
			if (!(num5 < 0.0 - num) && !(num5 > a.Len + num) && !(num6 < 0.0 - num) && !(num6 > b.Len + num))
			{
				a.Ts.Add(num5);
				b.Ts.Add(num6);
			}
		}
	}

	private static FoundRoom MakeRoom(ElementId levelId, List<HalfEdge> loop, List<XYZ> nodes, double area)
	{
		FoundRoom foundRoom = new FoundRoom
		{
			LevelId = levelId,
			Area = area,
			Polygon = loop.Select((HalfEdge h) => nodes[h.From]).ToList()
		};
		foreach (IGrouping<Seg, HalfEdge> item in from h in loop
			group h by h.Seg)
		{
			Seg s = item.Key;
			HalfEdge halfEdge = item.First();
			XYZ xYZ = nodes[halfEdge.To] - nodes[halfEdge.From];
			int sign = ((new XYZ(0.0 - xYZ.Y, xYZ.X, 0.0).DotProduct(s.Wall.Orientation) > 0.0) ? 1 : (-1));
			List<double> source = (from p in item.SelectMany((HalfEdge h) => new XYZ[2]
				{
					nodes[h.From],
					nodes[h.To]
				})
				select (p - s.P0).DotProduct(s.D)).ToList();
			double num = source.Min();
			double num2 = source.Max();
			foundRoom.Walls.Add(new RoomWall
			{
				Wall = s.Wall,
				Sign = sign,
				U0 = num,
				U1 = num2,
				A = s.P0 + s.D * num,
				B = s.P0 + s.D * num2
			});
		}
		return foundRoom;
	}

	private static int NodeAt(List<XYZ> nodes, XYZ p)
	{
		for (int i = 0; i < nodes.Count; i++)
		{
			if (nodes[i].DistanceTo(p) < 0.2)
			{
				return i;
			}
		}
		nodes.Add(p);
		return nodes.Count - 1;
	}

	private static double SignedArea(List<XYZ> poly)
	{
		double num = 0.0;
		for (int i = 0; i < poly.Count; i++)
		{
			XYZ xYZ = poly[i];
			XYZ xYZ2 = poly[(i + 1) % poly.Count];
			num += xYZ.X * xYZ2.Y - xYZ2.X * xYZ.Y;
		}
		return num / 2.0;
	}

	internal static XYZ Flat(XYZ p)
	{
		return new XYZ(p.X, p.Y, 0.0);
	}

	private static double Cross(XYZ a, XYZ b)
	{
		return a.X * b.Y - a.Y * b.X;
	}
}
