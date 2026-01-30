use anyhow::{Context, Result};
use serde::{Deserialize, Serialize};
use std::path::Path;
use std::time::Duration;
use tokio::io::AsyncWriteExt;
use tokio::process::Command;
use tokio::time::timeout;

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
}

impl Default for AssistantConfig {
    fn default() -> Self {
        Self { provider: None }
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
) -> Result<()> {
    let mut cmd = Command::new("codex");
    cmd.arg("exec");
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

pub async fn run_claude_structured(prompt: &str, schema: &str) -> Result<String> {
    let mut cmd = Command::new("claude");
    cmd.arg("--print");
    cmd.arg("--output-format").arg("json");
    cmd.arg("--json-schema").arg(schema);
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
