using System;
using System.Text;

namespace FeiqLight
{
    // 显示摘要与传输原文分离；阈值按 UTF-8 字节计算。
    public static class MessageText
    {
        public const int FileThreshold = 16 * 1024;
        public const int InputLimit = 1024 * 1024;
        public static bool AsFile(string text) { return Encoding.UTF8.GetByteCount(text ?? "") > FileThreshold; }
        public static string Preview(string text)
        {
            text = text ?? ""; int end = Math.Min(text.Length, 600), lines = 0;
            for (int i = 0; i < end; i++) if (text[i] == '\n' && ++lines == 8) { end = i; break; }
            if (end == text.Length) return text;
            if (end > 0 && Char.IsHighSurrogate(text[end - 1])) end--;
            return text.Substring(0, end) + "\n… 查看全文（双击）";
        }
    }
}
