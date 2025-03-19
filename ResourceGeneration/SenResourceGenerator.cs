using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Runtime;
using Newtonsoft.Json.Linq;
using MaxRectsBinPack;

namespace SWFToPAM.ResourceGeneration;

public class SenResourceGenerator : IResourceGenerator
{
    private MaxRectsBinPack.MaxRectsBinPack m_binPack = new(4096, 4096, false);
    private Image<Rgba32> m_binPackImage = new(4096, 4096, new Rgba32(255, 255, 255, 0));
    private int m_atlasCount = 0;
    private List<(string id, JObject obj)> m_atlasResTexEntries = new();
    private List<(string id, JObject obj)> m_atlasResEntries = new();
    private Dictionary<int, System.Drawing.Rectangle> m_rectsForImages = new();
    private string m_swfName = string.Empty;
    private JObject m_resInfoObj = new();

    public void SaveCurrentAtlas()
    {
        if (m_binPack.GetUsedRectCount() <= 0)
            return;

        var bbox = m_binPack.GetBoundingBox();
        int w = Utils.EnsureClosestMultipleOfPowerOfTwo(bbox.Width);
        int h = Utils.EnsureClosestMultipleOfPowerOfTwo(bbox.Height);
        m_binPackImage = Utils.ChangeImageDimensions(m_binPackImage, w, h);

        string atlasId = Program.Settings.ResourceFormat is ResourceFormat.SEN
            ? $"ATLASIMAGE_ATLAS_{Program.Settings.ResGroupName.ToUpperInvariant()}_{m_atlasCount.ToString("D2")}"
            : $"ATLASIMAGE_ATLAS_{Program.Settings.ResGroupName.ToUpperInvariant()}_1536_{m_atlasCount.ToString("D2")}";

        JObject atlasData = new();
        foreach (var atlasEntry in m_atlasResTexEntries)
        {
            atlasData[atlasEntry.id] = atlasEntry.obj;
        }

        m_atlasResTexEntries.Clear();
        JObject atlasInfo = null;
        string atlasPath = string.Empty;

        if (Program.Settings.ResourceFormat is ResourceFormat.SEN)
        {
            atlasPath = $"atlases/{Program.Settings.ResGroupName.ToUpperInvariant()}_{m_atlasCount.ToString("D2")}";
            atlasInfo = new JObject
            {
                ["type"] = "Image",
                ["path"] = atlasPath,
                ["dimension"] = new JObject
                {
                    ["width"] = w,
                    ["height"] = h
                },
                ["data"] = atlasData
            };
        }
        else
        {
            atlasPath = $"atlases/{Program.Settings.ResGroupName.ToUpperInvariant()}_1536_{m_atlasCount.ToString("D2")}";
            atlasInfo = new JObject
            {
                ["Type"] = "Image",
                ["Path"] = new JArray(atlasPath.Split('/')),
                ["AtlasInfo"] = new JObject
                {
                    ["Size"] = new JArray(w, h),
                    ["Image"] = atlasData
                },
            };
        }

        m_atlasResEntries.Add(new(atlasId, atlasInfo));

        //Console.WriteLine(atlasInfo.ToString());

        //m_binPackImage.Save($"{m_swfName}_{m_atlasCount}.png");
        string path = Program.Settings.ResourceFormat is ResourceFormat.SEN
            ? $"output/SEN/{atlasPath}.png"
            : $"output/SPC/{atlasPath}.png";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        m_binPackImage.Save(path);

        m_binPackImage = new(4096, 4096, new Rgba32(255, 255, 255, 0));
        m_binPack.Init(4096, 4096, false);

        m_atlasCount++;
    }

    public void InsertImageIntoAtlas(Image<Rgba32> img, int imgIdx)
    {
        var rect = m_binPack.Insert(img.Width, img.Height, FreeRectChoiceHeuristic.RectBestAreaFit);
        if (rect.Height == 0)
        {
            SaveCurrentAtlas();
        }

        Utils.PaintImageOntoImage(m_binPackImage, rect.Left, rect.Top, img);
        m_rectsForImages.Add(imgIdx, rect);
    }

    public void CreateAtlasEntryForImage(string imgName, string imgResId, int imgIdx)
    {
        JObject imgEntry = null;
        if (Program.Settings.ResourceFormat is ResourceFormat.SEN)
        {
            imgEntry = new()
            {
                ["type"] = "Image",
                ["path"] = $"images/full/{Program.Settings.ResBasePath}/{m_swfName}/{imgName}",
                ["default"] = new JObject
                {
                    ["ax"] = m_rectsForImages[imgIdx].X,
                    ["ay"] = m_rectsForImages[imgIdx].Y,
                    ["aw"] = m_rectsForImages[imgIdx].Width,
                    ["ah"] = m_rectsForImages[imgIdx].Height,
                    ["x"] = 0,
                    ["y"] = 0
                }
            };
        }
        else
        {
            string resPath = $"images/1536/full/{Program.Settings.ResBasePath}/{m_swfName}/{imgName}";
            imgEntry = new()
            {
                ["Type"] = "Image",
                ["Path"] = new JArray(resPath.Split('/')),
                ["ImageInfo"] = new JArray(
                    m_rectsForImages[imgIdx].X,
                    m_rectsForImages[imgIdx].Y,
                    m_rectsForImages[imgIdx].Width,
                    m_rectsForImages[imgIdx].Height,
                    0,
                    0)
            };
        }

        m_atlasResTexEntries.Add(new(imgResId, imgEntry!));
    }

    public void FinalizeResources()
    {
        SaveCurrentAtlas();

        if (Program.Settings.ResourceFormat is ResourceFormat.SEN)
        {
            JObject packetInfo = new();
            foreach (var atlasEntry in m_atlasResEntries)
            {
                packetInfo[atlasEntry.id] = atlasEntry.obj;
            }
            m_resInfoObj["packet"] = packetInfo;
            string outputJson = $"output/SEN/atlases/{Program.Settings.ResGroupName}.json";
            Directory.CreateDirectory(Path.GetDirectoryName(outputJson)!);
            File.WriteAllText(outputJson, m_resInfoObj.ToString());
        }
        else
        {
            JObject res = new();
            foreach (var atlasEntry in m_atlasResEntries)
            {
                res[atlasEntry.id] = atlasEntry.obj;
            }
            m_resInfoObj["Res"] = res;
            string outputJson = $"output/SPC/Include/{Program.Settings.ResGroupName}/Include/{Program.Settings.ResGroupName}_1536.json";
            Directory.CreateDirectory(Path.GetDirectoryName(outputJson)!);
            File.WriteAllText(outputJson, m_resInfoObj.ToString());
        }
    }

    public void InitResources(string swfName)
    {
        m_swfName = swfName;
        if (Program.Settings.ResourceFormat is ResourceFormat.SEN)
        {
            m_resInfoObj["type"] = "1536";
        }
        else
        {
            m_resInfoObj["Category"] = new JArray(1536, null);
        }
    }
}
