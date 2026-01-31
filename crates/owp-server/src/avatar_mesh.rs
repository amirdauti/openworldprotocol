use anyhow::{Context, Result};
use owp_protocol::{AvatarMeshPartV1, AvatarMeshV1, AvatarSpecV1};
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
  "required": ["name","primary_color","secondary_color","tags","parts","scad"],
  "properties": {
    "name": { "type": "string", "minLength": 1, "maxLength": 32 },
    "primary_color": { "type": "string", "pattern": "^#[0-9A-Fa-f]{6}$" },
    "secondary_color": { "type": "string", "pattern": "^#[0-9A-Fa-f]{6}$" },
    "tags": { "type": "array", "items": { "type": "string" }, "maxItems": 16 },
    "parts": {
      "type": "array",
      "maxItems": 8,
      "items": {
        "type": "object",
        "additionalProperties": false,
        "required": ["id","material"],
        "properties": {
          "id": { "type": "string", "pattern": "^[a-z0-9_]{1,16}$" },
          "material": { "type": ["string","null"], "enum": ["primary","secondary","emissive",null] }
        }
      }
    },
    "scad": { "type": "string", "minLength": 1, "maxLength": 60000 }
  }
}"#;

#[derive(Debug, Deserialize)]
struct ScadResult {
    name: String,
    primary_color: String,
    secondary_color: String,
    #[serde(default)]
    tags: Vec<String>,
    #[serde(default)]
    parts: Vec<ScadPart>,
    scad: String,
}

#[derive(Debug, Deserialize)]
struct ScadPart {
    id: String,
    #[serde(default)]
    material: Option<String>,
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

pub fn avatar_mesh_parts_dir(store: &WorldStore, profile_id: &str) -> PathBuf {
    avatar_mesh_dir(store, profile_id).join("parts")
}

pub fn avatar_mesh_part_stl_path(store: &WorldStore, profile_id: &str, part: &str) -> PathBuf {
    avatar_mesh_parts_dir(store, profile_id).join(format!("{part}.stl"))
}

pub fn avatar_mesh_stderr_path(store: &WorldStore, profile_id: &str) -> PathBuf {
    avatar_mesh_dir(store, profile_id).join("openscad.stderr.txt")
}

pub fn avatar_mesh_exists(store: &WorldStore, profile_id: &str) -> bool {
    avatar_mesh_stl_path(store, profile_id).exists()
}

pub fn avatar_mesh_part_exists(store: &WorldStore, profile_id: &str, part: &str) -> bool {
    avatar_mesh_part_stl_path(store, profile_id, part).exists()
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
- The silhouette should NOT be a single capsule/cylinder; include multiple body parts and accessories.\n\
\n\
Minimum structure (unless the user explicitly asks for something abstract):\n\
- A head\n\
- A torso\n\
- Two arms + two legs (simple is fine)\n\
- 1–3 iconic accessories tied to the prompt\n\
  Examples:\n\
  - wizard: pointed hat + staff + robe/cape\n\
  - knight: helmet + chestplate + sword/shield\n\
  - robot: segmented limbs + antenna + panel details\n\
  - alien: elongated limbs + crest + bioluminescent markings\n\
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
Readability constraints:\n\
- Exaggerate key features so they read at a distance (hat brim, staff head, shoulder silhouette, etc.).\n\
- Avoid thin spikes/wires that may disappear; keep smallest feature thickness >= 0.02m.\n\
\n\
Safety constraints:\n\
- Do NOT use import(), surface(), include, or use statements.\n\
- Do NOT reference external files.\n\
\n\
Multi-material hint:\n\
- STL has no colors, so we export multiple STL parts and apply materials in Unity.\n\
- In `parts`, ALWAYS include: {{\"id\":\"body\",\"material\":\"primary\"}}.\n\
- Add 1–3 accessory parts (e.g. \"hat\", \"staff\", \"orb\") and mark them as \"secondary\" or \"emissive\".\n\
\n\
Output requirements:\n\
- `scad` must be valid OpenSCAD.\n\
- `scad` must define `module avatar()` containing a single top-level `union()`.\n\
- `scad` must define `module part_body()`.\n\
- For each entry in `parts`, define a module named `part_<id>()`.\n\
- Add this at the end so the server can export individual parts:\n\
  - `render_part = \"all\";`\n\
  - if `render_part == \"all\"` call `avatar()`\n\
  - else if `render_part == \"body\"` call `part_body()`\n\
  - else if `render_part == \"<id>\"` call `part_<id>()` for each part\n\
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
    std::fs::create_dir_all(avatar_mesh_parts_dir(store, profile_id))
        .with_context(|| "create parts dir")?;

    let scad_path = avatar_mesh_scad_path(store, profile_id);
    std::fs::write(&scad_path, &scad.scad).with_context(|| format!("write {scad_path:?}"))?;

    let stl_path = avatar_mesh_stl_path(store, profile_id);

    // Render STL via OpenSCAD headless.
    let mut cmd = Command::new("openscad");
    cmd.arg("--render");
    cmd.arg("-o").arg(&stl_path);
    cmd.arg("-D").arg("render_part=\"all\"");
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
        let stderr_path = avatar_mesh_stderr_path(store, profile_id);
        let _ = std::fs::write(&stderr_path, err.as_bytes());
        anyhow::bail!("openscad failed: {err}");
    }

    let stl_bytes = std::fs::read(&stl_path).with_context(|| format!("read {stl_path:?}"))?;
    let hash = hex::encode(Sha256::digest(&stl_bytes));

    // Render optional accessory parts to separate STL files (for multi-material looks in Unity).
    let mut mesh_parts: Vec<AvatarMeshPartV1> = Vec::new();
    for p in scad.parts.iter() {
        let part_id = p.id.as_str();
        if part_id == "all" {
            continue;
        }
        if part_id == "body" {
            // Reserved selector for part_body(). Uses the combined mesh bytes/hash.
            mesh_parts.push(AvatarMeshPartV1 {
                id: "body".to_string(),
                uri: format!("/avatar/mesh?profile_id={profile_id}&part=body"),
                sha256: Some(hash.clone()),
                material: Some("primary".to_string()),
            });
            continue;
        }

        let out_path = avatar_mesh_part_stl_path(store, profile_id, part_id);

        let mut pcmd = Command::new("openscad");
        pcmd.arg("--render");
        pcmd.arg("-o").arg(&out_path);
        pcmd.arg("-D").arg(format!("render_part=\"{part_id}\""));
        pcmd.arg(&scad_path);
        pcmd.stdin(std::process::Stdio::null());
        pcmd.stdout(std::process::Stdio::null());
        pcmd.stderr(std::process::Stdio::piped());

        let pout = timeout(Duration::from_secs(60), pcmd.output())
            .await
            .context("openscad timeout (part)")?
            .context("run openscad (part)")?;

        if !pout.status.success() {
            continue;
        }

        if let Ok(bytes) = std::fs::read(&out_path) {
            let phash = hex::encode(Sha256::digest(&bytes));
            if phash == hash {
                // Likely ignored render_part and exported the full mesh; don't duplicate.
                continue;
            }
            mesh_parts.push(AvatarMeshPartV1 {
                id: part_id.to_string(),
                uri: format!("/avatar/mesh?profile_id={profile_id}&part={part_id}"),
                sha256: Some(phash),
                material: p.material.clone(),
            });
        }
    }

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
    avatar.primary_color = scad.primary_color;
    avatar.secondary_color = scad.secondary_color;
    // Replace tags with the model-provided tags (avoid unbounded tag spam from prior pipelines).
    avatar.tags.clear();
    avatar.tags.push("mesh".to_string());
    for t in scad.tags {
        if avatar.tags.iter().any(|x| x.eq_ignore_ascii_case(&t)) {
            continue;
        }
        if avatar.tags.len() >= 16 {
            break;
        }
        avatar.tags.push(t);
    }
    // Mesh supersedes primitive parts.
    avatar.parts.clear();

    avatar.mesh = Some(AvatarMeshV1 {
        format: "stl".to_string(),
        uri: format!("/avatar/mesh?profile_id={profile_id}"),
        sha256: Some(hash),
        parts: mesh_parts,
    });

    avatar_mod::save_avatar(store, profile_id, &avatar).context("save avatar")?;
    Ok(avatar)
}

pub fn read_mesh_bytes(
    store: &WorldStore,
    profile_id: &str,
    part: Option<&str>,
) -> Result<Vec<u8>> {
    let p = match part {
        None => avatar_mesh_stl_path(store, profile_id),
        Some("body") => avatar_mesh_stl_path(store, profile_id),
        Some(id) => avatar_mesh_part_stl_path(store, profile_id, id),
    };
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
