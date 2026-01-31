using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GLTFast;
using UnityEngine;
using UnityEngine.UI;

public class OwpBootstrap : MonoBehaviour
{
    private const string AdminBaseUrl = "http://127.0.0.1:9333";
    private const string KenneyCcoPack = "OWPAssetPacks/kenney-cc0";
    private static readonly HttpClient Http = new HttpClient();
    private static Font _defaultFont;
    private static OwpBootstrap _instance;

	#if UNITY_EDITOR
	    private static bool _editorHooksInstalled;
	#endif

	    // Model options are "best effort" presets; users can still type arbitrary prompts.
	    // Keep these aligned with Codex CLI docs (models may vary by account).
	    private static readonly string[] CodexModelOptions =
	    {
	        "default",
	        "gpt-5.2",
	        "gpt-5.2-codex",
	        "gpt-5.1-codex-max",
	        "gpt-5.1-codex",
	        "gpt-5.1-codex-mini",
	        "gpt-5.1",
	        "gpt-5-codex",
	        "gpt-5-codex-mini",
	        "gpt-5",
	        // Older/alt models (may or may not be enabled)
	        "gpt-4.1",
	        "gpt-4.1-mini",
	        "o3-mini",
	    };
	    private static readonly string[] CodexEffortOptions = { "low", "medium", "high", "very_high" };
	    private static readonly string[] ClaudeModelOptions = { "default", "haiku", "sonnet", "opus" };

    private Process _serverProcess;
    private GameObject _avatarSceneRoot;
    private Transform _avatarHoloRing;
    private GameObject _avatarRoot;
    private Transform _avatarBody;
    private Transform _avatarHead;
    private Transform _avatarPartsRoot;
    private Transform _avatarBodyPartsRoot;
    private Transform _avatarHeadPartsRoot;
    private string _avatarArchetype = "humanoid";
    private Vector3 _avatarBodyBaseScale = Vector3.one;
    private Vector3 _avatarHeadBaseScale = new Vector3(0.45f, 0.45f, 0.45f);
    private float _avatarHeadBaseY = 1.55f;
	    private Renderer _avatarRenderer;
	    private Renderer _hairRenderer;
	    private GameObject _avatarMeshGo;
	    private MeshFilter _avatarMeshFilter;
	    private MeshRenderer _avatarMeshRenderer;
	    private string _avatarMeshSha256;
    private Texture2D _starterTexCircuit;
    private Texture2D _starterTexStripes;
    private Texture2D _starterTexScales;
	    private Texture2D _starterTexCloth;

    private static Mesh _meshCone;
    private static Mesh _meshTorus;
    private static Mesh _meshWing;

    private Canvas _canvas;
    private Button _orbButton;
    private Button _worldsToggleButton;
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
    private InputField _worldPromptInput;
    private Button _generateWorldButton;
    private Button _avatarViewButton;
    private Button _worldViewButton;
    private Button _hostConnectButton;
    private Text _selectedWorldLabel;
    private bool _worldsUseOnChain;

    private Process _gameProcess;
    private string _selectedWorldId;
    private int _selectedWorldPort;

    private GameObject _worldRoot;
    private WorldPlan _lastWorldPlan;
    private GameObject _gltfCacheRoot;
    private readonly System.Collections.Generic.Dictionary<string, GameObject> _gltfPrefabCache =
        new System.Collections.Generic.Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);

	    private GameObject _providerPanel;
	    private Button _useCodexButton;
	    private Button _useClaudeButton;
	    private Button _codexModelButton;
	    private Text _codexModelLabel;
	    private Button _codexEffortButton;
	    private Text _codexEffortLabel;
	    private Button _claudeModelButton;
	    private Text _claudeModelLabel;
	    private Button _avatarMeshButton;
	    private Text _avatarMeshLabel;

	    private string _codexModel = "default";
	    private string _codexEffort = "medium";
	    private string _claudeModel = "default";
	    private bool _avatarMeshEnabled = true;

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
        LoadStarterPackResources();
        CreatePlaceholderAvatar();
        EnsureAvatarShowcaseScene();
        CreateUi();
        EnterAvatarView();

        StartCoroutine(BootSequence());
    }

    private void Update()
    {
        if (_avatarHoloRing != null)
        {
            _avatarHoloRing.Rotate(0f, 0f, 22f * Time.deltaTime, Space.Self);
        }
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
	        yield return StartCoroutine(RefreshAssistantConfig());
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

		    private IEnumerator RefreshAssistantConfig()
		    {
		        var task = HttpRequestAsync("GET", $"{AdminBaseUrl}/assistant/config", null, 5);
		        yield return new WaitUntil(() => task.IsCompleted);

	        if (task.Status != TaskStatus.RanToCompletion || !task.Result.ok)
	        {
	            // Older servers won't have this endpoint; keep defaults.
	            UpdateAssistantSettingsUi();
	            yield break;
	        }

		        var cfg = JsonUtility.FromJson<AssistantConfigResponse>(task.Result.text);
		        if (cfg == null)
		        {
		            UpdateAssistantSettingsUi();
		            yield break;
		        }

		        _codexModel = string.IsNullOrEmpty(cfg.codex_model) ? "default" : cfg.codex_model;
		        _claudeModel = string.IsNullOrEmpty(cfg.claude_model) ? "default" : cfg.claude_model;
		        _codexEffort = string.IsNullOrEmpty(cfg.codex_reasoning_effort) ? "medium" : cfg.codex_reasoning_effort;
		        if (_codexEffort == "xhigh") _codexEffort = "very_high";
		        if (task.Result.text != null && task.Result.text.Contains("avatar_mesh_enabled")) _avatarMeshEnabled = cfg.avatar_mesh_enabled;

		        UpdateAssistantSettingsUi();
		    }

		    private IEnumerator SaveAssistantConfig()
		    {
		        var json =
		            "{"
		            + "\"codex_model\":" + JsonEscape(_codexModel)
		            + ",\"codex_reasoning_effort\":" + JsonEscape(_codexEffort)
		            + ",\"claude_model\":" + JsonEscape(_claudeModel)
		            + ",\"avatar_mesh_enabled\":" + (_avatarMeshEnabled ? "true" : "false")
		            + "}";

	        var task = HttpRequestAsync("POST", $"{AdminBaseUrl}/assistant/config", json, 10);
	        yield return new WaitUntil(() => task.IsCompleted);

	        if (task.Status != TaskStatus.RanToCompletion || !task.Result.ok)
	        {
	            AppendLog("Assistant: failed to save settings.");
	            yield break;
	        }

		        var cfg = JsonUtility.FromJson<AssistantConfigResponse>(task.Result.text);
		        if (cfg != null)
		        {
		            _codexModel = string.IsNullOrEmpty(cfg.codex_model) ? "default" : cfg.codex_model;
		            _claudeModel = string.IsNullOrEmpty(cfg.claude_model) ? "default" : cfg.claude_model;
		            _codexEffort = string.IsNullOrEmpty(cfg.codex_reasoning_effort) ? "medium" : cfg.codex_reasoning_effort;
		            if (_codexEffort == "xhigh") _codexEffort = "very_high";
		            _avatarMeshEnabled = cfg.avatar_mesh_enabled;
		        }

		        UpdateAssistantSettingsUi();
		    }

		    private void UpdateAssistantSettingsUi()
		    {
		        if (_codexModelLabel != null) _codexModelLabel.text = $"Codex model: {_codexModel}";
		        // Server stores Codex effort as "xhigh" but UI uses "very_high".
		        var effortLabel = _codexEffort == "xhigh" ? "very_high" : _codexEffort;
		        if (_codexEffortLabel != null) _codexEffortLabel.text = $"Effort: {effortLabel}";
		        if (_claudeModelLabel != null) _claudeModelLabel.text = $"Claude model: {_claudeModel}";
		        if (_avatarMeshLabel != null) _avatarMeshLabel.text = $"Avatar mesh: {(_avatarMeshEnabled ? "on" : "off")}";
		    }

	    private static string CycleOption(string current, string[] options)
	    {
	        if (options == null || options.Length == 0) return current;
	        int idx = 0;
	        for (int i = 0; i < options.Length; i++)
	        {
	            if (options[i] == current) { idx = i; break; }
	        }
	        idx = (idx + 1) % options.Length;
	        return options[idx];
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

    private IEnumerator GenerateWorldPlan(string prompt)
    {
        var json = $"{{\"prompt\":{JsonEscape(prompt)}}}";
        var task = HttpRequestAsync("POST", $"{AdminBaseUrl}/world/plan", json, 180);
        yield return new WaitUntil(() => task.IsCompleted);

        if (task.Status != TaskStatus.RanToCompletion || !task.Result.ok)
        {
            var status = task.Status == TaskStatus.RanToCompletion ? task.Result.status : 0;
            if (status == 404)
            {
                AppendLog("World: server is outdated (missing /world/plan).");
                AppendLog("World: stop Play mode, rebuild `owp-server`, then Play again.");
                yield break;
            }
            if (status == 412)
            {
                AppendLog("World: choose Codex or Claude first.");
                _providerPanel.SetActive(true);
                yield break;
            }

            var detail = task.Status == TaskStatus.RanToCompletion ? (task.Result.text ?? "") : (task.Exception?.GetBaseException().Message ?? "");
            detail = (detail ?? "").Trim();
            if (detail.Length > 180) detail = detail.Substring(0, 180) + "…";
            AppendLog(detail.Length == 0
                ? $"World: plan generation failed ({status})."
                : $"World: plan generation failed ({status}) → {detail}");
            yield break;
        }

        var resp = JsonUtility.FromJson<WorldPlanResponse>(task.Result.text);
        if (resp == null || resp.plan == null)
        {
            AppendLog("World: plan parse failed.");
            yield break;
        }

        ApplyWorldPlan(resp.plan);
        AppendLog($"World: generated → {resp.plan.name}");
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
            var btn = CreateSciFiButton(
                _worldsListRoot,
                $"World_{w.world_id}",
                $"{w.name}  [{w.port}]",
                width: 0,
                height: 30,
                flexibleWidth: 1
            );
            var label = btn.GetComponentInChildren<Text>();
            if (label != null)
            {
                label.alignment = TextAnchor.MiddleLeft;
            }

            // Selected-state highlight on border if present
            var borderObj = btn.transform.Find($"World_{w.world_id}_Border");
            if (borderObj != null)
            {
                var borderImg = borderObj.GetComponent<Image>();
                if (borderImg != null)
                {
                    borderImg.color = (w.world_id == _selectedWorldId)
                        ? new Color(0f, 0.95f, 1f, 1f)
                        : new Color(0f, 0.7f, 1f, 0.6f);
                }
            }

            btn.onClick.AddListener(() =>
            {
                SelectWorld(w.world_id, w.port, w.name);
                RenderWorldList(worlds);
            });
        }

        if (worlds.Length == 0)
        {
            var t = CreateSciFiText(_worldsListRoot, "NoWorlds", "No worlds yet. Create one.", 13, TextAnchor.MiddleLeft, new Color(0.6f, 0.8f, 0.95f, 0.8f));
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

    private static async Task<(bool ok, long status, byte[] bytes, string error)> HttpGetBytesAsync(
        string url,
        int timeoutSeconds
    )
    {
        try
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            using (var resp = await Http.SendAsync(req, cts.Token))
            {
                var code = (long)resp.StatusCode;
                if (resp.Content == null)
                {
                    return (ok: false, status: code, bytes: null, error: "empty response");
                }
                var b = await resp.Content.ReadAsByteArrayAsync();
                return (ok: code >= 200 && code < 300, status: code, bytes: b, error: null);
            }
        }
        catch (Exception ex)
        {
            return (ok: false, status: 0, bytes: null, error: ex.Message);
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

    private static string InferArchetype(AvatarSpec avatar)
    {
        if (avatar == null) return "humanoid";

        if (avatar.tags != null)
        {
            foreach (var t in avatar.tags)
            {
                var tag = (t ?? "").ToLowerInvariant();
                if (tag.Contains("robot") || tag.Contains("android") || tag.Contains("cyborg")) return "robot";
                if (tag.Contains("dragon")) return "dragon";
                if (tag.Contains("angel")) return "angel";
                if (tag.Contains("wizard") || tag.Contains("mage")) return "wizard";
                if (tag.Contains("navi") || tag.Contains("na'vi")) return "navi";
            }
        }

        if (avatar.parts != null)
        {
            foreach (var p in avatar.parts)
            {
                if (p == null || string.IsNullOrEmpty(p.id)) continue;
                var id = p.id.ToLowerInvariant();
                if (id.Contains("visor") || id.Contains("antenna")) return "robot";
                if (id.Contains("halo")) return "angel";
                if (id.Contains("staff")) return "wizard";
            }
        }

        return "humanoid";
    }

    private void EnsureAvatarBase(string archetype)
    {
        if (_avatarRoot == null) return;

        archetype = string.IsNullOrEmpty(archetype) ? "humanoid" : archetype.ToLowerInvariant();
        if (_avatarArchetype == archetype && _avatarBody != null && _avatarHead != null) return;
        _avatarArchetype = archetype;

        if (_avatarBodyPartsRoot != null) Destroy(_avatarBodyPartsRoot.gameObject);
        if (_avatarHeadPartsRoot != null) Destroy(_avatarHeadPartsRoot.gameObject);
        if (_avatarBody != null) Destroy(_avatarBody.gameObject);
        if (_avatarHead != null) Destroy(_avatarHead.gameObject);

        _avatarBodyPartsRoot = null;
        _avatarHeadPartsRoot = null;
        _avatarBody = null;
        _avatarHead = null;
        _avatarRenderer = null;
        _hairRenderer = null;

        var bodyPrim = PrimitiveType.Capsule;
        var headPrim = PrimitiveType.Sphere;
        _avatarBodyBaseScale = Vector3.one;
        _avatarHeadBaseScale = new Vector3(0.45f, 0.45f, 0.45f);
        _avatarHeadBaseY = 1.55f;

        switch (archetype)
        {
            case "robot":
                bodyPrim = PrimitiveType.Cube;
                headPrim = PrimitiveType.Cube;
                _avatarBodyBaseScale = new Vector3(0.9f, 1.25f, 0.6f);
                _avatarHeadBaseScale = new Vector3(0.42f, 0.42f, 0.42f);
                _avatarHeadBaseY = 1.65f;
                break;
            case "dragon":
                _avatarBodyBaseScale = new Vector3(1.1f, 1.05f, 1.1f);
                _avatarHeadBaseScale = new Vector3(0.48f, 0.48f, 0.48f);
                _avatarHeadBaseY = 1.62f;
                break;
            case "navi":
                _avatarBodyBaseScale = new Vector3(0.95f, 1.1f, 0.9f);
                _avatarHeadBaseScale = new Vector3(0.42f, 0.42f, 0.42f);
                _avatarHeadBaseY = 1.62f;
                break;
            case "angel":
            case "wizard":
            default:
                break;
        }

        var body = GameObject.CreatePrimitive(bodyPrim);
        body.name = "Body";
        body.transform.SetParent(_avatarRoot.transform, false);
        body.transform.localPosition = new Vector3(0, 1, 0);
        body.transform.localScale = _avatarBodyBaseScale;
        _avatarBody = body.transform;

        _avatarRenderer = body.GetComponent<Renderer>();
        _avatarRenderer.material = new Material(Shader.Find("Standard"));
        _avatarRenderer.material.color = Color.cyan;

        var head = GameObject.CreatePrimitive(headPrim);
        head.name = "Head";
        head.transform.SetParent(_avatarRoot.transform, false);
        head.transform.localPosition = new Vector3(0, _avatarHeadBaseY, 0);
        head.transform.localScale = _avatarHeadBaseScale;
        _avatarHead = head.transform;

        _hairRenderer = head.GetComponent<Renderer>();
        _hairRenderer.material = new Material(Shader.Find("Standard"));
        _hairRenderer.material.color = Color.white;

        var bodyParts = new GameObject("BodyParts");
        bodyParts.transform.SetParent(_avatarBody, false);
        _avatarBodyPartsRoot = bodyParts.transform;

        var headParts = new GameObject("HeadParts");
        headParts.transform.SetParent(_avatarHead, false);
        _avatarHeadPartsRoot = headParts.transform;
    }

    private void ApplyStarterPackLook(AvatarSpec avatar, string archetype)
    {
        var primary = ParseHex(avatar.primary_color, Color.cyan);
        var secondary = ParseHex(avatar.secondary_color, Color.white);

        var tags = "";
        if (avatar.tags != null && avatar.tags.Length > 0)
        {
            tags = string.Join(" ", avatar.tags).ToLowerInvariant();
        }

        archetype = (archetype ?? "humanoid").ToLowerInvariant();
        var isRobot = archetype == "robot" || tags.Contains("robot") || tags.Contains("cyborg") || tags.Contains("android");
        var isDragon = archetype == "dragon" || tags.Contains("dragon");
        var isAngel = archetype == "angel" || tags.Contains("angel");
        var isWizard = archetype == "wizard" || tags.Contains("wizard") || tags.Contains("mage");
        var isNavi = archetype == "navi" || tags.Contains("navi") || tags.Contains("na'vi");
        var wantsGlow = tags.Contains("glow") || tags.Contains("biolum") || tags.Contains("neon");

        Texture2D bodyTex = null;
        Texture2D headTex = null;
        var metallic = 0.0f;
        var smoothness = 0.4f;

        if (isRobot)
        {
            bodyTex = _starterTexCircuit;
            headTex = _starterTexCircuit;
            metallic = 0.85f;
            smoothness = 0.88f;
            wantsGlow = true;
        }
        else if (isDragon)
        {
            bodyTex = _starterTexScales;
            headTex = _starterTexScales;
            metallic = 0.1f;
            smoothness = 0.35f;
        }
        else if (isWizard || isAngel)
        {
            bodyTex = _starterTexCloth;
            headTex = _starterTexCloth;
            metallic = 0.0f;
            smoothness = 0.3f;
        }
        else if (isNavi)
        {
            bodyTex = _starterTexStripes;
            headTex = _starterTexStripes;
            metallic = 0.0f;
            smoothness = 0.55f;
            wantsGlow = wantsGlow || true;
        }

        var emission = wantsGlow ? primary : Color.black;
        var emissionStrength = wantsGlow ? 0.6f : 0.0f;

        ApplyRendererLook(_avatarRenderer, primary, bodyTex, metallic, smoothness, emission, emissionStrength, tiling: 2f);
        ApplyRendererLook(_hairRenderer, secondary, headTex, metallic * 0.4f, smoothness, emission, emissionStrength * 0.4f, tiling: 2f);
    }

    private static void ApplyRendererLook(
        Renderer r,
        Color color,
        Texture2D tex,
        float metallic,
        float smoothness,
        Color emissionColor,
        float emissionStrength,
        float tiling
    )
    {
        if (r == null) return;
        if (r.material == null) r.material = new Material(Shader.Find("Standard"));

        var m = r.material;
        m.shader = Shader.Find("Standard");
        m.color = color;
        m.SetFloat("_Metallic", Mathf.Clamp01(metallic));
        m.SetFloat("_Glossiness", Mathf.Clamp01(smoothness));

        if (tex != null)
        {
            m.mainTexture = tex;
            m.mainTextureScale = new Vector2(tiling, tiling);
        }
        else
        {
            m.mainTexture = null;
        }

        if (emissionStrength > 0.0f)
        {
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", emissionColor * emissionStrength);
        }
        else
        {
            m.DisableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", Color.black);
        }
    }

    private void ApplyAvatar(AvatarSpec avatar)
    {
        if (_avatarRoot == null) return;

        var archetype = InferArchetype(avatar);
        EnsureAvatarBase(archetype);

        _avatarRoot.name = $"Avatar_{avatar.name}";
        var height = Mathf.Clamp(avatar.height, 0.5f, 2f);
        if (_avatarBody != null)
        {
            _avatarBody.localScale = new Vector3(
                _avatarBodyBaseScale.x,
                _avatarBodyBaseScale.y * height,
                _avatarBodyBaseScale.z
            );
        }
        if (_avatarHead != null)
        {
            _avatarHead.localPosition = new Vector3(0, _avatarHeadBaseY * height, 0);
            _avatarHead.localScale = _avatarHeadBaseScale;
        }

        ApplyStarterPackLook(avatar, archetype);

        ApplyAvatarParts(avatar);

        // Optional mesh override (OpenSCAD STL pipeline).
        StartCoroutine(EnsureAvatarMesh(avatar));
    }

	    private IEnumerator EnsureAvatarMesh(AvatarSpec avatar)
	    {
	        if (avatar == null || avatar.mesh == null || string.IsNullOrEmpty(avatar.mesh.uri))
	        {
	            SetAvatarMeshActive(false);
	            yield break;
	        }
	        if (!string.Equals(avatar.mesh.format, "stl", StringComparison.OrdinalIgnoreCase))
	        {
	            SetAvatarMeshActive(false);
	            yield break;
	        }

	        // Cache by a composite key when provided.
	        var meshKey = BuildMeshKey(avatar.mesh);
	        if (!string.IsNullOrEmpty(meshKey) && string.Equals(_avatarMeshSha256, meshKey, StringComparison.OrdinalIgnoreCase) && _avatarMeshGo != null)
	        {
	            SetAvatarMeshActive(true);
	            yield break;
	        }

	        EnsureAvatarMeshGo();
	        ClearAvatarMeshChildren();

	        var desiredHeight = Mathf.Clamp(avatar.height > 0.1f ? avatar.height : 1.8f, 0.5f, 2.0f);
	        var primaryMat = CreateMeshMaterial(avatar, "primary");
	        var secondaryMat = CreateMeshMaterial(avatar, "secondary");
	        var emissiveMat = CreateMeshMaterial(avatar, "emissive");

	        // Multi-part STL (lets us apply multiple materials even though STL is colorless).
	        if (avatar.mesh.parts != null && avatar.mesh.parts.Length > 0)
	        {
	            AppendLog("Avatar: loading mesh parts…");

	            // Prefer "body" part as the scaling reference.
	            AvatarMeshPart bodyPart = null;
	            foreach (var p in avatar.mesh.parts)
	            {
	                if (p == null) continue;
	                if (p.id == "body") { bodyPart = p; break; }
	            }
	            if (bodyPart == null) bodyPart = avatar.mesh.parts[0];

	            if (bodyPart == null || string.IsNullOrEmpty(bodyPart.uri))
	            {
	                AppendLog("Avatar: mesh parts missing URIs.");
	                SetAvatarMeshActive(false);
	                yield break;
	            }

	            // Load body first to compute bounds/scale.
	            var bodyUrl = AdminBaseUrl + bodyPart.uri;
	            var bodyTask = HttpGetBytesAsync(bodyUrl, 60);
	            yield return new WaitUntil(() => bodyTask.IsCompleted);
	            if (bodyTask.Status != TaskStatus.RanToCompletion || !bodyTask.Result.ok || bodyTask.Result.bytes == null || bodyTask.Result.bytes.Length == 0)
	            {
	                AppendLog($"Avatar: body mesh load failed ({bodyTask.Result.status}).");
	                SetAvatarMeshActive(false);
	                yield break;
	            }
	            if (!StlImporter.TryLoad(bodyTask.Result.bytes, swapYAndZ: true, out var bodyMesh, out var bodyErr))
	            {
	                AppendLog($"Avatar: body mesh parse failed ({bodyErr}).");
	                SetAvatarMeshActive(false);
	                yield break;
	            }

	            var b = bodyMesh.bounds;
	            var h = Mathf.Max(0.0001f, b.size.y);
	            var scale = desiredHeight / h;
	            var minY = b.min.y;
	            var localOffset = new Vector3(0f, -minY, 0f);

	            CreateMeshPartGo("body", bodyMesh, PickMaterial(bodyPart.material, primaryMat, secondaryMat, emissiveMat), scale, localOffset);

	            // Load remaining parts.
	            foreach (var p in avatar.mesh.parts)
	            {
	                if (p == null) continue;
	                if (p.id == "body") continue;
	                if (string.IsNullOrEmpty(p.uri)) continue;

	                var url = AdminBaseUrl + p.uri;
	                var task = HttpGetBytesAsync(url, 60);
	                yield return new WaitUntil(() => task.IsCompleted);
	                if (task.Status != TaskStatus.RanToCompletion || !task.Result.ok || task.Result.bytes == null || task.Result.bytes.Length == 0)
	                {
	                    continue;
	                }
	                if (!StlImporter.TryLoad(task.Result.bytes, swapYAndZ: true, out var mesh, out var err))
	                {
	                    continue;
	                }
	                CreateMeshPartGo(p.id, mesh, PickMaterial(p.material, primaryMat, secondaryMat, emissiveMat), scale, localOffset);
	            }

	            _avatarMeshGo.transform.localScale = Vector3.one;
	            _avatarMeshGo.transform.localPosition = Vector3.zero;
	            _avatarMeshGo.transform.localRotation = Quaternion.identity;

	            _avatarMeshSha256 = meshKey;
	            SetAvatarMeshActive(true);
	            AppendLog("Avatar: mesh parts applied.");
	            yield break;
	        }

	        // Single-mesh STL fallback.
	        var urlSingle = AdminBaseUrl + avatar.mesh.uri;
	        AppendLog("Avatar: loading mesh…");

	        var taskSingle = HttpGetBytesAsync(urlSingle, 60);
	        yield return new WaitUntil(() => taskSingle.IsCompleted);

	        if (taskSingle.Status != TaskStatus.RanToCompletion || !taskSingle.Result.ok || taskSingle.Result.bytes == null || taskSingle.Result.bytes.Length == 0)
	        {
	            AppendLog($"Avatar: mesh load failed ({taskSingle.Result.status}).");
	            SetAvatarMeshActive(false);
	            yield break;
	        }

	        if (!StlImporter.TryLoad(taskSingle.Result.bytes, swapYAndZ: true, out var meshSingle, out var errSingle))
	        {
	            AppendLog($"Avatar: mesh parse failed ({errSingle}).");
	            SetAvatarMeshActive(false);
	            yield break;
	        }

	        var bb = meshSingle.bounds;
	        var hh = Mathf.Max(0.0001f, bb.size.y);
	        var scaleSingle = desiredHeight / hh;
	        var minYSingle = bb.min.y;
	        var localOffsetSingle = new Vector3(0f, -minYSingle, 0f);

	        CreateMeshPartGo("mesh", meshSingle, primaryMat, scaleSingle, localOffsetSingle);

	        _avatarMeshSha256 = meshKey;
	        SetAvatarMeshActive(true);
	        AppendLog("Avatar: mesh applied.");
	    }

	    private static string BuildMeshKey(AvatarMesh mesh)
	    {
	        if (mesh == null) return null;
	        var sb = new StringBuilder();
	        sb.Append(mesh.sha256 ?? "");
	        if (mesh.parts != null)
	        {
	            foreach (var p in mesh.parts)
	            {
	                if (p == null) continue;
	                sb.Append("|");
	                sb.Append(p.id ?? "");
	                sb.Append(":");
	                sb.Append(p.sha256 ?? "");
	            }
	        }
	        return sb.ToString();
	    }

	    private void EnsureAvatarMeshGo()
	    {
	        if (_avatarMeshGo != null) return;
	        if (_avatarRoot == null) return;

	        _avatarMeshGo = new GameObject("AvatarMesh");
	        _avatarMeshGo.transform.SetParent(_avatarRoot.transform, false);
	        _avatarMeshFilter = _avatarMeshGo.AddComponent<MeshFilter>();
	        _avatarMeshRenderer = _avatarMeshGo.AddComponent<MeshRenderer>();
	        _avatarMeshGo.SetActive(false);
	    }

	    private void ClearAvatarMeshChildren()
	    {
	        if (_avatarMeshGo == null) return;
	        for (int i = _avatarMeshGo.transform.childCount - 1; i >= 0; i--)
	        {
	            Destroy(_avatarMeshGo.transform.GetChild(i).gameObject);
	        }
	        if (_avatarMeshFilter != null) _avatarMeshFilter.sharedMesh = null;
	    }

	    private void CreateMeshPartGo(string id, Mesh mesh, Material mat, float scale, Vector3 localOffset)
	    {
	        if (_avatarMeshGo == null) return;
	        var go = new GameObject($"MeshPart_{id}");
	        go.transform.SetParent(_avatarMeshGo.transform, false);
	        var mf = go.AddComponent<MeshFilter>();
	        mf.sharedMesh = mesh;
	        var mr = go.AddComponent<MeshRenderer>();
	        mr.sharedMaterial = mat;
	        go.transform.localScale = Vector3.one * scale;
	        go.transform.localPosition = localOffset * scale;
	        go.transform.localRotation = Quaternion.identity;
	    }

	    private static Material PickMaterial(string hint, Material primary, Material secondary, Material emissive)
	    {
	        if (string.Equals(hint, "secondary", StringComparison.OrdinalIgnoreCase)) return secondary;
	        if (string.Equals(hint, "emissive", StringComparison.OrdinalIgnoreCase)) return emissive;
	        return primary;
	    }

	    private static Material CreateMeshMaterial(AvatarSpec avatar, string hint)
	    {
	        var primary = ParseHexColor(avatar != null ? avatar.primary_color : null, new Color(0f, 0.82f, 1f, 1f));
	        var secondary = ParseHexColor(avatar != null ? avatar.secondary_color : null, Color.white);

	        var baseColor = primary;
	        if (string.Equals(hint, "secondary", StringComparison.OrdinalIgnoreCase)) baseColor = secondary;

	        var m = new Material(Shader.Find("Standard"));
	        m.shader = Shader.Find("Standard");
	        m.color = baseColor;
	        m.SetFloat("_Metallic", 0.05f);
	        m.SetFloat("_Glossiness", 0.55f);

	        var emissive = Color.black;
	        if (string.Equals(hint, "emissive", StringComparison.OrdinalIgnoreCase))
	        {
	            emissive = primary * 1.25f;
	        }
	        else
	        {
	            emissive = primary * 0.12f;
	        }
	        m.EnableKeyword("_EMISSION");
	        m.SetColor("_EmissionColor", emissive);
	        return m;
	    }

    private void SetAvatarMeshActive(bool active)
    {
        if (_avatarMeshGo != null) _avatarMeshGo.SetActive(active);

        var showPrims = !active;
        if (_avatarBody != null) _avatarBody.gameObject.SetActive(showPrims);
        if (_avatarHead != null) _avatarHead.gameObject.SetActive(showPrims);
        if (_avatarBodyPartsRoot != null) _avatarBodyPartsRoot.gameObject.SetActive(showPrims);
        if (_avatarHeadPartsRoot != null) _avatarHeadPartsRoot.gameObject.SetActive(showPrims);
    }

    private static Color ParseHexColor(string hex, Color fallback)
    {
        if (string.IsNullOrEmpty(hex)) return fallback;
        if (!hex.StartsWith("#") || hex.Length != 7) return fallback;
        try
        {
            var r = byte.Parse(hex.Substring(1, 2), System.Globalization.NumberStyles.HexNumber);
            var g = byte.Parse(hex.Substring(3, 2), System.Globalization.NumberStyles.HexNumber);
            var b = byte.Parse(hex.Substring(5, 2), System.Globalization.NumberStyles.HexNumber);
            return new Color(r / 255f, g / 255f, b / 255f, 1f);
        }
        catch
        {
            return fallback;
        }
    }

    private void ApplyAvatarParts(AvatarSpec avatar)
    {
        if (_avatarBodyPartsRoot == null && _avatarHeadPartsRoot == null) return;

        if (_avatarBodyPartsRoot != null)
        {
            for (int i = _avatarBodyPartsRoot.childCount - 1; i >= 0; i--)
            {
                Destroy(_avatarBodyPartsRoot.GetChild(i).gameObject);
            }
        }
        if (_avatarHeadPartsRoot != null)
        {
            for (int i = _avatarHeadPartsRoot.childCount - 1; i >= 0; i--)
            {
                Destroy(_avatarHeadPartsRoot.GetChild(i).gameObject);
            }
        }

        if (avatar.parts == null || avatar.parts.Length == 0)
        {
            // Fallback: generate a couple of parts from tags to keep "prompt anything" somewhat visible.
            if (avatar.tags == null) return;
            foreach (var t in avatar.tags)
            {
                if (string.IsNullOrEmpty(t)) continue;
                var tag = t.ToLowerInvariant();
                if (tag.Contains("horn"))
                {
                    SpawnPart(new AvatarPart
                    {
                        id = "horn_left",
                        attach = "head",
                        primitive = "capsule",
                        position = new float[] { -0.25f, 0.24f, 0.06f },
                        rotation = new float[] { 25f, 0f, 20f },
                        scale = new float[] { 0.12f, 0.45f, 0.12f },
                        color = avatar.secondary_color,
                        emission_color = null,
                        emission_strength = 0f
                    }, avatar);
                    SpawnPart(new AvatarPart
                    {
                        id = "horn_right",
                        attach = "head",
                        primitive = "capsule",
                        position = new float[] { 0.25f, 0.24f, 0.06f },
                        rotation = new float[] { 25f, 0f, -20f },
                        scale = new float[] { 0.12f, 0.45f, 0.12f },
                        color = avatar.secondary_color,
                        emission_color = null,
                        emission_strength = 0f
                    }, avatar);
                }
                if (tag.Contains("glow") || tag.Contains("biolum"))
                {
                    // simple glow stripes
                    for (int i = 0; i < 5; i++)
                    {
                        SpawnPart(new AvatarPart
                        {
                            id = $"stripe_{i}",
                            attach = "body",
                            primitive = "cube",
                            position = new float[] { -0.15f + i * 0.075f, 0.85f, -0.56f },
                            rotation = new float[] { 0f, 0f, 0f },
                            scale = new float[] { 0.02f, 0.4f, 0.02f },
                            color = avatar.primary_color,
                            emission_color = avatar.primary_color,
                            emission_strength = 2.5f
                        }, avatar);
                    }
                }
            }
            return;
        }

        foreach (var p in avatar.parts)
        {
            if (p == null) continue;
            SpawnPart(p, avatar);
        }
    }

    private void SpawnPart(AvatarPart p, AvatarSpec avatar)
    {
        EnsureStarterMeshes();

        var root = new GameObject($"Part_{p.id}");

        var attach = (p.attach ?? "body").ToLowerInvariant();
        if (attach == "head" && _avatarHeadPartsRoot != null)
        {
            root.transform.SetParent(_avatarHeadPartsRoot, false);
        }
        else if (attach == "head" && _avatarHead != null)
        {
            root.transform.SetParent(_avatarHead, false);
        }
        else if (_avatarBodyPartsRoot != null)
        {
            root.transform.SetParent(_avatarBodyPartsRoot, false);
        }
        else if (_avatarBody != null)
        {
            root.transform.SetParent(_avatarBody, false);
        }
        else if (_avatarRoot != null)
        {
            root.transform.SetParent(_avatarRoot.transform, false);
        }

        root.transform.localPosition = ToVector3(p.position);
        root.transform.localRotation = Quaternion.Euler(ToVector3(p.rotation));
        root.transform.localScale = ToVector3(p.scale, Vector3.one * 0.1f);

        var id = (p.id ?? "").ToLowerInvariant();
        if (id.Contains("staff"))
        {
            SpawnWizardStaff(root.transform, p, avatar);
            return;
        }
        if (id.Contains("hat_top"))
        {
            SpawnWizardHatTop(root.transform, p, avatar);
            return;
        }
        if (id.Contains("halo"))
        {
            SpawnHalo(root.transform, p, avatar);
            return;
        }
        if (id.Contains("wing"))
        {
            SpawnWing(root.transform, p, avatar);
            return;
        }

        // default primitive
        var prim = ParsePrimitive(p.primitive);
        var go = GameObject.CreatePrimitive(prim);
        go.name = "Primitive";
        go.transform.SetParent(root.transform, false);
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = Vector3.one;
        RemoveCollider(go);
        ApplyPartMaterial(go, p);
    }

    private static void RemoveCollider(GameObject go)
    {
        // Avoid a hard dependency on Unity's Physics module (Collider types can be disabled).
        // Using the string overload keeps this script compiling even when Physics is not installed/enabled.
        var c = go.GetComponent("Collider");
        if (c != null) Destroy(c);
    }

    private static void ApplyPartMaterial(GameObject go, AvatarPart p)
    {
        var r = go.GetComponent<Renderer>();
        if (r == null) return;

        r.material = new Material(Shader.Find("Standard"));
        r.material.color = ParseHex(p.color, Color.white);

        if (!string.IsNullOrEmpty(p.emission_color) && p.emission_strength > 0f)
        {
            var ec = ParseHex(p.emission_color, r.material.color);
            r.material.EnableKeyword("_EMISSION");
            r.material.SetColor("_EmissionColor", ec * p.emission_strength);
        }
    }

    private void ApplyPartMaterialRecursive(Transform root, AvatarPart p)
    {
        foreach (var r in root.GetComponentsInChildren<Renderer>())
        {
            r.material = new Material(Shader.Find("Standard"));
            r.material.color = ParseHex(p.color, Color.white);

            if (!string.IsNullOrEmpty(p.emission_color) && p.emission_strength > 0f)
            {
                var ec = ParseHex(p.emission_color, r.material.color);
                r.material.EnableKeyword("_EMISSION");
                r.material.SetColor("_EmissionColor", ec * p.emission_strength);
            }
        }
    }

    private void SpawnWizardStaff(Transform parent, AvatarPart p, AvatarSpec avatar)
    {
        // Shaft
        var shaft = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        shaft.name = "Staff_Shaft";
        shaft.transform.SetParent(parent, false);
        shaft.transform.localPosition = new Vector3(0f, 0.5f, 0f);
        shaft.transform.localRotation = Quaternion.identity;
        shaft.transform.localScale = new Vector3(0.12f, 1.0f, 0.12f);
        RemoveCollider(shaft);

        // Top crystal
        var gem = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        gem.name = "Staff_Gem";
        gem.transform.SetParent(parent, false);
        gem.transform.localPosition = new Vector3(0f, 1.12f, 0f);
        gem.transform.localRotation = Quaternion.identity;
        gem.transform.localScale = new Vector3(0.28f, 0.28f, 0.28f);
        RemoveCollider(gem);

        var gemPart = new AvatarPart
        {
            id = p.id,
            attach = p.attach,
            primitive = "sphere",
            position = p.position,
            rotation = p.rotation,
            scale = p.scale,
            color = avatar.primary_color,
            emission_color = avatar.primary_color,
            emission_strength = 2.2f
        };

        ApplyPartMaterial(shaft, p);
        ApplyPartMaterial(gem, gemPart);
    }

    private void SpawnWizardHatTop(Transform parent, AvatarPart p, AvatarSpec avatar)
    {
        var go = new GameObject("Hat_Cone");
        go.transform.SetParent(parent, false);
        go.transform.localPosition = new Vector3(0f, 0.35f, 0f);
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = new Vector3(0.55f, 0.75f, 0.55f);

        var mf = go.AddComponent<MeshFilter>();
        mf.sharedMesh = _meshCone;
        var mr = go.AddComponent<MeshRenderer>();
        mr.material = new Material(Shader.Find("Standard"));
        mr.material.color = ParseHex(p.color, Color.white);
    }

    private void SpawnHalo(Transform parent, AvatarPart p, AvatarSpec avatar)
    {
        var go = new GameObject("Halo_Torus");
        go.transform.SetParent(parent, false);
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = Vector3.one;

        var mf = go.AddComponent<MeshFilter>();
        mf.sharedMesh = _meshTorus;
        var mr = go.AddComponent<MeshRenderer>();
        mr.material = new Material(Shader.Find("Standard"));
        var c = ParseHex(p.color, Color.white);
        mr.material.color = c;
        mr.material.EnableKeyword("_EMISSION");
        mr.material.SetColor("_EmissionColor", c * 2.0f);
    }

    private void SpawnWing(Transform parent, AvatarPart p, AvatarSpec avatar)
    {
        var go = new GameObject("Wing_Quad");
        go.transform.SetParent(parent, false);
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = Vector3.one;

        var mf = go.AddComponent<MeshFilter>();
        mf.sharedMesh = _meshWing;
        var mr = go.AddComponent<MeshRenderer>();
        mr.material = new Material(Shader.Find("Standard"));
        mr.material.color = ParseHex(p.color, Color.white);
        if (_starterTexStripes != null)
        {
            mr.material.mainTexture = _starterTexStripes;
            mr.material.mainTextureScale = new Vector2(2f, 2f);
        }
    }

    private static PrimitiveType ParsePrimitive(string primitive)
    {
        switch ((primitive ?? "").ToLowerInvariant())
        {
            case "sphere": return PrimitiveType.Sphere;
            case "capsule": return PrimitiveType.Capsule;
            case "cylinder": return PrimitiveType.Cylinder;
            case "cube":
            default: return PrimitiveType.Cube;
        }
    }

    private static Vector3 ToVector3(float[] v, Vector3 fallback = default(Vector3))
    {
        if (v == null || v.Length < 3) return fallback;
        return new Vector3(v[0], v[1], v[2]);
    }

    private static Vector3 ToVector3(float[] v) => ToVector3(v, Vector3.zero);

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

    private void LoadStarterPackResources()
    {
        // Optional built-in textures for a slightly more detailed “starter pack” look.
        // These live under Assets/Resources/OWPStarterPack/Textures/.
        _starterTexCircuit = Resources.Load<Texture2D>("OWPStarterPack/Textures/circuit");
        _starterTexStripes = Resources.Load<Texture2D>("OWPStarterPack/Textures/stripes");
        _starterTexScales = Resources.Load<Texture2D>("OWPStarterPack/Textures/scales");
        _starterTexCloth = Resources.Load<Texture2D>("OWPStarterPack/Textures/cloth");
    }

    private static void EnsureStarterMeshes()
    {
        if (_meshCone == null) _meshCone = BuildConeMesh(20);
        if (_meshTorus == null) _meshTorus = BuildTorusMesh(0.5f, 0.12f, 28, 14);
        if (_meshWing == null) _meshWing = BuildWingMesh();
    }

    private static Mesh BuildWingMesh()
    {
        var m = new Mesh();
        var v = new[]
        {
            new Vector3(0f, 0f, 0f),
            new Vector3(1f, 0f, 0f),
            new Vector3(0f, 1f, 0f),
            new Vector3(1f, 1f, 0f),
        };
        var uv = new[]
        {
            new Vector2(0f, 0f),
            new Vector2(1f, 0f),
            new Vector2(0f, 1f),
            new Vector2(1f, 1f),
        };
        var t = new[] { 0, 2, 1, 2, 3, 1 };
        m.vertices = v;
        m.uv = uv;
        m.triangles = t;
        m.RecalculateNormals();
        m.RecalculateBounds();
        return m;
    }

    private static Mesh BuildConeMesh(int sides)
    {
        sides = Mathf.Clamp(sides, 8, 64);
        var m = new Mesh();
        var verts = new System.Collections.Generic.List<Vector3>();
        var tris = new System.Collections.Generic.List<int>();

        // tip
        verts.Add(new Vector3(0f, 1f, 0f)); // 0
        // base center
        verts.Add(new Vector3(0f, 0f, 0f)); // 1

        for (int i = 0; i < sides; i++)
        {
            var a = (float)i / sides * Mathf.PI * 2f;
            verts.Add(new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)));
        }

        // side triangles
        for (int i = 0; i < sides; i++)
        {
            var a = 2 + i;
            var b = 2 + ((i + 1) % sides);
            tris.Add(0);
            tris.Add(b);
            tris.Add(a);
        }

        // base triangles
        for (int i = 0; i < sides; i++)
        {
            var a = 2 + i;
            var b = 2 + ((i + 1) % sides);
            tris.Add(1);
            tris.Add(a);
            tris.Add(b);
        }

        m.SetVertices(verts);
        m.SetTriangles(tris, 0);
        m.RecalculateNormals();
        m.RecalculateBounds();
        return m;
    }

    private static Mesh BuildTorusMesh(float majorRadius, float minorRadius, int majorSegments, int minorSegments)
    {
        majorSegments = Mathf.Clamp(majorSegments, 12, 128);
        minorSegments = Mathf.Clamp(minorSegments, 8, 64);
        majorRadius = Mathf.Max(0.05f, majorRadius);
        minorRadius = Mathf.Max(0.01f, minorRadius);

        var m = new Mesh();
        var verts = new Vector3[majorSegments * minorSegments];
        var uvs = new Vector2[verts.Length];
        var tris = new int[majorSegments * minorSegments * 6];

        int vi = 0;
        for (int i = 0; i < majorSegments; i++)
        {
            var u = (float)i / majorSegments;
            var a = u * Mathf.PI * 2f;
            var center = new Vector3(Mathf.Cos(a) * majorRadius, 0f, Mathf.Sin(a) * majorRadius);
            var tangent = new Vector3(-Mathf.Sin(a), 0f, Mathf.Cos(a));
            var up = Vector3.up;
            var bitangent = Vector3.Cross(tangent, up).normalized;
            tangent = tangent.normalized;

            for (int j = 0; j < minorSegments; j++)
            {
                var v = (float)j / minorSegments;
                var b = v * Mathf.PI * 2f;
                var ring = (Mathf.Cos(b) * bitangent + Mathf.Sin(b) * up) * minorRadius;
                verts[vi] = center + ring;
                uvs[vi] = new Vector2(u, v);
                vi++;
            }
        }

        int ti = 0;
        for (int i = 0; i < majorSegments; i++)
        {
            int ni = (i + 1) % majorSegments;
            for (int j = 0; j < minorSegments; j++)
            {
                int nj = (j + 1) % minorSegments;
                int a = i * minorSegments + j;
                int b = ni * minorSegments + j;
                int c = i * minorSegments + nj;
                int d = ni * minorSegments + nj;

                tris[ti++] = a;
                tris[ti++] = d;
                tris[ti++] = b;
                tris[ti++] = a;
                tris[ti++] = c;
                tris[ti++] = d;
            }
        }

        m.vertices = verts;
        m.uv = uvs;
        m.triangles = tris;
        m.RecalculateNormals();
        m.RecalculateBounds();
        return m;
    }

    private void CreatePlaceholderAvatar()
    {
        _avatarRoot = new GameObject("AvatarRoot");
        _avatarRoot.transform.position = new Vector3(0, 0, 0);

        var parts = new GameObject("Parts");
        parts.transform.SetParent(_avatarRoot.transform, false);
        _avatarPartsRoot = parts.transform;

        EnsureAvatarBase("humanoid");
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

    private void EnsureAvatarShowcaseScene()
    {
        if (_avatarSceneRoot != null) return;

        EnsureStarterMeshes();
        EnsureGltfCacheRoot();

        _avatarSceneRoot = new GameObject("OWP_AvatarShowcase");
        DontDestroyOnLoad(_avatarSceneRoot);

        // Avatar-leaning render settings (overwritten when a world plan is applied).
        var skyShader = Shader.Find("Skybox/Procedural");
        if (skyShader != null)
        {
            var sky = new Material(skyShader);
            sky.SetColor("_SkyTint", new Color(0.10f, 0.18f, 0.28f, 1f));
            sky.SetColor("_GroundColor", new Color(0.03f, 0.04f, 0.06f, 1f));
            sky.SetFloat("_AtmosphereThickness", 1.6f);
            sky.SetFloat("_SunSize", 0.04f);
            RenderSettings.skybox = sky;
        }
        RenderSettings.fog = true;
        RenderSettings.fogColor = new Color(0.03f, 0.05f, 0.08f, 1f);
        RenderSettings.fogDensity = 0.0105f;
        RenderSettings.ambientLight = new Color(0.12f, 0.16f, 0.22f, 1f);
        RenderSettings.ambientIntensity = 1.2f;

        // Lighting
        var dir = FindObjectsOfType<Light>();
        Light dirLight = null;
        foreach (var l in dir)
        {
            if (l != null && l.type == LightType.Directional)
            {
                dirLight = l;
                break;
            }
        }
        if (dirLight == null)
        {
            var lightGo = new GameObject("Directional Light");
            dirLight = lightGo.AddComponent<Light>();
            dirLight.type = LightType.Directional;
        }
        dirLight.color = new Color(0.75f, 0.88f, 1.0f, 1f);
        dirLight.intensity = 1.15f;
        dirLight.transform.rotation = Quaternion.Euler(50f, -35f, 0f);

        var rim = new GameObject("AvatarRimLight");
        rim.transform.SetParent(_avatarSceneRoot.transform, false);
        rim.transform.position = new Vector3(0.0f, 2.2f, 3.8f);
        var rimLight = rim.AddComponent<Light>();
        rimLight.type = LightType.Point;
        rimLight.range = 12f;
        rimLight.intensity = 1.8f;
        rimLight.color = new Color(0.35f, 0.95f, 0.85f, 1f);

        var magenta = new GameObject("AvatarAccentLight");
        magenta.transform.SetParent(_avatarSceneRoot.transform, false);
        magenta.transform.position = new Vector3(-3.2f, 1.8f, -1.8f);
        var magentaLight = magenta.AddComponent<Light>();
        magentaLight.type = LightType.Point;
        magentaLight.range = 10f;
        magentaLight.intensity = 1.4f;
        magentaLight.color = new Color(0.75f, 0.35f, 1.0f, 1f);

        // Platform
        var platform = GameObject.CreatePrimitive(PrimitiveType.Cube);
        platform.name = "Platform";
        platform.transform.SetParent(_avatarSceneRoot.transform, false);
        platform.transform.position = new Vector3(0f, -0.05f, 0f);
        platform.transform.localScale = new Vector3(10f, 0.1f, 10f);
        RemoveCollider(platform);

        var platformMat = new Material(Shader.Find("Standard"));
        platformMat.color = new Color(0.06f, 0.08f, 0.12f, 1f);
        platformMat.SetFloat("_Glossiness", 0.15f);
        if (_starterTexCircuit != null)
        {
            platformMat.mainTexture = _starterTexCircuit;
            platformMat.mainTextureScale = new Vector2(4f, 4f);
        }
        platformMat.EnableKeyword("_EMISSION");
        platformMat.SetColor("_EmissionColor", new Color(0f, 0.45f, 0.9f, 1f) * 0.35f);
        var pr = platform.GetComponent<Renderer>();
        if (pr != null) pr.material = platformMat;

        // Back wall
        var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        wall.name = "Backdrop";
        wall.transform.SetParent(_avatarSceneRoot.transform, false);
        wall.transform.position = new Vector3(0f, 2.6f, 9.5f);
        wall.transform.localScale = new Vector3(16f, 6f, 0.2f);
        RemoveCollider(wall);
        var wr = wall.GetComponent<Renderer>();
        if (wr != null)
        {
            var mat = new Material(Shader.Find("Standard"));
            mat.color = new Color(0.03f, 0.05f, 0.09f, 1f);
            mat.SetFloat("_Glossiness", 0.05f);
            if (_starterTexStripes != null)
            {
                mat.mainTexture = _starterTexStripes;
                mat.mainTextureScale = new Vector2(6f, 2f);
            }
            wr.material = mat;
        }

        // Hologram ring
        var ring = new GameObject("HoloRing");
        ring.transform.SetParent(_avatarSceneRoot.transform, false);
        ring.transform.position = new Vector3(0f, 0.02f, 0f);
        ring.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        ring.transform.localScale = new Vector3(2.8f, 2.8f, 0.22f);
        _avatarHoloRing = ring.transform;
        var ringMf = ring.AddComponent<MeshFilter>();
        ringMf.sharedMesh = _meshTorus;
        var ringMr = ring.AddComponent<MeshRenderer>();
        var ringMat = new Material(Shader.Find("Standard"));
        ringMat.color = new Color(0.02f, 0.03f, 0.05f, 1f);
        ringMat.EnableKeyword("_EMISSION");
        ringMat.SetColor("_EmissionColor", new Color(0.35f, 0.95f, 0.85f, 1f) * 2.0f);
        ringMr.material = ringMat;

        // CC0 props (Kenney pack) — makes the avatar stage look like a real scene without LLM mesh generation.
        CreateKenneyPropHolder(_avatarSceneRoot.transform, "lampRoundFloor", new Vector3(-2.6f, 0f, 1.8f), new Vector3(0f, 30f, 0f), Vector3.one * 0.95f);
        CreateKenneyPropHolder(_avatarSceneRoot.transform, "lampRoundFloor", new Vector3(2.6f, 0f, 1.8f), new Vector3(0f, -30f, 0f), Vector3.one * 0.95f);

        CreateKenneyPropHolder(_avatarSceneRoot.transform, "gate_complex", new Vector3(0f, 0f, 6.2f), new Vector3(0f, 180f, 0f), Vector3.one * 1.25f);

        CreateKenneyPropHolder(_avatarSceneRoot.transform, "rock_crystalsLargeA", new Vector3(-3.4f, 0f, 5.2f), new Vector3(0f, 10f, 0f), Vector3.one * 1.1f);
        CreateKenneyPropHolder(_avatarSceneRoot.transform, "rock_crystalsLargeB", new Vector3(3.2f, 0f, 5.6f), new Vector3(0f, -20f, 0f), Vector3.one * 1.15f);

        CreateKenneyPropHolder(_avatarSceneRoot.transform, "barrels", new Vector3(2.1f, 0f, -1.4f), new Vector3(0f, 45f, 0f), Vector3.one * 1.0f);
        CreateKenneyPropHolder(_avatarSceneRoot.transform, "barrel", new Vector3(-1.9f, 0f, -1.2f), new Vector3(0f, -30f, 0f), Vector3.one * 1.0f);

        CreateKenneyPropHolder(_avatarSceneRoot.transform, "astronautA", new Vector3(-3.6f, 0f, 2.1f), new Vector3(0f, 90f, 0f), Vector3.one * 1.05f);
        CreateKenneyPropHolder(_avatarSceneRoot.transform, "alien", new Vector3(3.6f, 0f, 2.2f), new Vector3(0f, -90f, 0f), Vector3.one * 1.05f);

        CreateKenneyPropHolder(_avatarSceneRoot.transform, "plantSmall2", new Vector3(-2.8f, 0f, 3.5f), new Vector3(0f, 25f, 0f), Vector3.one * 1.15f);
        CreateKenneyPropHolder(_avatarSceneRoot.transform, "plantSmall3", new Vector3(2.9f, 0f, 3.3f), new Vector3(0f, -35f, 0f), Vector3.one * 1.1f);
        CreateKenneyPropHolder(_avatarSceneRoot.transform, "pottedPlant", new Vector3(0.0f, 0f, 2.2f), new Vector3(0f, 0f, 0f), Vector3.one * 1.05f);

        CreateKenneyPropHolder(_avatarSceneRoot.transform, "van", new Vector3(-4.0f, 0f, 7.0f), new Vector3(0f, 25f, 0f), Vector3.one * 1.1f);
        CreateKenneyPropHolder(_avatarSceneRoot.transform, "ambulance", new Vector3(4.2f, 0f, 7.2f), new Vector3(0f, -25f, 0f), Vector3.one * 1.1f);

        // Accent pylons
        for (int i = 0; i < 4; i++)
        {
            float a = i * Mathf.PI * 0.5f;
            var p = new GameObject($"Pylon_{i}");
            p.transform.SetParent(_avatarSceneRoot.transform, false);
            p.transform.position = new Vector3(Mathf.Cos(a) * 3.6f, 1.2f, Mathf.Sin(a) * 3.6f);
            p.transform.rotation = Quaternion.Euler(0f, -i * 90f, 0f);

            var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.name = "Body";
            body.transform.SetParent(p.transform, false);
            body.transform.localPosition = Vector3.zero;
            body.transform.localScale = new Vector3(0.35f, 2.2f, 0.35f);
            RemoveCollider(body);
            var br = body.GetComponent<Renderer>();
            if (br != null) br.material = platformMat;

            var cap = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            cap.name = "Cap";
            cap.transform.SetParent(p.transform, false);
            cap.transform.localPosition = new Vector3(0f, 1.2f, 0f);
            cap.transform.localScale = new Vector3(0.4f, 0.4f, 0.4f);
            RemoveCollider(cap);

            var capMat = new Material(Shader.Find("Standard"));
            capMat.color = new Color(0.02f, 0.03f, 0.05f, 1f);
            capMat.EnableKeyword("_EMISSION");
            capMat.SetColor("_EmissionColor", new Color(0.6f, 0.25f, 1.0f, 1f) * 1.8f);
            var cr = cap.GetComponent<Renderer>();
            if (cr != null) cr.material = capMat;
        }

        // Camera framing
        var cam = Camera.main;
        if (cam != null)
        {
            cam.fieldOfView = 40f;
            cam.transform.position = new Vector3(0.9f, 1.55f, -4.2f);
            cam.transform.LookAt(new Vector3(0f, 1.15f, 0f));
        }
    }

    private GameObject CreateKenneyPropHolder(Transform parent, string assetName, Vector3 localPos, Vector3 localEuler, Vector3 holderScale)
    {
        var holder = new GameObject("Kenney_" + assetName);
        holder.transform.SetParent(parent, false);
        holder.transform.localPosition = localPos;
        holder.transform.localRotation = Quaternion.Euler(localEuler);
        holder.transform.localScale = holderScale;
        StartCoroutine(InstantiateKenneyAssetInto(holder.transform, assetName));
        return holder;
    }

    private void ApplyWorldPlan(WorldPlan plan)
    {
        if (plan == null || plan.ground == null || plan.sky == null || plan.fog == null)
        {
            AppendLog("World: invalid plan.");
            return;
        }

        EnsureStarterMeshes();
        EnsureWorldRoot();
        ClearChildren(_worldRoot.transform);

        ApplyWorldRenderSettings(plan);

        // Ground mesh
        var groundGo = new GameObject("Ground");
        groundGo.transform.SetParent(_worldRoot.transform, false);
        var mf = groundGo.AddComponent<MeshFilter>();
        mf.sharedMesh = BuildGroundMesh(plan.ground, plan.seed);
        var mr = groundGo.AddComponent<MeshRenderer>();
        mr.material = CreateGroundMaterial(plan.ground);

        var rng = new System.Random(plan.seed);
        if (plan.objects != null)
        {
            foreach (var o in plan.objects)
            {
                if (o == null) continue;
                var prefab = (o.prefab ?? "").ToLowerInvariant();
                var go = BuildCatalogPrefab(prefab, o, rng);
                go.transform.SetParent(_worldRoot.transform, false);

                var p = ToVector3(o.position, Vector3.zero);
                var r = ToVector3(o.rotation, Vector3.zero);
                var s = ToVector3(o.scale, Vector3.one);

                // Place on ground by default; allow user-specified offset via position.y
                var groundY = SampleGroundHeight(plan.ground, plan.seed, p.x, p.z);
                go.transform.position = new Vector3(p.x, groundY + p.y, p.z);
                go.transform.rotation = Quaternion.Euler(r);
                go.transform.localScale = s;
            }
        }

        RepositionCamera(plan.ground.size);
        _lastWorldPlan = plan;
        EnterWorldView();
    }

    private void EnterAvatarView()
    {
        if (_worldRoot != null) _worldRoot.SetActive(false);
        if (_avatarSceneRoot != null) _avatarSceneRoot.SetActive(true);
        if (_avatarRoot != null) _avatarRoot.SetActive(true);

        // Camera framing for avatar stage
        var cam = Camera.main;
        if (cam != null)
        {
            cam.fieldOfView = 40f;
            cam.transform.position = new Vector3(0.9f, 1.55f, -4.2f);
            cam.transform.LookAt(new Vector3(0f, 1.15f, 0f));
        }
    }

    private void EnterWorldView()
    {
        if (_worldRoot == null)
        {
            AppendLog("World: nothing to view yet. Generate a world first.");
            return;
        }
        if (_avatarSceneRoot != null) _avatarSceneRoot.SetActive(false);
        if (_avatarRoot != null) _avatarRoot.SetActive(false);
        _worldRoot.SetActive(true);
    }

    private void EnsureWorldRoot()
    {
        if (_worldRoot != null) return;
        _worldRoot = new GameObject("OWP_WorldRoot");
        DontDestroyOnLoad(_worldRoot);
    }

    private static void ClearChildren(Transform t)
    {
        for (int i = t.childCount - 1; i >= 0; i--)
        {
            Destroy(t.GetChild(i).gameObject);
        }
    }

    private void ApplyWorldRenderSettings(WorldPlan plan)
    {
        // Skybox (Procedural)
        var skyShader = Shader.Find("Skybox/Procedural");
        if (skyShader != null)
        {
            var sky = new Material(skyShader);
            sky.SetColor("_SkyTint", ParseHex(plan.sky.sky_tint, new Color(0.55f, 0.7f, 1f, 1f)));
            sky.SetColor("_GroundColor", ParseHex(plan.sky.ground_color, new Color(0.3f, 0.3f, 0.35f, 1f)));
            sky.SetFloat("_AtmosphereThickness", Mathf.Clamp(plan.sky.atmosphere_thickness, 0.5f, 4.0f));
            sky.SetFloat("_SunSize", Mathf.Clamp(plan.sky.sun_size, 0.01f, 1.0f));
            RenderSettings.skybox = sky;
        }

        RenderSettings.fog = plan.fog.enabled;
        RenderSettings.fogColor = ParseHex(plan.fog.color, new Color(0.2f, 0.3f, 0.4f, 1f));
        RenderSettings.fogDensity = Mathf.Clamp(plan.fog.density, 0.0f, 0.05f);

        RenderSettings.ambientLight = new Color(0.2f, 0.25f, 0.35f, 1f);
        RenderSettings.ambientIntensity = 1.05f;
    }

    private Material CreateGroundMaterial(WorldGround ground)
    {
        var mat = new Material(Shader.Find("Standard"));
        mat.color = ParseHex(ground.color, new Color(0.22f, 0.25f, 0.24f, 1f));
        if (_starterTexScales != null)
        {
            mat.mainTexture = _starterTexScales;
            var tiling = Mathf.Clamp(ground.size / 8f, 2f, 40f);
            mat.mainTextureScale = new Vector2(tiling, tiling);
        }
        mat.SetFloat("_Glossiness", 0.2f);
        return mat;
    }

    private static Mesh BuildGroundMesh(WorldGround ground, int seed)
    {
        var grid = Mathf.Clamp(ground.grid, 16, 256);
        var size = Mathf.Clamp(ground.size, 20f, 400f);

        var verts = new Vector3[(grid + 1) * (grid + 1)];
        var uvs = new Vector2[verts.Length];
        var tris = new int[grid * grid * 6];

        int vi = 0;
        for (int z = 0; z <= grid; z++)
        {
            float vz = ((float)z / grid - 0.5f) * size;
            for (int x = 0; x <= grid; x++)
            {
                float vx = ((float)x / grid - 0.5f) * size;
                float y = SampleGroundHeight(ground, seed, vx, vz);
                verts[vi] = new Vector3(vx, y, vz);
                uvs[vi] = new Vector2((float)x / grid, (float)z / grid);
                vi++;
            }
        }

        int ti = 0;
        for (int z = 0; z < grid; z++)
        {
            for (int x = 0; x < grid; x++)
            {
                int a = z * (grid + 1) + x;
                int b = a + 1;
                int c = a + (grid + 1);
                int d = c + 1;

                tris[ti++] = a;
                tris[ti++] = c;
                tris[ti++] = b;
                tris[ti++] = b;
                tris[ti++] = c;
                tris[ti++] = d;
            }
        }

        var m = new Mesh();
        m.indexFormat = verts.Length > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
        m.vertices = verts;
        m.uv = uvs;
        m.triangles = tris;
        m.RecalculateNormals();
        m.RecalculateBounds();
        return m;
    }

    private static float SampleGroundHeight(WorldGround ground, int seed, float x, float z)
    {
        var noiseScale = Mathf.Max(0.5f, ground.noise_scale);
        var heightScale = Mathf.Clamp(ground.height_scale, 0f, 40f);

        // Seed acts as an offset so worlds are stable but different.
        float ox = seed * 0.0137f;
        float oz = seed * 0.0211f;
        float n = Mathf.PerlinNoise((x / noiseScale) + ox, (z / noiseScale) + oz);
        // Centered noise [-1..1]
        float centered = (n - 0.5f) * 2f;
        return centered * heightScale;
    }

    private void EnsureGltfCacheRoot()
    {
        if (_gltfCacheRoot != null) return;
        _gltfCacheRoot = new GameObject("OWP_gltf_cache");
        _gltfCacheRoot.SetActive(false);
        DontDestroyOnLoad(_gltfCacheRoot);
    }

    private static string PickKenneyAssetForPrefab(string prefab, System.Random rng)
    {
        // Maps logical prefab ids to a small built-in CC0 asset pack.
        switch ((prefab ?? "").ToLowerInvariant())
        {
            case "alien":
                return "alien";
            case "astronaut":
                return rng.Next(0, 2) == 0 ? "astronautA" : "astronautB";
            case "ambulance":
                return "ambulance";
            case "van":
                return "van";
            case "barrel":
                return rng.Next(0, 2) == 0 ? "barrel" : "barrels";
            case "lamp":
                return rng.Next(0, 2) == 0 ? "lampRoundFloor" : "lampWall";
            case "rock":
            {
                var pick = rng.Next(0, 5);
                if (pick == 0) return "rock";
                if (pick == 1) return "rock_largeA";
                if (pick == 2) return "rock_largeB";
                if (pick == 3) return "rocks_smallA";
                return "rocks_smallB";
            }
            case "crystal":
            {
                var pick = rng.Next(0, 3);
                if (pick == 0) return "rock_crystals";
                if (pick == 1) return "rock_crystalsLargeA";
                return "rock_crystalsLargeB";
            }
            default:
                return null;
        }
    }

    private static float KenneyScaleForAsset(string assetName)
    {
        // The Kenney CC0 models are generally authored at a small unit scale.
        // This multiplier keeps them readable alongside our procedural primitives.
        switch ((assetName ?? "").ToLowerInvariant())
        {
            case "lampwall":
                return 1.4f;
            case "lamproundfloor":
                return 1.6f;
            case "van":
            case "ambulance":
                return 2.4f;
            case "astronauta":
            case "astronautb":
            case "alien":
                return 1.6f;
            default:
                return 2.0f;
        }
    }

    private IEnumerator InstantiateKenneyAssetInto(Transform parent, string assetName)
    {
        EnsureGltfCacheRoot();

        if (string.IsNullOrEmpty(assetName))
        {
            yield break;
        }

        if (!_gltfPrefabCache.TryGetValue(assetName, out var cached) || cached == null)
        {
            var cachedRoot = new GameObject(assetName);
            cachedRoot.transform.SetParent(_gltfCacheRoot.transform, false);

            var path = Path.Combine(Application.streamingAssetsPath, KenneyCcoPack, assetName + ".glb");
            var uri = new Uri(path).AbsoluteUri;

            var gltf = new GltfImport();
            var loadTask = gltf.Load(uri);
            yield return new WaitUntil(() => loadTask.IsCompleted);

            if (loadTask.Status != TaskStatus.RanToCompletion || !loadTask.Result)
            {
                UnityEngine.Debug.LogWarning($"GLB load failed: {assetName} ({uri})");
                Destroy(cachedRoot);
                yield break;
            }

            var instTask = gltf.InstantiateMainSceneAsync(cachedRoot.transform);
            yield return new WaitUntil(() => instTask.IsCompleted);

            if (instTask.Status != TaskStatus.RanToCompletion || !instTask.Result)
            {
                UnityEngine.Debug.LogWarning($"GLB instantiate failed: {assetName}");
                Destroy(cachedRoot);
                yield break;
            }

            cachedRoot.SetActive(false);
            _gltfPrefabCache[assetName] = cachedRoot;
            cached = cachedRoot;
        }

        var instance = Instantiate(cached, parent);
        instance.name = assetName;
        instance.SetActive(true);
        instance.transform.localPosition = Vector3.zero;
        instance.transform.localRotation = Quaternion.identity;
        instance.transform.localScale = Vector3.one * KenneyScaleForAsset(assetName);
    }

    private GameObject BuildCatalogPrefab(string prefab, WorldObject o, System.Random rng)
    {
        var baseColor = ParseHex(o.color, new Color(0.75f, 0.8f, 0.9f, 1f));
        var emissionColor = ParseHex(o.emission_color, baseColor);
        var emissionStrength = Mathf.Max(0f, o.emission_strength);

        var kenneyAsset = PickKenneyAssetForPrefab(prefab, rng);
        if (!string.IsNullOrEmpty(kenneyAsset))
        {
            var root = new GameObject(prefab);
            StartCoroutine(InstantiateKenneyAssetInto(root.transform, kenneyAsset));
            return root;
        }

        switch (prefab)
        {
            case "tower": return BuildTower(baseColor, emissionColor, emissionStrength);
            case "house": return BuildHouse(baseColor, emissionColor, emissionStrength);
            case "ruins": return BuildRuins(baseColor, emissionColor, emissionStrength, rng);
            case "camp": return BuildCamp(baseColor, emissionColor, emissionStrength);
            case "portal": return BuildPortal(baseColor, emissionColor, emissionStrength);
            case "tree": return BuildTree(baseColor, emissionColor, emissionStrength, rng);
            case "rock": return BuildRock(baseColor, emissionColor, emissionStrength, rng);
            case "crystal": return BuildCrystal(baseColor, emissionColor, emissionStrength, rng);
            case "lamp": return BuildLamp(baseColor, emissionColor, emissionStrength);
            default: return BuildFallback(baseColor, emissionColor, emissionStrength);
        }
    }

    private Material CreateWorldMaterial(string prefab, Color baseColor, Color emissionColor, float emissionStrength)
    {
        var mat = new Material(Shader.Find("Standard"));
        mat.color = baseColor;

        var tex = PickStarterTexture(prefab);
        if (tex != null)
        {
            mat.mainTexture = tex;
            mat.mainTextureScale = new Vector2(2f, 2f);
        }

        mat.SetFloat("_Glossiness", 0.3f);
        if (emissionStrength > 0.01f)
        {
            mat.EnableKeyword("_EMISSION");
            mat.SetColor("_EmissionColor", emissionColor * emissionStrength);
        }
        return mat;
    }

    private Texture2D PickStarterTexture(string prefab)
    {
        switch (prefab)
        {
            case "tower": return _starterTexCircuit;
            case "house": return _starterTexStripes;
            case "camp": return _starterTexCloth;
            case "tree": return _starterTexScales;
            case "ruins": return _starterTexScales;
            default: return null;
        }
    }

    private GameObject CreatePrimitivePart(Transform parent, string name, PrimitiveType type, Vector3 localPos, Vector3 localScale, Quaternion localRot, Material mat)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localRotation = localRot;
        go.transform.localScale = localScale;
        RemoveCollider(go);
        var r = go.GetComponent<Renderer>();
        if (r != null && mat != null) r.material = mat;
        return go;
    }

    private GameObject CreateMeshPart(Transform parent, string name, Mesh mesh, Vector3 localPos, Vector3 localScale, Quaternion localRot, Material mat)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localRotation = localRot;
        go.transform.localScale = localScale;
        var mf = go.AddComponent<MeshFilter>();
        mf.sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.material = mat;
        return go;
    }

    private GameObject BuildTower(Color baseColor, Color emissionColor, float emissionStrength)
    {
        var root = new GameObject("tower");
        var mat = CreateWorldMaterial("tower", baseColor, emissionColor, emissionStrength);

        CreatePrimitivePart(root.transform, "Base", PrimitiveType.Cylinder, new Vector3(0f, 1.4f, 0f), new Vector3(1.6f, 2.8f, 1.6f), Quaternion.identity, mat);
        CreatePrimitivePart(root.transform, "Mid", PrimitiveType.Cylinder, new Vector3(0f, 3.9f, 0f), new Vector3(1.3f, 2.2f, 1.3f), Quaternion.identity, mat);
        CreateMeshPart(root.transform, "Roof", _meshCone, new Vector3(0f, 6.2f, 0f), new Vector3(1.6f, 1.3f, 1.6f), Quaternion.identity, mat);

        // Simple emissive windows band when requested
        if (emissionStrength > 0.01f)
        {
            var wMat = CreateWorldMaterial("tower", baseColor * 0.25f, emissionColor, emissionStrength);
            CreatePrimitivePart(root.transform, "Windows", PrimitiveType.Cylinder, new Vector3(0f, 4.6f, 0f), new Vector3(1.33f, 0.12f, 1.33f), Quaternion.identity, wMat);
        }

        return root;
    }

    private GameObject BuildHouse(Color baseColor, Color emissionColor, float emissionStrength)
    {
        var root = new GameObject("house");
        var mat = CreateWorldMaterial("house", baseColor, emissionColor, emissionStrength);
        var roofMat = CreateWorldMaterial("house", baseColor * 0.85f, emissionColor, emissionStrength);

        CreatePrimitivePart(root.transform, "Body", PrimitiveType.Cube, new Vector3(0f, 0.75f, 0f), new Vector3(2.2f, 1.5f, 2.0f), Quaternion.identity, mat);
        CreatePrimitivePart(root.transform, "Roof", PrimitiveType.Cube, new Vector3(0f, 1.75f, 0f), new Vector3(2.4f, 0.6f, 2.2f), Quaternion.Euler(0f, 45f, 0f), roofMat);
        CreatePrimitivePart(root.transform, "Door", PrimitiveType.Cube, new Vector3(0f, 0.45f, 1.02f), new Vector3(0.5f, 0.9f, 0.08f), Quaternion.identity, roofMat);

        return root;
    }

    private GameObject BuildRuins(Color baseColor, Color emissionColor, float emissionStrength, System.Random rng)
    {
        var root = new GameObject("ruins");
        var mat = CreateWorldMaterial("ruins", baseColor, emissionColor, emissionStrength);

        CreatePrimitivePart(root.transform, "PillarA", PrimitiveType.Cube, new Vector3(-1.1f, 1.0f, 0f), new Vector3(0.4f, 2.0f, 0.4f), Quaternion.identity, mat);
        CreatePrimitivePart(root.transform, "PillarB", PrimitiveType.Cube, new Vector3(1.1f, 1.0f, 0f), new Vector3(0.4f, 2.0f, 0.4f), Quaternion.identity, mat);
        CreatePrimitivePart(root.transform, "Lintel", PrimitiveType.Cube, new Vector3(0f, 2.1f, 0f), new Vector3(2.6f, 0.35f, 0.45f), Quaternion.identity, mat);

        for (int i = 0; i < 4; i++)
        {
            float rx = (float)(rng.NextDouble() * 2.2 - 1.1);
            float rz = (float)(rng.NextDouble() * 1.4 - 0.7);
            float s = 0.25f + (float)rng.NextDouble() * 0.25f;
            CreatePrimitivePart(root.transform, "Rubble", PrimitiveType.Sphere, new Vector3(rx, 0.15f, rz), new Vector3(s, s * 0.6f, s), Quaternion.identity, mat);
        }

        return root;
    }

    private GameObject BuildCamp(Color baseColor, Color emissionColor, float emissionStrength)
    {
        var root = new GameObject("camp");
        var tentMat = CreateWorldMaterial("camp", baseColor, emissionColor, 0f);
        var fireMat = CreateWorldMaterial("camp", new Color(0.15f, 0.12f, 0.1f, 1f), new Color(1f, 0.55f, 0.2f, 1f), Mathf.Max(2.5f, emissionStrength));

        CreatePrimitivePart(root.transform, "Tent", PrimitiveType.Cube, new Vector3(-0.7f, 0.55f, 0f), new Vector3(1.4f, 1.1f, 1.4f), Quaternion.Euler(0f, 25f, 0f), tentMat);
        CreatePrimitivePart(root.transform, "TentTop", PrimitiveType.Cube, new Vector3(-0.7f, 1.1f, 0f), new Vector3(1.45f, 0.35f, 1.45f), Quaternion.Euler(0f, 25f, 0f), tentMat);

        CreatePrimitivePart(root.transform, "FireBase", PrimitiveType.Cylinder, new Vector3(0.9f, 0.08f, 0f), new Vector3(0.7f, 0.08f, 0.7f), Quaternion.identity, tentMat);
        CreatePrimitivePart(root.transform, "Fire", PrimitiveType.Sphere, new Vector3(0.9f, 0.25f, 0f), new Vector3(0.35f, 0.35f, 0.35f), Quaternion.identity, fireMat);

        return root;
    }

    private GameObject BuildPortal(Color baseColor, Color emissionColor, float emissionStrength)
    {
        var root = new GameObject("portal");
        var ringMat = CreateWorldMaterial("portal", baseColor * 0.25f, emissionColor, Mathf.Max(2.0f, emissionStrength));

        CreateMeshPart(root.transform, "Ring", _meshTorus, new Vector3(0f, 1.2f, 0f), new Vector3(2.6f, 2.6f, 0.35f), Quaternion.Euler(90f, 0f, 0f), ringMat);
        CreatePrimitivePart(root.transform, "Base", PrimitiveType.Cylinder, new Vector3(0f, 0.25f, 0f), new Vector3(1.2f, 0.25f, 1.2f), Quaternion.identity, CreateWorldMaterial("ruins", baseColor, emissionColor, 0f));

        return root;
    }

    private GameObject BuildTree(Color baseColor, Color emissionColor, float emissionStrength, System.Random rng)
    {
        var root = new GameObject("tree");
        var bark = CreateWorldMaterial("tree", new Color(0.22f, 0.18f, 0.14f, 1f), emissionColor, 0f);
        var leaves = CreateWorldMaterial("tree", baseColor, emissionColor, emissionStrength);

        float h = 1.8f + (float)rng.NextDouble() * 1.2f;
        CreatePrimitivePart(root.transform, "Trunk", PrimitiveType.Cylinder, new Vector3(0f, h * 0.5f, 0f), new Vector3(0.35f, h * 0.5f, 0.35f), Quaternion.identity, bark);
        CreatePrimitivePart(root.transform, "Canopy", PrimitiveType.Sphere, new Vector3(0f, h + 0.35f, 0f), new Vector3(1.4f, 1.2f, 1.4f), Quaternion.identity, leaves);

        return root;
    }

    private GameObject BuildRock(Color baseColor, Color emissionColor, float emissionStrength, System.Random rng)
    {
        var root = new GameObject("rock");
        var mat = CreateWorldMaterial("rock", baseColor * 0.7f, emissionColor, 0f);

        float sx = 0.9f + (float)rng.NextDouble() * 0.6f;
        float sy = 0.55f + (float)rng.NextDouble() * 0.4f;
        float sz = 0.9f + (float)rng.NextDouble() * 0.6f;
        CreatePrimitivePart(root.transform, "Boulder", PrimitiveType.Sphere, new Vector3(0f, sy * 0.5f, 0f), new Vector3(sx, sy, sz), Quaternion.identity, mat);

        return root;
    }

    private GameObject BuildCrystal(Color baseColor, Color emissionColor, float emissionStrength, System.Random rng)
    {
        var root = new GameObject("crystal");
        var mat = CreateWorldMaterial("crystal", baseColor * 0.35f, emissionColor, Mathf.Max(1.5f, emissionStrength));

        float h = 2.2f + (float)rng.NextDouble() * 2.2f;
        CreateMeshPart(root.transform, "Spire", _meshCone, new Vector3(0f, h * 0.5f, 0f), new Vector3(0.9f, h, 0.9f), Quaternion.identity, mat);
        CreatePrimitivePart(root.transform, "Base", PrimitiveType.Cylinder, new Vector3(0f, 0.15f, 0f), new Vector3(0.75f, 0.15f, 0.75f), Quaternion.identity, CreateWorldMaterial("ruins", baseColor, emissionColor, 0f));

        return root;
    }

    private GameObject BuildLamp(Color baseColor, Color emissionColor, float emissionStrength)
    {
        var root = new GameObject("lamp");
        var poleMat = CreateWorldMaterial("lamp", baseColor, emissionColor, 0f);
        var lightMat = CreateWorldMaterial("lamp", baseColor * 0.2f, emissionColor, Mathf.Max(2.5f, emissionStrength));

        CreatePrimitivePart(root.transform, "Pole", PrimitiveType.Cylinder, new Vector3(0f, 1.25f, 0f), new Vector3(0.18f, 1.25f, 0.18f), Quaternion.identity, poleMat);
        CreatePrimitivePart(root.transform, "Lamp", PrimitiveType.Sphere, new Vector3(0f, 2.65f, 0f), new Vector3(0.35f, 0.35f, 0.35f), Quaternion.identity, lightMat);

        return root;
    }

    private GameObject BuildFallback(Color baseColor, Color emissionColor, float emissionStrength)
    {
        var root = new GameObject("object");
        var mat = CreateWorldMaterial("object", baseColor, emissionColor, emissionStrength);
        CreatePrimitivePart(root.transform, "Box", PrimitiveType.Cube, new Vector3(0f, 0.5f, 0f), new Vector3(1f, 1f, 1f), Quaternion.identity, mat);
        return root;
    }

    private void RepositionCamera(float worldSize)
    {
        var cam = Camera.main;
        if (cam == null) return;

        float d = Mathf.Clamp(worldSize, 30f, 250f);
        cam.transform.position = new Vector3(0f, d * 0.22f, -d * 0.52f);
        cam.transform.LookAt(new Vector3(0f, 0.8f, 0f));
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
        _worldsPanel = CreateSciFiPanel(root.transform, "WorldsPanel", new Color(0.03f, 0.05f, 0.12f, 0.92f), new Color(0f, 0.7f, 1f, 0.5f));
        var worldsRt = _worldsPanel.GetComponent<RectTransform>();
        worldsRt.anchorMin = new Vector2(0, 0);
        worldsRt.anchorMax = new Vector2(0.42f, 1);
        worldsRt.offsetMin = new Vector2(16, 16);
        worldsRt.offsetMax = new Vector2(-8, -16);

        var worldsLayout = _worldsPanel.AddComponent<VerticalLayoutGroup>();
        worldsLayout.padding = new RectOffset(10, 10, 10, 10);
        worldsLayout.spacing = 8;
        worldsLayout.childControlWidth = true;
        worldsLayout.childControlHeight = true;
        worldsLayout.childForceExpandWidth = true;
        worldsLayout.childForceExpandHeight = false;

        // Worlds header row
        var worldsHeader = CreateRow(_worldsPanel.transform, "WorldsHeader", 28);
        var worldsTitle = CreateSciFiText(worldsHeader.transform, "WorldsTitle", "Worlds", 16, TextAnchor.MiddleLeft, new Color(0.7f, 0.9f, 1f, 1f));
        AddLayoutElement(worldsTitle.gameObject, preferredWidth: -1, preferredHeight: 28, flexibleWidth: 1);
        _worldsSourceButton = CreateSciFiButton(worldsHeader.transform, "WorldsSource", "Source: Local", 130, 24);
        _worldsSourceLabel = _worldsSourceButton.GetComponentInChildren<Text>();
        _worldsSourceButton.onClick.AddListener(() =>
        {
            SetWorldsSource(!_worldsUseOnChain);
            StartCoroutine(RefreshWorlds());
        });
        _refreshWorldsButton = CreateSciFiButton(worldsHeader.transform, "RefreshWorlds", "Refresh", 100, 24);
        _refreshWorldsButton.onClick.AddListener(() => StartCoroutine(RefreshWorlds()));

        // Worlds create row
        var createRow = CreateRow(_worldsPanel.transform, "WorldsCreate", 30);
        _worldNameInput = CreateSciFiInput(createRow.transform, "WorldName", "World name…", 0, 28, flexibleWidth: 1);
        _createWorldButton = CreateSciFiButton(createRow.transform, "CreateWorld", "Create", 100, 28);
        _createWorldButton.onClick.AddListener(() =>
        {
            var n = (_worldNameInput.text ?? "").Trim();
            if (n.Length == 0) return;
            _worldNameInput.text = "";
            StartCoroutine(CreateWorld(n));
        });

        _selectedWorldLabel = CreateSciFiText(_worldsPanel.transform, "SelectedWorld", "Selected: (none)", 12, TextAnchor.MiddleLeft, new Color(0.7f, 0.9f, 1f, 0.9f));
        AddLayoutElement(_selectedWorldLabel.gameObject, preferredWidth: -1, preferredHeight: 22, flexibleWidth: 1);

        // Worlds list (scroll)
        var worldsScroll = CreateScrollView(_worldsPanel.transform, "WorldsScroll");
        AddLayoutElement(worldsScroll.scrollRect.gameObject, preferredWidth: -1, preferredHeight: -1, flexibleWidth: 1, flexibleHeight: 1);
        _worldsListRoot = worldsScroll.content;

        // View toggle row (avatar stage vs world view)
        var viewRow = CreateRow(_worldsPanel.transform, "WorldViewRow", 28);
        _avatarViewButton = CreateSciFiButton(viewRow.transform, "AvatarView", "Avatar View", 120, 24);
        _avatarViewButton.onClick.AddListener(() => EnterAvatarView());
        _worldViewButton = CreateSciFiButton(viewRow.transform, "WorldView", "World View", 120, 24);
        _worldViewButton.onClick.AddListener(() => EnterWorldView());

        // World prompt (LLM → plan → assembled scene)
        var worldPromptRow = CreateRow(_worldsPanel.transform, "WorldPromptRow", 32);
        _worldPromptInput = CreateSciFiInput(worldPromptRow.transform, "WorldPrompt", "Describe a world scene…", 0, 28, flexibleWidth: 1);
        _generateWorldButton = CreateSciFiButton(worldPromptRow.transform, "GenerateWorld", "Generate", 110, 28);
        _generateWorldButton.onClick.AddListener(() =>
        {
            var p = (_worldPromptInput != null ? _worldPromptInput.text : "") ?? "";
            p = p.Trim();
            if (p.Length == 0) return;
            _worldPromptInput.text = "";
            AppendLog($"You: {p}");
            StartCoroutine(GenerateWorldPlan(p));
        });

        _hostConnectButton = CreateSciFiButton(_worldsPanel.transform, "HostConnect", "Host + Connect", 0, 30, flexibleWidth: 1);
        _hostConnectButton.onClick.AddListener(() => StartCoroutine(HostAndConnectSelectedWorld()));

        // Companion panel (right)
        _chatPanel = CreateSciFiPanel(root.transform, "CompanionPanel", new Color(0.03f, 0.05f, 0.12f, 0.92f), new Color(0f, 0.6f, 1f, 0.4f));
        var chatRt = _chatPanel.GetComponent<RectTransform>();
        chatRt.anchorMin = new Vector2(0.42f, 0);
        chatRt.anchorMax = new Vector2(1, 1);
        chatRt.offsetMin = new Vector2(8, 16);
        chatRt.offsetMax = new Vector2(-16, -16);

        var chatLayout = _chatPanel.AddComponent<VerticalLayoutGroup>();
        chatLayout.padding = new RectOffset(10, 10, 10, 10);
        chatLayout.spacing = 8;
        chatLayout.childControlWidth = true;
        chatLayout.childControlHeight = true;
        chatLayout.childForceExpandWidth = true;
        chatLayout.childForceExpandHeight = false;

        var chatHeader = CreateRow(_chatPanel.transform, "CompanionHeader", 28);
        var companionTitle = CreateSciFiText(chatHeader.transform, "CompanionTitle", "Companion", 16, TextAnchor.MiddleLeft, new Color(0.7f, 0.9f, 1f, 1f));
        AddLayoutElement(companionTitle.gameObject, preferredWidth: -1, preferredHeight: 28, flexibleWidth: 1);
        _providerButton = CreateSciFiButton(chatHeader.transform, "ProviderButton", "Provider: (loading…)", 150, 24);
        _providerButtonLabel = _providerButton.GetComponentInChildren<Text>();
        _providerButton.onClick.AddListener(() => StartCoroutine(RefreshAssistantStatus(true)));

        var chatScroll = CreateSciFiChatScroll(_chatPanel.transform, "ChatScroll");
        AddLayoutElement(chatScroll.scrollRect.gameObject, preferredWidth: -1, preferredHeight: -1, flexibleWidth: 1, flexibleHeight: 1);
        _chatLog = chatScroll.text;
        _chatScrollRect = chatScroll.scrollRect;
        _chatLog.alignment = TextAnchor.UpperLeft;
        _chatLog.supportRichText = true;

        var inputRow = CreateRow(_chatPanel.transform, "ChatInputRow", 32);
        _chatInput = CreateSciFiInput(inputRow.transform, "ChatInput", "Describe your avatar…", 0, 28, flexibleWidth: 1);
        _sendButton = CreateSciFiButton(inputRow.transform, "SendButton", "Send", 90, 28);
        _sendButton.onClick.AddListener(() =>
        {
            var text = _chatInput.text ?? "";
            if (text.Trim().Length == 0) return;
            _chatInput.text = "";
            AppendLog($"You: {text}");
            StartCoroutine(SendChat(text));
        });

        // Orb toggle (top-right)
        _orbButton = CreateSciFiOrbButton(canvasGo.transform, "OrbButton", "◎", new Vector2(-34, -34), new Vector2(44, 44));
        _orbButton.onClick.AddListener(() =>
        {
            _chatPanel.SetActive(!_chatPanel.activeSelf);
        });

        // Worlds toggle (top-left)
        _worldsToggleButton = CreateSciFiCornerOrbButton(canvasGo.transform, "WorldsToggle", "☰", new Vector2(0, 1), new Vector2(34, -34), new Vector2(44, 44));
        _worldsToggleButton.onClick.AddListener(() =>
        {
            _worldsPanel.SetActive(!_worldsPanel.activeSelf);
        });

        // Provider panel (center)
        _providerPanel = CreateSciFiPanel(canvasGo.transform, "ProviderPanel", new Color(0.02f, 0.04f, 0.1f, 0.96f), new Color(0f, 0.8f, 1f, 0.6f));
        var prt = _providerPanel.GetComponent<RectTransform>();
        prt.anchorMin = new Vector2(0.5f, 0.5f);
        prt.anchorMax = new Vector2(0.5f, 0.5f);
        prt.pivot = new Vector2(0.5f, 0.5f);
        prt.sizeDelta = new Vector2(520, 230);
        prt.anchoredPosition = Vector2.zero;

        var providerTitle = CreateSciFiTextPositioned(_providerPanel.transform, "ProviderTitle", "Choose provider", new Vector2(0, 86), new Vector2(480, 26), new Color(0.7f, 0.95f, 1f, 1f));
        providerTitle.alignment = TextAnchor.MiddleCenter;

        _useCodexButton = CreateSciFiButtonPositioned(_providerPanel.transform, "UseCodex", "Use Codex", new Vector2(-120, 44), new Vector2(200, 30));
        _useClaudeButton = CreateSciFiButtonPositioned(_providerPanel.transform, "UseClaude", "Use Claude", new Vector2(120, 44), new Vector2(200, 30));

        _useCodexButton.onClick.AddListener(() => StartCoroutine(SetProvider("codex")));
        _useClaudeButton.onClick.AddListener(() => StartCoroutine(SetProvider("claude")));

        var settingsTitle = CreateSciFiTextPositioned(_providerPanel.transform, "SettingsTitle", "Model + Reasoning", new Vector2(0, 8), new Vector2(480, 22), new Color(0.6f, 0.9f, 1f, 0.9f));
        settingsTitle.alignment = TextAnchor.MiddleCenter;

        _codexModelButton = CreateSciFiButtonPositioned(_providerPanel.transform, "CodexModel", "Codex model: default", new Vector2(-120, -26), new Vector2(200, 26));
        _codexModelLabel = _codexModelButton.GetComponentInChildren<Text>();
        _codexModelButton.onClick.AddListener(() =>
        {
            _codexModel = CycleOption(_codexModel, CodexModelOptions);
            UpdateAssistantSettingsUi();
            StartCoroutine(SaveAssistantConfig());
        });

        _codexEffortButton = CreateSciFiButtonPositioned(_providerPanel.transform, "CodexEffort", "Effort: medium", new Vector2(120, -26), new Vector2(200, 26));
        _codexEffortLabel = _codexEffortButton.GetComponentInChildren<Text>();
        _codexEffortButton.onClick.AddListener(() =>
        {
            _codexEffort = CycleOption(_codexEffort, CodexEffortOptions);
            UpdateAssistantSettingsUi();
            StartCoroutine(SaveAssistantConfig());
        });

	        _claudeModelButton = CreateSciFiButtonPositioned(_providerPanel.transform, "ClaudeModel", "Claude model: default", new Vector2(0, -62), new Vector2(420, 26));
	        _claudeModelLabel = _claudeModelButton.GetComponentInChildren<Text>();
	        _claudeModelButton.onClick.AddListener(() =>
	        {
	            _claudeModel = CycleOption(_claudeModel, ClaudeModelOptions);
	            UpdateAssistantSettingsUi();
	            StartCoroutine(SaveAssistantConfig());
	        });

	        _avatarMeshButton = CreateSciFiButtonPositioned(_providerPanel.transform, "AvatarMesh", "Avatar mesh: on", new Vector2(0, -96), new Vector2(420, 26));
	        _avatarMeshLabel = _avatarMeshButton.GetComponentInChildren<Text>();
	        _avatarMeshButton.onClick.AddListener(() =>
	        {
	            _avatarMeshEnabled = !_avatarMeshEnabled;
	            UpdateAssistantSettingsUi();
	            StartCoroutine(SaveAssistantConfig());
	        });

	        _providerPanel.SetActive(false);

        SetWorldsSource(false);

        // Default closed: user toggles panels via the corner buttons.
        _worldsPanel.SetActive(false);
        _chatPanel.SetActive(false);
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

    // ========== SCI-FI UI COMPONENTS ==========

    private static GameObject CreateSciFiPanel(Transform parent, string name, Color bgColor, Color borderColor)
    {
        var panel = CreatePanel(parent, name, bgColor);

        // Add glowing border outline
        var borderObj = new GameObject($"{name}_Border");
        borderObj.transform.SetParent(panel.transform, false);
        var borderImg = borderObj.AddComponent<Image>();
        borderImg.color = borderColor;

        var borderRt = borderObj.GetComponent<RectTransform>();
        borderRt.anchorMin = Vector2.zero;
        borderRt.anchorMax = Vector2.one;
        borderRt.offsetMin = Vector2.zero;
        borderRt.offsetMax = Vector2.zero;

        // Create inner panel to simulate border (outer border effect)
        var innerObj = new GameObject($"{name}_Inner");
        innerObj.transform.SetParent(panel.transform, false);
        var innerImg = innerObj.AddComponent<Image>();
        innerImg.color = bgColor;

        var innerRt = innerObj.GetComponent<RectTransform>();
        innerRt.anchorMin = Vector2.zero;
        innerRt.anchorMax = Vector2.one;
        innerRt.offsetMin = new Vector2(2, 2);
        innerRt.offsetMax = new Vector2(-2, -2);

        // Add scanline overlay for holographic effect
        var scanlineObj = new GameObject($"{name}_Scanlines");
        scanlineObj.transform.SetParent(panel.transform, false);
        var scanlineImg = scanlineObj.AddComponent<Image>();
        scanlineImg.color = new Color(borderColor.r, borderColor.g, borderColor.b, 0.05f);

        var scanlineRt = scanlineObj.GetComponent<RectTransform>();
        scanlineRt.anchorMin = Vector2.zero;
        scanlineRt.anchorMax = Vector2.one;
        scanlineRt.offsetMin = Vector2.zero;
        scanlineRt.offsetMax = Vector2.zero;

        return panel;
    }

    private static Text CreateSciFiText(Transform parent, string name, string value, int fontSize, TextAnchor align, Color color)
    {
        var text = CreateTextLayout(parent, name, value, fontSize, align);
        text.color = color;
        text.fontStyle = FontStyle.Bold;
        return text;
    }

    private static Text CreateSciFiTextPositioned(Transform parent, string name, string value, Vector2 anchoredPos, Vector2 size, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = size;

        var text = go.AddComponent<Text>();
        text.font = GetDefaultFont();
        text.text = value;
        text.fontSize = 14;
        text.alignment = TextAnchor.MiddleCenter;
        text.color = color;
        text.fontStyle = FontStyle.Bold;

        return text;
    }

    private static Button CreateSciFiButton(Transform parent, string name, string label, float width, float height, float flexibleWidth = 0)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);

        var bg = go.AddComponent<Image>();
        bg.color = new Color(0.08f, 0.15f, 0.2f, 0.85f);

        var borderObj = new GameObject($"{name}_Border");
        borderObj.transform.SetParent(go.transform, false);
        var borderImg = borderObj.AddComponent<Image>();
        borderImg.color = new Color(0f, 0.9f, 1f, 0.7f);

        var borderRt = borderObj.GetComponent<RectTransform>();
        borderRt.anchorMin = Vector2.zero;
        borderRt.anchorMax = Vector2.one;
        borderRt.offsetMin = Vector2.zero;
        borderRt.offsetMax = Vector2.zero;

        var innerObj = new GameObject($"{name}_Inner");
        innerObj.transform.SetParent(go.transform, false);
        var innerImg = innerObj.AddComponent<Image>();
        innerImg.color = new Color(0.08f, 0.15f, 0.2f, 0.95f);

        var innerRt = innerObj.GetComponent<RectTransform>();
        innerRt.anchorMin = Vector2.zero;
        innerRt.anchorMax = Vector2.one;
        innerRt.offsetMin = new Vector2(1.5f, 1.5f);
        innerRt.offsetMax = new Vector2(-1.5f, -1.5f);

        var btn = go.AddComponent<Button>();

        var text = CreateTextLayout(innerObj.transform, $"{name}_Text", label, 13, TextAnchor.MiddleCenter);
        text.color = new Color(0.5f, 1f, 1f, 1f);
        text.fontStyle = FontStyle.Bold;

        var trt = text.GetComponent<RectTransform>();
        trt.anchorMin = Vector2.zero;
        trt.anchorMax = Vector2.one;
        trt.offsetMin = new Vector2(8, 4);
        trt.offsetMax = new Vector2(-8, -4);

        var le = go.AddComponent<LayoutElement>();
        if (width > 0) le.preferredWidth = width;
        if (height > 0) le.preferredHeight = height;
        if (flexibleWidth > 0) le.flexibleWidth = flexibleWidth;

        return btn;
    }

    private static Button CreateSciFiButtonPositioned(Transform parent, string name, string label, Vector2 anchoredPos, Vector2 size)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);

        var bg = go.AddComponent<Image>();
        bg.color = new Color(0.08f, 0.15f, 0.2f, 0.85f);

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = size;

        var borderObj = new GameObject($"{name}_Border");
        borderObj.transform.SetParent(go.transform, false);
        var borderImg = borderObj.AddComponent<Image>();
        borderImg.color = new Color(0f, 1f, 0.6f, 0.7f);

        var borderRt = borderObj.GetComponent<RectTransform>();
        borderRt.anchorMin = Vector2.zero;
        borderRt.anchorMax = Vector2.one;
        borderRt.offsetMin = Vector2.zero;
        borderRt.offsetMax = Vector2.zero;

        var innerObj = new GameObject($"{name}_Inner");
        innerObj.transform.SetParent(go.transform, false);
        var innerImg = innerObj.AddComponent<Image>();
        innerImg.color = new Color(0.08f, 0.15f, 0.2f, 0.95f);

        var innerRt = innerObj.GetComponent<RectTransform>();
        innerRt.anchorMin = Vector2.zero;
        innerRt.anchorMax = Vector2.one;
        innerRt.offsetMin = new Vector2(1.5f, 1.5f);
        innerRt.offsetMax = new Vector2(-1.5f, -1.5f);

        var btn = go.AddComponent<Button>();

        var textObj = new GameObject($"{name}_Text");
        textObj.transform.SetParent(innerObj.transform, false);

        var text = textObj.AddComponent<Text>();
        text.font = GetDefaultFont();
        text.text = label;
        text.fontSize = 14;
        text.alignment = TextAnchor.MiddleCenter;
        text.color = new Color(0.5f, 1f, 0.9f, 1f);
        text.fontStyle = FontStyle.Bold;

        var trt = text.GetComponent<RectTransform>();
        trt.anchorMin = Vector2.zero;
        trt.anchorMax = Vector2.one;
        trt.offsetMin = new Vector2(8, 4);
        trt.offsetMax = new Vector2(-8, -4);

        return btn;
    }

    private static Button CreateSciFiOrbButton(Transform parent, string name, string label, Vector2 anchoredPos, Vector2 size)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);

        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(1, 1);
        rt.anchorMax = new Vector2(1, 1);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = size;

        var bg = go.AddComponent<Image>();
        bg.color = new Color(0.5f, 0f, 1f, 0.6f);

        var btn = go.AddComponent<Button>();

        var textObj = new GameObject($"{name}_Text");
        textObj.transform.SetParent(go.transform, false);

        var text = textObj.AddComponent<Text>();
        text.font = GetDefaultFont();
        text.text = label;
        text.fontSize = 32;
        text.alignment = TextAnchor.MiddleCenter;
        text.color = new Color(1f, 1f, 1f, 1f);
        text.fontStyle = FontStyle.Bold;

        var trt = text.GetComponent<RectTransform>();
        trt.anchorMin = Vector2.zero;
        trt.anchorMax = Vector2.one;
        trt.offsetMin = Vector2.zero;
        trt.offsetMax = Vector2.zero;

        return btn;
    }

    private static Button CreateSciFiCornerOrbButton(Transform parent, string name, string label, Vector2 anchor, Vector2 anchoredPos, Vector2 size)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);

        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = anchor;
        rt.anchorMax = anchor;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = size;

        var bg = go.AddComponent<Image>();
        bg.color = new Color(0f, 0.75f, 1f, 0.35f);

        var btn = go.AddComponent<Button>();

        var textObj = new GameObject($"{name}_Text");
        textObj.transform.SetParent(go.transform, false);

        var text = textObj.AddComponent<Text>();
        text.font = GetDefaultFont();
        text.text = label;
        text.fontSize = 28;
        text.alignment = TextAnchor.MiddleCenter;
        text.color = new Color(0.85f, 1f, 1f, 1f);
        text.fontStyle = FontStyle.Bold;

        var trt = text.GetComponent<RectTransform>();
        trt.anchorMin = Vector2.zero;
        trt.anchorMax = Vector2.one;
        trt.offsetMin = Vector2.zero;
        trt.offsetMax = Vector2.zero;

        return btn;
    }

    private static InputField CreateSciFiInput(Transform parent, string name, string placeholderText, float width, float height, float flexibleWidth = 0)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);

        var borderImg = go.AddComponent<Image>();
        borderImg.color = new Color(0f, 0.8f, 1f, 0.5f);

        var innerObj = new GameObject($"{name}_Inner");
        innerObj.transform.SetParent(go.transform, false);
        var innerImg = innerObj.AddComponent<Image>();
        innerImg.color = new Color(0.05f, 0.1f, 0.15f, 0.8f);

        var innerRt = innerObj.GetComponent<RectTransform>();
        innerRt.anchorMin = Vector2.zero;
        innerRt.anchorMax = Vector2.one;
        innerRt.offsetMin = new Vector2(1.5f, 1.5f);
        innerRt.offsetMax = new Vector2(-1.5f, -1.5f);

        var input = go.AddComponent<InputField>();
        var le = go.AddComponent<LayoutElement>();
        if (width > 0) le.preferredWidth = width;
        if (height > 0) le.preferredHeight = height;
        if (flexibleWidth > 0) le.flexibleWidth = flexibleWidth;

        var placeholder = CreateTextLayout(innerObj.transform, "Placeholder", placeholderText, 13, TextAnchor.MiddleLeft);
        placeholder.color = new Color(0.5f, 0.8f, 1f, 0.4f);
        placeholder.fontStyle = FontStyle.Italic;

        var text = CreateTextLayout(innerObj.transform, "Text", "", 13, TextAnchor.MiddleLeft);
        text.color = new Color(0.7f, 1f, 1f, 1f);
        text.fontStyle = FontStyle.Bold;
        text.supportRichText = false;

        var prt = placeholder.GetComponent<RectTransform>();
        prt.anchorMin = Vector2.zero;
        prt.anchorMax = Vector2.one;
        prt.offsetMin = new Vector2(12, 6);
        prt.offsetMax = new Vector2(-12, -6);

        var trt = text.GetComponent<RectTransform>();
        trt.anchorMin = Vector2.zero;
        trt.anchorMax = Vector2.one;
        trt.offsetMin = new Vector2(12, 6);
        trt.offsetMax = new Vector2(-12, -6);

        input.textComponent = text;
        input.placeholder = placeholder;
        input.lineType = InputField.LineType.SingleLine;
        input.characterLimit = 256;

        return input;
    }

    private static ScrollView CreateSciFiScrollView(Transform parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);

        var bg = go.AddComponent<Image>();
        bg.color = new Color(0, 0.05f, 0.1f, 0.4f);

        var scrollRect = go.AddComponent<ScrollRect>();
        var mask = go.AddComponent<RectMask2D>();

        var viewport = new GameObject("Viewport");
        viewport.transform.SetParent(go.transform, false);
        var viewportRt = viewport.AddComponent<RectTransform>();
        viewportRt.anchorMin = Vector2.zero;
        viewportRt.anchorMax = Vector2.one;
        viewportRt.offsetMin = new Vector2(4, 4);
        viewportRt.offsetMax = new Vector2(-4, -4);
        viewport.AddComponent<RectMask2D>();

        var content = new GameObject("Content");
        content.transform.SetParent(viewport.transform, false);
        var contentRt = content.AddComponent<RectTransform>();
        contentRt.anchorMin = new Vector2(0, 1);
        contentRt.anchorMax = new Vector2(1, 1);
        contentRt.pivot = new Vector2(0.5f, 1);
        contentRt.offsetMin = Vector2.zero;
        contentRt.offsetMax = Vector2.zero;

        var vlg = content.AddComponent<VerticalLayoutGroup>();
        vlg.childControlWidth = true;
        vlg.childControlHeight = true;
        vlg.childForceExpandWidth = true;
        vlg.childForceExpandHeight = false;
        vlg.spacing = 6;

        var csf = content.AddComponent<ContentSizeFitter>();
        csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        scrollRect.content = contentRt;
        scrollRect.viewport = viewportRt;
        scrollRect.horizontal = false;
        scrollRect.vertical = true;
        scrollRect.movementType = ScrollRect.MovementType.Clamped;
        scrollRect.scrollSensitivity = 25;

        return new ScrollView { scrollRect = scrollRect, content = contentRt };
    }

    private struct ChatScrollView
    {
        public ScrollRect scrollRect;
        public Text text;
    }

    private static ChatScrollView CreateSciFiChatScroll(Transform parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);

        var bg = go.AddComponent<Image>();
        bg.color = new Color(0.1f, 0f, 0.1f, 0.4f);

        var scrollRect = go.AddComponent<ScrollRect>();
        var mask = go.AddComponent<RectMask2D>();

        var viewport = new GameObject("Viewport");
        viewport.transform.SetParent(go.transform, false);
        var viewportRt = viewport.AddComponent<RectTransform>();
        viewportRt.anchorMin = Vector2.zero;
        viewportRt.anchorMax = Vector2.one;
        viewportRt.offsetMin = new Vector2(8, 8);
        viewportRt.offsetMax = new Vector2(-8, -8);
        viewport.AddComponent<RectMask2D>();

        var content = new GameObject("Content");
        content.transform.SetParent(viewport.transform, false);
        var contentRt = content.AddComponent<RectTransform>();
        contentRt.anchorMin = new Vector2(0, 0);
        contentRt.anchorMax = new Vector2(1, 1);
        contentRt.pivot = new Vector2(0, 0);
        contentRt.offsetMin = Vector2.zero;
        contentRt.offsetMax = Vector2.zero;

        var text = content.AddComponent<Text>();
        text.font = GetDefaultFont();
        text.fontSize = 13;
        text.color = new Color(0.9f, 0.7f, 1f, 1f);
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;

        scrollRect.content = contentRt;
        scrollRect.viewport = viewportRt;
        scrollRect.horizontal = false;
        scrollRect.vertical = true;
        scrollRect.movementType = ScrollRect.MovementType.Clamped;

        return new ChatScrollView { scrollRect = scrollRect, text = text };
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
		    private class AssistantConfigResponse
		    {
		        public string provider;
		        public string codex_model;
		        public string codex_reasoning_effort;
		        public string claude_model;
		        public bool avatar_mesh_enabled;
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
    private class WorldPlanResponse
    {
        public WorldPlan plan;
    }

    [Serializable]
    private class WorldPlan
    {
        public string version;
        public string name;
        public int seed;
        public string[] biome_tags;
        public WorldGround ground;
        public WorldSky sky;
        public WorldFog fog;
        public WorldObject[] objects;
    }

    [Serializable]
    private class WorldGround
    {
        public float size;
        public int grid;
        public float height_scale;
        public float noise_scale;
        public string color;
    }

    [Serializable]
    private class WorldSky
    {
        public string sky_tint;
        public string ground_color;
        public float atmosphere_thickness;
        public float sun_size;
    }

    [Serializable]
    private class WorldFog
    {
        public bool enabled;
        public string color;
        public float density;
    }

    [Serializable]
    private class WorldObject
    {
        public string id;
        public string prefab;
        public float[] position;
        public float[] rotation;
        public float[] scale;
        public string color;
        public string emission_color;
        public float emission_strength;
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
		        public AvatarPart[] parts;
		        public AvatarMesh mesh;
		    }

		    [Serializable]
		    private class AvatarMesh
		    {
		        public string format;
		        public string uri;
		        public string sha256;
		        public AvatarMeshPart[] parts;
		    }

		    [Serializable]
		    private class AvatarMeshPart
		    {
		        public string id;
		        public string uri;
		        public string sha256;
		        public string material;
		    }

	    [Serializable]
	    private class AvatarPart
	    {
	        public string id;
	        public string attach;
	        public string primitive;
	        public float[] position;
	        public float[] rotation;
	        public float[] scale;
	        public string color;
	        public string emission_color;
	        public float emission_strength;
	    }
}
