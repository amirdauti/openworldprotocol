use anyhow::{Context, Result};
use directories::UserDirs;
use owp_protocol::{WorldManifestV1, WorldPorts, WorldTokenInfo, OWP_PROTOCOL_VERSION};
use rand::{distributions::Alphanumeric, Rng};
use std::fs;
use std::path::{Path, PathBuf};
use time::OffsetDateTime;
use uuid::Uuid;

#[derive(Clone)]
pub struct WorldStore {
    root: PathBuf,
}

impl WorldStore {
    pub fn new() -> Result<Self> {
        let user_dirs = UserDirs::new().context("resolve user dirs")?;
        let home = user_dirs.home_dir();
        let root = home.join(".owp");
        fs::create_dir_all(&root).context("create ~/.owp")?;
        fs::create_dir_all(root.join("worlds")).context("create ~/.owp/worlds")?;
        Ok(Self { root })
    }

    pub fn worlds_root(&self) -> PathBuf {
        self.root.join("worlds")
    }

    pub fn admin_token_path(&self) -> PathBuf {
        self.root.join("admin-token")
    }

    pub fn load_or_create_admin_token(&self) -> Result<String> {
        let path = self.admin_token_path();
        if path.exists() {
            let t = fs::read_to_string(&path).context("read admin-token")?;
            return Ok(t.trim().to_string());
        }

        let token: String = rand::thread_rng()
            .sample_iter(&Alphanumeric)
            .take(48)
            .map(char::from)
            .collect();
        fs::write(&path, format!("{token}\n")).context("write admin-token")?;
        Ok(token)
    }

    pub fn world_dir(&self, world_id: Uuid) -> PathBuf {
        self.worlds_root().join(world_id.to_string())
    }

    pub fn manifest_path(world_dir: &Path) -> PathBuf {
        world_dir.join("manifest").join("world.manifest.json")
    }

    pub fn create_world(&self, name: &str, game_port: u16) -> Result<WorldManifestV1> {
        let world_id = Uuid::new_v4();
        let dir = self.world_dir(world_id);
        fs::create_dir_all(dir.join("manifest")).context("create manifest dir")?;
        fs::create_dir_all(dir.join("chunks")).context("create chunks dir")?;
        fs::create_dir_all(dir.join("assets")).context("create assets dir")?;
        fs::create_dir_all(dir.join("snapshots")).context("create snapshots dir")?;
        fs::create_dir_all(dir.join("logs")).context("create logs dir")?;

        let manifest = WorldManifestV1 {
            protocol_version: OWP_PROTOCOL_VERSION.to_string(),
            world_id,
            name: name.to_string(),
            created_at: OffsetDateTime::now_utc(),
            world_authority_pubkey: None,
            ports: WorldPorts {
                game_port,
                asset_port: None,
            },
            token: None,
        };

        self.write_manifest(&dir, &manifest)?;
        Ok(manifest)
    }

    pub fn list_worlds(&self) -> Result<Vec<WorldManifestV1>> {
        let mut out = Vec::new();
        for entry in fs::read_dir(self.worlds_root()).context("read worlds dir")? {
            let entry = entry?;
            if !entry.file_type()?.is_dir() {
                continue;
            }
            let world_dir = entry.path();
            let manifest_path = Self::manifest_path(&world_dir);
            if !manifest_path.exists() {
                continue;
            }
            if let Ok(m) = self.read_manifest(&world_dir) {
                out.push(m);
            }
        }
        Ok(out)
    }

    pub fn read_manifest(&self, world_dir: &Path) -> Result<WorldManifestV1> {
        let path = Self::manifest_path(world_dir);
        let data = fs::read_to_string(&path).with_context(|| format!("read {path:?}"))?;
        let manifest: WorldManifestV1 =
            serde_json::from_str(&data).with_context(|| format!("parse {path:?}"))?;
        Ok(manifest)
    }

    pub fn write_manifest(&self, world_dir: &Path, manifest: &WorldManifestV1) -> Result<()> {
        let path = Self::manifest_path(world_dir);
        let json = serde_json::to_string_pretty(manifest).context("serialize manifest")?;
        fs::write(&path, format!("{json}\n")).with_context(|| format!("write {path:?}"))?;
        Ok(())
    }

    pub fn set_token_info(
        &self,
        world_id: Uuid,
        network: String,
        mint: String,
        dbc_pool: Option<String>,
        tx_signatures: Vec<String>,
    ) -> Result<WorldManifestV1> {
        let dir = self.world_dir(world_id);
        if !dir.exists() {
            anyhow::bail!("world not found");
        }

        let mut manifest = self.read_manifest(&dir)?;
        manifest.token = Some(WorldTokenInfo {
            network,
            mint,
            dbc_pool,
            tx_signatures,
        });
        self.write_manifest(&dir, &manifest)?;
        Ok(manifest)
    }
}
