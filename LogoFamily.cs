using System;
using System.Collections.Generic;
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

	private const string WidthParam = "Logo Width";

	private const string HeightParam = "Logo Height";

	// Tamaños que trae la familia como tipos: en Revit se cambia el tamaño eligiendo otro tipo
	private static readonly double[] SizesCm = { 60.0, 90.0, 120.0, 150.0, 200.0, 250.0 };

	public static string TypeName(double widthFeet)
	{
		return $"Logo {Math.Round(UnitUtils.ConvertFromInternalUnits(widthFeet, UnitTypeId.Centimeters))} cm";
	}

	// Alto / ancho de la imagen, para que el logo tenga su proporción
	public static double Aspect(string path)
	{
		try
		{
			using System.Drawing.Image img = System.Drawing.Image.FromFile(path);
			return (img.Width > 0) ? ((double)img.Height / img.Width) : 1.0;
		}
		catch
		{
			return 1.0;
		}
	}

	// La pieza se hunde 9 mm en el muro y sobresale ~1 mm: con un PNG recortado, por el hueco se ve el muro
	// y no la cara trasera de la pieza (que lleva la imagen espejada)
	private const double Back = 0.03;

	private const double Proud = 0.004;

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
	// Imagen gris con "CLIENT LOGO" para reservar el lugar hasta que llegue la del cliente
	public static string Placeholder()
	{
		string dir = Path.Combine(Path.GetTempPath(), "QbiqTools");
		Directory.CreateDirectory(dir);
		string file = Path.Combine(dir, "client_logo_placeholder.png");
		if (!File.Exists(file))
		{
			using System.Drawing.Bitmap bmp = new System.Drawing.Bitmap(800, 400);
			using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(bmp))
			{
				g.Clear(System.Drawing.Color.FromArgb(225, 228, 233));
				g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
				using System.Drawing.Font font = new System.Drawing.Font("Segoe UI", 64f, System.Drawing.FontStyle.Bold);
				using System.Drawing.StringFormat center = new System.Drawing.StringFormat { Alignment = System.Drawing.StringAlignment.Center, LineAlignment = System.Drawing.StringAlignment.Center };
				g.DrawString("CLIENT LOGO", font, System.Drawing.Brushes.Gray, new System.Drawing.RectangleF(0f, 0f, 800f, 400f), center);
			}
			bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
		}
		return file;
	}

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
				FamilyManager fm = fam.FamilyManager;
				if (fm.Types.Size == 0)
				{
					fm.NewType(TypeName(w));
				}
				else
				{
					fm.RenameCurrentType(TypeName(w));
				}
				FamilyParameter mat = fm.AddParameter(MaterialParam, GroupTypeId.Materials, SpecTypeId.Reference.Material, false);
				Extrusion ex;
				using (SubTransaction sizes = new SubTransaction(fam))
				{
					sizes.Start();
					try
					{
						ex = Sized(fam, w, h, mat);
						sizes.Commit();
					}
					catch
					{
						// Sin tipos por tamaño: un rectángulo fijo, como antes
						sizes.RollBack();
						ex = Rectangle(fam, w, h);
						fm.AssociateElementParameterToFamilyParameter(ex.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM), mat);
					}
				}
				t.Commit();
			}
			// Guardarla primero le da el nombre "Qbiq Logo"; después el documento se carga a sí mismo en el proyecto
			fam.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true });
			Family loaded = null;
			string why = null;
			try
			{
				loaded = fam.LoadFamily(doc, new Overwrite());
			}
			catch (Exception ex)
			{
				why = ex.Message;
			}
			loaded = loaded ?? new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().FirstOrDefault((Family f) => f.Name == FamilyName);
			if (loaded == null)
			{
				throw new InvalidOperationException("Revit didn't load the logo family into the project" + ((why != null) ? (": " + why) : " (no reason given)"));
			}
			List<FamilySymbol> types = loaded.GetFamilySymbolIds().Select((ElementId id) => doc.GetElement(id)).OfType<FamilySymbol>().ToList();
			if (types.Count == 0)
			{
				throw new InvalidOperationException("the logo family loaded with no types");
			}
			return types.FirstOrDefault((FamilySymbol s) => s.Name == TypeName(w)) ?? types.First();
		}
		finally
		{
			fam.Close(false);
		}
	}

	// Desde el origen hacia +X/+Y: la textura empieza en la esquina de la pieza, sin desfase
	private static Extrusion Rectangle(Document fam, double w, double h)
	{
		CurveArray loop = new CurveArray();
		XYZ[] p = { new XYZ(0.0, 0.0, -Back), new XYZ(w, 0.0, -Back), new XYZ(w, h, -Back), new XYZ(0.0, h, -Back) };
		for (int i = 0; i < 4; i++)
		{
			loop.Append(Line.CreateBound(p[i], p[(i + 1) % 4]));
		}
		CurveArrArray profile = new CurveArrArray();
		profile.Append(loop);
		SketchPlane plane = SketchPlane.Create(fam, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0.0, 0.0, -Back)));
		return fam.FamilyCreate.NewExtrusion(true, profile, plane, Back + Proud);
	}

	// El rectángulo atado a planos de referencia con "Logo Width" y "Logo Height", y un tipo por cada tamaño
	private static Extrusion Sized(Document fam, double w, double h, FamilyParameter mat)
	{
		FamilyManager fm = fam.FamilyManager;
		View view = new FilteredElementCollector(fam).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().First((ViewPlan v) => !v.IsTemplate);
		List<ReferencePlane> planes = new FilteredElementCollector(fam).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>().ToList();
		ReferencePlane left = planes.First((ReferencePlane r) => Math.Abs(Math.Abs(r.Normal.X) - 1.0) < 1E-06 && Math.Abs(r.BubbleEnd.X) < 1E-06);
		ReferencePlane bottom = planes.First((ReferencePlane r) => Math.Abs(Math.Abs(r.Normal.Y) - 1.0) < 1E-06 && Math.Abs(r.BubbleEnd.Y) < 1E-06);
		ReferencePlane right = fam.FamilyCreate.NewReferencePlane(new XYZ(w, -1.0, 0.0), new XYZ(w, 1.0, 0.0), XYZ.BasisZ, view);
		right.Name = "Logo Right";
		ReferencePlane top = fam.FamilyCreate.NewReferencePlane(new XYZ(-1.0, h, 0.0), new XYZ(1.0, h, 0.0), XYZ.BasisZ, view);
		top.Name = "Logo Top";
		Extrusion ex = Rectangle(fam, w, h);
		fm.AssociateElementParameterToFamilyParameter(ex.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM), mat);
		fam.Regenerate();
		// Cada cara lateral alineada (y bloqueada) a su plano
		List<PlanarFace> faces = ex.get_Geometry(new Options { ComputeReferences = true, IncludeNonVisibleObjects = true }).OfType<Solid>().SelectMany((Solid sol) => sol.Faces.OfType<PlanarFace>()).ToList();
		PlanarFace Side(XYZ n) => faces.First((PlanarFace f) => f.FaceNormal.IsAlmostEqualTo(n));
		fam.FamilyCreate.NewAlignment(view, left.GetReference(), Side(-XYZ.BasisX).Reference).IsLocked = true;
		fam.FamilyCreate.NewAlignment(view, right.GetReference(), Side(XYZ.BasisX).Reference).IsLocked = true;
		fam.FamilyCreate.NewAlignment(view, bottom.GetReference(), Side(-XYZ.BasisY).Reference).IsLocked = true;
		fam.FamilyCreate.NewAlignment(view, top.GetReference(), Side(XYZ.BasisY).Reference).IsLocked = true;
		FamilyParameter pw = fm.AddParameter(WidthParam, GroupTypeId.Geometry, SpecTypeId.Length, false);
		FamilyParameter ph = fm.AddParameter(HeightParam, GroupTypeId.Geometry, SpecTypeId.Length, false);
		ReferenceArray across = new ReferenceArray();
		across.Append(left.GetReference());
		across.Append(right.GetReference());
		fam.FamilyCreate.NewLinearDimension(view, Line.CreateBound(new XYZ(0.0, -0.5, 0.0), new XYZ(w, -0.5, 0.0)), across).FamilyLabel = pw;
		ReferenceArray up = new ReferenceArray();
		up.Append(bottom.GetReference());
		up.Append(top.GetReference());
		fam.FamilyCreate.NewLinearDimension(view, Line.CreateBound(new XYZ(-0.5, 0.0, 0.0), new XYZ(-0.5, h, 0.0)), up).FamilyLabel = ph;
		// Un tipo por tamaño, con la proporción de la imagen
		double aspect = h / w;
		FamilyType current = fm.CurrentType;
		fm.Set(pw, w);
		fm.Set(ph, h);
		foreach (double cm in SizesCm)
		{
			double sw = UnitUtils.ConvertToInternalUnits(cm, UnitTypeId.Centimeters);
			if (TypeName(sw) == TypeName(w))
			{
				continue;
			}
			fm.NewType(TypeName(sw));
			fm.Set(pw, sw);
			fm.Set(ph, sw * aspect);
		}
		fm.CurrentType = current;
		fam.Regenerate();
		return ex;
	}

	// Pinta cada tipo de la familia con la imagen, con la textura al tamaño de ese tipo (un material por tipo)
	public static void PaintAll(Document doc, RevitApp app, Family family, string image, double w0, double h0)
	{
		foreach (FamilySymbol s in family.GetFamilySymbolIds().Select((ElementId id) => doc.GetElement(id)).OfType<FamilySymbol>())
		{
			double w = s.LookupParameter(WidthParam)?.AsDouble() ?? 0.0;
			double h = s.LookupParameter(HeightParam)?.AsDouble() ?? 0.0;
			if (w <= 0.0 || h <= 0.0)
			{
				// Un tipo sin parámetros de tamaño (de una versión anterior): se mide una pieza puesta
				BoundingBoxXYZ bb = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().FirstOrDefault((FamilyInstance fi) => fi.Symbol.Id == s.Id)?.get_BoundingBox(null);
				w = (bb == null) ? w0 : Math.Max(bb.Max.X - bb.Min.X, bb.Max.Y - bb.Min.Y);
				h = (bb == null) ? h0 : (bb.Max.Z - bb.Min.Z);
			}
			Paint(doc, app, s, image, w, h, (s.Name == "Logo") ? MaterialName : (MaterialName + " " + s.Name.Replace("Logo ", "")));
		}
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
	public static Material Paint(Document doc, RevitApp app, FamilySymbol symbol, string image, double w, double h, string materialName = MaterialName)
	{
		Material mat = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().FirstOrDefault((Material m) => m.Name == materialName)
			?? (doc.GetElement(Material.Create(doc, materialName)) as Material);
		// Cada material con su propia apariencia (si la compartieran, la textura tendría un solo tamaño)
		if (!(doc.GetElement(mat.AppearanceAssetId) is AppearanceAssetElement ae) || ae.Name != materialName)
		{
			ae = AppearanceAssetElement.GetAppearanceAssetElementByName(doc, materialName) ?? NewGenericAppearance(doc, app, materialName);
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
	private static AppearanceAssetElement NewGenericAppearance(Document doc, RevitApp app, string name)
	{
		AppearanceAssetElement existing = new FilteredElementCollector(doc).OfClass(typeof(AppearanceAssetElement)).Cast<AppearanceAssetElement>()
			.FirstOrDefault((AppearanceAssetElement a) => a.GetRenderingAsset()?.FindByName(Generic.GenericDiffuse) != null);
		if (existing != null)
		{
			return existing.Duplicate(name);
		}
		Asset generic = app.GetAssets(AssetType.Appearance).FirstOrDefault((Asset a) => a.FindByName(Generic.GenericDiffuse) != null);
		if (generic == null)
		{
			throw new InvalidOperationException("couldn't find a Generic appearance to build the logo material");
		}
		return AppearanceAssetElement.Create(doc, name, generic);
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
