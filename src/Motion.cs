using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FeiqLight
{
    // 所有自绘动画共用一个 UI 线程计时器；没有进行中的动画时立即停表，不为每个控件常驻定时器。
    // 动画只改变绘制，不改变消息、滚动位置等逻辑状态的落点，调用方读到的始终是终态。
    internal static class Motion
    {
        private static readonly Stopwatch clock = Stopwatch.StartNew();
        private static readonly List<Func<bool>> steps = new List<Func<bool>>();
        private static Timer timer;
        private static readonly bool enabled = ReadEnabled();
        internal static double Now { get { return clock.Elapsed.TotalMilliseconds; } }
        // 系统关闭“在 Windows 中显示动画”时直接呈现终态，尊重减少动态效果的设置。
        internal static bool Enabled { get { return enabled; } }
        internal static void Run(Func<bool> step)
        {
            if (step == null) return;
            if (!steps.Contains(step)) steps.Add(step);
            if (timer == null) { timer = new Timer { Interval = 15 }; timer.Tick += Tick; }
            if (!timer.Enabled) timer.Start();
        }
        private static void Tick(object sender, EventArgs e)
        {
            foreach (Func<bool> step in steps.ToArray())
            {
                bool keep = false;
                try { keep = step(); }
                // 控件在动画途中被释放时安静结束这一帧，其余异常仍交给全局处理器暴露。
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
                if (!keep) steps.Remove(step);
            }
            if (steps.Count == 0) timer.Stop();
        }
        internal static double Progress(double start, double duration)
        {
            if (!enabled || duration <= 0) return 1;
            return Math.Max(0, Math.Min(1, (Now - start) / duration));
        }
        internal static double EaseOut(double t) { t = Math.Max(0, Math.Min(1, t)); return 1 - Math.Pow(1 - t, 3); }
        internal static double EaseInOut(double t) { t = Math.Max(0, Math.Min(1, t)); return t < .5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2; }
        internal static Color Mix(Color from, Color to, double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb((int)Math.Round(from.A + (to.A - from.A) * t), (int)Math.Round(from.R + (to.R - from.R) * t), (int)Math.Round(from.G + (to.G - from.G) * t), (int)Math.Round(from.B + (to.B - from.B) * t));
        }
        private static bool ReadEnabled()
        {
            try { bool value = true; return !SystemParametersInfo(0x1042, 0, ref value, 0) || value; }
            catch (EntryPointNotFoundException) { return true; }
            catch (DllNotFoundException) { return true; }
        }
        [DllImport("user32.dll")] private static extern bool SystemParametersInfo(int action, int parameter, ref bool value, int update);
    }
    // 0→1 的单值过渡：悬停、开关、出现/消失共用，避免各控件重复写计时逻辑。
    internal sealed class Tween
    {
        private readonly Control owner;
        private readonly double duration;
        private double from, to, start;
        private readonly Func<bool> step;
        internal Tween(Control owner, double duration, double initial)
        {
            this.owner = owner; this.duration = duration; from = to = initial; start = -duration;
            step = delegate { if (owner.IsDisposed) return false; owner.Invalidate(); return Motion.Progress(start, this.duration) < 1; };
        }
        internal double Target { get { return to; } }
        internal double Value { get { double t = Motion.Progress(start, duration); return from + (to - from) * Motion.EaseOut(t); } }
        internal void Set(double target, bool animate = true)
        {
            if (target == to && (animate || Value == target)) return;
            from = animate && Motion.Enabled && owner.IsHandleCreated && owner.Visible ? Value : target; to = target; start = Motion.Now;
            if (from != to) Motion.Run(step); else owner.Invalidate();
        }
    }
}
