use anyhow::{Context, Result};
use owp_protocol::{AvatarMeshV1, AvatarSpecV1};
use serde::Deserialize;
use serde_json::Value;
use sha2::{Digest, Sha256};
use std::path::PathBuf;
use std::time::Duration;
use tempfile::NamedTempFile;
use tokio::process::Command;
use tokio::time::timeout;

use crate::assistant::{
    run_claude_structured, run_codex_structured, AssistantConfig, AssistantProviderId,
};
use crate::avatar as avatar_mod;
use crate::storage::WorldStore;

const AVATAR_SCAD_SCHEMA_JSON: &str = r#"{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "type": "object",
  "additionalProperties": false,
  "required": ["name","tags","scad"],
  "properties": {
    "name": { "type": "string", "minLength": 1, "maxLength": 32 },
    "tags": { "type": "array", "items": { "type": "string" }, "maxItems": 16 },
    "scad": { "type": "string", "minLength": 1, "maxLength": 60000 }
  }
}"#;

#[derive(Debug, Deserialize)]
struct ScadResult {
    name: String,
    #[serde(default)]
    tags: Vec<String>,
    scad: String,
}

pub fn avatar_mesh_dir(store: &WorldStore, profile_id: &str) -> PathBuf {
    store.profiles_root().join(profile_id).join("avatar_mesh")
}

pub fn avatar_mesh_scad_path(store: &WorldStore, profile_id: &str) -> PathBuf {
    avatar_mesh_dir(store, profile_id).join("avatar.scad")
}

pub fn avatar_mesh_stl_path(store: &WorldStore, profile_id: &str) -> PathBuf {
    avatar_mesh_dir(store, profile_id).join("avatar.stl")
}

pub fn avatar_mesh_exists(store: &WorldStore, profile_id: &str) -> bool {
    avatar_mesh_stl_path(store, profile_id).exists()
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

pub async fn generate_avatar_mesh(
    store: &WorldStore,
    cfg: &AssistantConfig,
    profile_id: &str,
    user_prompt: &str,
) -> Result<AvatarSpecV1> {
    let Some(provider) = cfg.provider else {
        anyhow::bail!("no provider configured");
    };

    if !program_exists("openscad").await {
        anyhow::bail!("openscad not found on PATH");
    }

    let scad_prompt = format!(
        "You are generating a 3D avatar as OpenSCAD code.\n\
Return ONLY a JSON object matching the provided schema.\n\
Do not include markdown, backticks, or explanations.\n\
\n\
Goal:\n\
- Generate an avatar mesh that matches the user request.\n\
- The model should be visually distinctive and recognizable.\n\
\n\
Coordinate system + scale:\n\
- OpenSCAD is Z-up. Use Z as UP.\n\
- Units are meters.\n\
- Target overall height ~1.8m (feet at z=0, top of head near z=1.8).\n\
- The Unity client will convert OpenSCAD (x,y,z) into Unity (x,z,y).\n\
\n\
Performance constraints:\n\
- Keep polygon count reasonable; use $fn <= 48.\n\
- Prefer simple primitives + boolean ops + hull() + linear_extrude(); avoid excessive detail.\n\
- Ensure the mesh is closed/manifold.\n\
\n\
Safety constraints:\n\
- Do NOT use import(), surface(), include, or use statements.\n\
- Do NOT reference external files.\n\
\n\
Output requirements:\n\
- `scad` must be valid OpenSCAD.\n\
- `scad` should define `module avatar()` and call it at the end.\n\
- Put the entire model in a single `union()` inside `avatar()`.\n\
\n\
User request: {user_prompt}\n"
    );

    let raw_json = match provider {
        AssistantProviderId::Codex => {
            let schema_file = NamedTempFile::new().context("create schema tempfile")?;
            std::fs::write(schema_file.path(), AVATAR_SCAD_SCHEMA_JSON)
                .context("write schema tempfile")?;

            let output_file = NamedTempFile::new().context("create output tempfile")?;
            run_codex_structured(
                &scad_prompt,
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
            let raw = run_claude_structured(
                &scad_prompt,
                AVATAR_SCAD_SCHEMA_JSON,
                cfg.claude_model.as_deref(),
            )
            .await?;
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

    let scad: ScadResult = serde_json::from_str(&raw_json).context("parse scad json")?;

    let dir = avatar_mesh_dir(store, profile_id);
    std::fs::create_dir_all(&dir).with_context(|| format!("create {dir:?}"))?;

    let scad_path = avatar_mesh_scad_path(store, profile_id);
    std::fs::write(&scad_path, &scad.scad).with_context(|| format!("write {scad_path:?}"))?;

    let stl_path = avatar_mesh_stl_path(store, profile_id);

    // Render STL via OpenSCAD headless.
    let mut cmd = Command::new("openscad");
    cmd.arg("--render");
    cmd.arg("-o").arg(&stl_path);
    cmd.arg(&scad_path);
    cmd.stdin(std::process::Stdio::null());
    cmd.stdout(std::process::Stdio::null());
    cmd.stderr(std::process::Stdio::piped());

    let out = timeout(Duration::from_secs(60), cmd.output())
        .await
        .context("openscad timeout")?
        .context("run openscad")?;

    if !out.status.success() {
        let err = String::from_utf8_lossy(&out.stderr);
        anyhow::bail!("openscad failed: {err}");
    }

    let stl_bytes = std::fs::read(&stl_path).with_context(|| format!("read {stl_path:?}"))?;
    let hash = hex::encode(Sha256::digest(&stl_bytes));

    // Update avatar with tags + mesh pointer.
    let mut avatar = avatar_mod::load_avatar(store, profile_id)
        .context("load avatar")?
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

    avatar.name = scad.name;
    avatar.height = 1.8;
    // Merge tags (keep existing too).
    for t in scad.tags {
        if avatar.tags.iter().any(|x| x.eq_ignore_ascii_case(&t)) {
            continue;
        }
        if avatar.tags.len() >= 16 {
            break;
        }
        avatar.tags.push(t);
    }

    avatar.mesh = Some(AvatarMeshV1 {
        format: "stl".to_string(),
        uri: format!("/avatar/mesh?profile_id={profile_id}"),
        sha256: Some(hash),
    });

    avatar_mod::save_avatar(store, profile_id, &avatar).context("save avatar")?;
    Ok(avatar)
}

pub fn read_mesh_bytes(store: &WorldStore, profile_id: &str) -> Result<Vec<u8>> {
    let p = avatar_mesh_stl_path(store, profile_id);
    let bytes = std::fs::read(&p).with_context(|| format!("read {p:?}"))?;
    Ok(bytes)
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
