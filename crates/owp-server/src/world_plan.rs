use anyhow::{Context, Result};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use tempfile::NamedTempFile;

use crate::assistant::{
    run_claude_structured, run_codex_structured, AssistantConfig, AssistantProviderId,
};
use crate::storage::WorldStore;

// NOTE: Codex "output_schema" is strict: object schemas must list every key in `properties` in `required`.
pub const WORLD_PLAN_SCHEMA_JSON: &str = r#"{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "type": "object",
  "additionalProperties": false,
  "required": ["version","name","seed","biome_tags","ground","sky","fog","objects"],
  "properties": {
    "version": { "type": "string", "enum": ["v1"] },
    "name": { "type": "string", "minLength": 1, "maxLength": 48 },
    "seed": { "type": "integer", "minimum": 0, "maximum": 2147483647 },
    "biome_tags": { "type": "array", "items": { "type": "string" }, "maxItems": 16 },
    "ground": {
      "type": "object",
      "additionalProperties": false,
      "required": ["size","grid","height_scale","noise_scale","color"],
      "properties": {
        "size": { "type": "number", "minimum": 20.0, "maximum": 400.0 },
        "grid": { "type": "integer", "minimum": 16, "maximum": 256 },
        "height_scale": { "type": "number", "minimum": 0.0, "maximum": 40.0 },
        "noise_scale": { "type": "number", "minimum": 0.5, "maximum": 40.0 },
        "color": { "type": "string", "pattern": "^#[0-9A-Fa-f]{6}$" }
      }
    },
    "sky": {
      "type": "object",
      "additionalProperties": false,
      "required": ["sky_tint","ground_color","atmosphere_thickness","sun_size"],
      "properties": {
        "sky_tint": { "type": "string", "pattern": "^#[0-9A-Fa-f]{6}$" },
        "ground_color": { "type": "string", "pattern": "^#[0-9A-Fa-f]{6}$" },
        "atmosphere_thickness": { "type": "number", "minimum": 0.5, "maximum": 4.0 },
        "sun_size": { "type": "number", "minimum": 0.01, "maximum": 1.0 }
      }
    },
    "fog": {
      "type": "object",
      "additionalProperties": false,
      "required": ["enabled","color","density"],
      "properties": {
        "enabled": { "type": "boolean" },
        "color": { "type": "string", "pattern": "^#[0-9A-Fa-f]{6}$" },
        "density": { "type": "number", "minimum": 0.0, "maximum": 0.05 }
      }
    },
    "objects": {
      "type": "array",
      "maxItems": 400,
      "items": {
        "type": "object",
        "additionalProperties": false,
        "required": ["id","prefab","position","rotation","scale","color","emission_color","emission_strength"],
        "properties": {
          "id": { "type": "string", "minLength": 1, "maxLength": 48 },
          "prefab": { "type": "string", "enum": ["tower","tree","rock","crystal","camp","portal","ruins","house","lamp","alien","astronaut","barrel","van","ambulance"] },
          "position": { "type": "array", "items": { "type": "number" }, "minItems": 3, "maxItems": 3 },
          "rotation": { "type": "array", "items": { "type": "number" }, "minItems": 3, "maxItems": 3 },
          "scale": { "type": "array", "items": { "type": "number" }, "minItems": 3, "maxItems": 3 },
          "color": { "type": "string", "pattern": "^#[0-9A-Fa-f]{6}$" },
          "emission_color": { "type": "string", "pattern": "^#[0-9A-Fa-f]{6}$" },
          "emission_strength": { "type": "number", "minimum": 0.0, "maximum": 10.0 }
        }
      }
    }
  }
}"#;

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct WorldPlanV1 {
    pub version: String,
    pub name: String,
    pub seed: i32,
    #[serde(default)]
    pub biome_tags: Vec<String>,
    pub ground: WorldGroundV1,
    pub sky: WorldSkyV1,
    pub fog: WorldFogV1,
    #[serde(default)]
    pub objects: Vec<WorldObjectV1>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct WorldGroundV1 {
    pub size: f32,
    pub grid: i32,
    pub height_scale: f32,
    pub noise_scale: f32,
    pub color: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct WorldSkyV1 {
    pub sky_tint: String,
    pub ground_color: String,
    pub atmosphere_thickness: f32,
    pub sun_size: f32,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct WorldFogV1 {
    pub enabled: bool,
    pub color: String,
    pub density: f32,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct WorldObjectV1 {
    pub id: String,
    pub prefab: String,
    pub position: [f32; 3],
    pub rotation: [f32; 3],
    pub scale: [f32; 3],
    pub color: String,
    pub emission_color: String,
    pub emission_strength: f32,
}

pub async fn generate_world_plan(
    store: &WorldStore,
    cfg: &AssistantConfig,
    user_prompt: &str,
) -> Result<WorldPlanV1> {
    let Some(provider) = cfg.provider else {
        anyhow::bail!("no provider configured");
    };

    let prompt = format!(
        "You are generating a Unity world scene plan.\n\
Return ONLY a JSON object matching the provided schema.\n\
Do not include markdown, backticks, or explanations.\n\
\n\
Constraints:\n\
- World should look game-like (composition + variety), not a single empty plane.\n\
- Use a coherent style: sci-fi, fantasy, cyberpunk, desert, forest, etc.\n\
- Objects must be placed within the ground square centered at origin.\n\
- Y is up. Ground is centered at (0,0,0).\n\
\n\
Prefab catalog (stylized built-ins):\n\
- tower: tall cylindrical tower with roof and windows\n\
- house: small hut with a gabled roof\n\
- ruins: broken arch + rubble\n\
- camp: tent + campfire area\n\
- portal: glowing ring portal\n\
- tree: stylized low-poly tree\n\
- rock: stylized rock\n\
- crystal: glowing crystal spire\n\
- lamp: sci-fi lamp post with emissive light\n\
- alien: a small alien character statue/prop\n\
- astronaut: an astronaut character statue/prop\n\
- barrel: barrel prop(s)\n\
- van: sci-fi van vehicle prop\n\
- ambulance: sci-fi ambulance vehicle prop\n\
\n\
Guidance:\n\
- Include 1 main landmark (tower OR portal), 2-4 secondary POIs (camp/ruins/house), and natural scatter (trees/rocks/crystals).\n\
- Keep object count reasonable (50-200).\n\
- Use emissive only for portal/crystals/lamps/campfires.\n\
- If an object should NOT glow, set emission_strength=0 and emission_color=\"#000000\".\n\
\n\
User prompt: {user_prompt}\n"
    );

    let raw_json = match provider {
        AssistantProviderId::Codex => {
            let schema_file = NamedTempFile::new().context("create schema tempfile")?;
            std::fs::write(schema_file.path(), WORLD_PLAN_SCHEMA_JSON)
                .context("write schema tempfile")?;
            let output_file = NamedTempFile::new().context("create output tempfile")?;
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
            let raw =
                run_claude_structured(&prompt, WORLD_PLAN_SCHEMA_JSON, cfg.claude_model.as_deref())
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

    let plan: WorldPlanV1 = serde_json::from_str(&raw_json).context("parse world plan json")?;
    Ok(plan)
}

fn extract_json_object(text: &str) -> Result<String> {
    let start = text
        .find('{')
        .ok_or_else(|| anyhow::anyhow!("no '{{' found in text"))?;

    let mut depth = 0usize;
    let mut in_string = false;
    let mut escape = false;

    for (i, ch) in text[start..].char_indices() {
        if in_string {
            if escape {
                escape = false;
                continue;
            }
            match ch {
                '\\' => escape = true,
                '"' => in_string = false,
                _ => {}
            }
            continue;
        }

        match ch {
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
