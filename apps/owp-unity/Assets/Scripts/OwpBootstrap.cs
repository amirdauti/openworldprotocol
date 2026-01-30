using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

public class OwpBootstrap : MonoBehaviour
{
    private const string AdminBaseUrl = "http://127.0.0.1:9333";

    private Process _serverProcess;
    private GameObject _avatarRoot;
    private Renderer _avatarRenderer;
    private Renderer _hairRenderer;

    private Canvas _canvas;
    private Button _orbButton;
    private GameObject _chatPanel;
    private InputField _chatInput;
    private Text _chatLog;
    private Button _sendButton;

    private GameObject _providerPanel;
    private Button _useCodexButton;
    private Button _useClaudeButton;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    public static void Init()
    {
        var go = new GameObject("OWP_Bootstrap");
        DontDestroyOnLoad(go);
        go.AddComponent<OwpBootstrap>();
    }

    private void Start()
    {
        EnsureSceneBasics();
        CreatePlaceholderAvatar();
        CreateUi();

        StartCoroutine(BootSequence());
    }

    private IEnumerator BootSequence()
    {
        yield return StartCoroutine(EnsureServerRunning());
        yield return StartCoroutine(RefreshAssistantStatus());

        AppendLog("Companion: Hi. I can help create/edit your avatar and worlds.");
        AppendLog("Companion: Choose Codex or Claude the first time, then describe the avatar you want.");
    }

    private IEnumerator EnsureServerRunning()
    {
        // If server already running, we're done.
        yield return StartCoroutine(CheckHealth((ok) =>
        {
            if (ok) return;
            StartServerProcess();
        }));

        // Wait for readiness
        float deadline = Time.time + 10f;
        while (Time.time < deadline)
        {
            bool ok = false;
            yield return StartCoroutine(CheckHealth((v) => ok = v));
            if (ok)
            {
                AppendLog("Server: connected.");
                yield break;
            }
            yield return new WaitForSeconds(0.25f);
        }

        AppendLog("Server: failed to start. See console for details.");
    }

    private IEnumerator CheckHealth(Action<bool> cb)
    {
        using (var req = UnityWebRequest.Get($"{AdminBaseUrl}/health"))
        {
            req.timeout = 2;
            yield return req.SendWebRequest();
            cb(req.result == UnityWebRequest.Result.Success);
        }
    }

    private void StartServerProcess()
    {
        try
        {
            var serverPath = ResolveServerPath();
            if (string.IsNullOrEmpty(serverPath) || !File.Exists(serverPath))
            {
                UnityEngine.Debug.LogError($"OWP server binary not found. Expected at: {serverPath}");
                AppendLog("Server: binary not found. Build it with `cargo build -p owp-server`.");
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = serverPath,
                Arguments = "admin --listen 127.0.0.1:9333 --no-auth",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = ResolveRepoRoot()
            };

            _serverProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _serverProcess.OutputDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) UnityEngine.Debug.Log(e.Data); };
            _serverProcess.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) UnityEngine.Debug.LogWarning(e.Data); };

            _serverProcess.Start();
            _serverProcess.BeginOutputReadLine();
            _serverProcess.BeginErrorReadLine();

            AppendLog("Server: starting…");
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogError(ex);
            AppendLog("Server: failed to spawn process.");
        }
    }

    private static string ResolveRepoRoot()
    {
        // Application.dataPath -> apps/owp-unity/Assets
        var assetsDir = Application.dataPath;
        var unityDir = Directory.GetParent(assetsDir)?.FullName;
        var appsDir = Directory.GetParent(unityDir ?? "")?.FullName;
        var repoRoot = Directory.GetParent(appsDir ?? "")?.FullName;
        return repoRoot ?? Directory.GetCurrentDirectory();
    }

    private static string ResolveServerPath()
    {
        var env = Environment.GetEnvironmentVariable("OWP_SERVER_PATH");
        if (!string.IsNullOrEmpty(env)) return env;

        var root = ResolveRepoRoot();
        var exe = "owp-server";
        if (Application.platform == RuntimePlatform.WindowsPlayer || Application.platform == RuntimePlatform.WindowsEditor)
        {
            exe = "owp-server.exe";
        }

        return Path.Combine(root, "target", "debug", exe);
    }

    private IEnumerator RefreshAssistantStatus()
    {
        using (var req = UnityWebRequest.Get($"{AdminBaseUrl}/assistant/status"))
        {
            req.timeout = 5;
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                AppendLog("Assistant: cannot read status (server not ready).");
                yield break;
            }

            var status = JsonUtility.FromJson<AssistantStatus>(req.downloadHandler.text);
            if (status == null)
            {
                AppendLog("Assistant: status parse failed.");
                yield break;
            }

            if (string.IsNullOrEmpty(status.provider))
            {
                _providerPanel.SetActive(true);
                UpdateProviderButtons(status);
                AppendLog("Assistant: choose Codex or Claude.");
            }
            else
            {
                _providerPanel.SetActive(false);
                AppendLog($"Assistant: provider set to {status.provider}.");
            }
        }
    }

    private void UpdateProviderButtons(AssistantStatus status)
    {
        bool codexInstalled = false;
        bool claudeInstalled = false;
        if (status.providers != null)
        {
            foreach (var p in status.providers)
            {
                if (p == null) continue;
                if (p.id == "codex") codexInstalled = p.installed;
                if (p.id == "claude") claudeInstalled = p.installed;
            }
        }

        _useCodexButton.interactable = codexInstalled;
        _useClaudeButton.interactable = claudeInstalled;
    }

    private IEnumerator SetProvider(string provider)
    {
        var body = Encoding.UTF8.GetBytes($"{{\"provider\":\"{provider}\"}}");
        using (var req = new UnityWebRequest($"{AdminBaseUrl}/assistant/provider", "POST"))
        {
            req.uploadHandler = new UploadHandlerRaw(body);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.timeout = 10;
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                AppendLog($"Assistant: failed to set provider ({provider}).");
                yield break;
            }

            AppendLog($"Assistant: provider selected: {provider}.");
            _providerPanel.SetActive(false);
        }
    }

    private IEnumerator GenerateAvatar(string prompt)
    {
        var json = $"{{\"prompt\":{JsonEscape(prompt)}}}";
        var body = Encoding.UTF8.GetBytes(json);

        using (var req = new UnityWebRequest($"{AdminBaseUrl}/avatar/generate", "POST"))
        {
            req.uploadHandler = new UploadHandlerRaw(body);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.timeout = 120;
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                AppendLog("Assistant: avatar generation failed.");
                yield break;
            }

            var resp = JsonUtility.FromJson<AvatarGenerateResponse>(req.downloadHandler.text);
            if (resp == null || resp.avatar == null)
            {
                AppendLog("Assistant: invalid avatar response.");
                yield break;
            }

            ApplyAvatar(resp.avatar);
            AppendLog($"Assistant: avatar updated → {resp.avatar.name}");
        }
    }

    private void ApplyAvatar(AvatarSpec avatar)
    {
        if (_avatarRoot == null) return;

        _avatarRoot.name = $"Avatar_{avatar.name}";
        _avatarRoot.transform.localScale = new Vector3(1f, Mathf.Clamp(avatar.height, 0.5f, 2f), 1f);

        if (_avatarRenderer != null)
        {
            _avatarRenderer.material.color = ParseHex(avatar.primary_color, Color.cyan);
        }
        if (_hairRenderer != null)
        {
            _hairRenderer.material.color = ParseHex(avatar.secondary_color, Color.white);
        }
    }

    private static Color ParseHex(string hex, Color fallback)
    {
        if (string.IsNullOrEmpty(hex)) return fallback;
        Color c;
        if (ColorUtility.TryParseHtmlString(hex, out c)) return c;
        return fallback;
    }

    private static string JsonEscape(string s)
    {
        // Minimal JSON string escape.
        return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "") + "\"";
    }

    private void CreatePlaceholderAvatar()
    {
        _avatarRoot = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        _avatarRoot.transform.position = new Vector3(0, 1, 0);

        _avatarRenderer = _avatarRoot.GetComponent<Renderer>();
        _avatarRenderer.material = new Material(Shader.Find("Standard"));
        _avatarRenderer.material.color = Color.cyan;

        var hair = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        hair.transform.SetParent(_avatarRoot.transform, false);
        hair.transform.localPosition = new Vector3(0, 1.05f, 0);
        hair.transform.localScale = new Vector3(0.55f, 0.35f, 0.55f);

        _hairRenderer = hair.GetComponent<Renderer>();
        _hairRenderer.material = new Material(Shader.Find("Standard"));
        _hairRenderer.material.color = Color.white;
    }

    private void EnsureSceneBasics()
    {
        if (Camera.main == null)
        {
            var camGo = new GameObject("Main Camera");
            var cam = camGo.AddComponent<Camera>();
            cam.tag = "MainCamera";
            cam.transform.position = new Vector3(0, 1.5f, -4f);
            cam.transform.LookAt(new Vector3(0, 1, 0));
        }

        var light = FindObjectOfType<Light>();
        if (light == null)
        {
            var lightGo = new GameObject("Directional Light");
            var l = lightGo.AddComponent<Light>();
            l.type = LightType.Directional;
            l.transform.rotation = Quaternion.Euler(50, -30, 0);
        }
    }

    private void CreateUi()
    {
        var canvasGo = new GameObject("OWP_UI");
        DontDestroyOnLoad(canvasGo);
        _canvas = canvasGo.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvasGo.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        canvasGo.AddComponent<GraphicRaycaster>();

        if (FindObjectOfType<UnityEngine.EventSystems.EventSystem>() == null)
        {
            var es = new GameObject("EventSystem");
            DontDestroyOnLoad(es);
            es.AddComponent<UnityEngine.EventSystems.EventSystem>();
            es.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
        }

        _orbButton = CreateButton(canvasGo.transform, "OrbButton", "◉", new Vector2(-40, -40), new Vector2(60, 60));
        _orbButton.onClick.AddListener(() =>
        {
            _chatPanel.SetActive(!_chatPanel.activeSelf);
        });

        _chatPanel = new GameObject("ChatPanel");
        _chatPanel.transform.SetParent(canvasGo.transform, false);
        var panelImage = _chatPanel.AddComponent<Image>();
        panelImage.color = new Color(0, 0, 0, 0.7f);
        var rt = _chatPanel.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(1, 1);
        rt.anchorMax = new Vector2(1, 1);
        rt.pivot = new Vector2(1, 1);
        rt.sizeDelta = new Vector2(420, 280);
        rt.anchoredPosition = new Vector2(-20, -20);
        _chatPanel.SetActive(false);

        _chatLog = CreateText(_chatPanel.transform, "ChatLog", "", new Vector2(-10, -10), new Vector2(400, 180));
        _chatLog.alignment = TextAnchor.UpperLeft;
        _chatLog.supportRichText = true;

        _chatInput = CreateInput(_chatPanel.transform, "ChatInput", new Vector2(-10, 50), new Vector2(300, 32));
        _sendButton = CreateButton(_chatPanel.transform, "SendButton", "Send", new Vector2(-10, 50), new Vector2(80, 32));
        _sendButton.onClick.AddListener(() =>
        {
            var text = _chatInput.text ?? "";
            if (text.Trim().Length == 0) return;
            _chatInput.text = "";
            AppendLog($"You: {text}");
            StartCoroutine(GenerateAvatar(text));
        });

        // Provider panel (center)
        _providerPanel = new GameObject("ProviderPanel");
        _providerPanel.transform.SetParent(canvasGo.transform, false);
        var pimg = _providerPanel.AddComponent<Image>();
        pimg.color = new Color(0, 0, 0, 0.85f);
        var prt = _providerPanel.GetComponent<RectTransform>();
        prt.anchorMin = new Vector2(0.5f, 0.5f);
        prt.anchorMax = new Vector2(0.5f, 0.5f);
        prt.pivot = new Vector2(0.5f, 0.5f);
        prt.sizeDelta = new Vector2(420, 160);
        prt.anchoredPosition = Vector2.zero;

        var title = CreateText(_providerPanel.transform, "ProviderTitle", "Choose provider", new Vector2(0, -20), new Vector2(400, 40));
        title.alignment = TextAnchor.MiddleCenter;
        title.fontSize = 20;

        _useCodexButton = CreateButton(_providerPanel.transform, "UseCodex", "Use Codex", new Vector2(-110, 40), new Vector2(180, 44));
        _useClaudeButton = CreateButton(_providerPanel.transform, "UseClaude", "Use Claude", new Vector2(110, 40), new Vector2(180, 44));

        _useCodexButton.onClick.AddListener(() => StartCoroutine(SetProvider("codex")));
        _useClaudeButton.onClick.AddListener(() => StartCoroutine(SetProvider("claude")));

        _providerPanel.SetActive(false);
    }

    private static Button CreateButton(Transform parent, string name, string label, Vector2 anchoredPos, Vector2 size)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);

        var img = go.AddComponent<Image>();
        img.color = new Color(0.15f, 0.2f, 0.25f, 0.9f);

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(1, 1);
        rt.anchorMax = new Vector2(1, 1);
        rt.pivot = new Vector2(1, 1);
        rt.sizeDelta = size;
        rt.anchoredPosition = anchoredPos;

        var btn = go.AddComponent<Button>();

        var text = CreateText(go.transform, $"{name}_Text", label, Vector2.zero, size);
        text.alignment = TextAnchor.MiddleCenter;
        text.color = Color.white;

        return btn;
    }

    private static Text CreateText(Transform parent, string name, string value, Vector2 anchoredPos, Vector2 size)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);

        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(1, 1);
        rt.anchorMax = new Vector2(1, 1);
        rt.pivot = new Vector2(1, 1);
        rt.sizeDelta = size;
        rt.anchoredPosition = anchoredPos;

        var text = go.AddComponent<Text>();
        text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        text.text = value;
        text.fontSize = 14;
        text.color = new Color(0.85f, 0.9f, 1f, 1f);
        return text;
    }

    private static InputField CreateInput(Transform parent, string name, Vector2 anchoredPos, Vector2 size)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);

        var img = go.AddComponent<Image>();
        img.color = new Color(1f, 1f, 1f, 0.1f);

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(1, 1);
        rt.anchorMax = new Vector2(1, 1);
        rt.pivot = new Vector2(1, 1);
        rt.sizeDelta = size;
        rt.anchoredPosition = anchoredPos;

        var input = go.AddComponent<InputField>();
        var placeholder = CreateText(go.transform, "Placeholder", "Describe your avatar…", Vector2.zero, size);
        placeholder.color = new Color(1f, 1f, 1f, 0.35f);
        var text = CreateText(go.transform, "Text", "", Vector2.zero, size);
        text.color = Color.white;
        text.alignment = TextAnchor.MiddleLeft;
        text.supportRichText = false;

        input.textComponent = text;
        input.placeholder = placeholder;
        input.lineType = InputField.LineType.SingleLine;
        input.characterLimit = 256;
        return input;
    }

    private void AppendLog(string line)
    {
        if (_chatLog == null) return;
        _chatLog.text = (_chatLog.text + "\n" + line).Trim();
    }

    private void OnApplicationQuit()
    {
        try
        {
            if (_serverProcess != null && !_serverProcess.HasExited)
            {
                _serverProcess.Kill();
            }
        }
        catch { }
    }

    [Serializable]
    private class AssistantStatus
    {
        public string provider;
        public ProviderStatus[] providers;
    }

    [Serializable]
    private class ProviderStatus
    {
        public string id;
        public bool installed;
        public string note;
    }

    [Serializable]
    private class AvatarGenerateResponse
    {
        public AvatarSpec avatar;
    }

    [Serializable]
    private class AvatarSpec
    {
        public string version;
        public string name;
        public string primary_color;
        public string secondary_color;
        public float height;
        public string[] tags;
    }
}

