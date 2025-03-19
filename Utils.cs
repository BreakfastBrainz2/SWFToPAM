using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SixLabors.ImageSharp.Processing;

namespace SWFToPAM;
public static class Utils
{
    public static string GetTopLevelFolder(string path)
    {
        path = path.TrimEnd('/', '\\');

        string[] parts = path.Split(new char[]{ '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);

        return parts.Length > 0 ? parts[^1] : string.Empty;
    }

    public static bool FloatApproxEqual(float a, float b, float tolerance)
    {
        return Math.Abs(a - b) <= tolerance;
    }

    public static Image<Rgba32> ChangeImageDimensions(Image<Rgba32> image, int newWidth, int newHeight)
    {
        var newImage = new Image<Rgba32>(newWidth, newHeight, new Rgba32(255, 255, 255, 0));

        newImage.Mutate(ctx => ctx.DrawImage(image, new SixLabors.ImageSharp.Point(0, 0), 1.0f));

        image.Dispose();

        return newImage;
    }

    public static void ResizeImage(Image<Rgba32> image, int newWidth, int newHeight)
    {
        image.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new Size(newWidth, newHeight),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Bicubic
        }));
    }

    public static int PAMRound(float number)
    {
        float tenths = (number - (int)number) * 10;

        if (tenths > 5.625)
        {
            return (int)Math.Ceiling(number);
        }
        else
        {
            return (int)Math.Floor(number);
        }
    }

    public static int EnsurePowerOfTwo(int value)
    {
        if (value <= 0)
            return 1;

        if ((value & (value - 1)) == 0)
            return value;

        int nextPower = 1;
        while (nextPower < value)
            nextPower <<= 1;

        return nextPower;
    }

    public static int EnsureClosestMultipleOfPowerOfTwo(int value)
    {
        if (value <= 0)
            return 1; // Smallest power of two

        // Check if the value is already a power of two
        if ((value & (value - 1)) == 0)
            return value;

        // Find the next power of two greater than the value
        int nextPowerOfTwo = 1;
        while (nextPowerOfTwo < value)
            nextPowerOfTwo <<= 1;

        // Find the largest power of two less than the value
        int lowerPowerOfTwo = nextPowerOfTwo >> 1;

        // Calculate the next multiple of the lower power of two
        int nextMultipleOfLower = ((value / lowerPowerOfTwo) + 1) * lowerPowerOfTwo;

        // Calculate the next multiple of the current power of two
        int nextMultipleOfCurrent = ((value / nextPowerOfTwo) + 1) * nextPowerOfTwo;

        // Determine which multiple is the next closest valid value
        int closestValidValue = nextMultipleOfLower;
        if (nextMultipleOfCurrent < closestValidValue)
            closestValidValue = nextMultipleOfCurrent;

        return closestValidValue;
    }

    public static int EnsureDivisibleBy2(int value)
    {
        return value + (value % 2);
    }

    public static void PaintImageOntoImage(Image<Rgba32> targetImage, int startX, int startY, Image<Rgba32> sourceImage)
    {
        // Bounds check to avoid painting outside the target image
        if (startX < 0 || startY < 0 || startX + sourceImage.Width > targetImage.Width || startY + sourceImage.Height > targetImage.Height)
        {
            throw new ArgumentException("The specified area exceeds the boundaries of the target image.");
        }

        // Paint the source image onto the target image
        for (int y = 0; y < sourceImage.Height; y++)
        {
            for (int x = 0; x < sourceImage.Width; x++)
            {
                // Get the pixel from the source image
                Rgba32 sourcePixel = sourceImage[x, y];

                // Calculate the target coordinates in the target image
                int targetX = startX + x;
                int targetY = startY + y;

                // Set the pixel at the target coordinates
                targetImage[targetX, targetY] = sourcePixel;
            }
        }
    }

    public static void PaintPixelArrayOntoImage(Image<Rgba32> image, int startX, int startY, int pixelArrayWidth, int pixelArrayHeight, Rgba32[] pixelArray)
    {
        // Ensure the pixel array dimensions match the provided width and height
        if (pixelArray.Length != pixelArrayWidth * pixelArrayHeight)
        {
            throw new ArgumentException("Pixel array size does not match the specified width and height.");
        }

        // Bounds check to avoid painting outside the target image
        if (startX < 0 || startY < 0 || startX + pixelArrayWidth > image.Width || startY + pixelArrayHeight > image.Height)
        {
            throw new ArgumentException("The specified area exceeds the boundaries of the target image.");
        }

        // Paint the pixel array onto the image
        for (int y = 0; y < pixelArrayHeight; y++)
        {
            for (int x = 0; x < pixelArrayWidth; x++)
            {
                // Calculate the source index in the pixel array
                int sourceIndex = y * pixelArrayWidth + x;

                // Calculate the target coordinates in the image
                int targetX = startX + x;
                int targetY = startY + y;

                // Set the pixel at the target coordinates
                image[targetX, targetY] = pixelArray[sourceIndex];
            }
        }
    }
}
