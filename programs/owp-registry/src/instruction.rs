use alloc::string::String;
use borsh::{BorshDeserialize, BorshSerialize};
use solana_program::program_error::ProgramError;

pub const NAME_MAX_LEN: usize = 32;
pub const ENDPOINT_MAX_LEN: usize = 64;
pub const METADATA_URI_MAX_LEN: usize = 128;

#[derive(Debug, Clone, BorshSerialize, BorshDeserialize)]
pub enum RegistryInstruction {
    RegisterWorld {
        world_id: [u8; 16],
        name: String,
        endpoint: String,
        game_port: u16,
        asset_port: Option<u16>,
        token_mint: Option<[u8; 32]>,
        dbc_pool: Option<[u8; 32]>,
        metadata_uri: String,
    },

    UpdateWorld {
        name: Option<String>,
        endpoint: Option<String>,
        game_port: Option<u16>,
        /// None = no change, Some(None) = clear, Some(Some(v)) = set.
        asset_port: Option<Option<u16>>,
        /// None = no change, Some(None) = clear, Some(Some(v)) = set.
        token_mint: Option<Option<[u8; 32]>>,
        /// None = no change, Some(None) = clear, Some(Some(v)) = set.
        dbc_pool: Option<Option<[u8; 32]>>,
        metadata_uri: Option<String>,
    },

    DelistWorld,
}

pub fn decode(input: &[u8]) -> Result<RegistryInstruction, ProgramError> {
    RegistryInstruction::try_from_slice(input).map_err(|_| ProgramError::InvalidInstructionData)
}
