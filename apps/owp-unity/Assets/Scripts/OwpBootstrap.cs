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
    private static OwpBootstrap _instance;

#if UNITY_EDITOR
    private static bool _editorHooksInstalled;
#endif

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
    private ScrollRect _chatScrollRect;

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
        _instance = this;
#if UNITY_EDITOR
        InstallEditorHooks();
#endif
        EnsureSceneBasics();
        CreatePlaceholderAvatar();
        CreateUi();

        StartCoroutine(BootSequence());
    }

#if UNITY_EDITOR
    private static void InstallEditorHooks()
    {
        if (_editorHooksInstalled) return;
        _editorHooksInstalled = true;
        UnityEditor.EditorApplication.playModeStateChanged += (state) =>
        {
            if (state == UnityEditor.PlayModeStateChange.ExitingPlayMode)
            {
                try
                {
                    _instance?.KillChildProcesses();
                }
                catch { }
            }
        };
    }
#endif

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
            EnsureChildProcessPath(psi);

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

    private static void EnsureChildProcessPath(ProcessStartInfo psi)
    {
        // Unity-launched processes often have a minimal PATH on macOS, so `claude` / `codex` may not be found.
        var current = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (psi.EnvironmentVariables.ContainsKey("PATH"))
        {
            current = psi.EnvironmentVariables["PATH"] ?? current;
        }

        var candidatePaths = new[]
        {
            "/opt/homebrew/bin",
            "/usr/local/bin",
            "/usr/bin",
            "/bin",
            "/usr/sbin",
            "/sbin",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cargo", "bin"),
        };

        var next = current;
        foreach (var p in candidatePaths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            if (next.Contains(p)) continue;
            next = string.IsNullOrEmpty(next) ? p : (next + Path.PathSeparator + p);
        }

        psi.EnvironmentVariables["PATH"] = next;
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

        var providerInstalled = IsProviderInstalled(status, provider);
        if (!string.IsNullOrEmpty(provider) && !providerInstalled)
        {
            forceShowProviderPanel = true;
            AppendLog($"Assistant: provider '{provider}' not found. Install it or pick another provider.");
        }

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

    private static bool IsProviderInstalled(AssistantStatus status, string provider)
    {
        if (string.IsNullOrEmpty(provider)) return true;
        if (status == null || status.providers == null) return true;
        foreach (var p in status.providers)
        {
            if (p == null) continue;
            if (p.id == provider) return p.installed;
        }
        return true;
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
        // Back-compat: keep old endpoint available, but the primary UX is /assistant/chat via SendChat.
        var json = $"{{\"prompt\":{JsonEscape(prompt)},\"profile_id\":\"local\"}}";
        var task = HttpRequestAsync("POST", $"{AdminBaseUrl}/avatar/generate", json, 120);
        yield return new WaitUntil(() => task.IsCompleted);

        if (task.Status != TaskStatus.RanToCompletion || !task.Result.ok)
        {
            var status = task.Status == TaskStatus.RanToCompletion ? task.Result.status : 0;
            var detail = task.Status == TaskStatus.RanToCompletion ? (task.Result.text ?? "") : (task.Exception?.GetBaseException().Message ?? "");
            detail = (detail ?? "").Trim();
            if (detail.Length > 180) detail = detail.Substring(0, 180) + "…";
            AppendLog(detail.Length == 0
                ? $"Assistant: avatar generation failed ({status})."
                : $"Assistant: avatar generation failed ({status}) → {detail}");
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

    private IEnumerator SendChat(string message)
    {
        var json = $"{{\"message\":{JsonEscape(message)},\"profile_id\":\"local\"}}";
        var task = HttpRequestAsync("POST", $"{AdminBaseUrl}/assistant/chat", json, 120);
        yield return new WaitUntil(() => task.IsCompleted);

        if (task.Status != TaskStatus.RanToCompletion || !task.Result.ok)
        {
            var status = task.Status == TaskStatus.RanToCompletion ? task.Result.status : 0;
            if (status == 404)
            {
                AppendLog("Assistant: server is outdated (missing /assistant/chat).");
                AppendLog("Assistant: stop Play mode, rebuild `owp-server`, then Play again.");
                yield break;
            }
            if (status == 412)
            {
                AppendLog("Assistant: choose Codex or Claude first.");
                _providerPanel.SetActive(true);
                yield break;
            }

            var detail = task.Status == TaskStatus.RanToCompletion ? (task.Result.text ?? "") : (task.Exception?.GetBaseException().Message ?? "");
            detail = (detail ?? "").Trim();
            if (detail.Length > 180) detail = detail.Substring(0, 180) + "…";
            AppendLog(detail.Length == 0
                ? $"Assistant: chat failed ({status})."
                : $"Assistant: chat failed ({status}) → {detail}");
            yield break;
        }

        var resp = JsonUtility.FromJson<AssistantChatResponse>(task.Result.text);
        if (resp == null)
        {
            AppendLog("Assistant: chat response parse failed.");
            yield break;
        }

        if (!string.IsNullOrEmpty(resp.reply))
        {
            AppendLog($"Companion: {resp.reply}");
        }

        if (resp.avatar != null)
        {
            ApplyAvatar(resp.avatar);
            AppendLog($"Avatar: updated → {resp.avatar.name}");
        }
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

        foreach (var w in worlds)
        {
            if (w == null) continue;
            var btn = CreateButtonLayout(
                _worldsListRoot,
                $"World_{w.world_id}",
                $"{w.name}  ({w.port})",
                width: 0,
                height: 34,
                flexibleWidth: 1
            );
            var label = btn.GetComponentInChildren<Text>();
            if (label != null)
            {
                label.alignment = TextAnchor.MiddleLeft;
            }

            // Simple selected-state highlight
            var img = btn.GetComponent<Image>();
            if (img != null)
            {
                img.color = (w.world_id == _selectedWorldId)
                    ? new Color(0.18f, 0.35f, 0.55f, 0.95f)
                    : new Color(0.15f, 0.2f, 0.25f, 0.90f);
            }
            btn.onClick.AddListener(() =>
            {
                SelectWorld(w.world_id, w.port, w.name);
                RenderWorldList(worlds);
            });
        }

        if (worlds.Length == 0)
        {
            var t = CreateTextLayout(_worldsListRoot, "NoWorlds", "No worlds yet. Create one.", 14, TextAnchor.MiddleLeft);
            AddLayoutElement(t.gameObject, preferredWidth: -1, preferredHeight: 22, flexibleWidth: 1);
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
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1280, 720);
        canvasGo.AddComponent<GraphicRaycaster>();

        if (FindObjectOfType<UnityEngine.EventSystems.EventSystem>() == null)
        {
            var es = new GameObject("EventSystem");
            DontDestroyOnLoad(es);
            es.AddComponent<UnityEngine.EventSystems.EventSystem>();
            es.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
        }

        // Root UI container
        var root = new GameObject("Root");
        root.transform.SetParent(canvasGo.transform, false);
        var rootRt = root.AddComponent<RectTransform>();
        rootRt.anchorMin = Vector2.zero;
        rootRt.anchorMax = Vector2.one;
        rootRt.offsetMin = Vector2.zero;
        rootRt.offsetMax = Vector2.zero;

        // Worlds panel (left)
        _worldsPanel = CreatePanel(root.transform, "WorldsPanel", new Color(0, 0, 0, 0.35f));
        var worldsRt = _worldsPanel.GetComponent<RectTransform>();
        worldsRt.anchorMin = new Vector2(0, 0);
        worldsRt.anchorMax = new Vector2(0.42f, 1);
        worldsRt.offsetMin = new Vector2(16, 16);
        worldsRt.offsetMax = new Vector2(-8, -16);

        var worldsLayout = _worldsPanel.AddComponent<VerticalLayoutGroup>();
        worldsLayout.padding = new RectOffset(12, 12, 12, 12);
        worldsLayout.spacing = 10;
        worldsLayout.childControlWidth = true;
        worldsLayout.childControlHeight = true;
        worldsLayout.childForceExpandWidth = true;
        worldsLayout.childForceExpandHeight = false;

        // Worlds header row
        var worldsHeader = CreateRow(_worldsPanel.transform, "WorldsHeader", 32);
        var worldsTitle = CreateTextLayout(worldsHeader.transform, "WorldsTitle", "Worlds", 18, TextAnchor.MiddleLeft);
        AddLayoutElement(worldsTitle.gameObject, preferredWidth: -1, preferredHeight: 32, flexibleWidth: 1);
        _worldsSourceButton = CreateButtonLayout(worldsHeader.transform, "WorldsSource", "Source: Local", 140, 28);
        _worldsSourceLabel = _worldsSourceButton.GetComponentInChildren<Text>();
        _worldsSourceButton.onClick.AddListener(() =>
        {
            SetWorldsSource(!_worldsUseOnChain);
            StartCoroutine(RefreshWorlds());
        });
        _refreshWorldsButton = CreateButtonLayout(worldsHeader.transform, "RefreshWorlds", "Refresh", 110, 28);
        _refreshWorldsButton.onClick.AddListener(() => StartCoroutine(RefreshWorlds()));

        // Worlds create row
        var createRow = CreateRow(_worldsPanel.transform, "WorldsCreate", 32);
        _worldNameInput = CreateInputLayout(createRow.transform, "WorldName", "World name…", 0, 32, flexibleWidth: 1);
        _createWorldButton = CreateButtonLayout(createRow.transform, "CreateWorld", "Create", 110, 32);
        _createWorldButton.onClick.AddListener(() =>
        {
            var n = (_worldNameInput.text ?? "").Trim();
            if (n.Length == 0) return;
            _worldNameInput.text = "";
            StartCoroutine(CreateWorld(n));
        });

        _selectedWorldLabel = CreateTextLayout(_worldsPanel.transform, "SelectedWorld", "Selected: (none)", 14, TextAnchor.MiddleLeft);
        AddLayoutElement(_selectedWorldLabel.gameObject, preferredWidth: -1, preferredHeight: 22, flexibleWidth: 1);

        // Worlds list (scroll)
        var worldsScroll = CreateScrollView(_worldsPanel.transform, "WorldsScroll");
        AddLayoutElement(worldsScroll.scrollRect.gameObject, preferredWidth: -1, preferredHeight: -1, flexibleWidth: 1, flexibleHeight: 1);
        _worldsListRoot = worldsScroll.content;

        _hostConnectButton = CreateButtonLayout(_worldsPanel.transform, "HostConnect", "Host + Connect", 0, 34, flexibleWidth: 1);
        _hostConnectButton.onClick.AddListener(() => StartCoroutine(HostAndConnectSelectedWorld()));

        // Companion panel (right)
        _chatPanel = CreatePanel(root.transform, "CompanionPanel", new Color(0, 0, 0, 0.70f));
        var chatRt = _chatPanel.GetComponent<RectTransform>();
        chatRt.anchorMin = new Vector2(0.42f, 0);
        chatRt.anchorMax = new Vector2(1, 1);
        chatRt.offsetMin = new Vector2(8, 16);
        chatRt.offsetMax = new Vector2(-16, -16);

        var chatLayout = _chatPanel.AddComponent<VerticalLayoutGroup>();
        chatLayout.padding = new RectOffset(12, 12, 12, 12);
        chatLayout.spacing = 10;
        chatLayout.childControlWidth = true;
        chatLayout.childControlHeight = true;
        chatLayout.childForceExpandWidth = true;
        chatLayout.childForceExpandHeight = false;

        var chatHeader = CreateRow(_chatPanel.transform, "CompanionHeader", 32);
        var companionTitle = CreateTextLayout(chatHeader.transform, "CompanionTitle", "Companion", 18, TextAnchor.MiddleLeft);
        AddLayoutElement(companionTitle.gameObject, preferredWidth: -1, preferredHeight: 32, flexibleWidth: 1);
        _providerButton = CreateButtonLayout(chatHeader.transform, "ProviderButton", "Provider: (loading…)", 180, 28);
        _providerButtonLabel = _providerButton.GetComponentInChildren<Text>();
        _providerButton.onClick.AddListener(() => StartCoroutine(RefreshAssistantStatus(true)));

        var chatScroll = CreateChatScroll(_chatPanel.transform, "ChatScroll");
        AddLayoutElement(chatScroll.scrollRect.gameObject, preferredWidth: -1, preferredHeight: -1, flexibleWidth: 1, flexibleHeight: 1);
        _chatLog = chatScroll.text;
        _chatScrollRect = chatScroll.scrollRect;
        _chatLog.alignment = TextAnchor.UpperLeft;
        _chatLog.supportRichText = true;

        var inputRow = CreateRow(_chatPanel.transform, "ChatInputRow", 36);
        _chatInput = CreateInputLayout(inputRow.transform, "ChatInput", "Describe your avatar…", 0, 36, flexibleWidth: 1);
        _sendButton = CreateButtonLayout(inputRow.transform, "SendButton", "Send", 110, 36);
        _sendButton.onClick.AddListener(() =>
        {
            var text = _chatInput.text ?? "";
            if (text.Trim().Length == 0) return;
            _chatInput.text = "";
            AppendLog($"You: {text}");
            StartCoroutine(SendChat(text));
        });

        // Orb toggle (top-right)
        _orbButton = CreateButton(canvasGo.transform, "OrbButton", "◉", new Vector2(-40, -40), new Vector2(60, 60));
        _orbButton.onClick.AddListener(() =>
        {
            _chatPanel.SetActive(!_chatPanel.activeSelf);
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

        var providerTitle = CreateText(_providerPanel.transform, "ProviderTitle", "Choose provider", new Vector2(-10, -20), new Vector2(400, 40));
        providerTitle.alignment = TextAnchor.MiddleCenter;
        providerTitle.fontSize = 20;

        _useCodexButton = CreateButton(_providerPanel.transform, "UseCodex", "Use Codex", new Vector2(-220, -80), new Vector2(180, 44));
        _useClaudeButton = CreateButton(_providerPanel.transform, "UseClaude", "Use Claude", new Vector2(-20, -80), new Vector2(180, 44));

        _useCodexButton.onClick.AddListener(() => StartCoroutine(SetProvider("codex")));
        _useClaudeButton.onClick.AddListener(() => StartCoroutine(SetProvider("claude")));

        _providerPanel.SetActive(false);

        SetWorldsSource(false);
    }

    private static GameObject CreatePanel(Transform parent, string name, Color bg)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.color = bg;
        return go;
    }

    private static GameObject CreateRow(Transform parent, string name, float height)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var le = go.AddComponent<LayoutElement>();
        le.preferredHeight = height;
        var h = go.AddComponent<HorizontalLayoutGroup>();
        h.padding = new RectOffset(0, 0, 0, 0);
        h.spacing = 8;
        h.childAlignment = TextAnchor.MiddleLeft;
        h.childControlWidth = true;
        h.childControlHeight = true;
        h.childForceExpandWidth = false;
        h.childForceExpandHeight = true;
        return go;
    }

    private static void AddLayoutElement(
        GameObject go,
        float preferredWidth = -1,
        float preferredHeight = -1,
        float flexibleWidth = 0,
        float flexibleHeight = 0
    )
    {
        var le = go.GetComponent<LayoutElement>();
        if (le == null) le = go.AddComponent<LayoutElement>();
        if (preferredWidth >= 0) le.preferredWidth = preferredWidth;
        if (preferredHeight >= 0) le.preferredHeight = preferredHeight;
        if (flexibleWidth > 0) le.flexibleWidth = flexibleWidth;
        if (flexibleHeight > 0) le.flexibleHeight = flexibleHeight;
    }

    private static Text CreateTextLayout(
        Transform parent,
        string name,
        string value,
        int fontSize,
        TextAnchor align
    )
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var text = go.AddComponent<Text>();
        text.font = GetDefaultFont();
        text.text = value;
        text.fontSize = fontSize;
        text.alignment = align;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Truncate;
        text.supportRichText = false;
        text.color = new Color(0.9f, 0.93f, 1f, 1f);
        return text;
    }

    private static Button CreateButtonLayout(
        Transform parent,
        string name,
        string label,
        float width,
        float height,
        float flexibleWidth = 0
    )
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.color = new Color(0.15f, 0.2f, 0.25f, 0.9f);
        var btn = go.AddComponent<Button>();
        var text = CreateTextLayout(go.transform, $"{name}_Text", label, 14, TextAnchor.MiddleCenter);
        text.color = Color.white;
        var trt = text.GetComponent<RectTransform>();
        trt.anchorMin = Vector2.zero;
        trt.anchorMax = Vector2.one;
        trt.offsetMin = new Vector2(10, 4);
        trt.offsetMax = new Vector2(-10, -4);
        var le = go.AddComponent<LayoutElement>();
        if (width > 0) le.preferredWidth = width;
        if (height > 0) le.preferredHeight = height;
        if (flexibleWidth > 0) le.flexibleWidth = flexibleWidth;
        return btn;
    }

    private static InputField CreateInputLayout(
        Transform parent,
        string name,
        string placeholderText,
        float width,
        float height,
        float flexibleWidth = 0
    )
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.color = new Color(1f, 1f, 1f, 0.10f);

        var input = go.AddComponent<InputField>();
        var le = go.AddComponent<LayoutElement>();
        if (width > 0) le.preferredWidth = width;
        if (height > 0) le.preferredHeight = height;
        if (flexibleWidth > 0) le.flexibleWidth = flexibleWidth;

        var placeholder = CreateTextLayout(go.transform, "Placeholder", placeholderText, 14, TextAnchor.MiddleLeft);
        placeholder.color = new Color(1f, 1f, 1f, 0.35f);
        var text = CreateTextLayout(go.transform, "Text", "", 14, TextAnchor.MiddleLeft);
        text.color = Color.white;
        text.supportRichText = false;

        // Stretch inner text to fill input
        var prt = placeholder.GetComponent<RectTransform>();
        prt.anchorMin = Vector2.zero;
        prt.anchorMax = Vector2.one;
        prt.offsetMin = new Vector2(10, 6);
        prt.offsetMax = new Vector2(-10, -6);
        var trt = text.GetComponent<RectTransform>();
        trt.anchorMin = Vector2.zero;
        trt.anchorMax = Vector2.one;
        trt.offsetMin = new Vector2(10, 6);
        trt.offsetMax = new Vector2(-10, -6);

        input.textComponent = text;
        input.placeholder = placeholder;
        input.lineType = InputField.LineType.SingleLine;
        input.characterLimit = 256;
        return input;
    }

    private struct ScrollView
    {
        public ScrollRect scrollRect;
        public RectTransform content;
    }

    private static ScrollView CreateScrollView(Transform parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var bg = go.AddComponent<Image>();
        bg.color = new Color(0, 0, 0, 0.15f);

        var scroll = go.AddComponent<ScrollRect>();
        scroll.horizontal = false;
        scroll.movementType = ScrollRect.MovementType.Clamped;

        // Viewport
        var viewport = new GameObject("Viewport");
        viewport.transform.SetParent(go.transform, false);
        var vpImg = viewport.AddComponent<Image>();
        vpImg.color = new Color(1, 1, 1, 0.02f);
        var mask = viewport.AddComponent<Mask>();
        mask.showMaskGraphic = false;
        var vpRt = viewport.GetComponent<RectTransform>();
        vpRt.anchorMin = Vector2.zero;
        vpRt.anchorMax = Vector2.one;
        vpRt.offsetMin = new Vector2(0, 0);
        vpRt.offsetMax = new Vector2(0, 0);

        // Content
        var content = new GameObject("Content");
        content.transform.SetParent(viewport.transform, false);
        var cRt = content.AddComponent<RectTransform>();
        cRt.anchorMin = new Vector2(0, 1);
        cRt.anchorMax = new Vector2(1, 1);
        cRt.pivot = new Vector2(0.5f, 1);
        cRt.anchoredPosition = Vector2.zero;
        cRt.sizeDelta = new Vector2(0, 0);

        var vlg = content.AddComponent<VerticalLayoutGroup>();
        vlg.padding = new RectOffset(8, 8, 8, 8);
        vlg.spacing = 8;
        vlg.childAlignment = TextAnchor.UpperLeft;
        vlg.childControlWidth = true;
        vlg.childControlHeight = true;
        vlg.childForceExpandWidth = true;
        vlg.childForceExpandHeight = false;
        content.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        scroll.viewport = vpRt;
        scroll.content = cRt;
        scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHideAndExpandViewport;

        return new ScrollView { scrollRect = scroll, content = cRt };
    }

    private struct ChatScroll
    {
        public ScrollRect scrollRect;
        public Text text;
    }

    private static ChatScroll CreateChatScroll(Transform parent, string name)
    {
        var sv = CreateScrollView(parent, name);

        var text = CreateTextLayout(sv.content.transform, "ChatText", "", 14, TextAnchor.UpperLeft);
        text.supportRichText = true;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        var le = text.gameObject.AddComponent<LayoutElement>();
        le.flexibleWidth = 1;

        return new ChatScroll { scrollRect = sv.scrollRect, text = text };
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
        var next = (_chatLog.text + "\n" + line).Trim();
        _chatLog.text = next;

        if (_chatScrollRect != null)
        {
            Canvas.ForceUpdateCanvases();
            _chatScrollRect.verticalNormalizedPosition = 0f;
        }
    }

    private void OnApplicationQuit()
    {
        KillChildProcesses();
    }

    private void OnDestroy()
    {
        KillChildProcesses();
    }

    private void KillChildProcesses()
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
	    private class AssistantChatResponse
	    {
	        public string reply;
	        public AvatarSpec avatar;
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
