use anyhow::{Context, Result};
use clap::{Parser, Subcommand};
use tracing_subscriber::EnvFilter;

mod storage;
mod tcp_game;
mod web_admin;

#[derive(Debug, Parser)]
#[command(
    name = "owp-server",
    version,
    about = "OWP local world server (early scaffold)"
)]
struct Cli {
    #[command(subcommand)]
    cmd: Command,
}

#[derive(Debug, Subcommand)]
enum Command {
    /// Create a new local world workspace
    CreateWorld {
        #[arg(long)]
        name: String,
        #[arg(long, default_value_t = 7777)]
        game_port: u16,
    },

    /// Run the host-only admin HTTP API (binds to 127.0.0.1 by default)
    Admin {
        #[arg(long, default_value = "127.0.0.1:9333")]
        listen: String,

        /// Require a bearer token. If omitted, a token is generated and saved to ~/.owp/admin-token.
        #[arg(long)]
        token: Option<String>,

        /// Disable auth entirely (not recommended).
        #[arg(long, default_value_t = false)]
        no_auth: bool,
    },

    /// Run the game server TCP listener (handshake only, for now)
    Run {
        /// World id to serve
        #[arg(long)]
        world_id: String,

        /// Override listen address (defaults to 0.0.0.0:<world game_port>)
        #[arg(long)]
        listen: Option<String>,
    },
}

#[tokio::main]
async fn main() -> Result<()> {
    tracing_subscriber::fmt()
        .with_env_filter(EnvFilter::from_default_env().add_directive("info".parse().unwrap()))
        .init();

    let cli = Cli::parse();
    match cli.cmd {
        Command::CreateWorld { name, game_port } => {
            let store = storage::WorldStore::new()?;
            let manifest = store.create_world(&name, game_port)?;
            println!("{}", serde_json::to_string_pretty(&manifest)?);
            Ok(())
        }
        Command::Admin {
            listen,
            token,
            no_auth,
        } => {
            let store = storage::WorldStore::new()?;
            let auth = if no_auth {
                web_admin::AuthMode::Disabled
            } else {
                let token = match token {
                    Some(t) => t,
                    None => store
                        .load_or_create_admin_token()
                        .context("create/load admin token")?,
                };
                web_admin::AuthMode::BearerToken(token)
            };

            web_admin::serve(listen, store, auth).await
        }
        Command::Run { world_id, listen } => {
            let store = storage::WorldStore::new()?;
            let world_id = uuid::Uuid::parse_str(&world_id).context("invalid --world-id")?;
            tcp_game::serve(store, world_id, listen).await
        }
    }
}
