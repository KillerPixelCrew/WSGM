//! Whether this process holds an elevated (high-integrity) token — the parent uses
//! it to decide whether de-elevation is needed, the child to verify it worked.

use windows_sys::Win32::Foundation::{CloseHandle, HANDLE};
use windows_sys::Win32::Security::{
    GetTokenInformation, TOKEN_ELEVATION, TOKEN_QUERY, TokenElevation,
};
use windows_sys::Win32::System::Threading::{GetCurrentProcess, OpenProcessToken};

/// `Some(true)` elevated, `Some(false)` not, `None` when the token cannot be read.
pub fn is_elevated() -> Option<bool> {
    // SAFETY: standard OpenProcessToken + GetTokenInformation(TokenElevation).
    unsafe {
        let mut token: HANDLE = std::ptr::null_mut();
        if OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token) == 0 {
            return None;
        }
        let mut elevation = TOKEN_ELEVATION { TokenIsElevated: 0 };
        let mut returned = 0u32;
        let ok = GetTokenInformation(
            token,
            TokenElevation,
            (&mut elevation as *mut TOKEN_ELEVATION).cast(),
            std::mem::size_of::<TOKEN_ELEVATION>() as u32,
            &mut returned,
        );
        CloseHandle(token);
        if ok == 0 {
            None
        } else {
            Some(elevation.TokenIsElevated != 0)
        }
    }
}
