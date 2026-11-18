using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace NovelDump
{
    /// <summary>
    /// 把剧情正文原样落盘。
    ///
    /// 起因：新活动的剧情脚本不在公开 CDN 的 catalog 里，抓不到日文原文，
    /// 也就没法按原文去查翻译字典。这个插件挂在游戏解析/显示剧情的几个入口上，
    /// 把每句原文连同当前场景 ID 追加到 novel_dump.jsonl。
    ///
    /// 游戏有不止一种文字渲染路径（普通文本、点阵气泡、居中大字），
    /// 所以挂了四个点，并且逐个独立 patch：某一个签名对不上不会拖垮整个插件。
    /// </summary>
    [BepInPlugin(Guid, "NovelDump", "1.1.0")]
    public class Plugin : BasePlugin
    {
        public const string Guid = "local.dotabyss.noveldump";
        internal static ManualLogSource Logger;

        public override void Load()
        {
            Logger = Log;
            var harmony = new Harmony(Guid);

            // 所有重载一律用 __args 兜住：参数里任何带假名的字符串、或带 Message 字段的对象都记下来。
            // 之前按"猜的参数下标"绑定，遇到重载或参数顺序变化就会静默失效（hmr_xxx_11 就是这样漏的）。
            PatchAllOverloads(harmony, typeof(Project.Novel.NovelText), "Parse", nameof(Hooks.OnParseArgs), "NovelText.Parse");
            PatchAllOverloads(harmony, typeof(Project.Novel.NovelModelMessageLog), "Add", nameof(Hooks.OnLogArgs), "MessageLog.Add");
            PatchAllOverloads(harmony, typeof(Project.Novel.NovelModelDotBalloon), "StartBalloonMessage", nameof(Hooks.OnBalloonArgs), "DotBalloon.StartBalloonMessage");
            PatchAllOverloads(harmony, typeof(Project.Novel.NovelMessageTextComponent), "SetMessageText", nameof(Hooks.OnCenterArgs), "MessageTextComponent.SetMessageText");

            // 剧情标题 + 简介（GetScriptInfo 返回 (title, description)）
            Patch(harmony, typeof(Project.Novel.NovelScriptInfoUtility), "GetScriptInfo", nameof(Hooks.OnScriptInfo), "NovelScriptInfoUtility.GetScriptInfo");

            // 对话框真正设文本的地方：NovelViewMessageWindow.SetText(string)
            Patch(harmony, typeof(Project.Novel.NovelViewMessageWindow), "SetText", nameof(Hooks.OnAnyArgs), "NovelViewMessageWindow.SetText");

            // 其它渲染入口：直接改字符串参数（ref 绑定），既抓原文也查字典替换。
            PatchReplace(harmony, typeof(Project.Novel.NovelModelMessage), "Set", 2, "NovelModelMessage.Set");
            PatchReplace(harmony, typeof(Project.Novel.DotNovelBalloonComponent), "SetInitMessage", 0, "DotNovelBalloonComponent.SetInitMessage");
            PatchReplace(harmony, typeof(Project.Novel.NovelCmdMessageTextCenter), "SetMessage", 0, "NovelCmdMessageTextCenter.SetMessage");
            PatchReplace(harmony, typeof(Project.Novel.NovelCmdMessageTextUnder), "SetMessage", 0, "NovelCmdMessageTextUnder.SetMessage");
            PatchReplace(harmony, typeof(Project.Novel.NovelViewMessageWindow), "SetText", 0, "NovelViewMessageWindow.SetText(ref)");

            // 兜底探针：任何带假名的字符串被塞进 TMP 都记一笔
            try
            {
                var setter = AccessTools.PropertySetter(typeof(TMPro.TMP_Text), "text");
                if (setter != null)
                {
                    harmony.Patch(setter, prefix: new HarmonyMethod(typeof(Hooks), nameof(Hooks.OnTmpText)));
                    Log.LogInfo("[NovelDump] 已挂上 TMP 兜底探针");
                }
                else
                {
                    Log.LogWarning("[NovelDump] 找不到 TMP_Text.text setter");
                }
            }
            catch (Exception e)
            {
                Log.LogWarning("[NovelDump] TMP 探针挂载失败: " + e.Message);
            }

            // 场景 ID：这个方法有多个重载，逐个挂上（拿不到 ID 的话抓到的行没法归类）
            foreach (var mi in AccessTools.GetDeclaredMethods(typeof(Project.Novel.NovelPathUtility)))
            {
                if (mi.Name != "GetNovelScenarioDirectory" || mi.GetParameters().Length == 0)
                    continue;
                try
                {
                    harmony.Patch(mi, prefix: new HarmonyMethod(typeof(Hooks), nameof(Hooks.OnScenarioAny)));
                    Log.LogInfo("[NovelDump] 已挂上场景ID: " + mi);
                }
                catch (Exception e)
                {
                    Log.LogWarning("[NovelDump] 场景ID挂载失败 " + mi + ": " + e.Message);
                }
            }

            Log.LogInfo("[NovelDump] loaded -> " + Hooks.OutPath);
        }

        /// <summary>把某个名字下的所有重载都挂上同一个 prefix（参数用 __args 接）。</summary>
        private static void PatchAllOverloads(Harmony harmony, Type type, string method, string prefix, string label)
        {
            int n = 0;
            foreach (var mi in AccessTools.GetDeclaredMethods(type))
            {
                if (mi.Name != method)
                    continue;
                try
                {
                    harmony.Patch(mi, prefix: new HarmonyMethod(typeof(Hooks), prefix));
                    n++;
                }
                catch (Exception e)
                {
                    Logger?.LogWarning("[NovelDump] " + label + " 重载挂载失败 " + mi + ": " + e.Message);
                }
            }
            if (n == 0)
                Logger?.LogWarning("[NovelDump] " + label + ": 没有找到任何重载");
            else
                Logger?.LogInfo($"[NovelDump] 已挂上 {label}（{n} 个重载）");
        }

        /// <summary>把某个方法第 index 个参数（string）当作文本处理：记录 + 查字典替换。</summary>
        private static void PatchReplace(Harmony harmony, Type type, string method, int index, string label)
        {
            var prefix = index switch
            {
                0 => nameof(Hooks.OnReplace0),
                1 => nameof(Hooks.OnReplace1),
                2 => nameof(Hooks.OnReplace2),
                3 => nameof(Hooks.OnReplace3),
                _ => null,
            };
            if (prefix == null)
                return;
            int n = 0;
            foreach (var mi in AccessTools.GetDeclaredMethods(type))
            {
                if (mi.Name != method)
                    continue;
                var ps = mi.GetParameters();
                if (ps.Length <= index || ps[index].ParameterType != typeof(string))
                    continue;
                try
                {
                    harmony.Patch(mi, prefix: new HarmonyMethod(typeof(Hooks), prefix));
                    n++;
                }
                catch (Exception e)
                {
                    Logger?.LogWarning("[NovelDump] " + label + " 挂载失败: " + e.Message);
                }
            }
            Logger?.LogInfo($"[NovelDump] 已挂上 {label}（{n} 个重载，参数 {index}）");
        }

        private static void Patch(Harmony harmony, Type type, string method, string prefix, string label)
        {
            try
            {
                var target = AccessTools.Method(type, method);
                if (target == null)
                {
                    Logger?.LogWarning("[NovelDump] " + label + ": 找不到方法，跳过");
                    return;
                }
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(Hooks), prefix));
                Logger?.LogInfo("[NovelDump] 已挂上 " + label);
            }
            catch (Exception e)
            {
                Logger?.LogError("[NovelDump] " + label + " 挂载失败: " + e.Message);
            }
        }
    }

    public static class Hooks
    {
        public static readonly string OutPath = Path.Combine(
            Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".",
            "novel_dump.jsonl"
        );

        private static readonly Regex SceneIdPattern = new Regex("^(evs|mas|hmn|hmr|men|prg|prologue)_?[0-9]+", RegexOptions.IgnoreCase);
        private static string _scene = "";
        private static readonly HashSet<string> Seen = new HashSet<string>();
        private static readonly object Gate = new object();

        /// <summary>任意重载：参数里有像场景 ID 的字符串就记下来。</summary>
        public static void OnScenarioAny(object[] __args)
        {
            if (__args == null)
                return;
            foreach (var arg in __args)
            {
                var s = arg as string;
                if (!string.IsNullOrEmpty(s) && SceneIdPattern.IsMatch(s))
                {
                    if (_scene != s)
                        Plugin.Logger?.LogInfo("[NovelDump] scene = " + s);
                    _scene = s;
                    return;
                }
            }
        }

        /// <summary>
        /// 通用入口：不管重载怎么变，参数里任何带假名的字符串、或任何带 Message 字段的对象都记下来。
        /// </summary>
        public static void OnParseArgs(object[] __args) => Scan(__args, "parse");
        public static void OnLogArgs(object[] __args) => Scan(__args, "log");
        public static void OnBalloonArgs(object[] __args) => Scan(__args, "balloon");
        public static void OnCenterArgs(object[] __args) => Scan(__args, "center");
        public static void OnAnyArgs(object[] __args) => Scan(__args, "any");

        public static void OnReplace0(ref string __0) => Substitute(ref __0, "r0");
        public static void OnReplace1(ref string __1) => Substitute(ref __1, "r1");
        public static void OnReplace2(ref string __2) => Substitute(ref __2, "r2");
        public static void OnReplace3(ref string __3) => Substitute(ref __3, "r3");

        /// <summary>记录原文；字典里有就把参数改成中文。</summary>
        private static void Substitute(ref string text, string src)
        {
            var message = text;
            if (string.IsNullOrWhiteSpace(message))
                return;

            Dump(message, "line", src);

            EnsureDict();
            if (_dict != null && _dict.TryGetValue(message, out var translated))
            {
                if (!string.IsNullOrEmpty(translated) && translated != message)
                    text = translated;
            }
        }

        private static void Scan(object[] args, string src)
        {
            if (args == null)
                return;
            foreach (var arg in args)
            {
                if (arg == null)
                    continue;
                if (arg is string s)
                {
                    if (HasKana(s))
                        Dump(s, "line", src);
                    continue;
                }
                var msg = ReadMessage(arg);
                if (!string.IsNullOrEmpty(msg))
                    Dump(msg, "line", src);
            }
        }

        /// <summary>
        /// TMP 兜底：任何带假名的字符串被写进 TMP 都记一笔。
        /// 有些"前篇"场景（hmr_xxx_11）走的是自绘渲染，四个正常入口都接不到。
        /// </summary>
        public static void OnTmpText(string __0)
        {
            if (string.IsNullOrEmpty(__0) || _scene.Length == 0)
                return;
            if (!HasKana(__0))
                return;
            Dump(__0, "line", "tmp");
        }

        /// <summary>
        /// 对话框设文本：先记原文，再查 AbyssMod 的翻译缓存，命中就把中文塞回去。
        /// 这条路径覆盖了 Parse 管不到的那类场景（"前篇"等）。
        /// </summary>
        public static void OnSetText(ref string __0)
        {
            var message = __0;
            if (string.IsNullOrWhiteSpace(message))
                return;

            Dump(message, "line", "settext");

            EnsureDict();
            if (_dict != null && _dict.TryGetValue(message, out var translated))
            {
                if (!string.IsNullOrEmpty(translated) && translated != message)
                    __0 = translated;
            }
        }

        private static Dictionary<string, string> _dict;
        private static string _dictScene = null;

        /// <summary>按当前场景读 AbyssMod 的翻译缓存（novels/&lt;场景&gt;/&lt;语言&gt;.json）。</summary>
        private static void EnsureDict()
        {
            if (_dictScene == _scene)
                return;
            _dictScene = _scene;
            _dict = null;
            if (string.IsNullOrEmpty(_scene))
                return;
            try
            {
                var dir = Path.Combine(
                    Paths.PluginPath, "AbyssMod", "cache", "translations", "novels", _scene
                );
                if (!Directory.Exists(dir))
                    return;
                var files = Directory.GetFiles(dir, "*.json");
                if (files.Length == 0)
                    return;
                // 优先简体，其次任意
                Array.Sort(files, (a, b) =>
                {
                    int sa = a.EndsWith("zh_Hans.json", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
                    int sb = b.EndsWith("zh_Hans.json", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
                    return sa.CompareTo(sb);
                });
                var map = new Dictionary<string, string>();
                using (var fs = File.OpenRead(files[0]))
                using (var doc = JsonDocument.Parse(fs))
                {
                    foreach (var p in doc.RootElement.EnumerateObject())
                    {
                        var v = p.Value.GetString();
                        if (!string.IsNullOrEmpty(v))
                            map[p.Name] = v;
                    }
                }
                _dict = map;
                Plugin.Logger?.LogInfo($"[NovelDump] 载入翻译 {_scene}: {map.Count} 条");
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("[NovelDump] 读翻译缓存失败: " + e.Message);
            }
        }

        private static bool HasKana(string s)
        {
            foreach (char c in s)
            {
                if ((c >= '\u3040' && c <= '\u309F') || (c >= '\u30A0' && c <= '\u30FF'))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// NovelScriptInfoUtility.GetScriptInfo() 返回 (title, description)。
        /// 这是剧情列表/关卡详情里显示的那两段文字，Mod 用 titles / descriptions 两个字典查。
        /// </summary>
        public static void OnScriptInfo(Il2CppSystem.ValueTuple<string, string> __result)
        {
            try
            {
                Dump(__result.Item1, "title");
                Dump(__result.Item2, "desc");
            }
            catch
            {
                // 读不到就算了，不影响游戏
            }
        }

        /// <summary>不依赖具体类型地取 Message 字段/属性。</summary>
        private static string ReadMessage(object obj)
        {
            if (obj == null)
                return null;
            try
            {
                var type = obj.GetType();
                var prop = type.GetProperty("Message", BindingFlags.Public | BindingFlags.Instance);
                if (prop != null)
                    return prop.GetValue(obj) as string;
                var field = type.GetField("Message", BindingFlags.Public | BindingFlags.Instance);
                return field?.GetValue(obj) as string;
            }
            catch
            {
                return null;
            }
        }

        private static void Dump(string text, string kind = "line", string src = "")
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            lock (Gate)
            {
                if (!Seen.Add(src + "\u0003" + kind + "\u0002" + _scene + "\u0001" + text))
                    return;
                try
                {
                    var line =
                        "{\"scene\":\""
                        + Escape(_scene)
                        + "\",\"kind\":\""
                        + kind
                        + "\",\"src\":\""
                        + src
                        + "\",\"text\":\""
                        + Escape(text)
                        + "\"}";
                    File.AppendAllText(OutPath, line + "\n", new UTF8Encoding(false));
                }
                catch (Exception e)
                {
                    Plugin.Logger?.LogError("[NovelDump] write failed: " + e.Message);
                }
            }
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            var sb = new StringBuilder(s.Length + 16);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}


