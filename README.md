# Chill AI Mod

**为《Chill with You: Lo-Fi Story》添加基于 LLM 的 AI 文字陪伴对话（BepInEx 插件），让 Satone 支持长期记忆、主动搭话、现实时间上下文与熟悉度变化。**

## 特色

- 使用兼容 OpenAI Chat Completions 格式的 LLM API 生成对话文本。
- 支持 DeepSeek、OpenRouter、Ollama、Gemini OpenAI 兼容接口等服务。
- 游戏内聊天窗口可拖拽、缩放，并支持 F9/F10 快捷开关。
- 支持长期记忆：
  - 可读聊天日志：`ChatHistory/*.txt`
  - 结构化长期记忆：`Memory.jsonl`
  - 自动滚动摘要：`Summary.txt`
- 支持主动搭话：玩家安静一段时间后，Satone 可以自然开启轻量话题。
- 支持现实时间与节日上下文：时段、星期、公历节日、农历节日、会话状态等会注入系统提示词。
- 支持熟悉度系统：不显示数值，只影响称呼、语气、主动频率和亲近程度。
- Unity 桥接层带安全检查，便于后续扩展角色动作、视线与表情联动。

## 安装说明

### 安装 BepInEx 前置

1. 在 Steam 中右键游戏 -> 管理 -> 浏览本地文件，进入游戏根目录。
2. 安装适用于该游戏的 BepInEx。
3. 运行一次游戏，确认生成了 `BepInEx` 目录以及其中的 `config`、`plugins` 等目录。

> 如果没有生成 `BepInEx/plugins`，说明 BepInEx 尚未正确加载。请先解决前置问题，再继续安装 Mod。

### 安装 Mod

1. 构建或获取 `AIChat.dll`。
2. 将 `AIChat.dll` 放入游戏目录下：

   ```text
   BepInEx/plugins/AIChat.dll
   ```

3. 启动游戏。
4. 按 `F9` 或 `F10` 打开 AIChat 界面；也可以点击右侧生成的 AIChat 图标。
5. 展开 `API 配置 (DeepSeek)`，填写：
   - `API URL`
   - `API Key`
   - `模型名称`
6. 点击 `保存设置`。

API URL 示例：

```text
DeepSeek:   https://api.deepseek.com/chat/completions
OpenRouter: https://openrouter.ai/api/v1/chat/completions
Ollama:     http://127.0.0.1:11434/v1/chat/completions
Gemini:     https://generativelanguage.googleapis.com/v1beta/openai/chat/completions
```

注意：聊天内容会发送到你配置的 API 服务。请留心 API Key、聊天内容和所用服务的隐私策略。

## 使用与设置

### 游戏内界面

- 打开/关闭 Mod 窗口：按 `F9` 或 `F10`。
- 也可以点击游戏右侧的 AIChat 图标。
- 右下角可以拖拽调整窗口大小，松开鼠标后会保存尺寸。
- `保存设置` 会将配置写入 BepInEx 配置文件。

配置文件位置：

```text
BepInEx/config/com.username.chillaimod.cfg
```

### LLM 配置

在 `API 配置 (DeepSeek)` 中设置：

- `API URL`：兼容 Chat Completions 的接口地址。
- `API Key`：你的 API 密钥。
- `模型名称`：例如 `deepseek-chat`、`gpt-4o-mini`、`qwen2.5` 等，取决于你使用的服务。

### 界面与交互

在 `界面与交互设置` 中可以设置：

- 窗口宽度
- 窗口基础高度
- 背景透明度
- 是否反转 Enter 行为
- 是否启用主动搭话
- 主动搭话间隔

主动搭话只会在聊天窗口打开、输入框为空、AI 未处理请求且已有聊天历史时触发。

### 人设及上下文

在 `人设及上下文` 中可以设置：

- `启用增强互动人设`
- `接入现实时间`
- `SystemPrompt`

默认人设是 Satone（さとね）：一个热爱写诗、想象力丰富、温柔俏皮，并喜欢歌手许嵩的女孩。你可以直接在游戏内修改系统提示词。

现实时间上下文会提供：

- 当前日期与时间
- 星期
- 时段：凌晨、清晨、上午、中午、下午、傍晚、晚上、深夜
- 节日：元旦、春节、元宵、端午、中秋、七夕、国庆、圣诞等
- 本次会话时长
- 距玩家上次主动发言多久
- 本轮触发原因：玩家主动输入或系统主动搭话

### 长期记忆

长期记忆相关文件位于：

```text
BepInEx/config/ChillAIMod/
```

主要文件：

```text
Summary.txt             长期摘要
Memory.jsonl            结构化长期记忆
ChatHistory/*.txt       可读聊天日志
```

说明：

- `ChatHistory/*.txt` 主要用于人类查看。
- `Memory.jsonl` 是长期记忆召回的主要来源。
- 如果想让 AI 不再记得某句话，优先修改 `Memory.jsonl`。
- 修改后可在游戏内点击 `重载长期历史缓存`，或者重启游戏。

`Memory.jsonl` 中每一行是一条记忆，类似：

```json
{"Id":"...","Timestamp":"2026-05-18 21:10:00","Role":"User","Content":"某句话","Tags":"","Importance":2,"Enabled":true,"Pinned":false}
```

可以删除整行，或者把：

```json
"Enabled":true
```

改成：

```json
"Enabled":false
```

### 熟悉度系统

熟悉度系统默认开启，但不会在对话里显示具体分数。它只影响：

- Satone 的称呼方式
- 语气亲近程度
- 是否更自然地提到旧记忆
- 主动搭话频率

关系阶段包括：

```text
初识 -> 熟悉 -> 亲近 -> 信任
```

你也可以在游戏内填写 `称呼偏好`，让 Satone 更自然地称呼你。

## 构建

### 本地构建

需要：

- .NET SDK
- 游戏本体中的 Unity 依赖
- BepInEx core 依赖

项目文件中默认依赖路径为：

```xml
<UnityDepsPath>F:\Steam\steamapps\common\Chill with You Lo-Fi Story\Chill With You_Data\Managed</UnityDepsPath>
<BepInExPath>F:\Steam\steamapps\common\Chill with You Lo-Fi Story\BepInEx\core</BepInExPath>
```

如果你的游戏安装位置不同，请先修改 [AIChat.csproj](AIChat/AIChat.csproj) 中的路径。

构建 Release：

```powershell
dotnet build .\AIChat.sln -c Release
```

构建产物：

```text
AIChat/bin/Release/net472/AIChat.dll
```

将该 DLL 复制到：

```text
游戏目录/BepInEx/plugins/
```

## 问题排查

### Mod 没有显示

- 确认 BepInEx 已正确安装。
- 确认 `AIChat.dll` 位于 `BepInEx/plugins`。
- 查看 `BepInEx/LogOutput.log`。
- 进入游戏后尝试按 `F9` 或 `F10`。

### 找不到 plugins 文件夹

先运行一次游戏。BepInEx 正确加载后会自动生成目录结构。

### API 报错

- 检查 API URL 是否正确。
- 检查 API Key 是否有效。
- 检查模型名称是否被该服务支持。
- 如果使用本地 Ollama，确认服务已经启动。
- 如果出现 401，通常是 API Key 错误。
- 如果出现 429，通常是频率限制或额度不足。

### AI 不记得旧聊天

- 确认 `使用长期记忆参与回复` 已开启。
- 确认 `Memory.jsonl` 中有对应内容。
- 修改记忆文件后，点击 `重载长期历史缓存` 或重启游戏。
- 如果 `MaxMemoryResults` 设置为 0，则不会召回历史片段。

### 想重置配置

删除配置文件后重启游戏：

```text
BepInEx/config/com.username.chillaimod.cfg
```

如果想清空长期记忆，可备份后删除：

```text
BepInEx/config/ChillAIMod/
```

## 当前限制

- 当前版本主要支持文字聊天，不包含 TTS 语音朗读和 ASR 语音输入。
- 长期记忆目前是关键词召回，不是向量检索。
- Unity 角色动作桥接已经做了安全封装，但主聊天流程尚未接入完整情绪/动作联动。

## 声明

本项目使用或依赖以下项目：

- [BepInEx](https://github.com/BepInEx/BepInEx)：Unity/XNA 游戏 Mod 框架。
- [Harmony](https://github.com/pardeike/Harmony)：运行时补丁工具。
- Unity Engine：游戏引擎库，仅用于构建与运行时引用。

本项目与游戏官方无关。
