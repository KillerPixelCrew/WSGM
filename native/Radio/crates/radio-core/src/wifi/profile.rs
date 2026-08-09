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

    /// OWE ("Enhanced Open"): joined without a password like an open network,
    /// but encrypted, and Windows rejects a profile that claims
    /// `open`/`none` for it. Its own variant precisely so it cannot be
    /// authored as a legacy open network and then fail to connect.
    EnhancedOpen,

    /// A protection this crate cannot author a profile for — WEP, whose
    /// open-system authentication otherwise looks exactly like an unsecured
    /// network. Named so the UI can decline instead of installing an
    /// `open`/`none` profile that can never join.
    Unsupported,
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

/// The `<SSID>` element body.
///
/// An SSID is 32 arbitrary bytes, not text. When those bytes are not valid
/// UTF-8 the `<name>` form cannot express them — the lossy string carries
/// U+FFFD where the original had raw bytes, and Windows would then author a
/// profile for a network that does not exist. `<hex>` is the documented way to
/// name such an SSID exactly.
fn ssid_element(name: &str, raw: Option<&[u8]>) -> String {
    match raw {
        Some(bytes) if std::str::from_utf8(bytes).is_err() => {
            let mut hex = String::with_capacity(bytes.len() * 2);
            for byte in bytes {
                let _ = write!(hex, "{byte:02X}");
            }
            format!("<hex>{hex}</hex>")
        }
        _ => format!("<name>{name}</name>"),
    }
}

/// Builds a profile for a network that needs no password.
///
/// `raw` is the SSID's original bytes when they are known; see
/// [`ssid_element`]. `owe` authors an Enhanced Open profile instead of a
/// legacy unsecured one.
#[must_use]
pub fn open_profile(ssid: &str, raw: Option<&[u8]>, owe: bool) -> String {
    let name = escape(ssid);
    let ssid_element = ssid_element(&name, raw);
    // OWE is passwordless but encrypted: authoring it as open/none describes a
    // different network and the join fails.
    let (auth, encryption) = if owe {
        ("OWE", "AES")
    } else {
        ("open", "none")
    };
    let mut xml = String::new();
    let _ = write!(
        xml,
        r#"<?xml version="1.0"?>
<WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
  <name>{name}</name>
  <SSIDConfig><SSID>{ssid_element}</SSID></SSIDConfig>
  <connectionType>ESS</connectionType>
  <connectionMode>auto</connectionMode>
  <MSM><security>
    <authEncryption>
      <authentication>{auth}</authentication>
      <encryption>{encryption}</encryption>
      <useOneX>false</useOneX>
    </authEncryption>
  </security></MSM>
</WLANProfile>"#
    );
    xml
}

/// Which pre-shared-key profile shape to author.
///
/// The advertised authentication algorithm decides this, and it has to: a
/// profile is accepted by `WlanSetProfile` whether or not it describes the
/// network, so authoring WPA2/AES for a legacy WPA/TKIP access point installs
/// cleanly and then simply never connects.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PskFlavor {
    /// WPA3-SAE in transition mode: one profile joins WPA3-Personal and
    /// WPA2-PSK networks alike.
    Wpa3Transition,

    /// WPA2-PSK with AES.
    Wpa2Aes,

    /// Legacy WPA-PSK with TKIP, for an access point that offers nothing newer.
    WpaTkip,
}

impl PskFlavor {
    /// The `<authentication>`, `<encryption>` and transition-element text.
    fn parts(self) -> (&'static str, &'static str, &'static str) {
        match self {
            Self::Wpa3Transition => (
                "WPA3SAE",
                "AES",
                "\n      <transitionMode xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v4\">true</transitionMode>",
            ),
            Self::Wpa2Aes => ("WPA2PSK", "AES", ""),
            Self::WpaTkip => ("WPAPSK", "TKIP", ""),
        }
    }
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
pub fn psk_profile(ssid: &str, raw: Option<&[u8]>, passphrase: &str, flavor: PskFlavor) -> String {
    let name = escape(ssid);
    let ssid_element = ssid_element(&name, raw);
    let key = escape(passphrase);
    // A 64-hex-digit value is a raw PSK, not a passphrase, and Windows rejects
    // the profile if it is labelled as one — which made a key this crate's own
    // validator accepts unusable.
    let key_type = if is_raw_key(passphrase) {
        "networkKey"
    } else {
        "passPhrase"
    };
    let (auth, encryption, transition) = flavor.parts();
    let mut xml = String::new();
    let _ = write!(
        xml,
        r#"<?xml version="1.0"?>
<WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
  <name>{name}</name>
  <SSIDConfig><SSID>{ssid_element}</SSID></SSIDConfig>
  <connectionType>ESS</connectionType>
  <connectionMode>auto</connectionMode>
  <MSM><security>
    <authEncryption>
      <authentication>{auth}</authentication>
      <encryption>{encryption}</encryption>
      <useOneX>false</useOneX>{transition}
    </authEncryption>
    <sharedKey>
      <keyType>{key_type}</keyType>
      <protected>false</protected>
      <keyMaterial>{key}</keyMaterial>
    </sharedKey>
  </security></MSM>
</WLANProfile>"#
    );
    xml
}

/// The SSID a saved profile actually joins, read back out of its XML.
///
/// Profile names do not identify networks: Windows names a second profile for
/// the same SSID "<SSID> 2", so matching by name alone leaves duplicates behind
/// that keep auto-joining a network the user just forgot. Returns the SSID's
/// bytes — decoded from `<hex>` when present, otherwise the `<name>` text — or
/// `None` when the document carries neither.
#[must_use]
pub fn ssid_of_profile(xml: &str) -> Option<Vec<u8>> {
    // Deliberately a scan for the SSIDConfig block rather than an XML parse:
    // this reads back a document Windows itself produced, and a dependency-free
    // pure function is testable without a radio.
    let config = slice_between(xml, "<SSIDConfig>", "</SSIDConfig>")?;
    if let Some(hex) = slice_between(config, "<hex>", "</hex>") {
        let trimmed = hex.trim();
        if trimmed.len() % 2 != 0 {
            return None;
        }
        let mut bytes = Vec::with_capacity(trimmed.len() / 2);
        for pair in trimmed.as_bytes().chunks(2) {
            let text = std::str::from_utf8(pair).ok()?;
            bytes.push(u8::from_str_radix(text, 16).ok()?);
        }
        return Some(bytes);
    }
    slice_between(config, "<name>", "</name>").map(|name| unescape(name.trim()).into_bytes())
}

/// The text between the first `open` and the following `close`.
fn slice_between<'a>(haystack: &'a str, open: &str, close: &str) -> Option<&'a str> {
    let start = haystack.find(open)? + open.len();
    let rest = &haystack[start..];
    let end = rest.find(close)?;
    Some(&rest[..end])
}

/// Reverses [`escape`], so an SSID containing `&` compares equal to the one the
/// scan list reported.
fn unescape(value: &str) -> String {
    value
        .replace("&lt;", "<")
        .replace("&gt;", ">")
        .replace("&quot;", "\"")
        .replace("&apos;", "'")
        // Ampersand last: doing it first would re-expand the entities above.
        .replace("&amp;", "&")
}

/// Whether a passphrase can be used as a WPA-PSK passphrase at all.
///
/// Rejecting here turns a silent `WlanSetProfile` reason code into a message the
/// user can act on. The bounds are the 802.11 ones: 8..=63 printable ASCII
/// characters, or exactly 64 hex digits for a raw key.
#[must_use]
pub fn passphrase_is_valid(passphrase: &str) -> bool {
    if is_raw_key(passphrase) {
        return true;
    }
    let len = passphrase.chars().count();
    (8..=63).contains(&len) && passphrase.chars().all(|c| ('\u{20}'..='\u{7e}').contains(&c))
}

/// Whether the value is a raw 64-hex-digit PSK rather than a passphrase. The
/// two take different `keyType` values in the profile.
#[must_use]
pub fn is_raw_key(passphrase: &str) -> bool {
    passphrase.len() == 64 && passphrase.chars().all(|c| c.is_ascii_hexdigit())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_raw_64_digit_key_is_labelled_a_network_key_not_a_passphrase() {
        // Windows rejects a 64-digit PSK declared as passPhrase, so a key this
        // crate's own validator accepts would otherwise be unusable.
        let raw = "a1B2".repeat(16);
        assert!(passphrase_is_valid(&raw));
        let xml = psk_profile("Net", None, &raw, PskFlavor::Wpa3Transition);
        assert!(xml.contains("<keyType>networkKey</keyType>"));
        // An ordinary passphrase keeps the passPhrase form.
        assert!(
            psk_profile("Net", None, "password1", PskFlavor::Wpa3Transition)
                .contains("<keyType>passPhrase</keyType>")
        );
    }

    #[test]
    fn a_non_utf8_ssid_is_named_in_hex_rather_than_lossy_text() {
        // The lossy string carries U+FFFD where the air carried raw bytes, so a
        // <name> profile would describe a different network entirely.
        let raw = [0x41u8, 0xff, 0x42];
        let xml = psk_profile("A\u{fffd}B", Some(&raw), "password1", PskFlavor::Wpa2Aes);
        assert!(xml.contains("<hex>41FF42</hex>"));
        assert!(!xml.contains("<SSID><name>"));
        // A valid-UTF-8 SSID keeps the readable form.
        assert!(
            psk_profile("Cafe", Some(b"Cafe"), "password1", PskFlavor::Wpa2Aes)
                .contains("<SSID><name>Cafe</name></SSID>")
        );
    }

    #[test]
    fn enhanced_open_is_not_authored_as_a_legacy_open_network() {
        let xml = open_profile("Cafe", None, true);
        assert!(xml.contains("<authentication>OWE</authentication>"));
        // open/none describes a different network and would fail to join.
        assert!(!xml.contains("<encryption>none</encryption>"));
        assert!(open_profile("Cafe", None, false).contains("<authentication>open</authentication>"));
    }

    #[test]
    fn psk_profile_uses_the_v4_namespace_for_transition_mode_only() {
        let xml = psk_profile("Net", None, "password1", PskFlavor::Wpa3Transition);
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
        let xml = psk_profile("Net", None, "password1", PskFlavor::Wpa2Aes);
        assert!(xml.contains("<authentication>WPA2PSK</authentication>"));
        assert!(!xml.contains("transitionMode"));
    }

    #[test]
    fn ssid_and_passphrase_are_xml_escaped() {
        let xml = psk_profile("A&B<C>", None, "pw\"&<>'x", PskFlavor::Wpa3Transition);
        assert!(xml.contains("<name>A&amp;B&lt;C&gt;</name>"));
        assert!(xml.contains("<keyMaterial>pw&quot;&amp;&lt;&gt;&apos;x</keyMaterial>"));
        // No raw metacharacter may survive into the document body.
        assert!(!xml.contains("A&B"));
    }

    #[test]
    fn the_ssid_appears_as_both_profile_name_and_ssid() {
        let xml = open_profile("Cafe", None, false);
        assert_eq!(xml.matches("<name>Cafe</name>").count(), 2);
        assert!(xml.contains("<authentication>open</authentication>"));
    }

    #[test]
    fn a_legacy_wpa_network_gets_a_profile_that_can_actually_join_it() {
        // WlanSetProfile accepts a profile whether or not it describes the
        // network, so a WPA2/AES document installs cleanly for a WPA/TKIP AP
        // and then simply never connects.
        let xml = psk_profile("Old", None, "password1", PskFlavor::WpaTkip);
        assert!(xml.contains("<authentication>WPAPSK</authentication>"));
        assert!(xml.contains("<encryption>TKIP</encryption>"));
    }

    #[test]
    fn a_profiles_real_ssid_is_read_back_out_of_its_xml() {
        // Profile names do not identify networks: Windows calls the second
        // profile for one SSID "<SSID> 2", and matching by name leaves it
        // behind to keep auto-joining a network the user forgot.
        let xml = psk_profile("Cafe", None, "password1", PskFlavor::Wpa2Aes);
        assert_eq!(ssid_of_profile(&xml).as_deref(), Some(&b"Cafe"[..]));

        // The hex form, and an escaped name, both round-trip.
        let hex = psk_profile("A\u{fffd}B", Some(&[0x41, 0xff, 0x42]), "password1", PskFlavor::Wpa2Aes);
        assert_eq!(ssid_of_profile(&hex).as_deref(), Some(&[0x41u8, 0xff, 0x42][..]));
        let escaped = open_profile("A&B", None, false);
        assert_eq!(ssid_of_profile(&escaped).as_deref(), Some(&b"A&B"[..]));
    }

    #[test]
    fn a_document_with_no_ssid_block_yields_nothing_rather_than_a_guess() {
        assert!(ssid_of_profile("<WLANProfile></WLANProfile>").is_none());
        // An odd-length hex body is malformed, not half an SSID.
        assert!(ssid_of_profile("<SSIDConfig><SSID><hex>ABC</hex></SSID></SSIDConfig>").is_none());
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
