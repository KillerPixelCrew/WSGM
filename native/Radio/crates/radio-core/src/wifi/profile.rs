//! WLAN profile XML.
//!
//! The native WLAN API has no "connect with this password" entry point: a
//! profile carrying the passphrase must be installed first, and `WlanConnect`
//! then names it. Building that XML is therefore the whole of password support,
//! and it is a pure string function so it can be tested without a radio.

use std::fmt::Write as _;

/// How a network protects itself, as far as profile authoring is concerned.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Security {
    /// No authentication and no encryption.
    Open,

    /// A pre-shared key. One profile shape covers WPA2-PSK and WPA3-SAE
    /// networks; see [`psk_profile`].
    PersonalPsk,

    /// 802.1X / enterprise. Not supported: it needs EAP configuration and a
    /// credential flow that a handheld game-mode panel has no business guessing.
    Enterprise,
}

/// Escapes text for an XML element body.
///
/// Passphrases routinely contain `&` and `<`, and an SSID is attacker-influenced
/// data from the air, so neither may be interpolated raw.
fn escape(value: &str) -> String {
    let mut out = String::with_capacity(value.len());
    for ch in value.chars() {
        match ch {
            '&' => out.push_str("&amp;"),
            '<' => out.push_str("&lt;"),
            '>' => out.push_str("&gt;"),
            '"' => out.push_str("&quot;"),
            '\'' => out.push_str("&apos;"),
            _ => out.push(ch),
        }
    }
    out
}

/// Builds an open-network profile.
#[must_use]
pub fn open_profile(ssid: &str) -> String {
    let name = escape(ssid);
    let mut xml = String::new();
    let _ = write!(
        xml,
        r#"<?xml version="1.0"?>
<WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
  <name>{name}</name>
  <SSIDConfig><SSID><name>{name}</name></SSID></SSIDConfig>
  <connectionType>ESS</connectionType>
  <connectionMode>auto</connectionMode>
  <MSM><security>
    <authEncryption>
      <authentication>open</authentication>
      <encryption>none</encryption>
      <useOneX>false</useOneX>
    </authEncryption>
  </security></MSM>
</WLANProfile>"#
    );
    xml
}

/// Builds a pre-shared-key profile.
///
/// When `wpa3` is set the profile is authored as `WPA3SAE` with
/// `<transitionMode>true</transitionMode>`, which is Microsoft's documented way
/// to get one profile that connects to both WPA3-Personal and WPA2-PSK networks.
/// Note the transition element lives in the **v4** namespace while the rest of
/// the document is v1 — putting it in v1 makes the whole profile invalid.
///
/// `wpa3` must be false on Windows 10, where the WPA3 authentication values do
/// not exist (they need Windows 11 21H2 or Server 2022).
#[must_use]
pub fn psk_profile(ssid: &str, passphrase: &str, wpa3: bool) -> String {
    let name = escape(ssid);
    let key = escape(passphrase);
    let (auth, transition) = if wpa3 {
        (
            "WPA3SAE",
            "\n      <transitionMode xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v4\">true</transitionMode>",
        )
    } else {
        ("WPA2PSK", "")
    };
    let mut xml = String::new();
    let _ = write!(
        xml,
        r#"<?xml version="1.0"?>
<WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
  <name>{name}</name>
  <SSIDConfig><SSID><name>{name}</name></SSID></SSIDConfig>
  <connectionType>ESS</connectionType>
  <connectionMode>auto</connectionMode>
  <MSM><security>
    <authEncryption>
      <authentication>{auth}</authentication>
      <encryption>AES</encryption>
      <useOneX>false</useOneX>{transition}
    </authEncryption>
    <sharedKey>
      <keyType>passPhrase</keyType>
      <protected>false</protected>
      <keyMaterial>{key}</keyMaterial>
    </sharedKey>
  </security></MSM>
</WLANProfile>"#
    );
    xml
}

/// Whether a passphrase can be used as a WPA-PSK passphrase at all.
///
/// Rejecting here turns a silent `WlanSetProfile` reason code into a message the
/// user can act on. The bounds are the 802.11 ones: 8..=63 printable ASCII
/// characters, or exactly 64 hex digits for a raw key.
#[must_use]
pub fn passphrase_is_valid(passphrase: &str) -> bool {
    let len = passphrase.chars().count();
    if len == 64 && passphrase.chars().all(|c| c.is_ascii_hexdigit()) {
        return true;
    }
    (8..=63).contains(&len) && passphrase.chars().all(|c| ('\u{20}'..='\u{7e}').contains(&c))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn psk_profile_uses_the_v4_namespace_for_transition_mode_only() {
        let xml = psk_profile("Net", "password1", true);
        assert!(xml.contains("<authentication>WPA3SAE</authentication>"));
        assert!(xml.contains(
            r#"<transitionMode xmlns="http://www.microsoft.com/networking/WLAN/profile/v4">true</transitionMode>"#
        ));
        // The document element must stay v1; only the transition element is v4.
        assert!(xml.contains(
            r#"<WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">"#
        ));
    }

    #[test]
    fn the_wpa2_fallback_carries_no_transition_element() {
        let xml = psk_profile("Net", "password1", false);
        assert!(xml.contains("<authentication>WPA2PSK</authentication>"));
        assert!(!xml.contains("transitionMode"));
    }

    #[test]
    fn ssid_and_passphrase_are_xml_escaped() {
        let xml = psk_profile("A&B<C>", "pw\"&<>'x", true);
        assert!(xml.contains("<name>A&amp;B&lt;C&gt;</name>"));
        assert!(xml.contains("<keyMaterial>pw&quot;&amp;&lt;&gt;&apos;x</keyMaterial>"));
        // No raw metacharacter may survive into the document body.
        assert!(!xml.contains("A&B"));
    }

    #[test]
    fn the_ssid_appears_as_both_profile_name_and_ssid() {
        let xml = open_profile("Cafe");
        assert_eq!(xml.matches("<name>Cafe</name>").count(), 2);
        assert!(xml.contains("<authentication>open</authentication>"));
    }

    #[test]
    fn passphrase_bounds_follow_the_802_11_rules() {
        assert!(!passphrase_is_valid("short"));
        assert!(passphrase_is_valid("12345678"));
        assert!(passphrase_is_valid(&"a".repeat(63)));
        // 64 characters is only legal as a raw key, so it must be all hex.
        assert!(!passphrase_is_valid(&"z".repeat(64)));
        assert!(passphrase_is_valid(&"a1B2".repeat(16)));
        assert!(!passphrase_is_valid(&"a".repeat(65)));
        // Non-printable and non-ASCII are rejected before Windows sees them.
        assert!(!passphrase_is_valid("pass\tword"));
        assert!(!passphrase_is_valid("päss word"));
    }
}
