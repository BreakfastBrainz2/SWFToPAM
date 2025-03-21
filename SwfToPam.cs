using MaxRectsBinPack;
using Newtonsoft.Json.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SwfLib;
using SwfLib.Data;
using SwfLib.Shapes.FillStyles;
using SwfLib.Shapes.Records;
using SwfLib.Tags;
using SwfLib.Tags.BitmapTags;
using SwfLib.Tags.ControlTags;
using SwfLib.Tags.DisplayListTags;
using SwfLib.Tags.ShapeTags;
using SWFToPAM.ResourceGeneration;
using System.Diagnostics;
using System.Numerics;
using System.Text;

namespace SWFToPAM;

struct PAImage
{
    public int OrigWidth;
    public int OrigHeight;
    public int Cols;
    public int Rows;
    public string ImageName;
    public int DrawMode;
    public Matrix3x2 Transform;

    public PAImage()
    {
        Transform = Matrix3x2.Identity;
    }
}

struct ImageInfo
{
    public int Width;
    public int Height;
    public string ImgId;
}

struct PACommand
{
    public string Command;
    public string Param;
}

[Flags]
public enum FrameFlags
{
    HAS_REMOVES = 1 << 0,
    HAS_ADDS = 1 << 1,
    HAS_MOVES = 1 << 2,
    HAS_FRAME_NAME = 1 << 3,
    HAS_STOP = 1 << 4,
    HAS_COMMANDS = 1 << 5
};

[Flags]
public enum MoveFlags
{
    HAS_FRAMENUM = 1 << 10,
    HAS_LONGCOORDS = 1 << 11,
    HAS_MATRIX = 1 << 12,
    HAS_COLOR = 1 << 13,
    HAS_ROTATE = 1 << 14,
    HAS_SRCRECT = 1 << 15
};

public class SwfToPam
{
    private Dictionary<int, string> m_symbolNameMap = new();
    private Dictionary<int, ImageInfo> m_imageSymbolIdToImgInfo = new();
    private Dictionary<int, (PAImage img, int idx)> m_imageSymbolIdToPAImage = new();
    private Dictionary<int, int> m_spriteSymbolIdToPASpriteIdx = new();
    private Dictionary<int, int> m_shapeIdToImageSymbolId = new();
    private Dictionary<int, int> m_depthToObjIdxMap = new();
    private Dictionary<int, SwfMatrix> m_depthToMatrixMap = new();
    private Dictionary<int, ColorTransformRGBA> m_depthToColorTransMap = new();
    private Dictionary<string, int> m_endFrameForAnimations = new();
    private Dictionary<string, Dictionary<int, List<PACommand>>> m_actionsForSprites = new();
    private Dictionary<string, List<int>> m_stopsForSprites = new();
    private HashSet<string> m_imageNamePool = new();
    private List<(string imgPath, Image<Rgba32> img)> m_imagesToExport = new();
    private List<int> m_spriteSymbolIds = new();
    private List<int> m_processedBitmapIds = new();
    private List<BitmapBaseTag> m_bitmapTags = new();
    private string m_swfName = string.Empty;
    private string m_topFolderName = string.Empty;
    private string m_curAnim = string.Empty;
    private string m_imgOutputFolder = string.Empty;
    private int m_imgIdx = 0;
    private int m_spriteIdx = 0;
    private int m_bitmapCount = 0;
    private bool m_hasActionsJson = false;
    private ExtendedBinaryWriter m_writer;
    private IResourceGenerator m_resourceGenerator;

    private static readonly int MAIN_SPRITE_ID = -1;

    #region Helper Methods

    private string CreateNameForImage(int width, int height)
    {
        string name = $"{m_swfName}_{width}x{height}";
        int iter = 2;
        while (m_imageNamePool.Contains(name))
        {
            name = $"{m_swfName}_{width}x{height}_{iter}";
            iter++;
        }

        return name;
    }

    private string CreateResourceNameForImage(string imgName)
    {
        return $"IMAGE_{m_topFolderName.ToUpperInvariant()}_{m_swfName.ToUpperInvariant()}_{imgName.ToUpperInvariant()}";
    }

    #endregion

    #region Matrix Helpers

    private Matrix3x2 FlashMatrixToStandardMatrix(SwfMatrix flashMtx, bool isTwips)
    {
        Matrix3x2 mtx = new();
        if (isTwips)
        {
            mtx.M11 = (float)flashMtx.ScaleX / 20.0f;
            mtx.M12 = (float)flashMtx.RotateSkew0 / 20.0f;
            mtx.M21 = (float)flashMtx.RotateSkew1 / 20.0f;
            mtx.M22 = (float)flashMtx.ScaleY / 20.0f;
        }
        else
        {
            mtx.M11 = (float)flashMtx.ScaleX;
            mtx.M12 = (float)flashMtx.RotateSkew0 / 20.0f;
            mtx.M21 = (float)flashMtx.RotateSkew1 / 20.0f;
            mtx.M22 = (float)flashMtx.ScaleY;
        }
        mtx.M31 = flashMtx.TranslateX / 20.0f;
        mtx.M32 = flashMtx.TranslateY / 20.0f;
        return mtx;
    }

    private float DecomposeRotation(Matrix3x2 matrix, bool hasScale)
    {
        if (hasScale)
        {
            // Extract matrix elements
            float m11 = matrix.M11;
            float m12 = matrix.M12;
            float m21 = matrix.M21;
            float m22 = matrix.M22;

            // Normalize the first column to remove scaling
            float scaleX = MathF.Sqrt(m11 * m11 + m21 * m21);
            float normM11 = m11 / scaleX;
            float normM21 = m21 / scaleX;

            // Normalize the second column to remove scaling
            float scaleY = MathF.Sqrt(m12 * m12 + m22 * m22);
            float normM12 = m12 / scaleY;
            float normM22 = m22 / scaleY;

            // Calculate the angle using Atan2 on the normalized values
            float angle = MathF.Atan2(normM21, normM11);

            return angle;
        }
        else
        {
            // If no scaling, directly compute the angle
            float angle = MathF.Atan2(matrix.M21, matrix.M11);
            return angle;
        }
    }

    private bool IsRotationMatrix(SwfMatrix matrix)
    {
        /*
        float tolerance = 0.08f;

        // Check if M31 and M32 are near zero (translation)
        //bool translationIsZero = Math.Abs(matrix.TranslateX) < tolerance && Math.Abs(matrix.TranslateY) < tolerance;

        // Check if M11 and M22 are close to 1 (scaling should be 1 in pure rotation)
        bool isScalingIdentity = Math.Abs(matrix.ScaleX - 1.0f) < tolerance && Math.Abs(matrix.ScaleY - 1.0f) < tolerance;

        // Check if M12 and M21 are near zero (no shear)
        bool isShearZero = Math.Abs(matrix.RotateSkew0) < tolerance && Math.Abs(matrix.RotateSkew1) < tolerance;

        // If translation is zero, scaling is identity, and shear is zero, then it's a pure rotation
        //if (isScalingIdentity || isShearZero)
            //s_rotCount++;

        return isScalingIdentity && isShearZero;
        */


        /*
        float M11 = (float)matrix.ScaleX;
        float M12 = (float)matrix.RotateSkew0;
        float M21 = (float)matrix.RotateSkew1;
        float M22 = (float)matrix.ScaleY;

        float a = M11;
        float b = M21;
        float c = M12;
        float d = M22;
        const double tol = 0.01;
        bool isOrthonormal =
        Math.Abs((a * a + b * b) - 1.0f) < tol &&
        Math.Abs((c * c + d * d) - 1.0f) < tol &&
        Math.Abs(a * c + b * d) < tol;

        // Check determinant condition (should be ±1)
        float determinant = a * d - b * c;
        bool isDeterminantValid = Math.Abs(Math.Abs(determinant) - 1.0f) < tol;

        if (isOrthonormal && isDeterminantValid)
        {
            //s_rotCount++;
            return true;
        }

        return false;
        */
        /*
        float M11 = (float)matrix.ScaleX;
        float M12 = (float)matrix.RotateSkew0;
        float M21 = (float)matrix.RotateSkew1;
        float M22 = (float)matrix.ScaleY;

        float a = M11;
        float b = M21;
        float c = M12;
        float d = M22;

        const double tol = 0.01;

        // Check orthonormality: columns must be unit vectors and orthogonal
        bool isOrthonormal =
            Math.Abs((a * a + b * b) - 1.0f) < tol && // First column is unit length
            Math.Abs((c * c + d * d) - 1.0f) < tol && // Second column is unit length
            Math.Abs(a * c + b * d) < tol;            // Columns are orthogonal

        // Check determinant condition (should be ±1)
        float determinant = a * d - b * c;
        bool isDeterminantValid = Math.Abs(Math.Abs(determinant) - 1.0f) < tol;

        // A valid rotation matrix (including reflection) must be orthonormal and have determinant ±1
        return isOrthonormal && isDeterminantValid;
        */
        float M11 = (float)matrix.ScaleX;
        float M12 = (float)matrix.RotateSkew0;
        float M21 = (float)matrix.RotateSkew1;
        float M22 = (float)matrix.ScaleY;

        float a = M11;
        float b = M21;
        float c = M12;
        float d = M22;

        const double tol = 0.01;

        // Check orthonormality
        bool isOrthonormal =
            Math.Abs((a * a + b * b) - 1.0f) < tol &&
            Math.Abs((c * c + d * d) - 1.0f) < tol &&
            Math.Abs(a * c + b * d) < tol;

        // Check determinant condition (must be +1 for pure rotation)
        float determinant = a * d - b * c;
        bool isDeterminantValid = Math.Abs(determinant - 1.0f) < tol; // Only allow determinant ≈ 1

        return isOrthonormal && isDeterminantValid;
    }

    #endregion

    #region SWF Object Parsing

    private void ParseBitmapSymbol(BitmapBaseTag baseTag)
    {
        if (baseTag is DefineBitsLossless2Tag bmpTag)
        {
            var lineSize = bmpTag.BitmapWidth;

            int bmpWidth = bmpTag.BitmapWidth;
            int bmpHeight = bmpTag.BitmapHeight;
            var data = SwfZip.DecompressZlib(bmpTag.ZlibBitmapData);

            Rgba32[] pixels = new Rgba32[bmpWidth * bmpHeight];

            for (var y = 0; y < bmpHeight; y++)
            {
                for (var x = 0; x < bmpWidth; x++)
                {
                    var ind = (y * lineSize + x) * 4;

                    var alpha = data[ind];
                    var red = data[ind + 1];
                    var green = data[ind + 2];
                    var blue = data[ind + 3];

                    if (alpha == 0)
                    {
                        pixels[y * bmpWidth + x] = new Rgba32(0, 0, 0, 0);
                    }
                    else
                    {
                        red = (byte)Math.Clamp((red * 255) / alpha, 0, 255);
                        green = (byte)Math.Clamp((green * 255) / alpha, 0, 255);
                        blue = (byte)Math.Clamp((blue * 255) / alpha, 0, 255);

                        pixels[y * bmpWidth + x] = new Rgba32(red, green, blue, alpha);
                    }
                }
            }

            Image<Rgba32> bmpAsImg = Image.LoadPixelData<Rgba32>(pixels, bmpWidth, bmpHeight);
            if(Program.Settings.ScaleImages)
            {
                Utils.ResizeImage(bmpAsImg, Utils.PAMRound(bmpWidth / Program.Settings.ImageScaleFactor), Utils.PAMRound(bmpHeight / Program.Settings.ImageScaleFactor));
            }

            int imgW = bmpAsImg.Width;
            int imgH = bmpAsImg.Height;

            m_resourceGenerator.InsertImageIntoAtlas(bmpAsImg, m_imgIdx);

            if(Program.Settings.ScaleImages)
            {
                float scaledW_f = imgW * Program.Settings.ImageScaleFactor;
                float scaledH_f = imgH * Program.Settings.ImageScaleFactor;
                imgW = Utils.PAMRound(scaledW_f);
                imgH = Utils.PAMRound(scaledH_f);
            }
            string imgName = CreateNameForImage(imgW, imgH);

            if(Program.Settings.ExportImages)
            {
                string filePath = Path.Combine(m_imgOutputFolder, imgName + ".png");
                //bmpAsImg.Save(filePath);
                m_imagesToExport.Add(new(filePath, bmpAsImg));
            }

            ImageInfo imgInfo = new();
            imgInfo.Width = imgW;
            imgInfo.Height = imgH;
            imgInfo.ImgId = imgName;

            m_imageSymbolIdToImgInfo.Add(baseTag.CharacterID, imgInfo);
            m_imageNamePool.Add(imgName);
        }
    }

    private void ParseFillStyles(IList<FillStyleRGB> fillStyles, int shapeId)
    {
        foreach (var fillStyle in fillStyles)
        {
            if (fillStyle is BitmapFillStyleRGB bitmapFill)
            {
                if (bitmapFill.BitmapID != ushort.MaxValue)
                {
                    Matrix3x2 mtx = FlashMatrixToStandardMatrix(bitmapFill.BitmapMatrix, true);
                    if(Program.Settings.ScaleImages)
                    {
                        mtx = Matrix3x2Extensions.CreateScale(Program.Settings.ImageScaleFactor, Program.Settings.ImageScaleFactor, new PointF(0, 0)) * mtx;
                    }

                    if(!m_processedBitmapIds.Contains(bitmapFill.BitmapID))
                    {
                        ParseBitmapSymbol(m_bitmapTags.Find(t => t.CharacterID == bitmapFill.BitmapID)!);
                        m_processedBitmapIds.Add(bitmapFill.BitmapID);
                    }

                    ImageInfo infoForImg = m_imageSymbolIdToImgInfo[bitmapFill.BitmapID];
                    PAImage img = new();
                    img.Transform = mtx;
                    if(Program.Settings.ScaleImages)
                    {
                        img.OrigWidth = -1;
                        img.OrigHeight = -1;
                    }
                    else
                    {
                        img.OrigWidth = infoForImg.Width;
                        img.OrigHeight = infoForImg.Height;
                    }
                    img.ImageName = infoForImg.ImgId;
                    m_imageSymbolIdToPAImage.Add(bitmapFill.BitmapID, new(img, m_imgIdx));
                    m_shapeIdToImageSymbolId.Add(shapeId, bitmapFill.BitmapID);

                    // image name|image resource id
                    string imgName = infoForImg.ImgId;
                    string imgResId = CreateResourceNameForImage(imgName);
                    string imgId = $"{imgName}|{imgResId}";

                    m_resourceGenerator.CreateAtlasEntryForImage(imgName, imgResId, m_imgIdx);

                    m_writer.WriteUShortSizedString(imgId);

                    if(Program.Settings.ScaleImages)
                    {
                        m_writer.Write((short)infoForImg.Width);
                        m_writer.Write((short)infoForImg.Height);
                        m_writer.Write((int)((mtx.M11 / Program.Settings.ImageScaleFactor) * 1310720));
                        m_writer.Write((int)((mtx.M12 / Program.Settings.ImageScaleFactor) * 1310720));
                        m_writer.Write((int)((mtx.M21 / Program.Settings.ImageScaleFactor) * 1310720));
                        m_writer.Write((int)((mtx.M22 / Program.Settings.ImageScaleFactor) * 1310720));
                    }
                    else
                    {
                        m_writer.Write((short)-1);
                        m_writer.Write((short)-1);
                        m_writer.Write((int)((mtx.M11) * 1310720));
                        m_writer.Write((int)((mtx.M12) * 1310720));
                        m_writer.Write((int)((mtx.M21) * 1310720));
                        m_writer.Write((int)((mtx.M22) * 1310720));
                    }
                    m_writer.Write((short)bitmapFill.BitmapMatrix.TranslateX);
                    m_writer.Write((short)bitmapFill.BitmapMatrix.TranslateY);

                    m_imgIdx++;
                }
            }
        }
    }

    private void WriteSprite(int spriteId, float framerate, IList<SwfTagBase> tags)
    {
        m_depthToObjIdxMap.Clear();
        m_depthToMatrixMap.Clear();
        //m_depthToColorTransMap.Clear();

        VerifyFormatting(tags);

        if(spriteId != MAIN_SPRITE_ID && !m_symbolNameMap.ContainsKey(spriteId))
        {
            throw new InvalidDataException($"Sprite with id {spriteId} has no name assigned. This is not allowed.");
        }
        m_writer.WriteUShortSizedString(spriteId == MAIN_SPRITE_ID ? "" : m_symbolNameMap[spriteId]);
        m_writer.WriteUShortSizedString(""); // second name field, not sure why this exists
        m_writer.Write(Convert.ToInt32(framerate) * 65536);

        int frameCount = tags.OfType<ShowFrameTag>().Count();
        m_writer.Write((ushort)frameCount);
        // Work area start and duration
        m_writer.Write((ushort)0);
        if (frameCount == 0)
            m_writer.Write((ushort)0);
        else
            m_writer.Write((ushort)(frameCount - 1));

        if (frameCount > 0)
        {
            List<PlaceObject2Tag> symbolPlacesSortedByDepth = tags.OfType<PlaceObject2Tag>().Where(t => t.HasCharacter).OrderBy(t => t.Depth).ToList();
            Dictionary<PlaceObject2Tag, int> objIdxForSymbolPlaces = new();
            for (int i = 0; i < symbolPlacesSortedByDepth.Count; i++)
            {
                objIdxForSymbolPlaces.Add(symbolPlacesSortedByDepth[i], i);
            }

            int frameIdx = 0;

            long frameFlagPos = m_writer.BaseStream.Position;
            FrameFlags frameFlags = 0;
            m_writer.Write((byte)frameFlags); // temp - flags

            List<SwfTagBase> tagsForThisFrame = new List<SwfTagBase>();
            foreach (var tag in tags)
            {
                tagsForThisFrame.Add(tag);
                if (tag is ShowFrameTag showFrame)
                {
                    // We've hit the end of a frame, now write it out to the file
                    var removeTags = tagsForThisFrame.OfType<RemoveObject2Tag>().ToList();
                    // Need to write symbol replacements as removes since PAM does not account for this
                    var replaceTags = tagsForThisFrame.OfType<PlaceObject2Tag>().Where(t => t.HasCharacter && m_depthToObjIdxMap.ContainsKey(t.Depth) && !removeTags.Any(f => f.Depth == t.Depth)).ToList();

                    int removeCount = removeTags.Count + replaceTags.Count;
                    if (removeCount > 0)
                    {
                        frameFlags |= FrameFlags.HAS_REMOVES;
                        if (removeCount >= byte.MaxValue)
                        {
                            m_writer.Write(byte.MaxValue);
                            m_writer.Write((ushort)removeCount);
                        }
                        else
                        {
                            m_writer.Write((byte)removeCount);
                        }

                        foreach (var remove in removeTags)
                        {
                            int removeIdx = m_depthToObjIdxMap[remove.Depth];
                            if (removeIdx >= 2047)
                            {
                                m_writer.Write(ushort.MaxValue);
                                m_writer.Write(removeIdx);
                            }
                            else
                            {
                                m_writer.Write((ushort)removeIdx);
                            }

                            m_depthToObjIdxMap.Remove(remove.Depth);
                            m_depthToMatrixMap.Remove(remove.Depth);
                            //m_depthToColorTransMap.Remove(remove.Depth);
                        }

                        foreach(var replace in replaceTags)
                        {
                            int removeIdx = m_depthToObjIdxMap[replace.Depth];
                            if(removeIdx >= 2047)
                            {
                                m_writer.Write(ushort.MaxValue);
                                m_writer.Write(removeIdx);
                            }
                            else
                            {
                                m_writer.Write((ushort)removeIdx);
                            }

                            m_depthToObjIdxMap.Remove(replace.Depth);
                        }
                    }

                    var addTags = tagsForThisFrame.OfType<PlaceObject2Tag>().Where(t => t.HasCharacter).ToList();

                    if (addTags.Count > 0)
                    {
                        frameFlags |= FrameFlags.HAS_ADDS;
                        if (addTags.Count >= byte.MaxValue)
                        {
                            m_writer.Write(byte.MaxValue);
                            m_writer.Write((ushort)addTags.Count);
                        }
                        else
                        {
                            m_writer.Write((byte)addTags.Count);
                        }

                        foreach (var add in addTags)
                        {
                            //if (m_depthToObjIdxMap.ContainsKey(add.Depth))
                            {
                                //s_depthToObjIdxMap[add.Depth] = objIdx;
                                //m_depthToObjIdxMap[add.Depth] = objIdxForSymbolPlaces[add];
                            }
                            //else
                            {
                                //s_depthToObjIdxMap.Add(add.Depth, objIdx);
                                m_depthToObjIdxMap.Add(add.Depth, objIdxForSymbolPlaces[add]);
                            }

                            if (m_depthToMatrixMap.ContainsKey(add.Depth))
                            {
                                m_depthToMatrixMap[add.Depth] = add.Matrix;
                            }
                            else
                            {
                                m_depthToMatrixMap.Add(add.Depth, add.Matrix);
                            }

                            long packedDataPos = m_writer.Position;
                            ushort packedData = 0;
                            m_writer.Write(packedData);

                            int objIdx = objIdxForSymbolPlaces[add];
                            packedData |= (ushort)(objIdx >= 2047 ? 2047 : objIdx & 0x7FF);
                            if (objIdx >= 2047)
                                m_writer.Write(objIdx);

                            bool isSprite = m_spriteSymbolIds.Contains(add.CharacterID);//s_spriteSymbolIdToPASpriteDef.ContainsKey(add.CharacterID);
                            if (isSprite)
                            {
                                packedData |= 0x8000;
                            }

                            int resNum = isSprite
                            ? m_spriteSymbolIdToPASpriteIdx[add.CharacterID]
                            : m_imageSymbolIdToPAImage[m_shapeIdToImageSymbolId[add.CharacterID]].idx;

                            if (resNum >= byte.MaxValue)
                            {
                                m_writer.Write(byte.MaxValue);
                                m_writer.Write((ushort)resNum);
                            }
                            else
                            {
                                m_writer.Write((byte)resNum);
                            }

                            m_writer.StepIn(packedDataPos);
                            m_writer.Write(packedData);
                            m_writer.StepOut();

                            objIdx++;
                        }
                    }

                    var moveTags = tagsForThisFrame.OfType<PlaceObject2Tag>().Where(t => t.Move || t.HasColorTransform || t.HasMatrix).ToList();
                    if (moveTags.Count > 0)
                    {
                        frameFlags |= FrameFlags.HAS_MOVES;

                        if (moveTags.Count >= byte.MaxValue)
                        {
                            m_writer.Write(byte.MaxValue);
                            m_writer.Write((ushort)moveTags.Count);
                        }
                        else
                        {
                            m_writer.Write((byte)moveTags.Count);
                        }

                        foreach (var move in moveTags)
                        {
                            long packedDataPos = m_writer.Position;
                            ushort packedData = 0;
                            m_writer.Write(packedData);

                            int moveObjIdx = m_depthToObjIdxMap[move.Depth];
                            packedData |= (ushort)(moveObjIdx >= 0x3FF ? 0x3FF : moveObjIdx & 0x3FF);
                            if (moveObjIdx >= 0x3FF)
                                m_writer.Write(moveObjIdx);

                            SwfMatrix moveMtx = move.HasMatrix ? move.Matrix : m_depthToMatrixMap[move.Depth];

                            if (move.HasMatrix)
                            {
                                // PAM requires rotation/matrix and translation data for every move
                                m_depthToMatrixMap[move.Depth] = move.Matrix;
                            }

                            Matrix3x2 mtx = FlashMatrixToStandardMatrix(moveMtx, true);
                            if (IsRotationMatrix(move.Matrix))
                            {
                                //s_rotCount++;
                                packedData |= 0x4000;
                                m_writer.Write((short)(-DecomposeRotation(mtx, moveMtx.HasScale) * 1000));
                            }
                            else
                            {
                                packedData |= 0x1000;
                                int p1 = (int)(moveMtx.ScaleX * 65536);
                                int p2 = (int)(moveMtx.RotateSkew1 * 65536);
                                int p3 = (int)(moveMtx.RotateSkew0 * 65536);
                                int p4 = (int)(moveMtx.ScaleY * 65536);
                                m_writer.Write(p1);
                                m_writer.Write(p2);
                                m_writer.Write(p3);
                                m_writer.Write(p4);
                            }

                            int tX = moveMtx.TranslateX;
                            int tY = moveMtx.TranslateY;
                            /*
                            if(Program.Settings.ScaleImages && Program.Settings.ScaleTransforms)
                            {
                                float origTx = tX / 20.0f;
                                float origTy = tY / 20.0f;
                                float scaledTx = origTx / Program.Settings.ImageScaleFactor;
                                float scaledTy = origTy / Program.Settings.ImageScaleFactor;
                                tX = (int)(scaledTx * 20.0f);
                                tY = (int)(scaledTy * 20.0f);
                            }
                            */

                            if (tX > ushort.MaxValue || tY > ushort.MaxValue)
                            {
                                packedData |= 0x800;
                                m_writer.Write((int)tX);
                                m_writer.Write((int)tY);
                            }
                            else
                            {
                                m_writer.Write((short)tX);
                                m_writer.Write((short)tY);
                            }

                            if (move.HasColorTransform)
                            {
                                short r = 255;
                                short g = 255;
                                short b = 255;
                                short a = 255;

                                if(move.ColorTransform.HasMultTerms)
                                {
                                    r = move.ColorTransform.RedMultTerm;
                                    g = move.ColorTransform.GreenMultTerm;
                                    b = move.ColorTransform.BlueMultTerm;
                                    a = move.ColorTransform.AlphaMultTerm;

                                    /*
                                    if (m_depthToColorTransMap.ContainsKey(move.Depth))
                                        m_depthToColorTransMap[move.Depth] = move.ColorTransform;
                                    else
                                        m_depthToColorTransMap.Add(move.Depth, move.ColorTransform);
                                    */
                                }
                                /*
                                else
                                {
                                    r = m_depthToColorTransMap[move.Depth].RedMultTerm;
                                    g = m_depthToColorTransMap[move.Depth].GreenMultTerm;
                                    b = m_depthToColorTransMap[move.Depth].BlueMultTerm;
                                    a = m_depthToColorTransMap[move.Depth].AlphaMultTerm;
                                }
                                */

                                packedData |= 0x2000;
                                m_writer.Write((byte)Math.Clamp(r, (short)0, (short)255));
                                m_writer.Write((byte)Math.Clamp(g, (short)0, (short)255));
                                m_writer.Write((byte)Math.Clamp(b, (short)0, (short)255));
                                m_writer.Write((byte)Math.Clamp(a, (short)0, (short)255));
                            }

                            m_writer.StepIn(packedDataPos);
                            m_writer.Write(packedData);
                            m_writer.StepOut();
                        }
                    }

                    var labelTag = tagsForThisFrame.OfType<FrameLabelTag>().ToList();
                    if (labelTag.Count > 0)
                    {
                        Debug.Assert(labelTag.Count == 1);
                        frameFlags |= FrameFlags.HAS_FRAME_NAME;
                        string animLabel = labelTag.First().Name;
                        m_writer.WriteUShortSizedString(animLabel);
                        m_curAnim = animLabel;
                    }

                    tagsForThisFrame.Clear();

                    if(m_hasActionsJson)
                    {
                        string spriteName = spriteId == MAIN_SPRITE_ID ? "main" : m_symbolNameMap[spriteId];
                        if (m_stopsForSprites.ContainsKey(spriteName))
                        {
                            if (m_stopsForSprites[spriteName].Contains(frameIdx))
                            {
                                frameFlags |= FrameFlags.HAS_STOP;
                            }
                        }

                        if (m_actionsForSprites.ContainsKey(spriteName) && m_actionsForSprites[spriteName].ContainsKey(frameIdx))
                        {
                            List<PACommand> commands = m_actionsForSprites[spriteName][frameIdx];
                            if (commands.Count > 0)
                            {
                                frameFlags |= FrameFlags.HAS_COMMANDS;

                                m_writer.Write((byte)commands.Count);

                                foreach (var cmd in commands)
                                {
                                    m_writer.WriteUShortSizedString(cmd.Command);
                                    m_writer.WriteUShortSizedString(cmd.Param);
                                }
                            }
                        }
                    }
                    else
                    {
                        // no actions json was provided - automatically calculate stop frames
                        if (spriteId == MAIN_SPRITE_ID)
                        {
                            if (m_endFrameForAnimations[m_curAnim] == frameIdx)
                                frameFlags |= FrameFlags.HAS_STOP;
                        }
                        /*
                        else
                        {
                            if (frameCount > 1 && frameIdx + 1 == frameCount)
                            {
                                frameFlags |= FrameFlags.HAS_STOP;
                            }
                        }
                        */
                    }

                    m_writer.StepIn(frameFlagPos);
                    m_writer.Write((byte)frameFlags);
                    m_writer.StepOut();

                    frameIdx++;
                    if (frameIdx < frameCount)
                    {
                        frameFlagPos = m_writer.Position;
                        frameFlags = 0;
                        m_writer.Write((byte)frameFlags);
                    }
                }
            }
        }

        m_spriteSymbolIdToPASpriteIdx.Add(spriteId, m_spriteIdx);
        m_spriteIdx++;
    }

    private void VerifyFormatting(IList<SwfTagBase> tags)
    {
        foreach (var placeTag in tags.OfType<PlaceObjectBaseTag>())
        {
            if (placeTag is not PlaceObject2Tag)
            {
                throw new InvalidDataException("Animation contains symbol instances with filters applied. Filters are not supported by PopAnims.");
            }
        }

        foreach(var shapeTag in tags.OfType<ShapeBaseTag>())
        {
            if(shapeTag is not DefineShapeTag)
            {
                throw new InvalidDataException("Animation contains vectors or combined bitmap instances. This is not supported by PopAnims.");
            }
        }

        if(tags.OfType<DefineBitsJpegTagBase>().Count() > 0)
        {
            throw new InvalidDataException("Animation contains images stored as JPEG. Store them as PNG instead.");
        }
    }

    #endregion

    private void PreprocessSWF(SwfFile swf)
    {
        VerifyFormatting(swf.Tags);

        var symbolClassTag = swf.Tags.OfType<SymbolClassTag>().First();
        foreach (var symbolRef in symbolClassTag.References)
        {
            m_symbolNameMap.Add(symbolRef.SymbolID, symbolRef.SymbolName);
        }

        foreach (var defineSprite in swf.Tags.OfType<DefineSpriteTag>())
        {
            m_spriteSymbolIds.Add(defineSprite.SpriteID);
        }

        var frameLabelData = swf.Tags.OfType<DefineSceneAndFrameLabelDataTag>().First();
        for (int i = 0; i < frameLabelData.Frames.Count; i++)
        {
            if (i == frameLabelData.Frames.Count - 1)
            {
                m_endFrameForAnimations.Add(frameLabelData.Frames[i].Label, swf.Header.FrameCount - 1);
                continue;
            }

            int nextFrameStart = (int)frameLabelData.Frames[i + 1].FrameNumber;
            m_endFrameForAnimations.Add(frameLabelData.Frames[i].Label, nextFrameStart - 1);
        }

        m_bitmapTags = swf.Tags.OfType<BitmapBaseTag>().ToList();
        m_bitmapCount = m_bitmapTags.Count;
    }

    private void ProcessActionsJSON(string path)
    {
        if (File.Exists(path))
        {
            m_hasActionsJson = true;

            JObject actionsJson = JObject.Parse(File.ReadAllText(path));
            foreach (var symbol in actionsJson)
            {
                List<int> frameStops = new();
                Dictionary<int, List<PACommand>> commands = new();

                JObject frames = (JObject)symbol.Value!;

                foreach (var frame in frames)
                {
                    string frameNum = frame.Key;
                    JArray actions = (JArray)frame.Value!;
                    List<PACommand> cmdList = new List<PACommand>(actions.Count);

                    foreach (var action in actions)
                    {
                        string actionFunc = (string)action["action"]!;

                        if (actionFunc.Equals("stop();", StringComparison.InvariantCultureIgnoreCase))
                        {
                            frameStops.Add(int.Parse(frameNum));
                        }
                        else if (actionFunc.StartsWith("fscommand", StringComparison.InvariantCultureIgnoreCase))
                        {
                            int start = actionFunc.IndexOf('(') + 1;
                            int end = actionFunc.LastIndexOf(')');

                            string cmdArgs = actionFunc.Substring(start, end - start);

                            string[] args = cmdArgs.Split(',');

                            if (args.Length > 0)
                            {
                                PACommand cmd = new();

                                cmd.Command = args[0].Trim().Trim('"');

                                StringBuilder paramBuilder = new();
                                for (int i = 1; i < args.Length; i++)
                                {
                                    string arg = args[i].Trim().Trim('"');

                                    if (paramBuilder.Length > 0)
                                        paramBuilder.Append(",");

                                    paramBuilder.Append(arg);
                                }

                                cmd.Param = paramBuilder.ToString();
                                cmdList.Add(cmd);
                            }
                        }
                    }

                    if (cmdList.Count > 0)
                        commands.Add(int.Parse(frameNum), cmdList);
                }

                m_stopsForSprites.Add(symbol.Key, frameStops);
                m_actionsForSprites.Add(symbol.Key, commands);
            }
        }
        else
        {
            Console.WriteLine($"WARNING - Actions json not found! Converter will attempt to automatically find stop frames.");
            m_hasActionsJson = false;
        }
    }

    private IResourceGenerator CreateResourceGenerator(ResourceFormat resFmt)
    {
        switch (resFmt)
        {
            case ResourceFormat.SEN: return new SenResourceGenerator();
            case ResourceFormat.SPC: throw new NotImplementedException($"SPC Resource generation");
            default: throw new InvalidDataException($"Invalid resource format: {resFmt}");
        }
    }

    public void ParseSWF()
    {
        m_resourceGenerator = CreateResourceGenerator(Program.Settings.ResourceFormat);
        m_swfName = Path.GetFileNameWithoutExtension(Program.Settings.SWFFileName);
        m_topFolderName = Utils.GetTopLevelFolder(Program.Settings.ResBasePath);
        string inputDir = Path.GetDirectoryName(Program.Settings.SWFFileName)!;

        // use this to determine how long swf -> pam conversion took
        Stopwatch swfConvertTimer = new();
        swfConvertTimer.Start();

        string actionsJsonFilename = Path.Combine(inputDir, $"{m_swfName}_actions.json");
        ProcessActionsJSON(actionsJsonFilename);

        string outputFileName = Path.Combine(inputDir, $"{m_swfName}.pam");
        m_writer = new ExtendedBinaryWriter(File.Create(outputFileName));

        if(Program.Settings.ExportImages)
        {
            m_imgOutputFolder = Path.Combine(inputDir, $"image_output_{m_swfName}");
            Directory.CreateDirectory(m_imgOutputFolder);
        }

        m_resourceGenerator.InitResources(m_swfName);

        using (var srcFile = File.Open(Program.Settings.SWFFileName, FileMode.Open, FileAccess.Read))
        {
            SwfFile swf = SwfFile.ReadFrom(srcFile);

            // Gather necessary information first
            PreprocessSWF(swf);

            m_writer.Write((uint)0xBAF01954); // PAM Magic
            m_writer.Write((int)6); // Version - I see no reason to support older versions
            m_writer.Write((byte)swf.Header.FrameRate);
            // anim rect - not used by the game, so always write the default of 390,390
            m_writer.Write((short)0);
            m_writer.Write((short)0);
            m_writer.Write((short)7800);
            m_writer.Write((short)7800);

            m_writer.Write((ushort)m_bitmapCount);
            foreach (var defineShape in swf.Tags.OfType<ShapeBaseTag>())
            {
                m_symbolNameMap.Add(defineShape.ShapeID, $"shape_{defineShape.ShapeID}");

                var fillStyles = defineShape.GetType().GetProperty("FillStyles")?.GetValue(defineShape)
                    ?? defineShape.GetType().GetField("FillStyles")?.GetValue(defineShape);

                var shapeRecords = defineShape.GetType().GetProperty("ShapeRecords")?.GetValue(defineShape)
                    ?? defineShape.GetType().GetField("ShapeRecords")?.GetValue(defineShape);

                if (fillStyles is IList<FillStyleRGB> fillStylesRGBs)
                    ParseFillStyles(fillStylesRGBs, defineShape.ShapeID);

                if (shapeRecords is IList<IShapeRecordRGB> shapeRecordsRGBs)
                {
                    foreach (var shapeRecord in shapeRecordsRGBs)
                    {
                        if (shapeRecord is StyleChangeShapeRecordRGB styleChangeRecord)
                        {
                            if (styleChangeRecord.StateNewStyles)
                            {
                                ParseFillStyles(styleChangeRecord.FillStyles, defineShape.ShapeID);
                            }
                        }
                    }
                }
            }

            var sprites = swf.Tags.OfType<DefineSpriteTag>().ToList();
            m_writer.Write((ushort)sprites.Count);

            foreach (var defineSprite in sprites)
            {
                WriteSprite(defineSprite.SpriteID, (float)swf.Header.FrameRate, defineSprite.Tags);
            }

            bool hasMainSprite = swf.Tags.OfType<ShowFrameTag>().Count() > 0;
            m_writer.Write(hasMainSprite);

            if(hasMainSprite)
            {
                WriteSprite(MAIN_SPRITE_ID, (float)swf.Header.FrameRate, swf.Tags);
            }

            m_resourceGenerator.FinalizeResources();

            Parallel.ForEach(m_imagesToExport, imgEntry =>
            {
                imgEntry.img.Save(imgEntry.imgPath);
            });

            m_writer.Close();
            swfConvertTimer.Stop();
            Console.WriteLine($"Finished writing {outputFileName} in {swfConvertTimer.Elapsed.TotalSeconds} seconds");
        }
    }
}
