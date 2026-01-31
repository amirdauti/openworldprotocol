use anyhow::{Context, Result};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::path::Path;
use std::path::PathBuf;
use std::time::Duration;
use tokio::io::AsyncWriteExt;
use tokio::process::Command;
use tokio::time::timeout;

use owp_protocol::AvatarSpecV1;

use crate::avatar as avatar_mod;
use crate::storage::WorldStore;

#[derive(Debug, Clone, Copy, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum AssistantProviderId {
    Codex,
    Claude,
}

impl AssistantProviderId {
    pub fn as_str(self) -> &'static str {
        match self {
            AssistantProviderId::Codex => "codex",
            AssistantProviderId::Claude => "claude",
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AssistantConfig {
    #[serde(default)]
    pub provider: Option<AssistantProviderId>,
    /// Optional Codex model override (e.g. "gpt-4.1"). None uses Codex CLI defaults/config.
    #[serde(default)]
    pub codex_model: Option<String>,
    /// One of: low, medium, high, very_high. None uses Codex CLI defaults/config.
    #[serde(default)]
    pub codex_reasoning_effort: Option<String>,
    /// Optional Claude model override (e.g. "haiku", "sonnet", "opus"). None uses Claude defaults.
    #[serde(default)]
    pub claude_model: Option<String>,
    /// When enabled, generate an OpenSCAD→STL avatar mesh on each chat update (host-only).
    #[serde(default)]
    pub avatar_mesh_enabled: bool,
}

impl Default for AssistantConfig {
    fn default() -> Self {
        Self {
            provider: None,
            codex_model: None,
            codex_reasoning_effort: None,
            claude_model: None,
            avatar_mesh_enabled: true,
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ProviderStatus {
    pub id: String,
    pub installed: bool,
    #[serde(default)]
    pub note: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AssistantStatus {
    #[serde(default)]
    pub provider: Option<String>,
    pub providers: Vec<ProviderStatus>,
}

pub fn load_config(store: &WorldStore) -> Result<AssistantConfig> {
    let path = store.config_path();
    if !path.exists() {
        return Ok(AssistantConfig::default());
    }
    let data = std::fs::read_to_string(&path).with_context(|| format!("read {path:?}"))?;
    let cfg: AssistantConfig = serde_json::from_str(&data).context("parse assistant config")?;
    Ok(cfg)
}

pub fn save_config(store: &WorldStore, cfg: &AssistantConfig) -> Result<()> {
    let path = store.config_path();
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent).with_context(|| format!("create {parent:?}"))?;
    }
    let json = serde_json::to_string_pretty(cfg).context("serialize assistant config")?;
    std::fs::write(&path, format!("{json}\n")).with_context(|| format!("write {path:?}"))?;
    Ok(())
}

pub async fn status(store: &WorldStore) -> Result<AssistantStatus> {
    let cfg = load_config(store)?;
    let provider = cfg.provider.map(|p| p.as_str().to_string());

    let codex = program_exists("codex").await;
    let claude = program_exists("claude").await;

    Ok(AssistantStatus {
        provider,
        providers: vec![
            ProviderStatus {
                id: "codex".to_string(),
                installed: codex,
                note: None,
            },
            ProviderStatus {
                id: "claude".to_string(),
                installed: claude,
                note: None,
            },
        ],
    })
}

async fn program_exists(program: &str) -> bool {
    let mut cmd = Command::new(program);
    cmd.arg("--version");
    cmd.stdin(std::process::Stdio::null());
    cmd.stdout(std::process::Stdio::null());
    cmd.stderr(std::process::Stdio::null());
    timeout(Duration::from_secs(2), cmd.status())
        .await
        .ok()
        .and_then(|r| r.ok())
        .is_some()
}

pub async fn run_codex_structured(
    prompt: &str,
    schema_path: &Path,
    output_path: &Path,
    cwd: Option<&Path>,
    model: Option<&str>,
    reasoning_effort: Option<&str>,
) -> Result<()> {
    let mut cmd = Command::new("codex");
    cmd.arg("exec");
    if let Some(model) = model {
        if !model.trim().is_empty() {
            cmd.arg("--model").arg(model.trim());
        }
    }
    if let Some(effort) = reasoning_effort {
        let effort = effort.trim();
        if !effort.is_empty() {
            // Codex supports config overrides via `-c key=value` where value is parsed as TOML.
            // Map very_high -> xhigh (Codex uses "xhigh" per docs/changelog).
            let mapped = match effort {
                "low" => "low",
                "medium" => "medium",
                "high" => "high",
                "very_high" => "xhigh",
                "xhigh" => "xhigh",
                _ => effort,
            };
            cmd.arg("-c")
                .arg(format!("model_reasoning_effort=\"{mapped}\""));
        }
    }
    cmd.arg("--sandbox").arg("read-only");
    cmd.arg("--skip-git-repo-check");
    cmd.arg("--output-schema").arg(schema_path);
    cmd.arg("--output-last-message").arg(output_path);
    if let Some(cwd) = cwd {
        cmd.arg("-C").arg(cwd);
    }
    cmd.arg("-");
    cmd.stdin(std::process::Stdio::piped());
    cmd.stdout(std::process::Stdio::null());
    cmd.stderr(std::process::Stdio::piped());

    let mut child = cmd.spawn().context("spawn codex")?;
    if let Some(mut stdin) = child.stdin.take() {
        stdin
            .write_all(prompt.as_bytes())
            .await
            .context("write codex stdin")?;
    }

    let status = timeout(Duration::from_secs(120), child.wait_with_output())
        .await
        .context("codex timeout")?
        .context("wait codex")?;

    if !status.status.success() {
        let err = String::from_utf8_lossy(&status.stderr);
        anyhow::bail!("codex failed: {err}");
    }
    Ok(())
}

pub async fn run_claude_structured(
    prompt: &str,
    schema: &str,
    model: Option<&str>,
) -> Result<String> {
    let mut cmd = Command::new("claude");
    cmd.arg("--print");
    cmd.arg("--output-format").arg("json");
    cmd.arg("--json-schema").arg(schema);
    if let Some(model) = model {
        if !model.trim().is_empty() {
            cmd.arg("--model").arg(model.trim());
        }
    }
    cmd.arg(prompt);
    cmd.stdin(std::process::Stdio::null());
    cmd.stdout(std::process::Stdio::piped());
    cmd.stderr(std::process::Stdio::piped());

    let out = timeout(Duration::from_secs(120), cmd.output())
        .await
        .context("claude timeout")?
        .context("run claude")?;

    if !out.status.success() {
        let err = String::from_utf8_lossy(&out.stderr);
        anyhow::bail!("claude failed: {err}");
    }
    Ok(String::from_utf8_lossy(&out.stdout).to_string())
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
struct CompanionTurn {
    role: String, // "user" | "assistant"
    content: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CompanionChatResponse {
    pub reply: String,
    #[serde(default)]
    pub avatar: Option<AvatarSpecV1>,
}

fn companion_history_path(store: &WorldStore, profile_id: &str) -> PathBuf {
    store
        .profiles_root()
        .join(profile_id)
        .join("companion_history.json")
}

fn load_companion_history(store: &WorldStore, profile_id: &str) -> Result<Vec<CompanionTurn>> {
    let path = companion_history_path(store, profile_id);
    if !path.exists() {
        return Ok(Vec::new());
    }
    let data = std::fs::read_to_string(&path).with_context(|| format!("read {path:?}"))?;
    let turns: Vec<CompanionTurn> =
        serde_json::from_str(&data).context("parse companion history")?;
    Ok(turns)
}

fn save_companion_history(
    store: &WorldStore,
    profile_id: &str,
    turns: &[CompanionTurn],
) -> Result<()> {
    let path = companion_history_path(store, profile_id);
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent).with_context(|| format!("create {parent:?}"))?;
    }
    let json = serde_json::to_string_pretty(turns).context("serialize companion history")?;
    std::fs::write(&path, format!("{json}\n")).with_context(|| format!("write {path:?}"))?;
    Ok(())
}

fn extract_json_object(text: &str) -> Result<String> {
    let start = text
        .find('{')
        .ok_or_else(|| anyhow::anyhow!("no '{{' found in text"))?;

    let mut depth = 0usize;
    let mut in_string = false;
    let mut escape = false;

    for (i, ch) in text[start..].char_indices() {
        let c = ch;
        if in_string {
            if escape {
                escape = false;
                continue;
            }
            match c {
                '\\' => escape = true,
                '"' => in_string = false,
                _ => {}
            }
            continue;
        }

        match c {
            '"' => in_string = true,
            '{' => depth += 1,
            '}' => {
                depth = depth.saturating_sub(1);
                if depth == 0 {
                    let end = start + i + 1;
                    return Ok(text[start..end].to_string());
                }
            }
            _ => {}
        }
    }

    anyhow::bail!("unterminated json object");
}

fn companion_schema_json() -> String {
    // Avatar schema is inlined (no $ref) to keep Codex schema support simple.
    r#"{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "type": "object",
  "additionalProperties": false,
  "required": ["reply","avatar"],
  "properties": {
    "reply": { "type": "string", "minLength": 1, "maxLength": 600 },
    "avatar": {
      "anyOf": [
        { "type": "null" },
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["version","name","primary_color","secondary_color","height","tags","parts"],
          "properties": {
            "version": { "type": "string" },
            "name": { "type": "string", "minLength": 1, "maxLength": 32 },
            "primary_color": { "type": "string", "pattern": "^#[0-9A-Fa-f]{6}$" },
            "secondary_color": { "type": "string", "pattern": "^#[0-9A-Fa-f]{6}$" },
            "height": { "type": "number", "minimum": 0.5, "maximum": 2.0 },
            "tags": { "type": "array", "items": { "type": "string" }, "maxItems": 16 },
            "parts": {
              "type": "array",
              "maxItems": 48,
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["id","attach","primitive","position","rotation","scale","color"],
                "properties": {
                  "id": { "type": "string", "minLength": 1, "maxLength": 64 },
                  "attach": { "type": "string", "enum": ["body","head"] },
                  "primitive": { "type": "string", "enum": ["sphere","capsule","cube","cylinder"] },
                  "position": { "type": "array", "items": { "type": "number" }, "minItems": 3, "maxItems": 3 },
                  "rotation": { "type": "array", "items": { "type": "number" }, "minItems": 3, "maxItems": 3 },
                  "scale": { "type": "array", "items": { "type": "number" }, "minItems": 3, "maxItems": 3 },
                  "color": { "type": "string", "pattern": "^#[0-9A-Fa-f]{6}$" },
                  "emission_color": { "type": ["string","null"], "pattern": "^#[0-9A-Fa-f]{6}$" },
                  "emission_strength": { "type": ["number","null"], "minimum": 0.0, "maximum": 10.0 }
                }
              }
            }
          }
        }
      ]
    }
  }
}"#
    .to_string()
}

pub async fn companion_chat(
    store: &WorldStore,
    cfg: &AssistantConfig,
    profile_id: &str,
    message: &str,
) -> Result<CompanionChatResponse> {
    if cfg.avatar_mesh_enabled {
        match crate::avatar_mesh::generate_avatar_mesh(store, cfg, profile_id, message).await {
            Ok(avatar) => {
                let reply = format!(
                    "Updated—your avatar mesh is now **{}**. Tell me what to change next.",
                    avatar.name
                );

                let mut history = load_companion_history(store, profile_id).unwrap_or_default();
                history.push(CompanionTurn {
                    role: "user".to_string(),
                    content: message.trim().to_string(),
                });
                history.push(CompanionTurn {
                    role: "assistant".to_string(),
                    content: reply.clone(),
                });
                if history.len() > 80 {
                    history = history.split_off(history.len().saturating_sub(80));
                }
                save_companion_history(store, profile_id, &history).ok();

                return Ok(CompanionChatResponse {
                    reply,
                    avatar: Some(avatar),
                });
            }
            Err(e) => {
                // Fall back to the primitives/tag pipeline if mesh generation isn't available.
                let mut out = companion_chat_primitives(store, cfg, profile_id, message).await?;
                let msg = e.to_string();
                if msg.contains("openscad not found") {
                    out.reply = format!(
                        "{}\n\nTip: install OpenSCAD to enable high-quality 3D avatar meshes.",
                        out.reply
                    );
                }
                return Ok(out);
            }
        }
    }

    companion_chat_primitives(store, cfg, profile_id, message).await
}

async fn companion_chat_primitives(
    store: &WorldStore,
    cfg: &AssistantConfig,
    profile_id: &str,
    message: &str,
) -> Result<CompanionChatResponse> {
    let Some(provider) = cfg.provider else {
        anyhow::bail!("no provider configured");
    };

    let mut history = load_companion_history(store, profile_id).unwrap_or_default();
    // keep history bounded
    if history.len() > 50 {
        history = history.split_off(history.len().saturating_sub(50));
    }

    let current_avatar = avatar_mod::load_avatar(store, profile_id)
        .context("load current avatar")?
        .unwrap_or(AvatarSpecV1 {
            version: "v1".to_string(),
            name: "Traveler".to_string(),
            primary_color: "#00D1FF".to_string(),
            secondary_color: "#FFFFFF".to_string(),
            height: 1.0,
            tags: vec!["default".to_string()],
            parts: Vec::new(),
            mesh: None,
        });
    let current_avatar_json =
        serde_json::to_string_pretty(&current_avatar).context("serialize current avatar")?;

    let mut prompt = String::new();
    prompt.push_str("You are the OWP Companion inside a Unity game.\n");
    prompt.push_str("You chat with the user and MAY update their avatar.\n");
    prompt.push_str("Return ONLY a JSON object matching the provided schema.\n");
    prompt.push_str("Do not include markdown, backticks, or explanations.\n");
    prompt.push_str("\nRules:\n");
    prompt.push_str("- Always set `reply` to a friendly, concise message.\n");
    prompt.push_str("- If the user requests an avatar change, set `avatar` to the FULL updated avatar object.\n");
    prompt.push_str("- If no avatar change is needed, set `avatar` to null.\n");
    prompt.push_str("- Keep colors as hex like \"#RRGGBB\" and height within 0.5..2.0.\n");
    prompt.push_str("- The Unity client renders a simple base archetype inferred from `avatar.tags` (humanoid/robot/dragon/wizard/etc.) plus `avatar.parts` primitives.\n");
    prompt.push_str("- Visual detail must be encoded via `avatar.tags` and `avatar.parts` (no real mesh/texture generation).\n");
    prompt.push_str("- Only claim details that are explicitly encoded in `avatar.tags` and/or `avatar.parts`.\n");
    prompt.push_str("- If the user asks for something you can't literally model, approximate it with primitives (horns/stripes/gear) and be honest.\n");
    prompt.push_str("\nCurrent avatar JSON:\n");
    prompt.push_str(&current_avatar_json);
    prompt.push_str("\n\nConversation:\n");
    for t in history.iter().rev().take(16).rev() {
        let who = if t.role == "assistant" {
            "Assistant"
        } else {
            "User"
        };
        prompt.push_str(who);
        prompt.push_str(": ");
        prompt.push_str(&t.content);
        prompt.push('\n');
    }
    prompt.push_str("User: ");
    prompt.push_str(message.trim());
    prompt.push('\n');

    let schema = companion_schema_json();
    let raw_json = match provider {
        AssistantProviderId::Codex => {
            let schema_file = tempfile::NamedTempFile::new().context("create schema tempfile")?;
            std::fs::write(schema_file.path(), &schema).context("write schema tempfile")?;
            let output_file = tempfile::NamedTempFile::new().context("create output tempfile")?;
            run_codex_structured(
                &prompt,
                schema_file.path(),
                output_file.path(),
                Some(store.root_dir()),
                cfg.codex_model.as_deref(),
                cfg.codex_reasoning_effort.as_deref(),
            )
            .await?;
            std::fs::read_to_string(output_file.path()).context("read codex output")?
        }
        AssistantProviderId::Claude => {
            let raw = run_claude_structured(&prompt, &schema, cfg.claude_model.as_deref()).await?;
            let v: Value = serde_json::from_str(&raw).context("parse claude result wrapper")?;
            if let Some(so) = v.get("structured_output") {
                serde_json::to_string(so).context("serialize structured_output")?
            } else if let Some(result) = v.get("result").and_then(|r| r.as_str()) {
                extract_json_object(result).context("extract json from claude result")?
            } else {
                anyhow::bail!("claude did not return structured_output or result");
            }
        }
    };

    let mut out: CompanionChatResponse =
        serde_json::from_str(&raw_json).context("parse companion output")?;
    out.reply = out.reply.trim().to_string();

    // Update avatar if provided
    if let Some(ref mut a) = out.avatar {
        a.version = "v1".to_string();
        avatar_mod::normalize_avatar(a);
        ensure_parts_for_prompt(a, message);
        avatar_mod::save_avatar(store, profile_id, a).context("save avatar")?;
        out.reply = enforce_honest_reply(&out.reply, a, message);
    }

    // Append to history and persist
    history.push(CompanionTurn {
        role: "user".to_string(),
        content: message.trim().to_string(),
    });
    history.push(CompanionTurn {
        role: "assistant".to_string(),
        content: out.reply.clone(),
    });
    if history.len() > 80 {
        history = history.split_off(history.len().saturating_sub(80));
    }
    save_companion_history(store, profile_id, &history).ok();

    Ok(out)
}

fn ensure_parts_for_prompt(avatar: &mut AvatarSpecV1, message: &str) {
    let had_parts = !avatar.parts.is_empty();

    let msg = message.to_lowercase();
    let primary = avatar.primary_color.clone();
    let secondary = avatar.secondary_color.clone();
    let mut parts: Vec<owp_protocol::AvatarPartV1> = Vec::new();

    fn ensure_tag(tags: &mut Vec<String>, tag: &str) {
        if tags.iter().any(|t| t.eq_ignore_ascii_case(tag)) {
            return;
        }
        tags.push(tag.to_string());
    }

    let wants_navi = msg.contains("na'vi")
        || msg.contains("navi")
        || msg.contains("pandora")
        || (msg.contains("avatar") && msg.contains("movie"));

    let wants_robot = msg.contains("robot")
        || msg.contains("android")
        || msg.contains("cyborg")
        || msg.contains("droid")
        || msg.contains("mech")
        || msg.contains("mecha")
        || msg.contains("cyberpunk");

    let wants_dragon = msg.contains("dragon") || msg.contains("wyrm") || msg.contains("drake");
    let wants_demon = msg.contains("demon") || msg.contains("devil") || msg.contains("infernal");
    let wants_angel = msg.contains("angel") || msg.contains("seraph") || msg.contains("holy");

    let wants_animal = msg.contains("cat")
        || msg.contains("feline")
        || msg.contains("fox")
        || msg.contains("wolf")
        || msg.contains("dog")
        || msg.contains("canine");

    let wants_knight = msg.contains("knight")
        || msg.contains("warrior")
        || msg.contains("soldier")
        || msg.contains("space marine")
        || msg.contains("samurai");

    let wants_wizard = msg.contains("wizard")
        || msg.contains("mage")
        || msg.contains("sorcer")
        || msg.contains("witch")
        || msg.contains("necromancer");

    if wants_navi {
        ensure_tag(&mut avatar.tags, "navi");
        ensure_tag(&mut avatar.tags, "biolum");
    }
    if wants_robot {
        ensure_tag(&mut avatar.tags, "robot");
    }
    if wants_dragon {
        ensure_tag(&mut avatar.tags, "dragon");
    }
    if wants_angel {
        ensure_tag(&mut avatar.tags, "angel");
    }
    if wants_wizard {
        ensure_tag(&mut avatar.tags, "wizard");
    }
    if wants_knight {
        ensure_tag(&mut avatar.tags, "knight");
    }
    if wants_animal {
        ensure_tag(&mut avatar.tags, "animal");
    }

    // If the model already provided parts, keep them, but still apply style tags.
    if had_parts {
        return;
    }

    let wants_horns = msg.contains("horn") || msg.contains("antler") || wants_dragon || wants_demon;
    let wants_glow =
        msg.contains("glow") || msg.contains("biolum") || msg.contains("neon") || wants_robot;
    let wants_tail = msg.contains("tail") || wants_animal || wants_dragon;
    let wants_wings = msg.contains("wing") || wants_dragon || wants_angel;
    let wants_braids =
        msg.contains("braid") || msg.contains("dread") || msg.contains("hair") || wants_navi;
    let wants_armor = msg.contains("armor")
        || msg.contains("armour")
        || msg.contains("shoulder")
        || wants_robot
        || wants_knight;
    let wants_stripes = msg.contains("stripe") || msg.contains("pattern") || wants_robot;

    // "Prompt anything" friendly presets: map well-known fantasies to visible procedural parts.
    let wants_glow = wants_glow || wants_navi;
    let wants_tail = wants_tail || wants_navi;
    let wants_braids = wants_braids || wants_navi;
    let wants_stripes = wants_stripes || wants_navi;

    if wants_navi {
        parts.push(make_part(
            "ear_left",
            "head",
            "capsule",
            [-0.32, 0.02, 0.02],
            [0.0, 0.0, 55.0],
            [0.08, 0.25, 0.08],
            secondary.clone(),
            None,
            None,
        ));
        parts.push(make_part(
            "ear_right",
            "head",
            "capsule",
            [0.32, 0.02, 0.02],
            [0.0, 0.0, -55.0],
            [0.08, 0.25, 0.08],
            secondary.clone(),
            None,
            None,
        ));
        parts.push(make_part(
            "eye_left",
            "head",
            "sphere",
            [-0.12, 0.02, -0.24],
            [0.0, 0.0, 0.0],
            [0.06, 0.06, 0.06],
            "#FFD36A".to_string(),
            Some("#FFD36A".to_string()),
            Some(1.6),
        ));
        parts.push(make_part(
            "eye_right",
            "head",
            "sphere",
            [0.12, 0.02, -0.24],
            [0.0, 0.0, 0.0],
            [0.06, 0.06, 0.06],
            "#FFD36A".to_string(),
            Some("#FFD36A".to_string()),
            Some(1.6),
        ));
    }

    if wants_animal && !wants_navi {
        parts.push(make_part(
            "ear_left",
            "head",
            "capsule",
            [-0.26, 0.22, 0.02],
            [0.0, 0.0, 35.0],
            [0.09, 0.22, 0.09],
            secondary.clone(),
            None,
            None,
        ));
        parts.push(make_part(
            "ear_right",
            "head",
            "capsule",
            [0.26, 0.22, 0.02],
            [0.0, 0.0, -35.0],
            [0.09, 0.22, 0.09],
            secondary.clone(),
            None,
            None,
        ));
    }

    if wants_robot {
        parts.push(make_part(
            "visor",
            "head",
            "cube",
            [0.0, 0.02, -0.26],
            [0.0, 0.0, 0.0],
            [0.34, 0.1, 0.04],
            "#0C1B2A".to_string(),
            Some(primary.clone()),
            Some(1.8),
        ));
        parts.push(make_part(
            "antenna",
            "head",
            "cylinder",
            [0.0, 0.32, 0.0],
            [0.0, 0.0, 0.0],
            [0.03, 0.22, 0.03],
            secondary.clone(),
            Some(primary.clone()),
            Some(1.2),
        ));
    }

    if wants_angel {
        parts.push(make_part(
            "halo",
            "head",
            "cylinder",
            [0.0, 0.42, 0.0],
            [0.0, 0.0, 0.0],
            [0.55, 0.04, 0.55],
            "#FFD36A".to_string(),
            Some("#FFD36A".to_string()),
            Some(2.0),
        ));
    }

    if wants_wizard {
        parts.push(make_part(
            "staff",
            "body",
            "cylinder",
            [0.65, 0.55, -0.15],
            [0.0, 0.0, 15.0],
            [0.6, 0.9, 0.6],
            secondary.clone(),
            Some(primary.clone()),
            Some(0.8),
        ));
        parts.push(make_part(
            "hat_brim",
            "head",
            "cylinder",
            [0.0, 0.18, 0.0],
            [0.0, 0.0, 0.0],
            [0.52, 0.05, 0.52],
            secondary.clone(),
            None,
            None,
        ));
        parts.push(make_part(
            "hat_top",
            "head",
            "cylinder",
            [0.0, 0.32, 0.0],
            [0.0, 0.0, 0.0],
            [0.9, 0.9, 0.9],
            secondary.clone(),
            None,
            None,
        ));
    }

    if wants_horns {
        parts.push(make_part(
            "horn_left",
            "head",
            "capsule",
            [-0.25, 0.24, 0.06],
            [25.0, 0.0, 20.0],
            [0.12, 0.45, 0.12],
            secondary.clone(),
            None,
            None,
        ));
        parts.push(make_part(
            "horn_right",
            "head",
            "capsule",
            [0.25, 0.24, 0.06],
            [25.0, 0.0, -20.0],
            [0.12, 0.45, 0.12],
            secondary.clone(),
            None,
            None,
        ));
    }

    if wants_braids {
        for i in 0..4 {
            parts.push(make_part(
                &format!("braid_{i}"),
                "head",
                "cylinder",
                [-0.15 + i as f32 * 0.1, -0.05, -0.12],
                [0.0, 0.0, 90.0],
                [0.04, 0.25, 0.04],
                secondary.clone(),
                None,
                None,
            ));
        }
    }

    if wants_tail {
        parts.push(make_part(
            "tail",
            "body",
            "cylinder",
            [0.0, 0.2, -0.35],
            [15.0, 0.0, 0.0],
            [0.06, 0.6, 0.06],
            primary.clone(),
            None,
            None,
        ));
    }

    if wants_wings {
        parts.push(make_part(
            "wing_left",
            "body",
            "cube",
            [-0.35, 0.9, -0.1],
            [0.0, 0.0, 20.0],
            [0.9, 0.55, 1.0],
            secondary.clone(),
            None,
            None,
        ));
        parts.push(make_part(
            "wing_right",
            "body",
            "cube",
            [0.35, 0.9, -0.1],
            [0.0, 0.0, -20.0],
            [0.9, 0.55, 1.0],
            secondary.clone(),
            None,
            None,
        ));
    }

    if wants_armor {
        parts.push(make_part(
            "shoulder_left",
            "body",
            "cube",
            [-0.22, 1.0, 0.0],
            [0.0, 0.0, 15.0],
            [0.25, 0.08, 0.18],
            secondary.clone(),
            None,
            None,
        ));
        parts.push(make_part(
            "shoulder_right",
            "body",
            "cube",
            [0.22, 1.0, 0.0],
            [0.0, 0.0, -15.0],
            [0.25, 0.08, 0.18],
            secondary.clone(),
            None,
            None,
        ));
    }

    if wants_glow || wants_stripes {
        for i in 0..5 {
            parts.push(make_part(
                &format!("stripe_{i}"),
                "body",
                "cube",
                [-0.15 + i as f32 * 0.075, 0.85, -0.56],
                [0.0, 0.0, 0.0],
                [0.02, 0.4, 0.02],
                primary.clone(),
                Some(primary.clone()),
                Some(2.5),
            ));
        }
    }

    // If still empty, add a default detail kit for visual feedback.
    if parts.is_empty() {
        parts.push(make_part(
            "chest_plate",
            "body",
            "cube",
            [0.0, 0.85, -0.58],
            [0.0, 0.0, 0.0],
            [0.26, 0.3, 0.1],
            secondary.clone(),
            None,
            None,
        ));
        parts.push(make_part(
            "belt",
            "body",
            "cylinder",
            [0.0, 0.62, 0.0],
            [0.0, 0.0, 0.0],
            [0.65, 0.06, 0.65],
            secondary.clone(),
            None,
            None,
        ));
    }

    avatar.parts = parts;
}

fn make_part(
    id: &str,
    attach: &str,
    primitive: &str,
    position: [f32; 3],
    rotation: [f32; 3],
    scale: [f32; 3],
    color: String,
    emission_color: Option<String>,
    emission_strength: Option<f32>,
) -> owp_protocol::AvatarPartV1 {
    owp_protocol::AvatarPartV1 {
        id: id.to_string(),
        attach: attach.to_string(),
        primitive: primitive.to_string(),
        position,
        rotation,
        scale,
        color,
        emission_color,
        emission_strength,
    }
}

fn enforce_honest_reply(reply: &str, avatar: &AvatarSpecV1, message: &str) -> String {
    let mut r = reply.trim().to_string();
    let style = summarize_style(&avatar.tags);
    let parts = summarize_parts(&avatar.parts);
    let msg = message.to_lowercase();
    let unrealistic = msg.contains("exactly like")
        || msg.contains("hyper-real")
        || msg.contains("cinematic")
        || msg.contains("photoreal")
        || msg.contains("movie")
        || msg.contains("perfect");

    if unrealistic {
        r = format!(
            "I can’t create fully realistic meshes yet; I approximated with simple shapes. {}",
            r
        );
    }

    let rendered = match (style.is_empty(), parts.is_empty()) {
        (true, true) => "base body only".to_string(),
        (false, true) => style,
        (true, false) => parts,
        (false, false) => format!("{style}; {parts}"),
    };
    r = format!("{r} Rendered: {rendered}.");
    r
}

fn summarize_style(tags: &[String]) -> String {
    if tags.is_empty() {
        return String::new();
    }
    let blob = tags
        .iter()
        .map(|t| t.to_lowercase())
        .collect::<Vec<_>>()
        .join(" ");

    let mut out = Vec::new();
    if blob.contains("robot") || blob.contains("cyborg") || blob.contains("android") {
        out.push("robot".to_string());
    }
    if blob.contains("navi") || blob.contains("na'vi") {
        out.push("na'vi".to_string());
    }
    if blob.contains("dragon") {
        out.push("dragon".to_string());
    }
    if blob.contains("angel") {
        out.push("angel".to_string());
    }
    if blob.contains("wizard") || blob.contains("mage") {
        out.push("wizard".to_string());
    }
    if blob.contains("knight") {
        out.push("knight".to_string());
    }
    if blob.contains("animal") {
        out.push("animal".to_string());
    }
    if blob.contains("biolum") || blob.contains("glow") || blob.contains("neon") {
        out.push("glow".to_string());
    }

    out.join(", ")
}

fn summarize_parts(parts: &[owp_protocol::AvatarPartV1]) -> String {
    if parts.is_empty() {
        return String::new();
    }
    let mut horns = 0;
    let mut stripes = 0;
    let mut wings = 0;
    let mut tail = 0;
    let mut armor = 0;
    let mut braids = 0;
    for p in parts {
        let id = p.id.to_lowercase();
        if id.contains("horn") {
            horns += 1;
        }
        if id.contains("stripe") {
            stripes += 1;
        }
        if id.contains("wing") {
            wings += 1;
        }
        if id.contains("tail") {
            tail += 1;
        }
        if id.contains("shoulder") || id.contains("armor") {
            armor += 1;
        }
        if id.contains("braid") {
            braids += 1;
        }
    }
    let mut out = Vec::new();
    if horns > 0 {
        out.push(format!("{horns} horns"));
    }
    if stripes > 0 {
        out.push(format!("{stripes} glow stripes"));
    }
    if wings > 0 {
        out.push(format!("{wings} wings"));
    }
    if tail > 0 {
        out.push("tail".to_string());
    }
    if armor > 0 {
        out.push("shoulder armor".to_string());
    }
    if braids > 0 {
        out.push(format!("{braids} braids"));
    }
    if out.is_empty() {
        out.push(format!("{} parts", parts.len()));
    }
    out.join(", ")
}
