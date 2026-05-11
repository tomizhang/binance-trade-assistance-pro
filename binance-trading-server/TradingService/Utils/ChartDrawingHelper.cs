using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkiaSharp;
using TradingTerminal.Models;

namespace TradingTerminal.Utils
{
    /// <summary>
    /// 基于 SkiaSharp 的图表绘制助手
    /// 支持 K线、点位、文字、斜线、平行通道等绘制
    /// 提供了多个重载接口方便不同参数的调用
    /// </summary>
    public class ChartDrawingHelper : IDisposable
    {
        private readonly int _width;
        private readonly int _height;
        private readonly SKBitmap _bitmap;
        private readonly SKCanvas _canvas;

        // 画笔设置
        private readonly SKPaint _klineUpPaint;
        private readonly SKPaint _klineDownPaint;
        private readonly SKPaint _textPaint;
        private readonly SKPaint _pointPaint;
        private readonly SKPaint _linePaint;
        private readonly SKPaint _channelPaint;

        public ChartDrawingHelper(int width = 1920, int height = 1080)
        {
            _width = width;
            _height = height;
            _bitmap = new SKBitmap(width, height);
            _canvas = new SKCanvas(_bitmap);
            
            // 默认黑色背景
            _canvas.Clear(SKColors.Black); 

            _klineUpPaint = new SKPaint { Color = SKColors.Green, Style = SKPaintStyle.Fill, IsAntialias = false };
            _klineDownPaint = new SKPaint { Color = SKColors.Red, Style = SKPaintStyle.Fill, IsAntialias = false };
            
            _textPaint = new SKPaint 
            { 
                Color = SKColors.White, 
                TextSize = 20, 
                IsAntialias = true,
                Typeface = SKTypeface.Default
            };
            
            _pointPaint = new SKPaint { Color = SKColors.Yellow, Style = SKPaintStyle.Fill, IsAntialias = true };
            
            _linePaint = new SKPaint { Color = SKColors.LightBlue, Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true };
            
            _channelPaint = new SKPaint { Color = SKColors.DarkBlue.WithAlpha(80), Style = SKPaintStyle.Fill, IsAntialias = true };
        }

        /// <summary>
        /// 批量绘制 K线
        /// </summary>
        public ChartDrawingHelper DrawKlines(IEnumerable<KlineMessage> klines, decimal minPrice, decimal maxPrice, float margin = 50f)
        {
            var klineList = klines.ToList();
            if (!klineList.Any()) return this;

            int count = klineList.Count;
            float drawableWidth = _width - 2 * margin;
            float drawableHeight = _height - 2 * margin;
            
            float candleWidth = drawableWidth / count;
            float priceRange = (float)(maxPrice - minPrice);
            if (priceRange == 0) priceRange = 1;

            for (int i = 0; i < count; i++)
            {
                var k = klineList[i];
                float x = margin + i * candleWidth + candleWidth / 2;
                
                float highY = margin + drawableHeight * (1 - (float)(k.High - minPrice) / priceRange);
                float lowY = margin + drawableHeight * (1 - (float)(k.Low - minPrice) / priceRange);
                float openY = margin + drawableHeight * (1 - (float)(k.Open - minPrice) / priceRange);
                float closeY = margin + drawableHeight * (1 - (float)(k.Close - minPrice) / priceRange);

                var paint = k.Close >= k.Open ? _klineUpPaint : _klineDownPaint;

                // 影线
                _canvas.DrawLine(x, highY, x, lowY, new SKPaint { Color = paint.Color, StrokeWidth = 1, IsAntialias = false });

                // 实体
                float bodyTop = Math.Min(openY, closeY);
                float bodyBottom = Math.Max(openY, closeY);
                float bodyHeight = Math.Max(bodyBottom - bodyTop, 1f); // 至少1像素
                
                _canvas.DrawRect(x - candleWidth * 0.4f, bodyTop, candleWidth * 0.8f, bodyHeight, paint);
            }

            return this;
        }

        /// <summary>
        /// 批量绘制文字 (统一颜色)
        /// </summary>
        public ChartDrawingHelper DrawTexts(IEnumerable<(string Text, float X, float Y)> texts)
        {
            foreach (var t in texts)
            {
                _canvas.DrawText(t.Text, t.X, t.Y, _textPaint);
            }
            return this;
        }

        /// <summary>
        /// 批量绘制文字 (各自指定颜色)
        /// </summary>
        public ChartDrawingHelper DrawTexts(IEnumerable<(string Text, float X, float Y, SKColor Color)> texts)
        {
            foreach (var t in texts)
            {
                using var p = _textPaint.Clone();
                p.Color = t.Color;
                _canvas.DrawText(t.Text, t.X, t.Y, p);
            }
            return this;
        }

        /// <summary>
        /// 批量绘制点位 (统一颜色)
        /// </summary>
        public ChartDrawingHelper DrawPoints(IEnumerable<(float X, float Y)> points, float radius = 5f)
        {
            foreach (var p in points)
            {
                _canvas.DrawCircle(p.X, p.Y, radius, _pointPaint);
            }
            return this;
        }

        /// <summary>
        /// 批量绘制点位 (各自指定颜色)
        /// </summary>
        public ChartDrawingHelper DrawPoints(IEnumerable<(float X, float Y, SKColor Color)> points, float radius = 5f)
        {
            foreach (var p in points)
            {
                using var paint = _pointPaint.Clone();
                paint.Color = p.Color;
                _canvas.DrawCircle(p.X, p.Y, radius, paint);
            }
            return this;
        }

        /// <summary>
        /// 批量绘制斜线 (统一颜色)
        /// </summary>
        public ChartDrawingHelper DrawLines(IEnumerable<(float X1, float Y1, float X2, float Y2)> lines)
        {
            foreach (var l in lines)
            {
                _canvas.DrawLine(l.X1, l.Y1, l.X2, l.Y2, _linePaint);
            }
            return this;
        }

        /// <summary>
        /// 批量绘制斜线 (各自指定颜色和线宽)
        /// </summary>
        public ChartDrawingHelper DrawLines(IEnumerable<(float X1, float Y1, float X2, float Y2, SKColor Color, float StrokeWidth)> lines)
        {
            foreach (var l in lines)
            {
                using var p = _linePaint.Clone();
                p.Color = l.Color;
                p.StrokeWidth = l.StrokeWidth;
                _canvas.DrawLine(l.X1, l.Y1, l.X2, l.Y2, p);
            }
            return this;
        }

        /// <summary>
        /// 批量绘制平行通道
        /// ChannelWidth 表示在法线方向上的宽度
        /// </summary>
        public ChartDrawingHelper DrawChannels(IEnumerable<(float X1, float Y1, float X2, float Y2, float ChannelWidth)> channels)
        {
            foreach (var c in channels)
            {
                // 计算法向量
                float dx = c.X2 - c.X1;
                float dy = c.Y2 - c.Y1;
                float len = (float)Math.Sqrt(dx * dx + dy * dy);
                if (len == 0) continue;

                float nx = -dy / len;
                float ny = dx / len;

                float halfW = c.ChannelWidth / 2;

                using var path = new SKPath();
                path.MoveTo(c.X1 + nx * halfW, c.Y1 + ny * halfW);
                path.LineTo(c.X2 + nx * halfW, c.Y2 + ny * halfW);
                path.LineTo(c.X2 - nx * halfW, c.Y2 - ny * halfW);
                path.LineTo(c.X1 - nx * halfW, c.Y1 - ny * halfW);
                path.Close();

                _canvas.DrawPath(path, _channelPaint);
            }
            return this;
        }

        /// <summary>
        /// 批量绘制平行通道 (各自指定颜色)
        /// </summary>
        public ChartDrawingHelper DrawChannels(IEnumerable<(float X1, float Y1, float X2, float Y2, float ChannelWidth, SKColor Color)> channels)
        {
            foreach (var c in channels)
            {
                float dx = c.X2 - c.X1;
                float dy = c.Y2 - c.Y1;
                float len = (float)Math.Sqrt(dx * dx + dy * dy);
                if (len == 0) continue;

                float nx = -dy / len;
                float ny = dx / len;
                float halfW = c.ChannelWidth / 2;

                using var path = new SKPath();
                path.MoveTo(c.X1 + nx * halfW, c.Y1 + ny * halfW);
                path.LineTo(c.X2 + nx * halfW, c.Y2 + ny * halfW);
                path.LineTo(c.X2 - nx * halfW, c.Y2 - ny * halfW);
                path.LineTo(c.X1 - nx * halfW, c.Y1 - ny * halfW);
                path.Close();

                using var p = _channelPaint.Clone();
                p.Color = c.Color;
                _canvas.DrawPath(path, p);
            }
            return this;
        }

        /// <summary>
        /// 【专为金融图表设计】批量绘制垂直平移的平行通道
        /// 解决因价格和时间坐标比例悬殊，导致几何法线计算时产生的严重角度变形问题。
        /// 在交易中，通道宽度通常是价格（Y轴）的绝对偏移量，而不是几何上的垂直距离。
        /// VerticalPixelWidth 表示仅仅在 Y 轴上下平移的像素距离。
        /// </summary>
        public ChartDrawingHelper DrawPriceChannels(IEnumerable<(float X1, float Y1, float X2, float Y2, float VerticalPixelWidth)> channels)
        {
            foreach (var c in channels)
            {
                float halfW = c.VerticalPixelWidth / 2;

                using var path = new SKPath();
                // 仅在 Y 轴上偏移，不受斜率影响
                path.MoveTo(c.X1, c.Y1 - halfW);
                path.LineTo(c.X2, c.Y2 - halfW);
                path.LineTo(c.X2, c.Y2 + halfW);
                path.LineTo(c.X1, c.Y1 + halfW);
                path.Close();

                _canvas.DrawPath(path, _channelPaint);
            }
            return this;
        }

        /// <summary>
        /// 批量绘制垂直平移的平行通道 (各自指定颜色)
        /// </summary>
        public ChartDrawingHelper DrawPriceChannels(IEnumerable<(float X1, float Y1, float X2, float Y2, float VerticalPixelWidth, SKColor Color)> channels)
        {
            foreach (var c in channels)
            {
                float halfW = c.VerticalPixelWidth / 2;

                using var path = new SKPath();
                path.MoveTo(c.X1, c.Y1 - halfW);
                path.LineTo(c.X2, c.Y2 - halfW);
                path.LineTo(c.X2, c.Y2 + halfW);
                path.LineTo(c.X1, c.Y1 + halfW);
                path.Close();

                using var p = _channelPaint.Clone();
                p.Color = c.Color;
                _canvas.DrawPath(path, p);
            }
            return this;
        }

        // ============================
        // 坐标映射辅助方法
        // ============================

        /// <summary>
        /// 根据 K线索引映射 X 坐标
        /// </summary>
        public float MapX(int index, int totalKlines, float margin = 50f)
        {
             if (totalKlines <= 0) return margin;
             float drawableWidth = _width - 2 * margin;
             float candleWidth = drawableWidth / totalKlines;
             return margin + index * candleWidth + candleWidth / 2;
        }

        /// <summary>
        /// 根据价格映射 Y 坐标
        /// </summary>
        public float MapY(decimal price, decimal minPrice, decimal maxPrice, float margin = 50f)
        {
             float priceRange = (float)(maxPrice - minPrice);
             if (priceRange == 0) priceRange = 1;
             float drawableHeight = _height - 2 * margin;
             return margin + drawableHeight * (1 - (float)(price - minPrice) / priceRange);
        }

        /// <summary>
        /// 导出为 PNG 字节数组
        /// </summary>
        public byte[] ToPngBytes()
        {
            using var image = SKImage.FromBitmap(_bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
        
        /// <summary>
        /// 保存为图片文件
        /// </summary>
        public void SaveToFile(string filePath)
        {
            File.WriteAllBytes(filePath, ToPngBytes());
        }

        public void Dispose()
        {
            _klineUpPaint?.Dispose();
            _klineDownPaint?.Dispose();
            _textPaint?.Dispose();
            _pointPaint?.Dispose();
            _linePaint?.Dispose();
            _channelPaint?.Dispose();
            _canvas?.Dispose();
            _bitmap?.Dispose();
        }
    }
}
