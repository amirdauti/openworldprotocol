extern crate alloc;

pub mod entrypoint;
pub mod error;
pub mod instruction;
pub mod processor;
pub mod state;

pub const SEED_WORLD: &[u8] = b"world";

