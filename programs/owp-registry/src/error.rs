use solana_program::program_error::ProgramError;

#[repr(u32)]
pub enum RegistryError {
    InvalidInstruction = 1,
    InvalidPda = 2,
    Unauthorized = 3,
    StringTooLong = 4,
    AlreadyInitialized = 5,
    InvalidAccountData = 6,
}

impl From<RegistryError> for ProgramError {
    fn from(e: RegistryError) -> Self {
        ProgramError::Custom(e as u32)
    }
}

