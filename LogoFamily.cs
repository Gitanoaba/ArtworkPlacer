using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Visual;
using RevitApp = Autodesk.Revit.ApplicationServices.Application;

namespace ArtworkPlacer;

// El logo como familia, igual que el artwork: Revit 2024 no deja crear ni colocar decals por código.
// Una familia face-based "Qbiq Logo" del tamaño exacto de la imagen, con un material que la lleva como textura.
internal static class LogoFamily
{
	public const string FamilyName = "Qbiq Logo";

	private const string MaterialName = "Qbiq Logo";

	private const string MaterialParam = "Logo Material";

	private const double Depth = 0.02;

	private class Overwrite : IFamilyLoadOptions
	{
		public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
		{
			overwriteParameterValues = true;
			return true;
		}

		public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
		{
			source = FamilySource.Family;
			overwriteParameterValues = true;
			return true;
		}
	}

	// Genera la familia con el rectángulo de w × h (pies) y la carga en el proyecto. Fuera de toda transacción.
	public static FamilySymbol Build(Document doc, RevitApp app, double w, double h)
	{
		string template = FindTemplate(app);
		if (template == null)
		{
			throw new InvalidOperationException("Revit's \"Generic Model face based\" family template isn't installed");
		}
		string folder = Path.Combine(Path.GetTempPath(), "QbiqTools");
		Directory.CreateDirectory(folder);
		string path = Path.Combine(folder, FamilyName + ".rfa");
		Document fam = app.NewFamilyDocument(template);
		try
		{
			using (Transaction t = new Transaction(fam, "Qbiq Logo"))
			{
				t.Start();
				// Desde el origen hacia +X/+Y: la textura empieza en la esquina de la pieza, sin desfase
				CurveArray loop = new CurveArray();
				XYZ[] p = { XYZ.Zero, new XYZ(w, 0.0, 0.0), new XYZ(w, h, 0.0), new XYZ(0.0, h, 0.0) };
				for (int i = 0; i < 4; i++)
				{
					loop.Append(Line.CreateBound(p[i], p[(i + 1) % 4]));
				}
				CurveArrArray profile = new CurveArrArray();
				profile.Append(loop);
				SketchPlane plane = SketchPlane.Create(fam, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero));
				Extrusion ex = fam.FamilyCreate.NewExtrusion(true, profile, plane, Depth);
				FamilyParameter mat = fam.FamilyManager.AddParameter(MaterialParam, GroupTypeId.Materials, SpecTypeId.Reference.Material, false);
				fam.FamilyManager.AssociateElementParameterToFamilyParameter(ex.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM), mat);
				t.Commit();
			}
			fam.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true });
		}
		finally
		{
			fam.Close(false);
		}
		if (!doc.LoadFamily(path, new Overwrite(), out var family) || family == null)
		{
			family = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().FirstOrDefault((Family f) => f.Name == FamilyName);
		}
		return (family == null) ? null : (doc.GetElement(family.GetFamilySymbolIds().First()) as FamilySymbol);
	}

	private static string FindTemplate(RevitApp app)
	{
		string[] names = { "Metric Generic Model face based.rft", "Generic Model face based.rft" };
		foreach (string root in new[] { app.FamilyTemplatePath, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Autodesk", "RVT " + app.VersionNumber, "Family Templates") })
		{
			if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
			{
				continue;
			}
			foreach (string name in names)
			{
				string hit = Directory.GetFiles(root, name, SearchOption.AllDirectories).FirstOrDefault();
				if (hit != null)
				{
					return hit;
				}
			}
		}
		return null;
	}

	// Material con la imagen como textura, del tamaño exacto de la pieza (sin repetir). Dentro de una transacción.
	public static Material Paint(Document doc, RevitApp app, FamilySymbol symbol, string image, double w, double h)
	{
		Material mat = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().FirstOrDefault((Material m) => m.Name == MaterialName)
			?? (doc.GetElement(Material.Create(doc, MaterialName)) as Material);
		if (!(doc.GetElement(mat.AppearanceAssetId) is AppearanceAssetElement ae))
		{
			ae = AppearanceAssetElement.GetAppearanceAssetElementByName(doc, MaterialName) ?? NewGenericAppearance(doc, app);
			mat.AppearanceAssetId = ae.Id;
		}
		mat.UseRenderAppearanceForShading = true;
		mat.Color = new Autodesk.Revit.DB.Color(255, 255, 255);

		string diffuse = image;
		string mask = null;
		if (HasTransparency(image))
		{
			diffuse = Flattened(image, out mask);
		}
		using (AppearanceAssetEditScope scope = new AppearanceAssetEditScope(doc))
		{
			Asset asset = scope.Start(ae.Id);
			SetBitmap(asset.FindByName(Generic.GenericDiffuse), diffuse, w, h);
			AssetProperty cutout = asset.FindByName(Generic.GenericCutoutOpacity);
			if (mask != null)
			{
				SetBitmap(cutout, mask, w, h);
			}
			else if (cutout != null && cutout.NumberOfConnectedProperties > 0)
			{
				cutout.RemoveConnectedAsset();
			}
			scope.Commit(true);
		}
		symbol.LookupParameter(MaterialParam)?.Set(mat.Id);
		return mat;
	}

	// Una apariencia "Generic" nueva: copia de una del proyecto si hay, si no de la biblioteca de Revit
	private static AppearanceAssetElement NewGenericAppearance(Document doc, RevitApp app)
	{
		AppearanceAssetElement existing = new FilteredElementCollector(doc).OfClass(typeof(AppearanceAssetElement)).Cast<AppearanceAssetElement>()
			.FirstOrDefault((AppearanceAssetElement a) => a.GetRenderingAsset()?.FindByName(Generic.GenericDiffuse) != null);
		if (existing != null)
		{
			return existing.Duplicate(MaterialName);
		}
		Asset generic = app.GetAssets(AssetType.Appearance).FirstOrDefault((Asset a) => a.FindByName(Generic.GenericDiffuse) != null);
		if (generic == null)
		{
			throw new InvalidOperationException("couldn't find a Generic appearance to build the logo material");
		}
		return AppearanceAssetElement.Create(doc, MaterialName, generic);
	}

	private static void SetBitmap(AssetProperty prop, string path, double w, double h)
	{
		if (prop == null)
		{
			return;
		}
		if (prop.NumberOfConnectedProperties == 0)
		{
			prop.AddConnectedAsset("UnifiedBitmapSchema");
		}
		Asset bmp = prop.GetSingleConnectedAsset();
		if (bmp.FindByName(UnifiedBitmap.UnifiedbitmapBitmap) is AssetPropertyString file && file.IsValidValue(path))
		{
			file.Value = path;
		}
		SetDistance(bmp, UnifiedBitmap.TextureRealWorldScaleX, w);
		SetDistance(bmp, UnifiedBitmap.TextureRealWorldScaleY, h);
		SetDistance(bmp, UnifiedBitmap.TextureRealWorldOffsetX, 0.0);
		SetDistance(bmp, UnifiedBitmap.TextureRealWorldOffsetY, 0.0);
		if (bmp.FindByName(UnifiedBitmap.TextureURepeat) is AssetPropertyBoolean u)
		{
			u.Value = false;
		}
		if (bmp.FindByName(UnifiedBitmap.TextureVRepeat) is AssetPropertyBoolean v)
		{
			v.Value = false;
		}
	}

	// Las distancias de una textura van en sus propias unidades
	private static void SetDistance(Asset bmp, string name, double feet)
	{
		if (bmp.FindByName(name) is AssetPropertyDistance d)
		{
			d.Value = UnitUtils.Convert(feet, UnitTypeId.Feet, d.GetUnitTypeId());
		}
	}

	private static bool HasTransparency(string path)
	{
		try
		{
			using Bitmap bmp = new Bitmap(path);
			if (!System.Drawing.Image.IsAlphaPixelFormat(bmp.PixelFormat))
			{
				return false;
			}
			int step = Math.Max(1, Math.Min(bmp.Width, bmp.Height) / 64);
			for (int y = 0; y < bmp.Height; y += step)
			{
				for (int x = 0; x < bmp.Width; x += step)
				{
					if (bmp.GetPixel(x, y).A < 250)
					{
						return true;
					}
				}
			}
		}
		catch
		{
		}
		return false;
	}

	// PNG con transparencia: la imagen sobre blanco (sin bordes negros) y una máscara blanco/negro para el recorte
	private static string Flattened(string path, out string mask)
	{
		string dir = Path.GetDirectoryName(path);
		string stem = Path.GetFileNameWithoutExtension(path);
		string flat = Path.Combine(dir, stem + "_logo_flat.png");
		mask = Path.Combine(dir, stem + "_logo_mask.png");
		using Bitmap src = new Bitmap(path);
		using Bitmap a = new Bitmap(src.Width, src.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
		using Bitmap b = new Bitmap(src.Width, src.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
		using (Graphics g = Graphics.FromImage(a))
		{
			g.Clear(System.Drawing.Color.White);
			g.DrawImage(src, 0, 0, src.Width, src.Height);
		}
		for (int y = 0; y < src.Height; y++)
		{
			for (int x = 0; x < src.Width; x++)
			{
				int alpha = src.GetPixel(x, y).A;
				b.SetPixel(x, y, System.Drawing.Color.FromArgb(alpha, alpha, alpha));
			}
		}
		a.Save(flat, ImageFormat.Png);
		b.Save(mask, ImageFormat.Png);
		return flat;
	}
}
