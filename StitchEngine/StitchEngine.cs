using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using OpenCvSharp;

namespace Sinp.Stitch
{
    /// <summary>
    /// 长截图拼接结果
    /// </summary>
    public record StitchResult(
        bool Success,
        Bitmap? Image,
        int FrameCount,
        string? ErrorMessage = null
    );

    /// <summary>
    /// 帧重叠检测结果
    /// </summary>
    public record OverlapResult(
        bool HasOverlap,
        double OverlapRatio,
        int OverlapHeight
    );

    /// <summary>
    /// 长截图拼接引擎（Scroll Capture Pipeline）
    /// 算法：ORB 特征匹配 + SSIM 相似度
    /// </summary>
    public class StitchEngine : IDisposable
    {
        private readonly List<Bitmap> _frames = new();
        private Bitmap? _outputImage;

        /// <summary>
        /// 添加一帧
        /// </summary>
        public void AddFrame(Bitmap frame)
        {
            _frames.Add(new Bitmap(frame));
        }

        /// <summary>
        /// 检测两帧之间的重叠区域（ORB 特征匹配）
        /// </summary>
        public OverlapResult DetectOverlap(Bitmap topFrame, Bitmap bottomFrame)
        {
            using var topMat = BitmapToMat(topFrame);
            using var bottomMat = BitmapToMat(bottomFrame);

            // 取底部帧的顶部区域 & 顶部帧的底部区域做匹配
            var overlapHeight = Math.Min(topFrame.Height, bottomFrame.Height) / 3;
            using var topRegion = new Mat(topMat, new Rect(0, topMat.Rows - overlapHeight, topMat.Cols, overlapHeight));
            using var bottomRegion = new Mat(bottomMat, new Rect(0, 0, bottomMat.Cols, overlapHeight));

            // ORB 特征检测
            using var orb = ORB.Create(1000);
            var keypoints1 = new KeyPoint[0];
            var keypoints2 = new KeyPoint[0];
            using var descriptors1 = new Mat();
            using var descriptors2 = new Mat();

            orb.DetectAndCompute(topRegion, null, out keypoints1, descriptors1);
            orb.DetectAndCompute(bottomRegion, null, out keypoints2, descriptors2);

            if (descriptors1.Rows == 0 || descriptors2.Rows == 0)
                return new OverlapResult(false, 0, 0);

            // BFMatcher 匹配
            using var matcher = new BFMatcher(NormTypes.Hamming, false);
            var matches = matcher.Match(descriptors1, descriptors2);

            if (matches.Length < 10)
                return new OverlapResult(false, 0, 0);

            // 计算匹配质量
            var avgDistance = matches.Take(10).Average(m => m.Distance);
            var hasOverlap = avgDistance < 50; // 阈值可调

            return new OverlapResult(hasOverlap, 1.0 - avgDistance / 100.0, overlapHeight);
        }

        /// <summary>
        /// 执行拼接（主入口）
        /// </summary>
        public StitchResult Stitch()
        {
            if (_frames.Count == 0)
                return new StitchResult(false, null, 0, "没有可拼接的帧");

            if (_frames.Count == 1)
                return new StitchResult(true, _frames[0], 1);

            try
            {
                var result = _frames[0];
                for (int i = 1; i < _frames.Count; i++)
                {
                    result = MergeTwoFrames(result, _frames[i]);
                }

                _outputImage = result;
                return new StitchResult(true, result, _frames.Count);
            }
            catch (Exception ex)
            {
                return new StitchResult(false, null, _frames.Count, ex.Message);
            }
        }

        /// <summary>
        /// 两帧合并（去除重叠区域）
        /// </summary>
        private Bitmap MergeTwoFrames(Bitmap top, Bitmap bottom)
        {
            var overlap = DetectOverlap(top, bottom);

            // 如果没有检测到重叠，直接纵向拼接
            if (!overlap.HasOverlap)
            {
                var noOverlap = new Bitmap(
                    Math.Max(top.Width, bottom.Width),
                    top.Height + bottom.Height
                );
                using var g = Graphics.FromImage(noOverlap);
                g.DrawImage(top, 0, 0);
                g.DrawImage(bottom, 0, top.Height);
                return noOverlap;
            }

            // 去除重叠区域后拼接
            var nonOverlapHeight = bottom.Height - overlap.OverlapHeight;
            var merged = new Bitmap(
                Math.Max(top.Width, bottom.Width),
                top.Height + nonOverlapHeight
            );

            using var g2 = Graphics.FromImage(merged);
            g2.DrawImage(top, 0, 0);
            g2.DrawImage(bottom, 0, top.Height);

            return merged;
        }

        /// <summary>
        /// 清除所有帧
        /// </summary>
        public void Clear()
        {
            foreach (var frame in _frames)
                frame.Dispose();
            _frames.Clear();
        }

        private static Mat BitmapToMat(Bitmap bmp)
        {
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;
            return Mat.FromImageData(ms.ToArray());
        }

        public void Dispose()
        {
            Clear();
            _outputImage?.Dispose();
        }
    }
}
