using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Visual;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using MediaColor = System.Windows.Media.Color;

namespace ArtworkPlacer
{
    // ─── MINIATURAS DE TIPOS ─────────────────────────────────────────────────
    // La vista previa de la familia suele ser solo el marco gris; la foto real está en la textura
    // del material "photo" del tipo. Orden de búsqueda:
    //   1. Type Image del tipo
    //   2. Textura del material de foto (en las mismas carpetas donde Revit busca texturas)
    //   3. Vista previa de la familia
    internal class TypeThumbnails
    {
        private const int DecodeWidth = 256;

        private static readonly string[] PhotoWords = { "photo", "image", "picture", "pic", "art", "print", "canvas", "poster" };
        private static readonly string[] FrameWords = { "frame", "marco" };

        // Propiedades de color/difuso de los esquemas de apariencia más comunes
        private static readonly string[] DiffuseProps =
        {
            "generic_diffuse", "opaque_albedo", "wallpaint_color", "ceramic_color", "plasticvinyl_color",
            "metal_color", "hardwood_color", "stone_color", "concrete_color", "masonrycmu_color", "glazing_transmittance_map"
        };

        private readonly Document _doc;
        private readonly List<string> _textureDirs;
        private readonly Dictionary<long, Thumb> _cache = new Dictionary<long, Thumb>();

        public TypeThumbnails(Document doc, string revitVersion)
        {
            _doc = doc;
            _textureDirs = TextureDirs(doc, revitVersion);
        }

        public Thumb Get(long id)
        {
            if (_cache.TryGetValue(id, out Thumb cached)) return cached;

            var thumb = new Thumb();
            try
            {
                if (_doc.GetElement(new ElementId(id)) is FamilySymbol sym)
                {
                    thumb.Image = FromTypeImage(sym) ?? FromPhotoMaterial(sym, thumb) ?? FromPreview(sym);
                    if (thumb.Frame == null) thumb.Frame = FrameColor(sym);
                }
            }
            catch { }   // una miniatura que falla no debe romper el diálogo

            _cache[id] = thumb;
            return thumb;
        }

        private BitmapSource FromTypeImage(FamilySymbol sym)
        {
            ElementId imgId = sym.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_IMAGE)?.AsElementId();
            if (imgId == null || imgId == ElementId.InvalidElementId) return null;
            using (System.Drawing.Bitmap bmp = (_doc.GetElement(imgId) as ImageType)?.GetImage())
                return ToSource(bmp);
        }

        private BitmapSource FromPreview(FamilySymbol sym)
        {
            using (System.Drawing.Bitmap bmp = sym.GetPreviewImage(new System.Drawing.Size(DecodeWidth, DecodeWidth)))
                return ToSource(bmp);
        }

        // Materiales del tipo con textura: primero los que se llaman como foto, nunca el marco
        private BitmapSource FromPhotoMaterial(FamilySymbol sym, Thumb thumb)
        {
            var candidates = TypeMaterials(sym)
                .Where(m => !HasWord(m.Item1, FrameWords) && !HasWord(m.Item2.Name, FrameWords))
                .OrderBy(m => HasWord(m.Item1, PhotoWords) || HasWord(m.Item2.Name, PhotoWords) ? 0 : 1);

            foreach (var m in candidates)
            {
                string file = ResolveTexture(TexturePath(m.Item2));
                if (file == null) continue;
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = DecodeWidth;
                    bmp.UriSource = new Uri(file);
                    bmp.EndInit();
                    bmp.Freeze();
                    return bmp;
                }
                catch { }
            }
            return null;
        }

        private MediaColor? FrameColor(FamilySymbol sym)
        {
            var frame = TypeMaterials(sym).FirstOrDefault(m => HasWord(m.Item1, FrameWords) || HasWord(m.Item2.Name, FrameWords));
            Color c = frame?.Item2.Color;
            return c != null && c.IsValid ? MediaColor.FromRgb(c.Red, c.Green, c.Blue) : (MediaColor?)null;
        }

        // (nombre del parámetro, material) para cada parámetro de tipo que apunta a un material
        private IEnumerable<Tuple<string, Material>> TypeMaterials(FamilySymbol sym)
        {
            foreach (Parameter p in sym.Parameters)
            {
                if (p.StorageType != StorageType.ElementId) continue;
                if (_doc.GetElement(p.AsElementId()) is Material mat)
                    yield return Tuple.Create(p.Definition.Name, mat);
            }
        }

        private string TexturePath(Material mat)
        {
            if (!(_doc.GetElement(mat.AppearanceAssetId) is AppearanceAssetElement ae)) return null;
            Asset asset = ae.GetRenderingAsset();
            if (asset == null) return null;

            foreach (string name in DiffuseProps)
            {
                string path = BitmapOf(asset.FindByName(name));
                if (path != null) return path;
            }
            for (int i = 0; i < asset.Size; i++)
            {
                string path = BitmapOf(asset.Get(i));
                if (path != null) return path;
            }
            return null;
        }

        private static string BitmapOf(AssetProperty prop)
        {
            if (prop == null || prop.NumberOfConnectedProperties == 0) return null;
            Asset connected = prop.GetSingleConnectedAsset();
            string value = (connected?.FindByName(UnifiedBitmap.UnifiedbitmapBitmap) as AssetPropertyString)?.Value;
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        // La ruta guardada puede ser absoluta, relativa o solo el nombre; puede traer varias separadas por "|"
        private string ResolveTexture(string stored)
        {
            if (stored == null) return null;
            foreach (string raw in stored.Split('|').Select(s => s.Trim()).Where(s => s.Length > 0))
            {
                try
                {
                    if (Path.IsPathRooted(raw) && File.Exists(raw)) return raw;
                    string name = Path.GetFileName(raw);
                    foreach (string dir in _textureDirs)
                    {
                        string a = Path.Combine(dir, raw);
                        if (File.Exists(a)) return a;
                        string b = Path.Combine(dir, name);
                        if (File.Exists(b)) return b;
                    }
                }
                catch { }
            }
            return null;
        }

        // Carpeta del proyecto + "Additional render appearance paths" de Revit.ini + texturas de Autodesk
        private static List<string> TextureDirs(Document doc, string revitVersion)
        {
            var dirs = new List<string>();
            if (!string.IsNullOrEmpty(doc.PathName))
            {
                try { dirs.Add(Path.GetDirectoryName(doc.PathName)); } catch { }
            }

            try
            {
                string revitDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
                string ini = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Autodesk", "Revit", $"Autodesk Revit {revitVersion}", "Revit.ini");
                string line = File.Exists(ini)
                    ? File.ReadAllLines(ini).FirstOrDefault(l => l.StartsWith("AdditionalRenderAppearancePaths=", StringComparison.OrdinalIgnoreCase))
                    : null;
                if (line != null)
                {
                    foreach (string p in line.Substring(line.IndexOf('=') + 1).Split('|').Select(s => s.Trim()).Where(s => s.Length > 0))
                    {
                        try { dirs.Add(Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(revitDir, p))); } catch { }
                    }
                }
            }
            catch { }

            string shared = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
                "Autodesk Shared", "Materials", "Textures");
            dirs.Add(shared);
            foreach (string sub in new[] { "1", "2", "3" }) dirs.Add(Path.Combine(shared, sub, "Mats"));

            return dirs.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool HasWord(string text, string[] words) =>
            text != null && words.Any(w => text.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);

        private static BitmapSource ToSource(System.Drawing.Bitmap bmp)
        {
            if (bmp == null) return null;
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                ms.Position = 0;
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.StreamSource = ms;
                img.EndInit();
                img.Freeze();
                return img;
            }
        }
    }
}
