using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;

namespace ArtworkPlacer;

public enum FurnKind
{
	Low,
	Tall,
	Sofa,
	Tv,
	Table,
	Chair,
	Column
}

// Un mueble (o columna) en planta: su contorno en pies, en coordenadas del modelo.
// Sale de los muebles del modelo de Revit o, si no hay, del JSON del diseño.
public class Furnishing
{
	public string Name;

	public Point[] Outline;

	public FurnKind Kind;

	// Más alto que esto, el arte no va detrás (≈1,30 m)
	public const double TallFeet = 4.25;

	private static readonly Regex TvWord = new Regex("(^|[^A-Z])TV([^A-Z]|$)");

	private static readonly Regex TallWords = new Regex("PHONE.?BOOTH|LOCKER|STORAGE_\\d+_H|_H#|BOOKCASE|SHELV|SHELF|WARDROBE|CABINET_H|TALL|FRIDGE|REFRIG|KITCHEN.?COUNTER|PLANT");

	private static readonly Regex SofaWords = new Regex("SOFA|COUCH|BENCH|LOUNGER|BOOTH|BANQUETTE");

	// Por el nombre de la familia (QBIQ_CW_LOCKER#30x60, QBIQ_FR_SOFA_3_SEAT…); top = alto sobre el piso si se conoce
	public static FurnKind KindOf(string name, double? top)
	{
		string n = (name ?? "").ToUpperInvariant();
		FurnKind kind;
		if (TvWord.IsMatch(n))
		{
			kind = FurnKind.Tv;
		}
		else if (n.Contains("COLUMN"))
		{
			kind = FurnKind.Column;
		}
		else if (TallWords.IsMatch(n))
		{
			kind = FurnKind.Tall;
		}
		else if (SofaWords.IsMatch(n))
		{
			kind = FurnKind.Sofa;
		}
		else if (n.Contains("TABLE") && n.Contains("CONF"))
		{
			kind = FurnKind.Table;
		}
		else if (n.Contains("CHAIR") || n.Contains("STOOL"))
		{
			kind = FurnKind.Chair;
		}
		else
		{
			kind = FurnKind.Low;
		}
		// Con el alto real (Revit): una planta chica no tapa nada, y algo alto sin nombre conocido sí
		if (top.HasValue)
		{
			if (kind == FurnKind.Tall && top.Value < 3.3)
			{
				kind = FurnKind.Low;
			}
			else if (kind == FurnKind.Low && top.Value > TallFeet)
			{
				kind = FurnKind.Tall;
			}
		}
		return kind;
	}

	// "QBIQ_CW_STORAGE_100_H#60" → "storage"
	public string Label
	{
		get
		{
			string n = Regex.Replace(Name ?? "", "^QBIQ_[A-Z]{2}_\\s*", "", RegexOptions.IgnoreCase);
			int hash = n.IndexOf('#');
			if (hash > 0)
			{
				n = n.Substring(0, hash);
			}
			n = Regex.Replace(n.Replace('_', ' '), "\\b(\\d+|HC\\d|[HL])\\b", "", RegexOptions.IgnoreCase);
			n = Regex.Replace(n, "\\s+", " ").Trim().ToLowerInvariant();
			return (n.Length > 0) ? n : "furniture";
		}
	}

	public Point Center => new Point(Outline.Average((Point p) => p.X), Outline.Average((Point p) => p.Y));

	// Dirección del lado más largo (en una mesa, hacia sus cabeceras)
	public Vector LongAxis
	{
		get
		{
			Vector best = new Vector(1.0, 0.0);
			double len = -1.0;
			for (int i = 0; i < Outline.Length; i++)
			{
				Vector e = Outline[(i + 1) % Outline.Length] - Outline[i];
				if (e.Length > len)
				{
					len = e.Length;
					best = e;
				}
			}
			best.Normalize();
			return best;
		}
	}

	// Largo / ancho: una mesa rectangular de conferencia es claramente alargada
	public double Elongation
	{
		get
		{
			Vector a = LongAxis;
			Vector b = new Vector(0.0 - a.Y, a.X);
			double la = Outline.Max((Point p) => (Vector)p * a) - Outline.Min((Point p) => (Vector)p * a);
			double lb = Outline.Max((Point p) => (Vector)p * b) - Outline.Min((Point p) => (Vector)p * b);
			return la / Math.Max(0.1, lb);
		}
	}
}
