// Stamps what the protocol's identity members report: each library's locked version, the compiler, and the flags of this build.
use std::process::Command;

fn main() {
    println!("cargo:rerun-if-changed=Cargo.lock");
    let lock = std::fs::read_to_string("Cargo.lock").expect("Cargo.lock");
    for (crate_name, variable) in [
        ("rqrr", "XLANG_RQRR_VERSION"),
        ("fast_qr", "XLANG_FAST_QR_VERSION"),
        ("qrcode", "XLANG_QRCODE_VERSION"),
    ] {
        // Matched by line, since a Windows checkout may give the lock file CRLF endings.
        let name = format!("name = \"{crate_name}\"");
        let version = lock
            .split("[[package]]")
            .find(|block| block.lines().any(|line| line == name))
            .and_then(|block| {
                block
                    .lines()
                    .find_map(|line| line.strip_prefix("version = \"")?.strip_suffix('"'))
            })
            .unwrap_or("unknown");
        println!("cargo:rustc-env={variable}={version}");
    }

    let rustc = std::env::var("RUSTC").unwrap_or_else(|_| "rustc".into());
    let version = Command::new(rustc)
        .arg("--version")
        .output()
        .map(|o| String::from_utf8_lossy(&o.stdout).trim().to_string())
        .unwrap_or_default();
    println!("cargo:rustc-env=XLANG_RUSTC={version}");

    // CARGO_ENCODED_RUSTFLAGS separates flags with 0x1f.
    let flags = std::env::var("CARGO_ENCODED_RUSTFLAGS")
        .unwrap_or_default()
        .replace('\u{1f}', " ");
    println!(
        "cargo:rustc-env=XLANG_RUSTFLAGS={}",
        if flags.is_empty() { "default" } else { &flags }
    );
}
