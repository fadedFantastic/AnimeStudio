// AnimeStudio 生成的 AssetMap JSON 可能有几百 MB，ConvertFrom-Json 会把整棵树读进内存。
// Newtonsoft 的 Indented 输出里每个字段独占一行，所以可以逐行走状态机，内存占用与文件大小无关。
// 由 tools\Build-ZzzAssetMap.ps1 和 tools\Find-ZzzAsset.ps1 通过 Add-Type -Path 加载。

using System;
using System.IO;
using System.Text;

namespace AnimeStudio.Tools
{
    public static class AssetMapIndexer
    {
        public const string Header = "Name\tType\tBlk\tOffset\tPathID\tContainer\tHash\tSource";

        public static long JsonToTsv(string jsonPath, string tsvPath)
        {
            long count = 0;
            string name = null, container = null, source = null, type = null, hash = null;
            string pathId = null, offset = null;

            using (var reader = new StreamReader(jsonPath, Encoding.UTF8))
            using (var writer = new StreamWriter(tsvPath, false, new UTF8Encoding(false)))
            {
                writer.WriteLine(Header);

                string raw;
                while ((raw = reader.ReadLine()) != null)
                {
                    string line = raw.Trim();
                    if (line.Length == 0) continue;

                    // 带引号的值不可能以 { 或 } 开头，所以看首字符判断层级是安全的
                    char c = line[0];
                    if (c == '{')
                    {
                        name = container = source = type = hash = pathId = offset = null;
                        continue;
                    }
                    if (c == '}')
                    {
                        if (name != null || type != null)
                        {
                            writer.WriteLine(string.Join("\t", new string[] {
                                Clean(name), Clean(type), Clean(BlkName(source)),
                                Clean(offset), Clean(pathId), Clean(container),
                                Clean(hash), Clean(source)
                            }));
                            count++;
                            name = container = source = type = hash = pathId = offset = null;
                        }
                        continue;
                    }
                    if (c != '"') continue;

                    int sep = line.IndexOf("\": ", StringComparison.Ordinal);
                    if (sep < 1) continue;

                    string key = line.Substring(1, sep - 1);
                    string val = line.Substring(sep + 3);
                    if (val.EndsWith(",", StringComparison.Ordinal))
                        val = val.Substring(0, val.Length - 1);
                    if (val.Length >= 2 && val[0] == '"' && val[val.Length - 1] == '"')
                        val = Unescape(val.Substring(1, val.Length - 2));
                    else if (val == "null")
                        val = "";   // Hash 之类可能是 null，别把字面量当成值

                    switch (key)
                    {
                        case "Name": name = val; break;
                        case "Container": container = val; break;
                        case "Source": source = val; break;
                        case "Type": type = val; break;
                        case "Hash": hash = val; break;
                        case "PathID": pathId = val; break;
                        case "Offset": offset = val; break;
                    }
                }
            }
            return count;
        }

        // 单独列出 blk 文件名，查询结果更好读；完整路径保留在最后一列
        private static string BlkName(string source)
        {
            if (string.IsNullOrEmpty(source)) return source;
            int i = source.LastIndexOfAny(new char[] { '\\', '/' });
            return i >= 0 ? source.Substring(i + 1) : source;
        }

        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.IndexOf('\t') >= 0) s = s.Replace('\t', ' ');
            if (s.IndexOf('\n') >= 0) s = s.Replace('\n', ' ');
            if (s.IndexOf('\r') >= 0) s = s.Replace('\r', ' ');
            return s;
        }

        private static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
                char n = s[++i];
                switch (n)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 < s.Length)
                        {
                            sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 4), 16));
                            i += 4;
                        }
                        break;
                    default: sb.Append(n); break;   // \" \\ \/ 原样输出
                }
            }
            return sb.ToString();
        }
    }
}
