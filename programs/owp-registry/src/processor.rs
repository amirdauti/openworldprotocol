use borsh::{BorshDeserialize, BorshSerialize};
use solana_program::{
    account_info::{next_account_info, AccountInfo},
    entrypoint::ProgramResult,
    msg,
    program::invoke_signed,
    program_error::ProgramError,
    pubkey::Pubkey,
    system_instruction,
    sysvar::{clock::Clock, rent::Rent, Sysvar},
};

use crate::{
    error::RegistryError,
    instruction::{decode, RegistryInstruction, ENDPOINT_MAX_LEN, METADATA_URI_MAX_LEN, NAME_MAX_LEN},
    state::{read_fixed_string, write_fixed_string, WorldEntry, WORLD_ENTRY_MAGIC, WORLD_ENTRY_VERSION},
    SEED_WORLD,
};

pub struct Processor;

impl Processor {
    pub fn process(
        program_id: &Pubkey,
        accounts: &[AccountInfo],
        instruction_data: &[u8],
    ) -> ProgramResult {
        let ix = decode(instruction_data)?;
        match ix {
            RegistryInstruction::RegisterWorld {
                world_id,
                name,
                endpoint,
                game_port,
                asset_port,
                token_mint,
                dbc_pool,
                metadata_uri,
            } => Self::register_world(
                program_id,
                accounts,
                world_id,
                name,
                endpoint,
                game_port,
                asset_port,
                token_mint,
                dbc_pool,
                metadata_uri,
            ),
            RegistryInstruction::UpdateWorld {
                name,
                endpoint,
                game_port,
                asset_port,
                token_mint,
                dbc_pool,
                metadata_uri,
            } => Self::update_world(
                program_id,
                accounts,
                name,
                endpoint,
                game_port,
                asset_port,
                token_mint,
                dbc_pool,
                metadata_uri,
            ),
            RegistryInstruction::DelistWorld => Self::delist_world(program_id, accounts),
        }
    }

    fn register_world(
        program_id: &Pubkey,
        accounts: &[AccountInfo],
        world_id: [u8; 16],
        name: String,
        endpoint: String,
        game_port: u16,
        asset_port: Option<u16>,
        token_mint: Option<[u8; 32]>,
        dbc_pool: Option<[u8; 32]>,
        metadata_uri: String,
    ) -> ProgramResult {
        if name.as_bytes().len() > NAME_MAX_LEN
            || endpoint.as_bytes().len() > ENDPOINT_MAX_LEN
            || metadata_uri.as_bytes().len() > METADATA_URI_MAX_LEN
        {
            return Err(RegistryError::StringTooLong.into());
        }

        let account_info_iter = &mut accounts.iter();
        let payer = next_account_info(account_info_iter)?;
        let world_entry_account = next_account_info(account_info_iter)?;
        let authority = next_account_info(account_info_iter)?;
        let system_program = next_account_info(account_info_iter)?;

        if !payer.is_signer || !authority.is_signer {
            return Err(ProgramError::MissingRequiredSignature);
        }
        if *system_program.key != solana_program::system_program::id() {
            return Err(ProgramError::IncorrectProgramId);
        }

        let (expected_pda, bump) =
            Pubkey::find_program_address(&[SEED_WORLD, world_id.as_ref()], program_id);
        if expected_pda != *world_entry_account.key {
            msg!("invalid world entry PDA: expected={expected_pda} got={}", world_entry_account.key);
            return Err(RegistryError::InvalidPda.into());
        }

        if world_entry_account.lamports() > 0 {
            return Err(RegistryError::AlreadyInitialized.into());
        }

        let rent = Rent::get()?;
        let lamports = rent.minimum_balance(WorldEntry::LEN);
        invoke_signed(
            &system_instruction::create_account(
                payer.key,
                world_entry_account.key,
                lamports,
                WorldEntry::LEN as u64,
                program_id,
            ),
            &[payer.clone(), world_entry_account.clone(), system_program.clone()],
            &[&[SEED_WORLD, world_id.as_ref(), &[bump]]],
        )?;

        let clock = Clock::get()?;

        let mut entry = WorldEntry {
            magic: WORLD_ENTRY_MAGIC,
            version: WORLD_ENTRY_VERSION,
            bump,
            world_id,
            authority: authority.key.to_bytes(),
            name: [0u8; crate::state::NAME_LEN],
            endpoint: [0u8; crate::state::ENDPOINT_LEN],
            game_port,
            asset_port: asset_port.unwrap_or(0),
            token_mint: token_mint.unwrap_or([0u8; 32]),
            dbc_pool: dbc_pool.unwrap_or([0u8; 32]),
            metadata_uri: [0u8; crate::state::METADATA_URI_LEN],
            last_update_slot: clock.slot,
        };

        write_fixed_string(&mut entry.name, &name).map_err(|_| RegistryError::StringTooLong)?;
        write_fixed_string(&mut entry.endpoint, &endpoint).map_err(|_| RegistryError::StringTooLong)?;
        write_fixed_string(&mut entry.metadata_uri, &metadata_uri)
            .map_err(|_| RegistryError::StringTooLong)?;

        let mut data = world_entry_account.data.borrow_mut();
        entry
            .serialize(&mut &mut data[..])
            .map_err(|_| RegistryError::InvalidAccountData)?;

        msg!(
            "registered world: {} at {}:{}",
            read_fixed_string(&entry.name),
            read_fixed_string(&entry.endpoint),
            entry.game_port
        );
        Ok(())
    }

    fn update_world(
        program_id: &Pubkey,
        accounts: &[AccountInfo],
        name: Option<String>,
        endpoint: Option<String>,
        game_port: Option<u16>,
        asset_port: Option<Option<u16>>,
        token_mint: Option<Option<[u8; 32]>>,
        dbc_pool: Option<Option<[u8; 32]>>,
        metadata_uri: Option<String>,
    ) -> ProgramResult {
        let account_info_iter = &mut accounts.iter();
        let world_entry_account = next_account_info(account_info_iter)?;
        let authority = next_account_info(account_info_iter)?;

        if !authority.is_signer {
            return Err(ProgramError::MissingRequiredSignature);
        }
        if world_entry_account.owner != program_id {
            return Err(ProgramError::IncorrectProgramId);
        }

        let mut entry = WorldEntry::try_from_slice(&world_entry_account.data.borrow())
            .map_err(|_| RegistryError::InvalidAccountData)?;
        if entry.magic != WORLD_ENTRY_MAGIC || entry.version != WORLD_ENTRY_VERSION {
            return Err(RegistryError::InvalidAccountData.into());
        }

        let (expected_pda, _) =
            Pubkey::find_program_address(&[SEED_WORLD, entry.world_id.as_ref()], program_id);
        if expected_pda != *world_entry_account.key {
            return Err(RegistryError::InvalidPda.into());
        }
        if entry.authority != authority.key.to_bytes() {
            return Err(RegistryError::Unauthorized.into());
        }

        if let Some(v) = name {
            if v.as_bytes().len() > NAME_MAX_LEN {
                return Err(RegistryError::StringTooLong.into());
            }
            write_fixed_string(&mut entry.name, &v).map_err(|_| RegistryError::StringTooLong)?;
        }
        if let Some(v) = endpoint {
            if v.as_bytes().len() > ENDPOINT_MAX_LEN {
                return Err(RegistryError::StringTooLong.into());
            }
            write_fixed_string(&mut entry.endpoint, &v).map_err(|_| RegistryError::StringTooLong)?;
        }
        if let Some(v) = metadata_uri {
            if v.as_bytes().len() > METADATA_URI_MAX_LEN {
                return Err(RegistryError::StringTooLong.into());
            }
            write_fixed_string(&mut entry.metadata_uri, &v)
                .map_err(|_| RegistryError::StringTooLong)?;
        }

        if let Some(p) = game_port {
            entry.game_port = p;
        }
        if let Some(v) = asset_port {
            entry.asset_port = v.unwrap_or(0);
        }
        if let Some(v) = token_mint {
            entry.token_mint = v.unwrap_or([0u8; 32]);
        }
        if let Some(v) = dbc_pool {
            entry.dbc_pool = v.unwrap_or([0u8; 32]);
        }

        entry.last_update_slot = Clock::get()?.slot;

        let mut data = world_entry_account.data.borrow_mut();
        entry
            .serialize(&mut &mut data[..])
            .map_err(|_| RegistryError::InvalidAccountData)?;

        msg!(
            "updated world: {} at {}:{}",
            read_fixed_string(&entry.name),
            read_fixed_string(&entry.endpoint),
            entry.game_port
        );
        Ok(())
    }

    fn delist_world(program_id: &Pubkey, accounts: &[AccountInfo]) -> ProgramResult {
        let account_info_iter = &mut accounts.iter();
        let world_entry_account = next_account_info(account_info_iter)?;
        let authority = next_account_info(account_info_iter)?;

        if !authority.is_signer {
            return Err(ProgramError::MissingRequiredSignature);
        }
        if world_entry_account.owner != program_id {
            return Err(ProgramError::IncorrectProgramId);
        }

        let entry = WorldEntry::try_from_slice(&world_entry_account.data.borrow())
            .map_err(|_| RegistryError::InvalidAccountData)?;
        if entry.magic != WORLD_ENTRY_MAGIC || entry.version != WORLD_ENTRY_VERSION {
            return Err(RegistryError::InvalidAccountData.into());
        }
        if entry.authority != authority.key.to_bytes() {
            return Err(RegistryError::Unauthorized.into());
        }

        let (expected_pda, _) =
            Pubkey::find_program_address(&[SEED_WORLD, entry.world_id.as_ref()], program_id);
        if expected_pda != *world_entry_account.key {
            return Err(RegistryError::InvalidPda.into());
        }

        // Drain lamports to authority and zero out data.
        let lamports = world_entry_account.lamports();
        **authority.lamports.borrow_mut() = authority
            .lamports()
            .checked_add(lamports)
            .ok_or(ProgramError::ArithmeticOverflow)?;
        **world_entry_account.lamports.borrow_mut() = 0;

        let mut data = world_entry_account.data.borrow_mut();
        for b in data.iter_mut() {
            *b = 0;
        }

        msg!("delisted world entry");
        Ok(())
    }
}
