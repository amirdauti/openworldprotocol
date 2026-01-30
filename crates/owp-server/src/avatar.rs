use anyhow::{Context, Result};
use owp_protocol::AvatarSpecV1;
use serde_json::Value;
use std::path::PathBuf;
use tempfile::NamedTempFile;

use crate::assistant::{run_claude_structured, run_codex_structured, AssistantProviderId};
use crate::storage::WorldStore;

pub const AVATAR_SCHEMA_JSON: &str = r#"{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "type": "object",
  "additionalProperties": false,
  "required": ["version","name","primary_color","secondary_color","height","tags"],
  "properties": {
    "version": { "type": "string" },
    "name": { "type": "string", "minLength": 1, "maxLength": 32 },
    "primary_color": { "type": "string", "pattern": "^#[0-9A-Fa-f]{6}$" },
    "secondary_color": { "type": "string", "pattern": "^#[0-9A-Fa-f]{6}$" },
    "height": { "type": "number", "minimum": 0.5, "maximum": 2.0 },
    "tags": { "type": "array", "items": { "type": "string" }, "maxItems": 16 }
  }
}"#;

pub fn avatar_path(store: &WorldStore, profile_id: &str) -> PathBuf {
    store.profiles_root().join(profile_id).join("avatar.json")
}

pub fn load_avatar(store: &WorldStore, profile_id: &str) -> Result<Option<AvatarSpecV1>> {
    let path = avatar_path(store, profile_id);
    if !path.exists() {
        return Ok(None);
    }
    let data = std::fs::read_to_string(&path).with_context(|| format!("read {path:?}"))?;
    let avatar: AvatarSpecV1 = serde_json::from_str(&data).context("parse avatar")?;
    Ok(Some(avatar))
}

pub fn save_avatar(store: &WorldStore, profile_id: &str, avatar: &AvatarSpecV1) -> Result<()> {
    let path = avatar_path(store, profile_id);
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent).with_context(|| format!("create {parent:?}"))?;
    }
    let json = serde_json::to_string_pretty(avatar).context("serialize avatar")?;
    std::fs::write(&path, format!("{json}\n")).with_context(|| format!("write {path:?}"))?;
    Ok(())
}

pub async fn generate_avatar(
    store: &WorldStore,
    provider: AssistantProviderId,
    user_prompt: &str,
) -> Result<AvatarSpecV1> {
    let system_prompt = format!(
        "You are the OWP avatar generator.\n\
Return ONLY a JSON object matching the provided schema.\n\
Do not include markdown, backticks, or explanations.\n\
User request: {user_prompt}\n\
\n\
Constraints:\n\
- version must be \"v1\"\n\
- colors must be hex like \"#RRGGBB\"\n\
- height must be between 0.5 and 2.0\n"
    );

    let avatar_json = match provider {
        AssistantProviderId::Codex => {
            let schema_file = NamedTempFile::new().context("create schema tempfile")?;
            std::fs::write(schema_file.path(), AVATAR_SCHEMA_JSON)
                .context("write schema tempfile")?;

            let output_file = NamedTempFile::new().context("create output tempfile")?;
            run_codex_structured(
                &system_prompt,
                schema_file.path(),
                output_file.path(),
                Some(store.root_dir()),
            )
            .await?;
            std::fs::read_to_string(output_file.path()).context("read codex output")?
        }
        AssistantProviderId::Claude => {
            let raw = run_claude_structured(&system_prompt, AVATAR_SCHEMA_JSON).await?;
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

    let avatar_value: Value = serde_json::from_str(&avatar_json).context("parse avatar json")?;
    let mut avatar = value_to_avatar(&avatar_value).context("normalize avatar json")?;
    avatar.version = "v1".to_string();
    normalize_avatar(&mut avatar);

    Ok(avatar)
}

fn value_to_avatar(v: &Value) -> Result<AvatarSpecV1> {
    let obj = v
        .as_object()
        .ok_or_else(|| anyhow::anyhow!("avatar is not an object"))?;

    let name = obj
        .get("name")
        .and_then(|v| v.as_str())
        .unwrap_or("Traveler")
        .to_string();

    let height = obj.get("height").and_then(|v| v.as_f64()).unwrap_or(1.0) as f32;

    let tags = obj
        .get("tags")
        .and_then(|v| v.as_array())
        .map(|arr| {
            arr.iter()
                .filter_map(|v| v.as_str().map(|s| s.to_string()))
                .collect::<Vec<_>>()
        })
        .unwrap_or_default();

    let primary_color = obj
        .get("primary_color")
        .and_then(|v| v.as_str())
        .map(|s| s.to_string())
        .or_else(|| {
            obj.get("colors")
                .and_then(|c| c.get("primary"))
                .and_then(|v| v.as_str())
                .map(|s| s.to_string())
        })
        .unwrap_or_else(|| "#00D1FF".to_string());

    let secondary_color = obj
        .get("secondary_color")
        .and_then(|v| v.as_str())
        .map(|s| s.to_string())
        .or_else(|| {
            obj.get("colors")
                .and_then(|c| c.get("secondary"))
                .and_then(|v| v.as_str())
                .map(|s| s.to_string())
        })
        .unwrap_or_else(|| "#FFFFFF".to_string());

    Ok(AvatarSpecV1 {
        version: obj
            .get("version")
            .and_then(|v| v.as_str())
            .unwrap_or("v1")
            .to_string(),
        name,
        primary_color,
        secondary_color,
        height,
        tags,
    })
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

fn normalize_avatar(a: &mut AvatarSpecV1) {
    if a.primary_color.is_empty() {
        a.primary_color = "#00D1FF".to_string();
    }
    if a.secondary_color.is_empty() {
        a.secondary_color = "#FFFFFF".to_string();
    }
    if !(0.5..=2.0).contains(&a.height) {
        a.height = a.height.clamp(0.5, 2.0);
    }
    if a.name.trim().is_empty() {
        a.name = "Traveler".to_string();
    }
}
