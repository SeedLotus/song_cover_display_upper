using System;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace upper.Services
{
    /// <summary>
    /// 图像处理服务：负责专辑封面的智能适配、编码和转换
    /// </summary>
    public static class ImageProcessor
    {
        // 配置常量
        public const int TARGET_SIZE = 240;          // 目标正方形边长
        private const int BACKGROUND_SIZE = 20;     // 模糊背景的参考尺寸
        private const double BLUR_RADIUS = 20.0;     // 模糊效果半径，没用，ai 乱写的，只能改上面这个把图片拉小再拉大，会有栅格感

        /// <summary>
        /// 主处理方法：将任意尺寸的专辑封面处理为240x240的智能适配图像
        /// </summary>
        /// <param name="source">原始专辑封面</param>
        /// <returns>处理后的240x240图像</returns>
        public static BitmapSource ProcessAlbumArt(BitmapSource source)
        {
            if (source == null)
                return CreatePlaceholderImage();

            try
            {
                // 步骤1：创建模糊背景
                var background = CreateBlurredBackground(source);

                // 步骤2：创建前景（等比例缩放的原图）
                var foreground = CreateScaledForeground(source);

                // 步骤3：合成最终图像
                var finalImage = CompositeImages(background, foreground);

                return finalImage;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"图片处理失败: {ex.Message}");
                return CreatePlaceholderImage();
            }
        }

        /// <summary>
        /// 创建模糊背景
        /// </summary>
        private static BitmapSource CreateBlurredBackground(BitmapSource source)
        {
            // 1. 缩小原图以生成背景（性能优化）
            double backgroundScale = BACKGROUND_SIZE / (double)Math.Min(source.PixelWidth, source.PixelHeight);
            backgroundScale = Math.Min(backgroundScale, 1.0); // 确保不会放大

            var backgroundScaler = new TransformedBitmap();
            backgroundScaler.BeginInit();
            backgroundScaler.Source = source;
            backgroundScaler.Transform = new ScaleTransform(backgroundScale, backgroundScale);
            backgroundScaler.EndInit();
            backgroundScaler.Freeze();

            // 2. 创建临时图像并应用模糊效果
            // 首先将缩放后的图像渲染到RenderTargetBitmap
            var tempDrawingVisual = new DrawingVisual();
            using (var tempDrawingContext = tempDrawingVisual.RenderOpen())
            {
                tempDrawingContext.DrawImage(backgroundScaler,
                    new Rect(0, 0, backgroundScaler.PixelWidth, backgroundScaler.PixelHeight));
            }

            var tempBitmap = new RenderTargetBitmap(
                backgroundScaler.PixelWidth,
                backgroundScaler.PixelHeight,
                96, 96, PixelFormats.Pbgra32);
            tempBitmap.Render(tempDrawingVisual);
            tempBitmap.Freeze();

            // 3. 创建一个包含模糊效果的图像
            var blurredVisual = new DrawingVisual();
            using (var blurredContext = blurredVisual.RenderOpen())
            {
                // 创建模糊效果
                var blurEffect = new BlurEffect
                {
                    Radius = BLUR_RADIUS,
                    KernelType = KernelType.Gaussian,
                    RenderingBias = RenderingBias.Performance
                };

                // 创建一个临时的Image元素来承载模糊效果
                var tempImage = new System.Windows.Controls.Image
                {
                    Source = tempBitmap,
                    Effect = blurEffect,
                    Width = backgroundScaler.PixelWidth,
                    Height = backgroundScaler.PixelHeight
                };

                // 测量和排列临时Image元素
                tempImage.Measure(new System.Windows.Size(tempImage.Width, tempImage.Height));
                tempImage.Arrange(new Rect(0, 0, tempImage.Width, tempImage.Height));

                // 渲染到DrawingContext
                blurredContext.DrawImage(tempBitmap,
                    new Rect(0, 0, tempImage.Width, tempImage.Height));

                // 或者使用RenderTargetBitmap渲染Image元素
                // 更简单的方法：直接绘制应用了模糊效果的图像
            }

            // 4. 渲染模糊图像
            var blurredBitmap = new RenderTargetBitmap(
                backgroundScaler.PixelWidth,
                backgroundScaler.PixelHeight,
                96, 96, PixelFormats.Pbgra32);
            blurredBitmap.Render(blurredVisual);
            blurredBitmap.Freeze();

            // 5. 将模糊图像缩放到目标尺寸
            var finalScaler = new TransformedBitmap();
            finalScaler.BeginInit();
            finalScaler.Source = blurredBitmap;

            // 计算从模糊图像缩放到240x240的比例
            double finalScaleX = TARGET_SIZE / (double)blurredBitmap.PixelWidth;
            double finalScaleY = TARGET_SIZE / (double)blurredBitmap.PixelHeight;

            finalScaler.Transform = new ScaleTransform(finalScaleX, finalScaleY);
            finalScaler.EndInit();
            finalScaler.Freeze();

            return finalScaler;
        }

        /// <summary>
        /// 创建等比例缩放的前景图
        /// </summary>
        private static BitmapSource CreateScaledForeground(BitmapSource source)
        {
            // 计算缩放比例，使最长边等于TARGET_SIZE
            double scaleX = TARGET_SIZE / (double)source.PixelWidth;
            double scaleY = TARGET_SIZE / (double)source.PixelHeight;
            double scale = Math.Min(scaleX, scaleY); // 取较小值，确保图片完全在框内

            int newWidth = (int)(source.PixelWidth * scale);
            int newHeight = (int)(source.PixelHeight * scale);

            // 应用缩放变换
            var scaler = new TransformedBitmap();
            scaler.BeginInit();
            scaler.Source = source;
            scaler.Transform = new ScaleTransform(scale, scale);
            scaler.EndInit();
            scaler.Freeze();

            // 转换为统一格式
            if (scaler.Format != PixelFormats.Pbgra32)
            {
                var formatted = new FormatConvertedBitmap(scaler, PixelFormats.Pbgra32, null, 0);
                formatted.Freeze();
                return formatted;
            }

            return scaler;
        }

        /// <summary>
        /// 合成背景和前景图
        /// </summary>
        private static BitmapSource CompositeImages(BitmapSource background, BitmapSource foreground)
        {
            // 计算前景图居中位置
            int left = (TARGET_SIZE - foreground.PixelWidth) / 2;
            int top = (TARGET_SIZE - foreground.PixelHeight) / 2;

            // 创建绘图视觉对象
            var drawingVisual = new DrawingVisual();
            using (var drawingContext = drawingVisual.RenderOpen())
            {
                // 1. 绘制模糊背景
                drawingContext.DrawImage(background, new Rect(0, 0, TARGET_SIZE, TARGET_SIZE));

                // 2. 绘制前景图（居中）
                drawingContext.DrawImage(foreground, new Rect(left, top,
                    foreground.PixelWidth, foreground.PixelHeight));
            }

            // 渲染最终图像
            var finalBitmap = new RenderTargetBitmap(
                TARGET_SIZE, TARGET_SIZE, 96, 96, PixelFormats.Pbgra32);
            finalBitmap.Render(drawingVisual);
            finalBitmap.Freeze();

            return finalBitmap;
        }

        /// <summary>
        /// 将BitmapSource转换为RGB565字节数组（包含高低字节对调）
        /// </summary>
        /// <param name="source">输入图像（建议为240x240）</param>
        /// <returns>RGB565格式的字节数组，每个像素2字节（低字节在前）</returns>
        public static byte[] ConvertToRgb565(BitmapSource source)
        {
            // 验证输入
            if (source.PixelWidth != TARGET_SIZE || source.PixelHeight != TARGET_SIZE)
            {
                throw new ArgumentException($"输入图像尺寸必须为{TARGET_SIZE}x{TARGET_SIZE}");
            }

            // 转换为Bgr32格式以便访问像素数据
            var formattedBitmap = new FormatConvertedBitmap();
            formattedBitmap.BeginInit();
            formattedBitmap.Source = source;
            formattedBitmap.DestinationFormat = PixelFormats.Bgr32;
            formattedBitmap.EndInit();
            formattedBitmap.Freeze();

            // 复制像素数据
            int stride = formattedBitmap.PixelWidth * 4; // Bgr32每个像素4字节
            byte[] pixelData = new byte[stride * formattedBitmap.PixelHeight];
            formattedBitmap.CopyPixels(pixelData, stride, 0);

            // 转换为RGB565
            byte[] rgb565Data = new byte[TARGET_SIZE * TARGET_SIZE * 2];
            int rgbIndex = 0;

            for (int y = 0; y < TARGET_SIZE; y++)
            {
                for (int x = 0; x < TARGET_SIZE; x++)
                {
                    int pixelIndex = y * stride + x * 4;

                    // 获取Bgr32格式的像素值（注意顺序：Blue, Green, Red, Alpha）
                    byte blue = pixelData[pixelIndex];
                    byte green = pixelData[pixelIndex + 1];
                    byte red = pixelData[pixelIndex + 2];

                    // 转换为RGB565（5位红色，6位绿色，5位蓝色）
                    ushort r5 = (ushort)((red >> 3) & 0x1F);
                    ushort g6 = (ushort)((green >> 2) & 0x3F);
                    ushort b5 = (ushort)((blue >> 3) & 0x1F);

                    // 组合为16位RGB565值
                    ushort rgb565 = (ushort)((r5 << 11) | (g6 << 5) | b5);

                    // 高低字节对调：低字节在前，高字节在后
                    //rgb565Data[rgbIndex++] = (byte)(rgb565 & 0xFF);      // 低字节
                    //rgb565Data[rgbIndex++] = (byte)((rgb565 >> 8) & 0xFF); // 高字节
                    rgb565Data[rgbIndex++] = (byte)((rgb565 >> 8) & 0xFF); // 高字节
                    rgb565Data[rgbIndex++] = (byte)(rgb565 & 0xFF); // 低字节
                }
            }

            return rgb565Data;
        }

        /// <summary>
        /// 计算图像的MD5哈希值（用于检测图片是否变化）
        /// </summary>
        public static string ComputeImageHash(BitmapSource image)
        {
            if (image == null) return string.Empty;

            try
            {
                using (var md5 = MD5.Create())
                {
                    // 将图像转换为字节数组
                    int stride = (image.PixelWidth * image.Format.BitsPerPixel + 7) / 8;
                    byte[] pixelData = new byte[stride * image.PixelHeight];
                    image.CopyPixels(pixelData, stride, 0);

                    // 计算哈希
                    byte[] hashBytes = md5.ComputeHash(pixelData);
                    var sb = new StringBuilder();
                    foreach (byte b in hashBytes)
                        sb.Append(b.ToString("x2"));

                    return sb.ToString();
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// 创建占位图像（当没有专辑封面时显示）
        /// </summary>
        public static BitmapSource CreatePlaceholderImage()
        {
            return CreatePlaceholderImage(TARGET_SIZE, TARGET_SIZE);
        }

        /// <summary>
        /// 创建指定尺寸的占位图像
        /// </summary>
        public static BitmapSource CreatePlaceholderImage(int width, int height)
        {
            var drawingVisual = new DrawingVisual();
            using (var drawingContext = drawingVisual.RenderOpen())
            {
                // 绘制灰色背景
                drawingContext.DrawRectangle(
                    System.Windows.Media.Brushes.LightGray,
                    null,
                    new Rect(0, 0, width, height)
                );

                // 绘制文本
                var text = new FormattedText(
                    "T X",
                    System.Globalization.CultureInfo.CurrentCulture,
                    System.Windows.FlowDirection.LeftToRight,
                    new Typeface("Arial"),
                    Math.Min(width, height) * 0.4,
                    System.Windows.Media.Brushes.DarkGray,
                    VisualTreeHelper.GetDpi(drawingVisual).PixelsPerDip
                );

                // 居中显示
                double x = (width - text.Width) / 2;
                double y = (height - text.Height) / 2;
                drawingContext.DrawText(text, new System.Windows.Point(x, y));

                text = new FormattedText(
                    "i",
                    System.Globalization.CultureInfo.CurrentCulture,
                    System.Windows.FlowDirection.LeftToRight,
                    new Typeface("Arial"),
                    Math.Min(width, height) * 0.4,
                    System.Windows.Media.Brushes.DarkGray,
                    VisualTreeHelper.GetDpi(drawingVisual).PixelsPerDip
                );

                // 居中显示
                x = (width - text.Width) / 2;
                y = (height - text.Height) / 2;
                drawingContext.DrawText(text, new System.Windows.Point(x, y));
            }

            var renderTarget = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            renderTarget.Render(drawingVisual);
            renderTarget.Freeze();

            return renderTarget;
        }

        /// <summary>
        /// 调试辅助：将字节数组保存为临时文件（用于验证RGB565转换结果）
        /// </summary>
        public static void SaveRgb565ForDebug(byte[] rgb565Data, string filename = "debug_rgb565.raw")
        {
            try
            {
                System.IO.File.WriteAllBytes(filename, rgb565Data);
                Debug.WriteLine($"RGB565数据已保存到: {System.IO.Path.GetFullPath(filename)}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"保存调试文件失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取RGB565图片数据的分包信息
        /// </summary>
        /// <param name="rgb565Data">RGB565数据</param>
        /// <param name="packetDataSize">每个包的数据部分大小（默认61字节）</param>
        /// <returns>总包数</returns>
        public static int CalculatePacketCount(byte[] rgb565Data, int packetDataSize = 61)
        {
            if (rgb565Data == null || rgb565Data.Length == 0)
                return 0;

            return (int)Math.Ceiling(rgb565Data.Length / (double)packetDataSize);
        }
    }
}
