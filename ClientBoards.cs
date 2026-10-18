using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace ArtworkPlacer;

public enum ArtZone
{
	None,
	General,
	Kitchen,
	Meeting,
	Reception
}

// Un tablero de cliente (p.ej. IWG_Regus): sus piezas agrupadas por zona, en orden de lectura
public class ClientBoard
{
	public string Name;

	public Dictionary<ArtZone, List<long>> Zones = new Dictionary<ArtZone, List<long>>();

	public int Count(ArtZone z)
	{
		return Zones.TryGetValue(z, out var ids) ? ids.Count : 0;
	}
}

// Lee las secciones de artwork donde cada tablero tiene un título (IWG_HQ, IWG_Regus…) y encabezados de zona
// (General, Kitchen, Meeting Rooms / Lounge / Club, Reception) con las piezas debajo.
internal static class ClientBoards
{
	private class Note
	{
		public string Text;

		public double X;

		public double Y;

		public ArtZone Zone;
	}

	private static readonly string[] ReceptionWords = { "reception", "lobby", "entrance", "entry", "welcome", "front desk", "קבלה", "לובי" };

	private static readonly string[] MeetingWords = { "meeting", "conference", "boardroom", "board room", "lounge", "club", "ישיבות" };

	private static readonly string[] KitchenWords = { "kitchen", "pantry", "cafe", "café", "canteen", "coffee", "break", "מטבח" };

	private static readonly string[] GeneralWords = { "general", "corridor", "hall", "open space", "open office", "open plan", "workstation", "work station", "desk", "circulation", "מסדרון" };

	// Zona de un texto o del nombre de un room. Reception y Meeting antes que General: "Lounge hall" es lounge
	public static ArtZone ZoneOf(string text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return ArtZone.None;
		}
		if (WallRules.Has(text, ReceptionWords))
		{
			return ArtZone.Reception;
		}
		if (WallRules.Has(text, MeetingWords))
		{
			return ArtZone.Meeting;
		}
		if (WallRules.Has(text, KitchenWords))
		{
			return ArtZone.Kitchen;
		}
		if (WallRules.Has(text, GeneralWords))
		{
			return ArtZone.General;
		}
		return ArtZone.None;
	}

	public static List<ClientBoard> Read(Document doc, HashSet<long> placeable)
	{
		List<ClientBoard> boards = new List<ClientBoard>();
		foreach (ViewSection view in new FilteredElementCollector(doc).OfClass(typeof(ViewSection)).Cast<ViewSection>()
			.Where((ViewSection v) => !v.IsTemplate && PlaceArtworkCommand.LooksLikeArtwork(v.Name)))
		{
			XYZ right = view.RightDirection;
			XYZ up = view.UpDirection;
			List<Note> notes = new FilteredElementCollector(doc, view.Id).OfClass(typeof(TextNote)).Cast<TextNote>()
				.Select((TextNote n) => new Note
				{
					Text = (n.Text ?? "").Trim(),
					X = n.Coord.DotProduct(right),
					Y = n.Coord.DotProduct(up),
					Zone = ZoneOf(n.Text)
				})
				.Where((Note n) => n.Text.Length > 0)
				.ToList();
			List<Note> titles = notes.Where((Note n) => n.Zone == ArtZone.None).ToList();
			List<Note> headings = notes.Where((Note n) => n.Zone != ArtZone.None).ToList();
			if (titles.Count == 0 || headings.Count == 0)
			{
				continue;
			}
			// Cada encabezado pertenece al título más cercano por encima de él
			Dictionary<Note, Note> boardOf = headings.ToDictionary((Note h) => h, (Note h) => NearestAbove(titles, h.X, h.Y));
			Dictionary<string, ClientBoard> byName = new Dictionary<string, ClientBoard>();
			var pieces = from FamilyInstance fi in new FilteredElementCollector(doc, view.Id).OfClass(typeof(FamilyInstance))
				where placeable.Contains(fi.Symbol.Id.Value)
				let c = Center(fi)
				where c != null
				select new { Id = fi.Symbol.Id.Value, X = c.DotProduct(right), Y = c.DotProduct(up) };
			foreach (var p in pieces.OrderByDescending(p => Math.Round(p.Y, 1)).ThenBy(p => p.X))
			{
				// La pieza va con el encabezado más cercano por encima (su columna); el tablero es el de ese encabezado
				Note heading = NearestAbove(headings, p.X, p.Y);
				Note title = (heading != null) ? boardOf[heading] : null;
				if (heading == null || title == null)
				{
					continue;
				}
				if (!byName.TryGetValue(title.Text, out var board))
				{
					board = (byName[title.Text] = new ClientBoard { Name = title.Text });
				}
				if (!board.Zones.TryGetValue(heading.Zone, out var ids))
				{
					ids = (board.Zones[heading.Zone] = new List<long>());
				}
				if (!ids.Contains(p.Id))
				{
					ids.Add(p.Id);
				}
			}
			boards.AddRange(byName.Values.Where((ClientBoard b) => b.Zones.Count > 0));
		}
		return boards.GroupBy((ClientBoard b) => b.Name).Select((IGrouping<string, ClientBoard> g) => g.First()).OrderBy((ClientBoard b) => b.Name).ToList();
	}

	// El texto por encima del punto que está más cerca; lo horizontal pesa el doble, para quedarse en la misma columna
	private static Note NearestAbove(List<Note> notes, double x, double y)
	{
		return notes.Where((Note n) => n.Y > y + 0.05).OrderBy((Note n) => n.Y - y + 2.0 * Math.Abs(n.X - x)).FirstOrDefault();
	}

	private static XYZ Center(Element e)
	{
		BoundingBoxXYZ bb = e.get_BoundingBox(null);
		return (bb == null) ? null : ((bb.Min + bb.Max) / 2.0);
	}
}
