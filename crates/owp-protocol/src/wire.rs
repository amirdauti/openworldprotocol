use crate::Message;
use serde_json::Value;
use tokio::io::{AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt};

pub const MAX_FRAME_LEN: usize = 4 * 1024 * 1024; // 4 MiB

pub fn encode_frame(message: &Message) -> Result<Vec<u8>, serde_json::Error> {
    let payload = serde_json::to_vec(message)?;
    let mut out = Vec::with_capacity(4 + payload.len());
    let len = u32::try_from(payload.len()).unwrap_or(u32::MAX);
    out.extend_from_slice(&len.to_be_bytes());
    out.extend_from_slice(&payload);
    Ok(out)
}

pub async fn write_message<W: AsyncWrite + Unpin>(
    writer: &mut W,
    message: &Message,
) -> Result<(), WireError> {
    let frame = encode_frame(message)?;
    writer.write_all(&frame).await?;
    writer.flush().await?;
    Ok(())
}

pub async fn read_message<R: AsyncRead + Unpin>(reader: &mut R) -> Result<Message, WireError> {
    let mut len_buf = [0u8; 4];
    reader.read_exact(&mut len_buf).await?;
    let len = u32::from_be_bytes(len_buf) as usize;
    if len == 0 || len > MAX_FRAME_LEN {
        return Err(WireError::FrameLength(len));
    }

    let mut payload = vec![0u8; len];
    reader.read_exact(&mut payload).await?;

    // Validate JSON before decoding to structured types for better errors in logs.
    let _v: Value = serde_json::from_slice(&payload)?;
    let msg: Message = serde_json::from_slice(&payload)?;
    Ok(msg)
}

#[derive(Debug, thiserror::Error)]
pub enum WireError {
    #[error("io error: {0}")]
    Io(#[from] std::io::Error),
    #[error("json error: {0}")]
    Json(#[from] serde_json::Error),
    #[error("invalid frame length: {0}")]
    FrameLength(usize),
}
