using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace FeiqLight
{
    internal sealed class ToolsPairings
    {
        internal sealed class Pairing
        {
            public string Hash { get; set; }
            public string Scope { get; set; }
            public long Until { get; set; }
            public bool AlwaysAllow { get; set; }
        }
        private readonly string path;
        private List<Pairing> items = new List<Pairing>();
        internal ToolsPairings(string root)
        {
            if (root == null) return;
            path = Path.Combine(root, "tools-pairings.json");
            if (File.Exists(path))
            {
                try { items = new JavaScriptSerializer().Deserialize<List<Pairing>>(File.ReadAllText(path, Encoding.UTF8)); }
                catch (ArgumentException) { throw new IOException("配对记录无法读取，请检查 tools-pairings.json"); }
                if (items == null || items.Any(p => p == null || p.Hash == null || p.Scope == null)) throw new IOException("配对记录格式无效");
            }
        }
        private static string Hash(string token)
        {
            using (SHA256 hash = SHA256.Create()) return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(token)));
        }
        internal bool Contains(string token, string scope)
        {
            return token.Length == 64 && items.Any(p => p.Hash == Hash(token) && p.Scope == scope && p.Until > DateTime.UtcNow.Ticks);
        }
        internal bool AlwaysAllows(string token, string scope) { return Contains(token, scope) && items.Any(p => p.Hash == Hash(token) && p.Scope == scope && p.AlwaysAllow); }
        internal string Remember(string token, string scope, bool alwaysAllow = false)
        {
            if (Contains(token, scope))
            {
                if (alwaysAllow && !AlwaysAllows(token, scope)) Save(items.Select(p => new Pairing { Hash = p.Hash, Scope = p.Scope, Until = p.Until, AlwaysAllow = p.AlwaysAllow || (p.Hash == Hash(token) && p.Scope == scope) }).ToList());
                return token;
            }
            token = ToolsService.RandomToken(32);
            List<Pairing> next = items.Where(p => p.Until > DateTime.UtcNow.Ticks).OrderByDescending(p => p.Until).Take(31).ToList();
            next.Add(new Pairing { Hash = Hash(token), Scope = scope, Until = DateTime.UtcNow.AddDays(90).Ticks, AlwaysAllow = alwaysAllow });
            Save(next); return token;
        }
        internal void Clear() { Save(new List<Pairing>()); }
        private void Save(List<Pairing> next)
        {
            if (path != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string temporary = path + ".tmp";
                File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(next), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            items = next;
        }
    }
}
