//! Shows the Windows touch keyboard.
//!
//! Starting `TabTip.exe` is the obvious approach and does not work on Windows
//! 11: the process is already running, so a second launch exits immediately and
//! nothing appears. The keyboard is shown through the shell's `ITipInvocation`
//! COM interface instead, which is what the input host itself uses.
//!
//! On a keyboard-less handheld this is the only way to type a Wi-Fi password or
//! a Bluetooth PIN, so a failure here makes the whole panel unusable.

// Toggle() keeps the name from the COM interface definition; renaming it would
// not match the vtable slot it stands for.
#![allow(non_snake_case)]

use windows::Win32::Foundation::HWND;
use windows::Win32::System::Com::{
    CLSCTX_ALL, COINIT_APARTMENTTHREADED, CoCreateInstance, CoInitializeEx, CoUninitialize,
};
use windows_core::{GUID, IUnknown, IUnknown_Vtbl, interface};

use crate::error::{Error, Result, winrt};

// UIHostNoLaunch. Undocumented but stable since Windows 8, and the mechanism
// every on-screen-keyboard helper uses; there is no documented alternative.
const CLSID_UI_HOST_NO_LAUNCH: GUID = GUID::from_u128(0x4CE576FA_83DC_4F88_951C_9D0782B4E376);

#[interface("37c994e7-432b-4834-a2f7-dce1f13b834b")]
unsafe trait ITipInvocation: IUnknown {
    /// Shows the touch keyboard, or hides it if it is already up.
    fn Toggle(&self, hwnd: HWND) -> windows_core::HRESULT;
}

/// Shows the touch keyboard over the foreground window.
///
/// `Toggle` is the only verb the interface offers, so this is called only from a
/// place where the keyboard is known to be down — pressing the button on a
/// prompt that has just opened.
pub fn show() -> Result<()> {
    // STA: this is a shell UI object, and it is invoked from a short-lived call
    // rather than the crate's MTA worker.
    let initialized = unsafe { CoInitializeEx(None, COINIT_APARTMENTTHREADED) };
    let must_uninitialize = initialized.is_ok();

    let result = (|| -> Result<()> {
        let invocation: ITipInvocation = unsafe {
            CoCreateInstance(&CLSID_UI_HOST_NO_LAUNCH, None, CLSCTX_ALL)
                .map_err(|e| winrt("CoCreateInstance(UIHostNoLaunch)", e))?
        };
        // The desktop window: the keyboard positions itself against the
        // foreground window regardless, and passing a window this process owns
        // is not required.
        let hr = unsafe { invocation.Toggle(HWND::default()) };
        if hr.is_err() {
            return Err(Error::WinRt {
                api: "ITipInvocation.Toggle",
                hresult: hr.0,
                message: String::new(),
            });
        }
        Ok(())
    })();

    if must_uninitialize {
        unsafe { CoUninitialize() };
    }
    result
}
