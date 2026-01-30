use anyhow::{Context, Result};
use clap::Parser;
use owp_protocol::{wire, Hello, Message, OWP_PROTOCOL_VERSION};
use std::net::SocketAddr;
use tokio::net::TcpStream;
use tracing_subscriber::EnvFilter;
use url::Url;
use uuid::Uuid;

#[derive(Debug, Parser)]
#[command(
    name = "owp-client",
    version,
    about = "OWP minimal test client (handshake)"
)]
struct Cli {
    /// Connect string like `owp://127.0.0.1:7777?world=<uuid>`
    #[arg(long)]
    connect: Option<String>,

    /// Host:port (used if --connect is not provided)
    #[arg(long)]
    addr: Option<String>,

    /// World id (used if --connect is not provided)
    #[arg(long)]
    world_id: Option<String>,
}

#[tokio::main]
async fn main() -> Result<()> {
    tracing_subscriber::fmt()
        .with_env_filter(EnvFilter::from_default_env().add_directive("info".parse().unwrap()))
        .init();

    let cli = Cli::parse();
    let (addr, world_id) = if let Some(connect) = cli.connect {
        parse_connect_string(&connect)?
    } else {
        let addr = cli.addr.context("missing --addr or --connect")?;
        let world_id = cli.world_id.context("missing --world-id or --connect")?;
        (
            addr,
            Uuid::parse_str(&world_id).context("invalid --world-id")?,
        )
    };

    let addr: SocketAddr = addr.parse().context("invalid addr")?;
    let mut stream = TcpStream::connect(addr).await.context("connect")?;

    let request_id = Uuid::new_v4();
    let hello = Message::Hello(Hello {
        protocol_version: OWP_PROTOCOL_VERSION.to_string(),
        request_id,
        world_id: Some(world_id),
        client_name: Some("owp-client-cli".to_string()),
    });

    wire::write_message(&mut stream, &hello).await?;
    let msg = wire::read_message(&mut stream).await?;
    println!("{}", serde_json::to_string_pretty(&msg)?);
    Ok(())
}

fn parse_connect_string(connect: &str) -> Result<(String, Uuid)> {
    let url = Url::parse(connect).context("invalid connect string url")?;
    if url.scheme() != "owp" {
        anyhow::bail!("invalid scheme (expected owp://)");
    }
    let host = url.host_str().context("missing host")?;
    let port = url.port().context("missing port")?;

    let mut world_id: Option<Uuid> = None;
    for (k, v) in url.query_pairs() {
        if k == "world" {
            world_id = Some(Uuid::parse_str(&v).context("invalid world query param")?);
        }
    }
    let world_id = world_id.context("missing world query param")?;
    Ok((format!("{host}:{port}"), world_id))
}
