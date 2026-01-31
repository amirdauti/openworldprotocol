use borsh::{BorshDeserialize, BorshSerialize};

pub const SEED_WORLD: &[u8] = b"world";

pub const WORLD_ENTRY_MAGIC: [u8; 8] = *b"OWPREG01";
pub const WORLD_ENTRY_VERSION: u8 = 1;

pub const NAME_LEN: usize = 32;
pub const ENDPOINT_LEN: usize = 64;
pub const METADATA_URI_LEN: usize = 128;

#[derive(Debug, Clone, BorshSerialize, BorshDeserialize)]
pub struct WorldEntry {
    pub magic: [u8; 8],
    pub version: u8,
    pub bump: u8,

    pub world_id: [u8; 16],
    pub authority: [u8; 32],

    pub name: [u8; NAME_LEN],
    pub endpoint: [u8; ENDPOINT_LEN],
    pub game_port: u16,
    /// 0 means "none".
    pub asset_port: u16,

    /// All-zero pubkey bytes means "none".
    pub token_mint: [u8; 32],
    /// All-zero pubkey bytes means "none".
    pub dbc_pool: [u8; 32],

    pub metadata_uri: [u8; METADATA_URI_LEN],
    pub last_update_slot: u64,
}

impl WorldEntry {
    pub const LEN: usize = 358;
}

pub fn write_fixed_string<const N: usize>(dst: &mut [u8; N], src: &str) -> Result<(), ()> {
    let bytes = src.as_bytes();
    if bytes.len() > N {
        return Err(());
    }
    *dst = [0u8; N];
    dst[..bytes.len()].copy_from_slice(bytes);
    Ok(())
}

pub fn read_fixed_string(bytes: &[u8]) -> String {
    let mut end = 0usize;
    while end < bytes.len() && bytes[end] != 0 {
        end += 1;
    }
    String::from_utf8_lossy(&bytes[..end]).to_string()
}

#[cfg(test)]
mod tests {
    use super::*;
    use borsh::BorshSerialize;

    #[test]
    fn world_entry_len_matches_borsh() {
        let entry = WorldEntry {
            magic: WORLD_ENTRY_MAGIC,
            version: WORLD_ENTRY_VERSION,
            bump: 255,
            world_id: [7u8; 16],
            authority: [9u8; 32],
            name: [0u8; NAME_LEN],
            endpoint: [0u8; ENDPOINT_LEN],
            game_port: 7777,
            asset_port: 0,
            token_mint: [0u8; 32],
            dbc_pool: [0u8; 32],
            metadata_uri: [0u8; METADATA_URI_LEN],
            last_update_slot: 0,
        };
        let data = entry.try_to_vec().expect("serialize");
        assert_eq!(data.len(), WorldEntry::LEN);
    }
}
