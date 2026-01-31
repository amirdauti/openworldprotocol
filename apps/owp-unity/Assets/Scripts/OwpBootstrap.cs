using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

public class OwpBootstrap : MonoBehaviour
{
    private const string AdminBaseUrl = "http://127.0.0.1:9333";
    private static readonly HttpClient Http = new HttpClient();
    private static Font _defaultFont;

    private Process _serverProcess;
    private GameObject _avatarRoot;
    private Renderer _avatarRenderer;
    private Renderer _hairRenderer;

    private Canvas _canvas;
    private Button _orbButton;
    private GameObject _chatPanel;
    private Button _providerButton;
    private Text _providerButtonLabel;
    private InputField _chatInput;
    private Text _chatLog;
    private Button _sendButton;

    private GameObject _worldsPanel;
    private InputField _worldNameInput;
    private Button _createWorldButton;
    private Button _refreshWorldsButton;
    private Button _worldsSourceButton;
    private Text _worldsSourceLabel;
    private Transform _worldsListRoot;
    private Button _hostConnectButton;
    private Text _selectedWorldLabel;
    private bool _worldsUseOnChain;

    private Process _gameProcess;
    private string _selectedWorldId;
    private int _selectedWorldPort;

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
        yield return StartCoroutine(RefreshWorlds());

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
        var task = HttpRequestAsync("GET", $"{AdminBaseUrl}/health", null, 2);
        yield return new WaitUntil(() => task.IsCompleted);
        cb(task.Status == TaskStatus.RanToCompletion && task.Result.ok);
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

    private IEnumerator RefreshAssistantStatus(bool forceShowProviderPanel = false)
    {
        var task = HttpRequestAsync("GET", $"{AdminBaseUrl}/assistant/status", null, 5);
        yield return new WaitUntil(() => task.IsCompleted);
        if (task.Status != TaskStatus.RanToCompletion || !task.Result.ok)
        {
            AppendLog("Assistant: cannot read status (server not ready).");
            yield break;
        }

        var status = JsonUtility.FromJson<AssistantStatus>(task.Result.text);
        if (status == null)
        {
            AppendLog("Assistant: status parse failed.");
            yield break;
        }

        var provider = status.provider ?? "";
        SetProviderButtonLabel(provider);

        if (string.IsNullOrEmpty(provider) || forceShowProviderPanel)
        {
            _providerPanel.SetActive(true);
            UpdateProviderButtons(status);
            AppendLog("Assistant: choose Codex or Claude.");
        }
        else
        {
            _providerPanel.SetActive(false);
            AppendLog($"Assistant: provider set to {provider}.");
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
        var jsonBody = $"{{\"provider\":\"{provider}\"}}";
        var task = HttpRequestAsync("POST", $"{AdminBaseUrl}/assistant/provider", jsonBody, 10);
        yield return new WaitUntil(() => task.IsCompleted);

        if (task.Status != TaskStatus.RanToCompletion || !task.Result.ok)
        {
            AppendLog($"Assistant: failed to set provider ({provider}).");
            yield break;
        }

        AppendLog($"Assistant: provider selected: {provider}.");
        _providerPanel.SetActive(false);
        yield return StartCoroutine(RefreshAssistantStatus());
    }

    private void SetProviderButtonLabel(string provider)
    {
        if (_providerButtonLabel == null) return;
        if (string.IsNullOrEmpty(provider))
        {
            _providerButtonLabel.text = "Provider: (select)";
        }
        else
        {
            _providerButtonLabel.text = $"Provider: {provider}";
        }
    }

    private IEnumerator GenerateAvatar(string prompt)
    {
        var json = $"{{\"prompt\":{JsonEscape(prompt)}}}";
        var task = HttpRequestAsync("POST", $"{AdminBaseUrl}/avatar/generate", json, 120);
        yield return new WaitUntil(() => task.IsCompleted);

        if (task.Status != TaskStatus.RanToCompletion || !task.Result.ok)
        {
            AppendLog("Assistant: avatar generation failed.");
            yield break;
        }

        var resp = JsonUtility.FromJson<AvatarGenerateResponse>(task.Result.text);
        if (resp == null || resp.avatar == null)
        {
            AppendLog("Assistant: invalid avatar response.");
            yield break;
        }

        ApplyAvatar(resp.avatar);
        AppendLog($"Assistant: avatar updated → {resp.avatar.name}");
    }

    private IEnumerator RefreshWorlds()
    {
        var path = _worldsUseOnChain ? "/discovery/worlds" : "/worlds";
        var task = HttpRequestAsync("GET", $"{AdminBaseUrl}{path}", null, 10);
        yield return new WaitUntil(() => task.IsCompleted);

        if (task.Status != TaskStatus.RanToCompletion || !task.Result.ok)
        {
            if (_worldsUseOnChain && task.Status == TaskStatus.RanToCompletion && task.Result.status == 412)
            {
                AppendLog("Worlds: on-chain discovery not configured (missing RPC URL or program id).");
            }
            else
            {
                AppendLog("Worlds: failed to load list.");
            }
            yield break;
        }

        var wrapped = "{\"items\":" + task.Result.text + "}";
        var list = JsonUtility.FromJson<WorldListResponse>(wrapped);
        if (list == null || list.items == null)
        {
            AppendLog("Worlds: list parse failed.");
            yield break;
        }

        RenderWorldList(list.items);
    }

    private void RenderWorldList(WorldDirectoryEntry[] worlds)
    {
        if (_worldsListRoot == null) return;

        for (int i = _worldsListRoot.childCount - 1; i >= 0; i--)
        {
            Destroy(_worldsListRoot.GetChild(i).gameObject);
        }

        float y = 0;
        foreach (var w in worlds)
        {
            if (w == null) continue;
            var btn = CreateButtonTL(_worldsListRoot, $"World_{w.world_id}", $"{w.name}  ({w.port})", new Vector2(0, y), new Vector2(380, 30));
            btn.onClick.AddListener(() =>
            {
                SelectWorld(w.world_id, w.port, w.name);
            });
            y -= 34;
        }

        if (worlds.Length == 0)
        {
            var t = CreateTextTL(_worldsListRoot, "NoWorlds", "No worlds yet. Create one.", new Vector2(0, 0), new Vector2(380, 30));
            t.alignment = TextAnchor.MiddleLeft;
        }
    }

    private void SelectWorld(string worldId, int port, string name)
    {
        _selectedWorldId = worldId;
        _selectedWorldPort = port;
        if (_selectedWorldLabel != null)
        {
            _selectedWorldLabel.text = $"Selected: {name} ({port})";
        }
    }

    private IEnumerator CreateWorld(string name)
    {
        var json = $"{{\"name\":{JsonEscape(name)},\"game_port\":7777}}";
        var task = HttpRequestAsync("POST", $"{AdminBaseUrl}/worlds", json, 20);
        yield return new WaitUntil(() => task.IsCompleted);

        if (task.Status != TaskStatus.RanToCompletion || !task.Result.ok)
        {
            AppendLog("Worlds: create failed.");
            yield break;
        }

        var manifest = JsonUtility.FromJson<WorldManifest>(task.Result.text);
        if (manifest == null || string.IsNullOrEmpty(manifest.world_id))
        {
            AppendLog("Worlds: create response parse failed.");
            yield break;
        }

        AppendLog($"Worlds: created {manifest.name} ({manifest.world_id}).");
        SelectWorld(manifest.world_id, manifest.ports != null ? manifest.ports.game_port : 7777, manifest.name);
        yield return StartCoroutine(RefreshWorlds());
    }

    private IEnumerator HostAndConnectSelectedWorld()
    {
        if (string.IsNullOrEmpty(_selectedWorldId))
        {
            AppendLog("Worlds: select a world first.");
            yield break;
        }

        EnsureGameServerRunning(_selectedWorldId);

        float deadline = Time.time + 10f;
        while (Time.time < deadline)
        {
            var ok = false;
            yield return StartCoroutine(CheckTcpPort("127.0.0.1", _selectedWorldPort, (v) => ok = v));
            if (ok) break;
            yield return new WaitForSeconds(0.25f);
        }

        yield return StartCoroutine(ConnectAndHandshake("127.0.0.1", _selectedWorldPort, _selectedWorldId));
    }

    private void EnsureGameServerRunning(string worldId)
    {
        try
        {
            if (_gameProcess != null && !_gameProcess.HasExited)
            {
                // For now: one world at a time; restart if different.
                _gameProcess.Kill();
                _gameProcess = null;
            }

            var serverPath = ResolveServerPath();
            if (string.IsNullOrEmpty(serverPath) || !File.Exists(serverPath))
            {
                AppendLog("Game: server binary not found.");
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = serverPath,
                Arguments = $"run --world-id {worldId}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = ResolveRepoRoot()
            };

            _gameProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _gameProcess.OutputDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) UnityEngine.Debug.Log(e.Data); };
            _gameProcess.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) UnityEngine.Debug.LogWarning(e.Data); };

            _gameProcess.Start();
            _gameProcess.BeginOutputReadLine();
            _gameProcess.BeginErrorReadLine();

            AppendLog($"Game: starting world {worldId} on tcp://127.0.0.1:{_selectedWorldPort}…");
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogError(ex);
            AppendLog("Game: failed to start.");
        }
    }

    private IEnumerator CheckTcpPort(string host, int port, Action<bool> cb)
    {
        var task = CheckTcpPortAsync(host, port);
        yield return new WaitUntil(() => task.IsCompleted);
        cb(task.Status == TaskStatus.RanToCompletion && task.Result);
    }

    private static async Task<bool> CheckTcpPortAsync(string host, int port)
    {
        try
        {
            using (var client = new TcpClient())
            {
                var t = client.ConnectAsync(host, port);
                var completed = await Task.WhenAny(t, Task.Delay(500));
                if (completed != t) return false;
                await t;
                return client.Connected;
            }
        }
        catch
        {
            return false;
        }
    }

    private static async Task<HttpResult> HttpRequestAsync(
        string method,
        string url,
        string jsonBody,
        int timeoutSeconds
    )
    {
        try
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
            using (var req = new HttpRequestMessage(new HttpMethod(method), url))
            {
                if (!string.IsNullOrEmpty(jsonBody))
                {
                    req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                }

                using (var resp = await Http.SendAsync(req, cts.Token))
                {
                    var text = resp.Content != null ? await resp.Content.ReadAsStringAsync() : "";
                    var code = (long)resp.StatusCode;
                    return new HttpResult
                    {
                        ok = code >= 200 && code < 300,
                        status = code,
                        text = text
                    };
                }
            }
        }
        catch (Exception ex)
        {
            return new HttpResult { ok = false, status = 0, text = ex.Message };
        }
    }

    private IEnumerator ConnectAndHandshake(string host, int port, string worldId)
    {
        AppendLog($"Net: connecting to {host}:{port}…");

        var task = ConnectAndHandshakeAsync(host, port, worldId);
        yield return new WaitUntil(() => task.IsCompleted);

        if (task.Status != TaskStatus.RanToCompletion)
        {
            AppendLog("Net: handshake failed (exception).");
            yield break;
        }

        if (!task.Result.ok)
        {
            AppendLog($"Net: handshake failed ({task.Result.error}).");
            yield break;
        }

        AppendLog($"Net: welcome → {task.Result.motd}");
    }

    private static async Task<HandshakeResult> ConnectAndHandshakeAsync(string host, int port, string worldId)
    {
        try
        {
            using (var client = new TcpClient())
            {
                await client.ConnectAsync(host, port);
                using (var stream = client.GetStream())
                {
                    var hello = new HelloMessage
                    {
                        type = "hello",
                        protocol_version = "0.1",
                        request_id = Guid.NewGuid().ToString(),
                        world_id = worldId,
                        client_name = "owp-unity"
                    };
                    var helloJson = JsonUtility.ToJson(hello);
                    await WriteFrame(stream, helloJson);

                    var msgJson = await ReadFrame(stream);
                    if (string.IsNullOrEmpty(msgJson))
                    {
                        return new HandshakeResult { ok = false, error = "empty response" };
                    }

                    var env = JsonUtility.FromJson<MessageEnvelope>(msgJson);
                    if (env == null || string.IsNullOrEmpty(env.type))
                    {
                        return new HandshakeResult { ok = false, error = "invalid message envelope" };
                    }

                    if (env.type != "welcome")
                    {
                        return new HandshakeResult { ok = false, error = "unexpected message: " + env.type };
                    }

                    var welcome = JsonUtility.FromJson<WelcomeMessage>(msgJson);
                    return new HandshakeResult { ok = true, motd = welcome != null ? welcome.motd : "Welcome" };
                }
            }
        }
        catch (Exception ex)
        {
            return new HandshakeResult { ok = false, error = ex.Message };
        }
    }

    private static async Task WriteFrame(NetworkStream stream, string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var len = payload.Length;
        var header = new byte[4];
        header[0] = (byte)((len >> 24) & 0xFF);
        header[1] = (byte)((len >> 16) & 0xFF);
        header[2] = (byte)((len >> 8) & 0xFF);
        header[3] = (byte)(len & 0xFF);
        await stream.WriteAsync(header, 0, 4);
        await stream.WriteAsync(payload, 0, payload.Length);
        await stream.FlushAsync();
    }

    private static async Task<string> ReadFrame(NetworkStream stream)
    {
        var header = await ReadExact(stream, 4);
        if (header == null || header.Length != 4) return null;
        var len = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
        if (len <= 0 || len > 4 * 1024 * 1024) return null;
        var payload = await ReadExact(stream, len);
        if (payload == null) return null;
        return Encoding.UTF8.GetString(payload);
    }

    private static async Task<byte[]> ReadExact(NetworkStream stream, int n)
    {
        var buf = new byte[n];
        var off = 0;
        while (off < n)
        {
            var read = await stream.ReadAsync(buf, off, n - off);
            if (read <= 0) return null;
            off += read;
        }
        return buf;
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

        _providerButton = CreateButton(_chatPanel.transform, "ProviderButton", "Provider: (loading…)", new Vector2(-10, -10), new Vector2(180, 24));
        _providerButtonLabel = _providerButton.GetComponentInChildren<Text>();
        _providerButton.onClick.AddListener(() =>
        {
            StartCoroutine(RefreshAssistantStatus(true));
        });

        _chatLog = CreateText(_chatPanel.transform, "ChatLog", "", new Vector2(-10, -40), new Vector2(400, 170));
        _chatLog.alignment = TextAnchor.UpperLeft;
        _chatLog.supportRichText = true;

        _chatInput = CreateInput(_chatPanel.transform, "ChatInput", new Vector2(-110, -230), new Vector2(300, 32));
        _sendButton = CreateButton(_chatPanel.transform, "SendButton", "Send", new Vector2(-10, -230), new Vector2(80, 32));
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

        var title = CreateText(_providerPanel.transform, "ProviderTitle", "Choose provider", new Vector2(-10, -20), new Vector2(400, 40));
        title.alignment = TextAnchor.MiddleCenter;
        title.fontSize = 20;

        _useCodexButton = CreateButton(_providerPanel.transform, "UseCodex", "Use Codex", new Vector2(-220, -80), new Vector2(180, 44));
        _useClaudeButton = CreateButton(_providerPanel.transform, "UseClaude", "Use Claude", new Vector2(-20, -80), new Vector2(180, 44));

        _useCodexButton.onClick.AddListener(() => StartCoroutine(SetProvider("codex")));
        _useClaudeButton.onClick.AddListener(() => StartCoroutine(SetProvider("claude")));

        _providerPanel.SetActive(false);

        // Worlds panel (top-left)
        _worldsPanel = new GameObject("WorldsPanel");
        _worldsPanel.transform.SetParent(canvasGo.transform, false);
        var wimg = _worldsPanel.AddComponent<Image>();
        wimg.color = new Color(0, 0, 0, 0.35f);
        var wrt = _worldsPanel.GetComponent<RectTransform>();
        wrt.anchorMin = new Vector2(0, 1);
        wrt.anchorMax = new Vector2(0, 1);
        wrt.pivot = new Vector2(0, 1);
        wrt.sizeDelta = new Vector2(420, 280);
        wrt.anchoredPosition = new Vector2(20, -20);

        var wtitle = CreateTextTL(_worldsPanel.transform, "WorldsTitle", "Worlds", new Vector2(0, 0), new Vector2(400, 30));
        wtitle.fontSize = 18;
        wtitle.alignment = TextAnchor.MiddleLeft;

        _worldsSourceButton = CreateButtonTL(_worldsPanel.transform, "WorldsSource", "Source: Local", new Vector2(140, 0), new Vector2(140, 28));
        _worldsSourceLabel = _worldsSourceButton.GetComponentInChildren<Text>();
        _worldsSourceButton.onClick.AddListener(() =>
        {
            SetWorldsSource(!_worldsUseOnChain);
            StartCoroutine(RefreshWorlds());
        });

        _refreshWorldsButton = CreateButtonTL(_worldsPanel.transform, "RefreshWorlds", "Refresh", new Vector2(290, 0), new Vector2(110, 28));
        _refreshWorldsButton.onClick.AddListener(() => StartCoroutine(RefreshWorlds()));

        _worldNameInput = CreateInputTL(_worldsPanel.transform, "WorldName", new Vector2(0, -40), new Vector2(280, 28));
        _createWorldButton = CreateButtonTL(_worldsPanel.transform, "CreateWorld", "Create", new Vector2(290, -40), new Vector2(110, 28));
        _createWorldButton.onClick.AddListener(() =>
        {
            var n = (_worldNameInput.text ?? "").Trim();
            if (n.Length == 0) return;
            _worldNameInput.text = "";
            StartCoroutine(CreateWorld(n));
        });

        _selectedWorldLabel = CreateTextTL(_worldsPanel.transform, "SelectedWorld", "Selected: (none)", new Vector2(0, -76), new Vector2(400, 24));
        _selectedWorldLabel.alignment = TextAnchor.MiddleLeft;

        var listGo = new GameObject("WorldsList");
        listGo.transform.SetParent(_worldsPanel.transform, false);
        var lrt = listGo.AddComponent<RectTransform>();
        lrt.anchorMin = new Vector2(0, 1);
        lrt.anchorMax = new Vector2(0, 1);
        lrt.pivot = new Vector2(0, 1);
        lrt.sizeDelta = new Vector2(400, 140);
        lrt.anchoredPosition = new Vector2(0, -104);
        _worldsListRoot = listGo.transform;

        _hostConnectButton = CreateButtonTL(_worldsPanel.transform, "HostConnect", "Host + Connect", new Vector2(0, -250), new Vector2(400, 28));
        _hostConnectButton.onClick.AddListener(() => StartCoroutine(HostAndConnectSelectedWorld()));

        SetWorldsSource(false);
    }

    private void SetWorldsSource(bool onChain)
    {
        _worldsUseOnChain = onChain;
        if (_worldsSourceLabel != null)
        {
            _worldsSourceLabel.text = _worldsUseOnChain ? "Source: On-chain" : "Source: Local";
        }

        // On-chain mode is read-only (directory); local mode can create worlds.
        if (_worldNameInput != null) _worldNameInput.interactable = !_worldsUseOnChain;
        if (_createWorldButton != null) _createWorldButton.interactable = !_worldsUseOnChain;
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
        text.font = GetDefaultFont();
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

    private static Button CreateButtonTL(Transform parent, string name, string label, Vector2 anchoredPos, Vector2 size)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);

        var img = go.AddComponent<Image>();
        img.color = new Color(0.15f, 0.2f, 0.25f, 0.9f);

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0, 1);
        rt.anchorMax = new Vector2(0, 1);
        rt.pivot = new Vector2(0, 1);
        rt.sizeDelta = size;
        rt.anchoredPosition = anchoredPos;

        var btn = go.AddComponent<Button>();

        var text = CreateTextTL(go.transform, $"{name}_Text", label, Vector2.zero, size);
        text.alignment = TextAnchor.MiddleCenter;
        text.color = Color.white;

        return btn;
    }

    private static Text CreateTextTL(Transform parent, string name, string value, Vector2 anchoredPos, Vector2 size)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);

        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(0, 1);
        rt.anchorMax = new Vector2(0, 1);
        rt.pivot = new Vector2(0, 1);
        rt.sizeDelta = size;
        rt.anchoredPosition = anchoredPos;

        var text = go.AddComponent<Text>();
        text.font = GetDefaultFont();
        text.text = value;
        text.fontSize = 14;
        text.color = new Color(0.85f, 0.9f, 1f, 1f);
        return text;
    }

    private static Font GetDefaultFont()
    {
        if (_defaultFont != null) return _defaultFont;
        // Unity 2022+ removed Arial.ttf from built-in fonts.
        _defaultFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (_defaultFont == null)
        {
            _defaultFont = Resources.GetBuiltinResource<Font>("Arial.ttf");
        }
        return _defaultFont;
    }

    private static InputField CreateInputTL(Transform parent, string name, Vector2 anchoredPos, Vector2 size)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);

        var img = go.AddComponent<Image>();
        img.color = new Color(1f, 1f, 1f, 0.1f);

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0, 1);
        rt.anchorMax = new Vector2(0, 1);
        rt.pivot = new Vector2(0, 1);
        rt.sizeDelta = size;
        rt.anchoredPosition = anchoredPos;

        var input = go.AddComponent<InputField>();
        var placeholder = CreateTextTL(go.transform, "Placeholder", "World name…", Vector2.zero, size);
        placeholder.color = new Color(1f, 1f, 1f, 0.35f);
        var text = CreateTextTL(go.transform, "Text", "", Vector2.zero, size);
        text.color = Color.white;
        text.alignment = TextAnchor.MiddleLeft;
        text.supportRichText = false;

        input.textComponent = text;
        input.placeholder = placeholder;
        input.lineType = InputField.LineType.SingleLine;
        input.characterLimit = 64;
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
            if (_gameProcess != null && !_gameProcess.HasExited)
            {
                _gameProcess.Kill();
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
    private class WorldListResponse
    {
        public WorldDirectoryEntry[] items;
    }

    [Serializable]
    private class WorldDirectoryEntry
    {
        public string world_id;
        public string name;
        public string endpoint;
        public int port;
        public string token_mint;
        public string dbc_pool;
        public string world_pubkey;
        public string last_seen;
    }

    [Serializable]
    private class WorldManifest
    {
        public string protocol_version;
        public string world_id;
        public string name;
        public string created_at;
        public string world_authority_pubkey;
        public WorldPorts ports;
    }

    [Serializable]
    private class WorldPorts
    {
        public int game_port;
        public int asset_port;
    }

    [Serializable]
    private class MessageEnvelope
    {
        public string type;
    }

    [Serializable]
    private class HelloMessage
    {
        public string type;
        public string protocol_version;
        public string request_id;
        public string world_id;
        public string client_name;
    }

    [Serializable]
    private class WelcomeMessage
    {
        public string type;
        public string protocol_version;
        public string request_id;
        public string world_id;
        public string token_mint;
        public string motd;
    }

    private struct HandshakeResult
    {
        public bool ok;
        public string motd;
        public string error;
    }

    private struct HttpResult
    {
        public bool ok;
        public long status;
        public string text;
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
