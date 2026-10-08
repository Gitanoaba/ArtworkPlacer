using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ArtworkPlacer
{
    // Una sala encontrada a partir de los muros seleccionados (sin depender de los rooms de Revit)
    internal class FoundRoom
    {
        public ElementId LevelId;
        public List<XYZ> Polygon = new List<XYZ>();      // en planta (Z = 0), antihorario
        public List<RoomWall> Walls = new List<RoomWall>();
        public double Area;
    }

    // El tramo de un muro que da a la sala
    internal class RoomWall
    {
        public Wall Wall;
        public int Sign;          // cara que mira a la sala: +1 exterior, -1 interior
        public double U0, U1;     // tramo sobre la línea del muro, medido desde su punto inicial
        public XYZ A, B;          // extremos del tramo en planta
    }

    // ─── SALAS A PARTIR DE MUROS ─────────────────────────────────────────────
    // Las líneas de los muros seleccionados forman un grafo plano: cada cara cerrada es una sala.
    // Así funciona aunque los rooms de Revit falten o estén mal hechos.
    internal static class RoomFinder
    {
        private const double NodeTol = 0.2;     // pies: puntos más cerca que esto son el mismo nodo
        private const double MinArea = 20;      // pies² (~2 m²): caras más chicas se ignoran

        private class Seg
        {
            public Wall Wall;
            public XYZ P0, D;
            public double Len, Tol;
            public List<double> Ts = new List<double>();   // cruces con otros muros, sobre la línea
        }

        private class HalfEdge
        {
            public int From, To;
            public Seg Seg;
            public HalfEdge Twin;
            public double Angle;
            public bool Used;
        }

        // loose: muros que no cierran ninguna sala
        public static List<FoundRoom> Find(List<Wall> walls, out List<Wall> loose)
        {
            var rooms = new List<FoundRoom>();
            foreach (var group in walls.GroupBy(w => w.LevelId))
                rooms.AddRange(FindOnLevel(group.Key, group.ToList()));

            var used = new HashSet<ElementId>(rooms.SelectMany(r => r.Walls).Select(rw => rw.Wall.Id));
            loose = walls.Where(w => !used.Contains(w.Id)).ToList();
            return rooms;
        }

        private static List<FoundRoom> FindOnLevel(ElementId levelId, List<Wall> walls)
        {
            var segs = new List<Seg>();
            foreach (Wall w in walls)
            {
                if (!(w.Location is LocationCurve lc) || !(lc.Curve is Line line)) continue;
                XYZ a = Flat(line.GetEndPoint(0)), b = Flat(line.GetEndPoint(1));
                if (a.DistanceTo(b) < NodeTol) continue;
                double half = 0;
                try { half = w.Width / 2; } catch { }
                segs.Add(new Seg { Wall = w, P0 = a, D = (b - a).Normalize(), Len = a.DistanceTo(b), Tol = Math.Max(half + 0.2, 0.35) });
            }

            for (int i = 0; i < segs.Count; i++)
                for (int j = i + 1; j < segs.Count; j++)
                    Intersect(segs[i], segs[j]);

            // Nodos y aristas: cada muro queda partido en sus cruces
            var nodes = new List<XYZ>();
            var edges = new List<HalfEdge>();
            var seen = new HashSet<Tuple<int, int>>();
            foreach (Seg s in segs)
            {
                List<int> ids = s.Ts.OrderBy(t => t).Select(t => NodeAt(nodes, s.P0 + s.D * t)).ToList();
                for (int k = 0; k + 1 < ids.Count; k++)
                {
                    int a = ids[k], b = ids[k + 1];
                    if (a == b || !seen.Add(Tuple.Create(Math.Min(a, b), Math.Max(a, b)))) continue;
                    var h = new HalfEdge { From = a, To = b, Seg = s };
                    var back = new HalfEdge { From = b, To = a, Seg = s, Twin = h };
                    h.Twin = back;
                    edges.Add(h);
                    edges.Add(back);
                }
            }

            // Quitar ramas sueltas (puntas de muros que no cierran nada)
            while (true)
            {
                var degree = edges.GroupBy(h => h.From).ToDictionary(g => g.Key, g => g.Count());
                int before = edges.Count;
                edges.RemoveAll(h => degree[h.From] < 2 || degree[h.To] < 2);
                if (edges.Count == before) break;
            }

            foreach (HalfEdge h in edges)
            {
                XYZ v = nodes[h.To] - nodes[h.From];
                h.Angle = Math.Atan2(v.Y, v.X);
            }
            var outgoing = edges.GroupBy(h => h.From).ToDictionary(g => g.Key, g => g.OrderBy(h => h.Angle).ToList());

            // Recorrer caras dejando la cara a la izquierda: las cerradas salen antihorarias (área > 0)
            var rooms = new List<FoundRoom>();
            foreach (HalfEdge start in edges)
            {
                if (start.Used) continue;
                var loop = new List<HalfEdge>();
                HalfEdge h = start;
                while (!h.Used)
                {
                    h.Used = true;
                    loop.Add(h);
                    // En el nodo final, la arista inmediatamente en sentido horario desde la de vuelta
                    List<HalfEdge> outs = outgoing[h.To];
                    int idx = outs.IndexOf(h.Twin);
                    h = outs[(idx - 1 + outs.Count) % outs.Count];
                }
                if (h != start) continue;

                double area = SignedArea(loop.Select(e => nodes[e.From]).ToList());
                if (area < MinArea) continue;   // cara exterior (negativa) o demasiado chica
                rooms.Add(MakeRoom(levelId, loop, nodes, area));
            }
            return rooms;
        }

        // Cruce de dos muros, alargando un poco las puntas (suelen terminar en la cara del otro muro, no en su eje)
        private static void Intersect(Seg a, Seg b)
        {
            double tol = a.Tol + b.Tol;
            double denom = Cross(a.D, b.D);
            if (Math.Abs(denom) < 1e-6)
            {
                // Paralelos: solo cuentan si siguen la misma línea y se tocan por las puntas (un muro partido en dos)
                if (Math.Abs(Cross(a.D, b.P0 - a.P0)) > NodeTol) return;
                foreach (double ta in new[] { 0, a.Len })
                    foreach (double tb in new[] { 0, b.Len })
                    {
                        XYZ pa = a.P0 + a.D * ta, pb = b.P0 + b.D * tb;
                        if (pa.DistanceTo(pb) > tol) continue;
                        XYZ m = (pa + pb) / 2;
                        a.Ts.Add((m - a.P0).DotProduct(a.D));
                        b.Ts.Add((m - b.P0).DotProduct(b.D));
                    }
                return;
            }

            XYZ w = b.P0 - a.P0;
            double t = Cross(w, b.D) / denom, s = Cross(w, a.D) / denom;
            if (t < -tol || t > a.Len + tol || s < -tol || s > b.Len + tol) return;
            a.Ts.Add(t);
            b.Ts.Add(s);
        }

        private static FoundRoom MakeRoom(ElementId levelId, List<HalfEdge> loop, List<XYZ> nodes, double area)
        {
            var room = new FoundRoom { LevelId = levelId, Area = area, Polygon = loop.Select(h => nodes[h.From]).ToList() };
            foreach (var g in loop.GroupBy(h => h.Seg))
            {
                Seg s = g.Key;
                HalfEdge first = g.First();
                XYZ dir = nodes[first.To] - nodes[first.From];
                XYZ left = new XYZ(-dir.Y, dir.X, 0);    // hacia adentro de la sala
                int sign = left.DotProduct(s.Wall.Orientation) > 0 ? 1 : -1;

                var us = g.SelectMany(h => new[] { nodes[h.From], nodes[h.To] })
                          .Select(p => (p - s.P0).DotProduct(s.D)).ToList();
                double u0 = us.Min(), u1 = us.Max();
                room.Walls.Add(new RoomWall { Wall = s.Wall, Sign = sign, U0 = u0, U1 = u1, A = s.P0 + s.D * u0, B = s.P0 + s.D * u1 });
            }
            return room;
        }

        private static int NodeAt(List<XYZ> nodes, XYZ p)
        {
            for (int i = 0; i < nodes.Count; i++)
                if (nodes[i].DistanceTo(p) < NodeTol) return i;
            nodes.Add(p);
            return nodes.Count - 1;
        }

        private static double SignedArea(List<XYZ> poly)
        {
            double a = 0;
            for (int i = 0; i < poly.Count; i++)
            {
                XYZ p = poly[i], q = poly[(i + 1) % poly.Count];
                a += p.X * q.Y - q.X * p.Y;
            }
            return a / 2;
        }

        internal static XYZ Flat(XYZ p) => new XYZ(p.X, p.Y, 0);

        private static double Cross(XYZ a, XYZ b) => a.X * b.Y - a.Y * b.X;
    }
}
