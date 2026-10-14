using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Visual;

namespace ArtworkPlacer;

internal class TypeThumbnails
{
	private const int DecodeWidth = 256;

	private static readonly string[] PhotoWords = new string[8] { "photo", "image", "picture", "pic", "art", "print", "canvas", "poster" };

	private static readonly string[] FrameWords = new string[2] { "frame", "marco" };

	private static readonly string[] DiffuseProps = new string[11]
	{
		"generic_diffuse", "opaque_albedo", "wallpaint_color", "ceramic_color", "plasticvinyl_color", "metal_color", "hardwood_color", "stone_color", "concrete_color", "masonrycmu_color",
		"glazing_transmittance_map"
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
		if (_cache.TryGetValue(id, out var value))
		{
			return value;
		}
		Thumb thumb = new Thumb();
		try
		{
			if (_doc.GetElement(new ElementId(id)) is FamilySymbol sym)
			{
				thumb.Image = FromTypeImage(sym) ?? FromPhotoMaterial(sym, thumb) ?? FromPreview(sym);
				if (!thumb.Frame.HasValue)
				{
					thumb.Frame = FrameColor(sym);
				}
			}
		}
		catch
		{
		}
		_cache[id] = thumb;
		return thumb;
	}

	private BitmapSource FromTypeImage(FamilySymbol sym)
	{
		ElementId elementId = ((Element)sym).get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_IMAGE)?.AsElementId();
		if (elementId == null || elementId == ElementId.InvalidElementId)
		{
			return null;
		}
		using Bitmap bmp = (_doc.GetElement(elementId) as ImageType)?.GetImage();
		return ToSource(bmp);
	}

	private BitmapSource FromPreview(FamilySymbol sym)
	{
		using Bitmap bmp = sym.GetPreviewImage(new Size(256, 256));
		return ToSource(bmp);
	}

	private BitmapSource FromPhotoMaterial(FamilySymbol sym, Thumb thumb)
	{
		foreach (Tuple<string, Material> item in from m in TypeMaterials(sym)
			where !HasWord(m.Item1, FrameWords) && !HasWord(m.Item2.Name, FrameWords)
			orderby (!HasWord(m.Item1, PhotoWords) && !HasWord(m.Item2.Name, PhotoWords)) ? 1 : 0
			select m)
		{
			string text = ResolveTexture(TexturePath(item.Item2));
			if (text != null)
			{
				try
				{
					BitmapImage bitmapImage = new BitmapImage();
					bitmapImage.BeginInit();
					bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
					bitmapImage.DecodePixelWidth = 256;
					bitmapImage.UriSource = new Uri(text);
					bitmapImage.EndInit();
					bitmapImage.Freeze();
					return bitmapImage;
				}
				catch
				{
				}
			}
		}
		return null;
	}

	private System.Windows.Media.Color? FrameColor(FamilySymbol sym)
	{
		Autodesk.Revit.DB.Color color = TypeMaterials(sym).FirstOrDefault((Tuple<string, Material> m) => HasWord(m.Item1, FrameWords) || HasWord(m.Item2.Name, FrameWords))?.Item2.Color;
		if (color == null || !color.IsValid)
		{
			return null;
		}
		return System.Windows.Media.Color.FromRgb(color.Red, color.Green, color.Blue);
	}

	private IEnumerable<Tuple<string, Material>> TypeMaterials(FamilySymbol sym)
	{
		foreach (Parameter parameter in sym.Parameters)
		{
			if (parameter.StorageType == StorageType.ElementId && _doc.GetElement(parameter.AsElementId()) is Material item)
			{
				yield return Tuple.Create(parameter.Definition.Name, item);
			}
		}
	}

	private string TexturePath(Material mat)
	{
		if (!(_doc.GetElement(mat.AppearanceAssetId) is AppearanceAssetElement appearanceAssetElement))
		{
			return null;
		}
		Asset renderingAsset = appearanceAssetElement.GetRenderingAsset();
		if (renderingAsset == null)
		{
			return null;
		}
		string[] diffuseProps = DiffuseProps;
		foreach (string name in diffuseProps)
		{
			string text = BitmapOf(renderingAsset.FindByName(name));
			if (text != null)
			{
				return text;
			}
		}
		for (int j = 0; j < renderingAsset.Size; j++)
		{
			string text2 = BitmapOf(renderingAsset.Get(j));
			if (text2 != null)
			{
				return text2;
			}
		}
		return null;
	}

	private static string BitmapOf(AssetProperty prop)
	{
		if (prop == null || prop.NumberOfConnectedProperties == 0)
		{
			return null;
		}
		string text = (prop.GetSingleConnectedAsset()?.FindByName(UnifiedBitmap.UnifiedbitmapBitmap) as AssetPropertyString)?.Value;
		if (!string.IsNullOrWhiteSpace(text))
		{
			return text;
		}
		return null;
	}

	private string ResolveTexture(string stored)
	{
		if (stored == null)
		{
			return null;
		}
		foreach (string item in from s in stored.Split('|')
			select s.Trim() into s
			where s.Length > 0
			select s)
		{
			try
			{
				if (Path.IsPathRooted(item) && File.Exists(item))
				{
					return item;
				}
				string fileName = Path.GetFileName(item);
				foreach (string textureDir in _textureDirs)
				{
					string text = Path.Combine(textureDir, item);
					if (File.Exists(text))
					{
						return text;
					}
					string text2 = Path.Combine(textureDir, fileName);
					if (File.Exists(text2))
					{
						return text2;
					}
				}
			}
			catch
			{
			}
		}
		return null;
	}

	private static List<string> TextureDirs(Document doc, string revitVersion)
	{
		List<string> list = new List<string>();
		if (!string.IsNullOrEmpty(doc.PathName))
		{
			try
			{
				list.Add(Path.GetDirectoryName(doc.PathName));
			}
			catch
			{
			}
		}
		try
		{
			string directoryName = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
			string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Autodesk", "Revit", "Autodesk Revit " + revitVersion, "Revit.ini");
			string text = (File.Exists(path) ? File.ReadAllLines(path).FirstOrDefault((string l) => l.StartsWith("AdditionalRenderAppearancePaths=", StringComparison.OrdinalIgnoreCase)) : null);
			if (text != null)
			{
				foreach (string item in from s in text.Substring(text.IndexOf('=') + 1).Split('|')
					select s.Trim() into s
					where s.Length > 0
					select s)
				{
					try
					{
						list.Add(Path.GetFullPath(Path.IsPathRooted(item) ? item : Path.Combine(directoryName, item)));
					}
					catch
					{
					}
				}
			}
		}
		catch
		{
		}
		string text2 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86), "Autodesk Shared", "Materials", "Textures");
		list.Add(text2);
		string[] array = new string[3] { "1", "2", "3" };
		foreach (string path2 in array)
		{
			list.Add(Path.Combine(text2, path2, "Mats"));
		}
		return list.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
	}

	private static bool HasWord(string text, string[] words)
	{
		if (text != null)
		{
			return words.Any((string w) => text.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);
		}
		return false;
	}

	private static BitmapSource ToSource(Bitmap bmp)
	{
		if (bmp == null)
		{
			return null;
		}
		using MemoryStream memoryStream = new MemoryStream();
		bmp.Save(memoryStream, ImageFormat.Png);
		memoryStream.Position = 0L;
		BitmapImage bitmapImage = new BitmapImage();
		bitmapImage.BeginInit();
		bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
		bitmapImage.StreamSource = memoryStream;
		bitmapImage.EndInit();
		bitmapImage.Freeze();
		return bitmapImage;
	}
}
