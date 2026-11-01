# dotabyss-translation-zh_Hans

《ドットアビス》（像素深渊）AbyssMod 的**简体中文**翻译资料库。

配套 [AbyssMod](https://github.com/s88037zz/AbyssMod) 使用：插件启动时会从这个仓库
下载 JSON 到本机 `BepInEx/plugins/AbyssMod/cache/translations/`。

## 使用者配置

编辑 `BepInEx/config/AbyssMod.cfg`：

```ini
[Translation]
CDN = https://raw.githubusercontent.com/Blackchocola/dotabyss-translation-zh_Hans/main/translations
Language = zh_Hans
Enabled = true
```

GitHub 连不上时可用镜像：

```ini
CDN = https://gh-proxy.com/https://raw.githubusercontent.com/Blackchocola/dotabyss-translation-zh_Hans/main/translations
```

保存后重启游戏。

## 内容

| 路径 | 内容 |
|------|------|
| `manifest/zh_Hans.json` | 各文件版本哈希 |
| `novels/<场景ID>/zh_Hans.json` | 剧情对话（969 个场景） |
| `names/zh_Hans.json` | 角色名（588 条） |
| `ui_texts/`, `m_*/` | 界面文字与各数据表（道具、技能、任务等） |
| `other/<分类>/`, `add-on/<分类>/` | 机翻补充 / 社区人工校对 |
| `titles/`, `ability_descriptions/` | 剧情标题、技能描述 |

统计：1115 个文件，10.1 MB，生成于 2026-11-01。

## 说明

- 原始译文来自 [s88037zz/dotabyss-translation](https://github.com/s88037zz/dotabyss-translation)（繁体），
  本仓库用 [OpenCC](https://github.com/BYVoid/OpenCC) `tw2sp` 整体转换为简体，
  并叠加了本机用 DeepSeek 补译的新剧情/界面文本。
- 只包含文本，不含游戏资源。
- 发现错译欢迎提 issue / PR；只改**值**，不要动 key（key 是日文原文，改了会查不到）。
