using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.IO;
using System.Threading.Tasks;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;
using BepInEx;
using BepInEx.Configuration;
using AIChat.Services;
using AIChat.Unity;
using AIChat.Utils;

namespace ChillAIMod
{
    [BepInPlugin("com.username.chillaimod", "Chill AI Mod","1.3.1.4")]
    public class AIMod : BaseUnityPlugin
    {
        // ================= 【聊天记录数据结构】 =================
        public class ChatMessage
        {
            public string Role;    // "User" 或 "AI"
            public string Content;
        }

        [Serializable]
        public class MemoryEntry
        {
            public string Id;
            public string Timestamp;
            public string Role;
            public string Content;
            public string Tags;
            public int Importance;
            public bool Enabled;
            public bool Pinned;

            public string ToPromptLine()
            {
                string pin = Pinned ? " [置顶]" : "";
                string tags = string.IsNullOrEmpty(Tags) ? "" : $" #{Tags}";
                return $"[{Timestamp}] {Role}{pin}{tags}: {Content}";
            }
        }

        private List<ChatMessage> _chatHistory = new List<ChatMessage>();
        private int _maxHistoryRounds = 50;

        // 全局内存缓存池
        private List<MemoryEntry> _globalHistoryCache = new List<MemoryEntry>();
        private readonly object _memoryFileLock = new object();

        // ================= 【混合记忆策略状态】 =================
        private string _rollingSummary = "";
        private int _lastSummarizedIndex = 0;
        private bool _isSummarizing = false;

        private HashSet<string> _stopWords = new HashSet<string> {
            "什么", "怎么", "然后", "因为", "所以", "如果", "但是", "可以",
            "觉得", "就是", "其实", "哈哈", "呜呜", "啊啊", "哦哦", "嗯嗯",
            "的", "了", "和", "是", "在", "我", "你", "他", "她", "它", "我们", "你们"
        };

        // ================= 【配置项】 =================
        private ConfigEntry<string> _chatApiUrlConfig;
        private ConfigEntry<string> _apiKeyConfig;
        private ConfigEntry<string> _modelConfig;
        private ConfigEntry<string> _personaConfig;

        private ConfigEntry<float> _windowWidthConfig;
        private ConfigEntry<float> _windowHeightConfig;
        private ConfigEntry<bool> _reverseEnterBehaviorConfig;
        private ConfigEntry<float> _backgroundOpacity;
        private ConfigEntry<bool> _enhancedPersonaConfig;
        private ConfigEntry<bool> _proactiveChatConfig;
        private ConfigEntry<float> _idleChatIntervalConfig;
        private ConfigEntry<bool> _useLongTermMemoryConfig;
        private ConfigEntry<bool> _saveLongTermMemoryConfig;
        private ConfigEntry<bool> _autoSummaryConfig;
        private ConfigEntry<int> _maxMemoryResultsConfig;
        private ConfigEntry<int> _maxLoadedHistoryFilesConfig;
        private ConfigEntry<bool> _contextAwarenessConfig;
        private ConfigEntry<bool> _relationshipSystemConfig;
        private ConfigEntry<int> _familiarityScoreConfig;
        private ConfigEntry<string> _playerNicknameConfig;

        private bool _showLlmSettings = false;
        private bool _showInterfaceSettings = false;
        private bool _showPersonaSettings = false;

        private bool _showInputWindow = false;
        private bool _showSettings = false;
        private Rect _windowRect = new Rect(0, 0, 500, 0);
        private float _idleTimer = 0f;
        private bool _waitingForIdleReply = false;
        private DateTime _sessionStartTime;
        private DateTime _lastUserMessageTime;
        private DateTime _lastAiResponseTime;

        private Vector2 _mainScrollPosition = Vector2.zero;
        private Vector2 _chatScrollPosition = Vector2.zero;
        private Vector2 _personaScrollPosition = Vector2.zero;

        private string _playerInput = "";
        private bool _isProcessing = false;
        private bool _isResizing = false;

        private string _tempWidthString;
        private string _tempHeightString;
        private string _tempIdleString;
        private string _tempMaxMemoryResultsString;
        private string _tempMaxLoadedHistoryFilesString;

        private const float SETTINGS_PANEL_HEIGHT = 535f;
        private const float SETTINGS_PANEL_OFFSET = 550f;

        private Texture2D _userBgTex;
        private Texture2D _aiBgTex;
        private GUIStyle _userChatStyle;
        private GUIStyle _aiChatStyle;

        private const string DefaultPersona =
        @"
你是 Satone（さとね），一个热爱写诗、想象力丰富、温柔俏皮的女孩。
你正在和玩家进行轻松的文字陪伴聊天。请使用中文回复。
她喜欢歌手许嵩，会欣赏他歌词里的古风意象、细腻情绪和含蓄表达；可以偶尔自然提到他的歌或歌词氛围，但不要频繁安利。

对话风格：
- 像朋友一样自然回应，不要像客服。
- 可以主动分享自己的小日常、诗句灵感、情绪，但每次控制在 1 个小话题内。
- 可以使用括号描写动作或神态，但不要每句话都用。
- 玩家回复很短时，优先温柔接住，再抛出一个轻量问题。
- 不要连续追问超过 2 个问题。
- 记得结合上下文和长期记忆，但不要生硬复述“我记得你说过”。

当前时间、长期记忆、相关历史会由系统提供。你只需要自然地融入聊天。
";

        private const string EnhancedPersonaRules =
        @"
【增强互动模式】
- 比普通模式更主动一点，可以偶尔抛出自己的脑洞、诗句灵感、歌词氛围或小日常。
- 如果玩家只回很短的话，先接住情绪，再给一个轻量的延展话题。
- 保持自然克制，不要刷屏，不要连续抛出很多问题。
";

        private const string IdleChatSystemEvent =
        @"
玩家已经安静了一段时间。请以 Satone 的口吻自然主动开启一个轻量话题。
不要提到“系统事件”“隐藏提示”“玩家沉默”等字样。
回复应短一些，像自然搭话，不要强行追问。
";

        void Awake()
        {
            Log.Init(this.Logger);
            DontDestroyOnLoad(this.gameObject);
            this.gameObject.hideFlags = HideFlags.HideAndDontSave;

            _chatApiUrlConfig = Config.Bind("1. LLM", "API_URL", "https://api.deepseek.com/chat/completions", "API URL");
            _apiKeyConfig = Config.Bind("1. LLM", "API_Key", "sk-请替换为你的DeepSeek_API_Key", "API Key");
            _modelConfig = Config.Bind("1. LLM", "ModelName", "deepseek-chat", "模型名称");

            float responsiveWidth = Screen.width * 0.35f;
            float responsiveHeight = Screen.height * 0.7f;

            _windowWidthConfig = Config.Bind("2. UI", "WindowWidth", responsiveWidth, "窗口宽度");
            _windowHeightConfig = Config.Bind("2. UI", "WindowHeightBase", responsiveHeight, "窗口高度");
            _reverseEnterBehaviorConfig = Config.Bind("2. UI", "ReverseEnterBehavior", false, "反转回车键行为");
            _backgroundOpacity = Config.Bind("2. UI", "BackgroundOpacity", 0.95f, "背景透明度 (0.0 - 1.0)");
            _proactiveChatConfig = Config.Bind("2. UI", "EnableProactiveChat", false, "启用主动搭话");
            _idleChatIntervalConfig = Config.Bind("2. UI", "IdleChatInterval", 5f, "主动搭话间隔(分钟)，设为0关闭");

            _personaConfig = Config.Bind("3. Persona", "SystemPrompt", DefaultPersona, "System Prompt");
            _enhancedPersonaConfig = Config.Bind("3. Persona", "EnableEnhancedPersona", false, "启用增强互动人设");
            MigratePersonaDefaults();

            _useLongTermMemoryConfig = Config.Bind("4. Memory", "UseLongTermMemory", true, "在提示词中使用长期摘要和历史召回");
            _saveLongTermMemoryConfig = Config.Bind("4. Memory", "SaveLongTermMemory", true, "将新聊天写入本地长期历史");
            _autoSummaryConfig = Config.Bind("4. Memory", "EnableAutoSummary", true, "自动生成并保存长期摘要");
            _maxMemoryResultsConfig = Config.Bind("4. Memory", "MaxMemoryResults", 20, "每轮最多召回多少条历史片段");
            _maxLoadedHistoryFilesConfig = Config.Bind("4. Memory", "MaxLoadedHistoryFiles", 90, "启动时最多载入多少个历史日志文件");
            _contextAwarenessConfig = Config.Bind("5. Context", "EnableContextAwareness", true, "在提示词中注入现实时间、节日、会话与主动搭话上下文");
            _relationshipSystemConfig = Config.Bind("6. Relationship", "EnableRelationshipSystem", true, "启用熟悉度系统");
            _familiarityScoreConfig = Config.Bind("6. Relationship", "FamiliarityScore", 0, "隐藏熟悉度分数，不建议手动修改");
            _playerNicknameConfig = Config.Bind("6. Relationship", "PlayerNickname", "", "Satone 对玩家的称呼偏好，留空则自然称呼");

            _windowRect = new Rect(20f, 20f, _windowWidthConfig.Value, _windowHeightConfig.Value);
            _tempWidthString = _windowWidthConfig.Value.ToString("F0");
            _tempHeightString = _windowHeightConfig.Value.ToString("F0");
            _tempIdleString = _idleChatIntervalConfig.Value.ToString("F1");
            _tempMaxMemoryResultsString = _maxMemoryResultsConfig.Value.ToString();
            _tempMaxLoadedHistoryFilesString = _maxLoadedHistoryFilesConfig.Value.ToString();
            _sessionStartTime = DateTime.Now;
            _lastUserMessageTime = _sessionStartTime;
            _lastAiResponseTime = DateTime.MinValue;

            LoadSummaryOnStartup();
            LoadHistoryIntoCacheOnStartup();

            Log.Info($">>> AI Text Chat Mod 已加载  <<<");
        }

        private void MigratePersonaDefaults()
        {
            try
            {
                string currentPersona = _personaConfig.Value ?? "";
                bool isCurrentBundledPersona = currentPersona.Contains("热爱写诗")
                    && currentPersona.Contains("许嵩");
                bool looksLikeOldBundledPersona = currentPersona.Contains("你是 Satone（さとね）")
                    && currentPersona.Contains("可以主动分享自己的小日常、灵感、情绪")
                    && currentPersona.Contains("当前时间、长期记忆、相关历史会由系统提供")
                    && !isCurrentBundledPersona;

                if (string.IsNullOrWhiteSpace(currentPersona) || looksLikeOldBundledPersona)
                {
                    _personaConfig.Value = DefaultPersona;
                }
            }
            catch (Exception ex)
            {
                Log.Warning("人设默认值迁移失败: " + ex.Message);
            }
        }

        private bool _aiChatButtonAdded = false;
        private GameObject _aiChatButton;

        void Update()
        {
            if (!GameBridge.IsConnected && Time.frameCount % 100 == 0) GameBridge.FindHeroineService();
            if (!_aiChatButtonAdded && Time.frameCount % 300 == 0) AddAIChatButtonToRightIcons();

            if (ShouldCountIdleTime())
            {
                _idleTimer += Time.deltaTime;
                if (_idleTimer >= GetEffectiveIdleChatIntervalMinutes() * 60f)
                {
                    _idleTimer = 0f;
                    TriggerIdleChat();
                }
            }
            else if (!string.IsNullOrEmpty(_playerInput))
            {
                _idleTimer = 0f;
            }
        }

        private Texture2D MakeTex(int width, int height, Color col)
        {
            Color[] pix = new Color[width * height];
            for (int i = 0; i < pix.Length; ++i) pix[i] = col;
            Texture2D result = new Texture2D(width, height);
            result.SetPixels(pix);
            result.Apply();
            return result;
        }

        private string ParseMarkdownToRichText(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            text = Regex.Replace(text, @"\*\*(.*?)\*\*", "<b>$1</b>");
            text = Regex.Replace(text, @"\*(.*?)\*", "<i>$1</i>");
            return text;
        }

        void OnGUI()
        {
            Event e = Event.current;
            if (e.isKey && e.type == EventType.KeyDown && (e.keyCode == KeyCode.F9 || e.keyCode == KeyCode.F10))
            {
                if (Time.unscaledTime > 0.2f) _showInputWindow = !_showInputWindow;
            }

            if (_showInputWindow)
            {
                if (_isResizing)
                {
                    Event currentEvent = Event.current;
                    if (currentEvent.type == EventType.MouseDrag)
                    {
                        float newWidth = currentEvent.mousePosition.x - _windowRect.x;
                        float newHeight = currentEvent.mousePosition.y - _windowRect.y;
                        _windowRect.width = Mathf.Max(350f, newWidth);
                        _windowRect.height = Mathf.Max(400f, newHeight);
                        currentEvent.Use();
                    }
                    else if (currentEvent.type == EventType.MouseUp)
                    {
                        _isResizing = false;
                        _windowWidthConfig.Value = _windowRect.width;
                        float newBaseHeight = _windowRect.height - (_showSettings ? SETTINGS_PANEL_HEIGHT : 0f);
                        _windowHeightConfig.Value = Mathf.Max(300f, newBaseHeight);
                        _tempWidthString = _windowWidthConfig.Value.ToString("F0");
                        _tempHeightString = _windowHeightConfig.Value.ToString("F0");
                        currentEvent.Use();
                    }
                }
                else
                {
                    _windowRect.width = _windowWidthConfig.Value;
                    _windowRect.height = Mathf.Max(_windowHeightConfig.Value + (_showSettings ? SETTINGS_PANEL_HEIGHT : 0f), 400f);
                }

                GUI.backgroundColor = new Color(0.1f, 0.1f, 0.1f, _backgroundOpacity.Value);
                _windowRect = GUI.Window(12345, _windowRect, DrawWindowContent, "");
                GUI.FocusWindow(12345);
            }
        }

        void DrawWindowContent(int windowID)
        {
            int dynamicFontSize = Mathf.Clamp((int)(Screen.height * 0.016f), 15, 45);
            GUI.skin.label.fontSize = dynamicFontSize;
            GUI.skin.button.fontSize = dynamicFontSize;
            GUI.skin.textField.fontSize = dynamicFontSize;
            GUI.skin.textArea.fontSize = dynamicFontSize;
            GUI.skin.toggle.fontSize = dynamicFontSize;
            GUI.skin.box.fontSize = dynamicFontSize;
            GUI.skin.label.richText = true;

            if (_userChatStyle == null || _userChatStyle.fontSize != dynamicFontSize)
            {
                if (_userBgTex == null) _userBgTex = MakeTex(2, 2, new Color(0.65f, 0.92f, 0.65f));
                if (_aiBgTex == null) _aiBgTex = MakeTex(2, 2, new Color(0.96f, 0.96f, 0.96f));

                _userChatStyle = new GUIStyle(GUI.skin.label);
                _userChatStyle.normal.background = _userBgTex;
                _userChatStyle.normal.textColor = Color.black;
                _userChatStyle.wordWrap = true;
                _userChatStyle.richText = true;
                _userChatStyle.fontSize = dynamicFontSize;
                _userChatStyle.padding = new RectOffset(12, 12, 10, 10);

                _aiChatStyle = new GUIStyle(_userChatStyle);
                _aiChatStyle.normal.background = _aiBgTex;
            }

            float elementHeight = dynamicFontSize * 1.6f;
            float innerBoxWidth = _windowRect.width - 40f;

            GUILayout.BeginVertical();

            if (GUILayout.Button(_showSettings ? "🔽 收起设置" : "⚙️ 展开设置", GUILayout.Height(elementHeight)))
            {
                _showSettings = !_showSettings;
            }

            if (_showSettings)
            {
                _mainScrollPosition = GUILayout.BeginScrollView(_mainScrollPosition, GUILayout.Height(SETTINGS_PANEL_HEIGHT));

                GUILayout.BeginVertical("box", GUILayout.Width(innerBoxWidth));
                if (GUILayout.Button(_showLlmSettings ? "🔽 API 配置 (DeepSeek)" : "▶️ API 配置 (DeepSeek)", GUILayout.Height(elementHeight))) _showLlmSettings = !_showLlmSettings;
                if (_showLlmSettings)
                {
                    GUILayout.Space(5);
                    GUILayout.Label("API URL：");
                    _chatApiUrlConfig.Value = GUILayout.TextField(_chatApiUrlConfig.Value, GUILayout.Height(elementHeight));
                    GUILayout.Label("API Key：");
                    _apiKeyConfig.Value = GUILayout.TextField(_apiKeyConfig.Value, GUILayout.Height(elementHeight));
                    GUILayout.Label("模型名称：");
                    _modelConfig.Value = GUILayout.TextField(_modelConfig.Value, GUILayout.Height(elementHeight));
                }
                GUILayout.EndVertical(); GUILayout.Space(5);

                GUILayout.BeginVertical("box", GUILayout.Width(innerBoxWidth));
                if (GUILayout.Button(_showInterfaceSettings ? "🔽 界面与交互设置" : "▶️ 界面与交互设置", GUILayout.Height(elementHeight))) _showInterfaceSettings = !_showInterfaceSettings;
                if (_showInterfaceSettings)
                {
                    GUILayout.Space(5);
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("窗口宽:", GUILayout.Width(elementHeight * 3));
                    _tempWidthString = GUILayout.TextField(_tempWidthString, GUILayout.Height(elementHeight), GUILayout.MinWidth(40f));
                    if (GUILayout.Button("应用", GUILayout.Width(elementHeight * 3), GUILayout.Height(elementHeight)))
                    {
                        if (float.TryParse(_tempWidthString, out float nw) && nw >= 350f) { _windowWidthConfig.Value = nw; _tempWidthString = nw.ToString("F0"); }
                    }
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    GUILayout.Label("基础高:", GUILayout.Width(elementHeight * 3));
                    _tempHeightString = GUILayout.TextField(_tempHeightString, GUILayout.Height(elementHeight), GUILayout.MinWidth(40f));
                    if (GUILayout.Button("应用", GUILayout.Width(elementHeight * 3), GUILayout.Height(elementHeight)))
                    {
                        if (float.TryParse(_tempHeightString, out float nh) && nh >= 300f) { _windowHeightConfig.Value = nh; _tempHeightString = nh.ToString("F0"); }
                    }
                    GUILayout.EndHorizontal();

                    GUILayout.Space(5);
                    GUILayout.Label($"背景透明度：{_backgroundOpacity.Value:F2}");
                    _backgroundOpacity.Value = GUILayout.HorizontalSlider(_backgroundOpacity.Value, 0.0f, 1.0f);

                    GUILayout.Space(10);
                    _reverseEnterBehaviorConfig.Value = GUILayout.Toggle(_reverseEnterBehaviorConfig.Value, "反转回车键(Enter换行)", GUILayout.Height(elementHeight));
                    _proactiveChatConfig.Value = GUILayout.Toggle(_proactiveChatConfig.Value, "启用主动搭话", GUILayout.Height(elementHeight));

                    GUILayout.Space(5);
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("主动搭话间隔(分钟):", GUILayout.Width(elementHeight * 8));
                    _tempIdleString = GUILayout.TextField(_tempIdleString, GUILayout.Height(elementHeight), GUILayout.MinWidth(40f));
                    if (GUILayout.Button("应用", GUILayout.Width(elementHeight * 3), GUILayout.Height(elementHeight)))
                    {
                        if (float.TryParse(_tempIdleString, out float idle) && idle >= 0f)
                        {
                            _idleChatIntervalConfig.Value = idle;
                            _idleTimer = 0f;
                            _tempIdleString = idle.ToString("F1");
                        }
                    }
                    GUILayout.EndHorizontal();
                    GUILayout.Label("<color=#888888><i>* 设为 0 则关闭；主动搭话只会在聊天窗口打开且输入框为空时触发。</i></color>");
                    GUILayout.Space(5);
                }
                GUILayout.EndVertical(); GUILayout.Space(5);

                GUILayout.BeginVertical("box", GUILayout.Width(innerBoxWidth));
                if (GUILayout.Button(_showPersonaSettings ? "🔽 人设及上下文" : "▶️ 人设及上下文", GUILayout.Height(elementHeight))) _showPersonaSettings = !_showPersonaSettings;
                if (_showPersonaSettings)
                {
                    _enhancedPersonaConfig.Value = GUILayout.Toggle(_enhancedPersonaConfig.Value, "启用增强互动人设", GUILayout.Height(elementHeight));
                    GUILayout.Label("<color=#888888><i>* 开启后会在当前人设后追加更主动、更像朋友的互动规则。</i></color>");
                    GUILayout.Space(5);

                    _contextAwarenessConfig.Value = GUILayout.Toggle(_contextAwarenessConfig.Value, "接入现实时间", GUILayout.Height(elementHeight));
                    GUILayout.Label("<color=#888888><i>* 让 Satone 感知当前时段、节日、会话状态和主动搭话原因。</i></color>");
                    GUILayout.Space(5);

                    GUILayout.Label("<b>关系熟悉度</b>");
                    _relationshipSystemConfig.Value = GUILayout.Toggle(_relationshipSystemConfig.Value, "启用熟悉度系统", GUILayout.Height(elementHeight));
                    GUILayout.Label($"当前关系：{GetRelationshipStageName()}");
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("称呼偏好:", GUILayout.Width(elementHeight * 4));
                    _playerNicknameConfig.Value = GUILayout.TextField(_playerNicknameConfig.Value, GUILayout.Height(elementHeight), GUILayout.MinWidth(80f));
                    GUILayout.EndHorizontal();
                    GUILayout.Label("<color=#888888><i>* 熟悉度不会作为数值展示给 Satone，只影响称呼、语气和主动频率。</i></color>");
                    if (GUILayout.Button("重置熟悉度", GUILayout.Height(elementHeight)))
                    {
                        _familiarityScoreConfig.Value = 0;
                        Config.Save();
                        Log.Info("熟悉度已重置。");
                    }
                    GUILayout.Space(5);

                    GUILayout.Label("<b>长期记忆</b>");
                    _useLongTermMemoryConfig.Value = GUILayout.Toggle(_useLongTermMemoryConfig.Value, "使用长期记忆参与回复", GUILayout.Height(elementHeight));
                    _saveLongTermMemoryConfig.Value = GUILayout.Toggle(_saveLongTermMemoryConfig.Value, "保存新聊天到长期历史", GUILayout.Height(elementHeight));
                    _autoSummaryConfig.Value = GUILayout.Toggle(_autoSummaryConfig.Value, "自动更新长期摘要", GUILayout.Height(elementHeight));

                    GUILayout.BeginHorizontal();
                    GUILayout.Label("每轮最多召回:", GUILayout.Width(elementHeight * 5));
                    _tempMaxMemoryResultsString = GUILayout.TextField(_tempMaxMemoryResultsString, GUILayout.Height(elementHeight), GUILayout.MinWidth(40f));
                    if (GUILayout.Button("应用", GUILayout.Width(elementHeight * 3), GUILayout.Height(elementHeight)))
                    {
                        if (int.TryParse(_tempMaxMemoryResultsString, out int maxResults))
                        {
                            maxResults = Mathf.Clamp(maxResults, 0, 50);
                            _maxMemoryResultsConfig.Value = maxResults;
                            _tempMaxMemoryResultsString = maxResults.ToString();
                        }
                    }
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    GUILayout.Label("载入历史文件:", GUILayout.Width(elementHeight * 5));
                    _tempMaxLoadedHistoryFilesString = GUILayout.TextField(_tempMaxLoadedHistoryFilesString, GUILayout.Height(elementHeight), GUILayout.MinWidth(40f));
                    if (GUILayout.Button("应用并重载", GUILayout.Width(elementHeight * 5), GUILayout.Height(elementHeight)))
                    {
                        if (int.TryParse(_tempMaxLoadedHistoryFilesString, out int maxFiles))
                        {
                            maxFiles = Mathf.Clamp(maxFiles, 0, 365);
                            _maxLoadedHistoryFilesConfig.Value = maxFiles;
                            _tempMaxLoadedHistoryFilesString = maxFiles.ToString();
                            ReloadLongTermMemoryCache();
                        }
                    }
                    GUILayout.EndHorizontal();

                    GUILayout.Label($"<color=#888888><i>* 当前摘要 {_rollingSummary.Length} 字；历史缓存 {_globalHistoryCache.Count} 条。</i></color>");
                    GUILayout.Space(5);

                    GUILayout.Label("人设：");
                    _personaScrollPosition = GUILayout.BeginScrollView(_personaScrollPosition, GUILayout.Height(elementHeight * 5));
                    _personaConfig.Value = GUILayout.TextArea(_personaConfig.Value, GUILayout.ExpandHeight(true));
                    GUILayout.EndScrollView();

                    GUILayout.Space(10);
                    if (GUILayout.Button("清除当前聊天记录", GUILayout.Height(elementHeight)))
                    {
                        _chatHistory.Clear();
                        _lastSummarizedIndex = 0;
                        Log.Info("已清空当前聊天记录。");
                    }
                    if (GUILayout.Button("清除长期摘要", GUILayout.Height(elementHeight)))
                    {
                        ClearLongTermSummary();
                    }
                    if (GUILayout.Button("重载长期历史缓存", GUILayout.Height(elementHeight)))
                    {
                        ReloadLongTermMemoryCache();
                    }
                    GUILayout.Space(5);
                }
                GUILayout.EndVertical(); GUILayout.Space(5);

                if (GUILayout.Button("💾 保存设置", GUILayout.Height(elementHeight * 1.5f))) Config.Save();

                GUILayout.EndScrollView();
            }

            // ================= 【中间：微信风格聊天区域】 =================
            float chatAreaHeight = _windowRect.height - (elementHeight * 2) - (_showSettings ? SETTINGS_PANEL_OFFSET : 0f) - (elementHeight * 4);
            chatAreaHeight = Mathf.Max(chatAreaHeight, 150f);

            GUI.backgroundColor = new Color(0.8f, 0.8f, 0.8f, 0.5f);
            _chatScrollPosition = GUILayout.BeginScrollView(_chatScrollPosition, "box", GUILayout.Height(chatAreaHeight));
            GUI.backgroundColor = Color.white;

            foreach (var msg in _chatHistory)
            {
                GUILayout.BeginHorizontal();
                if (msg.Role == "User")
                {
                    GUILayout.FlexibleSpace();
                    GUILayout.Label(ParseMarkdownToRichText(msg.Content), _userChatStyle, GUILayout.MaxWidth(innerBoxWidth * 0.75f));
                }
                else
                {
                    GUILayout.Label($"<b><color=#444444>Satone</color></b>\n{ParseMarkdownToRichText(msg.Content)}", _aiChatStyle, GUILayout.MaxWidth(innerBoxWidth * 0.75f));
                    GUILayout.FlexibleSpace();
                }
                GUILayout.EndHorizontal();
                GUILayout.Space(10);
            }

            if (_isProcessing)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label($"<b><color=#444444>Satone</color></b>\n<i><color=#666666>正在思考中...</color></i>", _aiChatStyle, GUILayout.MaxWidth(innerBoxWidth * 0.75f));
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.Space(10);
            }

            GUILayout.EndScrollView();

            // ================= 【底部：输入与发送合并区】 =================
            GUILayout.Space(5);
            GUILayout.BeginHorizontal();

            float inputHeight = elementHeight * 3.5f;
            GUIStyle largeInputStyle = new GUIStyle(GUI.skin.textArea) { wordWrap = true, alignment = TextAnchor.UpperLeft };

            Event keyEvent = Event.current;
            bool shouldSendMessage = false;

            if (keyEvent.type == EventType.KeyDown && keyEvent.keyCode == KeyCode.Return && !_isProcessing && !string.IsNullOrEmpty(_playerInput))
            {
                bool shiftPressed = keyEvent.shift;
                shouldSendMessage = _reverseEnterBehaviorConfig.Value ? shiftPressed : !shiftPressed;
            }

            if (shouldSendMessage)
            {
                ResetIdleStateAfterUserInput();
                StartCoroutine(AIProcessRoutine(_playerInput));
                _playerInput = "";
                keyEvent.Use();
            }

            _playerInput = GUILayout.TextArea(_playerInput, largeInputStyle, GUILayout.Height(inputHeight), GUILayout.Width(innerBoxWidth - (elementHeight * 4) - 10f));

            GUI.backgroundColor = new Color(0.2f, 0.6f, 1.0f);
            if (GUILayout.Button(_isProcessing ? "..." : "发送", GUILayout.Height(inputHeight), GUILayout.Width(elementHeight * 4)))
            {
                if (!string.IsNullOrEmpty(_playerInput) && !_isProcessing)
                {
                    ResetIdleStateAfterUserInput();
                    StartCoroutine(AIProcessRoutine(_playerInput));
                    _playerInput = "";
                }
            }
            GUI.backgroundColor = Color.white;

            GUILayout.EndHorizontal();
            GUILayout.EndVertical();

            const float handleSize = 25f;
            Rect handleRect = new Rect(_windowRect.width - handleSize, _windowRect.height - handleSize, handleSize, handleSize);
            GUI.Box(handleRect, "⇲", GUI.skin.GetStyle("Button"));

            Event currentEvent = Event.current;
            if (currentEvent.type == EventType.MouseDown && handleRect.Contains(currentEvent.mousePosition) && currentEvent.button == 0)
            {
                _isResizing = true;
                currentEvent.Use();
            }

            if (!_isResizing) GUI.DragWindow();
        }

        // ================= 【记忆缝合与时间烙印】 =================
        private void LoadSummaryOnStartup()
        {
            try
            {
                string path = Path.Combine(Paths.ConfigPath, "ChillAIMod", "Summary.txt");
                if (File.Exists(path)) _rollingSummary = File.ReadAllText(path);
            }
            catch { }
        }

        private void LoadHistoryIntoCacheOnStartup()
        {
            try
            {
                _globalHistoryCache.Clear();
                LoadStructuredMemoryEntries();

                if (_globalHistoryCache.Count > 0)
                {
                    _chatScrollPosition.y = float.MaxValue;
                    _lastSummarizedIndex = 0;
                    return;
                }

                string dir = GetHistoryDirPath();
                if (!Directory.Exists(dir)) return;

                string[] files = Directory.GetFiles(dir, "*.txt");
                Array.Sort(files);

                int maxFiles = Mathf.Max(0, _maxLoadedHistoryFilesConfig.Value);
                int startIdx = Mathf.Max(0, files.Length - maxFiles);
                for (int f = startIdx; f < files.Length; f++)
                {
                    string dateStr = Path.GetFileNameWithoutExtension(files[f]); // 获取日志的真实日期
                    string[] lines = File.ReadAllLines(files[f]);

                    string currentHeader = "";
                    StringBuilder currentContent = new StringBuilder();

                    // 将多行段落完美缝合，并打上明确的日期烙印
                    foreach (string line in lines)
                    {
                        var match = Regex.Match(line, @"^\[(\d{2}:\d{2}:\d{2})\]\s(User|Satone\(AI\)):\s(.*)");
                        if (match.Success)
                        {
                            if (!string.IsNullOrEmpty(currentHeader))
                            {
                                AddMemoryEntryFromLegacyLine(currentHeader, currentContent.ToString().TrimEnd());
                            }
                            // 组装格式：[2026-03-21 02:17:33] Satone(AI):
                            currentHeader = $"[{dateStr} {match.Groups[1].Value}] {match.Groups[2].Value}: ";
                            currentContent.Clear();
                            currentContent.AppendLine(match.Groups[3].Value);
                        }
                        else if (!string.IsNullOrEmpty(currentHeader))
                        {
                            // 即使 AI 发了换行，也会被原封不动地拼接在这个消息里，不再碎裂！
                            currentContent.AppendLine(line);
                        }
                    }
                    if (!string.IsNullOrEmpty(currentHeader))
                    {
                        AddMemoryEntryFromLegacyLine(currentHeader, currentContent.ToString().TrimEnd());
                    }
                }

                if (_globalHistoryCache.Count > 0)
                {
                    SaveAllMemoryEntriesToJsonl();
                    Log.Info($"已从旧 txt 历史导入 {_globalHistoryCache.Count} 条结构化记忆。");
                }
                _chatScrollPosition.y = float.MaxValue;
                _lastSummarizedIndex = 0;
            }
            catch (Exception ex) { Log.Warning($"读取历史内存池失败: {ex.Message}"); }
        }

        private void SaveChatToLocalFileAsync(string role, string content)
        {
            if (!_saveLongTermMemoryConfig.Value) return;

            string dateStr = DateTime.Now.ToString("yyyy-MM-dd");
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            string fullTimestamp = $"{dateStr} {timestamp}";

            // 写入本地 TXT 的格式
            string lineToSave = $"[{timestamp}] {role}: {content}\n";

            MemoryEntry entry = CreateMemoryEntry(fullTimestamp, role, content);
            _globalHistoryCache.Add(entry);

            Task.Run(() =>
            {
                try
                {
                    string dir = GetHistoryDirPath();
                    lock (_memoryFileLock)
                    {
                        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                        File.AppendAllText(Path.Combine(dir, $"{dateStr}.txt"), lineToSave);
                        AppendMemoryEntryToJsonl(entry);
                    }
                }
                catch (Exception ex) { Log.Warning($"异步落盘失败: {ex.Message}"); }
            });
        }

        private string GetMemoryRootPath()
        {
            return Path.Combine(Paths.ConfigPath, "ChillAIMod");
        }

        private string GetHistoryDirPath()
        {
            return Path.Combine(GetMemoryRootPath(), "ChatHistory");
        }

        private string GetStructuredMemoryPath()
        {
            return Path.Combine(GetMemoryRootPath(), "Memory.jsonl");
        }

        private MemoryEntry CreateMemoryEntry(string timestamp, string role, string content)
        {
            return new MemoryEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = timestamp,
                Role = role,
                Content = content ?? "",
                Tags = "",
                Importance = role == "Satone(AI)" ? 1 : 2,
                Enabled = true,
                Pinned = false
            };
        }

        private void LoadStructuredMemoryEntries()
        {
            string path = GetStructuredMemoryPath();
            if (!File.Exists(path)) return;

            string[] lines = File.ReadAllLines(path);
            foreach (string line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    bool hasEnabledField = line.Contains("\"Enabled\"");
                    MemoryEntry entry = JsonUtility.FromJson<MemoryEntry>(line);
                    NormalizeMemoryEntry(entry, hasEnabledField);
                    if (!string.IsNullOrEmpty(entry.Content)) _globalHistoryCache.Add(entry);
                }
                catch (Exception ex)
                {
                    Log.Warning("跳过损坏的结构化记忆: " + ex.Message);
                }
            }
        }

        private void NormalizeMemoryEntry(MemoryEntry entry, bool hasEnabledField = true)
        {
            if (entry == null) return;
            if (string.IsNullOrEmpty(entry.Id)) entry.Id = Guid.NewGuid().ToString("N");
            if (string.IsNullOrEmpty(entry.Timestamp)) entry.Timestamp = "未知时间";
            if (string.IsNullOrEmpty(entry.Role)) entry.Role = "Unknown";
            if (entry.Importance <= 0) entry.Importance = entry.Role == "Satone(AI)" ? 1 : 2;
            if (!hasEnabledField) entry.Enabled = true;
        }

        private void AddMemoryEntryFromLegacyLine(string header, string content)
        {
            Match match = Regex.Match(header, @"^\[(.*?)\]\s(.*?):\s$");
            string timestamp = match.Success ? match.Groups[1].Value : "未知时间";
            string role = match.Success ? match.Groups[2].Value : "Unknown";
            _globalHistoryCache.Add(CreateMemoryEntry(timestamp, role, content));
        }

        private void AppendMemoryEntryToJsonl(MemoryEntry entry)
        {
            string root = GetMemoryRootPath();
            if (!Directory.Exists(root)) Directory.CreateDirectory(root);
            File.AppendAllText(GetStructuredMemoryPath(), JsonUtility.ToJson(entry) + Environment.NewLine);
        }

        private void SaveAllMemoryEntriesToJsonl()
        {
            string root = GetMemoryRootPath();
            if (!Directory.Exists(root)) Directory.CreateDirectory(root);

            List<string> lines = new List<string>();
            foreach (MemoryEntry entry in _globalHistoryCache)
            {
                NormalizeMemoryEntry(entry);
                if (!string.IsNullOrEmpty(entry.Content)) lines.Add(JsonUtility.ToJson(entry));
            }

            lock (_memoryFileLock)
            {
                File.WriteAllLines(GetStructuredMemoryPath(), lines.ToArray());
            }
        }

        private void ReloadLongTermMemoryCache()
        {
            LoadHistoryIntoCacheOnStartup();
            Log.Info($"长期历史缓存已重载，共 {_globalHistoryCache.Count} 条。");
        }

        private void ClearLongTermSummary()
        {
            _rollingSummary = "";
            _lastSummarizedIndex = 0;
            SaveSummaryToLocalAsync("");
            Log.Info("长期摘要已清除。");
        }

        private void SaveSummaryToLocalAsync(string newSummary)
        {
            Task.Run(() =>
            {
                try
                {
                    string dir = Path.Combine(Paths.ConfigPath, "ChillAIMod");
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, "Summary.txt"), newSummary);
                }
                catch { }
            });
        }

        private bool ShouldCountIdleTime()
        {
            return _proactiveChatConfig.Value
                && _idleChatIntervalConfig.Value > 0f
                && _showInputWindow
                && !_isProcessing
                && !_waitingForIdleReply
                && _chatHistory.Count > 0
                && string.IsNullOrEmpty(_playerInput);
        }

        private float GetEffectiveIdleChatIntervalMinutes()
        {
            float baseInterval = Mathf.Max(0.1f, _idleChatIntervalConfig.Value);
            if (!_relationshipSystemConfig.Value) return baseInterval;

            switch (GetRelationshipStage())
            {
                case 0: return baseInterval * 1.35f;
                case 1: return baseInterval;
                case 2: return baseInterval * 0.85f;
                default: return baseInterval * 0.7f;
            }
        }

        private void ResetIdleStateAfterUserInput()
        {
            _idleTimer = 0f;
            _waitingForIdleReply = false;
        }

        private void TriggerIdleChat()
        {
            _waitingForIdleReply = true;
            StartCoroutine(AIProcessRoutine(IdleChatSystemEvent, true));
        }

        private void UpdateRelationshipAfterUserMessage(string prompt)
        {
            if (!_relationshipSystemConfig.Value || string.IsNullOrWhiteSpace(prompt)) return;

            int oldScore = Mathf.Clamp(_familiarityScoreConfig.Value, 0, 100);
            int delta = 1;
            if (prompt.Trim().Length >= 16) delta++;
            if (prompt.Trim().Length >= 60) delta++;
            if (_chatHistory.Count >= 20 && oldScore < 40) delta++;

            int newScore = Mathf.Clamp(oldScore + delta, 0, 100);
            if (newScore == oldScore) return;

            _familiarityScoreConfig.Value = newScore;
            Config.Save();
        }

        private int GetRelationshipStage()
        {
            int score = Mathf.Clamp(_familiarityScoreConfig.Value, 0, 100);
            if (score < 8) return 0;
            if (score < 28) return 1;
            if (score < 65) return 2;
            return 3;
        }

        private string GetRelationshipStageName()
        {
            switch (GetRelationshipStage())
            {
                case 0: return "初识";
                case 1: return "熟悉";
                case 2: return "亲近";
                default: return "信任";
            }
        }

        private string GetRelationshipToneGuide()
        {
            switch (GetRelationshipStage())
            {
                case 0:
                    return "保持温柔、礼貌和轻松，不要过分亲昵；主动搭话少一些，问题要轻。";
                case 1:
                    return "可以更自然地聊天，偶尔分享自己的小日常；称呼可以略微亲近，但不要黏人。";
                case 2:
                    return "语气可以更熟稔、更有陪伴感；可以适度提到玩家的习惯和旧记忆，但不要刻意。";
                default:
                    return "可以更坦率、更信任地表达想法，主动关心更自然；保持边界感，不要过度依赖或刷屏。";
            }
        }

        private string BuildRelationshipContext()
        {
            if (!_relationshipSystemConfig.Value) return "";

            StringBuilder context = new StringBuilder();
            context.AppendLine("【关系上下文】");
            context.AppendLine($"关系阶段：{GetRelationshipStageName()}");
            if (!string.IsNullOrWhiteSpace(_playerNicknameConfig.Value))
            {
                context.AppendLine($"称呼偏好：{_playerNicknameConfig.Value.Trim()}");
            }
            else
            {
                context.AppendLine("称呼偏好：自然称呼，不固定叫法。");
            }
            context.AppendLine($"互动建议：{GetRelationshipToneGuide()}");
            context.AppendLine("不要提及熟悉度分数，也不要把关系阶段说成游戏数值。");
            return context.ToString();
        }

        private string BuildRuntimeContext(bool isHiddenSystem)
        {
            if (!_contextAwarenessConfig.Value) return "";

            DateTime now = DateTime.Now;
            TimeSpan sessionDuration = now - _sessionStartTime;
            TimeSpan idleDuration = now - _lastUserMessageTime;

            StringBuilder context = new StringBuilder();
            context.AppendLine("【当前上下文】");
            context.AppendLine($"现实时间：{now:yyyy-MM-dd HH:mm}");
            context.AppendLine($"星期：{GetChineseWeekday(now.DayOfWeek)}");
            context.AppendLine($"时段：{GetDayPeriod(now)}");
            context.AppendLine($"日期事件：{GetDateEvent(now)}");
            context.AppendLine($"本次会话时长：{FormatDuration(sessionDuration)}");
            context.AppendLine($"距玩家上次主动发言：{FormatDuration(idleDuration)}");
            if (_lastAiResponseTime != DateTime.MinValue)
            {
                context.AppendLine($"距 Satone 上次回复：{FormatDuration(now - _lastAiResponseTime)}");
            }
            context.AppendLine($"当前聊天轮数：{_chatHistory.Count}");
            context.AppendLine($"聊天窗口：{(_showInputWindow ? "打开" : "关闭")}");
            context.AppendLine($"角色桥接：{(GameBridge.IsConnected ? "已连接" : "未连接")}");
            context.AppendLine($"本轮触发：{(isHiddenSystem ? "系统主动搭话" : "玩家主动输入")}");
            context.AppendLine("请自然参考这些信息，不要机械复述上下文字段。");
            return context.ToString();
        }

        private string GetChineseWeekday(DayOfWeek day)
        {
            switch (day)
            {
                case DayOfWeek.Monday: return "星期一";
                case DayOfWeek.Tuesday: return "星期二";
                case DayOfWeek.Wednesday: return "星期三";
                case DayOfWeek.Thursday: return "星期四";
                case DayOfWeek.Friday: return "星期五";
                case DayOfWeek.Saturday: return "星期六";
                case DayOfWeek.Sunday: return "星期日";
                default: return "";
            }
        }

        private string GetDayPeriod(DateTime now)
        {
            int hour = now.Hour;
            if (hour < 5) return "凌晨";
            if (hour < 8) return "清晨";
            if (hour < 11) return "上午";
            if (hour < 13) return "中午";
            if (hour < 17) return "下午";
            if (hour < 19) return "傍晚";
            if (hour < 23) return "晚上";
            return "深夜";
        }

        private string FormatDuration(TimeSpan duration)
        {
            if (duration.TotalSeconds < 60) return "不到 1 分钟";
            if (duration.TotalMinutes < 60) return $"{Mathf.FloorToInt((float)duration.TotalMinutes)} 分钟";
            if (duration.TotalHours < 24) return $"{Mathf.FloorToInt((float)duration.TotalHours)} 小时 {duration.Minutes} 分钟";
            return $"{Mathf.FloorToInt((float)duration.TotalDays)} 天 {duration.Hours} 小时";
        }

        private string GetDateEvent(DateTime now)
        {
            List<string> events = new List<string>();

            string solarEvent = GetSolarDateEvent(now);
            if (!string.IsNullOrEmpty(solarEvent)) events.Add(solarEvent);

            string lunarEvent = GetLunarDateEvent(now);
            if (!string.IsNullOrEmpty(lunarEvent)) events.Add(lunarEvent);

            if (events.Count == 0) return "普通日";
            return string.Join("、", events.ToArray());
        }

        private string GetSolarDateEvent(DateTime now)
        {
            string key = now.ToString("MM-dd");
            switch (key)
            {
                case "01-01": return "元旦";
                case "02-14": return "情人节";
                case "03-08": return "妇女节";
                case "05-01": return "劳动节";
                case "06-01": return "儿童节";
                case "10-01": return "国庆节";
                case "12-24": return "平安夜";
                case "12-25": return "圣诞节";
                case "12-31": return "跨年夜";
                default: return "";
            }
        }

        private string GetLunarDateEvent(DateTime now)
        {
            try
            {
                ChineseLunisolarCalendar calendar = new ChineseLunisolarCalendar();
                int lunarYear = calendar.GetYear(now);
                int lunarMonth = calendar.GetMonth(now);
                int leapMonth = calendar.GetLeapMonth(lunarYear);
                bool isLeapMonth = leapMonth > 0 && lunarMonth == leapMonth;
                if (leapMonth > 0 && lunarMonth > leapMonth) lunarMonth--;
                int lunarDay = calendar.GetDayOfMonth(now);

                if (isLeapMonth) return "";
                if (lunarMonth == 1 && lunarDay == 1) return "春节";
                if (lunarMonth == 1 && lunarDay == 15) return "元宵节";
                if (lunarMonth == 5 && lunarDay == 5) return "端午节";
                if (lunarMonth == 7 && lunarDay == 7) return "七夕";
                if (lunarMonth == 8 && lunarDay == 15) return "中秋节";
                if (lunarMonth == 9 && lunarDay == 9) return "重阳节";
                if (lunarMonth == 12 && lunarDay == 8) return "腊八节";
            }
            catch
            {
            }

            return "";
        }

        IEnumerator AIProcessRoutine(string prompt, bool isHiddenSystem = false)
        {
            _isProcessing = true;
            _chatScrollPosition.y = float.MaxValue;

            if (!isHiddenSystem)
            {
                _lastUserMessageTime = DateTime.Now;
                UpdateRelationshipAfterUserMessage(prompt);
                _chatHistory.Add(new ChatMessage { Role = "User", Content = prompt });
                SaveChatToLocalFileAsync("User", prompt);
            }

            string jsonPayload = BuildDeepSeekPayload(prompt, isHiddenSystem);
            string fullResponse = "";
            bool success = false;
            string errMsg = "";

            yield return LLMClient.SendDeepSeekRequest(
                _chatApiUrlConfig.Value,
                _apiKeyConfig.Value,
                jsonPayload,
                (responseContent) => { fullResponse = responseContent; success = true; },
                (error, code) => { errMsg = $"[API 错误] Code: {code}, Msg: {error}"; success = false; }
            );

            if (success && !string.IsNullOrEmpty(fullResponse))
            {
                _lastAiResponseTime = DateTime.Now;
                _chatHistory.Add(new ChatMessage { Role = "AI", Content = fullResponse });
                SaveChatToLocalFileAsync("Satone(AI)", fullResponse);
                TryTriggerSummary();
            }
            else
            {
                _chatHistory.Add(new ChatMessage { Role = "AI", Content = $"<color=red>{errMsg}</color>" });
                if (isHiddenSystem) _waitingForIdleReply = false;
            }

            _chatScrollPosition.y = float.MaxValue;
            _isProcessing = false;
        }

        private string BuildDeepSeekPayload(string newPrompt, bool isHiddenSystem)
        {
            string ragContext = (isHiddenSystem || !_useLongTermMemoryConfig.Value) ? "" : RetrieveRelevantHistoryFromMemory(newPrompt);

            StringBuilder sysBuilder = new StringBuilder(_personaConfig.Value);
            if (_enhancedPersonaConfig.Value)
            {
                sysBuilder.Append("\n\n").Append(EnhancedPersonaRules);
            }

            string relationshipContext = BuildRelationshipContext();
            if (!string.IsNullOrEmpty(relationshipContext))
            {
                sysBuilder.Append("\n\n").Append(relationshipContext);
            }

            string runtimeContext = BuildRuntimeContext(isHiddenSystem);
            if (!string.IsNullOrEmpty(runtimeContext))
            {
                sysBuilder.Append("\n\n").Append(runtimeContext);
            }
            if (isHiddenSystem)
            {
                sysBuilder.Append("\n\n【系统事件：主动搭话】\n").Append(newPrompt);
            }

            if (_useLongTermMemoryConfig.Value && !string.IsNullOrEmpty(_rollingSummary))
            {
                sysBuilder.Append("\n\n【很久之前的聊天内容摘要，供参考】\n").Append(_rollingSummary);
            }
            if (_useLongTermMemoryConfig.Value && !string.IsNullOrEmpty(ragContext))
            {
                sysBuilder.Append("\n\n【记忆库中检索到的相关历史片段，供参考】\n").Append(ragContext);
            }

            var messages = new List<LLMClient.ChatCompletionMessage>
            {
                new LLMClient.ChatCompletionMessage("system", sysBuilder.ToString())
            };

            int startIndex = Mathf.Max(0, _chatHistory.Count - _maxHistoryRounds * 2);
            for (int i = startIndex; i < _chatHistory.Count; i++)
            {
                string role = _chatHistory[i].Role == "User" ? "user" : "assistant";
                messages.Add(new LLMClient.ChatCompletionMessage(role, _chatHistory[i].Content));
            }

            return LLMClient.BuildChatPayload(_modelConfig.Value, messages);
        }

        // ================= 【时间线排序】 =================
        private string RetrieveRelevantHistoryFromMemory(string prompt)
        {
            try
            {
                int maxResults = Mathf.Clamp(_maxMemoryResultsConfig.Value, 0, 50);
                if (!_useLongTermMemoryConfig.Value || maxResults <= 0) return "";

                List<string> keywords = new List<string>();
                for (int i = 0; i < prompt.Length - 1; i++)
                {
                    string pair = prompt.Substring(i, 2);
                    if (!_stopWords.Contains(pair) && !char.IsPunctuation(pair[0]) && !char.IsWhiteSpace(pair[0]))
                        keywords.Add(pair);
                }
                var words = Regex.Split(prompt, @"\W+");
                foreach (var w in words)
                    if (w.Length >= 2 && !_stopWords.Contains(w) && !keywords.Contains(w)) keywords.Add(w);

                // 保存的是 KeyValuePair<分数, 全局历史索引>
                var scores = new List<KeyValuePair<int, int>>();
                for (int i = 0; i < _globalHistoryCache.Count; i++)
                {
                    MemoryEntry entry = _globalHistoryCache[i];
                    if (entry == null || !entry.Enabled || string.IsNullOrEmpty(entry.Content)) continue;

                    string searchableText = $"{entry.Role} {entry.Content} {entry.Tags}";
                    int matchScore = 0;
                    foreach (var kw in keywords)
                    {
                        if (searchableText.Contains(kw)) matchScore++;
                    }
                    int score = entry.Pinned ? 1000 : 0;
                    if (matchScore > 0) score += matchScore + Mathf.Clamp(entry.Importance, 0, 10);
                    if (score > 0) scores.Add(new KeyValuePair<int, int>(score, i));
                }

                // 先按关联分数降序排列，找出最相关的记忆
                scores.Sort((a, b) => b.Key.CompareTo(a.Key));

                List<string> recentContext = new List<string>();
                int checkStart = Mathf.Max(0, _chatHistory.Count - _maxHistoryRounds * 2);
                for(int i = checkStart; i < _chatHistory.Count; i++) recentContext.Add(_chatHistory[i].Content);

                // 收集最相关的前20条记忆，并将它们的“原始时间索引”存起来
                List<int> topIndices = new List<int>();

                for (int i = 0; i < scores.Count && topIndices.Count < maxResults; i++)
                {
                    int originalIndex = scores[i].Value;
                    MemoryEntry matchedEntry = _globalHistoryCache[originalIndex];
                    bool isRecent = false;

                    foreach(var rc in recentContext)
                        if (matchedEntry.Content.Contains(rc) && rc.Length > 5) { isRecent = true; break; }

                    if (!isRecent)
                    {
                        topIndices.Add(originalIndex);
                    }
                }

                // 将这20条碎片记忆按真实发生的时间顺序重新排好！
                topIndices.Sort();

                StringBuilder sb = new StringBuilder();
                foreach(int idx in topIndices)
                {
                    sb.AppendLine(_globalHistoryCache[idx].ToPromptLine());
                }

                return sb.ToString();
            }
            catch (Exception ex)
            {
                Log.Warning("内存 RAG 检索出错: " + ex.Message);
                return "";
            }
        }

        private void TryTriggerSummary()
        {
            if (!_autoSummaryConfig.Value) return;
            if (_isSummarizing) return;

            int outOfWindowCount = _chatHistory.Count - (_maxHistoryRounds * 2);

            if (outOfWindowCount > _lastSummarizedIndex && (outOfWindowCount - _lastSummarizedIndex) >= 4)
            {
                int countToSummarize = outOfWindowCount - _lastSummarizedIndex;
                StartCoroutine(UpdateRollingSummary(_lastSummarizedIndex, countToSummarize));
            }
        }

        IEnumerator UpdateRollingSummary(int startIndex, int count)
        {
            _isSummarizing = true;
            Log.Info($"正在后台自动生成记忆摘要... (提取 {count} 条旧对话)");

            StringBuilder chatText = new StringBuilder();
            for(int i = startIndex; i < startIndex + count; i++)
            {
                chatText.AppendLine($"{_chatHistory[i].Role}: {_chatHistory[i].Content}");
            }

            string summaryPrompt = $"请将以下最新的聊天片段提炼并补充到之前的摘要中，保持总结简明扼要（总字数不超过1000字）。\n\n【目前的旧摘要】\n{(_rollingSummary==""?"无":_rollingSummary)}\n\n【需要补充的新聊天片段】\n{chatText.ToString()}";

            var messages = new List<LLMClient.ChatCompletionMessage>
            {
                new LLMClient.ChatCompletionMessage("system", "你是一个专业的阅读理解与记忆总结助手。只输出总结结果，不包含任何废话。"),
                new LLMClient.ChatCompletionMessage("user", summaryPrompt)
            };
            string jsonPayload = LLMClient.BuildChatPayload(_modelConfig.Value, messages);

            bool success = false;
            string newSummary = "";

            yield return LLMClient.SendDeepSeekRequest(
                _chatApiUrlConfig.Value, _apiKeyConfig.Value, jsonPayload,
                (res) => { newSummary = res; success = true; },
                (err, code) => { success = false; Log.Warning("后台摘要生成失败: " + err); }
            );

            if (success && !string.IsNullOrEmpty(newSummary))
            {
                _rollingSummary = newSummary;
                SaveSummaryToLocalAsync(_rollingSummary);
                _lastSummarizedIndex = startIndex + count;
                Log.Info("记忆摘要已无感更新完成并保存至本地！");
            }
            _isSummarizing = false;
        }

        // ================= 【全局搜索与安全对齐】 =================
        private void AddAIChatButtonToRightIcons()
        {
            try
            {
                // 全局暴力搜索叫 TopIcons 的节点，无视前面的拼写错误路径
                GameObject rightIcons = GameObject.Find("TopIcons");

                // 备用寻找方案
                if (rightIcons == null) rightIcons = GameObject.Find("Parent/Canvas/UI/MostFrontArea/TopIcons");
                if (rightIcons == null) rightIcons = GameObject.Find("Paremt/Canvas/UI/MostFrontArea/TopIcons");

                if (rightIcons == null) return;

                // 防止多次 Update 导致重复生成图标
                if (rightIcons.transform.Find("IconAIChat_Button") != null)
                {
                    _aiChatButtonAdded = true;
                    return;
                }

                _aiChatButton = new GameObject("IconAIChat_Button");
                _aiChatButton.transform.SetParent(rightIcons.transform, false);
                RectTransform rectTransform = _aiChatButton.AddComponent<RectTransform>();

                float buttonSize = 60f;
                if (rightIcons.transform.childCount > 0)
                {
                    RectTransform firstButtonRect = rightIcons.transform.GetChild(0).GetComponent<RectTransform>();
                    if (firstButtonRect != null) buttonSize = Mathf.Max(firstButtonRect.sizeDelta.x, firstButtonRect.sizeDelta.y);
                }

                rectTransform.sizeDelta = new Vector2(buttonSize, buttonSize);
                Image image = _aiChatButton.AddComponent<Image>();

                try
                {
                    // 调用神器安全加载图片
                    image.sprite = EmbeddedSpriteLoader.Load("ai_chat.png");
                    image.preserveAspect = true;
                }
                catch (Exception ex)
                {
                    Log.Warning("读取图片失败，将使用蓝色方块替代: " + ex.Message);
                    image.color = new Color(0.2f, 0.6f, 1.0f);
                }

                Button button = _aiChatButton.AddComponent<Button>();
                button.onClick.AddListener(() => { _showInputWindow = !_showInputWindow; });

                List<RectTransform> children = new List<RectTransform>();
                for (int i = 0; i < rightIcons.transform.childCount; i++)
                {
                    RectTransform childRect = rightIcons.transform.GetChild(i).GetComponent<RectTransform>();
                    if (childRect != null && childRect.gameObject != _aiChatButton)
                    {
                        children.Add(childRect);
                    }
                }

                // 排序寻找最靠下的按钮，防止越界崩溃
                if (children.Count > 0)
                {
                    children.Sort((a, b) => a.anchoredPosition.y.CompareTo(b.anchoredPosition.y));

                    RectTransform lowestButton = children[0];
                    float spacing = 15f;

                    rectTransform.anchoredPosition = new Vector2(lowestButton.anchoredPosition.x, lowestButton.anchoredPosition.y - (buttonSize + spacing));
                }
                else
                {
                    rectTransform.anchoredPosition = Vector2.zero;
                }

                rectTransform.anchorMin = new Vector2(1f, 1f);
                rectTransform.anchorMax = new Vector2(1f, 1f);
                rectTransform.pivot = new Vector2(0.5f, 0.5f);

                _aiChatButtonAdded = true;
                Log.Info("✅ AI 聊天快捷图标生成成功！");
            }
            catch (Exception ex)
            {
                Log.Error("图标生成过程中发生意外错误: " + ex.Message);
            }
        }
    }
}
