# NovelDump（自用插件，不属于 AbyssMod）

BepInEx 插件：把剧情原文写到 `BepInEx/plugins/NovelDump/novel_dump.jsonl`，并在渲染层直接查翻译字典替换文本。

装法：把 `NovelDump.dll` 放到 `游戏目录/BepInEx/plugins/NovelDump/`。

源码 `Plugin.cs`；编译用 .NET 6 + 游戏自带的 BepInEx/interop 程序集（见项目内 `plugin/build.rsp`）。
