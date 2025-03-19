using Newtonsoft.Json.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SWFToPAM.ResourceGeneration;

public interface IResourceGenerator
{
    public void SaveCurrentAtlas();
    public void InitResources(string swfName);
    public void FinalizeResources();
    public void InsertImageIntoAtlas(Image<Rgba32> img, int imgIdx);
    public void CreateAtlasEntryForImage(string imgName, string imgResId, int imgIdx);
}
